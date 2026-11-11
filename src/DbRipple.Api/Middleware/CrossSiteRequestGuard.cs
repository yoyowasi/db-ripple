using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DbRipple.Api.Middleware;

/// <summary>
/// 교차 사이트 요청(CSRF) 방어 설정. 구성 <c>DbRipple:AllowedOrigins</c>(문자열 배열)를 읽고, 없거나 비어 있으면 <see cref="DefaultOrigins"/>를 쓴다.
/// </summary>
public sealed class CrossSiteRequestGuardOptions
{
    /// <summary>기본 허용 출처: Vite 개발 서버(5273)와 Host(5080)의 127.0.0.1·localhost.</summary>
    public static readonly string[] DefaultOrigins =
    [
        "http://127.0.0.1:5273", "http://localhost:5273", "http://127.0.0.1:5080", "http://localhost:5080",
    ];

    /// <summary>Origin 헤더가 있을 때 허용하는 출처(스킴://호스트:포트, 끝 슬래시 없음, 대소문자 무시).</summary>
    public IReadOnlyCollection<string> AllowedOrigins { get; set; } = DefaultOrigins;
}

/// <summary>
/// 로컬 API(127.0.0.1:5080)를 사용자가 연 다른 웹사이트가 부르는 것(no-cors POST 등)을 막는다. <c>/api</c> 아래의 POST·PUT·PATCH·DELETE에만 적용하고 GET·HEAD·OPTIONS는 통과시킨다.
/// <list type="number">
/// <item>사용자 지정 헤더 <c>X-DbRipple-Request: 1</c>이 없으면 403. 교차 출처에서 이 헤더를 붙이면 preflight가 필요한데 서버는 CORS를 허용하지 않으므로 브라우저가 막는다.</item>
/// <item><c>Origin</c> 헤더가 있으면 허용 목록에 있어야 한다. 아니면 403(<c>null</c> 출처 포함).</item>
/// <item><c>Sec-Fetch-Site</c>가 <c>cross-site</c>면 403.</item>
/// </list>
/// 403 본문은 ProblemDetails이고 원인만 짧게 적는다. 받은 헤더 값은 되돌려 쓰지 않는다.
/// <see cref="DbRippleApi.AddDbRippleApi"/>가 <see cref="IStartupFilter"/>로 파이프라인 맨 앞에 끼워, Host와 TestServer가 같은 경로를 탄다(예외 처리기보다 앞이고 모델 바인딩보다 먼저라 본문 형식과 무관하게 403).
/// </summary>
public static class CrossSiteRequestGuard
{
    /// <summary>요청 표지 헤더 이름. 웹 클라이언트는 모든 /api 요청에 붙인다.</summary>
    public const string HeaderName = "X-DbRipple-Request";

    /// <summary>요청 표지 헤더 값.</summary>
    public const string HeaderValue = "1";

    /// <summary>구성 키(문자열 배열).</summary>
    public const string AllowedOriginsConfigKey = "DbRipple:AllowedOrigins";

    internal static IServiceCollection AddCrossSiteRequestGuard(this IServiceCollection services)
    {
        services.AddOptions<CrossSiteRequestGuardOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var configured = configuration.GetSection(AllowedOriginsConfigKey).Get<string[]>();
            options.AllowedOrigins = configured is { Length: > 0 } ? configured : CrossSiteRequestGuardOptions.DefaultOrigins;
        });
        services.AddTransient<IStartupFilter, GuardStartupFilter>();
        return services;
    }

    /// <summary>가드를 적용해야 하는 요청인지(/api 아래의 변경 메서드).</summary>
    internal static bool IsGuarded(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") && (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method));

    /// <summary>요청을 검사해 거부 사유(응답에 쓰는 고정 문구)를 돌려준다. 통과면 null.</summary>
    internal static string? Check(HttpRequest request, IReadOnlyCollection<string> allowedOrigins)
    {
        if (!string.Equals(request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal))
            return $"{HeaderName} 헤더가 필요합니다.";

        if (request.Headers.TryGetValue("Origin", out var origin) && origin.Count > 0
            && !allowedOrigins.Contains(origin.ToString().TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
            return "허용되지 않은 출처의 요청입니다.";

        if (string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
            return "다른 사이트에서 보낸 요청입니다.";

        return null;
    }

    sealed class GuardStartupFilter(IOptions<CrossSiteRequestGuardOptions> options) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var allowed = options.Value.AllowedOrigins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).ToArray();
            app.Use(async (context, nextMiddleware) =>
            {
                if (IsGuarded(context.Request) && Check(context.Request, allowed) is { } reason)
                {
                    var problem = new ProblemDetails
                    {
                        Status = StatusCodes.Status403Forbidden,
                        Title = "교차 사이트 요청이 차단되었습니다",
                        Detail = reason,
                        Instance = context.Request.Path,
                    };
                    await TypedResults.Problem(problem).ExecuteAsync(context);
                    return;
                }
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
