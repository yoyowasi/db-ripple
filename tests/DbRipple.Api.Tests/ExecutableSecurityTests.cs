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

    sealed record Rig(WebApplication App, HttpClient Client, Sandbox Sandbox, LogSink Logs, string Root) : IAsyncDisposable
    {
        public LaunchSession Session => App.Services.GetRequiredService<LaunchSession>();
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    const string IndexHtml = "<!doctype html><html><head><meta charset=\"UTF-8\" /><title>app</title></head><body><div id=\"app\"></div></body></html>";

    // 콘텐츠 루트 = 게시본처럼 wwwroot/index.html·assets/app.js가 있는 임시 폴더.
    static async Task<Rig> StartAsync(bool session, string environment = "Production", Action<ConfigurationManager>? configure = null, bool openApi = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "dbrt_exe_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "wwwroot", "assets"));
        File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(root, "wwwroot", "assets", "app.js"), "console.log(1)");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = root, WebRootPath = "wwwroot" });
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
        if (openApi) app.MapOpenApi();
        app.UseStaticFiles();
        app.MapDbRippleApi();
        app.MapGet("/", () => TypedResults.Content("<!doctype html><title>app</title>", "text/html"));
        await app.StartAsync();
        return new Rig(app, app.GetTestClient(), sandbox, logs, root);
    }

    static readonly string CookieName = LaunchSession.CookieNameFor(0); // TestServer 포트

    static string SessionCookie(HttpResponseMessage res)
    {
        var header = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(CookieName + "=", header);
        return header.Split(';')[0];
    }

    /// <summary>실행 URL → 쿠키, 쿠키로 연 index.html의 meta → 요청 키.</summary>
    static async Task<(string Cookie, string Key)> LoginAsync(Rig rig)
    {
        var cookie = SessionCookie(await GetAsync(rig.Client, $"/?launch={rig.Session.Token}"));
        var doc = await GetAsync(rig.Client, "/", cookie);
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
        return (cookie, MetaKey(await doc.Content.ReadAsStringAsync()));
    }

    static string MetaKey(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, "<meta name=\"dbr-request-key\" content=\"([A-Za-z0-9_-]{43})\" />");
        Assert.True(m.Success, "요청 키 meta 없음");
        return m.Groups[1].Value;
    }

    static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? cookie = null, string? host = null, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        if (host is not null) req.Headers.Host = host;
        if (key is not null) req.Headers.Add(CrossSiteRequestGuard.HeaderName, key);
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

        var doc = await GetAsync(rig.Client, "/", cookie);
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
        Assert.Contains("no-store", doc.Headers.CacheControl?.ToString());
        var key = MetaKey(await doc.Content.ReadAsStringAsync());
        Assert.NotEqual(cookie.Split('=')[1], key);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(rig.Client, "/api/sandbox", cookie, key: key)).StatusCode);

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
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(rig.Client, "/api/sandbox", CookieName + "=forged", key: "forged")).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example/")]
    [InlineData("/%09/evil.example/")]
    [InlineData("/%0a/evil.example/")]
    [InlineData("/%5Cevil.example/")]
    [InlineData("/%2F%2Fevil.example/")]
    [InlineData("/settings/deep")]
    public async Task Redirect_after_launch_is_always_root(string path)
    {
        await using var rig = await StartAsync(session: true);
        var res = await rig.Client.GetAsync(new Uri($"http://localhost{path}?launch={rig.Session.Token}"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Cookie_alone_is_not_enough_for_api_request_key_header_is_required()
    {
        await using var rig = await StartAsync(session: true);
        var (cookie, key) = await LoginAsync(rig);
        // 다른 포트 서버가 쿠키를 받아 재사용하는 경우: 고정 헤더 "1"이나 틀린 키로는 GET도 401.
        foreach (var bad in new string?[] { null, "1", key + "x", key[..^1] + (key[^1] == 'A' ? 'B' : 'A') })
            await AssertNoSessionProblemAsync(await GetAsync(rig.Client, "/api/sandbox", cookie, key: bad));
        var put = new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/refresh") { Headers = { { "Cookie", cookie }, { CrossSiteRequestGuard.HeaderName, "1" } } };
        await AssertNoSessionProblemAsync(await rig.Client.SendAsync(put));

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(rig.Client, "/api/sandbox", cookie, key: key)).StatusCode);
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/refresh") { Headers = { { "Cookie", cookie }, { CrossSiteRequestGuard.HeaderName, key } } };
        Assert.Equal(HttpStatusCode.Accepted, (await rig.Client.SendAsync(post)).StatusCode);
        // 요청 키는 맞아도 출처 검사는 그대로(403).
        var cross = new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/refresh") { Headers = { { "Cookie", cookie }, { CrossSiteRequestGuard.HeaderName, key }, { "Origin", "http://127.0.0.1:9999" } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Client.SendAsync(cross)).StatusCode);

        // 세션마다 키가 다르다: 다른 세션의 키로는 401.
        var (cookie2, key2) = await LoginAsync(rig);
        Assert.NotEqual(key, key2);
        await AssertNoSessionProblemAsync(await GetAsync(rig.Client, "/api/sandbox", cookie2, key: key));
    }

    [Fact]
    public async Task Request_key_meta_is_injected_into_spa_documents_only_with_session()
    {
        await using var rig = await StartAsync(session: true);
        var (cookie, key) = await LoginAsync(rig);
        foreach (var path in new[] { "/index.html", "/settings/deep" }) // 클라이언트 라우트도 같은 문서
        {
            var res = await GetAsync(rig.Client, path, cookie);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(key, MetaKey(await res.Content.ReadAsStringAsync()));
        }
        var anon = await GetAsync(rig.Client, "/index.html");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.DoesNotContain("dbr-request-key", await anon.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Cookie_name_is_per_port()
    {
        Assert.Equal("dbr_session_5080", LaunchSession.CookieNameFor(5080));
        Assert.NotEqual(LaunchSession.CookieNameFor(5080), LaunchSession.CookieNameFor(5081));
    }

    [Fact]
    public async Task Development_environment_does_not_turn_off_executable_mode_security()
    {
        // 게시본 폴더(wwwroot/index.html 있음)는 --dev를 줘도 실행 파일 모드다(환경 변수는 보지 않는다).
        var root = Path.Combine(Path.GetTempPath(), "dbrt_mode_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        try
        {
            Assert.Equal(new LaunchMode.Result(false, null), LaunchMode.Resolve(root, ["--dev"], debugBuild: true));
            Assert.True(LaunchMode.Resolve(root, [], debugBuild: true).Executable);
            Assert.True(LaunchMode.Resolve(root, ["--dev"], debugBuild: false).Executable);
            File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), IndexHtml);
            var r = LaunchMode.Resolve(root, ["--DEV", "--no-browser"], debugBuild: true);
            Assert.True(r.Executable);
            Assert.NotNull(r.IgnoredDevReason);
        }
        finally { Directory.Delete(root, recursive: true); }

        // 실행 파일 모드 서버를 ASPNETCORE_ENVIRONMENT=Development로 띄워도: 세션 없는 /api 401, OpenAPI 404(세션이 있어도).
        await using var rig = await StartAsync(session: true, environment: "Development", openApi: true);
        await AssertNoSessionProblemAsync(await GetAsync(rig.Client, "/api/sandbox"));
        var (cookie, key) = await LoginAsync(rig);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(rig.Client, "/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(rig.Client, "/openapi/v1.json", cookie, key: key)).StatusCode);
    }

    [Fact]
    public async Task Development_mode_still_serves_openapi()
    {
        await using var rig = await StartAsync(session: false, environment: "Development", openApi: true);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(rig.Client, "/openapi/v1.json")).StatusCode);
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

        // 정적 자산 예외는 실제 파일만: 있는 파일 200, /assets 아래 없는 경로는 index.html이 아니라 404.
        var asset = await GetAsync(rig.Client, "/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("console.log(1)", await asset.Content.ReadAsStringAsync());
        foreach (var missing in new[] { "/assets/foo", "/assets/index-abc.js", "/assets/", "/assets" })
        {
            var res = await GetAsync(rig.Client, missing);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.DoesNotContain("<div id=\"app\">", await res.Content.ReadAsStringAsync());
        }
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
        var (cookie, key) = await LoginAsync(rig);
        rig.Sandbox.Status = () => throw new InvalidOperationException("boom");
        var responses = new[]
        {
            await GetAsync(rig.Client, "/"),                                  // 401 HTML
            await GetAsync(rig.Client, "/api/sandbox"),                       // 401 problem
            await GetAsync(rig.Client, "/", cookie),                          // 200(요청 키 주입)
            await GetAsync(rig.Client, "/api/sandbox", cookie, key: key),     // 500(예외 처리기가 헤더를 지운 뒤)
            await rig.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/refresh")
                { Headers = { { "Cookie", cookie }, { CrossSiteRequestGuard.HeaderName, key }, { "Sec-Fetch-Site", "cross-site" } } }), // 403
            await GetAsync(rig.Client, "/assets/nope.js"),                    // 404
        };
        Assert.Equal(HttpStatusCode.InternalServerError, responses[3].StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, responses[4].StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, responses[5].StatusCode);
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
        {
            var res = await GetAsync(rig.Client, "/api/sandbox", host: host);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            // Host 필터의 400에도 보안 헤더가 붙는다(헤더 등록이 Host 필터보다 바깥).
            Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Assert.Single(res.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("nosniff", Assert.Single(res.Headers.GetValues("X-Content-Type-Options")));
        }
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

    [Fact]
    public void Sandbox_status_serializes_instance_warnings_empty_by_default()
    {
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(new SandboxStatus { State = SandboxState.Ready, FingerprintMatch = true, Locked = false }, ContractJson.Options)))
            Assert.Equal(0, doc.RootElement.GetProperty("instanceWarnings").GetArrayLength());
        var status = new SandboxStatus { State = SandboxState.Ready, FingerprintMatch = true, Locked = false, InstanceWarnings = ["샌드박스 인스턴스에 'xp_cmdshell'이(가) 켜짐"] };
        using var doc2 = JsonDocument.Parse(JsonSerializer.Serialize(status, ContractJson.Options));
        Assert.Equal("샌드박스 인스턴스에 'xp_cmdshell'이(가) 켜짐", doc2.RootElement.GetProperty("instanceWarnings")[0].GetString());
    }
}
