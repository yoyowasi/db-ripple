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
    Func<Task<CatalogSchema>> schema = () => Task.FromResult(TestData.Schema());
    Func<string, ApplyRequest, Task<ApplyResult?>> apply = (_, _) => Task.FromResult<ApplyResult?>(null);
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

    sealed class SchemaProvider(ApiTests t) : ICatalogSchemaProvider
    {
        public Task<CatalogSchema> GetSchemaAsync(CancellationToken ct) => t.schema();
    }

    sealed class ApplySvc(ApiTests t) : IApplyService
    {
        public Task<ApplyResult?> ApplyAsync(string previewId, ApplyRequest request, CancellationToken ct) => t.apply(previewId, request);
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
        builder.Services.AddSingleton<ICatalogSchemaProvider>(new SchemaProvider(this));
        builder.Services.AddSingleton<IApplyService>(new ApplySvc(this));
        app = builder.Build();
        app.UseExceptionHandler();
        app.MapOpenApi();
        app.MapDbRippleApi();
        await app.StartAsync();
        client = app.GetTestClient().WithGuardHeader(); // POST는 요청 표지 헤더가 필요하다(CrossSiteRequestGuard)
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
    public async Task Analyze_without_ready_baseline_is_409_problem_json_with_reason()
    {
        context = () => throw new PreviewConflictException(ConflictReason.NoBaseline, "기준선 없음 connection=Password=SECRET");
        var res = await client.PostAsync("/api/analyze", Json("""{"sql":"DELETE FROM dbo.T;"}"""));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", text); // 예외 메시지는 응답에 넣지 않는다
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("NO_BASELINE", doc.RootElement.GetProperty("reason").GetString());
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
    public async Task Preview_conflicts_are_409_with_each_reason_code()
    {
        foreach (var (reason, wire) in new[]
        {
            (ConflictReason.Locked, "LOCKED"), (ConflictReason.Building, "BUILDING"), (ConflictReason.NoBaseline, "NO_BASELINE"),
            (ConflictReason.FingerprintMismatch, "FINGERPRINT_MISMATCH"), (ConflictReason.Dirty, "DIRTY"),
        })
        {
            pipeline = (_, _) => throw new PreviewConflictException(reason, "내부 메시지 SECRET");
            var res = await client.PostAsync("/api/previews", Json("""{"sql":"x"}"""));
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
            var text = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SECRET", text);
            using var doc = JsonDocument.Parse(text);
            Assert.Equal(409, doc.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(wire, doc.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task Exception_types_map_to_503_400_501_and_500_without_leaking_messages()
    {
        var cases = new (Exception Ex, HttpStatusCode Status)[]
        {
            (new SandboxUnavailableException("연결 끊김 Password=SECRET"), HttpStatusCode.ServiceUnavailable),
            (new ArgumentException("잘못된 인수 SECRET"), HttpStatusCode.BadRequest),
            (new NotImplementedException("SECRET"), HttpStatusCode.NotImplemented),
            (new InvalidOperationException("@@OPTIONS 점검 실패 SECRET"), HttpStatusCode.InternalServerError), // 인계 7: 예전 409
        };
        foreach (var (ex, status) in cases)
        {
            pipeline = (_, _) => throw ex;
            var res = await client.PostAsync("/api/previews", Json("""{"sql":"x"}"""));
            Assert.Equal(status, res.StatusCode);
            Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
            var text = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SECRET", text);
            using var doc = JsonDocument.Parse(text);
            Assert.Equal((int)status, doc.RootElement.GetProperty("status").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("reason", out _)); // reason은 409만
        }
    }

    [Fact]
    public async Task Sandbox_unavailable_from_apply_is_503_and_conflict_is_409_with_reason()
    {
        apply = (_, _) => throw new SandboxUnavailableException("끊김");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/previews/pv_a/apply", null)).StatusCode);
        apply = (_, _) => throw new PreviewConflictException(ConflictReason.Locked, "잠금");
        var res = await client.PostAsync("/api/previews/pv_a/apply", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = await BodyAsync(res);
        Assert.Equal("LOCKED", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Catalog_schema_200_returns_tables_columns_and_sql_types()
    {
        var res = await client.GetAsync("/api/catalog/schema");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = await BodyAsync(res);
        Assert.Equal("Origin", doc.RootElement.GetProperty("database").GetString());
        Assert.Equal("m-test", doc.RootElement.GetProperty("manifestVersion").GetString());
        var table = doc.RootElement.GetProperty("tables")[0];
        Assert.Equal("dbo.T", table.GetProperty("table").GetString());
        Assert.Equal("PK", table.GetProperty("keyKind").GetString());
        Assert.True(table.GetProperty("covered").GetBoolean());
        Assert.Equal(JsonValueKind.Null, table.GetProperty("excludedReason").ValueKind); // null 속성도 포함
        var id = table.GetProperty("columns")[0];
        Assert.Equal("IDENTITY", id.GetProperty("volatile").GetString());
        Assert.True(id.GetProperty("isIdentity").GetBoolean());
        Assert.Equal("decimal(19,4)", table.GetProperty("columns")[1].GetProperty("sqlType").GetString());
    }

    [Fact]
    public async Task Catalog_schema_without_baseline_is_409_with_reason()
    {
        schema = () => throw new PreviewConflictException(ConflictReason.Building, "구축 중");
        var res = await client.GetAsync("/api/catalog/schema");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = await BodyAsync(res);
        Assert.Equal("BUILDING", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Apply_returns_200_for_every_status_and_passes_the_request_body()
    {
        ApplyRequest? seen = null;
        foreach (var (status, wire) in new[]
        {
            (ApplyStatus.Committed, "committed"), (ApplyStatus.RolledBackMismatch, "rolled_back_mismatch"), (ApplyStatus.DriftDetected, "drift_detected"),
            (ApplyStatus.PreconditionFailed, "precondition_failed"), (ApplyStatus.Error, "error"),
        })
        {
            apply = (id, req) => { seen = req; return Task.FromResult<ApplyResult?>(TestData.ApplyResult(status, id)); };
            var res = await client.PostAsync("/api/previews/pv_a/apply", Json("""{"sqlHash":"sha256:abc"}"""));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("sha256:abc", seen!.SqlHash);
            using var doc = await BodyAsync(res);
            Assert.Equal("pv_a", doc.RootElement.GetProperty("previewId").GetString());
            Assert.Equal(wire, doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("SNAPSHOT_NETDIFF", doc.RootElement.GetProperty("verification").GetProperty("mode").GetString());
            Assert.Equal(5, doc.RootElement.GetProperty("timingsMs").GetProperty("commit").GetInt32());
            if (status == ApplyStatus.PreconditionFailed)
                Assert.Equal("SQL_HASH_MISMATCH", doc.RootElement.GetProperty("preconditionFailures")[0].GetString());
        }
    }

    [Fact]
    public async Task Apply_body_is_optional_empty_body_and_null_hash_are_allowed()
    {
        ApplyRequest? seen = null;
        apply = (id, req) => { seen = req; return Task.FromResult<ApplyResult?>(TestData.ApplyResult(ApplyStatus.Committed, id)); };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/previews/pv_a/apply", null)).StatusCode);
        Assert.NotNull(seen);
        Assert.Null(seen!.SqlHash);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/previews/pv_a/apply", Json("{}"))).StatusCode);
        Assert.Null(seen.SqlHash);
    }

    [Fact]
    public async Task Apply_404_when_the_preview_is_missing_or_expired()
    {
        apply = (_, _) => Task.FromResult<ApplyResult?>(null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/previews/none/apply", null)).StatusCode);
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
    public async Task Rows_net_table_filter_pages_on_server_and_validates()
    {
        var r = TestData.Record("pv_a", clock.GetUtcNow(), rowChanges: 5, nets: 5);
        await store.SaveAsync(r with { NetChanges = [.. r.NetChanges.Select((n, i) => i < 3 ? n : n with { Table = "Sales.Order" })] }, default);

        using (var doc = await BodyAsync(await client.GetAsync("/api/previews/pv_a/rows?view=net&table=dbo.T&offset=2&limit=1")))
        {
            Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
            var row = Assert.Single(doc.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal("dbo.T", row.GetProperty("table").GetString());
            Assert.Equal("3", row.GetProperty("key").GetProperty("values")[0].GetString());
        }
        using (var doc = await BodyAsync(await client.GetAsync("/api/previews/pv_a/rows?view=net&table=Sales.Order")))
            Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        using (var doc = await BodyAsync(await client.GetAsync("/api/previews/pv_a/rows?view=net&table=dbo.Nothing")))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32()); // 없는 테이블은 빈 페이지
            Assert.Equal(0, doc.RootElement.GetProperty("rows").GetArrayLength());
        }
        using (var doc = await BodyAsync(await client.GetAsync("/api/previews/pv_a/rows?view=net&table=")))
            Assert.Equal(5, doc.RootElement.GetProperty("total").GetInt32()); // 빈 값은 필터 없음

        foreach (var q in new[] { "view=net&table=T", "view=net&table=dbo.", "view=net&table=dbo." + new string('x', 129), "view=event&table=dbo.T", "table=dbo.T", "view=net&table=dbo.T&limit=1001" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/previews/pv_a/rows?" + q)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/previews/none/rows?view=net&table=dbo.T")).StatusCode);
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
        var text = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
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
        var catalog = paths.GetProperty("/api/catalog/schema").GetProperty("get").GetProperty("responses");
        foreach (var code in new[] { "200", "409" }) Assert.True(catalog.TryGetProperty(code, out _), "catalog " + code);
        Assert.False(catalog.TryGetProperty("501", out _));
        var applyResponses = paths.GetProperty("/api/previews/{id}/apply").GetProperty("post").GetProperty("responses");
        foreach (var code in new[] { "200", "404", "409", "503", "500" }) Assert.True(applyResponses.TryGetProperty(code, out _), "apply " + code);
        Assert.True(responses.TryGetProperty("503", out _));
        Assert.Contains("rolled_back_mismatch", text);
    }
}
