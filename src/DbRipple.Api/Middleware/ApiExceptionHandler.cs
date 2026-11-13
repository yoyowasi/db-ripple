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
/// <item>그 밖의 예외도 500으로 처리한다(.NET 10 기본 처리기가 원본 예외를 로그에 남기지 않게). 요청 취소만 기본 처리기에 넘긴다.</item>
/// </list>
/// 예외 메시지는 내부 정보(연결 문자열 등)가 섞일 수 있어 응답에 넣지 않고, 로그에는 <see cref="SafeExceptionText"/>로 가린 문장만 남긴다.
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
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (0, (string?)null),
            _ => (StatusCodes.Status500InternalServerError, "서버 내부 오류입니다"),
        };
        if (status == 0) return false;
        // 예외 객체를 그대로 넘기지 않는다: 메시지에 연결 문자열·비밀번호가 섞일 수 있다(N4). SqlException은 번호·서버만.
        var safe = SafeExceptionText.Describe(exception);
        if (status >= 500 && status != StatusCodes.Status501NotImplemented) logger.LogError("API 예외 처리: {Title} {Exception}", title, safe);
        else logger.LogWarning("API 예외 처리: {Title} {Exception}", title, safe);

        var problem = new ProblemDetails { Status = status, Title = title, Instance = httpContext.Request.Path };
        if (exception is PreviewConflictException conflict)
            problem.Extensions[PreviewConflictException.ReasonExtension] = JsonSerializer.SerializeToElement(conflict.Reason, ContractJson.Options).GetString();

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, Exception = exception, ProblemDetails = problem });
    }
}

/// <summary>로그용 예외 문장(N4). 비밀번호 키 값은 가리고, SqlException은 번호·서버·심각도만 쓴다(메시지 원문 없음). 스택 추적은 남긴다.</summary>
public static partial class SafeExceptionText
{
    /// <summary>예외 사슬 전체를 한 문장으로.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var parts = new List<string>();
        for (var e = exception; e is not null && parts.Count < 8; e = e.InnerException)
            parts.Add(e is Microsoft.Data.SqlClient.SqlException sql
                ? $"SqlException(오류 {sql.Number}, 서버 {sql.Server}, 심각도 {sql.Class})"
                : $"{e.GetType().Name}: {Scrub(e.Message)}");
        return string.Join(" ---> ", parts) + Environment.NewLine + exception.StackTrace;
    }

    /// <summary>Password=·Pwd= 값과 Access Token 값을 ***로.</summary>
    public static string Scrub(string text) => SecretPattern().Replace(text, m => m.Groups[1].Value + "***");

    [System.Text.RegularExpressions.GeneratedRegex(@"((?:password|pwd|access\s*token)\s*=\s*)(""[^""]*""|'[^']*'|[^;]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SecretPattern();
}
