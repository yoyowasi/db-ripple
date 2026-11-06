using DbRipple.Core.Sql;

namespace DbRipple.Core.Capture;

/// <summary>
/// 형식 → 행 JSON 식 매핑. SP6 실측 뒤 바뀔 수 있으므로 변환 규칙은 이 파일에만 둔다.
/// </summary>
public static class AuditValueExpressions
{
    /// <summary>geography/geometry의 SRID 별도 속성 접미사(점은 FOR JSON PATH에서 중첩이 되므로 쓰지 않음).</summary>
    public const string SridSuffix = "@srid";

    static readonly HashSet<string> UnobservableTypes = new(StringComparer.OrdinalIgnoreCase) { "text", "ntext", "image" };

    /// <summary>트리거에서 읽을 수 없는 열(text/ntext/image) → 매니페스트 unobservableColumns.</summary>
    public static bool IsUnobservable(ColumnInfo c) => !c.IsUserClrType && UnobservableTypes.Contains(c.TypeName);

    /// <summary>사용자 CLR UDT는 변환이 미검증(TODO(SP6)).</summary>
    public static bool IsUnverified(ColumnInfo c) => c.IsUserClrType;

    /// <summary>열 하나의 SELECT 항목("식 AS [이름]"). rowAlias는 inserted/deleted 별칭.</summary>
    public static IEnumerable<string> SelectItems(ColumnInfo c, string rowAlias)
    {
        var col = rowAlias + "." + SqlText.Quote(c.Name);
        var name = SqlText.Quote(c.Name);
        if (c.IsUserClrType)
        {
            // TODO(SP6): 사용자 CLR UDT 변환 실측 필요
            yield return $"CONVERT(nvarchar(max), {col}) AS {name}";
            yield break;
        }
        switch (c.TypeName.ToLowerInvariant())
        {
            case "hierarchyid":
                yield return $"{col}.ToString() AS {name}";
                break;
            case "geography":
            case "geometry":
                yield return $"{col}.STAsText() AS {name}";
                yield return $"{col}.STSrid AS {SqlText.Quote(c.Name + SridSuffix)}";
                break;
            case "xml":
                yield return $"CONVERT(nvarchar(max), {col}) AS {name}";
                break;
            default:
                yield return $"{col} AS {name}"; // FOR JSON 기본 변환
                break;
        }
    }
}
