using DbRipple.Core.Sql;

namespace DbRipple.Core.Capture;

/// <summary>
/// 형식 → 행 JSON 식 매핑. 변환 규칙은 이 파일에만 둔다(2026-11-10 SP6 실측 반영).
/// </summary>
public static class AuditValueExpressions
{
    /// <summary>geography/geometry의 SRID 별도 속성 접미사(점은 FOR JSON PATH에서 중첩이 되므로 쓰지 않음).</summary>
    public const string SridSuffix = "@srid";

    /// <summary>sql_variant의 기저 형식(SQL_VARIANT_PROPERTY BaseType) 별도 속성 접미사.</summary>
    public const string TypeSuffix = "@type";

    // float 스타일 3 = 항상 17자리(무손실). SP6: FOR JSON 기본 출력은 float 16자리·real 8자리라 값이 바뀜
    const string FloatStyle3 = "CONVERT(varchar(30), {0}, 3)";

    static readonly HashSet<string> UnobservableTypes = new(StringComparer.OrdinalIgnoreCase) { "text", "ntext", "image" };

    /// <summary>트리거에서 읽을 수 없는 열(text/ntext/image) → 매니페스트 unobservableColumns.</summary>
    public static bool IsUnobservable(ColumnInfo c) => !c.IsUserClrType && UnobservableTypes.Contains(c.TypeName);

    /// <summary>사용자 CLR UDT는 변환이 미검증(TODO(SP6)).</summary>
    public static bool IsUnverified(ColumnInfo c) => c.IsUserClrType;

    /// <summary>열 하나가 rowJson에 만드는 (식, 속성 이름). 첫 항목의 속성 이름은 열 이름이다. rowAlias는 inserted/deleted 별칭.</summary>
    public static IEnumerable<(string Expression, string Property)> Items(ColumnInfo c, string rowAlias)
    {
        var col = rowAlias + "." + SqlText.Quote(c.Name);
        if (c.IsUserClrType)
        {
            // TODO(SP6): 사용자 CLR UDT 변환 실측 필요(시스템 CLR 형식의 CONVERT(nvarchar(max))는 성공 확인)
            yield return ($"CONVERT(nvarchar(max), {col})", c.Name);
            yield break;
        }
        switch (c.TypeName.ToLowerInvariant())
        {
            case "hierarchyid":
                yield return ($"{col}.ToString()", c.Name);
                break;
            case "geography":
            case "geometry":
                // SP6: STAsText()는 Z/M을 버림. ToString()은 Z/M·17자리를 남기고 STGeomFromText(text, srid)로 이진 동일
                yield return ($"{col}.ToString()", c.Name);
                yield return ($"{col}.STSrid", c.Name + SridSuffix);
                break;
            case "xml":
                yield return ($"CONVERT(nvarchar(max), {col})", c.Name);
                break;
            case "float":
                yield return (string.Format(FloatStyle3, col), c.Name);
                break;
            case "real":
                yield return (string.Format(FloatStyle3, $"CONVERT(float, {col})"), c.Name);
                break;
            case "sql_variant":
                // 기저 float/real만 스타일 3 문자열(나머지는 FOR JSON 기본). 기저 형식은 별도 속성
                yield return ($"CASE WHEN SQL_VARIANT_PROPERTY({col}, 'BaseType') IN ('float', 'real') THEN CONVERT(sql_variant, {string.Format(FloatStyle3, $"CONVERT(float, {col})")}) ELSE {col} END", c.Name);
                yield return ($"CONVERT(nvarchar(128), SQL_VARIANT_PROPERTY({col}, 'BaseType'))", c.Name + TypeSuffix);
                break;
            default:
                yield return (col, c.Name); // FOR JSON 기본 변환
                break;
        }
    }

    /// <summary>열 하나의 SELECT 항목("식 AS [이름]").</summary>
    public static IEnumerable<string> SelectItems(ColumnInfo c, string rowAlias) =>
        Items(c, rowAlias).Select(x => $"{x.Expression} AS {SqlText.Quote(x.Property)}");

    /// <summary>
    /// rowJson·keyJson 속성으로 쓸 수 없는 열 이름과 사유(SP6). 이름에 '.'이 있으면 FOR JSON PATH가 중첩 객체를 만들고,
    /// 다른 열의 생성 속성(@srid/@type)과 이름이 같으면 CREATE TRIGGER가 13601로 실패한다.
    /// </summary>
    public static IReadOnlyDictionary<string, string> JsonKeyConflicts(IEnumerable<ColumnInfo> observable)
    {
        var cols = observable.ToList();
        var generated = cols.Where(c => !c.Name.Contains('.'))
            .SelectMany(c => Items(c, "x").Skip(1).Select(i => i.Property))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in cols)
        {
            if (c.Name.Contains('.')) result[c.Name] = "JSON_KEY_DOT";
            else if (generated.Contains(c.Name)) result[c.Name] = "JSON_KEY_CONFLICT";
        }
        return result;
    }
}
