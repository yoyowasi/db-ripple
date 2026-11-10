using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DbRipple.Api.Middleware;

/// <summary>
/// 예외 → ProblemDetails(§4 C5). InvalidOperationException(잠금 충돌, 샌드박스 준비 안 됨, 지문 불일치) 409, ArgumentException 400,
/// NotImplementedException 501(2주차 골격). 그 밖은 기본 처리기(500). 예외 메시지는 내부 정보(연결 문자열 등)가 섞일 수 있어 응답에 넣지 않는다.
/// </summary>
internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            NotImplementedException => (StatusCodes.Status501NotImplemented, "아직 구현되지 않았습니다"),
            ArgumentException => (StatusCodes.Status400BadRequest, "요청이 올바르지 않습니다"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "지금 처리할 수 없는 상태입니다"),
            _ => (0, (string?)null),
        };
        if (status == 0) return false;
        logger.LogWarning(exception, "API 예외 처리: {Title}", title);
        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Instance = httpContext.Request.Path },
        });
    }
}
