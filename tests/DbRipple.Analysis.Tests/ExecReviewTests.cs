using DbRipple.Contracts;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis.Tests;

/// <summary>
/// EXEC 독립 보안 검토(2026-11-13) 지적 재현: COMMIT 구조 검사, 스키마 없는 sp_ 호출, 본문 OUTPUT, 프로시저 색인 키, 사용자 정의 함수, 펼치기 비용, 실행 SQL 감싸기.
/// </summary>
public class ExecReviewTests
{
    static BaselineContext Ctx(Action<CatalogBuilder>? more = null)
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b => { b.Table("dbo", "C", "id", "t_id"); more?.Invoke(b); });
        return ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) };
    }

    static BaselineContext WithProc(string body, Action<CatalogBuilder>? more = null) =>
        Ctx(b => { b.Procedure("dbo", "q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} END;"); more?.Invoke(b); });

    static AnalysisResult Run(string sql, BaselineContext ctx) => new BatchAnalyzer().Analyze(sql, ctx);

    static string Show(AnalysisResult r) => string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message));

    // ---- 1(a) COMMIT은 그 지점에서 확실히 열린 BEGIN TRAN이 있어야 한다 ----

    [Theory]
    [InlineData("IF 1 = 0 BEGIN TRAN; UPDATE dbo.U SET y = 1; COMMIT;")]
    [InlineData("IF 1 = 0 BEGIN TRAN; ELSE PRINT 'x'; COMMIT;")]
    [InlineData("BEGIN TRAN; WHILE 1 = 0 BEGIN BEGIN TRAN; END; COMMIT; COMMIT;")]
    [InlineData("BEGIN TRAN; WHILE 1 = 0 BEGIN COMMIT; END;")]
    [InlineData("BEGIN TRAN; WHILE 1 = 1 BEGIN COMMIT; IF 1 = 1 BREAK; BEGIN TRAN; END; COMMIT;")]
    [InlineData("GOTO x; BEGIN TRAN; x: UPDATE dbo.U SET y = 1; COMMIT;")]
    [InlineData("BEGIN TRAN; x: UPDATE dbo.U SET y = 1; COMMIT; IF 1 = 0 GOTO x;")]
    [InlineData("BEGIN TRY RAISERROR('x', 16, 1); BEGIN TRAN; END TRY BEGIN CATCH COMMIT; END CATCH;")]
    [InlineData("BEGIN TRAN; BEGIN TRY COMMIT; RAISERROR('x', 16, 1); END TRY BEGIN CATCH COMMIT; END CATCH;")]
    [InlineData("BEGIN TRAN; IF 1 = 0 COMMIT; COMMIT;")]
    [InlineData("BEGIN TRAN; ROLLBACK; COMMIT;")]
    [InlineData("COMMIT; UPDATE dbo.U SET y = 1; BEGIN TRAN;")]
    [InlineData("BEGIN TRAN; COMMIT WORK; COMMIT WORK;")]
    [InlineData("BEGIN TRY BEGIN TRAN; UPDATE dbo.U SET y = 1; END TRY BEGIN CATCH IF XACT_STATE() = 1 COMMIT; END CATCH;")] // 닫힌 쪽(SP16 뒤 재검토)
    public void Commit_without_definitely_open_begin_is_rejected(string body)
    {
        var r = Run("EXEC dbo.q;", WithProc(body));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnbalancedCommit);
    }

    [Theory]
    [InlineData("BEGIN TRY BEGIN TRAN; UPDATE dbo.U SET y = 1; COMMIT; END TRY BEGIN CATCH IF XACT_STATE() <> 0 ROLLBACK; THROW; END CATCH;")]
    [InlineData("BEGIN TRY BEGIN TRAN; UPDATE dbo.U SET y = 1; COMMIT; END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK; END CATCH;")]
    [InlineData("BEGIN TRAN; UPDATE dbo.U SET y = 1; IF 1 = 0 BEGIN ROLLBACK; RETURN; END; COMMIT;")]
    [InlineData("BEGIN TRAN; IF 1 = 0 BEGIN UPDATE dbo.U SET y = 1; END ELSE BEGIN UPDATE dbo.U SET y = 2; END; COMMIT;")]
    [InlineData("BEGIN TRAN; WHILE 1 = 0 BEGIN BEGIN TRAN; UPDATE dbo.U SET y = 1; COMMIT; END; COMMIT;")]
    [InlineData("IF 1 = 0 BEGIN BEGIN TRAN; UPDATE dbo.U SET y = 1; COMMIT; END;")]
    [InlineData("BEGIN TRAN; BEGIN TRAN; COMMIT; COMMIT;")]
    [InlineData("BEGIN TRAN; BEGIN TRY UPDATE dbo.U SET y = 1; END TRY BEGIN CATCH THROW; END CATCH; COMMIT;")]
    [InlineData("x: UPDATE dbo.U SET y = 1; IF 1 = 0 GOTO x;")] // GOTO만 있고 COMMIT이 없으면 허용
    public void Structurally_balanced_commit_is_accepted(string body)
    {
        var r = Run("EXEC dbo.q;", WithProc(body));
        Assert.True(r.IsAccepted, Show(r));
    }

    // ---- 2) 스키마 없는 sp_ 호출은 카탈로그에 사용자 프로시저가 있어도 시스템 프로시저로 본다 ----

    [Fact]
    public void Unqualified_sp_call_is_system_even_if_user_procedure_shadows_it()
    {
        var ctx = WithProc("EXEC sp_rename 'dbo.U', 'U2';",
            b => b.Procedure("dbo", "sp_rename", "CREATE PROCEDURE dbo.sp_rename @a nvarchar(100), @b nvarchar(100) AS UPDATE dbo.U SET y = 1;"));
        var r = Run("EXEC dbo.q;", ctx);
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcExternalEffect && d.Message.Contains("system-proc:sp_rename"));
        Assert.DoesNotContain("dbo.sp_rename", r.Statements[0].Plan!.ExpandedCalls);
    }

    [Fact]
    public void Schema_qualified_user_sp_procedure_is_expanded()
    {
        var ctx = WithProc("EXEC dbo.sp_mine;", b => b.Procedure("dbo", "sp_mine", "CREATE PROCEDURE dbo.sp_mine AS UPDATE dbo.U SET y = 1;"));
        var r = Run("EXEC dbo.q;", ctx);
        Assert.True(r.IsAccepted, Show(r));
        Assert.Equal(["dbo.sp_mine"], r.Statements[0].Plan!.ExpandedCalls);
    }

    [Fact]
    public void Unqualified_sp_call_in_trigger_is_external()
    {
        var b = new CatalogBuilder().Table("dbo", "T", "a").Table("dbo", "L", "a")
            .Procedure("dbo", "sp_custom", "CREATE PROCEDURE dbo.sp_custom AS UPDATE dbo.L SET a = 1;");
        b.Trigger("dbo", "T", "trX", ["INSERT"], "CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS EXEC sp_custom;");
        var t = Assert.Single(new TriggerAnalyzer().Analyze(b.Build()));
        Assert.Contains("system-proc:sp_custom", t.ExternalSideEffects);
        Assert.Equal(["sp_custom"], t.UnresolvedProcedureCalls);
    }

    // ---- 3) 본문 OUTPUT: 실제 테이블 OUTPUT INTO와 클라이언트로 가는 OUTPUT은 거부, @테이블 변수·#임시 테이블은 허용 ----

    [Theory]
    [InlineData("UPDATE dbo.U SET y = 1 OUTPUT inserted.x INTO dbo.T (a);", true)]
    [InlineData("DELETE dbo.U OUTPUT deleted.x;", true)]
    [InlineData("DECLARE @t TABLE (a int); UPDATE dbo.U SET y = 1 OUTPUT inserted.x INTO @t (a);", false)]
    [InlineData("CREATE TABLE #t (a int); UPDATE dbo.U SET y = 1 OUTPUT inserted.x INTO #t (a);", false)]
    public void Output_clause_in_body(string body, bool rejected)
    {
        var r = Run("EXEC dbo.q;", WithProc(body));
        if (rejected)
        {
            var d = Assert.Single(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcStatementNotAllowed);
            Assert.Contains("OUTPUT", d.Message);
        }
        else Assert.True(r.IsAccepted, Show(r));
    }

    // ---- 4) 프로시저 색인: (스키마, 이름) 튜플, 대소문자만 다른 중복은 모호 → 거부 ----

    [Fact]
    public void Dotted_names_do_not_collide()
    {
        // 스키마 "a.b" + 이름 "c" 와 스키마 "a" + 이름 "b.c"는 문자열로 이으면 같다
        var ctx = Ctx(b => b
            .Procedure("a.b", "c", "CREATE PROCEDURE [a.b].c AS UPDATE dbo.U SET y = 1;")
            .Procedure("a", "b.c", "CREATE PROCEDURE a.[b.c] AS DELETE dbo.T WHERE a = 1;"));
        var r = Run("EXEC a.[b.c];", ctx);
        Assert.True(r.IsAccepted, Show(r));
        Assert.Equal(["dbo.T"], r.Statements[0].Plan!.WriteTargets);
    }

    [Fact]
    public void Case_only_duplicates_are_ambiguous()
    {
        var ctx = WithProc("EXEC dbo.inner1;", b => b
            .Procedure("dbo", "inner1", "CREATE PROCEDURE dbo.inner1 AS UPDATE dbo.U SET y = 1;")
            .Procedure("dbo", "INNER1", "CREATE PROCEDURE dbo.INNER1 AS DELETE dbo.T;"));
        Assert.Contains(Run("EXEC dbo.q;", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall);
        Assert.Contains(Run("EXEC dbo.inner1;", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectProcNotFound);
    }

    // ---- 5) 사용자 정의 함수는 본문을 펼치지 않으므로 EXEC 본문에서 거부 ----

    [Theory]
    [InlineData("UPDATE dbo.U SET y = dbo.f(1);", true)]
    [InlineData("SELECT dbo.f(1);", true)]
    [InlineData("DELETE dbo.T WHERE a IN (SELECT a FROM dbo.tvf(1));", true)]
    [InlineData("UPDATE dbo.U SET y = ABS(-1) + LEN(N'x');", false)]
    [InlineData("SELECT name FROM sys.fn_listextendedproperty(NULL, NULL, NULL, NULL, NULL, NULL, NULL);", false)]
    public void User_defined_functions_in_body(string body, bool rejected)
    {
        var r = Run("EXEC dbo.q;", WithProc(body));
        if (rejected) Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("함수"));
        else Assert.True(r.IsAccepted, Show(r));
    }

    // ---- 6) 펼치기 비용: 같은 모듈은 한 번만 펼친다 ----

    [Fact]
    public void Fan_out_expansion_is_memoized()
    {
        static string Calls(string callee, int n) => string.Concat(Enumerable.Repeat($"EXEC dbo.{callee}; ", n));
        var ctx = BatchAnalyzerTests.Ctx(more: b => b
            .Procedure("dbo", "d", "CREATE PROCEDURE dbo.d AS UPDATE dbo.U SET y = 1;")
            .Procedure("dbo", "c", $"CREATE PROCEDURE dbo.c AS BEGIN {Calls("d", 30)} END;")
            .Procedure("dbo", "b", $"CREATE PROCEDURE dbo.b AS BEGIN {Calls("c", 30)} END;")
            .Procedure("dbo", "a", $"CREATE PROCEDURE dbo.a AS BEGIN {Calls("b", 30)} END;"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Run("EXEC dbo.a;", ctx);
        sw.Stop();
        Assert.True(r.IsAccepted, Show(r));
        Assert.Equal(["dbo.b", "dbo.c", "dbo.d"], r.Statements[0].Plan!.ExpandedCalls);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms"); // 검토 측정: 메모 없이 30회 × 3단계에 2.3초
    }

    [Fact]
    public void Memoized_expansion_keeps_recursion_and_depth_results()
    {
        // p1 → p2 → p1(재귀)와 p3 → p2: p2를 메모해도 p3 경로의 결과(재귀 미상)는 그대로다
        var ctx = Ctx(b => b
            .Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS BEGIN UPDATE dbo.U SET y = 1; EXEC dbo.p2; END;")
            .Procedure("dbo", "p2", "CREATE PROCEDURE dbo.p2 AS BEGIN DELETE dbo.T WHERE a = 1; EXEC dbo.p1; END;")
            .Procedure("dbo", "p3", "CREATE PROCEDURE dbo.p3 AS BEGIN EXEC dbo.p2; EXEC dbo.p1; END;"));
        Assert.Contains(Run("EXEC dbo.p3;", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall);
        Assert.Contains(Run("EXEC dbo.p1;", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall);
    }

    // ---- 1(b) 실행 SQL은 BEGIN TRAN 한 겹을 더 씌우고 트랜잭션 수 변화를 돌려준다 ----

    [Fact]
    public void Exec_sql_wraps_call_in_one_more_transaction_level()
    {
        var sql = ExecSqlBuilder.Build("dbo", "p", [("@a", "1")]);
        Assert.Equal(
            "DECLARE @dbr_rc int, @dbr_tc0 int = @@TRANCOUNT;\nBEGIN TRAN;\nEXEC @dbr_rc = [dbo].[p] @a = 1;\n" +
            "DECLARE @dbr_dtc int = @@TRANCOUNT - @dbr_tc0;\nIF @dbr_dtc = 1 COMMIT TRAN;\nSELECT @dbr_rc AS dbr_rc, @dbr_dtc AS dbr_dtc;", sql);
        var parsed = (TSqlScript)new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
        Assert.Equal(6, parsed.Batches.Sum(b => b.Statements.Count));
    }
}
