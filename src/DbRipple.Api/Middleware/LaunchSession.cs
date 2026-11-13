using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DbRipple.Api.Middleware;

/// <summary>
/// 실행 토큰(2026-11-13 N4 계약 2절). 시작할 때 32바이트 난수 토큰을 메모리에만 만들고, 실행 URL <c>/?launch=토큰</c>을 한 번 받으면
/// HttpOnly·SameSite=Strict 쿠키 <c>dbr_session</c>을 주고 302로 쿼리를 지운다. 토큰은 쓰자마자 새로 바뀐다(새 URL은 <see cref="TokenRotated"/>로 콘솔에 알린다).
/// 쿠키가 없으면 /api/** 401 ProblemDetails(reason NO_SESSION), 그 밖의 문서 401 HTML. /assets/**·favicon은 비밀이 없어 통과.
/// Host가 Development가 아닐 때만 <see cref="LaunchSessionExtensions.AddLaunchSession"/>로 켠다.
/// </summary>
public sealed class LaunchSession
{
    /// <summary>세션 쿠키 이름.</summary>
    public const string CookieName = "dbr_session";
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
    readonly List<byte[]> sessions = [];
    string token = NewValue();

    /// <summary>토큰이 쓰여 새 토큰으로 바뀌었다(인자: 새 토큰). Host가 새 실행 URL을 콘솔에 찍는다.</summary>
    public event Action<string>? TokenRotated;

    /// <summary>지금 유효한 일회용 토큰.</summary>
    public string Token { get { lock (gate) return token; } }

    /// <summary>실행 URL(base는 실제 바인딩 주소).</summary>
    public string LaunchUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/?{QueryName}={Token}";

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
            sessions.Add(Encoding.ASCII.GetBytes(session));
            if (sessions.Count > MaxSessions) sessions.RemoveAt(0);
        }
        TokenRotated?.Invoke(next);
        return session;
    }

    /// <summary>쿠키 값이 발급한 세션인지(고정 시간 비교).</summary>
    public bool IsValid(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > 64) return false;
        var bytes = Encoding.ASCII.GetBytes(cookie);
        lock (gate)
        {
            var ok = false;
            foreach (var s in sessions) ok |= CryptographicOperations.FixedTimeEquals(s, bytes);
            return ok;
        }
    }

    static bool FixedEquals(string a, string b) =>
        b.Length <= 64 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    static string NewValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>요청을 검사한다. 계속 진행하면 true, 응답을 이미 썼으면 false.</summary>
    internal async Task<bool> GateAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path;
        var isApi = path.StartsWithSegments("/api");
        if (HttpMethods.IsGet(request.Method) && !isApi && request.Query.TryGetValue(QueryName, out var launch))
        {
            var session = Redeem(launch.ToString());
            context.Response.Headers.CacheControl = "no-store";
            if (session is null)
            {
                await WriteHtmlAsync(context, "실행 주소가 이미 쓰였거나 맞지 않습니다.");
                return false;
            }
            context.Response.Cookies.Append(CookieName, session, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Secure = false, IsEssential = true,
            });
            // 같은 경로로(쿼리 제거). "//"·"/\"로 시작하면 다른 호스트로 해석될 수 있어 루트로 보낸다.
            var target = request.PathBase.Add(path).Value;
            if (string.IsNullOrEmpty(target) || !target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal) || target.StartsWith("/\\", StringComparison.Ordinal))
                target = "/";
            context.Response.Redirect(target);
            return false;
        }
        if (path.StartsWithSegments("/assets") || path.Equals("/favicon.ico") || path.Equals("/favicon.svg")) return true;
        if (IsValid(request.Cookies[CookieName])) return true;

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

/// <summary>실행 토큰 등록.</summary>
public static class LaunchSessionExtensions
{
    /// <summary>실행 토큰을 켠다(/api/**·문서에 세션 쿠키 필수). Development가 아닐 때만 부른다.</summary>
    public static IServiceCollection AddLaunchSession(this IServiceCollection services) => services.AddSingleton<LaunchSession>();
}
