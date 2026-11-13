using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace DbRipple.Api.Middleware;

/// <summary>
/// 실행 토큰(2026-11-13 N4 계약 2절). 시작할 때 32바이트 난수 토큰을 메모리에만 만들고, 실행 URL <c>/?launch=토큰</c>을 한 번 받으면
/// HttpOnly·SameSite=Strict 쿠키 <c>dbr_session_포트</c>를 주고 302로 <c>/</c>에 보낸다. 토큰은 쓰자마자 새로 바뀐다(새 URL은 <see cref="TokenRotated"/>로 콘솔에 알린다).
/// 쿠키는 포트를 가리지 않아 같은 PC의 다른 포트 서버도 받을 수 있으므로 쿠키만으로는 인증하지 않는다: 세션마다 요청 키(난수)를 따로 만들어
/// SPA 문서(index.html)에 <c>meta dbr-request-key</c>로 넣고, /api/**는 쿠키와 헤더 <c>X-DbRipple-Request</c> = 요청 키가 둘 다 맞아야 한다(GET 포함).
/// 아니면 /api/** 401 ProblemDetails(reason NO_SESSION), 그 밖의 문서 401 HTML. 실제로 있는 /assets/** 파일·favicon만 세션 없이 통과(없는 /assets/**는 404).
/// 실행 파일 모드에서만 <see cref="LaunchSessionExtensions.AddLaunchSession"/>로 켠다(<see cref="LaunchMode"/>).
/// </summary>
public sealed class LaunchSession
{
    /// <summary>세션 쿠키 이름 앞부분(뒤에 서버 포트). 두 인스턴스가 서로의 쿠키를 덮어쓰지 않게 한다.</summary>
    public const string CookiePrefix = "dbr_session_";
    /// <summary>요청 키를 넣는 meta 이름.</summary>
    public const string RequestKeyMetaName = "dbr-request-key";
    /// <summary>실행 URL 쿼리 이름.</summary>
    public const string QueryName = "launch";
    /// <summary>401 ProblemDetails reason.</summary>
    public const string NoSessionReason = "NO_SESSION";
    /// <summary>401 ProblemDetails type.</summary>
    public const string NoSessionType = "https://example.local/problems/no-session";
    /// <summary>401 제목(계약 2절).</summary>
    public const string NoSessionTitle = "실행 파일이 연 브라우저 창에서만 쓸 수 있습니다";

    const int MaxSessions = 32;
    readonly object gate = new();
    readonly List<(byte[] Session, string Key)> sessions = [];
    string token = NewValue();

    /// <summary>토큰이 쓰여 새 토큰으로 바뀌었다(인자: 새 토큰). Host가 새 실행 URL을 콘솔에 찍는다.</summary>
    public event Action<string>? TokenRotated;

    /// <summary>지금 유효한 일회용 토큰.</summary>
    public string Token { get { lock (gate) return token; } }

    /// <summary>실행 URL(base는 실제 바인딩 주소).</summary>
    public string LaunchUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/?{QueryName}={Token}";

    /// <summary>서버 포트별 세션 쿠키 이름(TestServer는 0).</summary>
    public static string CookieNameFor(int port) => CookiePrefix + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>토큰이 맞으면 새 세션 값을 돌려주고 토큰을 바꾼다. 틀리거나 이미 쓴 토큰이면 null.</summary>
    public string? Redeem(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return null;
        string session, next;
        lock (gate)
        {
            if (!FixedEquals(token, candidate)) return null;
            token = next = NewValue();
            session = NewValue();
            sessions.Add((Encoding.ASCII.GetBytes(session), NewValue()));
            if (sessions.Count > MaxSessions) sessions.RemoveAt(0);
        }
        TokenRotated?.Invoke(next);
        return session;
    }

    /// <summary>쿠키 값이 발급한 세션인지(고정 시간 비교).</summary>
    public bool IsValid(string? cookie) => RequestKeyOf(cookie) is not null;

    /// <summary>세션의 요청 키. 발급한 세션이 아니면 null.</summary>
    public string? RequestKeyOf(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > 64) return null;
        var bytes = Encoding.ASCII.GetBytes(cookie);
        lock (gate)
        {
            string? key = null;
            foreach (var s in sessions)
                if (CryptographicOperations.FixedTimeEquals(s.Session, bytes)) key = s.Key;
            return key;
        }
    }

    static bool FixedEquals(string a, string b) =>
        b.Length <= 64 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    static string NewValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>요청을 검사한다. 계속 진행하면 true, 응답을 이미 썼으면 false. true인 /api 요청은 요청 키 헤더까지 확인한 것이다.</summary>
    internal async Task<bool> GateAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path;
        var isApi = path.StartsWithSegments("/api");
        var cookieName = CookieNameFor(context.Connection.LocalPort);
        if (HttpMethods.IsGet(request.Method) && !isApi && request.Query.TryGetValue(QueryName, out var launch))
        {
            var session = Redeem(launch.ToString());
            context.Response.Headers.CacheControl = "no-store";
            if (session is null)
            {
                await WriteHtmlAsync(context, "실행 주소가 이미 쓰였거나 맞지 않습니다.");
                return false;
            }
            context.Response.Cookies.Append(cookieName, session, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Secure = false, IsEssential = true,
            });
            // 경로는 유지하지 않는다(탭·"//"·"/\" 등으로 다른 호스트를 가리키는 우회를 막는다).
            context.Response.Redirect("/");
            return false;
        }

        var webRoot = context.RequestServices.GetService<IWebHostEnvironment>()?.WebRootFileProvider;
        // 정적 자산 예외는 실제 파일만. /assets 아래 없는 경로가 SPA 문서로 떨어지지 않게 404.
        if (path.StartsWithSegments("/assets") || path.Equals("/favicon.ico") || path.Equals("/favicon.svg"))
        {
            if (webRoot?.GetFileInfo(path.Value!) is { Exists: true, IsDirectory: false }) return true;
            if (path.StartsWithSegments("/assets")) return NotFound(context);
        }
        // 실행 모드에서는 OpenAPI 문서를 내지 않는다(Host도 매핑하지 않는다).
        if (path.StartsWithSegments("/openapi")) return NotFound(context);

        var key = RequestKeyOf(request.Cookies[cookieName]);
        if (key is not null && isApi && HeaderMatches(request, key)) return true;
        if (key is not null && !isApi)
        {
            if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && TryReadIndex(webRoot, path, out var html))
            {
                await WriteIndexAsync(context, html, key);
                return false;
            }
            return true;
        }

        context.Response.Headers.CacheControl = "no-store";
        if (isApi)
        {
            var problem = new ProblemDetails
            {
                Type = NoSessionType, Title = NoSessionTitle, Status = StatusCodes.Status401Unauthorized, Instance = path,
            };
            problem.Extensions["reason"] = NoSessionReason;
            await TypedResults.Problem(problem).ExecuteAsync(context);
        }
        else await WriteHtmlAsync(context, "db-ripple 실행 파일을 다시 열거나, 콘솔 창에 표시된 새 주소로 여세요.");
        return false;
    }

    static bool NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return false;
    }

    static bool HeaderMatches(HttpRequest request, string key)
    {
        var sent = request.Headers[CrossSiteRequestGuard.HeaderName].ToString();
        return sent.Length == key.Length
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sent), Encoding.ASCII.GetBytes(key));
    }

    // SPA 문서: "/", "/index.html", 또는 wwwroot에 없는 경로(클라이언트 라우트). 있는 다른 파일은 정적 파일 미들웨어로 넘긴다.
    static bool TryReadIndex(IFileProvider? webRoot, PathString path, out string html)
    {
        html = "";
        if (webRoot is null) return false;
        var value = path.Value ?? "/";
        if (value != "/" && !value.Equals("/index.html", StringComparison.OrdinalIgnoreCase) && webRoot.GetFileInfo(value).Exists) return false;
        var index = webRoot.GetFileInfo("index.html");
        if (!index.Exists || index.IsDirectory || index.PhysicalPath is null) return false;
        html = File.ReadAllText(index.PhysicalPath, Encoding.UTF8);
        return true;
    }

    static async Task WriteIndexAsync(HttpContext context, string html, string key)
    {
        // 키는 base64url이라 HTML 이스케이프가 필요 없다.
        var meta = $"<meta name=\"{RequestKeyMetaName}\" content=\"{key}\" />";
        var at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        var body = at < 0 ? meta + html : html.Insert(at, meta);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await context.Response.WriteAsync(body);
    }

    static async Task WriteHtmlAsync(HttpContext context, string hint)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($"""
            <!doctype html><html lang="ko"><head><meta charset="utf-8"><title>db-ripple</title></head>
            <body style="font-family:sans-serif;margin:2rem"><h1 style="font-size:1.2rem">{NoSessionTitle}</h1><p>{hint}</p></body></html>
            """);
    }
}

/// <summary>
/// 실행 모드 판정(2026-11-13 N4 보강). 환경 변수(ASPNETCORE_ENVIRONMENT)에 묶지 않는다:
/// 실행 파일 폴더에 wwwroot/index.html이 있으면 항상 실행 모드. 개발 모드는 Debug 빌드 + <c>--dev</c> 인자 + wwwroot 없음일 때만.
/// </summary>
public static class LaunchMode
{
    /// <summary>개발 모드 인자(Properties/launchSettings.json이 넘긴다).</summary>
    public const string DevArg = "--dev";

    /// <summary>판정 결과. <paramref name="IgnoredDevReason"/>는 --dev를 받았지만 무시한 이유(콘솔 경고용).</summary>
    public readonly record struct Result(bool Executable, string? IgnoredDevReason);

    /// <summary>appBaseDir: 실행 파일 폴더. debugBuild: Host가 Debug 구성으로 빌드됐는지.</summary>
    public static Result Resolve(string appBaseDir, IEnumerable<string> args, bool debugBuild)
    {
        if (!args.Contains(DevArg, StringComparer.OrdinalIgnoreCase)) return new(true, null);
        if (File.Exists(Path.Combine(appBaseDir, "wwwroot", "index.html"))) return new(true, "실행 파일 폴더에 wwwroot/index.html이 있습니다");
        if (!debugBuild) return new(true, "Debug 빌드가 아닙니다");
        return new(false, null);
    }
}

/// <summary>실행 토큰 등록.</summary>
public static class LaunchSessionExtensions
{
    /// <summary>실행 토큰을 켠다(/api/**·문서에 세션 쿠키, /api/**에 요청 키 필수). 실행 파일 모드에서만 부른다.</summary>
    public static IServiceCollection AddLaunchSession(this IServiceCollection services) => services.AddSingleton<LaunchSession>();
}
