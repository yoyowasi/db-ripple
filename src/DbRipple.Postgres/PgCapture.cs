using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Postgres;

/// <summary>
/// 감사 수집 설치(설계 결정 2). 샌드박스의 dbr_audit 스키마에 순번 시퀀스·헤더·행 이미지 표와 공용 트리거 함수 3개를 만들고,
/// 테이블마다 문장 단위 AFTER 트리거 3개(전이 테이블)를 단다. 트리거 인자로 키 열 이름을 넘겨 key_json을 만든다(ROW 키면 인자 없음).
/// 값은 to_jsonb 뒤 ->>로 문자열(NULL은 JSON null)이라 계약 규칙(값은 모두 문자열)과 같다. key_json은 json(순서 보존)이라
/// UPDATE의 old/new 키 문자열을 그대로 비교할 수 있다(수집기 PairUpdate).
/// 문장 단위 트리거는 0행이어도 돈다(SP22 B·C). 중첩된 0행 문장도 사용자 문장 트리거의 쓰기 문맥이 될 수 있으므로 헤더를 남긴다.
/// 이미지 상한은 발동마다 본다(2026-11-19, full_for): 헤더를 쓰기 전에 "이번 발동 이미지(dbr_new + dbr_old 행 수) > 남은 FULL 예산(dbr.image_cap)"이면
/// 이 발동부터 KEYS로 쓰고 dbr.image_mode를 KEYS로 바꿔 뒤 발동도 KEYS가 된다. 아니면 예산을 줄이고 FULL. 그래서 한 문장 안에서도 상한을 지킨다.
/// set_config(…, true)는 함수의 SET 절(search_path)과 다른 변수라 함수가 끝나도 트랜잭션 끝까지 남는다.
/// </summary>
public static class PgCapture
{
    public const string ManifestVersionPrefix = "pg-";

    /// <summary>
    /// 감사 저장소와 공용 함수(트리거 함수는 SECURITY DEFINER: 미리보기 역할은 감사 표에 직접 쓸 수 없다).
    /// full_for(n): 발동 단위 상한 판정(O(1), 헤더를 세지 않음). dbr.image_cap은 "남은 FULL 예산"이다: n ≤ 예산이면 예산에서 n을 빼고 FULL,
    /// n > 예산이면 KEYS로 닫는다. 예산이 없거나 빈 값이면(옛 실행기) 판정하지 않고, 숫자가 아니면 형 변환 오류 대신 KEYS로 닫는다.
    /// numeric으로 받아 아주 큰 값도 넘치지 않는다. 사용자 트리거의 예외 블록이 되돌린 발동은 GUC 변경도 함께 되돌려져 예산과 기록이 맞는다.
    /// SQL 본문에는 한글 주석을 두지 않는다(샌드박스 인코딩은 원본을 따라 LATIN1 등일 수 있다).
    /// </summary>
    public static string StoreSql(string? previewRole) => $"""
        DROP SCHEMA IF EXISTS dbr_audit CASCADE;
        CREATE SCHEMA dbr_audit;
        CREATE SEQUENCE dbr_audit.capture_seq;
        CREATE TABLE dbr_audit.meta (k text PRIMARY KEY, v text NOT NULL);
        CREATE TABLE dbr_audit.header (
            capture_seq bigint PRIMARY KEY, table_oid oid NOT NULL, table_schema text NOT NULL, table_name text NOT NULL,
            op char(1) NOT NULL, inserted_count bigint NOT NULL, deleted_count bigint NOT NULL, nest_level int NOT NULL, image_mode text NOT NULL);
        CREATE TABLE dbr_audit.row_image (
            capture_seq bigint NOT NULL, image_kind text NOT NULL, ordinal int NOT NULL, key_json text, row_json text);

        CREATE FUNCTION dbr_audit.key_of(r jsonb, cols text[]) RETURNS text LANGUAGE sql IMMUTABLE AS $f$
          SELECT CASE WHEN cardinality(cols) = 0 THEN NULL
                      ELSE (SELECT json_object_agg(u.c, r ->> u.c ORDER BY u.o)::text FROM unnest(cols) WITH ORDINALITY AS u(c, o)) END
        $f$;
        CREATE FUNCTION dbr_audit.row_of(r jsonb) RETURNS text LANGUAGE sql IMMUTABLE AS $f$
          SELECT json_object_agg(k, r ->> k)::text FROM jsonb_object_keys(r) AS k
        $f$;
        CREATE FUNCTION dbr_audit.full_mode() RETURNS boolean LANGUAGE sql STABLE AS $f$
          SELECT coalesce(current_setting('dbr.image_mode', true), '') <> 'KEYS'
        $f$;
        CREATE FUNCTION dbr_audit.full_for(n bigint) RETURNS boolean LANGUAGE plpgsql AS $f$
        DECLARE cfg text := current_setting('dbr.image_cap', true); budget numeric;
        BEGIN
          IF NOT dbr_audit.full_mode() THEN RETURN false; END IF;
          IF n = 0 OR coalesce(cfg, '') = '' THEN RETURN true; END IF;
          IF cfg ~ '^[0-9]+$' THEN budget := cfg::numeric; END IF;
          IF budget IS NULL OR n > budget THEN
            PERFORM set_config('dbr.image_mode', 'KEYS', true);
            RETURN false;
          END IF;
          PERFORM set_config('dbr.image_cap', (budget - n)::text, true);
          RETURN true;
        END $f$;

        CREATE FUNCTION dbr_audit.capture_ins() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $f$
        DECLARE s bigint := nextval('dbr_audit.capture_seq'); fm boolean; n bigint;
        BEGIN
          SELECT count(*) INTO n FROM dbr_new;
          fm := dbr_audit.full_for(n);
          INSERT INTO dbr_audit.header VALUES (s, TG_RELID, TG_TABLE_SCHEMA, TG_TABLE_NAME, 'I', n, 0, pg_trigger_depth(), CASE WHEN fm THEN 'FULL' ELSE 'KEYS' END);
          INSERT INTO dbr_audit.row_image
            SELECT s, 'INSERTED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_new x;
          RETURN NULL;
        END $f$;
        CREATE FUNCTION dbr_audit.capture_upd() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $f$
        DECLARE s bigint := nextval('dbr_audit.capture_seq'); fm boolean; ni bigint; nd bigint;
        BEGIN
          SELECT count(*) INTO ni FROM dbr_new;
          SELECT count(*) INTO nd FROM dbr_old;
          fm := dbr_audit.full_for(ni + nd);
          INSERT INTO dbr_audit.header VALUES (s, TG_RELID, TG_TABLE_SCHEMA, TG_TABLE_NAME, 'U', ni, nd, pg_trigger_depth(), CASE WHEN fm THEN 'FULL' ELSE 'KEYS' END);
          INSERT INTO dbr_audit.row_image
            SELECT s, 'DELETED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_old x;
          INSERT INTO dbr_audit.row_image
            SELECT s, 'INSERTED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_new x;
          RETURN NULL;
        END $f$;
        CREATE FUNCTION dbr_audit.capture_del() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $f$
        DECLARE s bigint := nextval('dbr_audit.capture_seq'); fm boolean; n bigint;
        BEGIN
          SELECT count(*) INTO n FROM dbr_old;
          fm := dbr_audit.full_for(n);
          INSERT INTO dbr_audit.header VALUES (s, TG_RELID, TG_TABLE_SCHEMA, TG_TABLE_NAME, 'D', 0, n, pg_trigger_depth(), CASE WHEN fm THEN 'FULL' ELSE 'KEYS' END);
          INSERT INTO dbr_audit.row_image
            SELECT s, 'DELETED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_old x;
          RETURN NULL;
        END $f$;
        REVOKE EXECUTE ON FUNCTION dbr_audit.full_for(bigint), dbr_audit.full_mode() FROM PUBLIC;
        {(previewRole is null ? "" : PreviewAuditGrants(previewRole))}
        """;

    /// <summary>
    /// 미리보기 역할의 감사 저장소 권한(2026-11-19 결정 3): 스키마 USAGE와 헤더·행 이미지 SELECT만. 쓰기(INSERT·UPDATE·DELETE·TRUNCATE)·시퀀스·meta 권한은 없다.
    /// 실행기는 미리보기 역할 연결의 같은 트랜잭션에서 감사 행을 읽는다(SECURITY DEFINER 읽기 함수 대신). 막아야 할 것은 감사 위조·삭제(쓰기)이고,
    /// 보이는 감사 행은 MVCC로 이번 트랜잭션 것뿐이다(미리보기는 늘 ROLLBACK, 구축은 감사 행을 커밋하지 않음).
    /// key_of·row_of는 KEYS 재조회가 쓰므로 PUBLIC 기본 EXECUTE를 둔다. capture_* 트리거 함수는 트리거 밖에서 부를 수 없다.
    /// </summary>
    static string PreviewAuditGrants(string previewRole)
    {
        var r = PgNames.Quote(previewRole);
        return $"GRANT USAGE ON SCHEMA dbr_audit TO {r};\nGRANT SELECT ON dbr_audit.header, dbr_audit.row_image TO {r};";
    }

    /// <summary>설치 계획: 테이블별 트리거 SQL과 매니페스트(설계 결정 2).</summary>
    public static (IReadOnlyList<string> Statements, CaptureManifest Manifest) Plan(PgCatalog pg, DateTimeOffset at)
    {
        var cat = pg.Catalog;
        var statements = new List<string>();
        var manifestTables = new List<ManifestTable>();
        var excluded = new List<ExcludedTable>();
        var input = cat.ToCaptureInput().ToDictionary(x => x.ObjectId);
        foreach (var t in pg.Tables)
        {
            var reason = t.RelKind == 'p' ? "PARTITIONED_TABLE" : t.IsPartition ? "PARTITION" : null;
            if (reason is not null)
            {
                excluded.Add(new ExcludedTable(t.Display, reason));
                continue;
            }
            var key = KeyOf(input[t.ObjectId]);
            var args = string.Join(", ", key.Select(PgNames.Literal));
            var q = PgNames.Quote(t.Schema, t.Name);
            statements.Add($"CREATE TRIGGER zzz_dbr_audit_i AFTER INSERT ON {q} REFERENCING NEW TABLE AS dbr_new FOR EACH STATEMENT EXECUTE FUNCTION dbr_audit.capture_ins({args})");
            statements.Add($"CREATE TRIGGER zzz_dbr_audit_u AFTER UPDATE ON {q} REFERENCING OLD TABLE AS dbr_old NEW TABLE AS dbr_new FOR EACH STATEMENT EXECUTE FUNCTION dbr_audit.capture_upd({args})");
            statements.Add($"CREATE TRIGGER zzz_dbr_audit_d AFTER DELETE ON {q} REFERENCING OLD TABLE AS dbr_old FOR EACH STATEMENT EXECUTE FUNCTION dbr_audit.capture_del({args})");

            // 같은 종류(AFTER 문장 단위) 트리거는 이름순으로 돈다. 이름이 zzz_dbr_audit_보다 뒤인 사용자 AFTER 문장 트리거가 있으면 마지막이 아니다(NONE).
            var later = pg.Triggers.Where(x => x.TableId == t.ObjectId && !x.IsRow && !x.IsBefore && !x.IsDisabled
                                              && string.CompareOrdinal(x.Name, PgNames.AuditTriggerPrefix + "z") > 0).ToList();
            string OrderFor(string ev) => later.Any(x => x.Events.Contains(ev)) ? CaptureManifest.None : CaptureManifest.Last;
            var volatileCols = cat.Columns.Where(c => c.ObjectId == t.ObjectId)
                .Select(c => (c.Name, Reason: PgCatalogReader.VolatileReason(c)))
                .Where(x => x.Reason is not null).Select(x => new VolatileColumn(x.Name, x.Reason!)).ToList();
            manifestTables.Add(new ManifestTable(t.Display, Covered: true,
                new AuditOrderInfo(OrderFor("INSERT"), OrderFor("UPDATE"), OrderFor("DELETE")),
                UnobservableColumns: [], volatileCols));
        }
        var manifest = new CaptureManifest(
            ManifestVersionPrefix + at.UtcDateTime.ToString("yyyyMMdd-HHmmss"),
            manifestTables, excluded,
            ["PostgreSQL 최소판: 문장 단위 AFTER 트리거(전이 테이블)로 수집, 깊이는 pg_trigger_depth()."]);
        return (statements, manifest);
    }

    /// <summary>
    /// 행 키 열: 수집기와 같은 규칙(Core TableKey.Choose: PK, 없으면 열이 모두 NOT NULL인 UNIQUE 중 인덱스 ID 순 첫째, 없으면 ROW)을 그대로 쓴다.
    /// 규칙이 갈리면 감사 key_json과 수집기의 RowKey 열이 어긋난다.
    /// </summary>
    public static IReadOnlyList<string> KeyOf(TableInfo table)
    {
        var key = TableKey.Choose(table);
        return key.Kind == KeyKind.ROW ? [] : key.Columns;
    }

    /// <summary>
    /// 미리보기 역할(2026-11-19 결정 3): LOGIN, 비 superuser, 비밀번호 없음(실행기가 연결마다 정했다가 지운다), 다른 역할의 구성원으로 넣지 않는다.
    /// 역할은 클러스터 전역이라 없을 때만 만들고, 옛 구조(NOLOGIN)면 LOGIN으로 바꾼다. 샌드박스에만 CONNECT와 사용자 스키마·테이블·시퀀스 DML 권한을 준다(원본에는 아무것도 주지 않는다).
    /// 예전처럼 연결 계정을 이 역할의 구성원으로 넣지 않는다(SET ROLE을 쓰지 않음). 안전한지는 이어서 PgPreviewLogin.RequireSafeAsync가 본다.
    /// ALTER ROLE에 NOSUPERUSER를 쓰지 않는 것은 superuser가 아닌 계정은 그 속성을 적기만 해도 거절되기 때문이다.
    /// </summary>
    public static IReadOnlyList<string> PreviewRoleSql(string role, string sandboxDb, IEnumerable<string> schemas)
    {
        var r = PgNames.Quote(role);
        var list = new List<string>
        {
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {PgNames.Literal(role)}) THEN CREATE ROLE {r} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE; ELSE ALTER ROLE {r} LOGIN PASSWORD NULL; END IF; END $$",
            $"GRANT CONNECT ON DATABASE {PgNames.Quote(sandboxDb)} TO {r}",
        };
        foreach (var s in schemas.Distinct(StringComparer.Ordinal))
        {
            var q = PgNames.Quote(s);
            list.Add($"GRANT USAGE ON SCHEMA {q} TO {r}");
            list.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {q} TO {r}");
            list.Add($"GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA {q} TO {r}");
        }
        return list;
    }
}
