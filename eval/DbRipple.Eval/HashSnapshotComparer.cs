using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbRipple.Contracts;
using Microsoft.Data.SqlClient;

namespace DbRipple.Eval;

/// <summary>한 시점의 행 해시·값(테이블 표시 이름 → 정규 키 → 행). Schema는 읽은 테이블 목록.</summary>
public sealed record RowHashSnapshot(
    string Database,
    IReadOnlyList<OracleTable> Schema,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, RowHash>> Tables);

/// <summary>행 하나의 해시와 값. 키 없는 테이블은 같은 값 행을 한 항목으로 모으고 Multiplicity로 센다.</summary>
public sealed record RowHash(string Sha256, IReadOnlyDictionary<string, string?> Values, long Multiplicity);

/// <summary>비교 결과: 순 변경과 비교기 자체 점검 문제(해시 판정과 셀 판정이 어긋남 등).</summary>
public sealed record HashComparison(IReadOnlyList<OracleRowChange> Changes, IReadOnlyList<string> Issues);

/// <summary>
/// 독립 해시 비교기(§10.3): 행별 SHA2_256(FOR JSON PATH, INCLUDE_NULL_VALUES)을 떠서 바뀐 행을 찾고, 그 행을 셀 단위로 비교한다.
/// 제품 C12(DbRipple.Diff)·Core 값 식·Core 카탈로그와 코드를 공유하지 않는다. 값 문자열 규칙은 설계 §6.1(명세)을 따로 구현한 것이다.
/// 열은 위치 별칭(p1, p2, …)으로 JSON에 넣으므로 이름에 '.'이 있는 열이나 생성 속성과 이름이 겹치는 열도 비교된다(제품은 이런 열을 unobservable로 둔다, §6.1).
/// 읽기는 모두 세 부분 이름([db].[스키마].[테이블])이고 session의 연결·트랜잭션 안에서 한다(미커밋 변경을 본다). 세션 옵션·트랜잭션은 바꾸지 않는다.
/// 참고 구현: cascadelab/verification/CascadeLab.Verify/Program.cs(1주차, 키 없는 테이블 미지원).
/// </summary>
public sealed class HashSnapshotComparer
{
    /// <summary>명령 제한 시간(초).</summary>
    public int CommandTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// database의 사용자 테이블(sys.tables, is_ms_shipped = 0)을 읽는다. excludeSchemas(예: 감사 스키마)와 excludeTables("스키마.테이블")는 뺀다.
    /// 메모리 최적화 테이블은 세 부분 이름으로 트랜잭션 안에서 읽을 수 없어 빼고 excluded에 사유와 함께 남긴다.
    /// </summary>
    public async Task<(IReadOnlyList<OracleTable> Tables, IReadOnlyList<string> Excluded)> LoadTablesAsync(
        SqlSession session, string database, IEnumerable<string> excludeSchemas, IEnumerable<string> excludeTables, CancellationToken ct)
    {
        var db = Quote(database);
        var exSchemas = excludeSchemas.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exTables = excludeTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = new List<string>();

        var columns = await RowsAsync(session, $"""
            SELECT t.object_id, s.name, t.name, t.is_memory_optimized, c.name, c.column_id,
                   CASE WHEN ty.is_user_defined = 1 AND ty.is_assembly_type = 0 THEN bt.name ELSE ty.name END AS type_name,
                   c.is_identity, c.is_computed,
                   CAST(CASE WHEN ty.name = N'timestamp' OR bt.name = N'timestamp' THEN 1 ELSE 0 END AS bit) AS is_rowversion,
                   CAST(CASE WHEN ty.is_assembly_type = 1 AND ty.is_user_defined = 1 THEN 1 ELSE 0 END AS bit) AS is_user_clr,
                   dc.definition
            FROM {db}.sys.tables AS t
            JOIN {db}.sys.schemas AS s ON s.schema_id = t.schema_id
            JOIN {db}.sys.columns AS c ON c.object_id = t.object_id
            JOIN {db}.sys.types AS ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN {db}.sys.types AS bt ON bt.user_type_id = ty.system_type_id
            LEFT JOIN {db}.sys.default_constraints AS dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id;
            """, ct);

        var keys = await RowsAsync(session, $"""
            SELECT i.object_id, i.index_id, i.is_primary_key, ic.key_ordinal, c.name, c.is_nullable
            FROM {db}.sys.indexes AS i
            JOIN {db}.sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN {db}.sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN {db}.sys.tables AS t ON t.object_id = i.object_id
            WHERE t.is_ms_shipped = 0 AND (i.is_primary_key = 1 OR i.is_unique = 1)
              AND i.has_filter = 0 AND i.is_disabled = 0 AND ic.is_included_column = 0 AND ic.key_ordinal > 0
            ORDER BY i.object_id, i.index_id, ic.key_ordinal;
            """, ct);
        var keyByTable = keys.GroupBy(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.GroupBy(r => Convert.ToInt32(r[1], CultureInfo.InvariantCulture)).OrderBy(x => x.Key).ToList());

        var tables = new List<OracleTable>();
        foreach (var g in columns.GroupBy(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)))
        {
            var first = g.First();
            var schema = (string)first[1]!;
            var name = (string)first[2]!;
            var display = schema + "." + name;
            if (exSchemas.Contains(schema) || exTables.Contains(display)) { excluded.Add(display + ": 제외 대상"); continue; }
            if ((bool)first[3]!) { excluded.Add(display + ": 메모리 최적화 테이블(세 부분 이름 읽기 불가)"); continue; }
            var cols = g.Select(r => new OracleColumn(
                Name: (string)r[4]!,
                ColumnId: Convert.ToInt32(r[5], CultureInfo.InvariantCulture),
                TypeName: ((string)r[6]!).ToLowerInvariant(),
                IsIdentity: (bool)r[7]!,
                IsComputed: (bool)r[8]!,
                IsRowVersion: (bool)r[9]!,
                IsUserClrType: (bool)r[10]!,
                HasNondeterministicDefault: IsNondeterministicDefault(r[11] as string))).ToList();

            var kind = OracleKeyKind.ROW;
            IReadOnlyList<string> keyCols = [];
            if (keyByTable.TryGetValue(g.Key, out var idx))
            {
                var pk = idx.FirstOrDefault(x => (bool)x.First()[2]!);
                var uq = idx.FirstOrDefault(x => !(bool)x.First()[2]! && x.All(r => !(bool)r[5]!));
                if (pk is not null) { kind = OracleKeyKind.PK; keyCols = pk.Select(r => (string)r[4]!).ToList(); }
                else if (uq is not null) { kind = OracleKeyKind.UNIQUE; keyCols = uq.Select(r => (string)r[4]!).ToList(); }
            }
            tables.Add(new OracleTable(schema, name, kind, keyCols, cols));
        }
        return (tables.OrderBy(t => t.Display, StringComparer.Ordinal).ToList(), excluded);
    }

    /// <summary>
    /// 현재 상태의 행 해시와 행 값(session의 트랜잭션 안에서 database를 읽는다). tables는 보통 LoadTablesAsync 결과(감사 스키마 제외).
    /// </summary>
    public async Task<RowHashSnapshot> TakeAsync(SqlSession session, string database, IReadOnlyList<OracleTable> tables, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, RowHash>>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            var items = Items(t);
            var select = string.Join(", ", items.Select((it, i) => $"{it.Expression} AS [p{i + 1}]"));
            var sql = $"""
                SELECT j.v, HASHBYTES('SHA2_256', j.v)
                FROM {Quote(database)}.{Quote(t.Schema)}.{Quote(t.Name)} AS x
                CROSS APPLY (SELECT (SELECT {select} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) AS v) AS j;
                """;
            var rows = new Dictionary<string, RowHash>(StringComparer.Ordinal);
            await using var cmd = session.CreateCommand(sql, CommandTimeoutSeconds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var json = r.GetString(0);
                var hash = Convert.ToHexString((byte[])r.GetValue(1));
                var values = ParseRow(json, items);
                var key = KeyOf(t, values);
                if (rows.TryGetValue(key, out var existing))
                {
                    if (t.KeyKind != OracleKeyKind.ROW) throw new InvalidOperationException($"{t.Display}: 키 중복 {RowKeys.Display(key)}");
                    rows[key] = existing with { Multiplicity = existing.Multiplicity + 1 };
                }
                else rows[key] = new RowHash(hash, values, 1);
            }
            result[t.Display] = rows;
        }
        return new RowHashSnapshot(database, tables, result);
    }

    /// <summary>
    /// 두 스냅샷의 순 변경. 키 있는 테이블은 키 조인(PK가 바뀐 행은 옛 키 DELETED + 새 키 INSERTED), 키 없는 테이블은 다중집합 개수 차이.
    /// 해시가 다른데 셀이 같거나, 해시가 같은데 셀이 다르면 Issues에 남긴다(비교기 자체 점검). 결과 순서: 테이블, 키(서수).
    /// </summary>
    public HashComparison Compare(RowHashSnapshot before, RowHashSnapshot after)
    {
        var changes = new List<OracleRowChange>();
        var issues = new List<string>();
        foreach (var t in before.Schema.OrderBy(x => x.Display, StringComparer.Ordinal))
        {
            if (!before.Tables.TryGetValue(t.Display, out var b)) { issues.Add($"{t.Display}: before에 없음"); continue; }
            if (!after.Tables.TryGetValue(t.Display, out var a)) { issues.Add($"{t.Display}: after에 없음"); continue; }
            if (t.KeyKind == OracleKeyKind.ROW)
            {
                foreach (var k in b.Keys.Union(a.Keys).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var nb = b.TryGetValue(k, out var rb) ? rb.Multiplicity : 0;
                    var na = a.TryGetValue(k, out var ra) ? ra.Multiplicity : 0;
                    if (rb is not null && ra is not null && rb.Sha256 != ra.Sha256) issues.Add($"{t.Display}{RowKeys.Display(k)}: 같은 값 행의 해시가 다름");
                    if (na > nb) changes.Add(new OracleRowChange(t.Display, k, ChangeKinds.MultisetAdded, na - nb, null, ra!.Values, []));
                    else if (nb > na) changes.Add(new OracleRowChange(t.Display, k, ChangeKinds.MultisetRemoved, nb - na, rb!.Values, null, []));
                }
                continue;
            }
            foreach (var k in b.Keys.Union(a.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                var hasB = b.TryGetValue(k, out var rb);
                var hasA = a.TryGetValue(k, out var ra);
                if (!hasB) { changes.Add(new OracleRowChange(t.Display, k, ChangeKinds.Inserted, 1, null, ra!.Values, [])); continue; }
                if (!hasA) { changes.Add(new OracleRowChange(t.Display, k, ChangeKinds.Deleted, 1, rb!.Values, null, [])); continue; }
                var changed = rb!.Values.Keys.Union(ra!.Values.Keys)
                    .Where(c => !string.Equals(Get(rb.Values, c), Get(ra.Values, c), StringComparison.Ordinal)
                                || rb.Values.ContainsKey(c) != ra.Values.ContainsKey(c))
                    .ToList();
                var hashChanged = rb.Sha256 != ra.Sha256;
                if (hashChanged != changed.Count > 0)
                    issues.Add($"{t.Display}{RowKeys.Display(k)}: hashChanged={hashChanged}, cellChanged={changed.Count > 0}");
                if (changed.Count > 0)
                    changes.Add(new OracleRowChange(t.Display, k, ChangeKinds.Updated, 1, rb.Values, ra.Values, changed));
            }
        }
        return new HashComparison(changes, issues);
    }

    /// <summary>스냅샷 요약(테이블·키·해시·개수의 SHA-256 앞 16자리). 시작 상태 확인·롤백 복원 확인에 쓴다.</summary>
    public static string Digest(RowHashSnapshot s)
    {
        var sb = new StringBuilder();
        foreach (var t in s.Tables.Keys.OrderBy(x => x, StringComparer.Ordinal))
            foreach (var (k, row) in s.Tables[t].OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append(t).Append('|').Append(k).Append('|').Append(row.Sha256).Append('|').Append(row.Multiplicity.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>SQL 식별자 인용([…], ]는 ]]).</summary>
    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>
    /// 열 하나의 (SQL 식, 결과 속성 이름) 목록(설계 §6.1 표를 하네스가 따로 구현). 첫 항목의 속성 이름은 열 이름이다.
    /// 키 없는 테이블(ROW)은 계산 열을 뺀다: 계산 열은 같은 행의 다른 열에서 나오므로 다중집합 판정에 정보가 없고, 오버플로하는 비지속 계산 열은 읽기만 해도 8115다.
    /// 제품 C12의 키 없는 테이블 규칙(이미지·다중집합에서 계산 열 제외)과 맞춘 것이다(코드는 공유하지 않는다, 2026-11-10 검토).
    /// </summary>
    public static IReadOnlyList<(string Expression, string Property)> Items(OracleTable t)
    {
        var list = new List<(string, string)>();
        foreach (var c in t.Columns.Where(c => t.KeyKind != OracleKeyKind.ROW || !c.IsComputed).OrderBy(c => c.ColumnId))
        {
            var col = "x." + Quote(c.Name);
            if (c.IsUserClrType) { list.Add(($"CONVERT(nvarchar(max), {col})", c.Name)); continue; }
            switch (c.TypeName)
            {
                case "hierarchyid":
                    list.Add(($"{col}.ToString()", c.Name));
                    break;
                case "geography":
                case "geometry":
                    list.Add(($"{col}.ToString()", c.Name));
                    list.Add(($"{col}.STSrid", c.Name + "@srid"));
                    break;
                case "xml":
                    list.Add(($"CONVERT(nvarchar(max), {col})", c.Name));
                    break;
                case "float":
                    list.Add(($"CONVERT(varchar(30), {col}, 3)", c.Name));
                    break;
                case "real":
                    list.Add(($"CONVERT(varchar(30), CONVERT(float, {col}), 3)", c.Name));
                    break;
                case "sql_variant":
                    list.Add(($"CASE WHEN SQL_VARIANT_PROPERTY({col}, 'BaseType') IN ('float', 'real') THEN CONVERT(sql_variant, CONVERT(varchar(30), CONVERT(float, {col}), 3)) ELSE {col} END", c.Name));
                    list.Add(($"CONVERT(nvarchar(128), SQL_VARIANT_PROPERTY({col}, 'BaseType'))", c.Name + "@type"));
                    break;
                case "text":
                    list.Add(($"CONVERT(varchar(max), {col})", c.Name));
                    break;
                case "ntext":
                    list.Add(($"CONVERT(nvarchar(max), {col})", c.Name));
                    break;
                case "image":
                    list.Add(($"CONVERT(varbinary(max), {col})", c.Name));
                    break;
                default:
                    list.Add((col, c.Name));
                    break;
            }
        }
        return list;
    }

    static Dictionary<string, string?> ParseRow(string json, IReadOnlyList<(string Expression, string Property)> items)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        for (var i = 0; i < items.Count; i++)
        {
            // INCLUDE_NULL_VALUES라 모든 별칭이 나온다. 없으면 형식 문제이므로 바로 실패시킨다.
            var v = doc.RootElement.GetProperty("p" + (i + 1).ToString(CultureInfo.InvariantCulture));
            if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new InvalidOperationException($"열 {items[i].Property}: 예상 밖 JSON 값 형식 {v.ValueKind}");
            values[items[i].Property] = RowKeys.JsonValueText(v);
        }
        return values;
    }

    static string KeyOf(OracleTable t, IReadOnlyDictionary<string, string?> values) =>
        t.KeyKind == OracleKeyKind.ROW
            ? RowKeys.Canonical(values)
            : RowKeys.Canonical(t.KeyColumns.Select(c => new KeyValuePair<string, string?>(c, values[c])));

    static string? Get(IReadOnlyDictionary<string, string?> d, string c) => d.TryGetValue(c, out var v) ? v : null;

    static readonly string[] NondeterministicFunctions =
        ["getdate", "getutcdate", "sysdatetime", "sysutcdatetime", "sysdatetimeoffset", "current_timestamp", "newid", "newsequentialid", "rand", "next value for"];

    static bool IsNondeterministicDefault(string? definition) =>
        definition is not null && NondeterministicFunctions.Any(f => definition.Contains(f, StringComparison.OrdinalIgnoreCase));

    static async Task<List<object?[]>> RowsAsync(SqlSession session, string sql, CancellationToken ct)
    {
        await using var cmd = session.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object?[]>();
        while (await r.ReadAsync(ct))
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
