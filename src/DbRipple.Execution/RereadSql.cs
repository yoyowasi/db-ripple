using DbRipple.Core.Capture;
using DbRipple.Core.Sql;

namespace DbRipple.Execution;

/// <summary>
/// 재조회 SQL(D3 대조 1, §5.2). 입력 @keys는 감사 keyJson 객체의 JSON 배열이고, 결과는 (배열 위치, 현재 행 JSON 또는 NULL)이다.
/// 행 JSON은 감사 트리거와 같은 열·같은 식(Core AuditValueExpressions, FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)이라 감사 rowJson과 문자열로 비교할 수 있다.
/// </summary>
public static class RereadSql
{
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
