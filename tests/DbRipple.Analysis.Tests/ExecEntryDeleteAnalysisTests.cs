using DbRipple.Contracts;

namespace DbRipple.Analysis.Tests;

public sealed class ExecEntryDeleteAnalysisTests
{
    const string Deletes = "DELETE FROM dbo.T WHERE id = @id AND 1 = 1; DELETE FROM dbo.U WHERE x = @id;";

    static ProcedurePlan Plan(string body, Action<CatalogBuilder>? configure = null)
    {
        var baseline = BatchAnalyzerTests.Ctx(more: b =>
        {
            b.Procedure("dbo", "q", $"CREATE PROCEDURE dbo.q @id int AS BEGIN {body} END;");
            configure?.Invoke(b);
        });
        return Assert.Single(new BatchAnalyzer().Analyze("EXEC dbo.q @id = 1;", baseline).Statements).Plan!;
    }

    [Fact]
    public void Qualified_leaf_with_only_the_generated_audit_trigger_has_an_entry_plan()
    {
        var plan = Plan("SET NOCOUNT ON; " + Deletes + " RETURN 0;", b =>
        {
            b.ForeignKey("FK", "dbo.T", "dbo.U", "CASCADE");
            b.Trigger("dbo", "T", "dbr_audit_T_D", ["DELETE"], null, isLast: true);
        });
        Assert.Equal("dbo.T", plan.FirstIndependentDelete?.Target);
        Assert.Equal(ChangeOperation.Delete, plan.FirstIndependentDelete!.Operation);
    }

    [Theory]
    [InlineData("IF @id = 1 DELETE FROM dbo.T;")]
    [InlineData("EXEC dbo.innerProc;")]
    [InlineData("BEGIN TRAN; COMMIT;")]
    [InlineData("RETURN 0; DELETE FROM dbo.T;")]
    [InlineData("DELETE FROM dbo.T OUTPUT deleted.id;")]
    [InlineData("DELETE FROM T;")]
    [InlineData("DELETE FROM dbo.T WHERE EXISTS(SELECT 1);")]
    [InlineData("SET NOEXEC ON;")]
    public void Unsupported_statement_anywhere_disables_entry_plan(string suffix) =>
        Assert.Null(Plan(Deletes + suffix, b => b.Procedure("dbo", "innerProc", "CREATE PROC dbo.innerProc AS RETURN 0;")).FirstIndependentDelete);

    [Theory]
    [InlineData("self")]
    [InlineData("cascade")]
    [InlineData("setNull")]
    [InlineData("userTrigger")]
    [InlineData("insteadOf")]
    public void Active_first_target_side_effect_disables_entry_plan(string obstacle)
    {
        var plan = Plan(Deletes, b =>
        {
            switch (obstacle)
            {
                case "self": b.ForeignKey("FK", "dbo.T", "dbo.T"); break;
                case "cascade": b.ForeignKey("FK", "dbo.U", "dbo.T", "CASCADE"); break;
                case "setNull": b.ForeignKey("FK", "dbo.U", "dbo.T", "SET_NULL"); break;
                case "userTrigger": b.Trigger("dbo", "T", "dbr_audit_user", ["DELETE"], null); break;
                case "insteadOf": b.Trigger("dbo", "T", "dbr_audit_T_D", ["DELETE"], null, insteadOf: true); break;
            }
        });
        Assert.Null(plan.FirstIndependentDelete);
    }
}
