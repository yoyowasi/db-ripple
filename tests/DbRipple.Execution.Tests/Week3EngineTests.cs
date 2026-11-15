using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Execution.Tests.Support;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Execution.Tests;

/// <summary>
/// 3주차 engine-1(작성 2026-11-11, 이번 라운드는 실행하지 않음): AD6 StatementRunner, 인계 5(3609 남은 두 경우), 인계 7(실패 경로 잔여 확인 실패), Q33 rowsAffected.
/// 테스트 라운드 1순위는 StatementRunner와 PreviewExecutor 결과 대조다(week3-plan §8 위험 1).
/// </summary>
[Collection(DbCollection.Name)]
public class Week3EngineTests(LabFixture lab)
{
    // ---------- 순수 판정(DB 없음) ----------

    static TriggerStaticInfo Info(string trigger, string table, ChangeOperation[]? events = null, bool rollback = false, bool control = false,
        string[]? writes = null, bool disabled = false, string[]? calls = null, string[]? expanded = null, string[]? unresolved = null) => new()
    {
        Trigger = trigger, ObjectId = 0, Table = table, Events = events ?? [ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete],
        IsInsteadOf = false, IsDisabled = disabled, IsEncrypted = false, IsAuditTrigger = false, HasDynamicSql = false,
        HasRollback = rollback, HasTransactionControl = control, WriteTargets = writes ?? [],
        ProcedureCalls = calls ?? [], ExpandedProcedureCalls = expanded ?? [], UnresolvedProcedureCalls = unresolved ?? [],
    };

    /// <summary>dbo.P(1) ← dbo.C(2) ON DELETE CASCADE(fkDisabled면 비활성), dbo.Log(3), dbo.X(4).</summary>
    static BaselineContext Ctx(bool fkDisabled = false, params TriggerStaticInfo[] triggers) => new()
    {
        OriginDb = "O", SandboxDb = "S", SnapshotDb = "B",
        BackupFinishedAt = DateTimeOffset.Parse("2026-11-11T00:00:00+09:00"),
        Fingerprint = new Fingerprint("17.0", "Developer", 170, false, 1, 0, 5432, "SQL_Latin1_General_CP1_CI_AS"),
        FingerprintMatch = true,
        Catalog = new DbCatalog(
            [new(1, "dbo", "P", TemporalType.None, false), new(2, "dbo", "C", TemporalType.None, false), new(3, "dbo", "Log", TemporalType.None, false), new(4, "dbo", "X", TemporalType.None, false)],
            [], [],
            [new(10, "FK_C_P", 2, 1, "CASCADE", "NO_ACTION", fkDisabled, false, 1, "p_id", "id")],
            [], []),
        Triggers = triggers,
        Manifest = new CaptureManifest("t", [], [], []),
    };

    [Fact]
    public void Ended_by_rollback_sees_cascade_target_triggers()
    {
        // 인계 5 경우 1: 대상(P)에는 트리거가 없고 연쇄 대상(C)의 ROLLBACK 트리거가 3609를 냄 → 예전 규칙은 후보 0개라 false(failed)였다
        Assert.True(ErrorRules.EndedByRollback(null, "dbo.P", ChangeOperation.Delete, Ctx(false, Info("dbo.trC", "dbo.C", rollback: true))));
        // 연쇄 대상에 COMMIT 트리거가 섞이면 닫힌 쪽
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.P", ChangeOperation.Delete,
            Ctx(false, Info("dbo.trC", "dbo.C", rollback: true), Info("dbo.trC2", "dbo.C", control: true))));
        // 비활성 FK면 C에 닿지 않는다(E22) → 후보 없음
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.P", ChangeOperation.Delete, Ctx(true, Info("dbo.trC", "dbo.C", rollback: true))));
        // 비활성 트리거는 후보가 아니다(E22)
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.P", ChangeOperation.Delete, Ctx(false, Info("dbo.trC", "dbo.C", rollback: true, disabled: true))));
        // 트리거 정적 쓰기 대상을 따라간다: P 트리거가 Log에 쓰고 Log 트리거가 ROLLBACK
        Assert.True(ErrorRules.EndedByRollback(null, "dbo.X", ChangeOperation.Update,
            Ctx(false, Info("dbo.trX", "dbo.X", writes: ["dbo.Log"]), Info("dbo.trLog", "dbo.Log", events: [ChangeOperation.Insert], rollback: true))));
        // 첫 테이블은 연산이 맞는 트리거만(예전 규칙 유지): X의 INSERT 트리거는 UPDATE 문장 후보가 아니다
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.X", ChangeOperation.Update,
            Ctx(false, Info("dbo.trX", "dbo.X", events: [ChangeOperation.Insert], rollback: true))));
    }

    [Fact]
    public void Ended_by_rollback_treats_unresolved_procedures_as_unknown_with_q38_compat()
    {
        var rb = Info("dbo.trC", "dbo.C", rollback: true);
        // 펼친 프로시저만 부르면 확인됨
        Assert.True(ErrorRules.EndedByRollback(null, "dbo.C", ChangeOperation.Delete,
            Ctx(false, rb with { ProcedureCalls = ["dbo.uspLogError"], ExpandedProcedureCalls = ["dbo.uspLogError"] })));
        // 못 펼친 프로시저(COMMIT할 수도 있음) → 닫힌 쪽
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.C", ChangeOperation.Delete,
            Ctx(false, rb with { ProcedureCalls = ["dbo.p"], UnresolvedProcedureCalls = ["dbo.p"] })));
        // Q38: 두 목록이 비고 ProcedureCalls만 있으면(펼치기 전 분석기) 전부 미상
        Assert.False(ErrorRules.EndedByRollback(null, "dbo.C", ChangeOperation.Delete, Ctx(false, rb with { ProcedureCalls = ["dbo.p"] })));
        Assert.Equal(["dbo.p"], ErrorRules.UnresolvedProcedureCalls(rb with { ProcedureCalls = ["dbo.p"] }));
        Assert.Empty(ErrorRules.UnresolvedProcedureCalls(rb with { ProcedureCalls = ["dbo.p"], ExpandedProcedureCalls = ["dbo.p"] }));
    }

    [Fact]
    public void Ended_by_rollback_adds_named_trigger_from_error_procedure()
    {
        // 인계 5 경우 2(COMMIT 뒤 RAISERROR): 대표 오류 50000의 프로시저가 트리거 이름이다. 그 트리거가 COMMIT 트리거면 false → failed
        var ctx = Ctx(false, Info("dbo.trCo", "dbo.X", control: true));
        Assert.False(ErrorRules.EndedByRollback("trCo", "dbo.Other", ChangeOperation.Update, ctx));
        Assert.True(ErrorRules.EndedByRollback("trRb", "dbo.Other", ChangeOperation.Update, Ctx(false, Info("dbo.trRb", "dbo.X", rollback: true))));
    }

    [Fact]
    public void Transaction_replaced_forces_failed_but_keeps_error()
    {
        var err = new StatementError { ErrorNumber = 50000, Message = "x", Procedure = "trCb", IsToolFault = false };
        Assert.Equal((StatementStatus.Failed, err), PreviewExecutor.Replaced((StatementStatus.TriggerBlocked, err), true));
        Assert.Equal((StatementStatus.TriggerBlocked, err), PreviewExecutor.Replaced((StatementStatus.TriggerBlocked, err), false));
    }

    [Fact]
    public void Residual_check_message_is_bounded_and_names_the_type()
    {
        var m = PreviewExecutor.ResidualCheckMessage(new InvalidOperationException(new string('x', 2000)));
        Assert.StartsWith("InvalidOperationException: ", m);
        Assert.Equal(500, m.Length);
    }

    /// <summary>
    /// 검토 reviewer-2(2026-11-11): REVERT가 실패해 대체 경로가 열린 트랜잭션을 ROLLBACK하면 TransactionOpen = false, Status = failed(도구 결함)여야 한다.
    /// 예전에는 TransactionOpen = true, ok로 돌아가 ApplyService가 트랜잭션 없이 다음 문장을 실행해 원본에 자동 커밋할 수 있었다.
    /// </summary>
    [Fact]
    public void Revert_fallback_rollback_is_failed_and_closes_transaction()
    {
        var st = Stmts.Sql("UPDATE dbo.X SET a = 1 WHERE id = 1;").Statements[0];
        var context = new StatementRunContext { Baseline = Ctx(), TransactionId = 7 };
        var fallback = new StatementRunner.RevertFallback(3930, "uncommittable", 0);

        // 오류 없는 문장 + 열린 트랜잭션(@@TRANCOUNT 1) + REVERT 대체 경로 ROLLBACK → failed, 닫힘
        var o = StatementRunner.Decide(st, context, null, 1, 1, 1, 7, fallback);
        Assert.Equal((StatementStatus.Failed, false, false), (o.Result.Status, o.TransactionOpen, o.TransactionReplaced));
        Assert.Equal((3930, true), (o.Result.Error!.ErrorNumber, o.Result.Error.IsToolFault));
        Assert.Contains("REVERT", o.Result.Error.Message);
        Assert.Null(o.Result.RowsAffected);
        Assert.Null(o.TriggerRollbackConfirmed);

        // 문장 오류가 있었어도(RAISERROR만, 트랜잭션 살아 있음) 대체 경로가 ROLLBACK했으면 failed이고 문장 오류를 메시지에 남긴다
        var raised = (StatementStatus.TriggerBlocked, new StatementError { ErrorNumber = 50000, Message = "blk", Procedure = "trg", IsToolFault = false });
        var o2 = StatementRunner.Decide(st, context, raised, null, 1, 1, 7, fallback);
        Assert.Equal((StatementStatus.Failed, false, 3930), (o2.Result.Status, o2.TransactionOpen, o2.Result.Error!.ErrorNumber));
        Assert.Contains("50000", o2.Result.Error.Message);

        // 트랜잭션이 이미 끝난 뒤(@@TRANCOUNT 0) REVERT만 다시 했으면 일반 판정: 오류 없음 → "상태가 예상과 다름" failed, 오류 있음 → ErrorRules 판정 그대로
        var o3 = StatementRunner.Decide(st, context, null, 1, 1, 0, null, fallback);
        Assert.Equal((StatementStatus.Failed, false, 0), (o3.Result.Status, o3.TransactionOpen, o3.Result.Error!.ErrorNumber));
        var fk = (StatementStatus.Failed, new StatementError { ErrorNumber = 547, Message = "fk", IsToolFault = false });
        var o4 = StatementRunner.Decide(st, context, fk, null, 1, 0, null, fallback);
        Assert.Equal((StatementStatus.Failed, false, 547), (o4.Result.Status, o4.TransactionOpen, o4.Result.Error!.ErrorNumber));
    }

    /// <summary>대조: REVERT가 성공하면(대체 경로 없음) 예전 판정과 같다(ok, 트랜잭션 ID 바뀜 → failed, @@TRANCOUNT 2 → 도구 결함).</summary>
    [Fact]
    public void Decide_without_revert_fallback_keeps_envelope_rules()
    {
        var st = Stmts.Sql("UPDATE dbo.X SET a = 1 WHERE id = 1;").Statements[0];
        var context = new StatementRunContext { Baseline = Ctx(), TransactionId = 7 };
        var ok = StatementRunner.Decide(st, context, null, 3, 1, 1, 7, null);
        Assert.Equal((StatementStatus.Ok, (long?)3, true, false), (ok.Result.Status, ok.Result.RowsAffected, ok.TransactionOpen, ok.TransactionReplaced));
        var replaced = StatementRunner.Decide(st, context, null, 3, 1, 1, 8, null);
        Assert.Equal((StatementStatus.Failed, true, true, false), (replaced.Result.Status, replaced.TransactionOpen, replaced.TransactionReplaced, replaced.Result.Error!.IsToolFault));
        var nested = StatementRunner.Decide(st, context, null, 3, 1, 2, 7, null);
        Assert.Equal((StatementStatus.Failed, true, true), (nested.Result.Status, nested.TransactionOpen, nested.Result.Error!.IsToolFault));
        var blocked = StatementRunner.Decide(st, context,
            (StatementStatus.TriggerBlocked, new StatementError { ErrorNumber = 3609, Message = "ended", IsToolFault = false }), null, 1, 0, null, null);
        Assert.Equal((StatementStatus.TriggerBlocked, false, (bool?)false), (blocked.Result.Status, blocked.TransactionOpen, blocked.TriggerRollbackConfirmed)); // dbo.X에 ROLLBACK 트리거 없음
    }

    [Fact]
    public void Statement_runner_shape()
    {
        Assert.NotNull(typeof(StatementRunner).GetConstructor(Type.EmptyTypes));
        Assert.IsAssignableFrom<IStatementRunner>(new StatementRunner());
        Assert.EndsWith("SELECT ROWCOUNT_BIG();", StatementRunner.RowCountSuffix);
    }

    // ---------- DB(SqlFact) ----------

    static SqlConnection Dedicated(string database) =>
        new(new SqlConnectionStringBuilder(TestDb.ConnectionString(database)) { Pooling = false }.ConnectionString);

    static async Task EnsurePreviewUserAsync(SqlConnection conn, BaselineContext ctx)
    {
        var schemas = ctx.Catalog.ToCaptureInput().Where(t => !AuditTriggerGenerator.IsCaptureStore(t)).Select(t => t.Schema);
        await using var cmd = new SqlCommand(PreviewUserSql.EnsureBatch(schemas), conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<long> ScalarLong(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Runner 기본 흐름(q27 스키마): Prepare → BEGIN TRAN → 문장. ok는 RowsAffected, 547은 failed + 트랜잭션 끝, ROLLBACK 트리거는 trigger_blocked + 확인됨,
    /// RAISERROR만은 trigger_blocked + 트랜잭션 살아 있음. 매번 REVERT되어 USER_NAME()이 dbo다. PreviewExecutor와 상태·오류 번호가 같아야 한다(대조).
    /// </summary>
    [SqlFact]
    public async Task Runner_matches_preview_executor_on_q27_cases()
    {
        await using var b = await ExeBaseline.CustomAsync("run", Q27Schema.Batches);
        var ctx = (await b.ContextAsync()) with
        {
            Triggers = [Info("dbo.trg_Blk_rb", "dbo.Blk", rollback: true), Info("dbo.trg_Blk_log", "dbo.Blk", rollback: true, writes: ["dbo.ErrLog"])],
        };
        var runner = new StatementRunner();
        string[] sqls =
        [
            "UPDATE dbo.Blk SET v = 0 WHERE id = 1;",    // ok, 1행
            "DELETE FROM dbo.Parent WHERE id = 1;",      // 547
            "UPDATE dbo.Blk SET v = 1 WHERE id = 1;",    // RAISERROR + ROLLBACK
            "UPDATE dbo.Blk SET v = 2 WHERE id = 1;",    // THROW
            "UPDATE dbo.Blk SET v = 3 WHERE id = 1;",    // RAISERROR만
            "DELETE FROM dbo.Ins WHERE id = 1;",         // INSTEAD OF 거부
            "UPDATE dbo.Blk SET v = 5 WHERE id = 1;",    // 트리거 본문 엔진 오류
            "UPDATE dbo.Calc SET a = a WHERE id = 2;",   // 감사 트리거 8115(도구 결함)
        ];
        foreach (var sql in sqls)
        {
            await using var conn = Dedicated(b.Sandbox);
            await conn.OpenAsync();
            await EnsurePreviewUserAsync(conn, ctx);
            var session = new SqlSession { Connection = conn };
            await runner.PrepareAsync(session, default);
            await using (var c = new SqlCommand("BEGIN TRAN;", conn)) await c.ExecuteNonQueryAsync();
            var tranId = await ScalarLong(conn, "SELECT CURRENT_TRANSACTION_ID();");
            var st = Stmts.Sql(sql).Statements[0];
            var outcome = await runner.RunAsync(session, st, new StatementRunContext { Baseline = ctx, ExecuteAsUser = PreviewUserSql.UserName, TransactionId = tranId }, default);
            await using (var c = new SqlCommand("SELECT USER_NAME();", conn)) Assert.Equal("dbo", (string)(await c.ExecuteScalarAsync())!);
            Assert.Equal(outcome.TransactionOpen, await ScalarLong(conn, "SELECT @@TRANCOUNT;") > 0);
            Assert.False(outcome.TransactionReplaced);
            Assert.Null(outcome.Result.CaptureSeqRange);
            await using (var c = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK;", conn)) await c.ExecuteNonQueryAsync();

            var raw = await new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName }, new ExceptNetDiffer())
                .ExecuteAsync(Stmts.Sql(sql), ctx, new PreviewOptions(), null, default);
            var exe = Assert.Single(raw.Executions).Result;
            Assert.True((exe.Status, exe.Error?.ErrorNumber, exe.Error?.Procedure, exe.Error?.IsToolFault)
                == (outcome.Result.Status, outcome.Result.Error?.ErrorNumber, outcome.Result.Error?.Procedure, outcome.Result.Error?.IsToolFault),
                $"{sql}: executor {exe.Status} {exe.Error?.ErrorNumber} / runner {outcome.Result.Status} {outcome.Result.Error?.ErrorNumber}");
            if (outcome.Result.Status == StatementStatus.Ok) Assert.Equal(exe.RowsAffected, outcome.Result.RowsAffected); // NOCOUNT ON 트리거뿐이라 같아야 한다
            Assert.Equal(outcome.Result.Status == StatementStatus.TriggerBlocked, outcome.TriggerRollbackConfirmed is not null);
        }
    }

    [SqlFact]
    public async Task Runner_reports_open_transaction_and_rollback_confirmation()
    {
        await using var b = await ExeBaseline.CustomAsync("run2", Q27Schema.Batches);
        var ctx = (await b.ContextAsync()) with { Triggers = [Info("dbo.trg_Blk_rb", "dbo.Blk", rollback: true)] };
        var runner = new StatementRunner();
        async Task<StatementRunOutcome> Run(string sql)
        {
            await using var conn = Dedicated(b.Sandbox);
            await conn.OpenAsync();
            var session = new SqlSession { Connection = conn };
            await runner.PrepareAsync(session, default);
            await using (var c = new SqlCommand("BEGIN TRAN;", conn)) await c.ExecuteNonQueryAsync();
            var o = await runner.RunAsync(session, Stmts.Sql(sql).Statements[0], new StatementRunContext { Baseline = ctx }, default);
            await using (var c = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK;", conn)) await c.ExecuteNonQueryAsync();
            return o;
        }
        var ok = await Run("UPDATE dbo.Blk SET v = 0 WHERE id = 1;");
        Assert.Equal((StatementStatus.Ok, (long?)1, true), (ok.Result.Status, ok.Result.RowsAffected, ok.TransactionOpen));
        var fk = await Run("DELETE FROM dbo.Parent WHERE id = 1;");
        Assert.Equal((StatementStatus.Failed, 547, false), (fk.Result.Status, fk.Result.Error!.ErrorNumber, fk.TransactionOpen));
        Assert.Null(fk.TriggerRollbackConfirmed);
        var rb = await Run("UPDATE dbo.Blk SET v = 1 WHERE id = 1;");
        Assert.Equal((StatementStatus.TriggerBlocked, false, (bool?)true), (rb.Result.Status, rb.TransactionOpen, rb.TriggerRollbackConfirmed));
        var raise = await Run("UPDATE dbo.Blk SET v = 3 WHERE id = 1;");
        Assert.Equal((StatementStatus.TriggerBlocked, true), (raise.Result.Status, raise.TransactionOpen)); // 트랜잭션 살아 있음 → 호출자가 ROLLBACK
        Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));
    }

    /// <summary>E20·AD9: 연결이 안 열렸으면 SandboxUnavailableException(정확한 형식), @@OPTIONS가 어긋나면 InvalidOperationException.</summary>
    [SqlFact]
    public async Task Runner_exceptions()
    {
        await using var b = await ExeBaseline.CustomAsync("run3", Q27Schema.Batches);
        var ctx = await b.ContextAsync();
        var runner = new StatementRunner();
        await using var closed = Dedicated(b.Sandbox);
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => runner.PrepareAsync(new SqlSession { Connection = closed }, default));
        await Assert.ThrowsAsync<SandboxUnavailableException>(() =>
            runner.RunAsync(new SqlSession { Connection = closed }, Stmts.Sql("UPDATE dbo.Blk SET v = 0 WHERE id = 1;").Statements[0], new StatementRunContext { Baseline = ctx }, default));

        await using var conn = Dedicated(b.Sandbox);
        await conn.OpenAsync();
        var session = new SqlSession { Connection = conn };
        await runner.PrepareAsync(session, default);
        await using (var c = new SqlCommand("SET XACT_ABORT OFF;", conn)) await c.ExecuteNonQueryAsync();
        // Prepare는 다시 맞추므로 통과한다(점검은 적용 뒤 값)
        await runner.PrepareAsync(session, default);
        // 없는 실행 사용자 → 봉투 명령 실패 = InvalidOperationException(연결 끊김 아님)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(session, Stmts.Sql("UPDATE dbo.Blk SET v = 0 WHERE id = 1;").Statements[0], new StatementRunContext { Baseline = ctx, ExecuteAsUser = "no_such_user_x" }, default));
        Assert.IsNotType<SandboxUnavailableException>(ex);
    }

    /// <summary>
    /// Q33: NOCOUNT OFF 트리거가 다른 테이블을 바꾸면 드라이버 영향 행 수(ExecuteNonQuery)는 트리거 문장까지 더해 과대 집계한다. Runner는 ROWCOUNT_BIG()으로 문장 행 수만 낸다.
    /// PreviewExecutor도 같은 식이어야 한다(D7 1 비교, 오케스트레이터 A안 승인 2026-11-11). Runner로의 전환 자체는 테스트 라운드 뒤.
    /// </summary>
    [SqlFact]
    public async Task Runner_rows_affected_ignores_nocount_off_trigger_rows()
    {
        await using var b = await ExeBaseline.CustomAsync("q33",
            """
            CREATE TABLE dbo.Src (id int NOT NULL CONSTRAINT PK_Src PRIMARY KEY, v int NOT NULL);
            CREATE TABLE dbo.Side (id int NOT NULL CONSTRAINT PK_Side PRIMARY KEY, n int NOT NULL);
            INSERT INTO dbo.Src (id, v) VALUES (1, 0), (2, 0);
            INSERT INTO dbo.Side (id, n) VALUES (1, 0), (2, 0), (3, 0);
            """,
            "CREATE TRIGGER dbo.trg_Src ON dbo.Src AFTER UPDATE AS BEGIN UPDATE dbo.Side SET n = n + 1; END;");
        var ctx = await b.ContextAsync();
        var runner = new StatementRunner();
        await using var conn = Dedicated(b.Sandbox);
        await conn.OpenAsync();
        var session = new SqlSession { Connection = conn };
        await runner.PrepareAsync(session, default);
        await using (var c = new SqlCommand("BEGIN TRAN;", conn)) await c.ExecuteNonQueryAsync();
        var o = await runner.RunAsync(session, Stmts.Sql("UPDATE dbo.Src SET v = 1 WHERE id = 1;").Statements[0], new StatementRunContext { Baseline = ctx }, default);
        await using (var c = new SqlCommand("ROLLBACK;", conn)) await c.ExecuteNonQueryAsync();
        Assert.Equal((StatementStatus.Ok, (long?)1), (o.Result.Status, o.Result.RowsAffected));

        var raw = await new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings(), new ExceptNetDiffer())
            .ExecuteAsync(Stmts.Sql("UPDATE dbo.Src SET v = 1 WHERE id = 1;"), ctx, new PreviewOptions(), null, default);
        var exeRows = Assert.Single(raw.Executions).Result.RowsAffected;
        Assert.Equal(o.Result.RowsAffected, exeRows); // A안(2026-11-11): 두 경로가 같은 식(ROWCOUNT_BIG). 예전 ExecuteNonQuery라면 4(1 + Side 3)
    }

    /// <summary>
    /// 인계 5 경우 1: 연쇄 대상(Child)의 ROLLBACK 트리거가 CATCH 대신 ROLLBACK 뒤 로그를 자동 커밋으로 남긴다 → 3609, 프로시저 없음.
    /// 예전 규칙은 대상(Parent) 트리거만 봐서 failed였다. 이제 trigger_blocked + ResidualCommitted(ErrLog) + SandboxDirty.
    /// </summary>
    [SqlFact]
    public async Task Cascade_target_rollback_trigger_with_autocommitted_log_is_trigger_blocked()
    {
        await using var b = await ExeBaseline.CustomAsync("h5a",
            """
            CREATE TABLE dbo.P (id int NOT NULL CONSTRAINT PK_P PRIMARY KEY);
            CREATE TABLE dbo.C (id int NOT NULL CONSTRAINT PK_C PRIMARY KEY, p_id int NOT NULL CONSTRAINT FK_C_P REFERENCES dbo.P (id) ON DELETE CASCADE);
            CREATE TABLE dbo.ErrLog (id int IDENTITY CONSTRAINT PK_ErrLog PRIMARY KEY, msg nvarchar(100) NOT NULL);
            INSERT INTO dbo.P (id) VALUES (1);
            INSERT INTO dbo.C (id, p_id) VALUES (10, 1);
            """,
            """
            CREATE TRIGGER dbo.trg_C_del ON dbo.C AFTER DELETE AS
            BEGIN
                SET NOCOUNT ON;
                BEGIN TRY
                    RAISERROR (N'no child delete', 16, 1);
                END TRY
                BEGIN CATCH
                    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                    INSERT INTO dbo.ErrLog (msg) VALUES (ERROR_MESSAGE());
                END CATCH;
            END;
            """);
        var ctx = (await b.ContextAsync()) with
        {
            Triggers = [Info("dbo.trg_C_del", "dbo.C", events: [ChangeOperation.Delete], rollback: true, writes: ["dbo.ErrLog"])],
        };
        var raw = await new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName }, new ExceptNetDiffer())
            .ExecuteAsync(Stmts.Sql("DELETE FROM dbo.P WHERE id = 1;"), ctx, new PreviewOptions(), null, default);
        Assert.Equal(PreviewStatus.TriggerBlocked, raw.Status);
        var r = Assert.Single(raw.Executions).Result;
        Assert.Equal((StatementStatus.TriggerBlocked, 3609), (r.Status, r.Error!.ErrorNumber));
        Assert.Contains(raw.ResidualCommitted, n => n.Table == "dbo.ErrLog" && n.Change == NetChangeKind.Inserted);
        Assert.True(raw.SandboxDirty);
        Assert.False(raw.ResidualCheckFailed);
    }

    /// <summary>
    /// 인계 5 경우 2: 트리거가 COMMIT 뒤 RAISERROR → 오류 50000(트리거) + 3609, 대표 오류는 50000이라 예전 3609 보정이 안 걸려 trigger_blocked로 남았다.
    /// 이제 failed + ResidualCommitted + SandboxDirty. "COMMIT; BEGIN TRAN; RAISERROR"(3609 없음, 트랜잭션 ID 바뀜)도 failed.
    /// </summary>
    [SqlFact]
    public async Task Commit_then_raiserror_trigger_is_failed_and_dirty()
    {
        await using var b = await ExeBaseline.CustomAsync("h5b",
            """
            CREATE TABLE dbo.Cr (id int NOT NULL CONSTRAINT PK_Cr PRIMARY KEY);
            CREATE TABLE dbo.Cb (id int NOT NULL CONSTRAINT PK_Cb PRIMARY KEY);
            """,
            "CREATE TRIGGER dbo.trg_Cr ON dbo.Cr AFTER INSERT AS BEGIN SET NOCOUNT ON; COMMIT TRAN; RAISERROR (N'after commit', 16, 1); END;",
            "CREATE TRIGGER dbo.trg_Cb ON dbo.Cb AFTER INSERT AS BEGIN SET NOCOUNT ON; COMMIT TRAN; BEGIN TRAN; RAISERROR (N'after commit, new tran', 16, 1); END;");
        var ctx = (await b.ContextAsync()) with
        {
            Triggers = [Info("dbo.trg_Cr", "dbo.Cr", events: [ChangeOperation.Insert], control: true), Info("dbo.trg_Cb", "dbo.Cb", events: [ChangeOperation.Insert], control: true)],
        };
        async Task<RawCapture> Run(string sql) =>
            await new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName }, new ExceptNetDiffer())
                .ExecuteAsync(Stmts.Sql(sql), ctx, new PreviewOptions(), null, default);

        var cr = await Run("INSERT INTO dbo.Cr (id) VALUES (1);");
        Assert.Equal(PreviewStatus.Failed, cr.Status);
        var r1 = Assert.Single(cr.Executions).Result;
        Assert.Equal((StatementStatus.Failed, 50000, "trg_Cr"), (r1.Status, r1.Error!.ErrorNumber, r1.Error.Procedure));
        Assert.Contains(cr.ResidualCommitted, n => n.Table == "dbo.Cr" && n.Change == NetChangeKind.Inserted);
        Assert.True(cr.SandboxDirty);

        var cb = await Run("INSERT INTO dbo.Cb (id) VALUES (1);");
        Assert.Equal(PreviewStatus.Failed, cb.Status);
        Assert.Equal(StatementStatus.Failed, Assert.Single(cb.Executions).Result.Status);
        Assert.Contains(cb.ResidualCommitted, n => n.Table == "dbo.Cb" && n.Change == NetChangeKind.Inserted);
        Assert.True(cb.SandboxDirty);
    }

    /// <summary>인계 7(Q31): 실패 경로의 최종 차이가 예외로 끝나도 결과로 돌려준다(ResidualCheckFailed, Message, SandboxDirty, ResidualCommitted 빈 배열).</summary>
    [SqlFact]
    public async Task Residual_check_failure_is_returned_not_thrown()
    {
        await using var b = await ExeBaseline.CustomAsync("h7", Q27Schema.Batches);
        var ctx = await b.ContextAsync();
        var raw = await new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName }, new ThrowingNetDiffer())
            .ExecuteAsync(Stmts.Sql("DELETE FROM dbo.Parent WHERE id = 1;"), ctx, new PreviewOptions(), null, default);
        Assert.Equal(PreviewStatus.Failed, raw.Status);
        Assert.True(raw.ResidualCheckFailed);
        Assert.Contains("residual boom", raw.ResidualCheckMessage);
        Assert.True(raw.SandboxDirty);
        Assert.Empty(raw.ResidualCommitted);
        Assert.Equal(547, Assert.Single(raw.Executions).Result.Error!.ErrorNumber);
    }

    sealed class ThrowingNetDiffer : INetDiffer
    {
        public Task<IReadOnlyList<NetChange>> DiffAsync(SqlSession session, string liveDb, string snapshotDb, IReadOnlyList<TableInfo> tables, CancellationToken ct) =>
            throw new InvalidOperationException("residual boom");
    }

    // ---------- CascadeLab CL09~CL20(lab-1 목록 2026-11-11) ----------

    public static TheoryData<string> Week3Cases()
    {
        var d = new TheoryData<string>();
        foreach (var c in LabCase.All.Where(c => string.CompareOrdinal(c.Id, "CL09") >= 0 && string.CompareOrdinal(c.Id, "CL21") < 0)) d.Add(c.Id);
        return d;
    }

    /// <summary>Analysis.Tests가 쓴 CascadeLab 사용자 트리거 정적 분석(tests/fixtures/capture/triggers-cascadelab.json) 가운데 이 기준선 카탈로그에 있는 테이블의 것.</summary>
    static List<TriggerStaticInfo> LabTriggers(BaselineContext ctx)
    {
        var path = Path.Combine(RepoPaths.Root, "tests", "fixtures", "capture", "triggers-cascadelab.json");
        Assert.True(File.Exists(path), "픽스처 없음: " + path + " (먼저 DBR_WRITE_FIXTURES=1로 Analysis.Tests Write_trigger_static_info_fixture를 돌린다)");
        var tables = ctx.Catalog.Tables.Select(t => t.Schema + "." + t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize<List<TriggerStaticInfo>>(File.ReadAllText(path), FixtureFiles.Options)!
            .Where(t => tables.Contains(t.Table)).ToList();
    }

    static bool LeavesResidue(string id)
    {
        var f = Directory.GetFiles(Path.Combine(RepoPaths.CascadeLab, "cases"), id + "-*.json").Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        return doc.RootElement.TryGetProperty("leavesResidue", out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// CL09~CL20 회귀(Execution: 상태·오류 번호·firings). 롤백 뒤 남는 변경이 기대되는 사례(leavesResidue, CL12a)는 공유 기준선을 오염시키므로 전용 기준선을 새로 만든다.
    /// 그 밖의 사례는 실행 뒤 샌드박스 = 스냅샷이다.
    /// </summary>
    [SqlTheory]
    [MemberData(nameof(Week3Cases))]
    public async Task Week3_cascadelab_firings_match_hand_answers(string id)
    {
        var c = LabCase.Get(id);
        var residue = LeavesResidue(id);
        ExeBaseline? own = null;
        try
        {
            ExeBaseline b;
            BaselineContext ctx;
            if (residue)
            {
                own = await ExeBaseline.CascadeLabAsync(c.Database == "CascadeLab_RT" ? CascadeLabVariant.RecursiveTriggers : CascadeLabVariant.Default);
                b = own;
                ctx = await own.ContextAsync();
            }
            else (b, ctx) = lab.For(c);

            var analysis = Stmts.Sql([.. c.Statements]);
            // 제품(SandboxManager)은 C6 트리거 정적 분석을 문맥에 채운다. 3609의 ROLLBACK 확인(ErrorRules.EndedByRollback)이 이것을 쓰므로
            // 실행에만 Analysis.Tests가 쓴 triggers-cascadelab.json을 넣는다(픽스처 baseline의 Triggers는 빈 배열 그대로).
            var raw = await new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName }, ExceptNetDiffer.Create())
                .ExecuteAsync(analysis, ctx with { Triggers = LabTriggers(ctx) }, new PreviewOptions(), null, default);
            Assert.Equal(c.Status, raw.Status switch
            {
                PreviewStatus.Ok => "ok", PreviewStatus.Failed => "failed", PreviewStatus.TriggerBlocked => "trigger_blocked", _ => "truncated",
            });
            if (c.ErrorNumber is int n) Assert.Equal(n, raw.Executions.First(e => e.Result.Status is not StatementStatus.Ok).Result.Error!.ErrorNumber);
            Assert.False(raw.ResidualCheckFailed);
            if (residue)
            {
                Assert.True(raw.SandboxDirty);
                Assert.NotEmpty(raw.ResidualCommitted);
            }
            else
            {
                Assert.False(raw.SandboxDirty);
                Assert.Empty(raw.ResidualCommitted);
                Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));
            }

            var collected = new ChangeCollector().Collect(raw, analysis, ctx);
            // v0.2: 노드 이름은 문장 안에서만 고유하다(CL12b는 문장 1·2에 같은 이름) → 문장 번호까지 비교한다
            var actual = collected.Firings.Select(f => (f.StatementIndex, f.Table, Op: f.Operation.ToString().ToUpperInvariant(), f.RowCount, f.Depth)).Order().ToList();
            var expected = c.Firings.Select(f => (f.StatementIndex, f.Table, Op: f.Operation, f.RowCount, f.Depth)).Order().ToList();
            foreach (var ef in c.Firings.Where(f => f.Pairing is not null))
            {
                var firing = collected.Firings.Single(f => f.StatementIndex == ef.StatementIndex && f.Table == ef.Table && f.Depth == ef.Depth
                    && f.RowCount == ef.RowCount && f.Operation.ToString().ToUpperInvariant() == ef.Operation);
                var pairings = collected.RowChanges.Where(r => r.FiringId == firing.Id).Select(r => r.Pairing switch
                {
                    Pairing.ByPk => "BY_PK", Pairing.ByUnique => "BY_UNIQUE", Pairing.SingleRow => "SINGLE_ROW", _ => "UNPAIRED",
                }).Distinct().ToList();
                if (pairings.Count > 0) Assert.True(pairings.SequenceEqual([ef.Pairing!]), $"{id} {ef.Node}: 기대 {ef.Pairing}, 실제 {string.Join(",", pairings)}");
            }
            Assert.Equal(expected, actual);
            await FixtureFiles.WriteAsync(id, raw, collected, analysis, ctx, c.Database);
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
    }
}

/// <summary>DBRIPPLE_SQL_CONNECTION이 없으면 건너뛰는 Theory(Analysis.Tests와 같은 모양).</summary>
public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute()
    {
        if (!TestDb.IsConfigured) Skip = $"{TestDb.ConnectionEnvVar} 없음";
    }
}
