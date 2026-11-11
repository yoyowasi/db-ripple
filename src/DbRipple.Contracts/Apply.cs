using System.Text.Json.Serialization;

namespace DbRipple.Contracts;

// [적용](C15) 계약: §5.3 절차, §6.4 ApplyResult, D7 일치 규칙, D8(잘린 결과는 적용 차단), E2(IDENTITY 짝 매핑).
// 2026-11-11 3주차 계약 추가(비파괴, 추가만). 설계 §6.4 예시에 없는 필드는 주석에 "설계 외(Qnn)"로 적었다. 기본값은 docs/week3-plan.md의 열린 질문 표를 따른다.

/// <summary>[적용] 결과 상태(§6.4 ApplyResult.status).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplyStatus>))]
public enum ApplyStatus
{
    /// <summary>검증 게이트 통과 후 COMMIT.</summary>
    [JsonStringEnumMemberName("committed")] Committed,
    /// <summary>커밋 전 최종 차이 대조가 미리보기와 달라 ROLLBACK(§5.3, D7).</summary>
    [JsonStringEnumMemberName("rolled_back_mismatch")] RolledBackMismatch,
    /// <summary>미리보기의 before 행이 원본에서 바뀌어 실행하지 않고 ROLLBACK(§5.3 드리프트 검사).</summary>
    [JsonStringEnumMemberName("drift_detected")] DriftDetected,
    /// <summary>선행 조건 위반(§5.3). 원본에 아무것도 실행하지 않았다. 어긴 조건은 ApplyResult.PreconditionFailures.</summary>
    [JsonStringEnumMemberName("precondition_failed")] PreconditionFailed,
    /// <summary>원본 실행 중 오류·트리거 거부(§5.3 "트리거가 거부하면 … 상태는 error"). 트랜잭션은 이미 끝났거나 ROLLBACK했다.</summary>
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>검증 범위(§6.4 verification.mode, D7 검증 범위).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationMode>))]
public enum VerificationMode
{
    /// <summary>원본 스냅샷(AW_Origin_pre) 대 원본, 전체 사용자 테이블 최종 차이(§5.3 기본 경로).</summary>
    [JsonStringEnumMemberName("SNAPSHOT_NETDIFF")] SnapshotNetdiff,
    /// <summary>원본 스냅샷을 만들 수 없을 때의 대체 경로(§5.3, A14): 테이블 건수 변화 + 미리보기 키 집합 after 비교. 행 수가 그대로인 UPDATE는 놓칠 수 있다.</summary>
    [JsonStringEnumMemberName("KEYSET")] Keyset,
}

/// <summary>
/// [적용] 선행 조건(§5.3 "하나라도 어기면 precondition_failed"). 설계는 조건만 적고 코드 값은 정하지 않았다(설계 외, Q28). 앞 넷이 §5.3 목록이고(PREVIEW_NOT_OK에 D8 truncated 차단 포함), 마지막은 Q30에서 나온 추가 조건이다.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplyPrecondition>))]
public enum ApplyPrecondition
{
    /// <summary>미리보기 상태가 ok가 아님(failed, trigger_blocked, truncated는 적용 불가. truncated 차단은 D8).</summary>
    [JsonStringEnumMemberName("PREVIEW_NOT_OK")] PreviewNotOk,
    /// <summary>저장된 원문 SQL의 해시가 PreviewResult.sqlHash와 다르거나, 요청의 sqlHash와 다름.</summary>
    [JsonStringEnumMemberName("SQL_HASH_MISMATCH")] SqlHashMismatch,
    /// <summary>미리보기를 만든 지 설정 시간(기본 30분, IPreviewStore.DefaultExpiry)이 지남. 저장소가 이미 지운 건은 404다.</summary>
    [JsonStringEnumMemberName("PREVIEW_EXPIRED")] PreviewExpired,
    /// <summary>원본과 샌드박스의 환경 지문이 다름(§5.1 6, §5.3).</summary>
    [JsonStringEnumMemberName("FINGERPRINT_MISMATCH")] FingerprintMismatch,
    /// <summary>원본에 EXECUTE AS 대상 사용자가 없음. 원본에 DDL을 하지 않으므로 만들지 않고 막는다(설계 외, Q30).</summary>
    [JsonStringEnumMemberName("EXECUTE_AS_USER_MISSING")] ExecuteAsUserMissing,
}

/// <summary>드리프트 종류(§5.3 드리프트 검사). 설계는 driftRows 원소를 정하지 않았다(설계 외, Q24).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DriftKind>))]
public enum DriftKind
{
    /// <summary>같은 키 행의 값이 미리보기 before와 다름.</summary>
    [JsonStringEnumMemberName("CHANGED")] Changed,
    /// <summary>미리보기 before에 있던 키 행이 원본에 없음.</summary>
    [JsonStringEnumMemberName("MISSING")] Missing,
    /// <summary>키 없는 테이블(ROW): 같은 모든 열 묶음의 개수가 미리보기 before와 다름(E6).</summary>
    [JsonStringEnumMemberName("MULTISET_COUNT")] MultisetCount,
}

/// <summary>D7 일치 규칙 위반 종류(§6.4 verification.mismatches 원소). 설계는 원소를 정하지 않았다(설계 외, Q24). 규칙 순서는 D7 목록 순서다.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MismatchKind>))]
public enum MismatchKind
{
    /// <summary>D7 1: 문장별 직접 대상 행 수가 다름.</summary>
    [JsonStringEnumMemberName("STATEMENT_ROW_COUNT")] StatementRowCount,
    /// <summary>D7 2: 테이블별 netSummary 건수가 다름(Expected/Actual은 "inserted,updated,deleted,multisetRemoved,multisetAdded").</summary>
    [JsonStringEnumMemberName("NET_SUMMARY")] NetSummary,
    /// <summary>D7 3: PK·UNIQUE 키 행의 비결정 열을 뺀 값이 다름(열 하나당 1건).</summary>
    [JsonStringEnumMemberName("ROW_VALUE")] RowValue,
    /// <summary>미리보기 최종 차이에 있던 행 변경이 실제에 없음.</summary>
    [JsonStringEnumMemberName("ROW_MISSING")] RowMissing,
    /// <summary>실제에만 있는 행 변경(미리보기에 있는 테이블).</summary>
    [JsonStringEnumMemberName("ROW_UNEXPECTED")] RowUnexpected,
    /// <summary>D7 4: 새 IDENTITY 행을 비결정 열을 뺀 값으로 짝지을 수 없음(짝 없음 또는 후보 여럿).</summary>
    [JsonStringEnumMemberName("IDENTITY_UNPAIRED")] IdentityUnpaired,
    /// <summary>D7 5: 미리보기에 없는 테이블에 변경이 있음.</summary>
    [JsonStringEnumMemberName("UNEXPECTED_TABLE")] UnexpectedTable,
    /// <summary>D7 6: 비결정 열의 존재(NULL 여부)·형식이 다름.</summary>
    [JsonStringEnumMemberName("VOLATILE_FORMAT")] VolatileFormat,
    /// <summary>KEYSET(§5.3): 테이블 COUNT_BIG(*) 변화가 미리보기 netSummary와 다름.</summary>
    [JsonStringEnumMemberName("KEYSET_TABLE_COUNT")] KeysetTableCount,
}

/// <summary>
/// POST /api/previews/{id}/apply 요청 본문(§6.4는 "—"(본문 없음)). 본문은 선택이다(설계 외, Q24).
/// 프런트는 화면에 보이는 미리보기의 sqlHash를 보내, 편집기 내용이 바뀐 뒤 옛 미리보기를 적용하는 일을 막는다(§5.3 "SQL 해시가 미리보기와 같다").
/// </summary>
public sealed record ApplyRequest
{
    /// <summary>"sha256:…"(PreviewResult.sqlHash 형식). null이면 저장된 원문과 PreviewResult.sqlHash만 대조한다.</summary>
    public string? SqlHash { get; init; }
}

/// <summary>문장별 직접 대상 행 수 대조(§6.4 verification.statementRowCounts 원소 `{ index, preview, actual }`).</summary>
public sealed record StatementRowCount
{
    /// <summary>문장 번호.</summary>
    public required int Index { get; init; }
    /// <summary>미리보기 StatementResult.rowsAffected.</summary>
    public long? Preview { get; init; }
    /// <summary>원본 실행 영향 행 수. 실행하지 않았거나 실패면 null.</summary>
    public long? Actual { get; init; }
}

/// <summary>드리프트 행 한 건(§6.4 verification.driftRows 원소, 설계 외 Q24). 미리보기 최종 차이의 before(기준선 값)를 원본 현재 값과 비교한 결과.</summary>
public sealed record DriftRow
{
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>행 키(ROW면 Columns·Values 빈 배열).</summary>
    public required RowKey Key { get; init; }
    /// <summary>드리프트 종류.</summary>
    public required DriftKind Kind { get; init; }
    /// <summary>값이 다른 열(CHANGED만, 그 밖은 빈 배열).</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>미리보기 before 행(§6.1 문자열).</summary>
    public IReadOnlyDictionary<string, string?>? Expected { get; init; }
    /// <summary>원본 현재 행. MISSING이면 null.</summary>
    public IReadOnlyDictionary<string, string?>? Actual { get; init; }
    /// <summary>MULTISET_COUNT: 미리보기 before의 같은 행 개수. 그 밖은 null.</summary>
    public long? ExpectedMultiplicity { get; init; }
    /// <summary>MULTISET_COUNT: 원본 현재의 같은 행 개수. 그 밖은 null.</summary>
    public long? ActualMultiplicity { get; init; }
}

/// <summary>D7 일치 규칙 위반 한 건(§6.4 verification.mismatches 원소, 설계 외 Q24). 값은 §6.1 문자열.</summary>
public sealed record ApplyMismatch
{
    /// <summary>위반 종류.</summary>
    public required MismatchKind Kind { get; init; }
    /// <summary>STATEMENT_ROW_COUNT의 문장 번호. 그 밖은 null.</summary>
    public int? StatementIndex { get; init; }
    /// <summary>"스키마.테이블". STATEMENT_ROW_COUNT면 null.</summary>
    public string? Table { get; init; }
    /// <summary>행 키(ROW_*, IDENTITY_UNPAIRED, VOLATILE_FORMAT). 그 밖은 null. IDENTITY 열은 짝 매핑 뒤의 미리보기 쪽 키.</summary>
    public RowKey? Key { get; init; }
    /// <summary>열 이름(ROW_VALUE, VOLATILE_FORMAT). 그 밖은 null.</summary>
    public string? Column { get; init; }
    /// <summary>미리보기 값(행 수·건수는 10진 문자열). 없으면 null.</summary>
    public string? Expected { get; init; }
    /// <summary>실제 값. 없으면 null.</summary>
    public string? Actual { get; init; }
    /// <summary>사람이 읽는 설명(연결 문자열 등 비밀값 금지).</summary>
    public required string Message { get; init; }
}

/// <summary>새 IDENTITY 행 짝(D7 4, E2). 미리보기 샌드박스의 키와 원본 실제 키를 비결정 열을 뺀 값으로 짝지은 결과. 설계 외(Q29).</summary>
public sealed record IdentityPair
{
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>미리보기(샌드박스)에서 생긴 키.</summary>
    public required RowKey PreviewKey { get; init; }
    /// <summary>원본에서 실제로 생긴 키.</summary>
    public required RowKey ActualKey { get; init; }
}

/// <summary>KEYSET 모드 테이블 건수 대조(§5.3 "BEGIN TRAN 직후 모든 사용자 테이블의 COUNT_BIG(*) … 건수 변화와 비교"). 설계 외(Q24).</summary>
public sealed record TableCountChange
{
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>BEGIN TRAN 직후 COUNT_BIG(*).</summary>
    public required long Before { get; init; }
    /// <summary>실행 뒤 COUNT_BIG(*).</summary>
    public required long After { get; init; }
    /// <summary>미리보기 netSummary로 기대한 변화(inserted + multisetAdded − deleted − multisetRemoved).</summary>
    public required long ExpectedDelta { get; init; }
}

/// <summary>[적용] 검증 내역(§6.4 ApplyResult.verification).</summary>
public sealed record ApplyVerification
{
    /// <summary>SNAPSHOT_NETDIFF 또는 KEYSET.</summary>
    public required VerificationMode Mode { get; init; }
    /// <summary>드리프트 행. 하나라도 있으면 status = drift_detected.</summary>
    public IReadOnlyList<DriftRow> DriftRows { get; init; } = [];
    /// <summary>문장별 행 수 대조. 실행 전에 끝났으면 빈 배열.</summary>
    public IReadOnlyList<StatementRowCount> StatementRowCounts { get; init; } = [];
    /// <summary>테이블별 netSummary 일치(D7 2). 대조까지 가지 못했으면 null(설계 예시는 bool, null 허용은 설계 외).</summary>
    public bool? NetSummaryMatch { get; init; }
    /// <summary>원본 실제 netSummary(ContractFormulas.Summarize와 같은 셈). 대조까지 가지 못했으면 빈 배열. 설계 외(Q24).</summary>
    public IReadOnlyList<NetSummaryRow> ActualNetSummary { get; init; } = [];
    /// <summary>D7 일치 규칙 위반. 하나라도 있으면 status = rolled_back_mismatch.</summary>
    public IReadOnlyList<ApplyMismatch> Mismatches { get; init; } = [];
    /// <summary>값 비교에서 뺀 비결정 열 "스키마.테이블.열"(§6.4 예시 "Production.TransactionHistory.TransactionDate"). 존재·형식만 비교했다(D7 6).</summary>
    public IReadOnlyList<string> VolatileIgnored { get; init; } = [];
    /// <summary>새 IDENTITY 행 짝(D7 4). 설계 외(Q29).</summary>
    public IReadOnlyList<IdentityPair> IdentityPairs { get; init; } = [];
    /// <summary>트리거 거부·오류 뒤 원본에 트랜잭션 밖으로 확정된 변경(§5.3 "롤백 뒤 남은 변경", A22). 원본 스냅샷 대 원본.</summary>
    public IReadOnlyList<NetChange> ResidualCommitted { get; init; } = [];
    /// <summary>
    /// 롤백 뒤 남은 변경 검사 자체가 실패했는지(예: 최종 차이 계산 중 8115). true면 ResidualCommitted는 믿을 수 없고 원본 상태를 알 수 없다.
    /// 2주차 인계 7과 같은 표시(PreviewResult.ResidualCheckFailed). 설계 외(Q31).
    /// </summary>
    public bool ResidualCheckFailed { get; init; }
    /// <summary>KEYSET 모드의 테이블 건수 대조. SNAPSHOT_NETDIFF면 빈 배열.</summary>
    public IReadOnlyList<TableCountChange> TableCounts { get; init; } = [];
    /// <summary>검증 한계 문구(§5.3 KEYSET "행 수가 그대로인 UPDATE는 놓칠 수 있다는 점을 결과에 명시", 스냅샷을 못 만든 이유 등). 설계 외(Q24).</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>[적용] 단계 시간(ms, §6.4 ApplyResult.timingsMs `{ snapshot, drift, execute, netDiff, commit }`).</summary>
public sealed record ApplyTimingsMs
{
    /// <summary>원본 스냅샷 삭제·생성.</summary>
    public long Snapshot { get; init; }
    /// <summary>드리프트 검사(UPDLOCK, HOLDLOCK 읽기).</summary>
    public long Drift { get; init; }
    /// <summary>문장 실행.</summary>
    public long Execute { get; init; }
    /// <summary>최종 차이(또는 KEYSET 대조).</summary>
    public long NetDiff { get; init; }
    /// <summary>COMMIT 또는 ROLLBACK.</summary>
    public long Commit { get; init; }
}

/// <summary>[적용] 결과(§6.4 ApplyResult, §4 C15 `Apply(previewId)` → `ApplyResult`). POST /api/previews/{id}/apply 200 본문. 상태 5종 모두 200이다(Q24).</summary>
public sealed record ApplyResult
{
    /// <summary>적용한 미리보기 id. 설계 외(Q24).</summary>
    public required string PreviewId { get; init; }
    /// <summary>committed, rolled_back_mismatch, drift_detected, precondition_failed, error.</summary>
    public required ApplyStatus Status { get; init; }
    /// <summary>처리 끝 시각. 설계 외(Q24).</summary>
    public required DateTimeOffset FinishedAt { get; init; }
    /// <summary>어긴 선행 조건(precondition_failed만). 설계 외(Q28).</summary>
    public IReadOnlyList<ApplyPrecondition> PreconditionFailures { get; init; } = [];
    /// <summary>검증 내역. precondition_failed면 Mode만 채운 빈 내역.</summary>
    public required ApplyVerification Verification { get; init; }
    /// <summary>원본에서의 문장 결과(StatementResult, captureSeqRange는 늘 null). 실행 전에 끝났으면 빈 배열. 설계 외(Q24).</summary>
    public IReadOnlyList<StatementResult> Statements { get; init; } = [];
    /// <summary>사람이 읽는 요약(오류 원인, 스냅샷 실패 이유 등). 비밀값 금지. 설계 외(Q24).</summary>
    public string? Message { get; init; }
    /// <summary>원본이 바뀌어(committed 또는 롤백 뒤 남은 변경) 샌드박스 기준선 새로 고침을 시작했는지(§5.3 "샌드박스 기준선 새로 고침 예약"). 설계 외(Q24).</summary>
    public bool SandboxRefreshStarted { get; init; }
    /// <summary>단계 시간.</summary>
    public required ApplyTimingsMs TimingsMs { get; init; }
}

/// <summary>
/// C15 적용 서비스(§4 C15, §5.3, D7). Apply 프로젝트가 구현하고 Host가 조립한다(AD1: Apply는 Contracts만 참조).
/// 미리보기와 같은 잠금(ISandboxManager.AcquirePreviewLockAsync)을 쥐고 원본에 전용 연결·도구 소유 트랜잭션을 연다. 문장 실행은 C10과 같은 실행 봉투(IStatementRunner)로 한다(D7 "같은 실행기").
/// 결과가 커밋됐거나 원본에 롤백 뒤 남은 변경이 생기면 잠금을 푼 뒤 ISandboxManager.StartRefresh()를 부른다. 커밋되면 미리보기를 저장소에서 지운다(같은 건 재적용 방지).
/// </summary>
public interface IApplyService
{
    /// <summary>
    /// 미리보기 한 건을 원본에 적용한다. 미리보기가 없거나 만료면 null(API 404). 잠금 충돌·기준선 구축 중은 PreviewConflictException(API 409).
    /// 선행 조건 위반·드리프트·불일치·실행 오류는 예외가 아니라 ApplyResult 상태다. 원본 트랜잭션은 어떤 경로든 COMMIT 아니면 ROLLBACK으로 끝낸다(finally, D7 XACT_ABORT ON).
    /// </summary>
    Task<ApplyResult?> ApplyAsync(string previewId, ApplyRequest request, CancellationToken ct);
}
