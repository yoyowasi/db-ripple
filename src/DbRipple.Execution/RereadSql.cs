using System.Globalization;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Sql;

namespace DbRipple.Execution;

/// <summary>재조회할 테이블 하나: 테이블, 재조회 SQL, 읽을 keyJson(감사 이미지 순서, 서수 중복 제거).</summary>
public sealed record RereadTable(TableInfo Table, string Sql, IReadOnlyList<string> Keys);

/// <summary>재조회 계획(<see cref="RereadSql.Plan"/>). Skipped = DELETE로만 나와 읽지 않은 키 수(키 있는 테이블만 셈).</summary>
public sealed record RereadPlan(IReadOnlyList<RereadTable> Tables, long Skipped)
{
    /// <summary>읽을 키 수.</summary>
    public long KeyCount => Tables.Sum(t => (long)t.Keys.Count);
}

/// <summary>
/// 재조회 SQL(D3 대조 1, §5.2). 입력 @keys는 감사 keyJson 객체의 JSON 배열이고, 결과는 (배열 위치, 현재 행 JSON 또는 NULL)이다.
/// 행 JSON은 감사 트리거와 같은 열·같은 식(Core AuditValueExpressions, FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)이라 감사 rowJson과 문자열로 비교할 수 있다.
/// </summary>
public static class RereadSql
{
    /// <summary>
    /// 재조회할 키(순수, 2026-11-18). 후보는 전과 같이 FULL 묶음 행 이미지의 keyJson(키 있는 테이블, 테이블마다 서수 중복 제거)이고,
    /// 그 가운데 같은 테이블의 INSERTED 이미지(FULL·KEYS 모두)에 접은 값으로 나온 키만 읽는다.
    /// 근거: C11 ApplyRereads는 재조회 행을 (테이블, keyJson 서수 일치) 묶음에 대고, 그 묶음에 after 있는 행 변경이 없으면 버린다.
    /// after 있는 행 변경의 묶음 키는 INSERTED 이미지의 keyJson이므로, DELETED에만 나온 키의 재조회 결과는 C11 출력을 바꾸지 못한다.
    /// 접기(후행 공백·대소문자·숫자 표기 무시)는 서수 일치보다 넓게 남기려는 여유다(빠뜨리지 않는 쪽). 서수로 먼저 찾고, 못 찾은 키가 있을 때만 접는다(비용).
    /// </summary>
    public static RereadPlan Plan(IReadOnlyList<AuditHeaderRow> headers, IReadOnlyList<AuditRowImageRow> images, IReadOnlyList<TableInfo> tables)
    {
        var byId = tables.ToDictionary(t => t.ObjectId);
        var tableOf = headers.ToDictionary(h => h.CaptureSeq, h => h.TableObjectId);
        var full = headers.Where(h => h.ImageMode == ImageMode.Full).Select(h => h.CaptureSeq).ToHashSet();
        var inserted = images.Where(i => i.ImageKind == ImageKind.Inserted && i.KeyJson is not null && tableOf.ContainsKey(i.CaptureSeq))
            .GroupBy(i => tableOf[i.CaptureSeq])
            .ToDictionary(g => g.Key, g => g.Select(i => i.KeyJson!).ToHashSet(StringComparer.Ordinal));
        var keysByTable = images.Where(i => i.KeyJson is not null && full.Contains(i.CaptureSeq))
            .GroupBy(i => tableOf[i.CaptureSeq])
            .Where(g => byId.ContainsKey(g.Key))
            .OrderBy(g => byId[g.Key].DisplayName, StringComparer.Ordinal);
        var result = new List<RereadTable>();
        long skipped = 0;
        foreach (var g in keysByTable)
        {
            var table = byId[g.Key];
            var sql = For(table);
            if (sql is null) continue;
            var keys = g.Select(i => i.KeyJson!).Distinct(StringComparer.Ordinal).ToList();
            var after = inserted.GetValueOrDefault(g.Key);
            HashSet<string>? folded = null;
            bool HasAfter(string k) => after is not null
                && (after.Contains(k) || (folded ??= after.Select(FoldKey).ToHashSet(StringComparer.Ordinal)).Contains(FoldKey(k)));
            var keep = keys.Where(HasAfter).ToList();
            skipped += keys.Count - keep.Count;
            if (keep.Count > 0) result.Add(new RereadTable(table, sql, keep));
        }
        return new RereadPlan(result, skipped);
    }

    /// <summary>
    /// keyJson 접기: 속성 이름(대소문자 무시) 순으로 이름=값. 값은 숫자면 decimal 정규형, 아니면 후행 공백을 떼고 대문자(Attribution ValueComparer 대소문자 무시 쪽과 같은 규칙).
    /// 같은 문자열이면 늘 같은 값이 나오는 함수라 서수 일치 키는 반드시 같이 접힌다. 읽지 못하는 JSON은 원문 그대로 둔다.
    /// </summary>
    public static string FoldKey(string keyJson)
    {
        Dictionary<string, string?> values;
        try { values = AuditJson.ParseObject(keyJson); }
        catch (System.Text.Json.JsonException) { return keyJson; }
        catch (InvalidOperationException) { return keyJson; } // 객체가 아닌 JSON
        return string.Join("\u001f", values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key.ToUpperInvariant() + "=" + FoldValue(kv.Value)));
    }

    static string FoldValue(string? v)
    {
        if (v is null) return "\u0000NULL";
        if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return "#" + d.ToString("G29", CultureInfo.InvariantCulture);
        return v.TrimEnd(' ').ToUpperInvariant();
    }

    const string JsonOptions = "FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES";

    // JSON_VALUE 문자열과 암시적 변환으로 정확히 비교되는 키 형식(형식 우선순위가 nvarchar보다 높거나 문자열 형식). 나머지는 키 JSON 전체 문자열 비교(전체 훑기).
    static readonly HashSet<string> Seekable = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney",
        "char", "varchar", "nchar", "nvarchar", "uniqueidentifier",
        "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time",
    };

    /// <summary>테이블의 재조회 SQL. 키 없는 테이블(ROW)이면 null.</summary>
    public static string? For(TableInfo t)
    {
        var key = TableKey.Choose(t);
        if (key.Kind == KeyKind.ROW) return null;
        var keyCols = key.Columns.Select(n => t.Columns.FirstOrDefault(c => c.Name == n) ?? new ColumnInfo(n, "")).ToList();
        var observable = t.Columns.Where(c => !AuditValueExpressions.IsUnobservable(c)).ToList();
        var conflicts = AuditValueExpressions.JsonKeyConflicts(observable);
        var rowCols = observable.Where(c => !conflicts.ContainsKey(c.Name)).ToList();
        var rowExpr = rowCols.Count == 0 ? "NULL" : JsonObject(rowCols, "r");

        string on;
        if (keyCols.All(c => !c.IsUserClrType && Seekable.Contains(c.TypeName)))
            on = string.Join(" AND ", keyCols.Select(c => $"r.{SqlText.Quote(c.Name)} = JSON_VALUE(k.[value], {SqlText.NLiteral(JsonPath(c.Name))})"));
        else
            on = $"{JsonObject(keyCols, "r")} = k.[value]";

        var present = $"r.{SqlText.Quote(keyCols[0].Name)}"; // 키 열은 NOT NULL(PK, NOT NULL 유일 키)이라 NULL이면 행이 없다
        return $"""
            SELECT k.[key], CASE WHEN {present} IS NULL THEN NULL ELSE {rowExpr} END
            FROM OPENJSON(@keys) AS k
            LEFT JOIN {t.QuotedName} AS r ON {on};
            """;
    }

    static string JsonObject(IEnumerable<ColumnInfo> cols, string alias) =>
        "(SELECT " + string.Join(", ", cols.SelectMany(c => AuditValueExpressions.SelectItems(c, alias))) + " " + JsonOptions + ")";

    /// <summary>JSON 경로 $."이름"(따옴표·역슬래시 이스케이프).</summary>
    static string JsonPath(string name) => "$.\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
