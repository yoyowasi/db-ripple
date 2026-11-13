using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using DbRipple.Api.Middleware;
using DbRipple.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbRipple.Api.Tests;

/// <summary>
/// 실행 파일 모드 보안 최소(N4 계약 2·3절): 실행 토큰 1회 교환 → 쿠키 + 302, 틀린·재사용 토큰 401, 세션 없는 /api(GET 포함) 401 NO_SESSION,
/// 문서 401 HTML, 정적 자산 통과, 개발 모드는 기존 동작, 보안 헤더(오류 응답 포함), AllowedHosts 고정, 5273 출처는 개발 모드에서만, 로그에 비밀번호 없음.
/// </summary>
public sealed class ExecutableSecurityTests
{
    sealed class Sandbox : ISandboxManager
    {
        public Func<SandboxStatus> Status = () => new SandboxStatus { State = SandboxState.Ready, FingerprintMatch = true, Locked = false };
        public Task<SandboxStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status());
        public Task<BaselineContext> GetContextAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public void StartRefresh() { }
        public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct) => throw new NotSupportedException();
        public void MarkDirty(string reason) { }
    }

    sealed class LogSink : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        sealed class Logger(LogSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Lines.Enqueue(formatter(state, exception) + " " + exception);
        }
    }

    sealed record Rig(WebApplication App, HttpClient Client, Sandbox Sandbox, LogSink Logs) : IAsyncDisposable
    {
        public LaunchSession Session => App.Services.GetRequiredService<LaunchSession>();
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    static async Task<Rig> StartAsync(bool session, string environment = "Production", Action<ConfigurationManager>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var logs = new LogSink();
        builder.Logging.AddProvider(logs);
        configure?.Invoke(builder.Configuration);
        builder.Services.AddDbRippleApi();
        if (session) builder.Services.AddLaunchSession();
        var sandbox = new Sandbox();
        builder.Services.AddSingleton<ISandboxManager>(sandbox);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapDbRippleApi();
        app.MapGet("/", () => TypedResults.Content("<!doctype html><title>app</title>", "text/html"));
        await app.StartAsync();
        return new Rig(app, app.GetTestClient(), sandbox, logs);
    }

    static string SessionCookie(HttpResponseMessage res)
    {
        var header = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(LaunchSession.CookieName + "=", header);
        return header.Split(';')[0];
    }

    static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? cookie = null, string? host = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        if (host is not null) req.Headers.Host = host;
        return client.SendAsync(req);
    }

    static async Task AssertNoSessionProblemAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(401, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("NO_SESSION", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal(LaunchSession.NoSessionTitle, doc.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Launch_token_is_exchanged_once_for_a_strict_httponly_cookie_and_302_without_query()
    {
        await using var rig = await StartAsync(session: true);
        var token = rig.Session.Token;
        Assert.Equal(43, token.Length); // 32바이트 base64url

        var res = await GetAsync(rig.Client, $"/?launch={token}");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
        var header = Assert.Single(res.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("httponly", header);
        Assert.Contains("samesite=strict", header);
        Assert.Contains("path=/", header);
        Assert.DoesNotContain("secure", header);
        var cookie = SessionCookie(res);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(rig.Client, "/api/sandbox", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(rig.Client, "/", cookie)).StatusCode);

        // 같은 토큰 재사용 → 401(토큰은 바뀌었다)
        var reuse = await GetAsync(rig.Client, $"/?launch={token}");
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.False(reuse.Headers.Contains("Set-Cookie"));
        Assert.NotEqual(token, rig.Session.Token);
    }

    [Fact]
    public async Task Wrong_token_is_401_and_issues_no_cookie()
    {
        await using var rig = await StartAsync(session: true);
        foreach (var bad in new[] { "x", "", rig.Session.Token + "A", new string('A', 43) })
        {
            var res = await GetAsync(rig.Client, $"/?launch={bad}");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.False(res.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(rig.Client, "/api/sandbox", LaunchSession.CookieName + "=forged")).StatusCode);
    }

    [Fact]
    public async Task Redirect_never_points_to_another_host()
    {
        await using var rig = await StartAsync(session: true);
        var res = await rig.Client.GetAsync(new Uri($"http://localhost//evil.example/?launch={rig.Session.Token}")); // 경로가 //로 시작
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Without_session_api_get_is_401_problem_and_document_is_401_html_but_assets_pass()
    {
        await using var rig = await StartAsync(session: true);
        await AssertNoSessionProblemAsync(await GetAsync(rig.Client, "/api/sandbox"));
        await AssertNoSessionProblemAsync(await GetAsync(rig.Client, "/api/settings"));
        await AssertNoSessionProblemAsync(await rig.Client.PostAsync("/api/sandbox/refresh", null));

        var doc = await GetAsync(rig.Client, "/");
        Assert.Equal(HttpStatusCode.Unauthorized, doc.StatusCode);
        Assert.Equal("text/html", doc.Content.Headers.ContentType?.MediaType);
        Assert.Contains(LaunchSession.NoSessionTitle, await doc.Content.ReadAsStringAsync());

        Assert.NotEqual(HttpStatusCode.Unauthorized, (await GetAsync(rig.Client, "/assets/index-abc.js")).StatusCode); // 비밀 없는 정적 자산
    }

    [Fact]
    public async Task Development_mode_keeps_existing_behavior_without_session()
    {
        await using var rig = await StartAsync(session: false, environment: "Development");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(rig.Client, "/api/sandbox")).StatusCode);
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/refresh");
        post.Headers.Add(CrossSiteRequestGuard.HeaderName, CrossSiteRequestGuard.HeaderValue);
        post.Headers.Add("Origin", "http://127.0.0.1:5273"); // Vite 출처는 개발 모드에서만
        Assert.Equal(HttpStatusCode.Accepted, (await rig.Client.SendAsync(post)).StatusCode);
    }

    [Fact]
    public async Task Vite_origin_is_not_allowed_outside_development()
    {
        await using var rig = await StartAsync(session: false);
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/refresh");
        post.Headers.Add(CrossSiteRequestGuard.HeaderName, CrossSiteRequestGuard.HeaderValue);
        post.Headers.Add("Origin", "http://127.0.0.1:5273");
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Client.SendAsync(post)).StatusCode);
        Assert.DoesNotContain(CrossSiteRequestGuardOptions.DefaultOrigins, o => o.EndsWith(":5273", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Security_headers_are_on_every_response_including_errors()
    {
        await using var rig = await StartAsync(session: true);
        var cookie = SessionCookie(await GetAsync(rig.Client, $"/?launch={rig.Session.Token}"));
        rig.Sandbox.Status = () => throw new InvalidOperationException("boom");
        var responses = new[]
        {
            await GetAsync(rig.Client, "/"),                                  // 401 HTML
            await GetAsync(rig.Client, "/api/sandbox"),                       // 401 problem
            await GetAsync(rig.Client, "/", cookie),                          // 200
            await GetAsync(rig.Client, "/api/sandbox", cookie),               // 500(예외 처리기가 헤더를 지운 뒤)
            await rig.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/refresh") { Headers = { { "Cookie", cookie } } }), // 403
        };
        Assert.Equal(HttpStatusCode.InternalServerError, responses[3].StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, responses[4].StatusCode);
        foreach (var res in responses)
        {
            Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Assert.Single(res.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("DENY", Assert.Single(res.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("nosniff", Assert.Single(res.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(res.Headers.GetValues("Referrer-Policy")));
        }
        Assert.Contains("frame-ancestors 'none'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("script-src 'self';", SecurityHeaders.ContentSecurityPolicy);
    }

    [Fact]
    public async Task Allowed_hosts_are_fixed_in_code_even_if_configuration_says_star()
    {
        await using var rig = await StartAsync(session: false, configure: c => c["AllowedHosts"] = "*");
        foreach (var host in new[] { "127.0.0.1:5080", "localhost:5080", "[::1]:5080", "localhost" })
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(rig.Client, "/api/sandbox", host: host)).StatusCode);
        foreach (var host in new[] { "evil.example", "evil.example:5080", "127.0.0.1.nip.io:5080", "192.168.0.10:5080" })
            Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(rig.Client, "/api/sandbox", host: host)).StatusCode);
    }

    [Fact]
    public async Task Exception_logs_never_contain_passwords_or_raw_connection_strings()
    {
        await using var rig = await StartAsync(session: false);
        var cases = new Exception[]
        {
            new SandboxUnavailableException("연결 끊김 Server=tcp:db,1433;User ID=sa;Password=SECRET1;Encrypt=True"),
            new InvalidOperationException("실패 pwd = 'SECRET2'", new ArgumentException("Password=\"SECRET3\";")),
            new IOException("그 밖의 예외 PASSWORD=SECRET4"), // 처리기가 모르는 형식도 500 + 가린 로그
        };
        foreach (var ex in cases)
        {
            rig.Sandbox.Status = () => throw ex;
            var res = await GetAsync(rig.Client, "/api/sandbox");
            Assert.True((int)res.StatusCode >= 500);
            Assert.DoesNotContain("SECRET", await res.Content.ReadAsStringAsync());
        }
        var all = string.Join("\n", rig.Logs.Lines);
        Assert.Contains("API 예외 처리", all);
        Assert.DoesNotContain("SECRET", all);
        Assert.Contains("Password=***", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scrub_masks_password_values_in_every_form()
    {
        Assert.Equal("a Password=***;b", SafeExceptionText.Scrub("a Password=xyz;b"));
        Assert.Equal("Pwd = ***", SafeExceptionText.Scrub("Pwd = \"x;y\""));
        Assert.DoesNotContain("tok", SafeExceptionText.Scrub("Access Token=tok;"));
    }

    [Fact]
    public void Sandbox_status_serializes_origin_login_sysadmin_and_reachability()
    {
        var status = new SandboxStatus
        {
            State = SandboxState.Ready, FingerprintMatch = true, Locked = false, OriginReachable = false,
            Origin = new OriginInfo { Server = "DEVSQL01", Database = "AW_Origin", Login = "Windows 인증", IsSysadmin = null },
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(status, ContractJson.Options));
        Assert.False(doc.RootElement.GetProperty("originReachable").GetBoolean());
        var origin = doc.RootElement.GetProperty("origin");
        Assert.Equal("Windows 인증", origin.GetProperty("login").GetString());
        Assert.Equal(JsonValueKind.Null, origin.GetProperty("isSysadmin").ValueKind);
        Assert.True(new SandboxStatus { State = SandboxState.Ready, FingerprintMatch = true, Locked = false }.OriginReachable); // 기본 true
    }
}
