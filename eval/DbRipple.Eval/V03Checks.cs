using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>
/// 정답 형식 v0.3(EXEC) 필드의 비교. 제품 계약의 새 필드(statements[].returnValue, error.isToolFault, 그래프 노드 type procedureCall)는
/// 아직 제품 계약 타입에 없거나 바뀌는 중이라, 제품 결과를 JSON(ContractJson.Options)으로 직렬화해 문자열 이름으로 읽는다(계약 enum·새 멤버에 의존하지 않는다).
/// 차이는 지표가 아니라 보고용 문장 목록이다(differences.txt). 제품 JSON에 필드가 없으면 "필드 없음"으로 적는다(계약이 아직 안 들어온 것과 값 불일치를 구분).
/// </summary>
public static class V03Checks
{
    /// <summary>제품 결과(PreviewResult)의 문장별 returnValue·isToolFault를 손 정답과 비교한다. 손 정답에 해당 필드가 없는 문장은 보지 않는다.</summary>
    public static IReadOnlyList<string> CompareStatements(HandCase hand, PreviewResult? result)
    {
        var diffs = new List<string>();
        if (hand.ExpectedReturnValues.Count == 0 && hand.ExpectedToolFault.Count == 0) return diffs;
        if (result is null) return ["제품 결과 없음(returnValue·isToolFault 비교 못 함)"];
        var node = JsonSerializer.SerializeToNode(result, ContractJson.Options) as JsonObject;
        var statements = node?["statements"] as JsonArray ?? new JsonArray();
        JsonObject? Find(int index) =>
            statements.OfType<JsonObject>().FirstOrDefault(s => s["index"]?.GetValue<int>() == index);

        foreach (var (index, expected) in hand.ExpectedReturnValues)
        {
            var st = Find(index);
            if (st is null) { diffs.Add($"문장 {index}: 제품 결과에 없음(returnValue 비교 못 함)"); continue; }
            if (!st.ContainsKey("returnValue")) { if (expected is not null) diffs.Add($"문장 {index}: returnValue 필드 없음(기대 {expected})"); continue; }
            int? actual = st["returnValue"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
            if (actual != expected)
                diffs.Add($"문장 {index}: returnValue 기대 {Text(expected)} 실제 {Text(actual)}");
        }
        foreach (var (index, expected) in hand.ExpectedToolFault)
        {
            var st = Find(index);
            if (st?["error"] is not JsonObject err) { diffs.Add($"문장 {index}: error 없음(isToolFault 기대 {expected})"); continue; }
            if (err["isToolFault"] is not JsonValue f || !f.TryGetValue<bool>(out var actual)) { diffs.Add($"문장 {index}: isToolFault 필드 없음(기대 {expected})"); continue; }
            if (actual != expected) diffs.Add($"문장 {index}: isToolFault 기대 {expected} 실제 {actual}");
        }
        return diffs;
    }

    /// <summary>제품 분석 결과의 거부(ruleId, 문장 번호). 분석 결과가 없으면 빈 목록.</summary>
    public static IReadOnlyList<(string RuleId, int? StatementIndex)> ActualRejects(AnalysisResult? analysis) =>
        (analysis?.Rejects ?? []).Select(r => (r.RuleId, r.StatementIndex)).ToList();

    static string Text(int? v) => v is { } n ? n.ToString(CultureInfo.InvariantCulture) : "null";
}
