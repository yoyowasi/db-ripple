using DbRipple.Core.Baseline;
using DbRipple.Contracts;
using DbRipple.Execution.Tests.Support;
using DbRipple.TestSupport;

namespace DbRipple.Execution.Tests;

/// <summary>Q27(trigger_blocked와 failed의 규칙), R18 isToolFault, 롤백 뒤 남은 변경(ResidualCommitted·SandboxDirty). 실측 근거는 ProbeTests.</summary>
[Collection(DbCollection.Name)]
public class ErrorRuleTests
{
    static readonly HashSet<string> UserTriggers = new(StringComparer.OrdinalIgnoreCase) { "trg_Blk_rb", "trg_Ins_iod" };

    [Fact]
    public void Pure_rules()
    {
        // 문장 수준 제약 위반 → failed
        var (s1, e1) = ErrorRules.Classify([new ErrorInfo(547, 16, "fk", null), new ErrorInfo(3621, 0, "terminated", null)], UserTriggers);
        Assert.Equal((StatementStatus.Failed, 547, false), (s1, e1.ErrorNumber, e1.IsToolFault));
        // 감사 트리거 안 오류 → failed + isToolFault, 대표 오류는 감사 트리거 오류(스키마가 붙어 와도)
        var (s2, e2) = ErrorRules.Classify([new ErrorInfo(8115, 16, "overflow", "[dbo].[dbr_audit_T_U]")], UserTriggers);
        Assert.Equal((StatementStatus.Failed, 8115, true, "[dbo].[dbr_audit_T_U]"), (s2, e2.ErrorNumber, e2.IsToolFault, e2.Procedure));
        // 사용자 트리거 RAISERROR + ROLLBACK → trigger_blocked, 대표는 첫 오류(50000)
        var (s3, e3) = ErrorRules.Classify([new ErrorInfo(50000, 16, "no", "trg_Blk_rb"), new ErrorInfo(3609, 16, "ended", null)], UserTriggers);
        Assert.Equal((StatementStatus.TriggerBlocked, 50000), (s3, e3.ErrorNumber));
        // ROLLBACK만(3609) → trigger_blocked
        Assert.Equal(StatementStatus.TriggerBlocked, ErrorRules.Classify([new ErrorInfo(3609, 16, "ended", null)], UserTriggers).Status);
        // 사용자 트리거 본문의 엔진 오류 → failed(트리거가 거부한 것이 아님, E21 같은 엔진 오류와 같게)
        Assert.Equal(StatementStatus.Failed, ErrorRules.Classify([new ErrorInfo(547, 16, "fk", "trg_Blk_rb")], UserTriggers).Status);
        // 사용자 정의 번호라도 트리거가 아니면(모르는 모듈) failed
        Assert.Equal(StatementStatus.Failed, ErrorRules.Classify([new ErrorInfo(50000, 16, "x", "someProc")], UserTriggers).Status);
    }

    [SqlFact]
    public async Task Executor_classifies_measured_cases()
    {
        await using var b = await ExeBaseline.CustomAsync("q27", Q27Schema.Batches);
        // C6 정적 분석 대역: 3609가 ROLLBACK 트리거에서 났는지 가르는 데 HasRollback을 쓴다(ErrorRules.EndedByRollback)
        var ctx = (await b.ContextAsync()) with { Triggers = [StaticInfo("dbo.trg_Blk_rb", "dbo.Blk", rollback: true), StaticInfo("dbo.trg_Blk_log", "dbo.Blk", rollback: true)] };
        var settings = new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName };

        async Task<RawCapture> Run(params string[] sql) =>
            await new PreviewExecutor(TestDb.ConnectionString(), settings, new ExceptNetDiffer()).ExecuteAsync(Stmts.Sql(sql), ctx, new PreviewOptions(), null, default);

        // R18: 계산 열 오버플로 → 감사 트리거 8115 → failed, isToolFault. XACT_ABORT ON이라 트랜잭션은 이미 롤백(doomed 아님), REVERT 성공, 남은 변경 없음
        var r18 = await Run("UPDATE dbo.Blk SET v = 0 WHERE id = 1;", "UPDATE dbo.Calc SET a = a WHERE id = 2;", "UPDATE dbo.Calc SET a = a WHERE id = 1;");
        Assert.Equal(PreviewStatus.Failed, r18.Status);
        Assert.Equal([StatementStatus.Ok, StatementStatus.Failed, StatementStatus.NotRun], r18.Executions.Select(e => e.Result.Status));
        var e18 = r18.Executions[1].Result.Error!;
        Assert.Equal((8115, "dbr_audit_Calc_U", true), (e18.ErrorNumber, e18.Procedure, e18.IsToolFault));
        Assert.Empty(r18.Headers);
        Assert.All(r18.Executions, e => Assert.Null(e.Result.CaptureSeqRange));
        Assert.Empty(r18.ResidualCommitted);
        Assert.False(r18.SandboxDirty);
        Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));

        (string Sql, PreviewStatus Status, int Number, string? Procedure)[] cases =
        [
            ("UPDATE dbo.Blk SET v = 1 WHERE id = 1;", PreviewStatus.TriggerBlocked, 50000, "trg_Blk_rb"),   // RAISERROR + ROLLBACK(3609)
            ("UPDATE dbo.Blk SET v = 2 WHERE id = 1;", PreviewStatus.TriggerBlocked, 50001, "trg_Blk_throw"), // THROW
            ("UPDATE dbo.Blk SET v = 3 WHERE id = 1;", PreviewStatus.TriggerBlocked, 50000, "trg_Blk_raise"), // RAISERROR만(트랜잭션 살아 있음 → 실행기가 ROLLBACK)
            ("DELETE FROM dbo.Ins WHERE id = 1;", PreviewStatus.TriggerBlocked, 50000, "trg_Ins_iod"),         // INSTEAD OF 거부
            ("UPDATE dbo.Blk SET v = 5 WHERE id = 1;", PreviewStatus.Failed, 547, "trg_Blk_engine"),          // 트리거 본문의 엔진 오류
            ("DELETE FROM dbo.Parent WHERE id = 1;", PreviewStatus.Failed, 547, null),                         // NO ACTION 위반
        ];
        foreach (var c in cases)
        {
            var raw = await Run(c.Sql);
            var err = Assert.Single(raw.Executions).Result.Error!;
            Assert.True((c.Status, c.Number, c.Procedure, false) == (raw.Status, err.ErrorNumber, err.Procedure, err.IsToolFault),
                $"{c.Sql}: {raw.Status} {err.ErrorNumber} {err.Procedure} {err.IsToolFault}");
            Assert.Equal(c.Status == PreviewStatus.TriggerBlocked ? StatementStatus.TriggerBlocked : StatementStatus.Failed, raw.Executions[0].Result.Status);
            Assert.Empty(raw.ResidualCommitted);
            Assert.False(raw.SandboxDirty);
            Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));
        }

        // 트리거 ROLLBACK 뒤 본문 INSERT는 자동 커밋으로 남는다(E13) → trigger_blocked + ResidualCommitted + SandboxDirty
        var dirty = await Run("UPDATE dbo.Blk SET v = 4 WHERE id = 1;");
        Assert.Equal(PreviewStatus.TriggerBlocked, dirty.Status);
        Assert.Null(dirty.Executions[0].Result.Error!.Procedure); // 실측: 3609에는 프로시저가 없다 → 대상 테이블의 트리거로 ROLLBACK 여부를 본다
        Assert.Equal(3609, dirty.Executions[0].Result.Error!.ErrorNumber);
        var residual = Assert.Single(dirty.ResidualCommitted);
        Assert.Equal(("dbo.ErrLog", NetChangeKind.Inserted), (residual.Table, residual.Change));
        Assert.True(dirty.SandboxDirty);
    }

    static TriggerStaticInfo StaticInfo(string trigger, string table, bool rollback = false, bool control = false) => new()
    {
        Trigger = trigger, ObjectId = 0, Table = table, Events = [ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete],
        IsInsteadOf = false, IsDisabled = false, IsEncrypted = false, IsAuditTrigger = false, HasDynamicSql = false, HasRollback = rollback, HasTransactionControl = control,
    };

    /// <summary>
    /// 검토 재현(2026-11-10): 트리거의 "COMMIT TRAN; BEGIN TRAN;"은 오류 없이 @@TRANCOUNT 1을 남겨 확정된 변경을 숨기고, "COMMIT TRAN;"만이면 3609로 끝난다.
    /// 둘 다 ok가 아니고 SandboxDirty이며 롤백 뒤 남은 변경(ResidualCommitted)에 그 행이 나와야 한다. 분석기 거부는 통합 테스트(Analysis 참조)가 본다.
    /// </summary>
    [SqlFact]
    public async Task Trigger_commit_is_failed_and_dirty_not_ok_or_trigger_blocked()
    {
        await using var b = await ExeBaseline.CustomAsync("commit",
            """
            CREATE TABLE dbo.Plain (id int NOT NULL CONSTRAINT PK_Plain PRIMARY KEY);
            CREATE TABLE dbo.Cb (id int NOT NULL CONSTRAINT PK_Cb PRIMARY KEY);
            CREATE TABLE dbo.Co (id int NOT NULL CONSTRAINT PK_Co PRIMARY KEY);
            """,
            "CREATE TRIGGER dbo.trg_Cb ON dbo.Cb AFTER INSERT AS BEGIN SET NOCOUNT ON; COMMIT TRAN; BEGIN TRAN; END;",
            "CREATE TRIGGER dbo.trg_Co ON dbo.Co AFTER INSERT AS BEGIN SET NOCOUNT ON; COMMIT TRAN; END;");
        var ctx = (await b.ContextAsync()) with { Triggers = [StaticInfo("dbo.trg_Cb", "dbo.Cb", control: true), StaticInfo("dbo.trg_Co", "dbo.Co", control: true)] };
        var settings = new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName };
        async Task<RawCapture> Run(params string[] sql) =>
            await new PreviewExecutor(TestDb.ConnectionString(), settings, new ExceptNetDiffer()).ExecuteAsync(Stmts.Sql(sql), ctx, new PreviewOptions(), null, default);

        // COMMIT; BEGIN TRAN; → 트랜잭션 ID가 바뀜 → failed(도구 결함 아님), 앞 문장(Plain)까지 확정됐으므로 둘 다 남은 변경
        var cb = await Run("INSERT INTO dbo.Plain (id) VALUES (1);", "INSERT INTO dbo.Cb (id) VALUES (1);", "INSERT INTO dbo.Plain (id) VALUES (2);");
        Assert.Equal(PreviewStatus.Failed, cb.Status);
        Assert.Equal([StatementStatus.Ok, StatementStatus.Failed, StatementStatus.NotRun], cb.Executions.Select(e => e.Result.Status));
        var ecb = cb.Executions[1].Result.Error!;
        Assert.Equal((0, false), (ecb.ErrorNumber, ecb.IsToolFault));
        Assert.Contains("트랜잭션 ID", ecb.Message);
        Assert.True(cb.SandboxDirty);
        Assert.Contains(cb.ResidualCommitted, n => n.Table == "dbo.Cb" && n.Change == NetChangeKind.Inserted);
        Assert.Contains(cb.ResidualCommitted, n => n.Table == "dbo.Plain" && n.Change == NetChangeKind.Inserted);

        // COMMIT만 → 3609, ROLLBACK 트리거가 아니고 남은 변경이 있으니 trigger_blocked가 아니라 failed
        var co = await Run("INSERT INTO dbo.Co (id) VALUES (1);");
        Assert.Equal(PreviewStatus.Failed, co.Status);
        var eco = Assert.Single(co.Executions).Result;
        Assert.Equal((StatementStatus.Failed, 3609), (eco.Status, eco.Error!.ErrorNumber));
        Assert.True(co.SandboxDirty);
        Assert.Contains(co.ResidualCommitted, n => n.Table == "dbo.Co" && n.Change == NetChangeKind.Inserted);

        // 순수 판정: 3609의 트리거가 ROLLBACK 트리거로 확인될 때만 trigger_blocked를 유지한다
        var rb = ctx with { Triggers = [StaticInfo("dbo.trg_Blk_log", "dbo.Blk", rollback: true)] };
        Assert.True(ErrorRules.EndedByRollback(null, "dbo.Blk", ChangeOperation.Update, rb));
        Assert.True(ErrorRules.EndedByRollback("[dbo].[trg_Blk_log]", "dbo.Other", ChangeOperation.Update, rb));
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.Co", ChangeOperation.Insert, ctx));        // COMMIT 트리거
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.Plain", ChangeOperation.Insert, ctx));     // 트리거를 모름
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.Co", ChangeOperation.Insert,
            ctx with { Triggers = [StaticInfo("dbo.trg_X", "dbo.Co", rollback: true), StaticInfo("dbo.trg_Y", "dbo.Co", control: true)] })); // 섞이면 닫힌 쪽
    }
}
