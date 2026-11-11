using DbRipple.Api.Middleware;
using DbRipple.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbRipple.Api;

/// <summary>
/// C5 REST API(§6.4). 서비스(ISandboxManager, IBatchAnalyzer, IPreviewPipeline, IPreviewStore, ICatalogSchemaProvider, IApplyService)는 호스트가 등록한다.
/// 경로·상태 코드·본문 형식은 §6.4 계약이다. 응답 JSON은 ContractJson 설정(camelCase, 열거형 문자열, null 포함).
/// 오류 응답은 모두 ProblemDetails다. 예외 → 상태 코드 매핑은 <see cref="ApiExceptionHandler"/>(AD9): 409(확장 <c>reason</c>), 503, 400, 501, 500.
/// </summary>
public static class DbRippleApi
{
    /// <summary>요청 SQL 최대 길이(문자 수, 열린 질문 Q20). 넘으면 400.</summary>
    public const int MaxSqlLength = 1_000_000;

    /// <summary>ProblemDetails, 예외 처리기, OpenAPI, JSON 설정을 등록한다.</summary>
    public static IServiceCollection AddDbRippleApi(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddOpenApi();
        services.ConfigureHttpJsonOptions(o => ContractJson.Apply(o.SerializerOptions));
        return services;
    }

    /// <summary>§6.4 엔드포인트를 등록한다. 호출 전에 app.UseExceptionHandler()를 둘 것.</summary>
    public static IEndpointRouteBuilder MapDbRippleApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/sandbox", async Task<Ok<SandboxStatus>> ([FromServices] ISandboxManager sandbox, CancellationToken ct) =>
                TypedResults.Ok(await sandbox.GetStatusAsync(ct)))
            .WithName("GetSandbox").WithSummary("샌드박스 상태(§6.4 SandboxStatus)");

        api.MapPost("/sandbox/refresh", Accepted ([FromServices] ISandboxManager sandbox) =>
            {
                sandbox.StartRefresh();
                return TypedResults.Accepted("/api/sandbox");
            })
            .WithName("RefreshSandbox").WithSummary("기준선 새로 고침 시작(202 후 GET /api/sandbox로 상태 조회)");

        api.MapGet("/catalog/schema", async Task<Ok<CatalogSchema>> ([FromServices] ICatalogSchemaProvider provider, CancellationToken ct) =>
                TypedResults.Ok(await provider.GetSchemaAsync(ct)))
            .WithName("GetCatalogSchema").WithSummary("자동완성용 스키마(Q23): 테이블·열·sqlType·키·수집 범위")
            .WithDescription("현재 기준선 샌드박스 카탈로그에서 만든다. 기준선이 없거나 구축 중이면 409(reason NO_BASELINE·BUILDING). baselineAt이 캐시 키다.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/analyze", async Task<Results<Ok<AnalysisResult>, ValidationProblem>> (
                AnalyzeRequest request, [FromServices] ISandboxManager sandbox, [FromServices] IBatchAnalyzer analyzer, CancellationToken ct) =>
            {
                if (SqlProblem(request.Sql) is { } invalid) return invalid;
                var baseline = await sandbox.GetContextAsync(ct);
                return TypedResults.Ok(analyzer.Analyze(request.Sql, baseline));
            })
            .WithName("Analyze").WithSummary("배치 분석(§6.4, 문장·거부·경고·Diagnostic[])")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/previews", async Task<Results<Ok<PreviewResult>, ProblemHttpResult, ValidationProblem>> (
                PreviewRequest request, [FromServices] IPreviewPipeline pipeline, CancellationToken ct) =>
            {
                if (SqlProblem(request.Sql) is { } invalid) return invalid;
                if (request.Options?.ImageCap is <= 0)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["options.imageCap"] = ["1 이상이어야 합니다."] });
                var outcome = await pipeline.RunAsync(request.Sql, request.Options ?? new PreviewOptions(), null, ct);
                return outcome.Record is { } record ? TypedResults.Ok(record.Result) : Rejected(outcome.Analysis);
            })
            .WithName("CreatePreview").WithSummary("미리보기 실행(동기). 거부면 422 ProblemDetails + rejects")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapGet("/previews/{id}", async Task<Results<Ok<PreviewResult>, NotFound>> (string id, [FromServices] IPreviewStore store, CancellationToken ct) =>
                await store.GetAsync(id, ct) is { } r ? TypedResults.Ok(r.Result) : TypedResults.NotFound())
            .WithName("GetPreview").WithSummary("미리보기 결과");

        api.MapGet("/previews/{id}/rows", async Task<Results<Ok<RowPage<RowChange>>, Ok<RowPage<NetChange>>, ValidationProblem, NotFound>> (
                string id, string? view, string? firingId, int? offset, int? limit, [FromServices] IPreviewStore store, CancellationToken ct) =>
            {
                RowView? v = view switch { null or "event" => RowView.Event, "net" => RowView.Net, _ => null };
                var off = offset ?? 0;
                var lim = limit ?? RowPageQuery.DefaultLimit;
                var errors = new Dictionary<string, string[]>();
                if (v is null) errors["view"] = ["event 또는 net"];
                if (off < 0) errors["offset"] = ["0 이상"];
                if (lim is < 1 or > RowPageQuery.MaxLimit) errors["limit"] = [$"1..{RowPageQuery.MaxLimit}"];
                if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
                var query = new RowPageQuery { View = v!.Value, FiringId = firingId, Offset = off, Limit = lim };
                if (query.View == RowView.Event)
                    return await store.PageEventsAsync(id, query, ct) is { } e ? TypedResults.Ok(e) : TypedResults.NotFound();
                return await store.PageNetAsync(id, query, ct) is { } n ? TypedResults.Ok(n) : TypedResults.NotFound();
            })
            .WithName("GetPreviewRows").WithSummary("행 페이지(view=event: RowChange[], view=net: NetChange[])");

        api.MapPost("/previews/{id}/apply", async Task<Results<Ok<ApplyResult>, NotFound>> (
                string id, [FromBody] ApplyRequest? request, [FromServices] IApplyService apply, CancellationToken ct) =>
                await apply.ApplyAsync(id, request ?? new ApplyRequest(), ct) is { } result ? TypedResults.Ok(result) : TypedResults.NotFound())
            .WithName("ApplyPreview").WithSummary("[적용](§5.3, C15): 미리보기를 원본에 반영. 상태 5종(committed, rolled_back_mismatch, drift_detected, precondition_failed, error) 모두 200")
            .WithDescription("본문은 선택({ sqlHash }). 미리보기가 없거나 만료면 404, 다른 미리보기·적용이 실행 중이거나 기준선 구축 중이면 409(reason LOCKED·BUILDING), SQL Server 연결 끊김은 503.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPost("/previews/{id}/cancel", async Task<Results<NoContent, NotFound>> (string id, [FromServices] IPreviewStore store, CancellationToken ct) =>
                await store.RemoveAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithName("CancelPreview").WithSummary("[취소](§5.4): 결과를 버린다");

        api.MapGet("/previews/{id}/export", async Task<Results<FileContentHttpResult, NotFound>> (string id, [FromServices] IPreviewStore store, CancellationToken ct) =>
            {
                using var ms = new MemoryStream();
                return await store.ExportAsync(id, ms, ct)
                    ? TypedResults.File(ms.ToArray(), "application/json", $"{id}.json")
                    : TypedResults.NotFound();
            })
            .WithName("ExportPreview").WithSummary("결과 JSON 파일(PreviewRecord)");

        return app;
    }

    /// <summary>SQL 입력 검사(비어 있음, 최대 길이 Q20). 문제가 없으면 null.</summary>
    static ValidationProblem? SqlProblem(string? sql)
    {
        var message = string.IsNullOrWhiteSpace(sql) ? "SQL이 비어 있습니다."
            : sql.Length > MaxSqlLength ? $"SQL은 최대 {MaxSqlLength:N0}자입니다(현재 {sql.Length:N0}자)."
            : null;
        return message is null ? null : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["sql"] = [message] });
    }

    /// <summary>422 본문(§6.4): ProblemDetails + rejects[].</summary>
    static ProblemHttpResult Rejected(AnalysisResult analysis)
    {
        var problem = new ProblemDetails
        {
            Type = ProblemTypes.Rejected,
            Title = "미리보기할 수 없는 문장이 있습니다",
            Status = StatusCodes.Status422UnprocessableEntity,
        };
        problem.Extensions[ProblemTypes.RejectsExtension] = analysis.Rejects
            .Select(d => new RejectItem { StatementIndex = d.StatementIndex, RuleId = d.RuleId, Span = d.Span })
            .ToList();
        return TypedResults.Problem(problem);
    }
}
