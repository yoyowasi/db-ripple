using DbRipple.Contracts;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis.Tests;

/// <summary>
/// EXEC 미리보기 1차(2026-11-13): 호출 모양·대상 프로시저·매개변수 대조·펼친 본문 규칙·도달 폐포·실행 SQL 생성. DB 없이 가짜 카탈로그로 본다.
/// 카탈로그: dbo.T(id 식별, a, b), dbo.U(x, y), Sales.Orders, audit.AuditHeader, dbo.C(id, t_id) → dbo.T ON DELETE CASCADE(FK는 테스트마다 선택).
/// </summary>
public class ExecAnalysisTests
{
    const string P = "CREATE PROCEDURE dbo.p @id int AS DELETE dbo.T WHERE a = @id;";

    static BaselineContext Ctx(Action<CatalogBuilder>? more = null)
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b => { b.Table("dbo", "C", "id", "t_id"); more?.Invoke(b); });
        return ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) };
    }

    static BaselineContext WithProc(string body, Action<CatalogBuilder>? more = null) =>
        Ctx(b => { b.Procedure("dbo", "q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} END;"); more?.Invoke(b); });

    static AnalysisResult Run(string sql, BaselineContext ctx) => new BatchAnalyzer().Analyze(sql, ctx);

    static void AssertRejected(AnalysisResult r, string rule)
    {
        Assert.False(r.IsAccepted);
        Assert.Contains(r.Rejects, d => d.RuleId == rule);
        Assert.All(r.Rejects, d => Assert.False(string.IsNullOrEmpty(d.DocRef)));
    }

    // ---- 허용 ----

    [Fact]
    public void Accepts_named_literal_call_and_fills_statement_and_plan()
    {
        var ctx = Ctx(b => b.Procedure("dbo", "usp_Del",
            "CREATE PROCEDURE dbo.usp_Del @id int, @note nvarchar(10) = N'x' AS BEGIN SET NOCOUNT ON; DELETE dbo.T WHERE a = @id; UPDATE dbo.U SET y = 1; END;"));
        var r = Run("DELETE FROM dbo.T WHERE id = 1;\nEXEC usp_Del @ID = 5;", ctx);
        Assert.True(r.IsAccepted, string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        Assert.Equal(2, r.Statements.Count);
        var s = r.Statements[1];
        Assert.Equal((ChangeOperation.Exec, (string?)null, "dbo.usp_Del"), (s.Kind, s.Target, s.Procedure));
        Assert.Equal(["@ID = 5"], s.Parameters);
        Assert.Equal("EXEC usp_Del @ID = 5;", s.Sql);
        Assert.NotNull(s.Plan);
        Assert.Equal(["dbo.T", "dbo.U"], s.Plan!.WriteTargets);
        // 정의의 철자(@id)로 바꾸고 이름은 대괄호로 인용한다
        Assert.Equal("DECLARE @dbr_rc int, @dbr_tc0 int = @@TRANCOUNT;\nBEGIN TRAN;\nEXEC @dbr_rc = [dbo].[usp_Del] @id = 5;\nDECLARE @dbr_dtc int = @@TRANCOUNT - @dbr_tc0;\nIF @dbr_dtc = 1 COMMIT TRAN;\nSELECT @dbr_rc AS dbr_rc, @dbr_dtc AS dbr_dtc;", s.Plan.ExecSql);
    }

    [Theory]
    [InlineData("@n = -5", "@n = -5")]
    [InlineData("@n = NULL", "@n = NULL")]
    [InlineData("@n = DEFAULT", "@n = DEFAULT")]
    [InlineData("@s = N'it''s'", "@s = N'it''s'")]
    [InlineData("@s = 'x'", "@s = 'x'")]
    [InlineData("@d = 1.5", "@d = 1.5")]
    [InlineData("@d = 1e3", "@d = 1e3")]
    [InlineData("@b = 0x1F", "@b = 0x1F")]
    [InlineData("@m = $5.25", "@m = $5.25")]
    public void Accepts_literal_kinds(string arg, string rendered)
    {
        var ctx = Ctx(b => b.Procedure("dbo", "lit",
            "CREATE PROCEDURE dbo.lit @n int = 0, @s nvarchar(20) = NULL, @d float = 0, @b varbinary(4) = NULL, @m money = 0 AS UPDATE dbo.U SET y = 1;"));
        var r = Run($"EXEC dbo.lit {arg};", ctx);
        Assert.True(r.IsAccepted, string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        Assert.Contains($"[dbo].[lit] {rendered};", r.Statements[0].Plan!.ExecSql);
    }

    [Fact]
    public void Execute_as_caller_is_allowed()
    {
        var ctx = Ctx(b => b.Procedure("dbo", "c", "CREATE PROCEDURE dbo.c WITH EXECUTE AS CALLER AS UPDATE dbo.U SET y = 1;"));
        Assert.True(Run("EXEC dbo.c;", ctx).IsAccepted);
    }

    // ---- 사용자 입력(호출 모양) 거부 ----

    [Theory]
    [InlineData("EXEC @rc = dbo.p @id = 1", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p 1", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p @id = @v OUTPUT", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p @id = 1 OUTPUT", AnalysisRules.RejectParseError)] // 리터럴 OUTPUT·식은 구문 오류
    [InlineData("EXEC dbo.p @id = 1 + 1", AnalysisRules.RejectParseError)]
    [InlineData("EXEC dbo.p @id = abc", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p @id = 1, @id = 2", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p @id = 1 WITH RESULT SETS NONE", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p @id = 1 WITH RECOMPILE", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p;2 @id = 1", AnalysisRules.RejectProcCallForm)]
    [InlineData("EXEC dbo.p @id = @x", AnalysisRules.RejectVariableOrFlow)]
    [InlineData("EXEC dbo.p @id = @@SPID", AnalysisRules.RejectVariableOrFlow)]
    [InlineData("EXEC @proc", AnalysisRules.RejectExec)]
    [InlineData("EXEC('DELETE dbo.T')", AnalysisRules.RejectExec)]
    [InlineData("EXEC sp_executesql N'DELETE dbo.T'", AnalysisRules.RejectExec)]
    [InlineData("INSERT INTO dbo.T (a) EXEC dbo.p @id = 1", AnalysisRules.RejectExec)]
    [InlineData("EXEC OtherDb.dbo.p @id = 1", AnalysisRules.RejectCrossDatabase)]
    [InlineData("EXEC Srv.OtherDb.dbo.p @id = 1", AnalysisRules.RejectCrossDatabase)]
    [InlineData("EXEC sp_who", AnalysisRules.RejectProcNotFound)]
    [InlineData("EXEC dbo.sp_p", AnalysisRules.RejectProcNotFound)]
    [InlineData("EXEC dbo.nope", AnalysisRules.RejectProcNotFound)]
    [InlineData("EXEC sys.p", AnalysisRules.RejectProcNotFound)]
    public void Rejects_unsupported_call_forms(string sql, string rule)
    {
        var r = Run(sql, Ctx(b => b.Procedure("dbo", "p", P).Procedure("dbo", "sp_p", "CREATE PROCEDURE dbo.sp_p AS UPDATE dbo.U SET y = 1;")));
        AssertRejected(r, rule);
    }

    // ---- 매개변수 대조 ----

    [Theory]
    [InlineData("EXEC dbo.p", "기본값 없는 @id")]
    [InlineData("EXEC dbo.p @id = DEFAULT", "기본값 없는 @id")]
    [InlineData("EXEC dbo.p @id = 1, @nope = 2", "@nope")]
    public void Rejects_parameter_mismatch(string sql, string fragment)
    {
        var r = Run(sql, Ctx(b => b.Procedure("dbo", "p", P)));
        AssertRejected(r, AnalysisRules.RejectProcParameter);
        Assert.Contains(fragment, r.Rejects.Single(d => d.RuleId == AnalysisRules.RejectProcParameter).Message);
        Assert.Equal("", Assert.Single(r.Statements).Plan!.ExecSql); // 거부된 호출은 실행 SQL을 만들지 않는다
    }

    [Theory]
    [InlineData("@o int = 0 OUTPUT")]
    [InlineData("@o int OUTPUT")]
    public void Rejects_procedures_with_output_parameters(string param)
    {
        var r = Run("EXEC dbo.o @id = 1;", Ctx(b => b.Procedure("dbo", "o", $"CREATE PROCEDURE dbo.o @id int, {param} AS UPDATE dbo.U SET y = @id;")));
        AssertRejected(r, AnalysisRules.RejectProcParameter);
    }

    // ---- 대상·펼친 본문 거부 ----

    [Theory]
    [InlineData("WITH EXECUTE AS OWNER")]
    [InlineData("WITH EXECUTE AS SELF")]
    [InlineData("WITH EXECUTE AS 'someone'")]
    public void Rejects_execute_as_other_than_caller(string option)
    {
        var r = Run("EXEC dbo.e;", Ctx(b => b.Procedure("dbo", "e", $"CREATE PROCEDURE dbo.e {option} AS UPDATE dbo.U SET y = 1;")));
        AssertRejected(r, AnalysisRules.RejectProcExecuteAs);
    }

    [Fact]
    public void Rejects_execute_as_in_expanded_inner_procedure()
    {
        var r = Run("EXEC dbo.q;", WithProc("EXEC dbo.inner1;",
            b => b.Procedure("dbo", "inner1", "CREATE PROCEDURE dbo.inner1 WITH EXECUTE AS OWNER AS UPDATE dbo.U SET y = 1;")));
        AssertRejected(r, AnalysisRules.RejectProcExecuteAs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CREATE PROCEDURE dbo.x AS UPDATE dbo.U SET y = ;")]
    public void Rejects_opaque_procedure(string? definition)
    {
        var r = Run("EXEC dbo.x;", Ctx(b => b.Procedure("dbo", "x", definition)));
        AssertRejected(r, AnalysisRules.RejectProcOpaque);
    }

    [Theory]
    [InlineData("DELETE dbo.T; EXEC (N'DELETE dbo.U');", AnalysisRules.RejectProcDynamicSql)]
    [InlineData("EXEC sp_executesql N'DELETE dbo.U';", AnalysisRules.RejectProcDynamicSql)]
    [InlineData("EXEC sp_prepexec @h OUTPUT, NULL, N'DELETE dbo.U';", AnalysisRules.RejectProcDynamicSql)]
    [InlineData("DECLARE @p sysname = N'x'; EXEC @p;", AnalysisRules.RejectProcDynamicSql)]
    [InlineData("EXEC dbo.missing;", AnalysisRules.RejectProcUnresolvedCall)]
    [InlineData("SAVE TRAN s1;", AnalysisRules.RejectProcSavepoint)]
    [InlineData("BEGIN TRAN; ROLLBACK TRAN s1;", AnalysisRules.RejectProcSavepoint)]
    [InlineData("COMMIT;", AnalysisRules.RejectProcUnbalancedCommit)]
    [InlineData("BEGIN TRAN; DELETE dbo.T; COMMIT; COMMIT;", AnalysisRules.RejectProcUnbalancedCommit)]
    [InlineData("CREATE TABLE dbo.X (a int);", AnalysisRules.RejectProcDdl)]
    [InlineData("ALTER TABLE dbo.T ADD c int;", AnalysisRules.RejectProcDdl)]
    [InlineData("DROP TABLE dbo.U;", AnalysisRules.RejectProcDdl)]
    [InlineData("SELECT a INTO dbo.Copy FROM dbo.T;", AnalysisRules.RejectProcDdl)]
    [InlineData("TRUNCATE TABLE dbo.T;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("WAITFOR DELAY '00:00:01';", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("DBCC CHECKDB;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("EXECUTE AS USER = 'u'; DELETE dbo.T; REVERT;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("GRANT SELECT ON dbo.T TO public;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("DENY SELECT ON dbo.T TO public;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("REVOKE SELECT ON dbo.T TO public;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("BACKUP DATABASE x TO DISK = 'c:\\x.bak';", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("KILL 55;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("SHUTDOWN;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("RECONFIGURE;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("UPDATE STATISTICS dbo.T;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("SET LOCK_TIMEOUT 10;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("BEGIN DISTRIBUTED TRANSACTION; COMMIT;", AnalysisRules.RejectProcStatementNotAllowed)]
    [InlineData("BULK INSERT dbo.T FROM 'c:\\x.csv';", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("RAISERROR('x', 10, 1) WITH LOG;", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("EXEC sp_setapprole 'r', 'p';", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("EXEC sp_getapplock @Resource = 'x', @LockMode = 'Exclusive';", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("EXEC master.dbo.xp_cmdshell 'dir';", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("EXEC msdb.dbo.sp_send_dbmail @recipients = 'a@b.c';", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("DECLARE @h uniqueidentifier; SEND ON CONVERSATION @h (N'x');", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("DECLARE @h uniqueidentifier; END CONVERSATION @h;", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("DECLARE @h uniqueidentifier; BEGIN DIALOG @h FROM SERVICE s TO SERVICE 't';", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("RECEIVE TOP (1) * FROM dbo.Q;", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("DELETE OtherDb.dbo.T;", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("DELETE dbo.T WHERE a IN (SELECT a FROM OtherDb.dbo.U);", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("INSERT Srv.Db.dbo.T (a) VALUES (1);", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("SELECT * FROM OPENQUERY(Lnk, 'select 1');", AnalysisRules.RejectProcExternalEffect)]
    [InlineData("BEGIN TRY IF @@TRANCOUNT > 0 DELETE dbo.T; END TRY BEGIN CATCH THROW; END CATCH;", AnalysisRules.RejectProcTrancountBranch)]
    [InlineData("BEGIN TRY IF XACT_STATE() = 1 DELETE dbo.T; END TRY BEGIN CATCH THROW; END CATCH;", AnalysisRules.RejectProcTrancountBranch)]
    [InlineData("IF @@TRANCOUNT = 0 BEGIN TRAN; DELETE dbo.T;", AnalysisRules.RejectProcTrancountBranch)]
    [InlineData("DELETE dbo.V;", AnalysisRules.RejectProcWriteTarget)]
    [InlineData("DELETE audit.AuditHeader;", AnalysisRules.RejectProcWriteTarget)]
    public void Rejects_procedure_body(string body, string rule)
    {
        var r = Run("EXEC dbo.q;", WithProc(body));
        AssertRejected(r, rule);
        var s = Assert.Single(r.Statements); // 거부돼도 표시용 문장은 남는다
        Assert.Equal("dbo.q", s.Procedure);
    }

    [Fact]
    public void Temp_tables_and_tempdb_are_not_rejected()
    {
        var r = Run("EXEC dbo.q;", WithProc(
            "CREATE TABLE #t (a int); CREATE INDEX ix ON #t (a); INSERT #t (a) SELECT a FROM dbo.T; SELECT a INTO #u FROM #t; " +
            "IF OBJECT_ID('tempdb..#t') IS NOT NULL DROP TABLE #t; DELETE dbo.T WHERE a IN (SELECT a FROM tempdb..#u);"));
        Assert.True(r.IsAccepted, string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        Assert.Contains(r.Warnings, d => d.RuleId == AnalysisRules.WarnProcTempTable);
    }

    [Theory]
    [InlineData("recursive")]
    [InlineData("too-deep")]
    [InlineData("encrypted-inner")]
    public void Rejects_unresolvable_nested_calls(string kind)
    {
        var ctx = Ctx(b =>
        {
            switch (kind)
            {
                case "recursive": b.Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS BEGIN UPDATE dbo.U SET y = 1; EXEC dbo.p1; END;"); break;
                case "too-deep":
                    for (var i = 1; i <= ProcedureAnalyzer.MaxProcedureDepth; i++)
                        b.Procedure("dbo", "p" + i, $"CREATE PROCEDURE dbo.p{i} AS BEGIN UPDATE dbo.U SET y = {i}; EXEC dbo.p{i + 1}; END;");
                    b.Procedure("dbo", "p" + (ProcedureAnalyzer.MaxProcedureDepth + 1), "CREATE PROCEDURE dbo.p5 AS UPDATE dbo.U SET y = 0;");
                    break;
                case "encrypted-inner":
                    b.Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS EXEC dbo.p2;").Procedure("dbo", "p2", null);
                    break;
            }
        });
        AssertRejected(Run("EXEC dbo.p1;", ctx), AnalysisRules.RejectProcUnresolvedCall);
    }

    [Fact]
    public void Nested_calls_at_depth_limit_are_expanded_and_listed()
    {
        var ctx = Ctx(b =>
        {
            for (var i = 1; i < ProcedureAnalyzer.MaxProcedureDepth; i++)
                b.Procedure("dbo", "p" + i, $"CREATE PROCEDURE dbo.p{i} AS BEGIN UPDATE dbo.U SET y = {i}; EXEC p{i + 1}; END;");
            b.Procedure("dbo", "p" + ProcedureAnalyzer.MaxProcedureDepth, "CREATE PROCEDURE dbo.p4 AS DELETE dbo.T WHERE a = 1;");
        });
        var r = Run("EXEC dbo.p1;", ctx);
        Assert.True(r.IsAccepted, string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        var plan = r.Statements[0].Plan!;
        Assert.Equal(["dbo.p2", "dbo.p3", "dbo.p4"], plan.ExpandedCalls);
        Assert.Equal(["dbo.T", "dbo.U"], plan.WriteTargets);
    }

    // ---- 경고 ----

    [Fact]
    public void Warnings_do_not_block()
    {
        var body = """
            DECLARE @t TABLE (a int);
            DECLARE c CURSOR LOCAL FOR SELECT a FROM dbo.T; OPEN c; CLOSE c; DEALLOCATE c;
            BEGIN TRY
                BEGIN TRAN;
                UPDATE dbo.U SET y = DATEPART(ms, GETDATE());
                COMMIT;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0 ROLLBACK;
            END CATCH;
            SELECT a FROM dbo.T;
            """;
        var r = Run("EXEC dbo.q;", WithProc(body));
        Assert.True(r.IsAccepted, string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        var rules = r.Warnings.Select(w => w.RuleId).ToHashSet();
        foreach (var w in new[]
                 {
                     AnalysisRules.WarnProcNestedTransaction, AnalysisRules.WarnProcRollback, AnalysisRules.WarnProcCatchSwallows, AnalysisRules.WarnProcXactStateCheck,
                     AnalysisRules.WarnProcCursor, AnalysisRules.WarnProcTableVariable, AnalysisRules.WarnProcResultSet, AnalysisRules.WarnProcNondeterministicWrite,
                 })
            Assert.Contains(w, rules);
        Assert.All(r.Warnings, w => Assert.Equal(Severity.Warning, w.Severity));
        Assert.Equal(r.Warnings.Count, r.Warnings.Select(w => w.RuleId).Distinct().Count()); // 규칙당 1개
    }

    [Fact]
    public void Catch_that_rethrows_is_not_swallowing()
    {
        var r = Run("EXEC dbo.q;", WithProc("BEGIN TRY DELETE dbo.T; END TRY BEGIN CATCH THROW; END CATCH;"));
        Assert.True(r.IsAccepted);
        Assert.DoesNotContain(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcCatchSwallows);
    }

    // ---- 도달 폐포 ----

    [Fact]
    public void Reached_trigger_with_external_effect_on_write_target_rejects_exec()
    {
        var ctx = WithProc("UPDATE dbo.U SET y = 1;",
            b => b.Trigger("dbo", "U", "trU", ["INSERT"], "CREATE TRIGGER dbo.trU ON dbo.U AFTER INSERT AS EXEC master.dbo.xp_cmdshell 'dir';"));
        var r = Run("EXEC dbo.q;", ctx);
        AssertRejected(r, AnalysisRules.RejectTriggerExternalEffect); // 연산을 모르므로 세 연산 모두로 넓힌다(INSERT 트리거도 닿음)
        Assert.Contains("EXEC dbo.q → dbo.U", r.Rejects.Single(d => d.RuleId == AnalysisRules.RejectTriggerExternalEffect).Message);
    }

    [Fact]
    public void Reached_through_fk_cascade_commit_trigger_rejects_and_rollback_trigger_does_not()
    {
        BaselineContext C(string trigger) => WithProc("DELETE dbo.T WHERE a = 1;", b => b
            .ForeignKey("FK_C_T", "dbo.C", "dbo.T", onDelete: "CASCADE")
            .Trigger("dbo", "C", "trC", ["DELETE"], $"CREATE TRIGGER dbo.trC ON dbo.C AFTER DELETE AS BEGIN {trigger} END"));
        AssertRejected(Run("EXEC dbo.q;", C("COMMIT TRAN; BEGIN TRAN;")), AnalysisRules.RejectTriggerTransactionControl);
        Assert.True(Run("EXEC dbo.q;", C("IF EXISTS (SELECT 1 FROM deleted) ROLLBACK;")).IsAccepted);
    }

    [Fact]
    public void Existing_dml_analysis_is_unchanged_for_plain_statements()
    {
        var r = Run("DELETE FROM dbo.T WHERE id = 1;", Ctx(b => b.Procedure("dbo", "p", P)));
        var s = Assert.Single(r.Statements);
        Assert.True(r.IsAccepted);
        Assert.Equal((ChangeOperation.Delete, "dbo.T", (string?)null, (ProcedurePlan?)null), (s.Kind, s.Target, s.Procedure, s.Plan));
        Assert.Empty(s.Parameters);
    }

    // ---- 실행 SQL 생성(인용·주입 불가) ----

    [Fact]
    public void Exec_sql_quotes_names_and_string_values_and_parses_to_three_statements()
    {
        var ctx = Ctx(b => b.Procedure("we]ird", "p'x", "CREATE PROCEDURE [we]]ird].[p'x] @s nvarchar(100) AS UPDATE dbo.U SET y = 1;"));
        var r = Run("EXEC [we]]ird].[p'x] @s = N'x''); DROP TABLE dbo.T; --';", ctx);
        Assert.True(r.IsAccepted, string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        var sql = r.Statements[0].Plan!.ExecSql;
        Assert.Equal("DECLARE @dbr_rc int, @dbr_tc0 int = @@TRANCOUNT;\nBEGIN TRAN;\nEXEC @dbr_rc = [we]]ird].[p'x] @s = N'x''); DROP TABLE dbo.T; --';\nDECLARE @dbr_dtc int = @@TRANCOUNT - @dbr_tc0;\nIF @dbr_dtc = 1 COMMIT TRAN;\nSELECT @dbr_rc AS dbr_rc, @dbr_dtc AS dbr_dtc;", sql);
        var parsed = (TSqlScript)new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
        Assert.Equal(6, parsed.Batches.Sum(b => b.Statements.Count));
    }

    [Theory]
    [InlineData("@a = 1; DROP TABLE x")]
    [InlineData("@a b")]
    [InlineData("a")]
    public void Exec_sql_builder_rejects_bad_parameter_names(string name)
    {
        Assert.Throws<InvalidOperationException>(() => ExecSqlBuilder.Build("dbo", "p", [(name, "1")]));
    }

    [Fact]
    public void Exec_sql_builder_verifies_shape_of_values()
    {
        // 리터럴이 아닌 값 조각을 넘기면(호출자 버그) 다시 파싱한 모양이 어긋나 예외
        Assert.Throws<InvalidOperationException>(() => ExecSqlBuilder.Build("dbo", "p", [("@a", "1; DROP TABLE dbo.T")]));
    }

    // ---- 트리거 본문 외부 부작용 보강(할 것 7) ----

    static TriggerStaticInfo AnalyzeTrigger(string body, Action<CatalogBuilder>? more = null)
    {
        var b = new CatalogBuilder().Table("dbo", "T", "a").Table("dbo", "L", "a");
        more?.Invoke(b);
        b.Trigger("dbo", "T", "trX", ["INSERT"], $"CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS BEGIN {body} END");
        return Assert.Single(new TriggerAnalyzer().Analyze(b.Build()), t => t.Trigger == "dbo.trX");
    }

    [Theory]
    [InlineData("INSERT OtherDb.dbo.L (a) SELECT a FROM inserted;", "3-part:OtherDb.dbo.L")]
    [InlineData("INSERT dbo.L (a) SELECT a FROM OtherDb.dbo.Src;", "3-part:OtherDb.dbo.Src")]
    [InlineData("INSERT dbo.L (a) SELECT a FROM OtherDb.dbo.Fn(1);", "3-part:OtherDb.dbo.Fn")]
    [InlineData("EXEC OtherDb.dbo.p;", "3-part:OtherDb.dbo.p")]
    [InlineData("DECLARE @h uniqueidentifier; SEND ON CONVERSATION @h (N'x');", "service-broker:SEND")]
    [InlineData("DECLARE @h uniqueidentifier; BEGIN DIALOG @h FROM SERVICE s TO SERVICE 't';", "service-broker:BEGIN_DIALOG")]
    [InlineData("DECLARE @h uniqueidentifier; END CONVERSATION @h;", "service-broker:END_CONVERSATION")]
    [InlineData("EXEC sp_addmessage 50001, 16, N'x';", "system-proc:sp_addmessage")]
    [InlineData("EXEC sys.sp_getapplock @Resource = 'x', @LockMode = 'Exclusive';", "system-proc:sys.sp_getapplock")]
    [InlineData("EXEC master.dbo.some_proc;", "system-proc:master.dbo.some_proc")]
    public void Trigger_external_effects_are_reinforced(string body, string marker)
    {
        Assert.Contains(marker, AnalyzeTrigger(body).ExternalSideEffects);
    }

    [Theory]
    [InlineData("INSERT dbo.L (a) SELECT a FROM tempdb..#x;")]
    [InlineData("EXEC dbo.sp_custom;")] // 카탈로그 사용자 프로시저로 해석되는 sp_ 이름은 시스템 프로시저가 아니다
    [InlineData("EXEC dbo.uspLog;")]
    public void Trigger_without_external_effects_stays_clean(string body)
    {
        var t = AnalyzeTrigger(body, b => b
            .Procedure("dbo", "sp_custom", "CREATE PROCEDURE dbo.sp_custom AS UPDATE dbo.L SET a = 1;")
            .Procedure("dbo", "uspLog", "CREATE PROCEDURE dbo.uspLog AS INSERT dbo.L (a) VALUES (1);"));
        Assert.Empty(t.ExternalSideEffects);
    }

    [Theory]
    [InlineData("EXEC sp_prepexec @h OUTPUT, NULL, N'DELETE dbo.L';")]
    [InlineData("EXEC sp_executesql N'DELETE dbo.L';")]
    public void Dynamic_entry_points_are_dynamic_sql_in_triggers(string body)
    {
        var t = AnalyzeTrigger(body);
        Assert.True(t.HasDynamicSql);
        Assert.Empty(t.ProcedureCalls);
    }

    [Fact]
    public void Dml_on_table_with_service_broker_trigger_is_rejected()
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b => b.Trigger("dbo", "U", "trU", ["DELETE"],
            "CREATE TRIGGER dbo.trU ON dbo.U AFTER DELETE AS BEGIN DECLARE @h uniqueidentifier; SEND ON CONVERSATION @h (N'x'); END"));
        ctx = ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) };
        AssertRejected(Run("DELETE dbo.U WHERE x = 1;", ctx), AnalysisRules.RejectTriggerExternalEffect);
    }
}
