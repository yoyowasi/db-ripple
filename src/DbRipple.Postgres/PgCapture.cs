using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Postgres;

/// <summary>
/// 감사 수집 설치(설계 결정 2). 샌드박스의 dbr_audit 스키마에 순번 시퀀스·헤더·행 이미지 표와 공용 트리거 함수 3개를 만들고,
/// 테이블마다 문장 단위 AFTER 트리거 3개(전이 테이블)를 단다. 트리거 인자로 키 열 이름을 넘겨 key_json을 만든다(ROW 키면 인자 없음).
/// 값은 to_jsonb 뒤 ->>로 문자열(NULL은 JSON null)이라 계약 규칙(값은 모두 문자열)과 같다. key_json은 json(순서 보존)이라
/// UPDATE의 old/new 키 문자열을 그대로 비교할 수 있다(수집기 PairUpdate).
/// 문장 단위 트리거는 0행이어도 돈다(SP22 B·C). 중첩된 0행 문장도 사용자 문장 트리거의 쓰기 문맥이 될 수 있으므로 헤더를 남긴다.
/// </summary>
public static class PgCapture
{
    public const string ManifestVersionPrefix = "pg-";

    /// <summary>감사 저장소와 공용 함수(트리거 함수는 SECURITY DEFINER: 미리보기 역할은 감사 표에 직접 쓸 수 없다).</summary>
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

        CREATE FUNCTION dbr_audit.capture_ins() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $f$
        DECLARE s bigint := nextval('dbr_audit.capture_seq'); fm boolean := dbr_audit.full_mode(); n bigint;
        BEGIN
          SELECT count(*) INTO n FROM dbr_new;
          INSERT INTO dbr_audit.header VALUES (s, TG_RELID, TG_TABLE_SCHEMA, TG_TABLE_NAME, 'I', n, 0, pg_trigger_depth(), CASE WHEN fm THEN 'FULL' ELSE 'KEYS' END);
          INSERT INTO dbr_audit.row_image
            SELECT s, 'INSERTED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_new x;
          RETURN NULL;
        END $f$;
        CREATE FUNCTION dbr_audit.capture_upd() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $f$
        DECLARE s bigint := nextval('dbr_audit.capture_seq'); fm boolean := dbr_audit.full_mode(); ni bigint; nd bigint;
        BEGIN
          SELECT count(*) INTO ni FROM dbr_new;
          SELECT count(*) INTO nd FROM dbr_old;
          INSERT INTO dbr_audit.header VALUES (s, TG_RELID, TG_TABLE_SCHEMA, TG_TABLE_NAME, 'U', ni, nd, pg_trigger_depth(), CASE WHEN fm THEN 'FULL' ELSE 'KEYS' END);
          INSERT INTO dbr_audit.row_image
            SELECT s, 'DELETED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_old x;
          INSERT INTO dbr_audit.row_image
            SELECT s, 'INSERTED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_new x;
          RETURN NULL;
        END $f$;
        CREATE FUNCTION dbr_audit.capture_del() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $f$
        DECLARE s bigint := nextval('dbr_audit.capture_seq'); fm boolean := dbr_audit.full_mode(); n bigint;
        BEGIN
          SELECT count(*) INTO n FROM dbr_old;
          INSERT INTO dbr_audit.header VALUES (s, TG_RELID, TG_TABLE_SCHEMA, TG_TABLE_NAME, 'D', 0, n, pg_trigger_depth(), CASE WHEN fm THEN 'FULL' ELSE 'KEYS' END);
          INSERT INTO dbr_audit.row_image
            SELECT s, 'DELETED', row_number() OVER (), dbr_audit.key_of(to_jsonb(x), TG_ARGV), CASE WHEN fm THEN dbr_audit.row_of(to_jsonb(x)) END FROM dbr_old x;
          RETURN NULL;
        END $f$;
        {(previewRole is null ? "" : $"REVOKE ALL ON SCHEMA dbr_audit FROM {PgNames.Quote(previewRole)};")}
        """;

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
    /// 미리보기 역할(비 superuser): 사용자 스키마·테이블·시퀀스에 DML 권한만 준다. 역할은 클러스터 전역이라 없을 때만 만든다.
    /// 연결 사용자가 SET ROLE 할 수 있게 그 역할의 구성원으로 넣는다(superuser면 필요 없지만 해가 없다).
    /// </summary>
    public static IReadOnlyList<string> PreviewRoleSql(string role, IEnumerable<string> schemas)
    {
        var r = PgNames.Quote(role);
        var list = new List<string>
        {
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {PgNames.Literal(role)}) THEN CREATE ROLE {r} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE; END IF; END $$",
            $"GRANT {r} TO CURRENT_USER",
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
