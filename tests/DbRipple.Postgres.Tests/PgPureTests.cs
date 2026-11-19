using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Execution;

namespace DbRipple.Postgres.Tests;

/// <summary>PostgreSQL 최소판 순수 테스트(DB 없이). 카탈로그·감사 기록은 손으로 만든다.</summary>
public class PgPureTests
{
    // s.parent(id) ← s.child(id, parent_id ON DELETE CASCADE) ← s.grandchild(id, child_id ON DELETE CASCADE), s.note(id, parent_id ON DELETE SET NULL), public.loose(a, b: 키 없음)
    const int Parent = 100, Child = 101, Grand = 102, Note = 103, Loose = 104;

    static DbCatalog Catalog() => new(
        Tables:
        [
            new(Parent, "s", "parent", TemporalType.None, false, "app"),
            new(Child, "s", "child", TemporalType.None, false, "app"),
            new(Grand, "s", "grandchild", TemporalType.None, false, "app"),
            new(Note, "s", "note", TemporalType.None, false, "app"),
            new(Loose, "public", "loose", TemporalType.None, false, "app"),
        ],
        Columns:
        [
            Col(Parent, 1, "id"), Col(Parent, 2, "name"),
            Col(Child, 1, "id"), Col(Child, 2, "parent_id"),
            Col(Grand, 1, "id"), Col(Grand, 2, "child_id"),
            Col(Note, 1, "id"), Col(Note, 2, "parent_id", nullable: true),
            Col(Loose, 1, "a", nullable: true), Col(Loose, 2, "b", nullable: true),
        ],
        KeyIndexes:
        [
            new(Parent, 1, "parent_pkey", true, true, 1, "id", false),
            new(Child, 1, "child_pkey", true, true, 1, "id", false),
            new(Grand, 1, "grandchild_pkey", true, true, 1, "id", false),
            new(Note, 1, "note_pkey", true, true, 1, "id", false),
        ],
        ForeignKeys:
        [
            new(200, "child_parent_fk", Child, Parent, "CASCADE", "NO_ACTION", false, false, 1, "parent_id", "id"),
            new(201, "grand_child_fk", Grand, Child, "CASCADE", "NO_ACTION", false, false, 1, "child_id", "id"),
            new(202, "note_parent_fk", Note, Parent, "SET_NULL", "NO_ACTION", false, false, 1, "parent_id", "id"),
        ],
        Triggers: [],
        Files: []);

    static CatalogColumn Col(int table, int id, string name, bool nullable = false) =>
        new(table, id, name, "integer", false, nullable, false, false, false, null);

    static BaselineContext Ctx(IReadOnlyList<TriggerStaticInfo>? triggers = null, IReadOnlyList<ExcludedTable>? excluded = null)
    {
        var cat = Catalog();
        var last = new AuditOrderInfo(CaptureManifest.Last, CaptureManifest.Last, CaptureManifest.Last);
        return new BaselineContext
        {
            OriginDb = "shop", SandboxDb = "shop_dbr_sandbox", SnapshotDb = "shop_dbr_sandbox",
            BackupFinishedAt = DateTimeOffset.UnixEpoch,
            Fingerprint = PgSandboxManager.FingerprintOf("17.2", 17, "C"), // 제품과 같은 고정값(재귀 트리거 켜짐)
            FingerprintMatch = true,
            Catalog = cat,
            Triggers = triggers ?? [],
            Manifest = new CaptureManifest("pg-test",
                cat.Tables.Where(t => excluded?.Any(e => e.Table == t.Schema + "." + t.Name) != true)
                    .Select(t => new ManifestTable(t.Schema + "." + t.Name, true, last, [], [])).ToList(),
                excluded ?? [], []),
        };
    }

    static TriggerStaticInfo Trig(string table, string name, ChangeOperation[] events, string[]? writes = null, bool unknown = false, string[]? external = null) => new()
    {
        Trigger = table.Split('.')[0] + "." + name, ObjectId = name.GetHashCode() & 0xffff, Table = table, Events = events,
        IsInsteadOf = false, IsDisabled = false, IsEncrypted = false, IsAuditTrigger = false,
        WriteTargets = writes ?? [], WriteTargetsUnknown = unknown, HasDynamicSql = unknown, HasRollback = false, ExternalSideEffects = external ?? [],
    };

    static AnalysisResult Analyze(string sql, BaselineContext? ctx = null) => new PgBatchAnalyzer().Analyze(sql, ctx ?? Ctx());

    // ---------------- 문장 나누기 ----------------

    [Fact]
    public void Splits_on_semicolons_outside_quotes_comments_and_dollar_strings()
    {
        var sql = "UPDATE s.parent SET name = 'a;b' WHERE id = 1; -- x;y\nDELETE FROM s.note WHERE id = 2 /* ; */;\nINSERT INTO s.parent VALUES (3, $q$x;y$q$)";
        var (parts, error, _) = PgSqlLexer.Split(sql);
        Assert.Null(error);
        Assert.Equal(3, parts.Count);
        Assert.StartsWith("UPDATE", parts[0].Sql);
        Assert.StartsWith("DELETE", parts[1].Sql);
        Assert.EndsWith("$q$x;y$q$)", parts[2].Sql);
        Assert.Equal(parts[0].Sql.Length, parts[0].Cleaned.Length);
    }

    [Fact]
    public void Positional_parameter_is_not_a_dollar_quote_and_empty_pieces_are_dropped()
    {
        var (parts, error, _) = PgSqlLexer.Split(";; DELETE FROM s.note WHERE id = $1;  -- only comment\n");
        Assert.Null(error);
        Assert.Single(parts);
    }

    [Theory]
    [InlineData("UPDATE s.parent SET name = 'abc")]
    [InlineData("DELETE FROM s.note /* never closed")]
    [InlineData("INSERT INTO s.parent VALUES ($$x)")]
    [InlineData("UPDATE \"s.parent SET name = 1")]
    public void Unterminated_quote_or_comment_is_an_error(string sql)
    {
        var (_, error, at) = PgSqlLexer.Split(sql);
        Assert.NotNull(error);
        Assert.True(at >= 0);
    }

    [Fact]
    public void Escape_string_with_backslash_quote_stays_inside_the_string()
    {
        var (parts, error, _) = PgSqlLexer.Split("UPDATE s.parent SET name = E'it\\'s;fine' WHERE id = 1; DELETE FROM s.note");
        Assert.Null(error);
        Assert.Equal(2, parts.Count);
    }

    // ---------------- 판별 ----------------

    [Theory]
    [InlineData("INSERT INTO s.parent (id, name) VALUES (1, 'x')", ChangeOperation.Insert, "s.parent")]
    [InlineData("update ONLY s.parent set name = 'y'", ChangeOperation.Update, "s.parent")]
    [InlineData("DELETE FROM \"s\".\"note\" WHERE id = 1", ChangeOperation.Delete, "s.note")]
    [InlineData("DELETE FROM loose", ChangeOperation.Delete, "public.loose")]
    public void Accepts_plain_dml_on_covered_user_tables(string sql, ChangeOperation kind, string target)
    {
        var r = Analyze(sql);
        Assert.True(r.IsAccepted, string.Join(" / ", r.Rejects.Select(d => d.RuleId + " " + d.Message)));
        var st = Assert.Single(r.Statements);
        Assert.Equal(kind, st.Kind);
        Assert.Equal(target, st.Target);
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnPgMinimalScope);
    }

    [Theory]
    [InlineData("TRUNCATE s.parent", "REJECT_TRUNCATE")]
    [InlineData("MERGE INTO s.parent p USING s.child c ON true WHEN MATCHED THEN DELETE", "REJECT_MERGE")]
    [InlineData("CALL s.do_it()", "REJECT_EXEC")]
    [InlineData("DO $$ BEGIN END $$", "REJECT_EXEC")]
    [InlineData("COMMIT", "REJECT_TRANSACTION_CONTROL")]
    [InlineData("SET search_path = s", "REJECT_SET_OPTION")]
    [InlineData("DROP TABLE s.parent", "REJECT_DDL")]
    [InlineData("COPY s.parent FROM '/tmp/x'", "REJECT_BULK_INSERT")]
    [InlineData("SELECT * INTO s.copy FROM s.parent", "REJECT_SELECT_INTO")]
    [InlineData("WITH d AS (DELETE FROM s.child RETURNING *) DELETE FROM s.parent", "REJECT_NOT_ALLOWED")]
    [InlineData("VACUUM s.parent", "REJECT_ADMIN_COMMAND")]
    [InlineData("DELETE FROM dbr_audit.header", "REJECT_CAPTURE_STORE")]
    [InlineData("DELETE FROM parent", "REJECT_TARGET_NOT_USER_TABLE")]
    [InlineData("DELETE FROM pg_catalog.pg_class", "REJECT_TARGET_NOT_USER_TABLE")]
    [InlineData("UPDATE s.parent SET name = set_config('role', 'postgres', false)", "REJECT_ADMIN_COMMAND")]
    // 2026-11-19: 감사 함수가 읽는 상한·모드 GUC도 같은 규칙으로 막힌다(인자와 상관없이 함수 이름으로 거부).
    [InlineData("UPDATE s.parent SET name = set_config('dbr.image_cap', '999999999', true) WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = pg_catalog.set_config('dbr.image_mode', 'FULL', true) WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = pg_catalog.pg_read_file ('/etc/passwd')", "REJECT_EXTERNAL_ROWSET")]
    [InlineData("INSERT INTO s.parent SELECT * FROM dblink('x', 'select 1') AS t(id int, name text)", "REJECT_EXTERNAL_ROWSET")]
    public void Rejects_out_of_scope_statements(string sql, string rule)
    {
        var r = Analyze(sql);
        Assert.False(r.IsAccepted);
        Assert.Contains(r.Rejects, d => d.RuleId == rule);
    }

    // 2026-11-19 따옴표 식별자 우회: 전에는 DangerousCall이 따옴표 토큰을 건너뛰어 아래가 모두 통과했다. 우회 경로마다 하나씩.
    [Theory]
    [InlineData("UPDATE s.parent SET name = \"set_config\"('role', 'postgres', false) WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = \"pg_catalog\".\"set_config\"('dbr.image_cap', '1', true) WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = pg_catalog.\"pg_terminate_backend\"(1)::text WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = \"set_config\" /* c */ ('role', 'postgres', false) WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = \"query_to_xml\"('select 1', true, true, '')::text WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = \"pg_sleep\"(1)::text WHERE id = 1", "REJECT_ADMIN_COMMAND")]
    [InlineData("INSERT INTO s.parent SELECT * FROM \"dblink\"('x', 'select 1') AS t(id int, name text)", "REJECT_EXTERNAL_ROWSET")]
    [InlineData("UPDATE s.parent SET name = \"public\".\"dblink_exec\"('x', 'y') WHERE id = 1", "REJECT_EXTERNAL_ROWSET")]
    [InlineData("UPDATE s.parent SET name = \"pg_read_file\"('/etc/passwd') WHERE id = 1", "REJECT_EXTERNAL_ROWSET")]
    [InlineData("UPDATE s.parent SET name = \"pg_catalog\".\"lo_export\"(1, '/tmp/x')::text WHERE id = 1", "REJECT_EXTERNAL_ROWSET")]
    // U&"…"는 렉서가 이스케이프(\005f = _)를 풀지 않으므로 이름과 상관없이 문장째 거부(REJECT_NOT_ALLOWED).
    [InlineData("UPDATE s.parent SET name = U&\"set\\005fconfig\"('role', 'postgres', false) WHERE id = 1", "REJECT_NOT_ALLOWED")]
    [InlineData("UPDATE s.parent SET name = u&\"pg_catalog\".x(1) WHERE id = 1", "REJECT_NOT_ALLOWED")]
    [InlineData("DELETE FROM U&\"s\".parent WHERE id = 1", "REJECT_NOT_ALLOWED")]
    [InlineData("UPDATE s.parent SET name = U&\"d!0061ta\" UESCAPE '!' WHERE id = 1", "REJECT_NOT_ALLOWED")]
    public void Quoted_or_unicode_escaped_function_names_do_not_bypass_the_deny_lists(string sql, string rule)
    {
        var r = Analyze(sql);
        Assert.False(r.IsAccepted, sql);
        Assert.Equal(rule, Assert.Single(r.Rejects).RuleId);
    }

    [Theory]
    // 따옴표 이름은 대소문자를 구분하므로 "SET_CONFIG"는 PostgreSQL에서 다른 함수다(목록의 소문자 이름과 정확히 같을 때만 거부).
    [InlineData("UPDATE s.parent SET name = \"SET_CONFIG\"('a', 'b', true) WHERE id = 1")]
    // 따옴표 열·테이블 이름, 함수 아닌 따옴표 이름 뒤 괄호 없음, U&'…' 문자열(이름이 아님)은 그대로 받는다.
    [InlineData("UPDATE \"s\".\"parent\" SET \"name\" = 'set_config' WHERE \"id\" = 1")]
    [InlineData("INSERT INTO \"s\".\"parent\" (id, name) VALUES (1, 'x')")]
    [InlineData("UPDATE s.parent SET name = U&'d\\0061t' WHERE id = 1")]
    public void Quoted_names_that_are_not_denied_calls_stay_accepted(string sql)
    {
        var r = Analyze(sql);
        Assert.True(r.IsAccepted, string.Join(" / ", r.Rejects.Select(d => d.RuleId + " " + d.Message)));
    }

    [Fact]
    public void Function_name_inside_a_string_is_not_a_call()
    {
        Assert.True(Analyze("UPDATE s.parent SET name = 'set_config(x)' WHERE id = 1").IsAccepted);
    }

    [Fact]
    public void One_reject_stops_the_whole_script_but_parsed_statements_are_kept()
    {
        var r = Analyze("DELETE FROM s.note; TRUNCATE s.parent");
        Assert.False(r.IsAccepted);
        Assert.Single(r.Statements);
        Assert.Equal(2, Assert.Single(r.Rejects).StatementIndex);
    }

    [Fact]
    public void Excluded_partitioned_table_is_rejected()
    {
        var r = Analyze("DELETE FROM s.note", Ctx(excluded: [new ExcludedTable("s.note", "PARTITIONED_TABLE")]));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectTargetNotUserTable && d.Message.Contains("PARTITIONED_TABLE"));
    }

    [Fact]
    public void Trigger_with_external_effect_reached_through_cascade_is_rejected()
    {
        var ctx = Ctx([Trig("s.grandchild", "t_mail", [ChangeOperation.Delete], external: ["pg_notify"])]);
        Assert.Contains(Analyze("DELETE FROM s.parent WHERE id = 1", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectTriggerExternalEffect);
        Assert.True(Analyze("UPDATE s.parent SET name = 'x'", ctx).IsAccepted); // UPDATE는 연쇄가 없다(NO_ACTION)
    }

    [Fact]
    public void Dynamic_trigger_is_a_warning_not_a_reject()
    {
        var r = Analyze("UPDATE s.parent SET name = 'x'", Ctx([Trig("s.parent", "t_dyn", [ChangeOperation.Update], unknown: true)]));
        Assert.True(r.IsAccepted);
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnTriggerDynamicSql);
    }

    [Fact]
    public void Analyzer_never_throws()
    {
        var r = new PgBatchAnalyzer().Analyze("DELETE FROM s.note", null!);
        Assert.Equal(AnalysisRules.RejectAnalysisError, Assert.Single(r.Rejects).RuleId);
    }

    // ---------------- 이미지 최종 차이 ----------------

    static AuditHeaderRow H(long seq, int table, ChangeOperation op, int depth = 1, ImageMode mode = ImageMode.Full) => new()
    {
        CaptureSeq = seq, TableObjectId = table, TableName = "[x].[y]", Operation = op, InsertedCount = 0, DeletedCount = 0,
        NestLevel = depth, TranCount = 2, ImageMode = mode,
    };

    static AuditRowImageRow I(long seq, ImageKind kind, string? key, string? row) => new() { CaptureSeq = seq, ImageKind = kind, Ordinal = 1, KeyJson = key, RowJson = row };

    static Dictionary<int, TableInfo> Tables() => Catalog().ToCaptureInput().ToDictionary(t => t.ObjectId);

    [Fact]
    public void Net_diff_is_order_independent_for_nested_rewrites_of_the_same_row()
    {
        // 바깥 UPDATE(a→1)가 트리거로 같은 행을 다시 고침(name→z). 후위 기록이라 안쪽이 먼저 기록된다.
        var headers = new[] { H(1, Parent, ChangeOperation.Update, 2), H(2, Parent, ChangeOperation.Update, 1) };
        var images = new[]
        {
            I(1, ImageKind.Deleted, """{"id":"1"}""", """{"id":"1","name":"b"}"""),
            I(1, ImageKind.Inserted, """{"id":"1"}""", """{"id":"1","name":"z"}"""),
            I(2, ImageKind.Deleted, """{"id":"1"}""", """{"id":"1","name":"a"}"""),
            I(2, ImageKind.Inserted, """{"id":"1"}""", """{"id":"1","name":"b"}"""),
        };
        var r = ImageNetDiff.Compute(headers, images, Tables());
        var c = Assert.Single(r.Changes);
        Assert.Equal(NetChangeKind.Updated, c.Change);
        Assert.Equal("a", c.Before!["name"]);
        Assert.Equal("z", c.After!["name"]);
        Assert.Equal(new[] { "name" }, c.ChangedColumns);
        Assert.Equal(new string?[] { "1" }, c.Key.Values);
    }

    [Fact]
    public void Insert_then_delete_of_the_same_key_cancels_out()
    {
        var headers = new[] { H(1, Note, ChangeOperation.Insert), H(2, Note, ChangeOperation.Delete) };
        var images = new[]
        {
            I(1, ImageKind.Inserted, """{"id":"9"}""", """{"id":"9","parent_id":null}"""),
            I(2, ImageKind.Deleted, """{"id":"9"}""", """{"id":"9","parent_id":null}"""),
        };
        Assert.Empty(ImageNetDiff.Compute(headers, images, Tables()).Changes);
    }

    [Fact]
    public void Keyless_table_is_a_multiset()
    {
        var headers = new[] { H(1, Loose, ChangeOperation.Delete) };
        var images = new[]
        {
            I(1, ImageKind.Deleted, null, """{"a":"1","b":"x"}"""),
            I(1, ImageKind.Deleted, null, """{"a":"1","b":"x"}"""),
        };
        var c = Assert.Single(ImageNetDiff.Compute(headers, images, Tables()).Changes);
        Assert.Equal(NetChangeKind.MultisetRemoved, c.Change);
        Assert.Equal(2, c.Multiplicity);
        Assert.Equal(KeyKind.ROW, c.Key.Kind);
    }

    [Fact]
    public void Keys_mode_table_is_left_for_reread()
    {
        var headers = new[] { H(1, Note, ChangeOperation.Delete, mode: ImageMode.Keys) };
        var r = ImageNetDiff.Compute(headers, [I(1, ImageKind.Deleted, """{"id":"1"}""", null)], Tables());
        Assert.Empty(r.Changes);
        Assert.Equal(new[] { Note }, r.Unresolved);
    }

    [Fact]
    public void Mixed_full_and_keys_batches_of_one_table_go_to_reread_with_keys_from_both()
    {
        // 한 문장 안: 부모 묶음 FULL, 자식 첫 묶음 FULL, 자식 둘째 묶음 KEYS(발동 단위 상한으로 문장 중간에 바뀜, 2026-11-19).
        var headers = new[]
        {
            H(1, Parent, ChangeOperation.Delete) with { DeletedCount = 1 },
            H(2, Child, ChangeOperation.Delete) with { DeletedCount = 1 },
            H(3, Child, ChangeOperation.Delete, mode: ImageMode.Keys) with { DeletedCount = 1 },
        };
        var images = new[]
        {
            I(1, ImageKind.Deleted, """{"id":"1"}""", """{"id":"1","name":"p1"}"""),
            I(2, ImageKind.Deleted, """{"id":"10"}""", """{"id":"10","parent_id":"1"}"""),
            I(3, ImageKind.Deleted, """{"id":"11"}""", null),
        };
        var r = ImageNetDiff.Compute(headers, images, Tables());
        // 자식은 FULL 이미지만 더하면 id 11이 빠지므로 테이블 전체를 재조회로 넘긴다. 부모는 이미지로 계산한다.
        Assert.Equal(new[] { Child }, r.Unresolved);
        var parent = Assert.Single(r.Changes);
        Assert.Equal(NetChangeKind.Deleted, parent.Change);
        Assert.Equal(new string?[] { "1" }, parent.Key.Values);
        // 재조회 키는 FULL 묶음의 키도 담는다.
        Assert.Equal(new[] { """{"id":"10"}""", """{"id":"11"}""" }, PgPreviewExecutor.RereadKeys(Child, headers, images));
    }

    // ---------------- 이미지 상한(발동 단위, 2026-11-19) ----------------

    static string FunctionBody(string sql, string head)
    {
        var start = sql.IndexOf(head, StringComparison.Ordinal);
        Assert.True(start >= 0, head + " 없음");
        return sql[start..sql.IndexOf("END $f$;", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void Audit_functions_decide_the_image_mode_before_writing_the_header()
    {
        var sql = PgCapture.StoreSql("dbr_preview");
        Assert.True(sql.All(char.IsAscii), "감사 설치 SQL은 ASCII만(샌드박스 인코딩은 원본을 따른다)");
        foreach (var (fn, arg) in new[] { ("capture_ins", "n"), ("capture_upd", "ni + nd"), ("capture_del", "n") })
        {
            var body = FunctionBody(sql, $"CREATE FUNCTION dbr_audit.{fn}()");
            var decide = body.IndexOf($"fm := dbr_audit.full_for({arg});", StringComparison.Ordinal);
            var header = body.IndexOf("INSERT INTO dbr_audit.header", StringComparison.Ordinal);
            Assert.True(decide >= 0 && header > decide, $"{fn}: 판정 위치 {decide}, 헤더 INSERT 위치 {header}");
            Assert.DoesNotContain("full_mode()", body); // 판정을 거치지 않고 모드를 바로 읽지 않는다
        }

        // 판정은 남은 FULL 예산 GUC 하나로 한다(O(1), 2026-11-19 계약 2 변경): 감사 표를 세지 않는다.
        var check = FunctionBody(sql, "CREATE FUNCTION dbr_audit.full_for(n bigint)");
        Assert.Contains("current_setting('dbr.image_cap', true)", check);
        Assert.DoesNotContain("dbr_audit.header", check);
        Assert.DoesNotContain("row_image", check);
        Assert.Contains("IF n = 0 OR coalesce(cfg, '') = '' THEN RETURN true; END IF;", check); // 0행 발동·예산 설정 없음(옛 실행기)은 판정하지 않는다
        Assert.Contains("IF cfg ~ '^[0-9]+$' THEN budget := cfg::numeric; END IF;", check);   // 숫자가 아니면 budget NULL → 아래에서 KEYS
        Assert.Contains("IF budget IS NULL OR n > budget THEN", check);                        // 같으면 FULL
        Assert.Contains("PERFORM set_config('dbr.image_mode', 'KEYS', true);", check);
        Assert.Contains("PERFORM set_config('dbr.image_cap', (budget - n)::text, true);", check);
        Assert.True(check.IndexOf("'KEYS'", StringComparison.Ordinal) < check.IndexOf("(budget - n)", StringComparison.Ordinal),
            "KEYS로 닫는 분기가 예산 차감보다 앞(넘치면 예산을 줄이지 않음)");
    }

    /// <summary>
    /// full_for의 판정을 C#으로 옮긴 모형(2026-11-19). 통합 테스트 (d)·(f) 기대값을 같은 규칙으로 다시 계산해 둔다.
    /// 예산 방식은 앞선 헤더 합 방식(FULL 합 + n > 상한이면 KEYS)과 같은 판정을 낸다: 예산 = 상한 − 지금까지 FULL.
    /// </summary>
    static string Modes(long cap, params long[] firings)
    {
        long budget = cap, fullSoFar = 0;
        bool keysByBudget = false, keysBySum = false;
        var modes = "";
        foreach (var n in firings)
        {
            // 예산 방식(지금): n > 남은 예산이면 KEYS로 닫고, 아니면 예산을 줄인다. 0행 발동은 그대로.
            if (!keysByBudget && n > 0) { if (n > budget) keysByBudget = true; else budget -= n; }
            // 헤더 합 방식(앞 커밋): 지금까지 FULL + n > 상한이면 KEYS.
            if (!keysBySum && n > 0) { if (fullSoFar + n > cap) keysBySum = true; else fullSoFar += n; }
            Assert.Equal(keysBySum, keysByBudget);
            modes += keysByBudget ? "K" : "F";
        }
        return $"{modes} full={cap - budget}";
    }

    [Theory]
    [InlineData(1L, "KKKKKKK full=0")]    // (d) 상한 1: 첫 발동 6 > 1
    [InlineData(14L, "FFKKKKK full=14")]  // (d) 상한 14: 6 ≤ 14(남음 8), 8 ≤ 8(남음 0), 2 > 0
    [InlineData(100_000L, "FFFFFFF full=25")]
    public void Budget_rule_gives_the_integration_d_expectations(long cap, string expected)
    {
        Assert.Equal(expected, Modes(cap, 6, 8, 2, 2, 4, 2, 1));
    }

    [Fact]
    public void Budget_rule_gives_the_integration_f_expectations()
    {
        Assert.Equal("FK full=10", Modes(100, 10, 20_000));         // (f) 상한 100: 부모 10 ≤ 100(남음 90), 자식 20,000 > 90
        Assert.Equal("FF full=20010", Modes(100_000, 10, 20_000)); // (f) 기본 상한
        Assert.Equal("FFK full=5", Modes(5, 5, 0, 1));              // 같으면 FULL, 0행 발동은 모드를 바꾸지 않음, 그 뒤 1 > 0
    }

    [Theory]
    [InlineData(ImageMode.Full, 10L, 100L, 0L, false)]  // 상한 안
    [InlineData(ImageMode.Full, 100L, 100L, 0L, false)] // 같으면 FULL
    [InlineData(ImageMode.Full, 10L, 100L, 1L, true)]   // 감사 함수가 문장 안에서 KEYS로 바꿈
    [InlineData(ImageMode.Full, 101L, 100L, 0L, true)]  // 옛 감사 함수(새로 고침 전): 문장 뒤 누적이 상한을 넘음
    [InlineData(ImageMode.Keys, 101L, 100L, 3L, false)] // 이미 KEYS
    public void Statement_switches_to_keys_on_keys_batches_or_old_style_overflow(ImageMode mode, long images, long cap, long keysBatches, bool expected)
    {
        Assert.Equal(expected, PgPreviewExecutor.SwitchesToKeys(mode, images, cap, keysBatches));
    }

    // ---------------- 감사 묶음 귀속 ----------------

    [Fact]
    public void Cascade_inside_a_user_trigger_keeps_its_depth_and_parent()
    {
        // SP22 추가 실측: parent(3)의 AFTER DELETE 트리거가 parent(2)를 지운다.
        // 안쪽 FK 연쇄는 안쪽 parent 뒤 같은 깊이로 기록되고 바깥 parent가 그 다음에 기록된다.
        var ctx = Ctx([Trig("s.parent", "t_nested", [ChangeOperation.Delete], writes: ["s.parent"])]);
        var analysis = Analyze("DELETE FROM s.parent WHERE id = 3", ctx);
        var headers = new[]
        {
            H(1, Parent, ChangeOperation.Delete, 2) with { DeletedCount = 1 },
            H(2, Child, ChangeOperation.Delete, 2) with { DeletedCount = 1 },
            H(3, Note, ChangeOperation.Update, 2) with { DeletedCount = 1, InsertedCount = 1 },
            H(4, Grand, ChangeOperation.Delete, 2),
            H(5, Parent, ChangeOperation.Delete) with { DeletedCount = 1 },
            H(6, Child, ChangeOperation.Delete),
            H(7, Note, ChangeOperation.Update),
        };
        var images = new[]
        {
            I(1, ImageKind.Deleted, """{"id":"2"}""", """{"id":"2","name":"p2"}"""),
            I(2, ImageKind.Deleted, """{"id":"20"}""", """{"id":"20","parent_id":"2"}"""),
            I(3, ImageKind.Deleted, """{"id":"1002"}""", """{"id":"1002","parent_id":"2"}"""),
            I(3, ImageKind.Inserted, """{"id":"1002"}""", """{"id":"1002","parent_id":null}"""),
            I(5, ImageKind.Deleted, """{"id":"3"}""", """{"id":"3","name":"p3-no-children"}"""),
        };
        var statement = new StatementResult
        {
            Index = 1, Batch = 1, Position = 1, Target = "s.parent", Kind = ChangeOperation.Delete,
            Span = Assert.Single(analysis.Statements).Span, Status = StatementStatus.Ok, RowsAffected = 1,
        };
        var net = ImageNetDiff.Compute(headers, images, Tables()).Changes;
        var raw = new RawCapture
        {
            Status = PreviewStatus.Ok,
            Executions = [new StatementExecution { Result = statement, Boundary = 7, ModeAfter = ImageMode.Full }],
            Boundary0 = 0, Headers = headers, Images = images,
            NetChanges = net, SandboxDirty = false,
            Capture = new CaptureInfo { ImageCount = images.Length, Cap = 100, Truncated = false, ManifestVersion = ctx.Manifest.Version },
            Timings = new ExecutionTimings(),
        };
        var result = new CausalResolver().Resolve(new ResolveInput
        {
            Statements = [statement], Collected = new ChangeCollector().Collect(raw, analysis, ctx), Net = net, Baseline = ctx,
        }).Model;

        var direct = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.Direct);
        var nested = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.Trigger);
        Assert.Equal(direct.Id, nested.Cause?.ParentFiringId);
        var cascade = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.FkCascade);
        var setNull = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.FkSetNull);
        Assert.All(new[] { cascade, setNull }, f =>
        {
            Assert.Equal(2, f.Depth);
            Assert.Equal(nested.Id, f.Cause?.ParentFiringId);
        });
        var emptyCascade = Assert.Single(result.Firings, f => f.Table == "s.grandchild");
        Assert.Equal(2, emptyCascade.Depth);
        Assert.Equal(0, emptyCascade.RowCount);
        Assert.DoesNotContain(result.NetChanges, n => n.Table == "s.grandchild");
    }

    // ---------------- 오류 분류·트리거 분석 ----------------

    [Theory]
    [InlineData("PL/pgSQL function s.f_block() line 3 at RAISE", "s.f_block")]
    [InlineData("SQL statement \"UPDATE x\"\nPL/pgSQL function dbr_audit.capture_upd() line 7 at SQL statement", "dbr_audit.capture_upd")]
    [InlineData("SQL function \"f\" statement 1", null)]
    [InlineData(null, null)]
    public void Trigger_function_is_read_from_the_error_context(string? where, string? expected)
    {
        Assert.Equal(expected, PgPreviewExecutor.TriggerFunction(where));
    }

    [Fact]
    public void Trigger_body_cleaning_removes_comments_and_string_contents()
    {
        var cleaned = PgTriggerAnalyzer.Clean("-- DELETE FROM a\nINSERT INTO s.log VALUES ('DELETE FROM b'); /* UPDATE c SET x = 1 */");
        Assert.DoesNotContain("DELETE FROM a", cleaned);
        Assert.DoesNotContain("DELETE FROM b", cleaned);
        Assert.DoesNotContain("UPDATE c", cleaned);
        Assert.Contains("INSERT INTO s.log", cleaned);
    }

    // 2026-11-19: 트리거 본문의 외부 부작용 검사(REJECT_TRIGGER_EXTERNAL_EFFECT 근거)도 따옴표·U& 이름으로 피하지 못한다.
    [Theory]
    [InlineData("BEGIN PERFORM pg_notify('c', 'x'); RETURN NULL; END", "pg_notify")]
    [InlineData("BEGIN PERFORM \"pg_notify\"('c', 'x'); RETURN NULL; END", "pg_notify")]
    [InlineData("BEGIN PERFORM \"public\".\"dblink_exec\"('c', 'x'); RETURN NULL; END", "dblink_exec")]
    [InlineData("BEGIN PERFORM U&\"pg\\005fnotify\"('c', 'x'); RETURN NULL; END", "U&\"…\" 식별자(해석 안 함)")]
    public void Trigger_body_external_call_is_found_even_when_quoted(string body, string expected)
    {
        var pg = new PgCatalog(Catalog(), [new PgTable(Parent, "s", "parent", "app", 'r', false)],
            [new PgTrigger(900, "t_x", Parent, true, false, false, false, ["DELETE"], "", "s.f", "plpgsql", body)]);
        Assert.Contains(expected, Assert.Single(PgTriggerAnalyzer.Analyze(pg)).ExternalSideEffects);
    }

    [Fact]
    public void Oid_beyond_int_range_fails_the_baseline()
    {
        Assert.Equal(16384, PgNames.ObjectId(16384u, "t"));
        Assert.Throws<InvalidOperationException>(() => PgNames.ObjectId(3_000_000_000u, "t"));
    }

    // ---------------- 상태의 실패 이유(2026-11-18) ----------------

    [Fact]
    public async Task Error_status_carries_the_failure_reason_without_the_password()
    {
        // 잘못된 원본 이름은 연결 전에 검사에서 멈추므로 DB 없이 error 상태를 만든다.
        var sbx = new PgSandboxManager(new PgOptions
        {
            ServerConnectionString = "Host=127.0.0.1;Port=1;Username=u;Password=secret-pw-123",
            OriginDb = "Bad-Name",
            SandboxDb = "s_box",
        }, TimeProvider.System);
        var status = await sbx.GetStatusAsync(default);
        Assert.Equal(SandboxState.Error, status.State);
        Assert.Equal(PgSandboxManager.EngineName, status.Engine);
        Assert.Contains("원본 DB", status.LastError);
        Assert.DoesNotContain("secret-pw-123", status.LastError);
        Assert.Equal(sbx.LastError, status.LastError);
    }

    [Fact]
    public void Last_error_is_omitted_from_json_unless_present()
    {
        var ok = new SandboxStatus { State = SandboxState.Ready, FingerprintMatch = true, Locked = false };
        Assert.DoesNotContain("lastError", System.Text.Json.JsonSerializer.Serialize(ok, ContractJson.Options));
        var bad = ok with { State = SandboxState.Error, LastError = "x" };
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(bad, ContractJson.Options));
        Assert.Equal("x", doc.RootElement.GetProperty("lastError").GetString());
    }
}
