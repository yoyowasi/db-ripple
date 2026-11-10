using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Execution.Tests.Support;

namespace DbRipple.Execution.Tests;

/// <summary>DB 없이 픽스처로 C11을 회귀한다: raw.json을 Collect한 결과가 collect.json과 같고, 왕복 직렬화가 같다.</summary>
public class FixtureTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var id in CascadeLabTests.CaseIds) data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Collect_of_raw_fixture_equals_collect_fixture(string id)
    {
        var dir = Path.Combine(FixtureFiles.Root, id);
        Assert.True(File.Exists(Path.Combine(dir, "raw.json")), $"픽스처 없음: {dir} (DBR_WRITE_FIXTURES=1로 CascadeLabTests를 돌려 만든다)");
        var lab = LabCase.Get(id);
        var raw = FixtureFiles.Read<RawCapture>(Path.Combine(dir, "raw.json"));
        var analysis = FixtureFiles.Read<AnalysisResult>(Path.Combine(dir, "analysis.json"));
        var ctx = FixtureFiles.Read<BaselineContext>(FixtureFiles.BaselineFile(lab.Database));
        var collected = new ChangeCollector().Collect(raw, analysis, ctx);
        var expected = File.ReadAllText(Path.Combine(dir, "collect.json")).TrimEnd();
        Assert.Equal(expected, JsonSerializer.Serialize(collected, FixtureFiles.Options));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "raw.json")).TrimEnd(), JsonSerializer.Serialize(raw, FixtureFiles.Options));
    }
}
