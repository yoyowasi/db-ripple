using System.Text.Json;

namespace DbRipple.Core.Capture;

/// <summary>
/// §6.1 값 문자열 규칙(행 JSON → 열 → 문자열 사전). 감사 이미지(C11)와 최종 차이(C12)가 같은 규칙을 쓰도록 한 곳에 둔다(위험 8-2, 2026-11-10 통합).
/// JSON 식 쪽 규칙(형식 → FOR JSON 식)은 <see cref="AuditValueExpressions"/>다.
/// </summary>
public static class AuditJson
{
    /// <summary>값 하나: 문자열 → 그대로, null → null, 숫자·true/false·객체·배열 → JSON 원문(숫자 원문 보존: 1.50은 "1.50").</summary>
    public static string? ToText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => v.GetString(),
        _ => v.GetRawText(),
    };

    /// <summary>JSON 객체 하나를 속성 → 값 문자열 사전으로(속성 이름은 대소문자 구분, 순서는 사전 순서와 무관).</summary>
    public static Dictionary<string, string?> ParseObject(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = ToText(p.Value);
        return d;
    }
}
