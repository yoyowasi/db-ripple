using System.Net;
using System.Text;
using System.Text.Json;
using DbRipple.Api.Middleware;
using DbRipple.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbRipple.Api.Tests;

/// <summary>
/// 교차 사이트 요청(CSRF) 방어(<see cref="CrossSiteRequestGuard"/>): 헤더 없음 403, 헤더 있음 정상, 다른 Origin 403, 허용 Origin 정상,
/// Sec-Fetch-Site cross-site 403, GET은 헤더 없이도 정상, 403 본문이 받은 값을 되돌려 쓰지 않음, 설정으로 허용 출처 교체, OpenAPI 403.
/// 403이면 서비스가 호출되지 않아야 한다(부작용 없음).
/// </summary>
public sealed class CrossSiteRequestGuardTests
{
    sealed class Probe
    {
        public int RefreshStarts;
        public int Applies;
    }

    sealed class Sandbox(Probe probe) : ISandboxManager
    {
        public Task<SandboxStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new SandboxStatus
        {
            State = SandboxState.Ready, BaselineAt = TestData.Start, FingerprintMatch = true, Locked = false,
            Manifest = new ManifestSummary { CoveredTables = 71, ExcludedTables = 0, AuditNotLast = 0 },
        });
        public Task<BaselineContext> GetContextAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public void StartRefresh() => probe.RefreshStarts++;
        public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct) => throw new NotSupportedException();
        public void MarkDirty(string reason) { }
    }

    sealed class ApplySvc(Probe probe) : IApplyService
    {
        public Task<ApplyResult?> ApplyAsync(string previewId, ApplyRequest request, CancellationToken ct)
        {
            probe.Applies++;
            return Task.FromResult<ApplyResult?>(TestData.ApplyResult(ApplyStatus.Committed, previewId));
        }
    }

    /// <summary>가짜 서비스로 TestServer를 띄운다. 돌려주는 클라이언트는 헤더가 없는 원시 클라이언트다(브라우저 밖 도구가 아니라 방어 규칙을 직접 시험).</summary>
    static async Task<(WebApplication App, HttpClient Client, Probe Probe)> StartAsync(Action<ConfigurationManager>? configure = null)
    {
        var probe = new Probe();
        // 개발 모드(Vite 5273 출처 허용)의 기존 동작을 시험한다. 실행 파일 모드는 ExecutableSecurityTests(N4).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        configure?.Invoke(builder.Configuration);
        builder.Services.AddDbRippleApi();
        builder.Services.AddSingleton<ISandboxManager>(new Sandbox(probe));
        builder.Services.AddSingleton<IApplyService>(new ApplySvc(probe));
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapOpenApi();
        app.MapDbRippleApi();
        await app.StartAsync();
        return (app, app.GetTestClient(), probe);
    }

    static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return client.SendAsync(request);
    }

    static (string, string) Guard => (CrossSiteRequestGuard.HeaderName, CrossSiteRequestGuard.HeaderValue);

    static async Task AssertForbiddenProblemAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(403, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Post_without_the_header_is_403_problem_and_has_no_side_effect()
    {
        var (app, client, probe) = await StartAsync();
        await using var server = app;

        // 교차 사이트 no-cors POST와 같은 모양: 본문 없음 또는 text/plain 본문, 표지 헤더 없음
        await AssertForbiddenProblemAsync(await client.PostAsync("/api/sandbox/refresh", null));
        await AssertForbiddenProblemAsync(await client.PostAsync("/api/previews/pv_a/apply", null));
        await AssertForbiddenProblemAsync(await client.PostAsync("/api/previews/pv_a/apply", new StringContent("{}", Encoding.UTF8, "text/plain")));
        await AssertForbiddenProblemAsync(await client.PostAsync("/api/previews/pv_a/cancel", null));
        await AssertForbiddenProblemAsync(await client.PostAsync("/api/analyze", new StringContent("""{"sql":"x"}""", Encoding.UTF8, "application/json")));
        await AssertForbiddenProblemAsync(await client.PostAsync("/api/previews", new StringContent("""{"sql":"x"}""", Encoding.UTF8, "application/json")));
        Assert.Equal(0, probe.RefreshStarts);
        Assert.Equal(0, probe.Applies);
    }

    [Fact]
    public async Task Put_patch_and_delete_under_api_are_guarded_too()
    {
        var (app, client, _) = await StartAsync();
        await using var server = app;
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            await AssertForbiddenProblemAsync(await SendAsync(client, method, "/api/sandbox"));
    }

    [Fact]
    public async Task Wrong_header_value_is_403()
    {
        var (app, client, probe) = await StartAsync();
        await using var server = app;
        foreach (var value in new[] { "0", "true", "", " 1 x" })
            await AssertForbiddenProblemAsync(await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", (CrossSiteRequestGuard.HeaderName, value)));
        Assert.Equal(0, probe.RefreshStarts);
    }

    [Fact]
    public async Task Post_with_the_header_works_without_origin_like_a_non_browser_client()
    {
        var (app, client, probe) = await StartAsync();
        await using var server = app;
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/previews/pv_a/apply", Guard)).StatusCode);
        Assert.Equal(1, probe.RefreshStarts);
        Assert.Equal(1, probe.Applies);
    }

    [Fact]
    public async Task Other_origin_is_403_even_with_the_header()
    {
        var (app, client, probe) = await StartAsync();
        await using var server = app;
        foreach (var origin in new[] { "https://evil.example", "http://127.0.0.1:9999", "http://127.0.0.1.evil.example:5080", "https://127.0.0.1:5080", "null" })
            await AssertForbiddenProblemAsync(await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Origin", origin)));
        Assert.Equal(0, probe.RefreshStarts);
    }

    [Fact]
    public async Task Allowed_origins_work_with_the_header()
    {
        var (app, client, probe) = await StartAsync();
        await using var server = app;
        var origins = new[]
        {
            "http://127.0.0.1:5273", "http://localhost:5273", "http://127.0.0.1:5080", "http://localhost:5080",
            "http://LOCALHOST:5273", // 호스트 대소문자는 구분하지 않는다
        };
        foreach (var origin in origins)
            Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Origin", origin))).StatusCode);
        Assert.Equal(origins.Length, probe.RefreshStarts);
    }

    [Fact]
    public async Task Allowed_origin_cannot_replace_the_header()
    {
        var (app, client, _) = await StartAsync();
        await using var server = app;
        await AssertForbiddenProblemAsync(await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", ("Origin", "http://127.0.0.1:5273")));
    }

    [Fact]
    public async Task Sec_Fetch_Site_cross_site_is_403_but_same_origin_same_site_and_none_pass()
    {
        var (app, client, probe) = await StartAsync();
        await using var server = app;
        await AssertForbiddenProblemAsync(await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Sec-Fetch-Site", "cross-site")));
        // 허용 출처와 함께 와도 cross-site 표시가 있으면 막는다
        await AssertForbiddenProblemAsync(await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Origin", "http://localhost:5273"), ("Sec-Fetch-Site", "Cross-Site")));
        Assert.Equal(0, probe.RefreshStarts);
        foreach (var site in new[] { "same-origin", "same-site", "none" })
            Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Sec-Fetch-Site", site))).StatusCode);
        Assert.Equal(3, probe.RefreshStarts);
    }

    [Fact]
    public async Task Get_needs_no_header_and_is_not_filtered_by_origin_or_fetch_site()
    {
        var (app, client, _) = await StartAsync();
        await using var server = app;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sandbox")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/sandbox", ("Origin", "https://evil.example"), ("Sec-Fetch-Site", "cross-site"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Head, "/api/sandbox")).StatusCode); // 안전한 메서드는 가드 대상이 아니다(MapGet은 HEAD를 라우팅하지 않아 405)
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Forbidden_body_never_echoes_received_header_values()
    {
        var (app, client, _) = await StartAsync();
        await using var server = app;
        var res = await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh",
            (CrossSiteRequestGuard.HeaderName, "ECHO_HEADER_VALUE"), ("Origin", "https://ECHO_ORIGIN_VALUE.example"), ("Sec-Fetch-Site", "cross-site"));
        await AssertForbiddenProblemAsync(res);
        Assert.DoesNotContain("ECHO_", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var res2 = await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Origin", "https://ECHO_ORIGIN_VALUE.example"));
        await AssertForbiddenProblemAsync(res2);
        Assert.DoesNotContain("ECHO_", await res2.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Configured_allowed_origins_replace_the_defaults()
    {
        var (app, client, probe) = await StartAsync(cfg =>
        {
            cfg["DbRipple:AllowedOrigins:0"] = "https://tool.example:8443/"; // 끝 슬래시는 무시한다
        });
        await using var server = app;
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Origin", "https://tool.example:8443"))).StatusCode);
        await AssertForbiddenProblemAsync(await SendAsync(client, HttpMethod.Post, "/api/sandbox/refresh", Guard, ("Origin", "http://127.0.0.1:5273")));
        Assert.Equal(1, probe.RefreshStarts);
    }

    [Fact]
    public async Task OpenApi_lists_403_on_every_mutating_route()
    {
        var (app, client, _) = await StartAsync();
        await using var server = app;
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var p in new[] { "/api/sandbox/refresh", "/api/analyze", "/api/previews", "/api/previews/{id}/apply", "/api/previews/{id}/cancel" })
            Assert.True(paths.GetProperty(p).GetProperty("post").GetProperty("responses").TryGetProperty("403", out _), p);
    }
}
