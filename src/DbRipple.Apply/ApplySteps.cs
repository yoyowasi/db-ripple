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

/// <summary>
/// KEYSET 검증(§5.3 원본 스냅샷을 만들 수 없을 때). BEGIN TRAN 직후와 실행 뒤 모든 사용자 테이블 COUNT_BIG(*)를 비교하고,
/// 미리보기 키 집합의 after 값을 비교한다. 행 수가 그대로인 UPDATE를 놓칠 수 있다는 한계를 Limitations에 적는다(호출자).
/// </summary>
internal static class KeysetVerifier
{
    /// <summary>KEYSET 한계 문구(결과 Limitations).</summary>
    public const string Limitation =
        "KEYSET 검증: 미리보기에 없는 테이블의 변경은 COUNT_BIG(*) 변화로만 감지하므로 행 수가 그대로인 UPDATE는 놓칠 수 있음. 새 IDENTITY 행과 그 자식 행은 건수로만 확인함";

    const int TablesPerQuery = 50;

    /// <summary>"스키마.테이블" → COUNT_BIG(*). 세션의 현재 DB(원본)에서 센다.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> CountAllAsync(SqlSession session, IReadOnlyList<TableInfo> tables, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tables);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var distinct = tables.GroupBy(t => t.DisplayName, StringComparer.Ordinal).Select(g => g.First()).OrderBy(t => t.DisplayName, StringComparer.Ordinal).ToList();
        for (var i = 0; i < distinct.Count; i += TablesPerQuery)
        {
            var chunk = distinct.Skip(i).Take(TablesPerQuery);
            var sql = string.Join("\nUNION ALL\n", chunk.Select(t => $"SELECT {SqlText.NLiteral(t.DisplayName)} AS t, COUNT_BIG(*) AS n FROM {t.QuotedName}")) + ";";
            await using var cmd = session.CreateCommand(sql, timeoutSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) counts[reader.GetString(0)] = reader.GetInt64(1);
        }
        return counts;
    }

    /// <summary>
    /// 건수 변화와 키 집합 after 대조. 건수: 모든 사용자 테이블에서 (실행 뒤 − 직후)가 미리보기 netSummary의 기대 변화(inserted + multisetAdded − deleted − multisetRemoved)와 같아야 한다.
    /// 키 집합: INSERTED·UPDATED는 키로 읽은 현재 행을 after와(비결정 열은 존재·형식만), DELETED는 행이 없어야 한다.
    /// IDENTITY가 있는 테이블의 INSERTED, 새 IDENTITY를 참조할 수 있는 테이블의 INSERTED, 키 없는 테이블은 건수로만 확인한다.
    /// </summary>
    public static async Task<(IReadOnlyList<TableCountChange> Counts, IReadOnlyList<ApplyMismatch> Mismatches)> VerifyAsync(
        SqlSession session, PreviewRecord preview, IReadOnlyDictionary<string, long> before, IReadOnlyList<TableInfo> tables,
        BaselineContext baseline, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(baseline);
        var after = await CountAllAsync(session, tables, timeoutSeconds, ct);
        var expected = preview.Result.NetSummary.ToDictionary(
            r => r.Table, r => r.Inserted + r.MultisetAdded - r.Deleted - r.MultisetRemoved, StringComparer.Ordinal);

        var counts = new List<TableCountChange>();
        var mismatches = new List<ApplyMismatch>();
        foreach (var table in after.Keys.Union(before.Keys, StringComparer.Ordinal).Union(expected.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(table);
            var a = after.GetValueOrDefault(table);
            var e = expected.GetValueOrDefault(table);
            if (a - b == 0 && e == 0) continue;
            counts.Add(new TableCountChange { Table = table, Before = b, After = a, ExpectedDelta = e });
            if (a - b != e)
                mismatches.Add(new ApplyMismatch
                {
                    Kind = MismatchKind.KeysetTableCount, Table = table,
                    Expected = e.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Actual = (a - b).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Message = $"{table} 행 수 변화가 미리보기와 다름(기대 {e}, 실제 {a - b})",
                });
        }

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
        return (counts, mismatches);
    }
}

/// <summary>원본 스냅샷 생성 결과.</summary>
internal enum SnapshotOutcome
{
    /// <summary>만들었다(SNAPSHOT_NETDIFF).</summary>
    Created,
    /// <summary>이 DB는 스냅샷을 만들 수 없다(A14: FILESTREAM·메모리 최적화·판 제한·디스크 등). 호출자가 KEYSET으로 낮춘다.</summary>
    Unsupported,
    /// <summary>설정·권한 문제(이름 충돌 50001·1801, 권한 오류, 원본 파일 없음). KEYSET으로 낮추지 않고 error로 끝낸다.</summary>
    Failed,
}

/// <summary>
/// 원본 스냅샷(§5.3 AW_Origin_pre). 남은 스냅샷을 지우고 원본 데이터 파일 논리 이름마다 Core BaselineSql.SnapshotPath로 경로를 지정해 만든다.
/// 스냅샷을 만들 수 없는 DB(A14)만 Unsupported로 돌려 KEYSET으로 낮추고, 이름 충돌·권한 오류는 Failed로 구분한다. 원본 자체에는 DDL을 하지 않는다(스냅샷은 별도 DB).
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
            var files = new List<string>();
            await using (var cmd = new SqlCommand("SELECT name FROM sys.master_files WHERE database_id = DB_ID(@db) AND type = 0 ORDER BY file_id;", conn) { CommandTimeout = timeoutSeconds })
            {
                cmd.Parameters.Add(new SqlParameter("@db", SqlDbType.NVarChar, 128) { Value = originDb });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) files.Add(r.GetString(0));
            }
            if (files.Count == 0) return (SnapshotOutcome.Failed, $"원본 {originDb}의 데이터 파일을 찾지 못함(원본 DB가 없거나 접근할 수 없음)");
            await ExecAsync(conn, BaselineSql.CreateSnapshot(snapshotDb, originDb, files), timeoutSeconds, ct);
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
            return (SnapshotOutcome.Unsupported, $"원본 스냅샷 생성 실패(오류 {ex.Number}): {ex.Message}");
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
