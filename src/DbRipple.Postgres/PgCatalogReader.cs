using System.Text.RegularExpressions;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Npgsql;

namespace DbRipple.Postgres;

/// <summary>PostgreSQL 테이블 한 개(카탈로그 + 감사 설치 판단용 속성).</summary>
public sealed record PgTable(int ObjectId, string Schema, string Name, string Owner, char RelKind, bool IsPartition)
{
    public string Display => Schema + "." + Name;
}

/// <summary>PostgreSQL 사용자 트리거(내부 RI 트리거와 감사 트리거 제외).</summary>
public sealed record PgTrigger(
    int ObjectId, string Name, int TableId, bool IsRow, bool IsBefore, bool IsInsteadOf, bool IsDisabled,
    IReadOnlyList<string> Events, string Definition, string FunctionName, string Language, string? FunctionSource);

/// <summary>카탈로그 읽기 결과: 계약 모양(DbCatalog)과 PostgreSQL 전용 부가 정보.</summary>
public sealed record PgCatalog(DbCatalog Catalog, IReadOnlyList<PgTable> Tables, IReadOnlyList<PgTrigger> Triggers);

/// <summary>pg_catalog → DbCatalog(설계 결정 7). 시스템 스키마와 감사 스키마(dbr_audit)는 뺀다.</summary>
public static partial class PgCatalogReader
{
    const string UserSchemas = """
        n.nspname NOT IN ('pg_catalog', 'information_schema', 'dbr_audit')
        AND n.nspname NOT LIKE 'pg\_toast%' AND n.nspname NOT LIKE 'pg\_temp%'
        """;

    static readonly string TablesSql = $"""
        SELECT c.oid, n.nspname, c.relname, pg_get_userbyid(c.relowner), c.relkind, c.relispartition
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p') AND {UserSchemas}
        ORDER BY n.nspname, c.relname
        """;

    static readonly string ColumnsSql = $"""
        SELECT a.attrelid, a.attnum, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
               a.attgenerated <> '', a.attidentity <> '', pg_get_expr(d.adbin, d.adrelid)
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE c.relkind IN ('r', 'p') AND {UserSchemas} AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attrelid, a.attnum
        """;

    // PK·UNIQUE 인덱스(부분 인덱스·식 인덱스 제외). 열이 모두 NOT NULL인지는 열 정보로 다시 본다.
    static readonly string KeysSql = $"""
        SELECT i.indrelid, i.indexrelid, ic.relname, i.indisprimary, i.indisunique, k.ord, a.attname, NOT a.attnotnull
        FROM pg_index i
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_class c ON c.oid = i.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
        JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
        WHERE (i.indisprimary OR i.indisunique) AND i.indpred IS NULL AND i.indexprs IS NULL AND {UserSchemas}
        ORDER BY i.indrelid, i.indexrelid, k.ord
        """;

    static readonly string ForeignKeysSql = $"""
        SELECT con.oid, con.conname, con.conrelid, con.confrelid, con.confdeltype, con.confupdtype,
               NOT con.convalidated, k.ord, pa.attname, ra.attname
        FROM pg_constraint con
        JOIN pg_namespace n ON n.oid = con.connamespace
        CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(pk, rk, ord)
        JOIN pg_attribute pa ON pa.attrelid = con.conrelid AND pa.attnum = k.pk
        JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = k.rk
        WHERE con.contype = 'f' AND {UserSchemas}
        ORDER BY con.oid, k.ord
        """;

    // tgtype 비트: 1 ROW, 2 BEFORE, 4 INSERT, 8 DELETE, 16 UPDATE, 32 TRUNCATE, 64 INSTEAD
    static readonly string TriggersSql = $"""
        SELECT t.oid, t.tgname, t.tgrelid, t.tgtype, t.tgenabled = 'D', pg_get_triggerdef(t.oid),
               p.proname, l.lanname, p.prosrc
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_proc p ON p.oid = t.tgfoid JOIN pg_language l ON l.oid = p.prolang
        WHERE NOT t.tgisinternal AND t.tgname NOT LIKE 'zzz\_dbr\_audit\_%' AND {UserSchemas}
        ORDER BY t.tgrelid, t.tgname
        """;

    public static async Task<PgCatalog> ReadAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var tables = new List<PgTable>();
        await using (var cmd = new NpgsqlCommand(TablesSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                tables.Add(new PgTable(PgNames.ObjectId(r.GetFieldValue<uint>(0), "테이블"), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetFieldValue<char>(4), r.GetBoolean(5)));

        var columns = new List<CatalogColumn>();
        await using (var cmd = new NpgsqlCommand(ColumnsSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                columns.Add(new CatalogColumn(
                    PgNames.ObjectId(r.GetFieldValue<uint>(0), "열"), r.GetInt16(1), r.GetString(2), r.GetString(3), IsUserClrType: false,
                    IsNullable: r.GetBoolean(4), IsComputed: r.GetBoolean(5), IsIdentity: r.GetBoolean(6), IsRowVersion: false,
                    DefaultDefinition: r.IsDBNull(7) ? null : r.GetString(7)));

        var keys = new List<CatalogIndexColumn>();
        await using (var cmd = new NpgsqlCommand(KeysSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                keys.Add(new CatalogIndexColumn(PgNames.ObjectId(r.GetFieldValue<uint>(0), "인덱스 테이블"), PgNames.ObjectId(r.GetFieldValue<uint>(1), "인덱스"),
                    r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), (int)r.GetInt64(5), r.GetString(6), r.GetBoolean(7)));

        var fks = new List<CatalogForeignKeyColumn>();
        await using (var cmd = new NpgsqlCommand(ForeignKeysSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                fks.Add(new CatalogForeignKeyColumn(
                    PgNames.ObjectId(r.GetFieldValue<uint>(0), "FK"), r.GetString(1),
                    PgNames.ObjectId(r.GetFieldValue<uint>(2), "FK 자식"), PgNames.ObjectId(r.GetFieldValue<uint>(3), "FK 부모"),
                    FkAction(r.GetFieldValue<char>(4)), FkAction(r.GetFieldValue<char>(5)), IsDisabled: false, IsNotTrusted: r.GetBoolean(6),
                    (int)r.GetInt64(7), r.GetString(8), r.GetString(9)));

        var triggers = new List<PgTrigger>();
        await using (var cmd = new NpgsqlCommand(TriggersSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var type = r.GetInt16(3);
                var events = new List<string>();
                if ((type & 4) != 0) events.Add("INSERT");
                if ((type & 16) != 0) events.Add("UPDATE");
                if ((type & 8) != 0) events.Add("DELETE");
                triggers.Add(new PgTrigger(
                    PgNames.ObjectId(r.GetFieldValue<uint>(0), "트리거"), r.GetString(1), PgNames.ObjectId(r.GetFieldValue<uint>(2), "트리거 테이블"),
                    IsRow: (type & 1) != 0, IsBefore: (type & 2) != 0, IsInsteadOf: (type & 64) != 0, IsDisabled: r.GetBoolean(4),
                    events, r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
            }

        var catalog = new DbCatalog(
            Tables: tables.Select(t => new CatalogTable(t.ObjectId, t.Schema, t.Name, TemporalType.None, IsMemoryOptimized: false, t.Owner)).ToList(),
            Columns: columns,
            KeyIndexes: keys,
            ForeignKeys: fks,
            // 트리거 정의는 CREATE TRIGGER 문 + 함수 본문(트리거 분석과 화면 표시용). 이벤트마다 한 줄(SQL Server 모양과 같게).
            Triggers: triggers.SelectMany(t => t.Events.Select(e => new CatalogTriggerEvent(
                t.ObjectId, t.Name, t.TableId, "OBJECT_OR_COLUMN", t.IsInsteadOf, t.IsDisabled, e, IsFirst: false, IsLast: false,
                t.Definition + "\n-- function " + t.FunctionName + " (" + t.Language + ")\n" + t.FunctionSource))).ToList(),
            Files: [],
            Procedures: [],
            Functions: [],
            Synonyms: [],
            Views: []);
        return new PgCatalog(catalog, tables, triggers);
    }

    /// <summary>confdeltype·confupdtype → 계약 문자열("CASCADE", "SET_NULL", "SET_DEFAULT", "NO_ACTION").</summary>
    public static string FkAction(char code) => code switch
    {
        'c' => "CASCADE",
        'n' => "SET_NULL",
        'd' => "SET_DEFAULT",
        _ => "NO_ACTION", // 'a' NO ACTION, 'r' RESTRICT
    };

    [GeneratedRegex(@"\b(now|clock_timestamp|statement_timestamp|transaction_timestamp|timeofday|random|gen_random_uuid|uuid_generate_v[1-4]|current_user|session_user)\s*\(|\b(current_timestamp|current_date|current_time|localtimestamp|localtime|current_user|session_user)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NondeterministicDefault();

    [GeneratedRegex(@"\bnextval\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex SequenceDefault();

    /// <summary>
    /// 매니페스트의 예상값 사유(수집기가 읽는 문자열): IDENTITY(식별 열·nextval 기본값 — 적용 때 새 값), NONDETERMINISTIC_DEFAULT(now()·random() 등).
    /// </summary>
    public static string? VolatileReason(CatalogColumn c) =>
        c.IsIdentity || (c.DefaultDefinition is { } d1 && SequenceDefault().IsMatch(d1)) ? "IDENTITY"
        : c.DefaultDefinition is { } d2 && NondeterministicDefault().IsMatch(d2) ? "NONDETERMINISTIC_DEFAULT"
        : c.IsComputed ? "COMPUTED"
        : null;
}
