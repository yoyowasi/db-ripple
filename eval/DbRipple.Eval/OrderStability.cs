using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>미리보기 한 번의 M4 서명. Canonical·ObservedOnly는 SHA-256 앞 16자리(빈 그래프도 값이 있다).</summary>
public sealed record GraphSignature(string Status, string Canonical, string ObservedOnly, int ObservedOnlyEdges);

/// <summary>
/// M4 순서 안정성(§10.4). 같은 사례를 k회 반복해 형제 순서를 뺀 정규 그래프가 같은지, observed_only 부분이 얼마나 바뀌는지 본다.
/// <list type="bullet">
/// <item>정규 그래프 = 노드 모양 다중집합 + 간선 모양 다중집합. 노드 모양: 묶음 "F:테이블:OP@깊이 rows=n", 트리거 "T:이름@부모 깊이 observed=b", 잔차 "R:테이블 범주",
/// 오류 "E:번호", 문장 "S#번호 상태". 간선 모양: "문장|부모 모양|자식 모양|종류|이름|확신도|순서 보장". id·captureSeq·#k(형제 순서에 기대는 번호)는 넣지 않는다.</item>
/// <item>observed_only 서명 = observed_only 간선마다 (간선 모양, 자식 묶음의 문장 안 captureSeq 순번)의 정렬 목록. 관측 순서가 바뀌면 이 서명이 바뀐다.</item>
/// <item>"문서 순서 규칙 위반 수"는 지표로 쓰지 않는다(§10.4).</item>
/// </list>
/// </summary>
public static class OrderStability
{
    /// <summary>미리보기 결과의 서명. 결과가 없으면(거부·예외) 상태만 있는 서명.</summary>
    public static GraphSignature Signature(PreviewResult? result, string statusWhenMissing)
    {
        if (result is null) return new GraphSignature(statusWhenMissing, Hash(""), Hash(""), 0);
        var nodes = new List<string>();
        var firings = result.Firings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var n in result.Graph?.Nodes ?? [])
            nodes.Add(n switch
            {
                StatementNode s => $"S#{s.StatementIndex.ToString(CultureInfo.InvariantCulture)} {CaseScorer.ContractEnumText(s.Status)}",
                FiringNode f => $"F:{f.Table}:{CaseScorer.ContractEnumText(f.Operation)}@{f.Depth.ToString(CultureInfo.InvariantCulture)} rows={f.RowCount.ToString(CultureInfo.InvariantCulture)}",
                TriggerNode t => $"T:{t.Trigger}@{(t.ParentFiringId is { } p && firings.TryGetValue(p, out var pf) ? pf.Depth : 0).ToString(CultureInfo.InvariantCulture)} observed={(t.Observed ? "1" : "0")}",
                ResidualNode r => $"R:{r.Table} {CaseScorer.ContractEnumText(r.Category)}",
                ErrorNode e => $"E:{e.ErrorNumber.ToString(CultureInfo.InvariantCulture)}",
                GroupNode g => $"G members={g.Members.Count.ToString(CultureInfo.InvariantCulture)} rows={g.RowCount.ToString(CultureInfo.InvariantCulture)}",
                _ => "?",
            });
        var edges = GraphEdges.FromResult(result);
        string Shape(PredictedEdge e) => string.Join('|', e.StatementIndex.ToString(CultureInfo.InvariantCulture), e.ParentShape, e.ChildShape, e.Kind, e.Name ?? "", e.Confidence, e.OrderGuarantee);
        var canonical = string.Join('\n', nodes.Order(StringComparer.Ordinal)) + "\n--\n" + string.Join('\n', edges.Select(Shape).Order(StringComparer.Ordinal));
        var observedOnly = edges.Where(e => e.OrderGuarantee == "observed_only").Select(e => Shape(e) + "#" + e.ChildRank.ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal).ToList();
        return new GraphSignature(CaseScorer.ContractEnumText(result.Status), Hash(canonical), Hash(string.Join('\n', observedOnly)), observedOnly.Count);
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
}

/// <summary>
/// M4 누적기: (system, 사례)마다 반복 서명을 모아 m4.csv 행과 스위트 행을 만든다.
/// 상태가 "error"(파이프라인 예외로 결과 없음)인 반복은 빈 서명이라 안정성 판정에 넣으면 "모두 예외 = 안정"이 되므로 분모에서 빼고 error_runs로 따로 센다.
/// 거부("rejected")는 분석 단계에서 결정적으로 정해지므로 그대로 센다(2026-11-11 검토 반영).
/// </summary>
public sealed class M4Accumulator
{
    /// <summary>안정성 분모에서 빼는 상태(예외).</summary>
    public const string ErrorStatus = "error";

    readonly Dictionary<(string System, string Case), List<GraphSignature>> runs = [];

    /// <summary>반복 한 번을 더한다.</summary>
    public void Add(string system, string caseId, GraphSignature signature)
    {
        if (!runs.TryGetValue((system, caseId), out var list)) runs[(system, caseId)] = list = [];
        list.Add(signature);
    }

    static (int Distinct, int ModeCount) Spread(IEnumerable<string> values)
    {
        var g = values.GroupBy(v => v, StringComparer.Ordinal).Select(x => x.Count()).ToList();
        return (g.Count, g.Count == 0 ? 0 : g.Max());
    }

    static List<GraphSignature> Valid(List<GraphSignature> list) => list.Where(s => s.Status != ErrorStatus).ToList();

    /// <summary>m4.csv 행들(MetricsCsv.M4Columns 순서, system·사례 서수 순). 유효 반복(예외 제외)이 0이면 안정성 칸은 빈 칸이다.</summary>
    public IEnumerable<IReadOnlyList<string>> Rows(string runId)
    {
        foreach (var ((system, caseId), list) in runs.OrderBy(x => x.Key.System, StringComparer.Ordinal).ThenBy(x => x.Key.Case, StringComparer.Ordinal))
        {
            var valid = Valid(list);
            var k = valid.Count;
            var st = Spread(list.Select(s => s.Status));
            var cg = Spread(valid.Select(s => s.Canonical));
            var oo = Spread(valid.Select(s => s.ObservedOnly));
            var any = k > 0;
            yield return
            [
                runId, system, caseId, MetricsCsv.Int(list.Count), MetricsCsv.Int(list.Count - k), MetricsCsv.Int(k),
                MetricsCsv.Int(st.Distinct),
                any ? MetricsCsv.Int(cg.Distinct) : "", any ? MetricsCsv.Bool(cg.Distinct == 1) : "", MetricsCsv.Rate(cg.ModeCount, k),
                any ? MetricsCsv.Int(valid.Max(s => s.ObservedOnlyEdges)) : "", any ? MetricsCsv.Int(oo.Distinct) : "", MetricsCsv.Rate(k - oo.ModeCount, k),
                any ? valid.GroupBy(s => s.Canonical, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key : "",
            ];
        }
    }

    /// <summary>
    /// metrics.csv 행들: M4.canonical_stable_rate(사례 단위, 유효 반복이 있는 사례만 분모), M4.observed_only_variation_rate(유효 반복 합),
    /// M4.error_run_rate(예외 반복 ÷ 전체 반복).
    /// </summary>
    public IEnumerable<IReadOnlyList<string>> SuiteRows(string runId)
    {
        foreach (var g in runs.GroupBy(x => x.Key.System).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var cases = g.Count();
            var scored = g.Select(x => Valid(x.Value)).Where(v => v.Count > 0).ToList();
            var stable = scored.Count(v => Spread(v.Select(s => s.Canonical)).Distinct == 1);
            long valid = scored.Sum(v => v.Count), varied = scored.Sum(v => v.Count - Spread(v.Select(s => s.ObservedOnly)).ModeCount);
            long all = g.Sum(x => x.Value.Count), errors = all - valid;
            yield return [runId, g.Key, "M4.canonical_stable_rate", "case", MetricsCsv.Int(stable), MetricsCsv.Int(scored.Count), MetricsCsv.Rate(stable, scored.Count), MetricsCsv.Int(cases)];
            yield return [runId, g.Key, "M4.observed_only_variation_rate", "repeat", MetricsCsv.Int(varied), MetricsCsv.Int(valid), MetricsCsv.Rate(varied, valid), MetricsCsv.Int(cases)];
            yield return [runId, g.Key, "M4.error_run_rate", "repeat", MetricsCsv.Int(errors), MetricsCsv.Int(all), MetricsCsv.Rate(errors, all), MetricsCsv.Int(cases)];
        }
    }
}
