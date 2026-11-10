using System.Text.Json;
using DbRipple.Contracts;
using Xunit.Abstractions;

namespace DbRipple.Attribution.Tests;

/// <summary>
/// C10·C11 픽스처(tests/fixtures/capture/&lt;사례&gt;/raw.json, collect.json, ContractJson) 회귀. 픽스처가 없으면 그 사례는 건너뛰었다고 출력만 한다
/// (xunit 2에는 실행 중 건너뛰기가 없다). 카탈로그는 픽스처에 없어서 손 카탈로그를 쓴다.
/// </summary>
public class FixtureRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(CascadeLabM3Tests.Cases), MemberType = typeof(CascadeLabM3Tests))]
    public void Fixture_edges_match_hand_answer(string id)
    {
        var dir = Repo.Path_("tests", "fixtures", "capture", id);
        var rawPath = Path.Combine(dir, "raw.json");
        var collectPath = Path.Combine(dir, "collect.json");
        if (!File.Exists(rawPath) || !File.Exists(collectPath))
        {
            output.WriteLine($"{id}: 픽스처 없음({dir}) — 건너뜀");
            return;
        }

        var raw = JsonSerializer.Deserialize<RawCapture>(File.ReadAllText(rawPath), ContractJson.Options)!;
        var collected = JsonSerializer.Deserialize<CollectResult>(File.ReadAllText(collectPath), ContractJson.Options)!;
        var data = CascadeLabCases.Load(id);
        var input = data.Input with { Statements = raw.Executions.Select(e => e.Result).ToList(), Collected = collected, Net = raw.NetChanges };
        var r = new CausalResolver().Resolve(input);
        GraphInvariants.Check(r);
        var report = CascadeLabM3Tests.Compare(id, data.Edges, r);
        output.WriteLine("fixture " + report.Summary);
        Assert.Equal(0, report.CertainOverconfidence);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
        Assert.Empty(report.ConfidenceMismatch);
    }

    /// <summary>
    /// 픽스처 기준선(baseline-cascadelab*.json: 실DB 카탈로그·매니페스트·지문)으로 한 회귀. 픽스처의 Triggers는 비어 있으므로(C6 미실행)
    /// 사용자 트리거 정적 분석만 손 목록으로 채운다. 감사 트리거는 이름(dbr_audit_)으로 걸러져야 한다.
    /// </summary>
    [Theory]
    [MemberData(nameof(CascadeLabM3Tests.Cases), MemberType = typeof(CascadeLabM3Tests))]
    public void Fixture_with_fixture_baseline_matches_hand_answer(string id)
    {
        var dir = Repo.Path_("tests", "fixtures", "capture", id);
        var data = CascadeLabCases.Load(id);
        var rt = data.Input.Baseline.Fingerprint.IsRecursiveTriggersOn;
        var basePath = Repo.Path_("tests", "fixtures", "capture", rt ? "baseline-cascadelab-rt.json" : "baseline-cascadelab.json");
        if (!File.Exists(Path.Combine(dir, "collect.json")) || !File.Exists(basePath))
        {
            output.WriteLine($"{id}: 픽스처 또는 기준선 없음 — 건너뜀");
            return;
        }
        var raw = JsonSerializer.Deserialize<RawCapture>(File.ReadAllText(Path.Combine(dir, "raw.json")), ContractJson.Options)!;
        var collected = JsonSerializer.Deserialize<CollectResult>(File.ReadAllText(Path.Combine(dir, "collect.json")), ContractJson.Options)!;
        var baseline = JsonSerializer.Deserialize<BaselineContext>(File.ReadAllText(basePath), ContractJson.Options)!;
        Assert.Equal(rt, baseline.Fingerprint.IsRecursiveTriggersOn);
        var userTriggers = CatalogBuilder.CascadeLab().Build().Triggers.Where(t => !t.IsAuditTrigger).ToList();
        var input = new ResolveInput
        {
            Statements = raw.Executions.Select(e => e.Result).ToList(), Collected = collected, Net = raw.NetChanges,
            Baseline = baseline with { Triggers = userTriggers },
        };
        var r = new CausalResolver().Resolve(input);
        GraphInvariants.Check(r);
        var report = CascadeLabM3Tests.Compare(id, data.Edges, r);
        output.WriteLine("fixture+baseline " + report.Summary + $" residuals={r.Model.Residuals.Count}");
        Assert.Equal(0, report.CertainOverconfidence);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
        Assert.Empty(report.ConfidenceMismatch);
        Assert.Empty(report.ObservedMismatch);
        Assert.Empty(r.Model.Residuals);
        Assert.All(r.Model.NetChanges, n => Assert.NotEmpty(n.ExplainedBy!.FiringIds));
    }

    /// <summary>
    /// C6 정적 분석이 없는(Triggers 빈) 픽스처 기준선 그대로면 사용자 트리거는 쓰기 대상 모름(Q6)이라 트리거 간선은 ambiguous로 낮아진다.
    /// certain 과신은 여전히 0이어야 한다(키는 같고 확신도만 낮아짐).
    /// </summary>
    [Theory]
    [InlineData("CL06")]
    [InlineData("CL07")]
    [InlineData("CL08b")]
    public void Fixture_baseline_without_trigger_analysis_degrades_to_ambiguous(string id)
    {
        var dir = Repo.Path_("tests", "fixtures", "capture", id);
        var data = CascadeLabCases.Load(id);
        var rt = data.Input.Baseline.Fingerprint.IsRecursiveTriggersOn;
        var basePath = Repo.Path_("tests", "fixtures", "capture", rt ? "baseline-cascadelab-rt.json" : "baseline-cascadelab.json");
        if (!File.Exists(Path.Combine(dir, "collect.json")) || !File.Exists(basePath)) return;
        var raw = JsonSerializer.Deserialize<RawCapture>(File.ReadAllText(Path.Combine(dir, "raw.json")), ContractJson.Options)!;
        var collected = JsonSerializer.Deserialize<CollectResult>(File.ReadAllText(Path.Combine(dir, "collect.json")), ContractJson.Options)!;
        var baseline = JsonSerializer.Deserialize<BaselineContext>(File.ReadAllText(basePath), ContractJson.Options)!;
        var r = new CausalResolver().Resolve(new ResolveInput
        {
            Statements = raw.Executions.Select(e => e.Result).ToList(), Collected = collected, Net = raw.NetChanges, Baseline = baseline,
        });
        GraphInvariants.Check(r);
        var report = CascadeLabM3Tests.Compare(id, data.Edges, r);
        output.WriteLine("fixture+baseline(no C6) " + report.Summary);
        Assert.Equal(0, report.CertainOverconfidence);
        Assert.Empty(report.Missing);
        Assert.All(r.Graph.Edges.Where(e => e.Kind == EdgeKind.Writes), w => Assert.Equal(Confidence.Ambiguous, w.Confidence));
    }
}

/// <summary>대조기 자체 검증: 정답을 일부러 틀리게 바꾸면 과신·누락을 잡는다.</summary>
public class CompareSelfTests
{
    [Fact]
    public void Comparer_counts_certain_overconfidence_and_missing()
    {
        var data = CascadeLabCases.Load("CL01");
        var r = new CausalResolver().Resolve(data.Input);
        var altered = data.Edges.Select(e => e.Kind == "FK" && e.Name == "FK_cl01_C_B" ? e with { Confidence = "inferred" } : e)
            .Append(new ExpectedEdge(1, "S", "F:cl01.Z:DELETE", "DIRECT", null, "certain", null)).ToList();
        var report = CascadeLabM3Tests.Compare("CL01-altered", altered, r);
        Assert.Equal(1, report.CertainOverconfidence);
        Assert.Single(report.Missing);
        Assert.Single(report.ConfidenceMismatch);
    }

    [Fact]
    public void Comparer_detects_wrong_trigger_name_and_unobserved_flag()
    {
        var data = CascadeLabCases.Load("CL08b");
        var r = new CausalResolver().Resolve(data.Input);
        var altered = data.Edges.Select(e => e.Observed == false ? e with { Observed = true } : e).ToList();
        Assert.Single(CascadeLabM3Tests.Compare("CL08b-altered", altered, r).ObservedMismatch);
    }
}

/// <summary>사례별 M3 요약 출력(보고용).</summary>
public class M3SummaryTests(ITestOutputHelper output)
{
    [Fact]
    public void Print_summary()
    {
        int exp = 0, keyOk = 0, over = 0;
        foreach (var id in CascadeLabCases.Ids)
        {
            var data = CascadeLabCases.Load(id);
            var rep = CascadeLabM3Tests.Compare(id, data.Edges, new CausalResolver().Resolve(data.Input));
            output.WriteLine(rep.Summary);
            exp += data.Edges.Count;
            keyOk += data.Edges.Count - rep.Missing.Count - rep.ConfidenceMismatch.Count;
            over += rep.CertainOverconfidence;
        }
        output.WriteLine($"TOTAL expected={exp} keyAndConfidenceMatch={keyOk} certainOverconfidence={over}");
        Assert.Equal(exp, keyOk);
        Assert.Equal(0, over);
    }
}
