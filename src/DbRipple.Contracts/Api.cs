namespace DbRipple.Contracts;

/// <summary>POST /api/analyze 요청(§6.4).</summary>
public sealed record AnalyzeRequest
{
    /// <summary>분석할 SQL 스크립트(GO 포함 가능). 최대 길이는 열린 질문 Q20.</summary>
    public required string Sql { get; init; }
}

/// <summary>POST /api/previews 요청(§6.4 `{ sql, options: { imageCap } }`).</summary>
public sealed record PreviewRequest
{
    /// <summary>미리보기할 SQL 스크립트.</summary>
    public required string Sql { get; init; }
    /// <summary>옵션. 없으면 기본값.</summary>
    public PreviewOptions? Options { get; init; }
}

/// <summary>행 페이지 조회 조건(§6.4 GET /api/previews/{id}/rows 쿼리).</summary>
public sealed record RowPageQuery
{
    /// <summary>기본 페이지 크기(§6.4 예시 limit 100).</summary>
    public const int DefaultLimit = 100;
    /// <summary>최대 페이지 크기. 설계에 없는 값이다(열린 질문 Q21).</summary>
    public const int MaxLimit = 1000;

    /// <summary>event 또는 net.</summary>
    public RowView View { get; init; } = RowView.Event;
    /// <summary>이 묶음의 행만(event: RowChange.firingId, net: explainedBy.firingIds에 포함). null이면 전체.</summary>
    public string? FiringId { get; init; }
    /// <summary>건너뛸 행 수(0 이상).</summary>
    public int Offset { get; init; }
    /// <summary>가져올 행 수(1..MaxLimit).</summary>
    public int Limit { get; init; } = DefaultLimit;
}

/// <summary>행 페이지(§6.4 RowPage). view=event면 TRow = RowChange, view=net이면 NetChange.</summary>
public sealed record RowPage<TRow>
{
    /// <summary>조건에 맞는 전체 행 수.</summary>
    public required long Total { get; init; }
    /// <summary>보기.</summary>
    public required RowView View { get; init; }
    /// <summary>오프셋.</summary>
    public required int Offset { get; init; }
    /// <summary>페이지 크기.</summary>
    public required int Limit { get; init; }
    /// <summary>행.</summary>
    public IReadOnlyList<TRow> Rows { get; init; } = [];
}

/// <summary>422 본문의 거부 항목(§6.4 `rejects[]`: statementIndex, ruleId, span).</summary>
public sealed record RejectItem
{
    /// <summary>문장 번호(파싱 실패면 null).</summary>
    public int? StatementIndex { get; init; }
    /// <summary>규칙 ID.</summary>
    public required string RuleId { get; init; }
    /// <summary>위치.</summary>
    public required SourceSpan Span { get; init; }
}

/// <summary>ProblemDetails type URI(§6.4). 설계 예시 값을 그대로 쓴다(실제 문서 URL은 열린 질문 Q22).</summary>
public static class ProblemTypes
{
    /// <summary>422 거부.</summary>
    public const string Rejected = "https://example.local/problems/rejected";
    /// <summary>422 본문의 거부 목록 확장 속성 이름.</summary>
    public const string RejectsExtension = "rejects";
}

/// <summary>
/// C14 미리보기 저장소(§4 C14, 구현은 Preview 프로젝트 PreviewStore: 메모리 + JSON 파일, 활성 1건, 만료 30분, `Save`, `Get`, `Page`, `Export`).
/// 만료되었거나 없는 id는 모든 메서드에서 null/false(API 404).
/// </summary>
public interface IPreviewStore
{
    /// <summary>만료 시간 기본값(§4 C14, §5.3 선행 조건 30분).</summary>
    static TimeSpan DefaultExpiry => TimeSpan.FromMinutes(30);

    /// <summary>저장. 활성 1건이므로 이전 건은 버린다.</summary>
    Task SaveAsync(PreviewRecord record, CancellationToken ct);
    /// <summary>한 건. 없거나 만료면 null.</summary>
    Task<PreviewRecord?> GetAsync(string previewId, CancellationToken ct);
    /// <summary>발동 보기 페이지(query.View = Event). 없거나 만료면 null.</summary>
    Task<RowPage<RowChange>?> PageEventsAsync(string previewId, RowPageQuery query, CancellationToken ct);
    /// <summary>최종 차이 보기 페이지(query.View = Net). 없거나 만료면 null.</summary>
    Task<RowPage<NetChange>?> PageNetAsync(string previewId, RowPageQuery query, CancellationToken ct);
    /// <summary>내보내기: PreviewRecord JSON(ContractJson.Options)을 destination에 쓴다. 없거나 만료면 false.</summary>
    Task<bool> ExportAsync(string previewId, Stream destination, CancellationToken ct);
    /// <summary>[취소](§5.4): 버린다. 설계 §4 C14 목록에 없지만 POST cancel(204)에 필요하다. 없으면 false.</summary>
    Task<bool> RemoveAsync(string previewId, CancellationToken ct);
}
