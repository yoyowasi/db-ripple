using System.Text.Json;
using DbRipple.Contracts;
using Xunit.Abstractions;

namespace DbRipple.Attribution.Tests;

/// <summary>
/// CascadeLab CL01~CL08 손 정답 대조(§10.4 M3): 간선 키(문장, 부모, 자식, 종류, 이름)와 확신도가 같고, certain 과신 오류가 0이다.
/// 입력은 1주차 실DB 감사 기록 + 손 카탈로그로 합성한다(C10·C11 픽스처는 FixtureRegressionTests).
/// </summary>
public class CascadeLabM3Tests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases() => new(CascadeLabCases.Ids);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Edges_match_hand_answer(string id)
    {
        var data = CascadeLabCases.Load(id);
        var r = new CausalResolver().Resolve(data.Input);
        var report = Compare(id, data.Edges, r);
        output.WriteLine(report.Summary);
        Assert.Equal(0, report.CertainOverconfidence);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
        Assert.Empty(report.ConfidenceMismatch);
        Assert.Empty(report.ObservedMismatch);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Firing_cause_kinds_match_hand_answer(string id)
    {
        var data = CascadeLabCases.Load(id);
        var r = new CausalResolver().Resolve(data.Input);
        var labels = FiringLabels(r);
        var actual = r.Model.Firings.Select(f => (labels[f.Id], Json(f.Cause!.Kind))).OrderBy(x => x).ToList();
        var expected = data.Firings.Select(f => (M3.NormalizeKey($"1|{f.Node}|x|k|", data.Firings.Select(x => $"1|{x.Node}|x|k|")).Split('|')[1], f.CauseKind)).OrderBy(x => x).ToList();
        Assert.Equal(expected, actual);
        Assert.All(r.Model.Firings, f => Assert.NotNull(f.OrderGuarantee));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Net_changes_are_all_explained_without_residuals(string id)
    {
        var data = CascadeLabCases.Load(id);
        var r = new CausalResolver().Resolve(data.Input);
        Assert.Empty(r.Model.Residuals);
        Assert.All(r.Model.NetChanges, n => { Assert.NotNull(n.ExplainedBy); Assert.NotEmpty(n.ExplainedBy!.FiringIds); Assert.Null(n.ExplainedBy.ResidualId); });
    }

    internal sealed record CompareReport(string Summary, int CertainOverconfidence, List<string> Missing, List<string> Extra, List<string> ConfidenceMismatch, List<string> ObservedMismatch);

    internal static CompareReport Compare(string id, IReadOnlyList<ExpectedEdge> expectedEdges, Resolution r)
    {
        var expKeys = expectedEdges.Select(e => e.Key).ToList();
        var expected = expectedEdges.ToDictionary(e => M3.NormalizeKey(e.Key, expKeys));
        var actualRaw = M3.Keys(r);
        var actKeys = actualRaw.Select(a => a.Key).ToList();
        var actual = actualRaw.GroupBy(a => M3.NormalizeKey(a.Key, actKeys)).ToDictionary(g => g.Key, g => g.First());

        var missing = expected.Keys.Except(actual.Keys).ToList();
        var extra = actual.Keys.Except(expected.Keys).ToList();
        var matched = expected.Keys.Intersect(actual.Keys).ToList();
        var confMismatch = matched.Where(k => expected[k].Confidence != actual[k].Confidence)
            .Select(k => $"{k}: exp={expected[k].Confidence} act={actual[k].Confidence}").ToList();
        var obsMismatch = matched.Where(k => expected[k].Observed is { } o && actual[k].Observed != o)
            .Select(k => $"{k}: exp observed={expected[k].Observed} act={actual[k].Observed}").ToList();
        // 과신 오류: 제품이 certain으로 낸 간선이 정답에 없거나 정답 확신도가 certain이 아님(§7.3).
        var over = actual.Where(a => a.Value.Confidence == "certain" && (!expected.TryGetValue(a.Key, out var e) || e.Confidence != "certain")).Count();
        var exact = matched.Count - confMismatch.Count;
        var summary = $"{id}: expected={expected.Count} actual={actual.Count} keyMatch={matched.Count} keyAndConfidenceMatch={exact} missing={missing.Count} extra={extra.Count} certainOverconfidence={over}"
            + string.Concat(missing.Select(m => "\n  missing " + m)) + string.Concat(extra.Select(m => "\n  extra " + m))
            + string.Concat(confMismatch.Select(m => "\n  confidence " + m)) + string.Concat(obsMismatch.Select(m => "\n  observed " + m));
        return new CompareReport(summary, over, missing, extra, confMismatch, obsMismatch);
    }

    internal static Dictionary<string, string> FiringLabels(Resolution r)
    {
        var labels = new Dictionary<string, string>();
        foreach (var grp in r.Model.Firings.GroupBy(f => (f.StatementIndex, $"F:{f.Table}:{f.Operation.ToString().ToUpperInvariant()}")))
        {
            var list = grp.OrderBy(f => f.Depth).ThenBy(f => f.CaptureSeq).ToList();
            for (var i = 0; i < list.Count; i++) labels[list[i].Id] = list.Count == 1 ? grp.Key.Item2 : $"{grp.Key.Item2}#{i + 1}";
        }
        return labels;
    }

    static string Json<T>(T v) => JsonSerializer.Serialize(v, ContractJson.Options).Trim('"');
}
