using DbRipple.Contracts;

namespace DbRipple.Analysis.Tests;

/// <summary>EXEC [적용]용 분석 재료(2026-11-17): 프로시저 본문 비결정 쓰기를 실행 계획에 싣기, 반환 값 비결정 판정과 경고.</summary>
public sealed class ExecApplyAnalysisTests
{
    static BaselineContext Ctx(params (string Name, string Definition)[] procedures)
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b =>
        {
            b.Table("dbo", "C", "id", "t_id");
            foreach (var (name, definition) in procedures) b.Procedure("dbo", name, definition);
        });
        return ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) };
    }

    static AnalyzedStatement Accepted(string sql, BaselineContext ctx, out AnalysisResult result)
    {
        result = new BatchAnalyzer().Analyze(sql, ctx);
        Assert.True(result.IsAccepted, string.Join(" | ", result.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        return Assert.Single(result.Statements);
    }

    [Fact]
    public void Plan_carries_nondeterministic_writes_of_expanded_body()
    {
        var ctx = Ctx(
            ("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; UPDATE dbo.U SET y = DATEPART(ms, SYSDATETIME()) WHERE x = 1; EXEC dbo.inner1; RETURN 0; END;"),
            ("inner1", "CREATE PROCEDURE dbo.inner1 AS INSERT INTO dbo.C (id, t_id) VALUES (CHECKSUM(NEWID()), 1);"));

        var st = Accepted("EXEC dbo.q;", ctx, out var r);

        Assert.Equal(["dbo.C.id", "dbo.U.y"], st.Plan!.NondeterministicWrites.Select(w => w.Table + "." + w.Column).Order(StringComparer.Ordinal));
        Assert.False(st.Plan.ReturnValueNondeterministic);   // RETURN 0(리터럴)
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicWrite);
        Assert.DoesNotContain(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.U (x, y) VALUES (1, 2); DECLARE @id int = SCOPE_IDENTITY(); RETURN @id;", true)]
    [InlineData("INSERT INTO dbo.U (x, y) VALUES (1, 2); RETURN @@IDENTITY;", true)]
    [InlineData("DECLARE @n int = DATEPART(second, GETDATE()); UPDATE dbo.U SET y = 1 WHERE x = @n; RETURN @n;", true)]
    [InlineData("UPDATE dbo.U SET y = 1; RETURN @@ROWCOUNT;", false)]                       // 비결정 함수 없음
    [InlineData("UPDATE dbo.U SET y = DATEPART(ms, GETDATE()); RETURN (0);", false)]       // 반환은 리터럴
    [InlineData("UPDATE dbo.U SET y = DATEPART(ms, GETDATE()); RETURN -1;", false)]
    [InlineData("UPDATE dbo.U SET y = DATEPART(ms, GETDATE()); RETURN;", false)]
    public void Return_value_nondeterminism_is_judged_conservatively(string body, bool expected)
    {
        var st = Accepted("EXEC dbo.q;", Ctx(("q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} END;")), out var r);

        Assert.Equal(expected, st.Plan!.ReturnValueNondeterministic);
        Assert.Equal(expected, r.Warnings.Any(w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn));
    }

    [Fact]
    public void Identity_function_in_inner_procedure_makes_non_literal_return_nondeterministic()
    {
        var ctx = Ctx(
            ("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; DECLARE @rc int; EXEC @rc = dbo.inner1; RETURN @rc; END;"),
            ("inner1", "CREATE PROCEDURE dbo.inner1 AS BEGIN INSERT INTO dbo.U (x, y) VALUES (1, 2); RETURN CAST(SCOPE_IDENTITY() AS int); END;"));

        var st = Accepted("EXEC dbo.q;", ctx, out var r);

        Assert.True(st.Plan!.ReturnValueNondeterministic);
        var w = Assert.Single(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn);
        Assert.Equal(Severity.Warning, w.Severity);
        Assert.Contains("NULL 여부만", w.Message);
    }
}
