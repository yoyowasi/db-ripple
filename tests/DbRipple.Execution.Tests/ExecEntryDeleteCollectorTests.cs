using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Execution.Tests;

public partial class CollectorTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("emptyFirst", true)]
    [InlineData("otherTableFirst", false)]
    [InlineData("wrongDepth", false)]
    [InlineData("wrongOperation", false)]
    [InlineData("wrongName", false)]
    [InlineData("missingFirst", false)]
    [InlineData("missingLast", false)]
    [InlineData("missingImage", false)]
    [InlineData("failedStatement", false)]
    [InlineData("failedPreview", false)]
    [InlineData("dirty", false)]
    [InlineData("truncated", false)]
    [InlineData("oldManifest", false)]
    [InlineData("wrongVersion", false)]
    [InlineData("uncovered", false)]
    [InlineData("oldPlan", false)]
    public void ExecEntryDelete_requires_the_first_raw_header_and_complete_capture(string mode, bool proven)
    {
        var ctx = Ctx();
        var module = new CatalogModule(7, "dbo", "q", "CREATE PROC dbo.q AS DELETE FROM dbo.T;");
        ctx = ctx with { Catalog = ctx.Catalog with { Procedures = [module] } };
        if (mode == "oldManifest")
        {
            var legacy = new { ctx.Manifest.Version, ctx.Manifest.Tables, ctx.Manifest.ExcludedTables, ctx.Manifest.Notes };
            ctx = ctx with { Manifest = JsonSerializer.Deserialize<CaptureManifest>(JsonSerializer.Serialize(legacy))! };
        }
        if (mode == "uncovered")
            ctx = ctx with { Manifest = ctx.Manifest with { Tables = ctx.Manifest.Tables.Select(t => t with { Covered = false }).ToList() } };

        var raw = new RawBuilder()
            .Firing(101, mode == "otherTableFirst" ? 4 : 1, mode == "wrongOperation" ? ChangeOperation.Update : ChangeOperation.Delete,
                mode == "wrongDepth" ? 2 : 1, deleted: mode == "emptyFirst" ? [] : [T1])
            .Firing(103, 1, ChangeOperation.Delete, 1, deleted: [T1])
            .EndStatement(103).Build();
        var first = new IndependentDelete(module.ObjectId, 1, "dbo.T", ChangeOperation.Delete);
        var plan = new ProcedurePlan { Procedure = "dbo.q", ExecSql = "x", WriteTargets = ["dbo.T"], FirstIndependentDelete = mode == "oldPlan" ? null : first };
        var statement = raw.Executions[0].Result with
        {
            Kind = ChangeOperation.Exec, Procedure = "dbo.q", Plan = plan, Target = null, CaptureSeqRange = [101, 103],
            Status = mode == "failedStatement" ? StatementStatus.Failed : StatementStatus.Ok,
        };
        var analysis = Analysis(new AnalyzedStatement
        {
            Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Exec, Target = null,
            Procedure = "dbo.q", Sql = "EXEC dbo.q;", Plan = plan, Span = statement.Span,
        });
        raw = raw with
        {
            Executions = [raw.Executions[0] with { Result = statement }],
            Headers = mode switch
            {
                "missingFirst" => [raw.Headers[1]], "missingLast" => [raw.Headers[0]],
                "wrongName" => [raw.Headers[0] with { TableName = "[dbo].[A]" }, raw.Headers[1]],
                _ => raw.Headers,
            },
            Images = mode == "missingImage" ? [raw.Images[1]] : raw.Images,
            Status = mode == "failedPreview" ? PreviewStatus.Failed : PreviewStatus.Ok,
            SandboxDirty = mode == "dirty",
            Capture = raw.Capture with { Truncated = mode == "truncated", ImageCount = raw.Images.Count,
                ManifestVersion = mode == "wrongVersion" ? "old" : ctx.Manifest.Version },
        };
        var result = Collect(raw, ctx, analysis);
        if (proven)
            Assert.Equal(new ProcedureEntryDelete(1, "f-101", 1, ChangeOperation.Delete), Assert.Single(result.ProcedureEntryDeletes));
        else
            Assert.Empty(result.ProcedureEntryDeletes);
    }
}
