using System.Diagnostics;
using DbRipple.Contracts;
using Npgsql;
using Xunit.Abstractions;

namespace DbRipple.Postgres.Tests;

/// <summary>
/// 실제 PostgreSQL 통합 테스트(CI pg 잡: 16·17·18, 비 superuser 계정). DBRIPPLE_PG_TEST_CONNECTION이 없으면 건너뛴다.
/// 기대값 근거: SP22 실측(docs/spikes/SP22.md), 2026-11-18 클라우드 Host API 결과(docs/test-reports/2026-11-18-pg-minimal-cloud.md),
/// 그 밖은 각 테스트의 픽스처에서 손으로 계산한 값이다(주석에 적음).
/// db 잡은 건너뜀 10개 초과를 실패로 보므로 테스트 수를 늘리지 말고 한 테스트 안에서 시나리오를 묶는다.
/// </summary>
[Collection(PgCollection.Name)]
public sealed class PgIntegrationTests(ITestOutputHelper output)
{
    // SP22 업무 스키마(spikes/sql/sp22_10_pg_capture.sql)의 테이블·FK·데이터·트리거.
    // 차이: 트리거를 시드 뒤에 만들어 주문 rev가 0에서 시작한다(SP22는 시드 정리 UPDATE가 트리거를 돌려 1).
    const string Sp22 = """
        CREATE SCHEMA s;
        CREATE TABLE s.parent (id int PRIMARY KEY, name text);
        CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id) ON DELETE CASCADE, v int);
        CREATE TABLE s.grandchild (id int PRIMARY KEY, child_id int REFERENCES s.child(id) ON DELETE CASCADE);
        CREATE TABLE s.note (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id) ON DELETE SET NULL);
        CREATE TABLE s.orders (id int PRIMARY KEY, total numeric(10,2), rev int DEFAULT 0);
        CREATE TABLE s.order_lines (id int PRIMARY KEY, order_id int REFERENCES s.orders(id) ON DELETE CASCADE, qty int, price numeric(10,2));
        CREATE TABLE s.history (id serial PRIMARY KEY, order_id int, at timestamptz DEFAULT now());

        INSERT INTO s.parent VALUES (1,'p1'),(2,'p2'),(3,'p3-no-children');
        INSERT INTO s.child VALUES (10,1,1),(11,1,2),(20,2,3);
        INSERT INTO s.grandchild VALUES (100,10),(101,10),(110,11);
        INSERT INTO s.note VALUES (1000,1),(1001,1),(1002,2);
        INSERT INTO s.orders VALUES (7, 20, 0);
        INSERT INTO s.order_lines VALUES (70,7,1,10),(71,7,2,5);

        CREATE FUNCTION s.recalc() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          UPDATE s.orders o SET total = (SELECT coalesce(sum(qty*price),0) FROM s.order_lines l WHERE l.order_id = o.id)
           WHERE o.id = coalesce(NEW.order_id, OLD.order_id);
          RETURN NULL;
        END $$;
        CREATE TRIGGER t_lines_recalc AFTER INSERT OR UPDATE OR DELETE ON s.order_lines FOR EACH ROW EXECUTE FUNCTION s.recalc();
        CREATE FUNCTION s.hist() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          INSERT INTO s.history(order_id) VALUES (NEW.id);
          UPDATE s.orders SET rev = rev + 1 WHERE id = NEW.id AND pg_trigger_depth() < 3;
          RETURN NULL;
        END $$;
        CREATE TRIGGER t_orders_hist AFTER UPDATE OF total ON s.orders FOR EACH ROW EXECUTE FUNCTION s.hist();
        """;

    static readonly string[] Sp22Tables = ["s.parent", "s.child", "s.grandchild", "s.note", "s.orders", "s.order_lines", "s.history"];

    // 같은 트리거의 실제 재귀: 자기 테이블을 다시 UPDATE하고 pg_trigger_depth()로 깊이 2에서 멈춘다(깊이 3 호출은 아무것도 안 함).
    const string RecursiveNode = """
        CREATE TABLE s.node (id int PRIMARY KEY, n int NOT NULL, note text);
        INSERT INTO s.node VALUES (1, 0, 'a'), (2, 0, 'b');
        CREATE FUNCTION s.bump() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF pg_trigger_depth() < 3 THEN
            UPDATE s.node SET n = n + 1 WHERE id = NEW.id;
          END IF;
          RETURN NULL;
        END $$;
        CREATE TRIGGER t_node_bump AFTER UPDATE ON s.node FOR EACH ROW EXECUTE FUNCTION s.bump();
        """;

    // 미리보기 역할 경계(설계 결정 3): 열 기본값이 문장을 실행한 역할(current_user)과 연결 계정(session_user)을 남긴다.
    // 실행기가 SET LOCAL ROLE을 빠뜨리면 who가 연결 계정이 된다. 다른 테스트의 기대 행 값이 바뀌지 않게 새로 고침 테스트에만 둔다.
    const string RoleProbe = """
        CREATE TABLE s.role_probe (id int PRIMARY KEY, who text DEFAULT current_user, sess text DEFAULT session_user);
        """;

    /// <summary>
    /// (a) 비 superuser 새로 고침: TEMPLATE 복제, 감사 설치, 소유 표시, 미리보기 역할, 문장이 미리보기 역할로 도는지(SET LOCAL ROLE),
    /// Host 재시작(meta 읽기), 두 번째 새로 고침(DROP … WITH (FORCE)).
    /// </summary>
    [PgFact]
    public async Task Refresh_as_non_superuser_builds_a_ready_sandbox_and_runs_statements_as_the_preview_role()
    {
        await using var env = await PgEnv.CreateAsync("refresh", output, Sp22, RoleProbe);
        var sbx = env.NewManager();

        // 아직 샌드박스가 없으면 error와 그 이유(LoadExisting 경로).
        var missing = await sbx.GetStatusAsync(CancellationToken.None);
        if (missing.State != SandboxState.Error || missing.LastError?.Contains(env.Sandbox, StringComparison.Ordinal) != true)
            throw PgAssert.Fail($"새로 고침 전 상태 기대 Error(샌드박스 {env.Sandbox} 없음), 실제 {PgFormat.Status(missing)}");

        var st = await env.RefreshReadyAsync(sbx);
        // 감사 대상 8개 = SP22 업무 테이블 7개(클라우드 보고서 "감사 대상 7개") + 역할 확인용 s.role_probe.
        PgAssert.Same(
            $"state=Ready engine=postgres covered=8 excluded=0 auditNotLast=0 fingerprintMatch=True locked=False lastError=- origin={env.Origin} login={PgTestDb.User}",
            PgFormat.Status(st), "새로 고침 뒤 상태");
        if (st.BaselineAt is null) throw PgAssert.Fail("새로 고침 뒤 BaselineAt이 null");

        long firstOid;
        await using (var admin = await PgTestDb.OpenAsync(PgTestDb.AdminDatabase))
            firstOid = await PgTestDb.CountAsync(admin, "SELECT oid::bigint FROM pg_database WHERE datname = $1", env.Sandbox);
        await using (var conn = await PgTestDb.OpenAsync(env.Sandbox))
        {
            var facts = string.Join("\n",
                $"auditTriggers={await PgTestDb.CountAsync(conn, "SELECT count(*) FROM pg_trigger WHERE left(tgname, 14) = 'zzz_dbr_audit_'")}",
                $"metaOwner={await PgTestDb.ScalarAsync(conn, "SELECT v FROM dbr_audit.meta WHERE k = 'owner'")}",
                $"metaOrigin={await PgTestDb.ScalarAsync(conn, "SELECT v FROM dbr_audit.meta WHERE k = 'origin_db'")}",
                $"dbComment={await PgTestDb.ScalarAsync(conn, "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = current_database()")}",
                $"roleSuper={await PgTestDb.ScalarAsync(conn, "SELECT rolsuper FROM pg_roles WHERE rolname = $1", env.Role)}",
                $"roleMember={await PgTestDb.ScalarAsync(conn, "SELECT pg_has_role(current_user, $1, 'MEMBER')", env.Role)}",
                $"auditHeaders={await PgTestDb.CountAsync(conn, "SELECT count(*) FROM dbr_audit.header")}");
            // 감사 트리거 = 테이블 8 × (I·U·D) 3.
            PgAssert.Same(string.Join("\n",
                "auditTriggers=24", "metaOwner=db-ripple", $"metaOrigin={env.Origin}", $"dbComment={PgSandboxManager.OwnerComment}",
                "roleSuper=False", "roleMember=True", "auditHeaders=0"), facts, "샌드박스 안 감사 설치·소유 표시·미리보기 역할");
        }

        // 문장은 미리보기 역할로 돈다(SET LOCAL ROLE): 기본값 current_user = 미리보기 역할, session_user = 연결 계정.
        var probe = await env.PreviewAsync(env.Pipeline(sbx), "INSERT INTO s.role_probe (id) VALUES (1)");
        PgAssert.SameLines([$"s.role_probe Inserted x1 key(PK:id=1) before- after{{id=1,sess={PgTestDb.User},who={env.Role}}} changed[]"],
            PgFormat.NetLines(probe.NetChanges), $"INSERT 미리보기의 실행 역할(기대 who = 미리보기 역할 {env.Role}, sess = 연결 계정 {PgTestDb.User})");
        await using (var conn = await PgTestDb.OpenAsync(env.Sandbox))
            PgAssert.Same("role_probe=0", $"role_probe={await PgTestDb.CountAsync(conn, "SELECT count(*) FROM s.role_probe")}", "역할 확인 미리보기 뒤 샌드박스(롤백)");

        // Host를 다시 켠 것과 같다: 새 관리자는 dbr_audit.meta에서 기준선을 읽는다.
        var reloaded = await env.NewManager().GetStatusAsync(CancellationToken.None);
        PgAssert.Same(PgFormat.Status(st), PgFormat.Status(reloaded), "새 관리자가 meta에서 읽은 상태");
        if (reloaded.BaselineAt != st.BaselineAt)
            throw PgAssert.Fail($"meta에서 읽은 기준 시각 기대 {st.BaselineAt:O}, 실제 {reloaded.BaselineAt:O}");

        // 두 번째 새로 고침: 도구가 만든 샌드박스만 DROP … WITH (FORCE) 뒤 다시 복제(비 superuser, DB 소유자).
        var again = await env.RefreshReadyAsync(sbx);
        long secondOid;
        await using (var admin = await PgTestDb.OpenAsync(PgTestDb.AdminDatabase))
            secondOid = await PgTestDb.CountAsync(admin, "SELECT oid::bigint FROM pg_database WHERE datname = $1", env.Sandbox);
        if (secondOid == firstOid || again.BaselineAt < st.BaselineAt)
            throw PgAssert.Fail($"두 번째 새로 고침이 샌드박스를 다시 만들지 않음: oid {firstOid} → {secondOid}, 기준 시각 {st.BaselineAt:O} → {again.BaselineAt:O}");
    }

    /// <summary>(b) FK 연쇄 DELETE(CASCADE 3단 + SET NULL)와 0행 연쇄. 미리보기 뒤 원본·샌드박스 행과 감사 저장소는 그대로.</summary>
    [PgFact]
    public async Task Fk_cascade_preview_matches_sp22_and_leaves_rows_and_audit_untouched()
    {
        await using var env = await PgEnv.CreateAsync("fk", output, Sp22);
        var sbx = env.NewManager();
        await env.RefreshReadyAsync(sbx);
        var pipeline = env.Pipeline(sbx);
        var originBefore = await PgTestDb.SnapshotAsync(env.Origin, Sp22Tables);
        var sandboxBefore = await PgTestDb.SnapshotAsync(env.Sandbox, Sp22Tables);

        // 클라우드 보고서 (a)·SP22 A: 모두 깊이 1·certain, 손자의 부모는 child.
        var a = await env.PreviewAsync(pipeline, "DELETE FROM s.parent WHERE id = 1");
        PgAssert.Same("Ok WARN_PG_MINIMAL_SCOPE rows=1",
            $"{a.Result.Status} {string.Join(",", a.Result.Warnings.Select(w => w.RuleId))} rows={a.Result.Statements.Single().RowsAffected}",
            "parent 1 삭제의 상태·경고·영향 행 수");
        PgAssert.Same("""
            s.child Delete d1 n2 FkCascade Certain parent=s.parent@d1 []
            s.grandchild Delete d1 n3 FkCascade Certain parent=s.child@d1 []
            s.note Update d1 n2 FkSetNull Certain parent=s.parent@d1 []
            s.parent Delete d1 n1 Direct Certain parent=- []
            """, PgFormat.Firings(a.Result.Firings, byTable: true), "parent 1 삭제의 묶음·원인(테이블 이름순)");
        // FK 연쇄는 부모 먼저 기록(SP22 H3: parent → child → note → grandchild).
        var seq = a.Result.Firings.GroupBy(f => f.Table).ToDictionary(g => g.Key, g => g.Min(f => f.CaptureSeq));
        if (!(seq["s.parent"] < seq["s.child"] && seq["s.parent"] < seq["s.note"] && seq["s.child"] < seq["s.grandchild"]))
            throw PgAssert.Fail($"FK 묶음 기록 순서 기대: parent < child·note, child < grandchild. 실제(순번 순):\n{PgFormat.Firings(a.Result.Firings)}");
        PgAssert.Same("""
            s.child I0 U0 D2 MR0 MA0
            s.grandchild I0 U0 D3 MR0 MA0
            s.note I0 U2 D0 MR0 MA0
            s.parent I0 U0 D1 MR0 MA0
            """, PgFormat.Summary(a.Result.NetSummary), "parent 1 삭제의 netSummary");

        // 클라우드 보고서 (b): note 1002의 parent_id "2" → null, FK 부모는 parent.
        var b = await env.PreviewAsync(pipeline, "DELETE FROM s.parent WHERE id = 2");
        PgAssert.Same("s.note Update d1 n1 FkSetNull Certain parent=s.parent@d1 []",
            string.Join("\n", b.Result.Firings.Where(f => f.Table == "s.note").Select(f => PgFormat.Firing(f, b.Result.Firings))), "parent 2 삭제의 note 묶음");
        PgAssert.SameLines(
            [
                "s.child Deleted x1 key(PK:id=20) before{id=20,parent_id=2,v=3} after- changed[]",
                "s.note Updated x1 key(PK:id=1002) before{id=1002,parent_id=2} after{id=1002,parent_id=null} changed[parent_id]",
                "s.parent Deleted x1 key(PK:id=2) before{id=2,name=p2} after- changed[]",
            ],
            PgFormat.NetLines(b.NetChanges), "parent 2 삭제의 최종 차이(행 값)");

        // SP22 B·클라우드 보고서 0행 연쇄: 자식이 없어도 child·note 0행 헤더가 깊이 1로 남는다.
        var c = await env.PreviewAsync(pipeline, "DELETE FROM s.parent WHERE id = 3");
        PgAssert.Same("s.child Delete d1 n0\ns.note Update d1 n0",
            string.Join("\n", c.Result.Firings.Where(f => f.Table != "s.parent").OrderBy(f => f.Table, StringComparer.Ordinal).Select(f => $"{f.Table} {f.Operation} d{f.Depth} n{f.RowCount}")),
            "parent 3(자식 없음) 삭제의 0행 FK 헤더");
        PgAssert.Same("s.parent I0 U0 D1 MR0 MA0", PgFormat.Summary(c.Result.NetSummary), "parent 3 삭제의 netSummary");

        // 롤백: 원본·샌드박스 업무 행 그대로, 감사 저장소 0행.
        PgAssert.Same(originBefore, await PgTestDb.SnapshotAsync(env.Origin, Sp22Tables), "미리보기 뒤 원본 업무 행");
        PgAssert.Same(sandboxBefore, await PgTestDb.SnapshotAsync(env.Sandbox, Sp22Tables), "미리보기 뒤 샌드박스 업무 행");
        await using var conn = await PgTestDb.OpenAsync(env.Sandbox);
        PgAssert.Same("header=0 row_image=0",
            $"header={await PgTestDb.CountAsync(conn, "SELECT count(*) FROM dbr_audit.header")} row_image={await PgTestDb.CountAsync(conn, "SELECT count(*) FROM dbr_audit.row_image")}",
            "미리보기 뒤 샌드박스 감사 저장소");
    }

    /// <summary>(c) 같은 테이블을 다시 고치는 사용자 트리거: 같은 트리거의 실제 재귀(깊이 3·2·1, 후위 기록)와 SP22 D 트리거 사슬.</summary>
    [PgFact]
    public async Task Recursive_trigger_firings_keep_depth_order_and_are_attributed_to_the_trigger()
    {
        await using var env = await PgEnv.CreateAsync("recur", output, Sp22, RecursiveNode);
        var sbx = env.NewManager();
        await env.RefreshReadyAsync(sbx);
        var pipeline = env.Pipeline(sbx);

        // 직접 UPDATE(note a→x, 깊이 1) → bump(깊이 1)가 n 0→1(깊이 2 묶음) → bump(깊이 2)가 n 1→2(깊이 3 묶음) → bump(깊이 3)는 멈춤.
        // 문장 단위 감사는 그 문장의 행 트리거 뒤에 돌므로 안쪽부터(3 → 2 → 1) 기록된다.
        var r = await env.PreviewAsync(pipeline, "UPDATE s.node SET note = 'x' WHERE id = 1");
        PgAssert.Same("""
            s.node Update d3 n1 Trigger Inferred parent=s.node@d2 [s.t_node_bump]
            s.node Update d2 n1 Trigger Inferred parent=s.node@d1 [s.t_node_bump]
            s.node Update d1 n1 Direct Certain parent=- []
            """, PgFormat.Firings(r.Result.Firings), "재귀 트리거 묶음(순번 순)·원인");
        PgAssert.SameLines(["s.node Updated x1 key(PK:id=1) before{id=1,n=0,note=a} after{id=1,n=2,note=x} changed[n,note]"],
            PgFormat.NetLines(r.NetChanges), "재귀 트리거의 최종 차이(행 값)");
        PgAssert.Same("s.node I0 U1 D0 MR0 MA0", PgFormat.Summary(r.Result.NetSummary), "재귀 트리거 netSummary");

        // SP22 D·클라우드 보고서 (c): 사용자 트리거 사슬은 깊이 3·3·2·1 순, orders total 20.00 → 30.00, rev 0 → 1(이 픽스처).
        var d = await env.PreviewAsync(pipeline, "UPDATE s.order_lines SET qty = qty + 1 WHERE id = 70");
        PgAssert.Same("""
            s.history Insert d3 n1 Trigger Inferred parent=s.orders@d2 [s.t_orders_hist]
            s.orders Update d3 n1 Trigger Inferred parent=s.orders@d2 [s.t_orders_hist]
            s.orders Update d2 n1 Trigger Inferred parent=s.order_lines@d1 [s.t_lines_recalc]
            s.order_lines Update d1 n1 Direct Certain parent=- []
            """, PgFormat.Firings(d.Result.Firings), "주문 트리거 사슬 묶음(순번 순)·원인");
        // history는 id(serial)·at(now())가 실행마다 달라 order_id만 본다.
        PgAssert.SameLines(
            [
                "s.order_lines Updated x1 key(PK:id=70) before{id=70,order_id=7,price=10.00,qty=1} after{id=70,order_id=7,price=10.00,qty=2} changed[qty]",
                "s.orders Updated x1 key(PK:id=7) before{id=7,rev=0,total=20.00} after{id=7,rev=1,total=30.00} changed[total,rev]",
                "s.history Inserted x1 order_id=7",
            ],
            d.NetChanges.Select(n => n.Table == "s.history" ? $"{n.Table} {n.Change} x{n.Multiplicity} order_id={n.After?.GetValueOrDefault("order_id") ?? "null"}" : PgFormat.Net(n)),
            "주문 트리거 사슬의 최종 차이(행 값)");
        PgAssert.Same("s.history I1 U0 D0 MR0 MA0\ns.order_lines I0 U1 D0 MR0 MA0\ns.orders I0 U1 D0 MR0 MA0",
            PgFormat.Summary(d.Result.NetSummary), "주문 트리거 사슬 netSummary");
    }

    /// <summary>
    /// (d) 이미지 상한을 넘겨 KEYS로 바뀐 테이블의 재조회가 FULL 이미지 계산과 같은 최종 차이를 내는지(PK 테이블·키 없는 테이블).
    /// 2026-11-19부터 상한은 발동 단위다: 상한 1(첫 발동부터 KEYS)과 상한 14(같으면 FULL, 한 테이블에 FULL·KEYS 섞임)를 함께 본다.
    /// </summary>
    [PgFact]
    public async Task Keys_reread_after_image_cap_gives_the_same_net_diff_as_full_images()
    {
        const string setup = """
            CREATE SCHEMA s;
            CREATE TABLE s.items (id int PRIMARY KEY, qty int NOT NULL, label text);
            CREATE TABLE s.loose (g int, v int);
            INSERT INTO s.items SELECT i, i, 'L' || i FROM generate_series(1, 10) AS i;
            INSERT INTO s.loose VALUES (1, 1), (1, 1), (1, 2), (2, 5), (3, 7);
            """;
        // 문장별 이미지(= 발동 하나의 dbr_new + dbr_old 행 수): 6(UPDATE 3행 × 2), 8(4행 × 2), 2, 2, 4, 2, 1. 합 25.
        // 2026-11-19 발동 단위 상한: 발동마다 "이번 발동 > 남은 FULL 예산(상한 − 지금까지 FULL)"이면 그 발동부터 KEYS, 같으면 FULL하고 예산을 줄인다(PgPureTests.Modes로 같은 계산).
        const string script = """
            UPDATE s.items SET qty = qty + 100 WHERE id <= 3;
            UPDATE s.items SET qty = qty + 1, label = NULL WHERE id BETWEEN 2 AND 5;
            INSERT INTO s.items (id, qty, label) VALUES (11, 0, 'new'), (12, 7, NULL);
            DELETE FROM s.items WHERE id IN (6, 11);
            UPDATE s.loose SET v = v + 10 WHERE g = 1 AND v = 1;
            INSERT INTO s.loose VALUES (4, 4), (1, 2);
            DELETE FROM s.loose WHERE g = 2;
            """;
        await using var env = await PgEnv.CreateAsync("keys", output, setup);
        var sbx = env.NewManager();
        await env.RefreshReadyAsync(sbx);
        var pipeline = env.Pipeline(sbx);

        var full = await env.PreviewAsync(pipeline, script);
        var keys = await env.PreviewAsync(pipeline, script, imageCap: 1);
        var mixed = await env.PreviewAsync(pipeline, script, imageCap: 14);

        // 기본 상한: 25 ≤ 100,000이라 모두 FULL.
        PgAssert.Same("Ok truncated=False images=25 modes=s.items:Full,s.items:Full,s.items:Full,s.items:Full,s.loose:Full,s.loose:Full,s.loose:Full",
            Mode(full), "기본 상한 미리보기의 상태·이미지 모드");
        // 상한 1: 첫 발동부터 6 > 예산 1이라 KEYS, 모드가 KEYS로 바뀌어 뒤 발동도 모두 KEYS. FULL 이미지 0개(옛 동작은 첫 문장 6개가 FULL).
        PgAssert.Same("Truncated truncated=True images=0 modes=s.items:Keys,s.items:Keys,s.items:Keys,s.items:Keys,s.loose:Keys,s.loose:Keys,s.loose:Keys",
            Mode(keys), "상한 1 미리보기의 상태·이미지 모드");
        // 상한 14: 6 ≤ 14 FULL(남음 8), 8 = 남은 8이라 FULL(남음 0), 2 > 0이라 셋째 발동부터 KEYS.
        // items는 FULL 2개·KEYS 2개가 섞이고 loose는 모두 KEYS. FULL 이미지 14개.
        PgAssert.Same("Truncated truncated=True images=14 modes=s.items:Full,s.items:Full,s.items:Keys,s.items:Keys,s.loose:Keys,s.loose:Keys,s.loose:Keys",
            Mode(mixed), "상한 14 미리보기의 상태·이미지 모드");

        // 손으로 계산한 최종 차이(id 11은 넣고 지워 없어짐, loose의 (1,2)는 원래 1개 + 넣은 1개).
        string[] expected =
        [
            "s.items Updated x1 key(PK:id=1) before{id=1,label=L1,qty=1} after{id=1,label=L1,qty=101} changed[qty]",
            "s.items Updated x1 key(PK:id=2) before{id=2,label=L2,qty=2} after{id=2,label=null,qty=103} changed[qty,label]",
            "s.items Updated x1 key(PK:id=3) before{id=3,label=L3,qty=3} after{id=3,label=null,qty=104} changed[qty,label]",
            "s.items Updated x1 key(PK:id=4) before{id=4,label=L4,qty=4} after{id=4,label=null,qty=5} changed[qty,label]",
            "s.items Updated x1 key(PK:id=5) before{id=5,label=L5,qty=5} after{id=5,label=null,qty=6} changed[qty,label]",
            "s.items Deleted x1 key(PK:id=6) before{id=6,label=L6,qty=6} after- changed[]",
            "s.items Inserted x1 key(PK:id=12) before- after{id=12,label=null,qty=7} changed[]",
            "s.loose MultisetRemoved x2 key(ROW:) before{g=1,v=1} after- changed[]",
            "s.loose MultisetRemoved x1 key(ROW:) before{g=2,v=5} after- changed[]",
            "s.loose MultisetAdded x2 key(ROW:) before- after{g=1,v=11} changed[]",
            "s.loose MultisetAdded x1 key(ROW:) before- after{g=1,v=2} changed[]",
            "s.loose MultisetAdded x1 key(ROW:) before- after{g=4,v=4} changed[]",
        ];
        PgAssert.SameLines(expected, PgFormat.NetLines(full.NetChanges), "FULL 이미지로 계산한 최종 차이");
        PgAssert.SameLines(PgFormat.NetLines(full.NetChanges), PgFormat.NetLines(keys.NetChanges), "상한 1 KEYS 재조회 최종 차이(기대 = FULL 이미지 계산)");
        PgAssert.Same(PgFormat.Summary(full.Result.NetSummary), PgFormat.Summary(keys.Result.NetSummary), "상한 1 KEYS 재조회 netSummary(기대 = FULL)");
        PgAssert.SameLines(PgFormat.NetLines(full.NetChanges), PgFormat.NetLines(mixed.NetChanges), "상한 14 FULL·KEYS 섞인 재조회 최종 차이(기대 = FULL 이미지 계산)");
        PgAssert.Same(PgFormat.Summary(full.Result.NetSummary), PgFormat.Summary(mixed.Result.NetSummary), "상한 14 재조회 netSummary(기대 = FULL)");
        PgAssert.Same("s.items I1 U5 D1 MR0 MA0\ns.loose I0 U0 D0 MR3 MA4", PgFormat.Summary(full.Result.NetSummary), "netSummary");

        static string Mode(PreviewRecord p) =>
            $"{p.Result.Status} truncated={p.Result.Capture.Truncated} images={p.Result.Capture.ImageCount} "
            + $"modes={string.Join(",", p.Result.Firings.OrderBy(f => f.CaptureSeq).Select(f => $"{f.Table}:{f.ImageMode}"))}";
    }

    /// <summary>(e) 원본에 다른 연결이 있으면 새로 고침은 55006으로 error가 되고, 도구는 그 원본 세션을 끊지 않는다.</summary>
    [PgFact]
    public async Task Refresh_with_an_open_origin_session_fails_with_55006_and_keeps_that_session()
    {
        await using var env = await PgEnv.CreateAsync("busy", output, Sp22);
        var sbx = env.NewManager();
        await env.RefreshReadyAsync(sbx); // 샌드박스가 있는 상태에서 다시 고칠 때(DROP 뒤 복제 실패)를 본다.

        await using (var held = await PgTestDb.OpenAsync(env.Origin))
        {
            var pid = await PgTestDb.CountAsync(held, "SELECT pg_backend_pid()");
            var sw = Stopwatch.StartNew();
            Exception? error = null;
            try { await sbx.RefreshAsync(CancellationToken.None); }
            catch (Exception ex) { error = ex; }
            output.WriteLine($"원본 연결이 있는 새로 고침 {sw.ElapsedMilliseconds}ms: {error?.GetType().Name ?? "예외 없음"}");
            var sqlState = (error as PostgresException ?? error?.InnerException as PostgresException)?.SqlState;
            if (sqlState != PostgresErrorCodes.ObjectInUse)
                throw PgAssert.Fail($"새로 고침 예외의 SQLSTATE 기대 55006, 실제 {sqlState ?? "-"}\n{PgTestDb.Mask(error?.ToString() ?? "예외 없음")}");

            var st = await sbx.GetStatusAsync(CancellationToken.None);
            var reason = st.LastError ?? "";
            var problems = new List<string>();
            if (st.State != SandboxState.Error) problems.Add($"상태 기대 Error, 실제 {st.State}");
            if (st.Engine != PgSandboxManager.EngineName) problems.Add($"engine 기대 postgres, 실제 {st.Engine ?? "-"}");
            if (!reason.Contains("다른 연결", StringComparison.Ordinal) || !reason.Contains(env.Origin, StringComparison.Ordinal))
                problems.Add("lastError에 원본 이름과 '다른 연결'(55006 사유)이 없음");
            if (PgTestDb.Password.Length > 0 && reason.Contains(PgTestDb.Password, StringComparison.Ordinal)) problems.Add("lastError에 비밀번호가 있음");
            if (reason.Contains("Password=", StringComparison.OrdinalIgnoreCase)) problems.Add("lastError에 연결 문자열이 있음");
            if (problems.Count > 0)
                throw PgAssert.Fail($"55006 뒤 상태: {string.Join("; ", problems)}\n상태: {PgTestDb.Mask(PgFormat.Status(st))}");

            // 도구는 원본 세션을 끊지 않는다: 같은 PID로 계속 응답한다.
            var samePid = await PgTestDb.CountAsync(held, "SELECT pg_backend_pid()");
            var parents = await PgTestDb.CountAsync(held, "SELECT count(*) FROM s.parent");
            long alive;
            await using (var admin = await PgTestDb.OpenAsync(PgTestDb.AdminDatabase))
                alive = await PgTestDb.CountAsync(admin, "SELECT count(*) FROM pg_stat_activity WHERE pid = $1 AND datname = $2", (int)pid, env.Origin);
            PgAssert.Same($"pid={pid} parents=3 alive=1", $"pid={samePid} parents={parents} alive={alive}", "새로 고침 실패 뒤 원본 세션");
        }

        // 원본 연결을 닫으면 다시 ready(실패 이유는 지워진다).
        var ready = await env.RefreshReadyAsync(sbx);
        if (ready.LastError is not null) throw PgAssert.Fail($"성공한 새로 고침 뒤 lastError 기대 null, 실제 {ready.LastError}");
    }

    /// <summary>
    /// (f) 중간 규모 연쇄: 부모 10행 삭제 → 자식 2만 행 CASCADE. 정확성과 걸린 시간(출력에 기록, 단언은 120초로 넉넉히).
    /// 2026-11-19: 같은 문장을 상한 100으로 한 번 더 돌려 한 문장 안에서 부모 FULL·자식 KEYS로 갈리고 최종 차이가 같은지 본다.
    /// </summary>
    [PgFact]
    public async Task Medium_cascade_of_20000_children_is_exact_and_timed()
    {
        const string setup = """
            CREATE SCHEMA s;
            CREATE TABLE s.big_parent (id int PRIMARY KEY, name text);
            CREATE TABLE s.big_child (id int PRIMARY KEY, parent_id int NOT NULL REFERENCES s.big_parent(id) ON DELETE CASCADE, v int);
            CREATE INDEX big_child_parent ON s.big_child (parent_id);
            INSERT INTO s.big_parent SELECT i, 'p' || i FROM generate_series(1, 20) AS i;
            INSERT INTO s.big_child SELECT i, (i % 20) + 1, i FROM generate_series(1, 40000) AS i;
            ANALYZE s.big_parent;
            ANALYZE s.big_child;
            """;
        await using var env = await PgEnv.CreateAsync("big", output, setup);
        var sbx = env.NewManager();
        await env.RefreshReadyAsync(sbx);
        var pipeline = env.Pipeline(sbx);

        var sw = Stopwatch.StartNew();
        var p = await env.PreviewAsync(pipeline, "DELETE FROM s.big_parent WHERE id <= 10");
        sw.Stop();
        output.WriteLine($"[측정] 자식 2만 행 연쇄 미리보기 전체 {sw.ElapsedMilliseconds}ms (실행 {p.Result.TimingsMs.Execute}, 감사 읽기 {p.Result.TimingsMs.ReadLog}, 최종 차이 {p.Result.TimingsMs.NetDiff}, 귀속 {p.Result.TimingsMs.Resolve}, 직렬화 {p.Result.TimingsMs.Serialize})");

        // 자식 parent_id = (i % 20) + 1 이므로 부모 1..10에 각 2,000행 = 20,000행.
        PgAssert.Same("Ok truncated=False images=20010 rows=10",
            $"{p.Result.Status} truncated={p.Result.Capture.Truncated} images={p.Result.Capture.ImageCount} rows={p.Result.Statements.Single().RowsAffected}",
            "2만 행 연쇄 상태·이미지 수");
        PgAssert.Same("""
            s.big_parent Delete d1 n10 Direct Certain parent=- []
            s.big_child Delete d1 n20000 FkCascade Certain parent=s.big_parent@d1 []
            """, PgFormat.Firings(p.Result.Firings), "2만 행 연쇄 묶음·원인(부모 먼저)");
        PgAssert.Same("s.big_child I0 U0 D20000 MR0 MA0\ns.big_parent I0 U0 D10 MR0 MA0", PgFormat.Summary(p.Result.NetSummary), "2만 행 연쇄 netSummary");
        var deletedChildren = p.NetChanges.Where(n => n.Table == "s.big_child" && n.Change == NetChangeKind.Deleted)
            .Select(n => int.Parse(n.Before!["parent_id"]!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (deletedChildren.Count != 20000 || deletedChildren.Any(id => id is < 1 or > 10))
            throw PgAssert.Fail($"지운 자식 기대 20000행(parent_id 1..10), 실제 {deletedChildren.Count}행, 범위 밖 {deletedChildren.Count(id => id is < 1 or > 10)}행");
        if (sw.Elapsed > TimeSpan.FromSeconds(120))
            throw PgAssert.Fail($"2만 행 연쇄 미리보기가 120초를 넘김: {sw.ElapsedMilliseconds}ms");

        // 2026-11-19 한 문장 안 상한: 상한 100이면 부모 발동(10 ≤ 100, 남음 90)은 FULL, 그 뒤 기록되는 자식 발동(20,000 > 90)은 같은 문장 안에서 KEYS.
        // FULL 이미지는 부모 10개뿐이고, 자식은 재조회(키 2만 개)로 기본 상한과 같은 최종 차이를 내야 한다.
        var swCap = Stopwatch.StartNew();
        var k = await env.PreviewAsync(pipeline, "DELETE FROM s.big_parent WHERE id <= 10", imageCap: 100);
        swCap.Stop();
        output.WriteLine($"[측정] 상한 100(자식 KEYS 재조회) 미리보기 전체 {swCap.ElapsedMilliseconds}ms (실행 {k.Result.TimingsMs.Execute}, 감사 읽기 {k.Result.TimingsMs.ReadLog}, 최종 차이 {k.Result.TimingsMs.NetDiff}, 귀속 {k.Result.TimingsMs.Resolve})");
        // 자식 묶음은 행 이미지가 없어 FK 값(parent_id)을 볼 수 없다. 원인 확신도는 귀속 코드 몫이라 단언하지 않고 출력에만 남긴다.
        output.WriteLine("상한 100 묶음: " + PgFormat.Firings(k.Result.Firings).ReplaceLineEndings(" / "));
        PgAssert.Same("Truncated truncated=True images=10 rows=10 modes=s.big_parent:Full,s.big_child:Keys",
            $"{k.Result.Status} truncated={k.Result.Capture.Truncated} images={k.Result.Capture.ImageCount} rows={k.Result.Statements.Single().RowsAffected} "
            + $"modes={string.Join(",", k.Result.Firings.OrderBy(f => f.CaptureSeq).Select(f => $"{f.Table}:{f.ImageMode}"))}",
            "상한 100 미리보기의 상태·이미지 수·묶음 모드(부모 FULL, 자식 KEYS)");
        PgAssert.Same(PgFormat.Summary(p.Result.NetSummary), PgFormat.Summary(k.Result.NetSummary), "상한 100 netSummary(기대 = 기본 상한)");
        // 2만 줄을 통째로 메시지에 넣지 않고 다른 줄만 몇 개 보인다.
        var fullLines = PgFormat.NetLines(p.NetChanges).ToList();
        var capLines = PgFormat.NetLines(k.NetChanges).ToList();
        if (!fullLines.SequenceEqual(capLines, StringComparer.Ordinal))
            throw PgAssert.Fail($"상한 100 최종 차이(행 단위)가 기본 상한과 다름: 기본 {fullLines.Count}줄, 상한 100 {capLines.Count}줄\n"
                                + $"기본에만: {string.Join("\n", fullLines.Except(capLines, StringComparer.Ordinal).Take(5))}\n"
                                + $"상한 100에만: {string.Join("\n", capLines.Except(fullLines, StringComparer.Ordinal).Take(5))}");
        if (swCap.Elapsed > TimeSpan.FromSeconds(120))
            throw PgAssert.Fail($"상한 100 2만 행 연쇄 미리보기가 120초를 넘김: {swCap.ElapsedMilliseconds}ms");

        await using (var conn = await PgTestDb.OpenAsync(env.Sandbox))
            PgAssert.Same("children=40000 headers=0", $"children={await PgTestDb.CountAsync(conn, "SELECT count(*) FROM s.big_child")} headers={await PgTestDb.CountAsync(conn, "SELECT count(*) FROM dbr_audit.header")}",
                "미리보기 뒤 샌드박스(롤백)");
    }
}
