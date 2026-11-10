using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.TestSupport;

namespace DbRipple.Execution.Tests.Support;

/// <summary>
/// CascadeLab 픽스처(tests/fixtures/capture). C13·C11 회귀용. 실제 실행 결과를 ContractJson(들여쓰기)으로 쓴다.
/// <list type="bullet">
/// <item>&lt;사례&gt;/raw.json: RawCapture(C10)</item>
/// <item>&lt;사례&gt;/collect.json: CollectResult(C11, raw.json을 Collect한 결과)</item>
/// <item>&lt;사례&gt;/analysis.json: AnalysisResult(C6 대역, 문장만)</item>
/// <item>baseline-cascadelab.json, baseline-cascadelab-rt.json: BaselineContext(카탈로그·매니페스트, Triggers는 빈 배열 = C6 정적 분석 없음)</item>
/// </list>
/// 쓰기는 환경 변수 DBR_WRITE_FIXTURES=1일 때만 한다(평소 테스트 실행이 저장소 파일을 바꾸지 않게).
/// </summary>
public static class FixtureFiles
{
    public static string Root => Path.Combine(RepoPaths.Root, "tests", "fixtures", "capture");

    public static JsonSerializerOptions Options { get; } = new(ContractJson.Create()) { WriteIndented = true };

    public static bool Enabled => Environment.GetEnvironmentVariable("DBR_WRITE_FIXTURES") == "1";

    public static string BaselineFile(string database) => Path.Combine(Root, database == "CascadeLab_RT" ? "baseline-cascadelab-rt.json" : "baseline-cascadelab.json");

    public static async Task WriteAsync(string id, RawCapture raw, CollectResult collected, AnalysisResult analysis, BaselineContext ctx, string database)
    {
        if (!Enabled) return;
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        await Write(Path.Combine(dir, "raw.json"), raw);
        await Write(Path.Combine(dir, "collect.json"), collected);
        await Write(Path.Combine(dir, "analysis.json"), analysis);
        await Write(BaselineFile(database), ctx);
    }

    static Task Write<T>(string path, T value) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Options) + "\n", new System.Text.UTF8Encoding(false));

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)!;
}
