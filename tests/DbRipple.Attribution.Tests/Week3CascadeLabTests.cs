using System.Text.Json;
using DbRipple.Contracts;
using Xunit.Abstractions;

namespace DbRipple.Attribution.Tests;

/// <summary>
/// CL09~CL20 M3 회귀(lab-1 목록 2026-11-11, answer-format v0.2). 1주차 감사 기록이 없는 사례라 손 카탈로그 대신 픽스처만 쓴다:
/// <list type="bullet">
/// <item>tests/fixtures/capture/&lt;사례&gt;/raw.json, collect.json: Execution.Tests Week3 CascadeLab 테스트가 DBR_WRITE_FIXTURES=1에서 쓴다.</item>
/// <item>baseline-cascadelab*.json: 실제 카탈로그·매니페스트·지문(Triggers 빈 배열).</item>
/// <item>triggers-cascadelab.json: Analysis.Tests가 쓰는 C6 정적 분석(사용자 트리거).</item>
/// </list>
/// 픽스처가 없으면 건너뛰었다고 출력만 한다(기존 FixtureRegressionTests와 같은 규약, xunit 2에는 실행 중 건너뛰기가 없다).
/// </summary>
public class Week3CascadeLabTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases()
    {
        var d = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Repo.Path_("cascadelab", "cases"), "CL*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(f).Split('-')[0];
            if (string.CompareOrdinal(id, "CL09") >= 0) d.Add(id);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture_edges_match_hand_answer(string id)
    {
        var caseFile = Directory.GetFiles(Repo.Path_("cascadelab", "cases"), id + "-*.json").Single();
        using var cdoc = JsonDocument.Parse(File.ReadAllText(caseFile));
        var c = cdoc.RootElement;
        var rt = c.TryGetProperty("settings", out var settings) && settings.TryGetProperty("RECURSIVE_TRIGGERS", out var r) && r.GetString() == "ON";

        var dir = Repo.Path_("tests", "fixtures", "capture", id);
        var basePath = Repo.Path_("tests", "fixtures", "capture", rt ? "baseline-cascadelab-rt.json" : "baseline-cascadelab.json");
        var trigPath = Repo.Path_("tests", "fixtures", "capture", "triggers-cascadelab.json");
        if (!File.Exists(Path.Combine(dir, "raw.json")) || !File.Exists(Path.Combine(dir, "collect.json")) || !File.Exists(basePath) || !File.Exists(trigPath))
        {
            output.WriteLine($"{id}: 픽스처 없음 — 건너뜀");
            return;
        }
        var raw = JsonSerializer.Deserialize<RawCapture>(File.ReadAllText(Path.Combine(dir, "raw.json")), ContractJson.Options)!;
        var collected = JsonSerializer.Deserialize<CollectResult>(File.ReadAllText(Path.Combine(dir, "collect.json")), ContractJson.Options)!;
        var baseline = JsonSerializer.Deserialize<BaselineContext>(File.ReadAllText(basePath), ContractJson.Options)!;
        var triggers = JsonSerializer.Deserialize<List<TriggerStaticInfo>>(File.ReadAllText(trigPath), ContractJson.Options)!;

        var res = new CausalResolver().Resolve(new ResolveInput
        {
            Statements = raw.Executions.Select(e => e.Result).ToList(), Collected = collected, Net = raw.NetChanges,
            Baseline = baseline with { Triggers = triggers },
        });
        GraphInvariants.Check(res);
        var expected = CascadeLabCases.ExpectedEdges(c);
        var report = CascadeLabM3Tests.Compare(id, expected, res);
        output.WriteLine("week3 fixture " + report.Summary + $" residuals={res.Model.Residuals.Count}");
        Assert.Equal(0, report.CertainOverconfidence);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
        Assert.Empty(report.ConfidenceMismatch);
        Assert.Empty(report.ObservedMismatch);
    }
}
