using System.Text.Json;
using DbRipple.Contracts;

namespace DbRipple.Attribution.Tests;

public class ExecEntryDeleteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_proven_first_firing_precedes_fk_attribution(bool firstIsEmpty)
    {
        var s = new Scenario();
        s.Cat.Table("t.P", ["id"], "id").Table("t.C", ["id"], "id", "p")
            .Fk("FK_C_P", "t.C", "p", "t.P", "id", "CASCADE");
        s.Exec(1, "t.Remove", ["t.C", "t.P"]);
        s.Firing(20, 1, 1, "t.C", ChangeOperation.Delete, firstIsEmpty ? [] : [("id=11,p=1", null)])
            .Firing(21, 1, 1, "t.C", ChangeOperation.Delete, ("id=12,p=1", null))
            .Firing(22, 1, 1, "t.P", ChangeOperation.Delete, ("id=1", null));
        var input = s.Input();
        var targetId = input.Baseline.Catalog.Tables.Single(t => t.Name == "C").ObjectId;
        var plan = JsonSerializer.Deserialize<ProcedurePlan>($$$"""
            {"Procedure":"t.Remove","ExecSql":"x","WriteTargets":["t.C","t.P"],
             "FirstIndependentDelete":{"ModuleObjectId":7,"TargetObjectId":{{{targetId}}},"Target":"t.C","Operation":"DELETE"}}
            """)!;
        // Collector evidence stays in memory and is deliberately excluded from JSON.
        var evidence = new CollectResult
        {
            ProcedureEntryDeletes = [new ProcedureEntryDelete(1, "f-20", targetId, ChangeOperation.Delete)],
        };
        var result = new CausalResolver().Resolve(input with
        {
            Statements = [input.Statements[0] with { Plan = plan, CaptureSeqRange = [20, 22] }],
            Collected = evidence with { Firings = input.Collected.Firings, RowChanges = input.Collected.RowChanges },
        });
        var first = result.Model.Firings[0].Cause!;
        Assert.Equal((CauseKind.ProcDirect, Confidence.Certain), (first.Kind, first.Confidence));
        Assert.Contains("\"procedureEntryDelete\"", JsonSerializer.Serialize(first.Evidence));
        Assert.All(result.Model.RowChanges.Where(r => r.FiringId == "f-20"), r => Assert.Empty(r.ParentRowIds));
        Assert.Equal(CauseKind.FkCascade, result.Model.Firings[1].Cause!.Kind);
        Assert.Equal(["rc-22-1"], result.Model.RowChanges.Single(r => r.FiringId == "f-21").ParentRowIds);
    }
}

