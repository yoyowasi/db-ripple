using DbRipple.Core.Baseline;

namespace DbRipple.Contracts;

/// <summary>미리보기의 기준선 정보(§6.2 PreviewResult.baseline).</summary>
public sealed record BaselineInfo
{
    /// <summary>원본 역할 DB.</summary>
    public required string OriginDb { get; init; }
    /// <summary>샌드박스 DB.</summary>
    public required string SandboxDb { get; init; }
    /// <summary>기준 스냅샷 DB.</summary>
    public required string SnapshotDb { get; init; }
    /// <summary>백업 완료 시각.</summary>
    public required DateTimeOffset BackupFinishedAt { get; init; }
    /// <summary>환경 지문(Core Fingerprint, 필드 이름이 §6.2 예시와 같다).</summary>
    public required Fingerprint Fingerprint { get; init; }
}

/// <summary>단계별 시간(ms, §6.2 timingsMs, §10.4 M5).</summary>
public sealed record TimingsMs
{
    /// <summary>분석(C6).</summary>
    public long Analyze { get; init; }
    /// <summary>문장 실행(C10).</summary>
    public long Execute { get; init; }
    /// <summary>감사 기록 읽기.</summary>
    public long ReadLog { get; init; }
    /// <summary>재조회.</summary>
    public long Reread { get; init; }
    /// <summary>최종 차이(C12).</summary>
    public long NetDiff { get; init; }
    /// <summary>원인 귀속(C11+C13).</summary>
    public long Resolve { get; init; }
    /// <summary>직렬화·저장(C14).</summary>
    public long Serialize { get; init; }
}

/// <summary>테이블별 순 변경 건수(§6.2 netSummary, D7 일치 규칙). 다중집합 건수는 Multiplicity 합으로 센다(열린 질문 Q16).</summary>
public sealed record NetSummaryRow
{
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>INSERTED 수.</summary>
    public long Inserted { get; init; }
    /// <summary>UPDATED 수.</summary>
    public long Updated { get; init; }
    /// <summary>DELETED 수.</summary>
    public long Deleted { get; init; }
    /// <summary>MULTISET_REMOVED 행 수.</summary>
    public long MultisetRemoved { get; init; }
    /// <summary>MULTISET_ADDED 행 수.</summary>
    public long MultisetAdded { get; init; }
}

/// <summary>
/// 미리보기 결과 요약(§6.2 PreviewResult, §6.4 POST /api/previews 200 응답). 행 단위 변경(RowChange·NetChange)은 여기 넣지 않고 행 페이지(RowPage)로 읽는다.
/// </summary>
public sealed record PreviewResult
{
    /// <summary>미리보기 id(예 "pv_20261106_0001", 형식은 C14가 정함).</summary>
    public required string PreviewId { get; init; }
    /// <summary>ok, failed, trigger_blocked, truncated.</summary>
    public required PreviewStatus Status { get; init; }
    /// <summary>생성 시각.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>"sha256:" + 원문 SQL의 SHA-256 hex([적용] 선행 조건, §5.3). 해시 입력의 인코딩·정규화는 열린 질문 Q17(제안: UTF-8, 정규화 없음).</summary>
    public required string SqlHash { get; init; }
    /// <summary>기준선 정보.</summary>
    public required BaselineInfo Baseline { get; init; }
    /// <summary>문장 결과.</summary>
    public IReadOnlyList<StatementResult> Statements { get; init; } = [];
    /// <summary>묶음(Cause 채워짐).</summary>
    public IReadOnlyList<Firing> Firings { get; init; } = [];
    /// <summary>잔차.</summary>
    public IReadOnlyList<Residual> Residuals { get; init; } = [];
    /// <summary>실패·거부 뒤 트랜잭션 밖에서 확정된 변경(§5.2 4).</summary>
    public IReadOnlyList<NetChange> ResidualCommitted { get; init; } = [];
    /// <summary>테이블별 순 변경 건수.</summary>
    public IReadOnlyList<NetSummaryRow> NetSummary { get; init; } = [];
    /// <summary>그래프.</summary>
    public required GraphDto Graph { get; init; }
    /// <summary>경고. 설계는 원소 형식을 정하지 않았다. 분석 경고와 같은 Diagnostic으로 둔다(열린 질문 Q18).</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; init; } = [];
    /// <summary>수집 범위 정보.</summary>
    public required CaptureInfo Capture { get; init; }
    /// <summary>단계 시간.</summary>
    public required TimingsMs TimingsMs { get; init; }
}

/// <summary>
/// 저장소(C14)가 보관하는 한 건 = 요약 + 행 단위 변경. 내보내기(§6.4 export) 파일 본문이고, 평가 하네스가 M1·M2를 계산하는 입력이다.
/// 설계는 내보내기 형식을 정하지 않았다(열린 질문 Q19).
/// </summary>
public sealed record PreviewRecord
{
    /// <summary>요약(API 응답과 같음).</summary>
    public required PreviewResult Result { get; init; }
    /// <summary>행 변경 전부(view=event).</summary>
    public IReadOnlyList<RowChange> RowChanges { get; init; } = [];
    /// <summary>최종 차이 전부(view=net, ExplainedBy 채워짐).</summary>
    public IReadOnlyList<NetChange> NetChanges { get; init; } = [];
    /// <summary>원문 SQL([적용] 재실행용, C15). API 응답에는 나가지 않는다.</summary>
    public required string Sql { get; init; }
}

/// <summary>미리보기 파이프라인 결과. 거부면 Rejected만, 아니면 Record만 채워진다.</summary>
public sealed record PreviewOutcome
{
    /// <summary>분석 결과(항상).</summary>
    public required AnalysisResult Analysis { get; init; }
    /// <summary>거부면 null(저장하지 않음, 422).</summary>
    public PreviewRecord? Record { get; init; }
    /// <summary>거부 여부.</summary>
    public bool IsRejected => Record is null;
}

/// <summary>
/// 미리보기 파이프라인(§5.2): 잠금 → (오염이면 재구축) → C6 → C10(+C12, 평가 훅) → C11 → C13 → 요약 조립 → C14 저장. API(C5)와 평가 하네스(C16 CLI)가 함께 쓴다.
/// 설계는 이 조립 위치를 정하지 않았다(§5.2 그림에서는 API가 차례로 부른다). Preview 프로젝트가 구현한다(아키텍처 결정 AD3).
/// </summary>
public interface IPreviewPipeline
{
    /// <summary>미리보기 한 건. 거부면 저장하지 않고 PreviewOutcome.Record = null. 지문 불일치·잠금 충돌은 InvalidOperationException.</summary>
    Task<PreviewOutcome> RunAsync(string sql, PreviewOptions options, IEvaluationHook? hook, CancellationToken ct);
}
