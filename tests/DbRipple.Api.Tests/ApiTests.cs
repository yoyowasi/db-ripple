using System.Net;
using System.Text;
using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Preview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbRipple.Api.Tests;

/// <summary>C5(§6.4): 가짜 서비스 + TestServer. 상태 코드, ProblemDetails 본문, 입력 검증, 행 페이지, 내보내기, 취소, OpenAPI.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dbrt_api_http_" + Guid.NewGuid().ToString("N")[..8]);
    readonly FakeClock clock = new(TestData.Start);
    WebApplication app = null!;
    HttpClient client = null!;
    PreviewStore store = null!;
    Func<string, PreviewOptions, Task<PreviewOutcome>> pipeline = (_, _) => throw new NotSupportedException();
    Func<Task<BaselineContext>> context = () => Task.FromResult(TestData.Baseline());
    int refreshStarts;

    sealed class Sandbox(ApiTests t) : ISandboxManager
    {
        public Task<SandboxStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new SandboxStatus
        {
            State = SandboxState.Ready, BaselineAt = TestData.Start, FingerprintMatch = true, Locked = false,
            Manifest = new ManifestSummary { CoveredTables = 71, ExcludedTables = 0, AuditNotLast = 0 },
        });
        public Task<BaselineContext> GetContextAsync(CancellationToken ct) => t.context();
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public void StartRefresh() => t.refreshStarts++;
        public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct) => throw new NotSupportedException();
        public void MarkDirty(string reason) { }
    }

    sealed class Pipeline(ApiTests t) : IPreviewPipeline
    {
        public Task<PreviewOutcome> RunAsync(string sql, PreviewOptions options, IEvaluationHook? hook, CancellationToken ct) => t.pipeline(sql, options);
    }

    sealed class Analyzer(Func<string, AnalysisResult> run) : IBatchAnalyzer
    {
        public AnalysisResult Analyze(string sql, BaselineContext baseline) => run(sql);
    }

    public async Task InitializeAsync()
    {
        store = new PreviewStore(dir, clock);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbRippleApi();
        builder.Services.AddSingleton<ISandboxManager>(new Sandbox(this));
        builder.Services.AddSingleton<IPreviewPipeline>(new Pipeline(this));
        builder.Services.AddSingleton<IBatchAnalyzer>(new Analyzer(sql => TestData.Accepted("W_X")));
        builder.Services.AddSingleton<IPreviewStore>(store);
        app = builder.Build();
        app.UseExceptionHandler();
        app.MapOpenApi();
        app.MapDbRippleApi();
        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await app.DisposeAsync();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    static async Task<JsonDocument> BodyAsync(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync());

    PreviewOutcome Outcome(string id = "pv_a") => new() { Analysis = TestData.Accepted(), Record = TestData.Record(id, clock.GetUtcNow()) };

    [Fact]
    public async Task Sandbox_status_and_refresh_202()
    {
        using (var doc = await BodyAsync(await client.GetAsync("/api/sandbox")))
        {
            Assert.Equal("ready", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal(71, doc.RootElement.GetProperty("manifest").GetProperty("coveredTables").GetInt32());
            Assert.False(doc.RootElement.GetProperty("locked").GetBoolean());
        }
        var res = await client.PostAsync("/api/sandbox/refresh", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("/api/sandbox", res.Headers.Location?.OriginalString);
        Assert.Equal(1, refreshStarts);
    }

    [Fact]
    public async Task Analyze_returns_statements_rejects_warnings_and_diagnostics()
    {
        var res = await client.PostAsync("/api/analyze", Json("""{"sql":"DELETE FROM dbo.T;"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = await BodyAsync(res);
        Assert.Equal(1, doc.RootElement.GetProperty("statements").GetArrayLength());
        Assert.Equal("warning", doc.RootElement.GetProperty("warnings")[0].GetProperty("severity").GetString());
        Assert.Equal("W_X", doc.RootElement.GetProperty("diagnostics")[0].GetProperty("ruleId").GetString());
    }

    [Fact]
    public async Task Analyze_without_ready_baseline_is_409_problem_json()
    {
        context = () => throw new InvalidOperationException("기준선 없음 connection=Password=SECRET");
        var res = await client.PostAsync("/api/analyze", Json("""{"sql":"DELETE FROM dbo.T;"}"""));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("SECRET", await res.Content.ReadAsStringAsync()); // 예외 메시지는 응답에 넣지 않는다
    }

    [Fact]
    public async Task Preview_200_returns_result_without_row_changes_or_sql()
    {
        pipeline = (_, _) => Task.FromResult(Outcome());
        var res = await client.PostAsync("/api/previews", Json("""{"sql":"DELETE FROM dbo.T;","options":{"imageCap":10}}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = await BodyAsync(res);
        Assert.Equal("pv_a", doc.RootElement.GetProperty("previewId").GetString());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("rowChanges", out _));
        Assert.False(doc.RootElement.TryGetProperty("sql", out _));
        Assert.Equal("f-1001", doc.RootElement.GetProperty("firings")[0].GetProperty("id").GetString());
        Assert.Equal("DIRECT", doc.RootElement.GetProperty("firings")[0].GetProperty("cause").GetProperty("kind").GetString());
        // 그래프 노드는 type 판별자로 직렬화된다
        Assert.Equal("statement", doc.RootElement.GetProperty("graph").GetProperty("nodes")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Preview_passes_options_and_uses_defaults_when_missing()
    {
        PreviewOptions? seen = null;
        pipeline = (_, o) => { seen = o; return Task.FromResult(Outcome()); };
        await client.PostAsync("/api/previews", Json("""{"sql":"x","options":{"imageCap":10}}"""));
        Assert.Equal(10, seen!.ImageCap);
        await client.PostAsync("/api/previews", Json("""{"sql":"x"}"""));
        Assert.Null(seen!.ImageCap);
    }

    [Fact]
    public async Task Preview_rejected_is_422_problem_with_rejects()
    {
        pipeline = (_, _) => Task.FromResult(new PreviewOutcome { Analysis = TestData.Rejected() });
        var res = await client.PostAsync("/api/previews", Json("""{"sql":"TRUNCATE TABLE t;"}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        using var doc = await BodyAsync(res);
        Assert.Equal(ProblemTypes.Rejected, doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        var reject = Assert.Single(doc.RootElement.GetProperty("rejects").EnumerateArray());
        Assert.Equal("REJECT_TRUNCATE", reject.GetProperty("ruleId").GetString());
        Assert.Equal(1, reject.GetProperty("statementIndex").GetInt32());
        Assert.Equal(1, reject.GetProperty("span").GetProperty("startLine").GetInt32());
        Assert.Null(await store.GetAsync("pv_a", default)); // 거부는 저장하지 않는다
    }

    [Fact]
    public async Task Preview_lock_conflict_and_fingerprint_mismatch_are_409()
    {
        pipeline = (_, _) => throw new InvalidOperationException("다른 미리보기가 실행 중입니다.");
        var res = await client.PostAsync("/api/previews", Json("""{"sql":"x"}"""));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = await BodyAsync(res);
        Assert.Equal(409, doc.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Not_implemented_endpoints_are_501_problem_json()
    {
        foreach (var (method, path) in new[] { ("GET", "/api/catalog/schema"), ("POST", "/api/previews/pv_a/apply") })
        {
            var res = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
            Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Component_not_implemented_exception_maps_to_501()
    {
        pipeline = (_, _) => throw new NotImplementedException("골격");
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.PostAsync("/api/previews", Json("""{"sql":"x"}"""))).StatusCode);
    }

    [Fact]
    public async Task Sql_validation_400_for_empty_missing_malformed_and_over_1_000_000_chars()
    {
        pipeline = (_, _) => Task.FromResult(Outcome());
        foreach (var path in new[] { "/api/previews", "/api/analyze" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, Json("""{"sql":""}"""))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, Json("""{"sql":"   \n"}"""))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, Json("{}"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, Json("{ not json"))).StatusCode);

            var atLimit = await client.PostAsync(path, Json(JsonSerializer.Serialize(new { sql = new string('a', DbRippleApi.MaxSqlLength) })));
            Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
            var tooLong = await client.PostAsync(path, Json(JsonSerializer.Serialize(new { sql = new string('a', DbRippleApi.MaxSqlLength + 1) })));
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            Assert.Equal("application/problem+json", tooLong.Content.Headers.ContentType?.MediaType);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/previews", Json("""{"sql":"x","options":{"imageCap":0}}"""))).StatusCode);
    }

    [Fact]
    public async Task Get_preview_404_when_missing_or_expired_and_200_when_stored()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/previews/pv_a")).StatusCode);
        await store.SaveAsync(TestData.Record("pv_a", clock.GetUtcNow()), default);
        var ok = await client.GetAsync("/api/previews/pv_a");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/previews/pv_a")).StatusCode);
    }

    [Fact]
    public async Task Rows_event_and_net_pages_filter_and_validate()
    {
        await store.SaveAsync(TestData.Record("pv_a", clock.GetUtcNow(), rowChanges: 5, nets: 5), default);

        using (var doc = await BodyAsync(await client.GetAsync("/api/previews/pv_a/rows")))
        {
            Assert.Equal("event", doc.RootElement.GetProperty("view").GetString());
            Assert.Equal(5, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(100, doc.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal("f-1001", doc.RootElement.GetProperty("rows")[0].GetProperty("firingId").GetString());
        }
        using (var doc = await BodyAsync(await client.GetAsync("/api/previews/pv_a/rows?view=event&firingId=f-1002&offset=1&limit=1")))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("rows").GetArrayLength());
            Assert.Equal(1, doc.RootElement.GetProperty("offset").GetInt32());
        }
        using (var doc = await BodyAsync(await client.GetAsync("/api/previews/pv_a/rows?view=net&firingId=f-1001")))
        {
            Assert.Equal("net", doc.RootElement.GetProperty("view").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("DELETED", doc.RootElement.GetProperty("rows")[0].GetProperty("change").GetString());
        }
        foreach (var q in new[] { "view=grid", "limit=0", "limit=1001", "offset=-1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/previews/pv_a/rows?" + q)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/previews/pv_a/rows?limit=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/previews/none/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/previews/none/rows?view=net")).StatusCode);
    }

    [Fact]
    public async Task Export_returns_preview_record_json_file()
    {
        await store.SaveAsync(TestData.Record("pv_a", clock.GetUtcNow()), default);
        var res = await client.GetAsync("/api/previews/pv_a/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("pv_a.json", res.Content.Headers.ContentDisposition?.FileName ?? res.Content.Headers.ContentDisposition?.FileNameStar);
        using var doc = await BodyAsync(res);
        Assert.Equal("DELETE FROM dbo.T;", doc.RootElement.GetProperty("sql").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("rowChanges").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/previews/none/export")).StatusCode);
    }

    [Fact]
    public async Task Cancel_discards_the_result_204_then_404()
    {
        await store.SaveAsync(TestData.Record("pv_a", clock.GetUtcNow()), default);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/previews/pv_a/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/previews/pv_a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/previews/pv_a/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Path_like_ids_never_reach_the_file_system()
    {
        var res = await client.GetAsync("/api/previews/..%2f..%2fsecrets");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task OpenApi_lists_all_routes_and_problem_responses()
    {
        var res = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = await BodyAsync(res);
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var p in new[]
        {
            "/api/sandbox", "/api/sandbox/refresh", "/api/catalog/schema", "/api/analyze", "/api/previews", "/api/previews/{id}",
            "/api/previews/{id}/rows", "/api/previews/{id}/apply", "/api/previews/{id}/cancel", "/api/previews/{id}/export",
        })
            Assert.True(paths.TryGetProperty(p, out _), p);
        var responses = paths.GetProperty("/api/previews").GetProperty("post").GetProperty("responses");
        foreach (var code in new[] { "200", "422", "409" }) Assert.True(responses.TryGetProperty(code, out _), code);
        Assert.True(paths.GetProperty("/api/sandbox/refresh").GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(paths.GetProperty("/api/catalog/schema").GetProperty("get").GetProperty("responses").TryGetProperty("501", out _));
    }
}
