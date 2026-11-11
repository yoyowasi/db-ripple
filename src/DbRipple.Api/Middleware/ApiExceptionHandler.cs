using System.Text.Json;
using DbRipple.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DbRipple.Api.Middleware;

/// <summary>
/// 예외 → ProblemDetails(§4 C5, AD9). 형식으로 가른다(하위 형식이 먼저).
/// <list type="bullet">
/// <item><see cref="PreviewConflictException"/>(잠금, 구축 중, 기준선 없음, 지문 불일치, 계속 오염) 409. 확장 속성 <c>reason</c>에 <see cref="ConflictReason"/>(LOCKED·BUILDING·NO_BASELINE·FINGERPRINT_MISMATCH·DIRTY).</item>
/// <item><see cref="SandboxUnavailableException"/>(SQL Server 연결 끊김, E20) 503.</item>
/// <item><see cref="ArgumentException"/> 400, <see cref="NotImplementedException"/> 501.</item>
/// <item>그 밖의 <see cref="InvalidOperationException"/>(실행기 SET 옵션 점검 실패 같은 내부 오류) 500. 예전에는 409로 나갔다(2주차 인계 7).</item>
/// <item>그 밖의 예외는 처리하지 않고(false) 기본 처리기가 500으로 낸다.</item>
/// </list>
/// 예외 메시지는 내부 정보(연결 문자열 등)가 섞일 수 있어 응답에 넣지 않는다(로그에만 남긴다).
/// </summary>
internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            PreviewConflictException => (StatusCodes.Status409Conflict, "지금 처리할 수 없는 상태입니다"),
            SandboxUnavailableException => (StatusCodes.Status503ServiceUnavailable, "SQL Server에 연결할 수 없습니다"),
            NotImplementedException => (StatusCodes.Status501NotImplemented, "아직 구현되지 않았습니다"),
            ArgumentException => (StatusCodes.Status400BadRequest, "요청이 올바르지 않습니다"),
            InvalidOperationException => (StatusCodes.Status500InternalServerError, "서버 내부 오류입니다"),
            _ => (0, (string?)null),
        };
        if (status == 0) return false;
        if (status >= 500 && status != StatusCodes.Status501NotImplemented) logger.LogError(exception, "API 예외 처리: {Title}", title);
        else logger.LogWarning(exception, "API 예외 처리: {Title}", title);

        var problem = new ProblemDetails { Status = status, Title = title, Instance = httpContext.Request.Path };
        if (exception is PreviewConflictException conflict)
            problem.Extensions[PreviewConflictException.ReasonExtension] = JsonSerializer.SerializeToElement(conflict.Reason, ContractJson.Options).GetString();

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, Exception = exception, ProblemDetails = problem });
    }
}
