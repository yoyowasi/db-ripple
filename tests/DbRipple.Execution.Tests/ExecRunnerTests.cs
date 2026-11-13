using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Core.Baseline;

namespace DbRipple.Execution.Tests;

/// <summary>
/// EXEC 미리보기(2026-11-13) 실행 판정(DB 없이): 프로시저가 도구 트랜잭션을 롤백하면 failed + isToolFault = false + "프로시저가 트랜잭션을 롤백함".
/// 같은 상태의 DML은 예전처럼 도구 결함이다(분류 불변). EXEC 영향 행 수는 null.
/// </summary>
public class ExecRunnerTests
{
    static BaselineContext Ctx(params TriggerStaticInfo[] triggers) => new()
    {
        OriginDb = "O", SandboxDb = "S", SnapshotDb = "B",
        BackupFinishedAt = DateTimeOffset.Parse("2026-11-13T00:00:00+09:00"),
        Fingerprint = new Fingerprint("17.0", "Developer", 170, false, 1, 0, 5432, "SQL_Latin1_General_CP1_CI_AS"),
        FingerprintMatch = true,
        Catalog = new DbCatalog(
            [new(1, "dbo", "P", TemporalType.None, false), new(2, "dbo", "C", TemporalType.None, false)],
            [], [], [new(10, "FK_C_P", 2, 1, "CASCADE", "NO_ACTION", false, false, 1, "p_id", "id")], [], []),
        Triggers = triggers,
        Manifest = new CaptureManifest("t", [], [], []),
    };

    static readonly SourceSpan Span1 = new() { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 10 };

    static AnalyzedStatement Exec(string execSql = "DECLARE @dbr_rc int;\nEXEC @dbr_rc = [dbo].[p];\nSELECT @dbr_rc AS dbr_rc;") => new()
    {
        Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Exec, Target = null, Span = Span1, Sql = "EXEC dbo.p;", Procedure = "dbo.p",
        Plan = new ProcedurePlan { Procedure = "dbo.p", ExecSql = execSql, WriteTargets = ["dbo.P"] },
    };

    static AnalyzedStatement Dml() => new()
    {
        Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Delete, Target = "dbo.P", Span = Span1, Sql = "DELETE dbo.P;",
    };

    static StatementRunContext Context(params TriggerStaticInfo[] triggers) => new() { Baseline = Ctx(triggers), TransactionId = 7 };

    [Fact]
    public void Transaction_gone_after_exec_is_failed_but_not_tool_fault_and_states_facts_only()
    {
        // 실행기 단독으로는 롤백인지 짝 없는 COMMIT인지 모른다 → 사실만 적는다(검토 2026-11-13 c)
        var o = StatementRunner.Decide(Exec(), Context(), null, null, 1, 0, null, null, execTranDelta: null);
        Assert.Equal((StatementStatus.Failed, false, false), (o.Result.Status, o.TransactionOpen, o.Result.Error!.IsToolFault));
        Assert.Equal("프로시저가 트랜잭션 수를 바꿈(ROLLBACK 또는 짝 없는 COMMIT): 실행 뒤 열린 트랜잭션이 없음", o.Result.Error.Message);
        Assert.Equal(StatementRunner.ProcedureTranGoneMessage, o.Result.Error.Message);
        Assert.Equal(ChangeOperation.Exec, o.Result.Kind);
        Assert.Null(o.Result.Target);
        Assert.Same(Exec().Plan!.ExecSql, o.Result.Plan!.ExecSql);
    }

    [Fact]
    public void Transaction_gone_with_trancount_mismatch_error_keeps_the_error_text()
    {
        var mismatch = (StatementStatus.Failed, new StatementError { ErrorNumber = ErrorRules.TranCountMismatch, Message = "Transaction count after EXECUTE ...", Procedure = "p", IsToolFault = false });
        var o = StatementRunner.Decide(Exec(), Context(), mismatch, null, 1, 0, null, null);
        Assert.Equal((StatementStatus.Failed, false, 266), (o.Result.Status, o.Result.Error!.IsToolFault, o.Result.Error.ErrorNumber));
        Assert.StartsWith(StatementRunner.ProcedureTranGoneMessage, o.Result.Error.Message);
        Assert.Contains("266", o.Result.Error.Message);
    }

    [Theory]
    // (문장 뒤 @@TRANCOUNT, 트랜잭션 ID, dbr_dtc, 266 오류) → 기대 메시지(null = ok)
    [InlineData(1, 7L, 1, false, null)]                                                        // 정상: 감싼 겹을 닫음
    [InlineData(1, 7L, 0, false, "프로시저가 트랜잭션 수를 바꿈: 짝 없는 COMMIT(감싼 BEGIN TRAN만 닫혀 도구 트랜잭션은 확정되지 않음)")]
    [InlineData(1, 7L, null, true, "프로시저가 트랜잭션 수를 바꿈: 짝 없는 COMMIT(감싼 BEGIN TRAN만 닫혀 도구 트랜잭션은 확정되지 않음)")] // 266 때문에 dbr_dtc를 못 읽음
    [InlineData(3, 7L, 2, false, "프로시저가 트랜잭션 수를 바꿈: 짝 없는 BEGIN TRAN(@@TRANCOUNT = 3)")]
    [InlineData(0, null, -1, false, "프로시저가 트랜잭션 수를 바꿈(ROLLBACK 또는 짝 없는 COMMIT): 실행 뒤 열린 트랜잭션이 없음")]
    [InlineData(1, 8L, 0, false, "프로시저가 트랜잭션 수를 바꿈: 도구 트랜잭션이 새 트랜잭션으로 바뀜(트랜잭션 ID 7 → 8)")] // ROLLBACK 뒤 BEGIN
    public void Exec_transaction_count_classification(int tranCount, long? tranId, int? delta, bool mismatch, string? expected)
    {
        var err = mismatch ? (StatementStatus.Failed, new StatementError { ErrorNumber = 266, Message = "m", IsToolFault = false }) : ((StatementStatus, StatementError)?)null;
        var o = StatementRunner.Decide(Exec(), Context(), err, null, 1, tranCount, tranId, null, delta);
        if (expected is null)
        {
            Assert.Equal((StatementStatus.Ok, (long?)null), (o.Result.Status, o.Result.RowsAffected)); // A9: EXEC 영향 행 수는 null
            return;
        }
        Assert.Equal((StatementStatus.Failed, false), (o.Result.Status, o.Result.Error!.IsToolFault));
        Assert.StartsWith(expected, o.Result.Error.Message);
    }

    [Fact]
    public void Exec_without_tran_delta_and_without_error_is_a_tool_fault()
    {
        var o = StatementRunner.Decide(Exec(), Context(), null, null, 1, 1, 7, null, execTranDelta: null);
        Assert.Equal((StatementStatus.Failed, true), (o.Result.Status, o.Result.Error!.IsToolFault));
    }

    [Theory]
    [InlineData(false, 0, false, "프로시저가 트랜잭션을 롤백함")]
    [InlineData(true, 0, false, "프로시저가 트랜잭션 수를 바꿈(ROLLBACK 또는 짝 없는 COMMIT): 실행 뒤 열린 트랜잭션이 없음 롤백 뒤에도 남은 변경이 있어 일부 변경이 확정됨(남은 최종 차이 0건, 샌드박스를 다시 만듦)")]
    [InlineData(false, 2, false, "프로시저가 트랜잭션 수를 바꿈(ROLLBACK 또는 짝 없는 COMMIT): 실행 뒤 열린 트랜잭션이 없음 롤백 뒤에도 남은 변경이 있어 일부 변경이 확정됨(남은 최종 차이 2건, 샌드박스를 다시 만듦)")]
    [InlineData(false, 0, true, "프로시저가 트랜잭션 수를 바꿈(ROLLBACK 또는 짝 없는 COMMIT): 실행 뒤 열린 트랜잭션이 없음 롤백 뒤 남은 변경을 확인하지 못함(샌드박스를 다시 만듦)")]
    public void Rollback_wording_follows_the_residual_check(bool committedAudit, int residual, bool checkFailed, string expected)
    {
        Assert.Equal(expected, PreviewExecutor.ExecTranGoneMessage(StatementRunner.ProcedureTranGoneMessage, committedAudit, residual, checkFailed));
        // 원래 오류 꼬리는 남긴다
        Assert.Equal("프로시저가 트랜잭션을 롤백함(오류 266: m)",
            PreviewExecutor.ExecTranGoneMessage(StatementRunner.ProcedureTranGoneMessage + "(오류 266: m)", false, 0, false));
    }

    [Fact]
    public void Dml_with_lost_transaction_is_still_a_tool_fault()
    {
        // 분류 불변: DML 문장 뒤 트랜잭션이 없어졌는데 오류가 없으면 도구 결함(예전 그대로)
        var o = StatementRunner.Decide(Dml(), Context(), null, 1, 1, 0, null, null);
        Assert.Equal((StatementStatus.Failed, true), (o.Result.Status, o.Result.Error!.IsToolFault));
        Assert.Contains("@@TRANCOUNT = 0", o.Result.Error.Message);
        var ok = StatementRunner.Decide(Dml(), Context(), null, 3, 1, 1, 7, null);
        Assert.Equal((StatementStatus.Ok, (long?)3), (ok.Result.Status, ok.Result.RowsAffected));
    }

    [Fact]
    public void Exec_engine_error_is_classified_normally()
    {
        // 프로시저 안 제약 위반(XACT_ABORT ON이라 트랜잭션도 끝남)은 트랜잭션 수 판정이 아니라 보통 실패다
        var fk = (StatementStatus.Failed, new StatementError { ErrorNumber = 547, Message = "fk", IsToolFault = false });
        var o = StatementRunner.Decide(Exec(), Context(), fk, null, 1, 0, null, null);
        Assert.Equal((StatementStatus.Failed, 547, "fk"), (o.Result.Status, o.Result.Error!.ErrorNumber, o.Result.Error.Message));
        // 수집 장치 오류는 266이어도 도구 결함 그대로
        var tool = (StatementStatus.Failed, new StatementError { ErrorNumber = 266, Message = "x", Procedure = "dbr_audit_T_I", IsToolFault = true });
        Assert.True(StatementRunner.Decide(Exec(), Context(), tool, null, 1, 0, null, null).Result.Error!.IsToolFault);
    }

    [Fact]
    public void Trigger_rollback_inside_exec_is_confirmed_through_procedure_write_targets()
    {
        // 프로시저가 dbo.P를 지우고 FK로 닿는 dbo.C의 ROLLBACK 트리거가 3609를 낸 경우
        var rb = new TriggerStaticInfo
        {
            Trigger = "dbo.trC", ObjectId = 0, Table = "dbo.C", Events = [ChangeOperation.Delete], IsInsteadOf = false, IsDisabled = false,
            IsEncrypted = false, IsAuditTrigger = false, HasDynamicSql = false, HasRollback = true,
        };
        var blocked = (StatementStatus.TriggerBlocked, new StatementError { ErrorNumber = 3609, Message = "ended", IsToolFault = false });
        var o = StatementRunner.Decide(Exec(), Context(rb), blocked, null, 1, 0, null, null);
        Assert.Equal((StatementStatus.TriggerBlocked, (bool?)true), (o.Result.Status, o.TriggerRollbackConfirmed));
        Assert.True(ErrorRules.EndedByRollback(null, null, ChangeOperation.Exec, Ctx(rb), ["dbo.P"]));
        Assert.False(ErrorRules.EndedByRollback(null, null, ChangeOperation.Exec, Ctx(rb), null)); // 쓰기 대상을 모르면 닫힌 쪽
    }

    [Fact]
    public void Preview_user_gets_execute_per_caller_procedure_not_per_schema()
    {
        // 검토 2026-11-13(5): 스키마 EXECUTE는 사용자 정의 함수까지 열어 DML 경로에서도 함수가 돌게 했다
        var sql = PreviewUserSql.EnsureBatch(["dbo", "audit"]);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [dbr_preview_user]", sql);
        Assert.DoesNotContain("GRANT EXECUTE ON SCHEMA", sql);
        Assert.Contains("REVOKE EXECUTE ON SCHEMA::[dbo] FROM [dbr_preview_user]", sql); // 첫 EXEC 판이 준 권한 정리
        Assert.Contains("N'GRANT EXECUTE ON OBJECT::' + QUOTENAME(SCHEMA_NAME(p.schema_id))", sql);
        Assert.Contains("FROM sys.procedures AS p JOIN sys.sql_modules AS m", sql); // 함수(sys.objects FN·IF·TF·FS)는 대상이 아니다
        Assert.Contains("p.type = 'P' AND m.definition IS NOT NULL AND m.execute_as_principal_id IS NULL AND SCHEMA_NAME(p.schema_id) <> N'audit'", sql);
        Assert.Contains("IF @dbr_grants IS NOT NULL BEGIN EXEC sys.sp_executesql @dbr_grants; SET @ddl = 1; END;", sql);
        Assert.EndsWith("SELECT @ddl;\n", sql);
    }

    [Fact]
    public void Exec_without_plan_is_a_contract_violation()
    {
        var bad = Exec() with { Plan = null };
        Assert.Throws<InvalidOperationException>(() => StatementRunner.ExecSql(bad));
        Assert.Throws<InvalidOperationException>(() => StatementRunner.ExecSql(Exec("")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ErrorRules.CascadeTriggers("dbo.P", ChangeOperation.Exec, Ctx()));
    }
}
