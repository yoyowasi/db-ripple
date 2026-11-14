namespace DbRipple.Contracts;

/// <summary>
/// 트리거 문맥(괄호) 판정 한 건(§7.2 2단계, 잠정). 설계 외 내부 경계 타입: C11이 §7.2 1·2단계를 맡고(최종 after를 전위 순서로 정하려면 이 트리가 필요하다, §7.2 6단계 v1.3)
/// C13이 이것을 받아 3단계(FK 우선)~7단계를 한다. API로 나가지 않는다(아키텍처 결정 AD4).
/// </summary>
public sealed record TriggerContextLink
{
    /// <summary>깊이 d+1 자식 묶음 id.</summary>
    public required string ChildFiringId { get; init; }
    /// <summary>
    /// 부모 후보 묶음 id(깊이 d). 보통 1개. §7.2 2단계 v1.3 보완(감사 순서 NONE 뒤에 기록된 중첩 묶음)이면 {F, 뒤 형제 G}처럼 여럿.
    /// 빈 배열이면 같은 문장 안에 깊이 d 부모가 없다(INSTEAD OF 가상 부모 후보, §7.2 5단계).
    /// </summary>
    public IReadOnlyList<string> CandidateParentFiringIds { get; init; } = [];
    /// <summary>이 판정의 확신도 상한(§7.2 2단계: 부모의 감사 순서 NONE, 뒤 형제 → ambiguous). 상한이 없으면 inferred.</summary>
    public required Confidence ConfidenceCap { get; init; }
}

/// <summary>
/// 재조회 값이 감사 after와 다른 열(§7.2 6단계). 설계 외 내부 경계 타입: C11이 RowChange.after를 재조회 값으로 고치고(REREAD), C13이 이것으로 Residual을 만든다(rowChangeId 연결).
/// </summary>
public sealed record RereadDifference
{
    /// <summary>after를 고친 행 변경 id(전위 순서 마지막 묶음의 행).</summary>
    public required string RowChangeId { get; init; }
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>행 키.</summary>
    public required RowKey Key { get; init; }
    /// <summary>값이 다른 열.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>
    /// 마지막 묶음을 정할 수 없어(형제 순서 observed_only) 재조회 값을 최종으로 쓴 경우 true(§7.2 6단계 v1.3 끝 문장).
    /// 이때 Columns는 그 행의 어떤 묶음 after와도 맞지 않는 열만 담는다(열린 질문 Q14).
    /// </summary>
    public bool OrderUndetermined { get; init; }
}

/// <summary>C11 결과(§4 `Collect(raw)` → `Firing[]`, `RowChange[]`) + C13에 넘길 내부 판정.</summary>
public sealed record CollectResult
{
    /// <summary>묶음(captureSeq 순). Cause·OrderGuarantee는 null.</summary>
    public IReadOnlyList<Firing> Firings { get; init; } = [];
    /// <summary>행 변경. ParentRowIds는 빈 배열.</summary>
    public IReadOnlyList<RowChange> RowChanges { get; init; } = [];
    /// <summary>§7.2 2단계 괄호 판정(깊이 2 이상 묶음마다 1개).</summary>
    public IReadOnlyList<TriggerContextLink> ContextLinks { get; init; } = [];
    /// <summary>§7.2 6단계 재조회 차이.</summary>
    public IReadOnlyList<RereadDifference> RereadDifferences { get; init; } = [];
    /// <summary>문장 시작 경계와 실제 첫 감사 헤더를 대조한 직접 DELETE 근거(0행 발동 포함).</summary>
    public IReadOnlyList<ProcedureEntryDelete> ProcedureEntryDeletes { get; init; } = [];
}

/// <summary>수집기가 루트 프로시저의 첫 독립 DELETE로 확인한 실제 발동.</summary>
/// <param name="StatementIndex">EXEC 문장 번호.</param>
/// <param name="FiringId">첫 감사 헤더에서 만든 발동 ID.</param>
/// <param name="TargetObjectId">헤더의 테이블 개체 ID.</param>
/// <param name="Operation">헤더의 연산(DELETE).</param>
public sealed record ProcedureEntryDelete(int StatementIndex, string FiringId, int TargetObjectId, ChangeOperation Operation);

/// <summary>
/// C11 변경 수집기(§4 C11). 헤더·행 이미지로 묶음을 만들고, 문장을 나누고(§7.2 1단계), 괄호를 판정하고(2단계), UPDATE 전후를 짝짓고, 바뀐 열을 값으로 계산하고,
/// 비결정·파생 열을 표시하고, §6.1로 값을 정규화하고, 재조회를 반영한다.
/// 2주차 인계 규칙(§7.2 6단계 v1.3): 재조회와 비교할 "감사 after"는 captureSeq 최댓값 묶음이 아니라 트리거 문맥 트리를 전위 순서로 훑어 그 행을 마지막으로 바꾼 묶음의 after다.
/// </summary>
public interface IChangeCollector
{
    /// <summary>
    /// §4 `Collect(raw)`. analysis는 비결정 함수 열(STATEMENT_FUNCTION), baseline은 매니페스트(auditOrder·비결정·수집 불가 열)·카탈로그(키·계산 열·테이블 이름)·트리거 분석(TRIGGER_GETDATE)을 위해 받는다.
    /// DB에 접근하지 않는다(순수 함수).
    /// </summary>
    CollectResult Collect(RawCapture raw, AnalysisResult analysis, BaselineContext baseline);
}
