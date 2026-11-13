using System.Data;
using DbRipple.Contracts;
using DbRipple.Core.Sql;
using Microsoft.Data.SqlClient;

namespace DbRipple.Execution;

/// <summary>
/// 실행 봉투 공유(AD6, D7 "같은 실행 봉투"). C10(PreviewExecutor)과 C15(ApplyService)가 함께 쓰는 문장 1개 실행기다. 상태가 없어 싱글턴으로 써도 된다.
/// <para>
/// 규칙은 PreviewExecutor와 같다(봉투 SET 옵션·@@OPTIONS 점검·EXECUTE AS → 실행 → XACT_STATE·@@TRANCOUNT·CURRENT_TRANSACTION_ID → doomed면 ROLLBACK → REVERT, 오류 판정은 ErrorRules).
/// PreviewExecutor가 이 클래스를 쓰도록 바꾸는 일은 테스트 라운드 뒤로 미뤘다(week3-plan §1). 그때까지 두 곳의 규칙을 함께 고친다.
/// </para>
/// <para>rowsAffected는 같은 명령 끝에 붙인 <c>SELECT ROWCOUNT_BIG();</c> 값이다(Q33, NOCOUNT OFF 트리거의 과대 집계를 피함). PreviewExecutor도 같은 식을 쓴다(D7 1 비교, 2026-11-11 A안).</para>
/// <para>
/// 예외: 연결이 안 열렸거나 끊기면 <see cref="SandboxUnavailableException"/>(E20, API 503). 취소는 OperationCanceledException.
/// 봉투 명령(EXECUTE AS·REVERT·상태 조회)의 그 밖 SQL 오류(예 실행 사용자 없음)는 InvalidOperationException(오류 번호 포함). 사용자 문장의 SQL 오류는 예외가 아니라 결과다.
/// </para>
/// </summary>
public sealed class StatementRunner : IStatementRunner
{
    /// <summary>사용자 문장 뒤에 붙이는 영향 행 수 조회(Q33). 앞 문장이 세미콜론 없이 끝나도 되도록 빈 문장 구분자로 시작한다.</summary>
    public const string RowCountSuffix = "\n;SELECT ROWCOUNT_BIG();";

    /// <summary>매개변수 없는 생성자(Host DI, AD6).</summary>
    public StatementRunner() { }

    /// <inheritdoc />
    public async Task PrepareAsync(SqlSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureOpen(session, "실행 봉투 준비");
        int options;
        try
        {
            await using var cmd = session.CreateCommand(PreviewExecutor.EnvelopeSql);
            options = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqlException ex)
        {
            throw Envelope(session, ex, ct, "실행 봉투 SET 옵션 적용");
        }
        if ((options & PreviewExecutor.RequiredOptions) != PreviewExecutor.RequiredOptions || (options & PreviewExecutor.ForbiddenOptions) != 0)
            throw new InvalidOperationException($"실행 봉투 SET 옵션 점검 실패(@@OPTIONS = {options})");
    }

    /// <inheritdoc />
    public async Task<StatementRunOutcome> RunAsync(SqlSession session, AnalyzedStatement statement, StatementRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);
        EnsureOpen(session, "문장 실행");
        var execSql = statement.Kind == ChangeOperation.Exec ? ExecSql(statement) : null; // 가장 전에 계약 위반을 거른다

        var impersonating = false;
        if (context.ExecuteAsUser is { } user)
        {
            await EnvelopeExecAsync(session, $"EXECUTE AS USER = {SqlText.NLiteral(user)};", ct, "EXECUTE AS");
            impersonating = true;
        }

        long? rows = null;
        ExecRead? exec = null;
        SqlException? error = null;
        try
        {
            if (execSql is not null)
            {
                await using var cmd = session.CreateCommand(execSql, context.CommandTimeoutSeconds);
                exec = await ReadExecAsync(cmd, ct);
            }
            else
            {
                await using var cmd = session.CreateCommand(statement.Sql + RowCountSuffix, context.CommandTimeoutSeconds);
                rows = await ReadRowCountAsync(cmd, ct);
            }
        }
        catch (SqlException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("문장 실행 취소", ex, ct);
            error = ex;
        }
        if (session.Connection.State != ConnectionState.Open)
            throw new SandboxUnavailableException("문장 실행 중 연결이 끊김(E20)", error);

        // PreviewExecutor와 같은 순서(위험 6, SP10): @@TRANCOUNT를 먼저 보고, 남은 트랜잭션이 doomed(−1)면 REVERT 전에 ROLLBACK(3930 방지).
        var (xact, tranCount, currentTranId) = await StateAsync(session, ct);
        if (tranCount > 0 && xact == -1)
        {
            await EnvelopeExecAsync(session, "ROLLBACK;", ct, "doomed ROLLBACK");
            tranCount = 0;
        }
        var revert = impersonating ? await RevertAsync(session, ct) : null;
        var classified = error is null ? ((StatementStatus, StatementError)?)null : ErrorRules.Classify(error, context.Baseline);
        var outcome = Decide(statement, context, classified, rows, xact, tranCount, currentTranId, revert, exec?.TranDelta);
        return exec is { } x && outcome.Result.Status == StatementStatus.Ok
            ? outcome with { Result = outcome.Result with { ReturnValue = x.ReturnValue, ResultSetCount = x.ResultSets } }
            : outcome;
    }

    /// <summary>EXEC 문장의 실행 SQL(분석기가 만든 ProcedurePlan.ExecSql). 없으면 계약 위반이라 InvalidOperationException.</summary>
    public static string ExecSql(AnalyzedStatement statement) =>
        statement.Plan is { ExecSql: { Length: > 0 } sql } ? sql : throw new InvalidOperationException($"EXEC 문장 {statement.Index}에 실행 SQL이 없음(분석기 계약 위반)");

    /// <summary>EXEC 실행 결과: 반환 값, 프로시저가 낸 결과 집합 수, 트랜잭션 수 변화(dbr_dtc, 못 읽었으면 null).</summary>
    public readonly record struct ExecRead(int? ReturnValue, int ResultSets, int? TranDelta);

    /// <summary>
    /// EXEC 실행 결과를 읽는다: 프로시저가 낸 결과 집합은 개수만 세고 버리고, 마지막 결과 집합(SELECT @dbr_rc AS dbr_rc, @dbr_dtc AS dbr_dtc)에서
    /// 반환 값과 트랜잭션 수 변화를 읽는다. 마지막 결과 집합이 그 모양이 아니면(정상 경로에서는 없음) 둘 다 null이고 모든 결과 집합을 프로시저 것으로 센다.
    /// 오류(266 등)가 마지막 결과 집합 전에 나면 SqlException으로 나가 값을 읽지 못한다(SP11·SP14 뒤 재검토).
    /// </summary>
    internal static async Task<ExecRead> ReadExecAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var sets = 0;
        int? rc = null, dtc = null;
        var lastIsOurs = false;
        do
        {
            if (r.FieldCount == 0) continue;
            sets++;
            var ours = r.FieldCount == 2 && r.GetName(0) == ProcedurePlan.ReturnColumn && r.GetName(1) == ProcedurePlan.TranDeltaColumn;
            int? v0 = null, v1 = null;
            var first = true;
            while (await r.ReadAsync(ct))
            {
                if (first && ours)
                {
                    v0 = r.IsDBNull(0) ? null : Convert.ToInt32(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                    v1 = r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
                }
                first = false;
            }
            lastIsOurs = ours;
            if (ours) (rc, dtc) = (v0, v1);
        } while (await r.NextResultAsync(ct));
        return lastIsOurs ? new(rc, sets - 1, dtc) : new(null, sets, null);
    }

    /// <summary>EXEC 프로시저가 도구 트랜잭션을 롤백했다고 확인된 경우의 메시지(계약). 실행기가 롤백 뒤 남은 변경이 없음을 확인했을 때만 쓴다.</summary>
    public const string ProcedureRolledBackMessage = "프로시저가 트랜잭션을 롤백함";

    /// <summary>EXEC 프로시저가 트랜잭션 수를 바꿨을 때 메시지 머리(사실만 적는다).</summary>
    public const string ProcedureTranCountMessage = "프로시저가 트랜잭션 수를 바꿈";

    /// <summary>EXEC 뒤 열린 트랜잭션이 없을 때의 메시지(롤백인지 확정인지는 롤백 뒤 남은 변경 검사로 가린다).</summary>
    public const string ProcedureTranGoneMessage = ProcedureTranCountMessage + "(ROLLBACK 또는 짝 없는 COMMIT): 실행 뒤 열린 트랜잭션이 없음";

    /// <summary>
    /// EXEC 문장의 트랜잭션 수 판정(순수). 실행 SQL은 호출을 BEGIN TRAN 한 겹으로 감싸고, 정상이면 그 겹을 닫아 문장 뒤 @@TRANCOUNT가 1, dbr_dtc가 1이다.
    /// <list type="bullet">
    /// <item>오류가 수집 장치 오류이거나 266(EXECUTE 뒤 트랜잭션 수 불일치)이 아니면 null(엔진 오류는 ErrorRules 판정에 맡긴다).</item>
    /// <item>트랜잭션이 바뀜 / 없어짐 / 1보다 많음 / dbr_dtc = 0(또는 266인데 dbr_dtc를 못 읽음)이면 failed, isToolFault = false, "프로시저가 트랜잭션 수를 바꿈..." 메시지.</item>
    /// <item>오류도 이상도 없는데 dbr_dtc를 못 읽었으면 도구 결함.</item>
    /// </list>
    /// 266이 XACT_ABORT ON에서 일괄 처리를 끊는지·트랜잭션을 롤백하는지는 SP14 뒤 재검토.
    /// </summary>
    public static StatementError? ExecTransactionAnomaly(int tranCount, bool replaced, long? fromTranId, long? toTranId, int? tranDelta, StatementError? error)
    {
        if (error is { } e && (e.IsToolFault || e.ErrorNumber != ErrorRules.TranCountMismatch)) return null;
        var message =
            replaced ? $"{ProcedureTranCountMessage}: 도구 트랜잭션이 새 트랜잭션으로 바뀜(트랜잭션 ID {fromTranId} → {toTranId})"
            : tranCount == 0 ? ProcedureTranGoneMessage
            : tranCount > 1 ? $"{ProcedureTranCountMessage}: 짝 없는 BEGIN TRAN(@@TRANCOUNT = {tranCount})"
            : tranDelta == 0 || (tranDelta is null && error is not null) ? $"{ProcedureTranCountMessage}: 짝 없는 COMMIT(감싼 BEGIN TRAN만 닫혀 도구 트랜잭션은 확정되지 않음)"
            : null;
        if (message is null)
        {
            if (tranDelta == 1) return null;
            return new StatementError { ErrorNumber = 0, Message = $"EXEC 결과의 트랜잭션 수 변화(dbr_dtc = {tranDelta?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"})를 해석할 수 없음", IsToolFault = true };
        }
        return new StatementError
        {
            ErrorNumber = error?.ErrorNumber ?? 0,
            Message = message + (error is null ? "" : $"(오류 {error.ErrorNumber}: {error.Message})"),
            Procedure = error?.Procedure,
            IsToolFault = false,
        };
    }

    /// <summary>REVERT 대체 경로 기록: 첫 REVERT의 오류와, "IF @@TRANCOUNT > 0 ROLLBACK; REVERT;" 뒤 다시 읽은 @@TRANCOUNT.</summary>
    public sealed record RevertFallback(int ErrorNumber, string Message, int TranCountAfter);

    /// <summary>
    /// 문장 뒤 상태로 결과를 판정한다(순수, 단위 테스트용). tranCount는 doomed ROLLBACK 뒤 값이다(했으면 0).
    /// <list type="number">
    /// <item>REVERT 대체 경로가 열린 트랜잭션을 ROLLBACK했으면(검토 reviewer-2, 2026-11-11): TransactionOpen = 다시 읽은 @@TRANCOUNT &gt; 0(= false), Status = failed,
    /// IsToolFault, 오류 번호는 REVERT 오류, 메시지에 문장 오류도 남긴다. 그러지 않으면 호출자(ApplyService)가 트랜잭션 없이 다음 문장을 실행해 자동 커밋할 수 있다.</item>
    /// <item>그 밖에는 PreviewExecutor와 같다: 오류면 ErrorRules 판정(트랜잭션이 바뀌었으면 failed), 오류 없이 트랜잭션 상태가 다르거나 바뀌었으면 failed, 아니면 ok.</item>
    /// </list>
    /// </summary>
    public static StatementRunOutcome Decide(AnalyzedStatement statement, StatementRunContext context, (StatementStatus Status, StatementError Error)? classified,
        long? rows, int xact, int tranCount, long? currentTranId, RevertFallback? revert, int? execTranDelta = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);
        if (revert is not null)
        {
            var rolledBack = tranCount > 0 && revert.TranCountAfter == 0;
            tranCount = revert.TranCountAfter;
            if (rolledBack || tranCount > 0)
            {
                var stmt = classified is { } c ? $" 문장 오류 {c.Error.ErrorNumber}: {c.Error.Message}" : "";
                return new StatementRunOutcome
                {
                    Result = Result(statement, StatementStatus.Failed) with
                    {
                        Error = new StatementError
                        {
                            ErrorNumber = revert.ErrorNumber,
                            Message = (rolledBack
                                ? $"REVERT 실패(오류 {revert.ErrorNumber}: {revert.Message})로 도구 트랜잭션을 ROLLBACK함. 이 문장과 앞 문장의 변경은 취소됨."
                                : $"REVERT 실패(오류 {revert.ErrorNumber}: {revert.Message}) 뒤 트랜잭션 상태가 예상과 다름(@@TRANCOUNT = {tranCount}).") + stmt,
                            IsToolFault = true,
                        },
                    },
                    TransactionOpen = tranCount > 0,
                    TransactionReplaced = false,
                };
            }
            // 이미 끝난 트랜잭션(@@TRANCOUNT 0)에서 REVERT만 다시 한 경우: 아래 일반 판정(트랜잭션 없음 → failed 또는 오류 판정)
        }

        var open = tranCount > 0;
        // 트리거의 "COMMIT; BEGIN TRAN;"(오류가 있어도, 인계 5): 트랜잭션 ID가 바뀌었으면 그 앞 변경은 확정됐다.
        var replaced = open && tranCount == 1 && context.TransactionId is long tranId && currentTranId != tranId;

        StatementResult result;
        bool? rollbackConfirmed = null;
        if (statement.Kind == ChangeOperation.Exec
            && ExecTransactionAnomaly(tranCount, replaced, context.TransactionId, currentTranId, execTranDelta, classified?.Error) is { } anomaly)
        {
            result = Result(statement, StatementStatus.Failed) with { Error = anomaly };
        }
        else if (classified is { } cl)
        {
            var (status, err) = PreviewExecutor.Replaced(cl, replaced);
            result = Result(statement, status) with { Error = err };
            if (status == StatementStatus.TriggerBlocked)
                rollbackConfirmed = ErrorRules.EndedByRollback(err.Procedure, statement.Target, statement.Kind, context.Baseline, statement.Plan?.WriteTargets);
        }
        else if (!open || tranCount != 1 || replaced)
        {
            result = Result(statement, StatementStatus.Failed) with
            {
                Error = new StatementError
                {
                    ErrorNumber = 0,
                    Message = replaced
                        ? $"문장 중 도구 트랜잭션이 확정되고 새 트랜잭션으로 바뀜(트리거의 COMMIT 뒤 BEGIN TRAN으로 보임, 트랜잭션 ID {context.TransactionId} → {currentTranId}). 확정된 변경은 롤백할 수 없음"
                        : $"문장 뒤 트랜잭션 상태가 예상과 다름(@@TRANCOUNT = {tranCount}, XACT_STATE = {xact})",
                    IsToolFault = !replaced,
                },
            };
        }
        else
        {
            result = Result(statement, StatementStatus.Ok) with { RowsAffected = statement.Kind == ChangeOperation.Exec ? null : rows };
        }

        return new StatementRunOutcome
        {
            Result = result,
            TransactionOpen = open,
            TransactionReplaced = replaced,
            TriggerRollbackConfirmed = rollbackConfirmed,
        };
    }

    /// <summary>
    /// 결과 집합을 끝까지 읽고 마지막 결과 집합의 첫 값(ROWCOUNT_BIG)을 돌려준다. 문장의 OUTPUT 절이나 트리거가 낸 결과 집합은 건너뛴다.
    /// 오류는 읽는 도중 SqlException으로 나온다(XACT_ABORT ON이면 일괄 처리가 중단되어 ROWCOUNT_BIG까지 가지 않는다).
    /// 값을 못 얻으면(정상 경로에서는 없음) 드라이버 RecordsAffected(과대 집계 가능), 그것도 없으면 null.
    /// </summary>
    internal static async Task<long?> ReadRowCountAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        long? last = null;
        do
        {
            long? first = null;
            while (await r.ReadAsync(ct))
                if (first is null && r.FieldCount == 1 && !r.IsDBNull(0) && r.GetValue(0) is long v) first = v;
            last = first ?? (r.FieldCount > 0 ? null : last);
        } while (await r.NextResultAsync(ct));
        if (last is null && r.RecordsAffected >= 0) last = r.RecordsAffected;
        return last;
    }

    static StatementResult Result(AnalyzedStatement st, StatementStatus status) => new()
    {
        Index = st.Index,
        Batch = st.Batch,
        Position = st.Position,
        Kind = st.Kind,
        Target = st.Target,
        Span = st.Span,
        Status = status,
        Plan = st.Plan,
    };

    static async Task<(int Xact, int TranCount, long? TranId)> StateAsync(SqlSession session, CancellationToken ct)
    {
        try
        {
            await using var cmd = session.CreateCommand("SELECT XACT_STATE(), @@TRANCOUNT, CURRENT_TRANSACTION_ID();");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (Convert.ToInt32(r.GetValue(0)), r.GetInt32(1), r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2)));
        }
        catch (SqlException ex)
        {
            throw Envelope(session, ex, ct, "트랜잭션 상태 조회");
        }
    }

    /// <summary>
    /// REVERT. 성공하면 null. 실패하면(예 doomed 3930) 남은 트랜잭션을 ROLLBACK하고 다시 REVERT한 뒤 @@TRANCOUNT를 다시 읽어 돌려준다(Decide가 failed로 판정).
    /// 다시 한 REVERT도 실패하면 예외(호출자는 연결을 버린다).
    /// </summary>
    static async Task<RevertFallback?> RevertAsync(SqlSession session, CancellationToken ct)
    {
        try
        {
            await using var cmd = session.CreateCommand("REVERT;");
            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }
        catch (SqlException ex) when (session.Connection.State == ConnectionState.Open && !ct.IsCancellationRequested)
        {
            await EnvelopeExecAsync(session, "IF @@TRANCOUNT > 0 ROLLBACK;", ct, "REVERT 전 ROLLBACK");
            await EnvelopeExecAsync(session, "REVERT;", ct, "REVERT");
            var (_, after, _) = await StateAsync(session, ct);
            return new RevertFallback(ex.Number, ex.Message, after);
        }
        catch (SqlException ex)
        {
            throw Envelope(session, ex, ct, "REVERT");
        }
    }

    static async Task EnvelopeExecAsync(SqlSession session, string sql, CancellationToken ct, string step)
    {
        try
        {
            await using var cmd = session.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex)
        {
            throw Envelope(session, ex, ct, step);
        }
    }

    /// <summary>봉투 명령 실패를 예외로 바꾼다: 취소 → OperationCanceledException, 연결 끊김 → SandboxUnavailableException, 그 밖 → InvalidOperationException.</summary>
    static Exception Envelope(SqlSession session, SqlException ex, CancellationToken ct, string step)
    {
        if (ct.IsCancellationRequested) return new OperationCanceledException($"{step} 취소", ex, ct);
        if (session.Connection.State != ConnectionState.Open || ex.Class >= 20)
            return new SandboxUnavailableException($"{step} 중 연결이 끊김(E20)", ex);
        return new InvalidOperationException($"{step} 실패(오류 {ex.Number}: {ex.Message})", ex);
    }

    static void EnsureOpen(SqlSession session, string step)
    {
        if (session.Connection.State != ConnectionState.Open)
            throw new SandboxUnavailableException($"{step}: 연결이 열려 있지 않음(E20)");
    }
}
