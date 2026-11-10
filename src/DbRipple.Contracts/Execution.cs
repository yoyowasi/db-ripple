using DbRipple.Core.Capture;

namespace DbRipple.Contracts;

/// <summary>미리보기 요청 옵션(§6.4 POST /api/previews `options`).</summary>
public sealed record PreviewOptions
{
    /// <summary>D8 기본 이미지 상한.</summary>
    public const long DefaultImageCap = 100_000;

    /// <summary>행 이미지 상한(D8, UPDATE 한 행 = 2). null이면 서버 기본값.</summary>
    public long? ImageCap { get; init; }
}

/// <summary>실행기 서버 설정(요청이 아니라 배포 구성). 설계 외 묶음: 각 항목 근거는 주석의 절.</summary>
public sealed record ExecutionSettings
{
    /// <summary>
    /// 사용자 문장 구간의 EXECUTE AS USER 대상 DB 사용자(§0, §5.2 3, A18, E19). null이면 가장하지 않는다(테스트·평가 전용으로만 허용할지는 열린 질문 Q12).
    /// </summary>
    public string? ExecuteAsUser { get; init; }
    /// <summary>문장별 CommandTimeout 초(E20).</summary>
    public int CommandTimeoutSeconds { get; init; } = 60;
    /// <summary>기본 이미지 상한(D8).</summary>
    public long DefaultImageCap { get; init; } = PreviewOptions.DefaultImageCap;
    /// <summary>평가 모드: 최종 차이를 전체 사용자 테이블에 대해 계산하고(D3 "평가 모드에서는 모든 테이블"), 평가 훅을 부른다(§10.3).</summary>
    public bool EvaluationMode { get; init; }
}

/// <summary>audit.AuditHeader 한 행 그대로(§6.2 감사 저장소). C10이 읽고 C11이 해석한다.</summary>
public sealed record AuditHeaderRow
{
    /// <summary>captureSeq(audit.CaptureSeq SEQUENCE).</summary>
    public required long CaptureSeq { get; init; }
    /// <summary>tableObjectId.</summary>
    public required int TableObjectId { get; init; }
    /// <summary>tableName 원문(대괄호 인용형, 예 "[Sales].[SalesOrderDetail]").</summary>
    public required string TableName { get; init; }
    /// <summary>operation('I','U','D'를 열거형으로).</summary>
    public required ChangeOperation Operation { get; init; }
    /// <summary>inserted의 COUNT_BIG(*).</summary>
    public required long InsertedCount { get; init; }
    /// <summary>deleted의 COUNT_BIG(*).</summary>
    public required long DeletedCount { get; init; }
    /// <summary>TRIGGER_NESTLEVEL().</summary>
    public required int NestLevel { get; init; }
    /// <summary>@@TRANCOUNT(BEGIN TRAN 안의 문장이면 2가 정상, §6.2 v1.3).</summary>
    public required int TranCount { get; init; }
    /// <summary>imageMode.</summary>
    public required ImageMode ImageMode { get; init; }
}

/// <summary>audit.AuditRowImage 한 행 그대로(§6.2). JSON은 해석하지 않은 원문이다(숫자 원문 보존은 C11이 §6.1대로).</summary>
public sealed record AuditRowImageRow
{
    /// <summary>헤더 captureSeq.</summary>
    public required long CaptureSeq { get; init; }
    /// <summary>DELETED 또는 INSERTED.</summary>
    public required ImageKind ImageKind { get; init; }
    /// <summary>묶음 안 일련번호(순서 의미 없음).</summary>
    public required int Ordinal { get; init; }
    /// <summary>키 열 JSON. 키 없는 테이블은 null.</summary>
    public string? KeyJson { get; init; }
    /// <summary>행 전체 JSON. KEYS 모드면 null.</summary>
    public string? RowJson { get; init; }
}

/// <summary>
/// 롤백 전 재조회 한 행(D3 대조 1, §5.2): 수집된 키의 현재 행. 값 식은 감사 트리거와 같은 Core AuditValueExpressions로 FOR JSON해 감사 rowJson과 같은 형식이다.
/// </summary>
public sealed record RereadRow
{
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>조회한 키 JSON(감사 keyJson과 같은 형식).</summary>
    public required string KeyJson { get; init; }
    /// <summary>현재 행 JSON. 행이 없으면(그 뒤 삭제됨) null.</summary>
    public string? RowJson { get; init; }
}

/// <summary>문장 1개의 실행 기록(C10). 문장 결과 + 문장 나누기(§7.2 1단계)에 쓰는 경계.</summary>
public sealed record StatementExecution
{
    /// <summary>API로 나가는 문장 결과.</summary>
    public required StatementResult Result { get; init; }
    /// <summary>REVERT 뒤 읽은 수집 순번 최댓값(이 문장 경계). 실행하지 않았으면 null.</summary>
    public long? Boundary { get; init; }
    /// <summary>이 문장 뒤 누적 이미지 수(rowJson 있는 행만, §6.2 상한 셈법).</summary>
    public long ImageCountAfter { get; init; }
    /// <summary>이 문장 뒤 제어 테이블 모드(상한을 넘으면 KEYS로 바꿈, §5.2 5).</summary>
    public ImageMode ModeAfter { get; init; }
}

/// <summary>수집 범위 정보(§6.2 PreviewResult.capture).</summary>
public sealed record CaptureInfo
{
    /// <summary>누적 이미지 수.</summary>
    public required long ImageCount { get; init; }
    /// <summary>이미지 상한.</summary>
    public required long Cap { get; init; }
    /// <summary>상한을 넘어 KEYS로 바뀌었는지.</summary>
    public required bool Truncated { get; init; }
    /// <summary>매니페스트 버전(CaptureManifest.Version).</summary>
    public required string ManifestVersion { get; init; }
}

/// <summary>
/// C10 결과(§4 `Execute(analysis)` → `RawCapture`). 롤백 전에 읽은 원자료만 담는다(§5.2 6). 실패 시 롤백 뒤 검사 결과는 ResidualCommitted.
/// </summary>
public sealed record RawCapture
{
    /// <summary>미리보기 상태(ok, failed, trigger_blocked, truncated).</summary>
    public required PreviewStatus Status { get; init; }
    /// <summary>문장별 실행 기록(분석 순서, 실행 안 한 문장은 not_run).</summary>
    public required IReadOnlyList<StatementExecution> Executions { get; init; }
    /// <summary>BEGIN TRAN 직후 읽은 수집 순번 최댓값 = 경계 0(§7.2 1단계). 감사 기록이 없으면 SEQUENCE 현재값 기준(구현 결정은 C10).</summary>
    public required long Boundary0 { get; init; }
    /// <summary>(경계 0, 마지막 경계] 범위의 감사 헤더. 실패로 트랜잭션이 끝났으면 빈 배열(감사 기록도 롤백됨, §5.2 4).</summary>
    public IReadOnlyList<AuditHeaderRow> Headers { get; init; } = [];
    /// <summary>위 헤더의 행 이미지.</summary>
    public IReadOnlyList<AuditRowImageRow> Images { get; init; } = [];
    /// <summary>수집된 키의 재조회(키 있는 테이블만).</summary>
    public IReadOnlyList<RereadRow> Rereads { get; init; } = [];
    /// <summary>롤백 전 같은 연결에서 계산한 최종 차이(C12). 범위: "감사 기록에 나온 테이블 ∪ 제외 테이블", 평가 모드면 전체 사용자 테이블(D3 대조 2). 실패면 빈 배열.</summary>
    public IReadOnlyList<NetChange> NetChanges { get; init; } = [];
    /// <summary>실패·거부 뒤 트랜잭션 밖에서 확정된 변경(롤백 뒤 전체 사용자 테이블 대 기준 스냅샷, §5.2 4, §6.2 residualCommitted).</summary>
    public IReadOnlyList<NetChange> ResidualCommitted { get; init; } = [];
    /// <summary>ResidualCommitted가 있어 샌드박스를 오염으로 표시해야 함(§5.2 4). 표시 자체는 파이프라인이 ISandboxManager.MarkDirty로 한다.</summary>
    public required bool SandboxDirty { get; init; }
    /// <summary>수집 범위 정보.</summary>
    public required CaptureInfo Capture { get; init; }
    /// <summary>단계 시간(ms): execute, readLog, reread, netDiff(§10.4 M5).</summary>
    public required ExecutionTimings Timings { get; init; }
}

/// <summary>C10 단계 시간(ms, §6.2 timingsMs 일부).</summary>
public sealed record ExecutionTimings
{
    /// <summary>문장 실행.</summary>
    public long Execute { get; init; }
    /// <summary>헤더·행 이미지 읽기.</summary>
    public long ReadLog { get; init; }
    /// <summary>재조회.</summary>
    public long Reread { get; init; }
    /// <summary>최종 차이.</summary>
    public long NetDiff { get; init; }
}

/// <summary>
/// 평가 훅 호출 문맥(§10.3 평가 훅, §4 C10). 제품 실행기가 마지막 문장과 REVERT 뒤, ROLLBACK 전에 같은 연결로 넘긴다.
/// </summary>
public sealed record EvaluationHookContext
{
    /// <summary>열린 트랜잭션이 있는 실행 연결. 훅은 읽기만 하고 트랜잭션을 끝내지 않는다.</summary>
    public required SqlSession Session { get; init; }
    /// <summary>샌드박스 DB.</summary>
    public required string SandboxDb { get; init; }
    /// <summary>기준 스냅샷 DB.</summary>
    public required string SnapshotDb { get; init; }
    /// <summary>감사 저장소 스키마(하네스 비교에서 뺀다, §10.3). 값은 Core AuditTriggerGenerator.StoreSchema.</summary>
    public string AuditSchema { get; init; } = AuditTriggerGenerator.StoreSchema;
    /// <summary>매니페스트 notes(설치 DDL 기록 테이블 등, 하네스가 뺄 대상 판단에 쓴다).</summary>
    public IReadOnlyList<string> ManifestNotes { get; init; } = [];
    /// <summary>지금까지의 문장 결과.</summary>
    public required IReadOnlyList<StatementResult> Statements { get; init; }
}

/// <summary>
/// 평가 훅(§10.3). C16(Eval)이 구현하고 C10이 부른다. 모든 문장이 끝나고 트랜잭션이 살아 있을 때만 부른다(실패로 트랜잭션이 끝났으면 부르지 않음, 열린 질문 Q13).
/// 훅이 예외를 던져도 실행기는 반드시 ROLLBACK한다(finally). 훅 결과는 하네스가 스스로 보관한다(제품 결과에 섞지 않음).
/// </summary>
public interface IEvaluationHook
{
    /// <summary>ROLLBACK 직전 호출.</summary>
    Task OnBeforeRollbackAsync(EvaluationHookContext context, CancellationToken ct);
}

/// <summary>
/// C10 미리보기 실행기(§4 C10, §5.2). 실행 봉투: 전용 연결(풀링 끔), 고정 SET 옵션(XACT_ABORT ON, QUOTED_IDENTIFIER ON 등), 도구 소유 트랜잭션.
/// 2주차 인계 규칙(§5.2 3 v1.3, SP10): 문장 뒤 XACT_STATE·@@TRANCOUNT를 확인하고, 트랜잭션이 남아 있고 doomed(XACT_STATE = −1)이면
/// ROLLBACK한 뒤 REVERT한다(doomed 상태의 REVERT는 3930으로 실패해 가장이 남는다). 실패 문장의 IsToolFault는 ErrorClassification.IsToolFault로 판정한다.
/// 최종 차이는 생성자로 받은 INetDiffer로 같은 연결·트랜잭션에서 계산한다(Execution 프로젝트는 Diff 프로젝트를 참조하지 않는다).
/// </summary>
public interface IPreviewExecutor
{
    /// <summary>
    /// 분석 결과의 문장을 샌드박스에서 실행하고 ROLLBACK 전에 원자료를 모은다. analysis.IsAccepted가 아니면 ArgumentException.
    /// hook은 settings.EvaluationMode일 때만 부른다.
    /// </summary>
    Task<RawCapture> ExecuteAsync(AnalysisResult analysis, BaselineContext baseline, PreviewOptions options, IEvaluationHook? hook, CancellationToken ct);
}

/// <summary>
/// C12 최종 차이 계산기(§4 C12). 제품 전용이며 평가 하네스(C16)와 코드를 공유하지 않는다(§10.3).
/// </summary>
public interface INetDiffer
{
    /// <summary>
    /// §4 `Diff(connection, liveDb, snapshotDb, tables)`. session의 열린 트랜잭션 안에서(미커밋 변경을 보기 위해) liveDb와 snapshotDb의 테이블별 순 변경을 계산한다.
    /// PK(없으면 NOT NULL 유일 키, Core TableKey.Choose)로 조인하고, 키 없는 테이블은 다중집합으로 비교하고, 값은 §6.1로 정규화한다.
    /// tables는 비교할 테이블(Core TableInfo, 보통 BaselineContext.Catalog.ToCaptureInput()에서 고른 것). 감사 저장소(audit.*)는 넘기지 않는다.
    /// 결과 순서: 테이블 이름, 키 순(결정적). ExplainedBy는 null.
    /// </summary>
    Task<IReadOnlyList<NetChange>> DiffAsync(SqlSession session, string liveDb, string snapshotDb, IReadOnlyList<TableInfo> tables, CancellationToken ct);
}
