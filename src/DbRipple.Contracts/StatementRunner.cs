namespace DbRipple.Contracts;

// 실행 봉투 공유 계약(D7 "같은 실행 봉투로 재실행", 아키텍처 결정 AD6). 2026-11-11 3주차 추가(비파괴).
// C10(PreviewExecutor) 안에 있던 "봉투 SET 옵션 점검 + 문장 1개 실행(EXECUTE AS → 실행 → 상태 확인 → doomed면 ROLLBACK → REVERT) + 오류 판정"을
// C15(Apply)도 쓰도록 인터페이스로 뺀다. Execution 프로젝트가 구현하고(StatementRunner), Apply는 생성자로 받는다(Apply는 Execution을 참조하지 않는다, AD1).
// 감사 경계·이미지 수·상한 전환은 C10 고유라 여기 넣지 않는다.

/// <summary>문장 1개 실행 문맥(C10·C15 공통).</summary>
public sealed record StatementRunContext
{
    /// <summary>오류 판정(ErrorRules, Q27)에 쓰는 카탈로그·트리거 정적 분석. 적용(C15)도 샌드박스 기준선 문맥을 넘긴다(지문이 같으므로 트리거 구성이 같다고 본다).</summary>
    public required BaselineContext Baseline { get; init; }
    /// <summary>사용자 문장 구간의 EXECUTE AS USER 대상(§5.2 3, E19, Q12). null이면 가장하지 않는다(테스트 전용).</summary>
    public string? ExecuteAsUser { get; init; }
    /// <summary>CommandTimeout 초(E20).</summary>
    public int CommandTimeoutSeconds { get; init; } = 60;
    /// <summary>BEGIN TRAN 직후 읽은 CURRENT_TRANSACTION_ID(). 문장 뒤 값이 다르면 트리거가 COMMIT 뒤 BEGIN TRAN 한 것이다(§5.2 v1.4 확정 감지). null이면 비교하지 않는다.</summary>
    public long? TransactionId { get; init; }
}

/// <summary>문장 1개 실행 결과.</summary>
public sealed record StatementRunOutcome
{
    /// <summary>
    /// 문장 결과. Status는 ok, failed, trigger_blocked 중 하나이고(not_run은 호출자가 만든다), ok면 RowsAffected가 있고, 실패면 Error가 있다.
    /// CaptureSeqRange는 늘 null(감사 경계는 C10 고유). 3609의 ROLLBACK/COMMIT 구분(롤백 뒤 남은 변경이 있으면 failed)은 호출자가 롤백 뒤 검사로 보정한다.
    /// </summary>
    public required StatementResult Result { get; init; }
    /// <summary>문장 뒤 도구 트랜잭션이 살아 있는지(@@TRANCOUNT = 1). false면 오류·트리거 ROLLBACK·doomed 처리로 이미 끝났다.</summary>
    public required bool TransactionOpen { get; init; }
    /// <summary>트랜잭션 ID가 바뀜(트리거의 COMMIT 뒤 BEGIN TRAN). true면 그 앞 변경은 이미 확정됐고 Result.Status = failed다.</summary>
    public bool TransactionReplaced { get; init; }
    /// <summary>
    /// Result.Status가 trigger_blocked일 때, 실행기가 정적 분석으로 그 거부가 트리거 ROLLBACK에서 났다고 확인했는지(Execution ErrorRules.EndedByRollback).
    /// 호출자는 롤백 뒤 남은 변경이 있고 이 값이 false면 Status를 failed로 바꾼다(3609 보정, 2주차 인계 5). trigger_blocked가 아니면 null.
    /// Apply는 Execution을 참조하지 않으므로(AD7) 이 값으로 보정한다.
    /// </summary>
    public bool? TriggerRollbackConfirmed { get; init; }
}

/// <summary>
/// 실행 봉투(§4 C10, D7). 호출자는 전용 연결(풀링 끔)을 열고 PrepareAsync → "BEGIN TRAN;"(T-SQL, SqlSession.Transaction = null) → 문장마다 RunAsync 순서로 부른다.
/// RunAsync는 돌아오기 전에 반드시 REVERT 한다(doomed면 ROLLBACK 먼저, SP10 3930). 예외(연결 끊김, 취소)로 나가면 가장이 남을 수 있으므로 호출자는 연결을 버린다.
/// </summary>
public interface IStatementRunner
{
    /// <summary>봉투 SET 옵션(XACT_ABORT ON, QUOTED_IDENTIFIER ON, NOCOUNT OFF 등)을 적용하고 @@OPTIONS를 점검한다. 어긋나면 InvalidOperationException.</summary>
    Task PrepareAsync(SqlSession session, CancellationToken ct);

    /// <summary>문장 1개를 실행하고 상태·오류를 판정한다(Q27 규칙). SQL 오류는 예외가 아니라 결과다. 연결이 끊기면 SandboxUnavailableException.</summary>
    Task<StatementRunOutcome> RunAsync(SqlSession session, AnalyzedStatement statement, StatementRunContext context, CancellationToken ct);
}
