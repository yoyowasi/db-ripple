namespace DbRipple.Contracts;

/// <summary>문장 실행 오류(§6.2 Statement.error). 설계는 필드를 정하지 않았다. 오류 노드(§6.3 error: errorNumber, message, isToolFault)와 D3 규칙 3에 필요한 값만 둔다(열린 질문 Q10).</summary>
public sealed record StatementError
{
    /// <summary>SQL Server 오류 번호(SqlError.Number). 여러 개면 첫 오류(대표). 3609는 "트리거 안에서 트랜잭션이 끝남"(A22).</summary>
    public required int ErrorNumber { get; init; }
    /// <summary>오류 메시지(서버 메시지 그대로, 연결 문자열 같은 비밀값은 넣지 않는다).</summary>
    public required string Message { get; init; }
    /// <summary>오류 프로시저(SqlError.Procedure). 트리거 안 오류면 트리거 이름.</summary>
    public string? Procedure { get; init; }
    /// <summary>수집 장치 오류(ErrorClassification.IsToolFault(Procedure), D3 규칙 3 v1.3, R18).</summary>
    public required bool IsToolFault { get; init; }
}

/// <summary>문장 결과(§6.2 Statement). C10이 만든다.</summary>
public sealed record StatementResult
{
    /// <summary>문장 전역 번호(1부터).</summary>
    public required int Index { get; init; }
    /// <summary>GO 배치 번호.</summary>
    public required int Batch { get; init; }
    /// <summary>배치 안 순번.</summary>
    public required int Position { get; init; }
    /// <summary>INSERT/UPDATE/DELETE, 저장 프로시저 호출이면 EXEC.</summary>
    public required ChangeOperation Kind { get; init; }
    /// <summary>직접 대상 "스키마.테이블". EXEC면 null.</summary>
    public required string? Target { get; init; }
    /// <summary>스크립트 전체 기준 위치.</summary>
    public required SourceSpan Span { get; init; }
    /// <summary>ok, failed, trigger_blocked, not_run.</summary>
    public required StatementStatus Status { get; init; }
    /// <summary>드라이버 영향 행 수(SqlCommand 결과, A9·SP2(c)). not_run·실패면 null. EXEC는 늘 null(A9: EXECUTE는 이전 @@ROWCOUNT를 유지).</summary>
    public long? RowsAffected { get; init; }
    /// <summary>EXEC: 부른 프로시저 "스키마.이름"(AnalyzedStatement.Procedure 그대로, 2026-11-13). DML이면 JSON에서 빠진다(기존 픽스처·계약 모양 유지).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Procedure { get; init; }
    /// <summary>EXEC: 프로시저 반환 값(RETURN). 그 밖·실패면 null.</summary>
    public int? ReturnValue { get; init; }
    /// <summary>EXEC: 프로시저가 낸 결과 집합 수(내용은 버린다). 그 밖·실패면 null.</summary>
    public int? ResultSetCount { get; init; }
    /// <summary>EXEC 귀속 재료(AnalyzedStatement.Plan 그대로). JSON에는 내보내지 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ProcedurePlan? Plan { get; init; }
    /// <summary>
    /// [이 문장 묶음의 최소 captureSeq, 최대 captureSeq](§6.2 captureSeqRange, 2원소 배열). 묶음이 없으면 null.
    /// 문장 귀속 자체는 경계(이전 문장 경계, 이 문장 경계]로 한다(§7.2 1단계, RawCapture.Executions).
    /// </summary>
    public IReadOnlyList<long>? CaptureSeqRange { get; init; }
    /// <summary>오류. 성공이면 null.</summary>
    public StatementError? Error { get; init; }
}

/// <summary>FK 원인 정보(§6.2 cause.foreignKey).</summary>
public sealed record ForeignKeyCause
{
    /// <summary>FK 이름(스키마 없이 sys.foreign_keys.name, 예시 "FK_SalesOrderDetail_SalesOrderHeader_SalesOrderID").</summary>
    public required string Name { get; init; }
    /// <summary>부모(참조되는) 테이블 "스키마.테이블".</summary>
    public required string Parent { get; init; }
    /// <summary>자식(참조하는) 테이블 "스키마.테이블".</summary>
    public required string Child { get; init; }
    /// <summary>CASCADE, SET_NULL, SET_DEFAULT.</summary>
    public required FkAction Action { get; init; }
    /// <summary>DELETE 또는 UPDATE.</summary>
    public required FkEvent Event { get; init; }
    /// <summary>키 값이 일치한 자식 행 수(§7.2 3단계).</summary>
    public required long MatchedRows { get; init; }
    /// <summary>일치하지 않은 자식 행 수(그 행은 4단계로 넘김).</summary>
    public required long UnmatchedRows { get; init; }
}

/// <summary>묶음의 원인(§6.2 Firing.cause, §7.2·§7.3). C13이 채운다.</summary>
public sealed record Cause
{
    /// <summary>원인 종류.</summary>
    public required CauseKind Kind { get; init; }
    /// <summary>확신도. 트리거 귀속은 최대 inferred(§7.3).</summary>
    public required Confidence Confidence { get; init; }
    /// <summary>부모 묶음 id. DIRECT·INSTEAD OF 가상 부모·UNEXPLAINED면 null.</summary>
    public string? ParentFiringId { get; init; }
    /// <summary>FK 원인이면 FK 정보, 아니면 null.</summary>
    public ForeignKeyCause? ForeignKey { get; init; }
    /// <summary>트리거 후보 "스키마.트리거"(TRIGGER·INSTEAD_OF, ambiguous면 여럿).</summary>
    public IReadOnlyList<string> TriggerCandidates { get; init; } = [];
    /// <summary>근거.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}

/// <summary>
/// 변경 묶음 = 감사 트리거 발동 1회(§6.2 Firing). C11이 감사 헤더로 만들고(Cause·OrderGuarantee는 null), C13이 `with`로 Cause·OrderGuarantee를 채운다.
/// API로 나가는 PreviewResult의 묶음은 Cause·OrderGuarantee가 null이 아니어야 한다.
/// </summary>
public sealed record Firing
{
    /// <summary>"f-{captureSeq}"(ContractIds.Firing).</summary>
    public required string Id { get; init; }
    /// <summary>수집 발동 순번. 감사 트리거가 기록한 순서일 뿐 행이 바뀐 순서가 아니다(A1, A2).</summary>
    public required long CaptureSeq { get; init; }
    /// <summary>문장 번호(§7.2 1단계: captureSeq가 (이전 경계, 이 경계]).</summary>
    public required int StatementIndex { get; init; }
    /// <summary>TRIGGER_NESTLEVEL() 깊이(사용자 문장 1, H2).</summary>
    public required int Depth { get; init; }
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>연산.</summary>
    public required ChangeOperation Operation { get; init; }
    /// <summary>발동 행 수(INSERT·UPDATE는 insertedCount, DELETE는 deletedCount, §6.2 마지막 문장 규칙). 0이면 0행 발동(E23).</summary>
    public required long RowCount { get; init; }
    /// <summary>FULL 또는 KEYS(헤더 imageMode).</summary>
    public required ImageMode ImageMode { get; init; }
    /// <summary>그 테이블·연산의 감사 트리거 Last 여부(매니페스트 auditOrder).</summary>
    public required AuditOrder AuditOrder { get; init; }
    /// <summary>원인(C13). C11 출력에서는 null.</summary>
    public Cause? Cause { get; init; }
    /// <summary>부모 → 자식 순서 보장(C13). C11 출력에서는 null.</summary>
    public OrderGuarantee? OrderGuarantee { get; init; }
}

/// <summary>비결정 셀(§6.2 RowChange.volatile[]). 그리드 "예상값", 적용 대조·M1은 존재·형식만 비교(E1).</summary>
public sealed record VolatileCell
{
    /// <summary>열 이름.</summary>
    public required string Column { get; init; }
    /// <summary>사유.</summary>
    public required VolatileReason Reason { get; init; }
}

/// <summary>행 변경 = 묶음 안 행 하나(§6.2 RowChange). C11이 만들고 C13이 ParentRowIds를 채운다.</summary>
public sealed record RowChange
{
    /// <summary>"rc-{captureSeq}-{n}"(ContractIds.RowChange).</summary>
    public required string Id { get; init; }
    /// <summary>소속 묶음 id.</summary>
    public required string FiringId { get; init; }
    /// <summary>연산(묶음과 같음).</summary>
    public required ChangeOperation Operation { get; init; }
    /// <summary>행 키. PK가 바뀐 짝지은 UPDATE에서 어느 쪽 키를 쓰는지는 열린 질문 Q11.</summary>
    public required RowKey Key { get; init; }
    /// <summary>before 행(§6.1 문자열). INSERT면 null. KEYS 모드면 null.</summary>
    public IReadOnlyDictionary<string, string?>? Before { get; init; }
    /// <summary>after 행. DELETE면 null. KEYS 모드면 null. 최종 after가 재조회로 바뀌면 그 값(FinalAfterSource = REREAD).</summary>
    public IReadOnlyDictionary<string, string?>? After { get; init; }
    /// <summary>값 비교로 계산한 바뀐 열(A12: UPDATE()·COLUMNS_UPDATED()가 아님). INSERT·DELETE면 빈 배열.</summary>
    public IReadOnlyList<string> ChangedColumns { get; init; } = [];
    /// <summary>비결정 셀.</summary>
    public IReadOnlyList<VolatileCell> Volatile { get; init; } = [];
    /// <summary>계산 열("파생", E4).</summary>
    public IReadOnlyList<string> Derived { get; init; } = [];
    /// <summary>UPDATE 짝짓기. INSERT·DELETE의 값은 열린 질문 Q11(제안: 한 행이면 SINGLE_ROW, 아니면 키 종류에 따라 BY_PK/BY_UNIQUE).</summary>
    public required Pairing Pairing { get; init; }
    /// <summary>최종 after 출처(§7.2 6단계 v1.3: 트리거 문맥 트리를 전위 순서로 훑어 그 행을 마지막으로 바꾼 묶음의 after를 재조회와 비교).</summary>
    public required FinalAfterSource FinalAfterSource { get; init; }
    /// <summary>행 단위로 부모가 확정되는 경우(FK 연쇄)의 부모 행 변경 id(C13).</summary>
    public IReadOnlyList<string> ParentRowIds { get; init; } = [];
}

/// <summary>잔차 = 감사 기록으로 설명되지 않는 최종 변경(§6.2 Residual). C13이 만든다.</summary>
public sealed record Residual
{
    /// <summary>"r-{n}".</summary>
    public required string Id { get; init; }
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>행 키.</summary>
    public required RowKey Key { get; init; }
    /// <summary>설명되지 않은 열.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>찾은 경로.</summary>
    public required ResidualDetectedBy DetectedBy { get; init; }
    /// <summary>후보 원인(트리거 "스키마.트리거" 등).</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];
    /// <summary>범주(§7.2 6단계 순서).</summary>
    public required ResidualCategory Category { get; init; }
    /// <summary>확신도.</summary>
    public required Confidence Confidence { get; init; }
    /// <summary>재조회로 after를 고친 행 변경 id. 감사 기록에 그 행이 없으면 null.</summary>
    public string? RowChangeId { get; init; }
}

/// <summary>최종 차이 한 건의 설명 연결(§6.2 NetChange.explainedBy). C13이 채운다.</summary>
public sealed record ExplainedBy
{
    /// <summary>이 순 변경을 만든 묶음 id.</summary>
    public IReadOnlyList<string> FiringIds { get; init; } = [];
    /// <summary>잔차 id. 없으면 null.</summary>
    public string? ResidualId { get; init; }
}

/// <summary>최종 차이 한 건(§6.2 NetChange). C12가 만들고(ExplainedBy = null) C13이 ExplainedBy를 채운다.</summary>
public sealed record NetChange
{
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>키. 키 없는 테이블은 Kind = ROW.</summary>
    public required RowKey Key { get; init; }
    /// <summary>INSERTED, DELETED, UPDATED, MULTISET_REMOVED, MULTISET_ADDED. PK가 바뀐 UPDATE는 옛 키 DELETED + 새 키 INSERTED(PK 조인).</summary>
    public required NetChangeKind Change { get; init; }
    /// <summary>같은 행의 개수. 키 있는 테이블은 1, 다중집합이면 늘거나 준 개수(E6).</summary>
    public required long Multiplicity { get; init; }
    /// <summary>before 행(§6.1). INSERTED·MULTISET_ADDED면 null. text/ntext/image 열도 여기서는 비교·표시한다(§6.1).</summary>
    public IReadOnlyDictionary<string, string?>? Before { get; init; }
    /// <summary>after 행. DELETED·MULTISET_REMOVED면 null.</summary>
    public IReadOnlyDictionary<string, string?>? After { get; init; }
    /// <summary>바뀐 열(UPDATED만).</summary>
    public IReadOnlyList<string> ChangedColumns { get; init; } = [];
    /// <summary>설명 연결(C13). C12 출력에서는 null.</summary>
    public ExplainedBy? ExplainedBy { get; init; }
}
