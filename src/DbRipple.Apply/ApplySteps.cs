using System.Data;
using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Sql;
using Microsoft.Data.SqlClient;

namespace DbRipple.Apply;

/// <summary>행 이미지 항목: SQL 식, 사전 속성 이름(열 이름 또는 열@srid·열@type), 소속 열 이름.</summary>
internal sealed record ImageItem(string Expression, string Property, string Column);

/// <summary>
/// §6.1 행 이미지(C12 NetDiffer와 같은 값 형식). Diff 프로젝트를 참조하지 않으므로(AD7) 같은 규칙을 Core <see cref="AuditValueExpressions"/>로 다시 만든다:
/// 열마다 AuditValueExpressions.Items, 감사 트리거가 볼 수 없는 text·ntext·image는 varchar(max)·nvarchar(max)·varbinary(max)로, 생성 속성이 실제 열 이름과 겹치면 생성 속성을 뺀다.
/// 속성 이름은 위치 별칭(p0, p1, …)으로 만들고 읽을 때 되돌린다(열 이름의 '.'·생성 속성 충돌에도 FOR JSON 구조가 깨지지 않게).
/// </summary>
internal static class RowImage
{
    const string JsonOptions = "FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES";

    /// <summary>열 하나당 항목(열 순서). includeComputed = false면 계산 열을 뺀다(다중집합 이미지, NetDiffer.Multiset과 같음).</summary>
    public static IReadOnlyList<ImageItem> Items(TableInfo t, string alias, bool includeComputed = true)
    {
        var realNames = t.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var items = new List<ImageItem>();
        foreach (var c in t.Columns)
        {
            if (c.IsComputed && !includeComputed) continue;
            var col = alias + "." + SqlText.Quote(c.Name);
            if (AuditValueExpressions.IsUnobservable(c))
            {
                var target = c.TypeName.Equals("image", StringComparison.OrdinalIgnoreCase) ? "varbinary(max)"
                    : c.TypeName.Equals("text", StringComparison.OrdinalIgnoreCase) ? "varchar(max)" : "nvarchar(max)";
                items.Add(new ImageItem($"CONVERT({target}, {col})", c.Name, c.Name));
                continue;
            }
            var first = true;
            foreach (var (expr, prop) in AuditValueExpressions.Items(c, alias))
            {
                if (!first && realNames.Contains(prop)) continue;
                first = false;
                items.Add(new ImageItem(expr, prop, c.Name));
            }
        }
        return items;
    }

    /// <summary>행 이미지 JSON 식(위치 별칭, 상관 하위 질의).</summary>
    public static string Json(IReadOnlyList<ImageItem> items) =>
        "(SELECT " + string.Join(", ", items.Select((x, i) => $"{x.Expression} AS [p{i}]")) + " " + JsonOptions + ")";

    /// <summary>위치 별칭 JSON → §6.1 문자열 사전(Core AuditJson 규칙).</summary>
    public static Dictionary<string, string?> Parse(string json, IReadOnlyList<ImageItem> items)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, string?>(items.Count, StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
            map[items[i].Property] = doc.RootElement.TryGetProperty("p" + i, out var v) ? AuditJson.ToText(v) : null;
        return map;
    }

    /// <summary>값 사전의 정규 문자열(정해진 속성 순서, NULL과 빈 문자열·없는 속성을 구분). 다중집합·짝짓기 묶음 키로 쓴다.</summary>
    public static string Canonical(IReadOnlyDictionary<string, string?>? row, IEnumerable<string> properties) =>
        string.Join("\u001f", properties.Select(p => p + "\u001e" + (row is null || !row.TryGetValue(p, out var v) ? "\u0002" : v is null ? "\u0000" : "\u0001" + v)));

    /// <summary>키 문자열(열 이름과 값, NULL 구분).</summary>
    public static string KeyString(IReadOnlyList<string> columns, IReadOnlyList<string?> values) =>
        string.Join("\u001f", columns.Select((c, i) => c + "\u001e" + (i < values.Count ? values[i] is { } v ? "\u0001" + v : "\u0000" : "\u0002")));
}

/// <summary>
/// 키로 행 이미지 읽기(드리프트·KEYSET 공용). 키 값(§6.1 문자열)을 매개변수로 보내 VALUES와 조인한다.
/// 문자열 → 열 형식 변환은 서버의 암시적 변환에 맡기되, 문자(varchar)는 열 형식으로 맞춰 탐색을 유지하고, 이진은 §6.1 base64를 풀어 보낸다.
/// 매개변수로 보낼 수 없는 키 형식(sql_variant, CLR 등)이면 테이블 전체를 읽어 §6.1 키 문자열로 맞춘다(같은 잠금 힌트, 결과는 같고 느리다).
/// </summary>
internal static class KeyedRows
{
    /// <summary>VALUES 한 묶음의 매개변수 상한(2100 제한 아래).</summary>
    const int MaxParameters = 2000;
    /// <summary>VALUES 한 묶음의 행 상한(행 생성자 1000 제한 아래).</summary>
    const int MaxRows = 500;

    static readonly HashSet<string> ImplicitTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nchar", "nvarchar", "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney",
        "float", "real", "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time", "uniqueidentifier",
    };
    static readonly HashSet<string> AnsiTypes = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar" };
    static readonly HashSet<string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase) { "binary", "varbinary", "timestamp", "rowversion" };

    /// <summary>
    /// keys마다 현재 행 이미지(없으면 null). lockRows면 WITH (UPDLOCK, HOLDLOCK)으로 읽어 트랜잭션 끝까지 잠근다(§5.3 드리프트).
    /// 세션의 현재 DB에서 읽는다(원본 전용 연결).
    /// </summary>
    public static async Task<IReadOnlyList<Dictionary<string, string?>?>> ReadAsync(
        SqlSession session, TableInfo t, IReadOnlyList<string> keyColumns, IReadOnlyList<IReadOnlyList<string?>> keys,
        bool lockRows, int timeoutSeconds, CancellationToken ct)
    {
        var result = new Dictionary<string, string?>?[keys.Count];
        if (keys.Count == 0) return result;
        var cols = keyColumns.Select(k => t.Columns.FirstOrDefault(c => string.Equals(c.Name, k, StringComparison.Ordinal))).ToList();
        var items = RowImage.Items(t, "x");
        if (keyColumns.Count == 0 || cols.Any(c => c is null || !Supported(c)))
            return await ScanAsync(session, t, keyColumns, keys, items, lockRows, timeoutSeconds, ct);

        var hint = lockRows ? " WITH (UPDLOCK, HOLDLOCK)" : "";
        var on = string.Join(" AND ", cols.Select((c, i) => $"x.{SqlText.Quote(c!.Name)} = {Converted(c, "v.k" + i)}"));
        var perChunk = Math.Max(1, Math.Min(MaxRows, MaxParameters / keyColumns.Count));
        for (var start = 0; start < keys.Count; start += perChunk)
        {
            var count = Math.Min(perChunk, keys.Count - start);
            await using var cmd = session.CreateCommand("", timeoutSeconds);
            var rows = new List<string>(count);
            for (var r = 0; r < count; r++)
            {
                var key = keys[start + r];
                var cells = new List<string> { (start + r).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                for (var i = 0; i < cols.Count; i++)
                {
                    var name = $"@k{r}_{i}";
                    cmd.Parameters.Add(Parameter(name, cols[i]!, i < key.Count ? key[i] : null));
                    cells.Add(name);
                }
                rows.Add("(" + string.Join(", ", cells) + ")");
            }
            var aliases = string.Join(", ", Enumerable.Range(0, cols.Count).Select(i => "k" + i));
            cmd.CommandText = $"""
                SELECT v.ord, {RowImage.Json(items)} AS img
                FROM (VALUES {string.Join(",\n       ", rows)}) AS v(ord, {aliases})
                JOIN {t.QuotedName} AS x{hint} ON {on};
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var ord = reader.GetInt32(0);
                if (result[ord] is null && !reader.IsDBNull(1)) result[ord] = RowImage.Parse(reader.GetString(1), items);
            }
        }
        return result;
    }

    static bool Supported(ColumnInfo c) =>
        !c.IsUserClrType && (ImplicitTypes.Contains(c.TypeName) || AnsiTypes.Contains(c.TypeName) || BinaryTypes.Contains(c.TypeName)
                             || c.TypeName.Equals("hierarchyid", StringComparison.OrdinalIgnoreCase));

    static string Converted(ColumnInfo c, string value) =>
        c.TypeName.Equals("hierarchyid", StringComparison.OrdinalIgnoreCase) ? $"hierarchyid::Parse({value})" : value;

    // NULL 키 값이나 풀 수 없는 base64는 어떤 행과도 같지 않은 NULL로 보낸다(= 행 없음으로 판정, 닫힌 쪽).
    static SqlParameter Parameter(string name, ColumnInfo c, string? value)
    {
        if (BinaryTypes.Contains(c.TypeName))
        {
            byte[]? bytes = null;
            if (value is not null) { try { bytes = Convert.FromBase64String(value); } catch (FormatException) { bytes = null; } }
            return new SqlParameter(name, SqlDbType.VarBinary, -1) { Value = (object?)bytes ?? DBNull.Value };
        }
        if (AnsiTypes.Contains(c.TypeName))
            return new SqlParameter(name, SqlDbType.VarChar, 8000) { Value = (object?)value ?? DBNull.Value };
        return new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = (object?)value ?? DBNull.Value };
    }

    static async Task<IReadOnlyList<Dictionary<string, string?>?>> ScanAsync(
        SqlSession session, TableInfo t, IReadOnlyList<string> keyColumns, IReadOnlyList<IReadOnlyList<string?>> keys,
        IReadOnlyList<ImageItem> items, bool lockRows, int timeoutSeconds, CancellationToken ct)
    {
        var wanted = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var k = RowImage.KeyString(keyColumns, keys[i]);
            if (!wanted.TryGetValue(k, out var list)) wanted[k] = list = [];
            list.Add(i);
        }
        var result = new Dictionary<string, string?>?[keys.Count];
        var hint = lockRows ? " WITH (UPDLOCK, HOLDLOCK)" : "";
        await using var cmd = session.CreateCommand($"SELECT {RowImage.Json(items)} AS img FROM {t.QuotedName} AS x{hint};", timeoutSeconds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            var row = RowImage.Parse(reader.GetString(0), items);
            var k = RowImage.KeyString(keyColumns, keyColumns.Select(c => row.GetValueOrDefault(c)).ToList());
            if (!wanted.TryGetValue(k, out var idx)) continue;
            foreach (var i in idx) result[i] ??= row;
        }
        return result;
    }

    /// <summary>키 없는 테이블의 이미지별 개수(계산 열 제외, NetDiffer.Multiset과 같은 이미지). source는 "[db].[s].[t]" 또는 "[s].[t]".</summary>
    public static async Task<Dictionary<string, long>> MultisetCountsAsync(
        SqlSession session, TableInfo t, string source, bool lockRows, int timeoutSeconds, CancellationToken ct)
    {
        var items = RowImage.Items(t, "r", includeComputed: false);
        var properties = items.Select(i => i.Property).ToList();
        var hint = lockRows ? " WITH (UPDLOCK, HOLDLOCK)" : "";
        // 서버에서는 바이트로 묶고(정렬 규칙·끝 공백 무시를 피함) 받은 뒤 §6.1 정규 문자열로 다시 합친다.
        var sql = $"""
            SELECT CAST(b AS nvarchar(max)) AS image_json, n
            FROM (SELECT b, COUNT_BIG(*) AS n
                  FROM (SELECT CAST({RowImage.Json(items)} AS varbinary(max)) AS b FROM {source} AS r{hint}) AS x
                  GROUP BY b, DATALENGTH(b)) AS g;
            """;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var cmd = session.CreateCommand(sql, timeoutSeconds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            var key = RowImage.Canonical(RowImage.Parse(reader.GetString(0), items), properties);
            counts[key] = counts.GetValueOrDefault(key) + reader.GetInt64(1);
        }
        return counts;
    }
}

/// <summary>
/// 드리프트 검사(§5.3). 미리보기 NetChanges의 before 행(UPDATED·DELETED는 키로, MULTISET_REMOVED는 모든 열 묶음 개수로)을
/// 원본에서 WITH (UPDLOCK, HOLDLOCK)으로 읽어 §6.1 형식(Core AuditValueExpressions)으로 비교하고 커밋까지 잠근다.
/// 다중집합의 기대 개수는 미리보기 기준 스냅샷(PreviewResult.baseline.snapshotDb)에서 센다(미리보기에는 바뀐 개수만 있다).
/// 잡지 못하는 것(새로 WHERE를 만족하게 된 행, 스냅샷 직후 변경)은 최종 차이 대조가 잡는다(§5.3).
/// </summary>
internal static class DriftChecker
{
    /// <summary>드리프트 행. 없으면 빈 목록. 세션은 원본 전용 연결(현재 DB = 원본)이고 열린 트랜잭션 안이어야 잠금이 커밋까지 간다.</summary>
    public static async Task<IReadOnlyList<DriftRow>> CheckAsync(SqlSession session, PreviewRecord preview, IReadOnlyList<TableInfo> tables, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(tables);
        var byName = tables.GroupBy(t => t.DisplayName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var drift = new List<DriftRow>();

        var keyed = preview.NetChanges
            .Where(n => n.Key.Kind != KeyKind.ROW && n.Before is not null && n.Change is NetChangeKind.Updated or NetChangeKind.Deleted)
            .GroupBy(n => n.Table, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in keyed)
        {
            ct.ThrowIfCancellationRequested();
            var changes = g.ToList();
            if (!byName.TryGetValue(g.Key, out var t))
            {
                // 카탈로그에 없는 테이블의 before는 확인할 수 없다: 닫힌 쪽(MISSING)으로 둔다.
                drift.AddRange(changes.Select(n => new DriftRow { Table = n.Table, Key = n.Key, Kind = DriftKind.Missing, Expected = n.Before }));
                continue;
            }
            // 같은 테이블 안에서도 키 열이 같다(TableKey.Choose). 혹시 다르면 키 열 묶음마다 나눠 읽는다.
            foreach (var byKey in changes.GroupBy(n => string.Join("\u001f", n.Key.Columns), StringComparer.Ordinal))
            {
                var list = byKey.ToList();
                var rows = await KeyedRows.ReadAsync(session, t, list[0].Key.Columns, list.Select(n => n.Key.Values).ToList(), lockRows: true, timeoutSeconds, ct);
                var items = RowImage.Items(t, "x");
                for (var i = 0; i < list.Count; i++)
                {
                    var n = list[i];
                    var actual = rows[i];
                    if (actual is null)
                    {
                        drift.Add(new DriftRow { Table = n.Table, Key = n.Key, Kind = DriftKind.Missing, Expected = n.Before });
                        continue;
                    }
                    var changed = new List<string>();
                    foreach (var (prop, expected) in n.Before!)
                    {
                        if (string.Equals(expected, actual.GetValueOrDefault(prop), StringComparison.Ordinal)) continue;
                        var column = items.FirstOrDefault(x => string.Equals(x.Property, prop, StringComparison.Ordinal))?.Column ?? prop;
                        if (!changed.Contains(column, StringComparer.Ordinal)) changed.Add(column);
                    }
                    if (changed.Count > 0)
                        drift.Add(new DriftRow { Table = n.Table, Key = n.Key, Kind = DriftKind.Changed, Columns = changed, Expected = n.Before, Actual = actual });
                }
            }
        }

        var multiset = preview.NetChanges
            .Where(n => n.Key.Kind == KeyKind.ROW && n.Change == NetChangeKind.MultisetRemoved && n.Before is not null)
            .GroupBy(n => n.Table, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        string? snapshotDb = null;
        foreach (var g in multiset)
        {
            ct.ThrowIfCancellationRequested();
            if (!byName.TryGetValue(g.Key, out var t))
            {
                drift.AddRange(g.Select(n => new DriftRow { Table = n.Table, Key = n.Key, Kind = DriftKind.MultisetCount, Expected = n.Before, ExpectedMultiplicity = null, ActualMultiplicity = null }));
                continue;
            }
            if (snapshotDb is null)
            {
                snapshotDb = preview.Result.Baseline.SnapshotDb;
                OriginNames.Validate(snapshotDb, "기준 스냅샷");
            }
            var properties = RowImage.Items(t, "r", includeComputed: false).Select(i => i.Property).ToList();
            var origin = await KeyedRows.MultisetCountsAsync(session, t, t.QuotedName, lockRows: true, timeoutSeconds, ct);
            var baseline = await KeyedRows.MultisetCountsAsync(session, t, SqlText.Quote(snapshotDb) + "." + t.QuotedName, lockRows: false, timeoutSeconds, ct);
            foreach (var n in g)
            {
                var key = RowImage.Canonical(n.Before, properties);
                var expected = baseline.GetValueOrDefault(key);
                var actual = origin.GetValueOrDefault(key);
                if (expected != actual)
                    drift.Add(new DriftRow
                    {
                        Table = n.Table, Key = n.Key, Kind = DriftKind.MultisetCount, Expected = n.Before,
                        ExpectedMultiplicity = expected, ActualMultiplicity = actual,
                    });
            }
        }
        return drift;
    }
}

/// <summary>KEYSET_HASH 대상 열(원본 카탈로그). TypeName은 CLR이면 sys.types 이름, 그 밖은 기반 시스템 형식명.</summary>
internal sealed record HashColumn(string Name, string TypeName, bool IsAssemblyType, bool IsComputed);

/// <summary>KEYSET_HASH 대상 테이블(원본 sys.tables 사용자 테이블).</summary>
internal sealed record HashTarget(string Schema, string Name, bool IsMemoryOptimized, IReadOnlyList<HashColumn> Columns)
{
    /// <summary>"스키마.테이블"(TableInfo.DisplayName과 같은 형식).</summary>
    public string Table => Schema + "." + Name;
    /// <summary>[스키마].[테이블](QUOTENAME 규칙).</summary>
    public string QuotedName => SqlText.Quote(Schema, Name);
}

/// <summary>테이블 상태 한 번 읽기. Checksum은 해시하지 않았거나(미리보기 테이블·비교 가능 열 없음) 빈 테이블이면 null.</summary>
internal sealed record TableState(long Count, int? Checksum);

/// <summary>실행 전 KEYSET_HASH 기준(대상 목록은 실행 뒤 읽기에도 그대로 쓴다).</summary>
internal sealed record KeysetBaseline(IReadOnlyList<HashTarget> Targets, IReadOnlySet<string> PreviewTables, IReadOnlyDictionary<string, TableState> States);

/// <summary>
/// KEYSET_HASH 검증(§5.3 원본 스냅샷을 만들 수 없을 때, 2026-11-13 KEYSET 대체). 원본 트랜잭션 안에서 사용자 문장 직전과 직후에
/// 원본 카탈로그의 사용자 테이블마다 COUNT_BIG(*)를 읽고, 미리보기에 없던 테이블은 CHECKSUM_AGG(BINARY_CHECKSUM(비교 가능 열))도 읽어 비교한다.
/// 미리보기 테이블은 건수 변화(netSummary 기대값)와 미리보기 키 집합 after 값으로 확인한다. 같은 세션이라 원본 연결의 LOCK_TIMEOUT을 그대로 탄다.
/// </summary>
internal static class KeysetVerifier
{
    /// <summary>KEYSET_HASH 한계 문구(결과 Limitations, 호출자가 모두 넣는다).</summary>
    public static readonly IReadOnlyList<string> Limitations =
    [
        "KEYSET_HASH 검증: 미리보기에 있던 테이블은 행 수 변화와 미리보기 키 행 값으로만 확인하므로 키 밖 행의 UPDATE는 놓칠 수 있음. 새 IDENTITY 행과 그 자식 행은 건수로만 확인함",
        "KEYSET_HASH 검증: 미리보기에 없던 테이블은 실행 전후 COUNT_BIG(*)와 CHECKSUM_AGG(BINARY_CHECKSUM(열))로만 비교함. 해시 충돌, 행끼리 값을 맞바꾸는 변경, 같은 행이 짝수 개 생기는 변경, 비교 불가 형식·계산 열의 변경은 놓칠 수 있음",
        "KEYSET_HASH 검증: 원본의 모든 사용자 테이블을 실행 전후 두 번 전부 읽음(큰 DB는 느리고, 원본 LOCK_TIMEOUT을 넘으면 오류로 롤백)",
        "KEYSET_HASH 검증: 실행 전후 읽기 사이에 다른 세션이 미리보기에 없던 테이블에 쓰면 거짓 불일치로 롤백함(커밋하지 않는 쪽으로 실패). 권한으로 보이지 않는 테이블은 비교하지 못함",
    ];

    /// <summary>예전 단일 문구 이름(첫 한계 문구).</summary>
    public static string Limitation => Limitations[0];

    /// <summary>BINARY_CHECKSUM이 무시하거나 오류를 내는 형식, 값이 늘 바뀌는 rowversion. CLR 형식(hierarchyid·geometry·geography·사용자 CLR)은 IsAssemblyType으로 뺀다.</summary>
    static readonly HashSet<string> UncomparableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "xml", "text", "ntext", "image", "sql_variant", "geography", "geometry", "hierarchyid", "timestamp", "rowversion", "json", "vector",
    };

    const int TablesPerQuery = 50;
    const int MaxListedColumns = 20;

    /// <summary>원본 DB 문맥에서 대상 테이블·열 읽기(읽기만). 외부 테이블(PolyBase)은 원격을 읽게 되므로 뺀다.</summary>
    public const string TargetsSql = """
        SELECT s.name, t.name, t.is_memory_optimized, c.name,
               CASE WHEN ty.is_assembly_type = 1 THEN ty.name ELSE TYPE_NAME(c.system_type_id) END,
               ty.is_assembly_type, c.is_computed
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        JOIN sys.columns AS c ON c.object_id = t.object_id
        JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        WHERE t.is_ms_shipped = 0 AND t.is_external = 0
        ORDER BY s.name, t.name, c.column_id;
        """;

    /// <summary>해시에 넣을 수 있는 열인지(계산 열은 기반 열로 대신 비교하고, 비결정 계산식의 거짓 불일치를 피한다).</summary>
    public static bool IsComparable(HashColumn c) => !c.IsComputed && !c.IsAssemblyType && !UncomparableTypes.Contains(c.TypeName);

    /// <summary>미리보기가 바꾼 테이블(netSummary·netChanges). 이 밖의 테이블이 해시 비교 대상이다.</summary>
    public static IReadOnlySet<string> PreviewTables(PreviewRecord preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in preview.Result.NetSummary) set.Add(r.Table);
        foreach (var n in preview.NetChanges) set.Add(n.Table);
        return set;
    }

    /// <summary>테이블 묶음 하나의 상태 SQL(UNION ALL). 미리보기 테이블과 비교 가능 열이 없는 테이블은 건수만 읽는다. 메모리 최적화 테이블은 명시 트랜잭션에서 읽으려고 SNAPSHOT 힌트.</summary>
    public static string StatesSql(IEnumerable<HashTarget> chunk, IReadOnlySet<string> previewTables) =>
        string.Join("\nUNION ALL\n", chunk.Select(t =>
        {
            List<string> cols = previewTables.Contains(t.Table) ? [] : t.Columns.Where(IsComparable).Select(c => SqlText.Quote(c.Name)).ToList();
            var hash = cols.Count == 0 ? "CAST(NULL AS int)" : $"CHECKSUM_AGG(BINARY_CHECKSUM({string.Join(", ", cols)}))";
            var hint = t.IsMemoryOptimized ? " WITH (SNAPSHOT)" : "";
            return $"SELECT {SqlText.NLiteral(t.Table)} AS t, COUNT_BIG(*) AS n, {hash} AS h FROM {t.QuotedName}{hint}";
        })) + ";";

    /// <summary>해시에서 뺀 열(비교 불가 형식) 한계 문구. 계산 열은 일반 문구로만 적는다. 없으면 빈 목록.</summary>
    public static IReadOnlyList<string> ExcludedColumnNotes(IReadOnlyList<HashTarget> targets, IReadOnlySet<string> previewTables)
    {
        var excluded = targets.Where(t => !previewTables.Contains(t.Table))
            .SelectMany(t => t.Columns.Where(c => !c.IsComputed && !IsComparable(c)).Select(c => $"{t.Table}.{c.Name}({c.TypeName})"))
            .ToList();
        var countOnly = targets.Where(t => !previewTables.Contains(t.Table) && !t.Columns.Any(IsComparable)).Select(t => t.Table).ToList();
        var notes = new List<string>();
        if (excluded.Count > 0)
            notes.Add($"KEYSET_HASH 해시에서 뺀 비교 불가 형식 열 {excluded.Count}개(이 열만 바뀌는 변경은 놓침): "
                      + string.Join(", ", excluded.Take(MaxListedColumns)) + (excluded.Count > MaxListedColumns ? " 외" : ""));
        if (countOnly.Count > 0)
            notes.Add($"KEYSET_HASH 비교 가능 열이 없어 행 수로만 비교한 테이블 {countOnly.Count}개: "
                      + string.Join(", ", countOnly.Take(MaxListedColumns)) + (countOnly.Count > MaxListedColumns ? " 외" : ""));
        return notes;
    }

    /// <summary>원본 카탈로그에서 대상 읽기(세션의 현재 DB = 원본).</summary>
    public static async Task<IReadOnlyList<HashTarget>> ReadTargetsAsync(SqlSession session, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new List<HashTarget>();
        await using var cmd = session.CreateCommand(TargetsSql, timeoutSeconds);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        string? schema = null, name = null;
        var memory = false;
        List<HashColumn>? cols = null;
        while (await r.ReadAsync(ct))
        {
            var s = r.GetString(0);
            var n = r.GetString(1);
            if (cols is null || !string.Equals(s, schema, StringComparison.Ordinal) || !string.Equals(n, name, StringComparison.Ordinal))
            {
                if (cols is not null) result.Add(new HashTarget(schema!, name!, memory, cols));
                (schema, name, memory, cols) = (s, n, r.GetBoolean(2), []);
            }
            cols.Add(new HashColumn(r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6)));
        }
        if (cols is not null) result.Add(new HashTarget(schema!, name!, memory, cols));
        return result;
    }

    /// <summary>"스키마.테이블" → 상태. 세션의 현재 DB(원본)에서 읽는다(원본 연결의 LOCK_TIMEOUT을 그대로 탄다).</summary>
    public static async Task<IReadOnlyDictionary<string, TableState>> ReadStatesAsync(
        SqlSession session, IReadOnlyList<HashTarget> targets, IReadOnlySet<string> previewTables, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targets);
        var states = new Dictionary<string, TableState>(StringComparer.Ordinal);
        var distinct = targets.GroupBy(t => t.Table, StringComparer.Ordinal).Select(g => g.First()).ToList();
        for (var i = 0; i < distinct.Count; i += TablesPerQuery)
        {
            await using var cmd = session.CreateCommand(StatesSql(distinct.Skip(i).Take(TablesPerQuery), previewTables), timeoutSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                states[reader.GetString(0)] = new TableState(reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
        }
        return states;
    }

    /// <summary>
    /// 전후 상태 비교. 미리보기 테이블(그리고 기대 변화가 있는 테이블): (실행 뒤 − 직전) 건수가 netSummary 기대 변화(inserted + multisetAdded − deleted − multisetRemoved)와 같아야 한다(KEYSET_TABLE_COUNT).
    /// 미리보기에 없던 테이블: 건수와 해시가 그대로여야 한다(OUTSIDE_TABLE_CHANGED, 테이블당 1건).
    /// </summary>
    public static (IReadOnlyList<TableCountChange> Counts, IReadOnlyList<ApplyMismatch> Mismatches) CompareStates(
        PreviewRecord preview, IReadOnlyDictionary<string, TableState> before, IReadOnlyDictionary<string, TableState> after, IReadOnlySet<string> previewTables)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var expected = preview.Result.NetSummary.ToDictionary(
            r => r.Table, r => r.Inserted + r.MultisetAdded - r.Deleted - r.MultisetRemoved, StringComparer.Ordinal);
        var counts = new List<TableCountChange>();
        var mismatches = new List<ApplyMismatch>();
        foreach (var table in after.Keys.Union(before.Keys, StringComparer.Ordinal).Union(expected.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(table);
            var a = after.GetValueOrDefault(table);
            var bn = b?.Count ?? 0;
            var an = a?.Count ?? 0;
            var e = expected.GetValueOrDefault(table);
            if (an - bn != 0 || e != 0) counts.Add(new TableCountChange { Table = table, Before = bn, After = an, ExpectedDelta = e });

            if (!previewTables.Contains(table) && e == 0)
            {
                if (b == a) continue; // 레코드 값 비교(건수·해시, null 포함)
                mismatches.Add(new ApplyMismatch
                {
                    Kind = MismatchKind.OutsideTableChanged, Table = table, Expected = Describe(b), Actual = Describe(a),
                    Message = an != bn
                        ? $"미리보기에 없던 {table}의 행 수가 실행 중 바뀜({bn} → {an})"
                        : $"미리보기에 없던 {table}의 값 해시가 실행 중 바뀜(행 수 {an} 그대로, 행 수가 그대로인 UPDATE)",
                });
                continue;
            }
            if (an - bn != e)
                mismatches.Add(new ApplyMismatch
                {
                    Kind = MismatchKind.KeysetTableCount, Table = table,
                    Expected = e.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Actual = (an - bn).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Message = $"{table} 행 수 변화가 미리보기와 다름(기대 {e}, 실제 {an - bn})",
                });
        }
        return (counts, mismatches);
    }

    static string Describe(TableState? s) =>
        s is null ? "missing" : $"count={s.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)},checksum={(s.Checksum is { } h ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")}";

    /// <summary>
    /// 미리보기 키 집합 after 대조: INSERTED·UPDATED는 키로 읽은 현재 행을 after와(비결정 열은 존재·형식만), DELETED는 행이 없어야 한다.
    /// IDENTITY가 있는 테이블의 INSERTED, 새 IDENTITY를 참조할 수 있는 테이블의 INSERTED, 키 없는 테이블은 건수로만 확인한다(CompareStates).
    /// </summary>
    public static async Task<IReadOnlyList<ApplyMismatch>> VerifyKeyRowsAsync(
        SqlSession session, PreviewRecord preview, IReadOnlyList<TableInfo> tables, BaselineContext baseline, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(baseline);
        var mismatches = new List<ApplyMismatch>();
        var byName = tables.GroupBy(t => t.DisplayName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rules = MatchRules.Create(preview, baseline);
        var ignored = new SortedSet<string>(StringComparer.Ordinal);
        var checkable = preview.NetChanges
            .Where(n => n.Key.Kind != KeyKind.ROW && byName.ContainsKey(n.Table))
            .Where(n => n.Change != NetChangeKind.Inserted || !rules.InsertCountsOnly(n.Table))
            .GroupBy(n => (n.Table, Columns: string.Join("\u001f", n.Key.Columns)))
            .OrderBy(g => g.Key.Table, StringComparer.Ordinal);
        foreach (var g in checkable)
        {
            ct.ThrowIfCancellationRequested();
            var list = g.ToList();
            var t = byName[g.Key.Table];
            var rows = await KeyedRows.ReadAsync(session, t, list[0].Key.Columns, list.Select(n => n.Key.Values).ToList(), lockRows: false, timeoutSeconds, ct);
            for (var i = 0; i < list.Count; i++)
            {
                var n = list[i];
                var actual = rows[i];
                if (n.Change == NetChangeKind.Deleted)
                {
                    if (actual is not null)
                        mismatches.Add(new ApplyMismatch { Kind = MismatchKind.RowUnexpected, Table = n.Table, Key = n.Key, Expected = "DELETED", Message = $"{n.Table} 행이 미리보기에서는 삭제됐지만 원본에 남아 있음" });
                    continue;
                }
                if (actual is null)
                {
                    mismatches.Add(new ApplyMismatch { Kind = MismatchKind.RowMissing, Table = n.Table, Key = n.Key, Expected = n.Change == NetChangeKind.Inserted ? "INSERTED" : "UPDATED", Message = $"{n.Table} 행이 미리보기에는 있지만 원본에 없음" });
                    continue;
                }
                rules.CompareValues(n.Table, n.Change, n.Key, n.After, actual, null, mismatches, ignored);
            }
        }
        return mismatches;
    }
}

/// <summary>원본 DB 옵션(sys.databases, 읽기만). [적용] 사전 경고에 쓴다.</summary>
internal sealed record OriginDbFlags(bool Trustworthy, bool Published, bool MergePublished, bool Subscribed, bool Cdc, bool Broker);

/// <summary>[적용] 원본 사전 경고(2026-11-13, 막지 않음). 문구는 결과 verification.limitations에 들어간다.</summary>
internal static class OriginWarnings
{
    /// <summary>원본 DB 문맥에서 읽는다(연결한 DB의 sys.databases 행은 늘 보인다).</summary>
    public const string Sql =
        "SELECT is_trustworthy_on, is_published, is_merge_published, is_subscribed, is_cdc_enabled, is_broker_enabled FROM sys.databases WHERE database_id = DB_ID();";

    public const string Trustworthy = "원본 DB가 TRUSTWORTHY ON: 트리거·프로시저의 EXECUTE AS가 DB 밖 권한을 쓸 수 있어 원본 실행이 미리보기와 다를 수 있음";
    public const string Replication = "원본 DB가 복제 게시·구독 중: 커밋한 변경이 다른 서버로 전파됨(병합 복제는 추적 행도 씀)";
    public const string Cdc = "원본 DB에 CDC가 켜짐: 커밋한 변경이 변경 테이블에 기록되어 소비자에게 전달됨";
    public const string Broker = "원본 DB에 Service Broker가 켜짐: 트리거·프로시저가 보낸 메시지는 커밋 뒤 다른 서비스로 전달될 수 있음(미리보기에서는 보이지 않음)";
    public const string Unknown = "원본 DB 옵션(TRUSTWORTHY·복제·CDC·Service Broker)을 확인 못 함(sys.databases를 읽지 못함)";

    /// <summary>플래그 → 문구. null이면 확인 못 함.</summary>
    public static IReadOnlyList<string> Describe(OriginDbFlags? f)
    {
        if (f is null) return [Unknown];
        var list = new List<string>();
        if (f.Trustworthy) list.Add(Trustworthy);
        if (f.Published || f.MergePublished || f.Subscribed) list.Add(Replication);
        if (f.Cdc) list.Add(Cdc);
        if (f.Broker) list.Add(Broker);
        return list;
    }
}

/// <summary>원본 스냅샷 생성 결과. 2026-11-13: 생성 실패는 모두 Failed(error)다. KEYSET_HASH는 실행 전 CheckSnapshotCapability로만 정한다(조용한 강등 없음).</summary>
internal enum SnapshotOutcome
{
    /// <summary>만들었다(SNAPSHOT_NETDIFF).</summary>
    Created,
    /// <summary>만들지 못했다(이름 충돌, 권한, 디스크, 그 밖 SQL 오류). KEYSET_HASH로 낮추지 않고 error로 끝낸다.</summary>
    Failed,
}

/// <summary>실행 전 검증 모드 결정 결과. CanSnapshot이 false면 KEYSET_HASH만 가능하고, 요청 동의(allowKeysetVerification)가 있어야 실행한다.</summary>
internal sealed record SnapshotCapability(bool CanSnapshot, string? Reason);

/// <summary>
/// 원본 스냅샷(§5.3 AW_Origin_pre). 남은 스냅샷을 지우고 원본 데이터 파일 논리 이름마다 Core BaselineSql.SnapshotPath로 경로를 지정해 만든다.
/// 스냅샷을 만들 수 없는 DB(A14)만 Unsupported로 돌려 KEYSET_HASH로 낮추고, 이름 충돌·권한 오류는 Failed로 구분한다. 원본 자체에는 DDL을 하지 않는다(스냅샷은 별도 DB).
/// </summary>
internal static class OriginSnapshot
{
    /// <summary>같은 이름의 다른 DB가 있음: DropSnapshot 가드(50001)와 CREATE DATABASE 이름 중복(1801).</summary>
    static readonly HashSet<int> NameConflictErrors = [50001, 1801];

    /// <summary>
    /// 권한 오류: 262(CREATE DATABASE 권한 없음), 229·230(개체·열 권한), 297·300(사용자·서버 권한), 916(DB 접근 불가), 15247(작업 권한 없음).
    /// 오류 번호 목록은 문서 기준이고 실측하지 않았다(테스트 라운드 확인).
    /// </summary>
    static readonly HashSet<int> PermissionErrors = [262, 229, 230, 297, 300, 916, 15247];

    /// <summary>
    /// 검증 모드 결정 SQL(master 문맥, 읽기만): CREATE ANY DATABASE 또는 master의 CREATE DATABASE 권한, 원본 FILESTREAM(FD)·메모리 최적화(FX) 파일 그룹 수.
    /// HAS_PERMS_BY_NAME·sys.filegroups는 공개 권한으로 읽힌다(원본 DB 접근 권한은 필요).
    /// </summary>
    public static string CapabilitySql(string originDb) => $"""
        SELECT CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE') = 1
                      OR HAS_PERMS_BY_NAME(N'master', N'DATABASE', N'CREATE DATABASE') = 1 THEN 1 ELSE 0 END,
               (SELECT COUNT(*) FROM {SqlText.Quote(originDb)}.sys.filegroups WHERE type = N'FD'),
               (SELECT COUNT(*) FROM {SqlText.Quote(originDb)}.sys.filegroups WHERE type = N'FX');
        """;

    /// <summary>권한·파일 그룹으로 검증 모드를 정한다(스냅샷 불가 이유는 결과·선행 조건 메시지에 싣는다).</summary>
    public static SnapshotCapability Decide(bool canCreateDatabase, int filestreamGroups, int memoryOptimizedGroups)
    {
        var why = new List<string>();
        if (!canCreateDatabase) why.Add("원본 로그인에 CREATE DATABASE 권한이 없음(데이터베이스 스냅샷 생성 불가)");
        if (filestreamGroups > 0) why.Add("원본에 FILESTREAM 파일 그룹이 있음(스냅샷에서 오프라인)");
        if (memoryOptimizedGroups > 0) why.Add("원본에 메모리 최적화 파일 그룹이 있음(스냅샷 미지원)");
        return new SnapshotCapability(why.Count == 0, why.Count == 0 ? null : string.Join(", ", why));
    }

    /// <summary>스냅샷 생성. 연결을 열 수 없으면 SandboxUnavailableException, 이름이 잘못된 서버 설정이면 InvalidOperationException.</summary>
    public static async Task<(SnapshotOutcome Outcome, string? Reason)> CreateAsync(string connectionString, string originDb, string snapshotDb, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        OriginNames.Validate(originDb, "원본");
        OriginNames.Validate(snapshotDb, "원본 스냅샷");
        if (string.Equals(originDb, snapshotDb, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("서버 설정 오류: 원본 스냅샷 이름이 원본과 같음(OriginSnapshotSuffix 확인)");

        await using var conn = await OriginConnections.OpenAsync(connectionString, "master", ct);
        try
        {
            // 남은 스냅샷 삭제(그 이름이 원본의 스냅샷이 아니면 50001로 멈춘다: 남의 DB를 지우지 않는다).
            await ExecAsync(conn, BaselineSql.DropSnapshot(snapshotDb, originDb), timeoutSeconds, ct);
            // 데이터 파일 논리 이름과 폴더: 원본 DB 문맥의 sys.database_files(공개 읽기). 스파스 파일은 각 데이터 파일과 같은 폴더에 둔다.
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var cmd = new SqlCommand($"SELECT name, physical_name FROM {SqlText.Quote(originDb)}.sys.database_files WHERE type = 0 ORDER BY file_id;", conn) { CommandTimeout = timeoutSeconds })
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) files[r.GetString(0)] = ServerPaths.Directory(r.GetString(1));
            }
            if (files.Count == 0) return (SnapshotOutcome.Failed, $"원본 {originDb}의 데이터 파일을 찾지 못함(원본 DB가 없거나 접근할 수 없음)");
            await ExecAsync(conn, BaselineSql.CreateSnapshot(snapshotDb, originDb, files.Keys, n => BaselineSql.SnapshotPath(snapshotDb, n, files[n])), timeoutSeconds, ct);
            return (SnapshotOutcome.Created, null);
        }
        catch (SqlException ex) when (conn.State != ConnectionState.Open)
        {
            throw new SandboxUnavailableException($"원본 스냅샷을 만드는 중 연결이 끊김(오류 {ex.Number})", ex);
        }
        catch (SqlException ex) when (NameConflictErrors.Contains(ex.Number))
        {
            return (SnapshotOutcome.Failed, $"원본 스냅샷 이름 {snapshotDb}을(를) 원본의 스냅샷이 아닌 DB가 쓰고 있어 지우거나 만들지 않음(오류 {ex.Number}). 서버 설정을 확인할 것");
        }
        catch (SqlException ex) when (PermissionErrors.Contains(ex.Number))
        {
            return (SnapshotOutcome.Failed, $"원본 스냅샷을 만들 권한이 없음(오류 {ex.Number}): {ex.Message}");
        }
        catch (SqlException ex)
        {
            // 예상 밖 오류도 KEYSET_HASH로 낮추지 않는다(2026-11-13): 검증 모드는 실행 전에 정했다.
            return (SnapshotOutcome.Failed, $"원본 스냅샷 생성 실패(오류 {ex.Number}): {ex.Message}");
        }
    }

    /// <summary>스냅샷 삭제(없으면 아무것도 안 함, 원본의 스냅샷이 아니면 지우지 않고 오류). finally에서 부른다.</summary>
    public static async Task DropAsync(string connectionString, string originDb, string snapshotDb, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        OriginNames.Validate(originDb, "원본");
        OriginNames.Validate(snapshotDb, "원본 스냅샷");
        await using var conn = await OriginConnections.OpenAsync(connectionString, "master", ct);
        await ExecAsync(conn, BaselineSql.DropSnapshot(snapshotDb, originDb), timeoutSeconds, ct);
    }

    static async Task ExecAsync(SqlConnection conn, string sql, int timeoutSeconds, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// DB 이름 검사(Core BaselineSql.ValidateName). 이름은 요청이 아니라 서버 설정(기준선 문맥, OriginSnapshotSuffix)에서 오므로
/// ArgumentException(API 400)이 아니라 InvalidOperationException(API 500)으로 바꿔 던진다.
/// </summary>
internal static class OriginNames
{
    public static void Validate(string name, string role)
    {
        try { BaselineSql.ValidateName(name, role); }
        catch (ArgumentException ex) { throw new InvalidOperationException("서버 설정 오류: " + ex.Message, ex); }
    }
}
