using DbRipple.Contracts;
using DbRipple.TestSupport;

namespace DbRipple.Integration.Tests;

/// <summary>실제 첫 DELETE 헤더(0행 포함)와 뒤 FK 발동을 구분한다. 각 사례는 자기 전용 원본·샌드박스를 사용한다.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class ExecEntryDeleteIntegrationTests
{
    [SqlTheory]
    [InlineData("all", 2, 0)]
    [InlineData("none", 0, 2)]
    [InlineData("partial", 1, 1)]
    [InlineData("branch", 0, 2)]
    public async Task First_delete_evidence_is_consumed_once_and_never_relabels_later_cascade(
        string variant, int directlyDeleted, int cascadeDeleted)
    {
        var firstDelete = variant switch
        {
            "all" => "DELETE FROM dbo.ChildA WHERE parent_id = @id;",
            "none" => "DELETE FROM dbo.ChildA WHERE parent_id = @id AND 1 = 0;",
            "partial" => "DELETE FROM dbo.ChildA WHERE parent_id = @id AND id = 10;",
            "branch" => "IF @id = -1 DELETE FROM dbo.ChildA WHERE parent_id = @id;",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var definition = $$"""
            CREATE PROCEDURE dbo.usp_DeleteParent @id int AS
            BEGIN
                SET NOCOUNT ON;
                {{firstDelete}}
                DELETE FROM dbo.Parent WHERE id = @id;
                RETURN 0;
            END;
            """;
        await using var lab = await Lab.CreateCustomAsync("entry" + variant,
            """
            CREATE TABLE dbo.Parent (id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.ChildA (id int NOT NULL PRIMARY KEY, parent_id int NOT NULL,
                CONSTRAINT FK_ChildA_Parent FOREIGN KEY (parent_id) REFERENCES dbo.Parent(id) ON DELETE CASCADE);
            CREATE TABLE dbo.ChildB (id int NOT NULL PRIMARY KEY, parent_id int NOT NULL,
                CONSTRAINT FK_ChildB_Parent FOREIGN KEY (parent_id) REFERENCES dbo.Parent(id) ON DELETE CASCADE);
            INSERT dbo.Parent VALUES (1), (2);
            INSERT dbo.ChildA VALUES (10, 1), (11, 1), (20, 2);
            INSERT dbo.ChildB VALUES (100, 1), (200, 2);
            """, definition);

        var outcome = await lab.Pipeline.RunAsync("EXEC dbo.usp_DeleteParent @id = 1;", new PreviewOptions(), null, default);
        Assert.True(outcome.Analysis.IsAccepted, string.Join("; ", outcome.Analysis.Rejects.Select(r => r.RuleId)));
        var record = Assert.IsType<PreviewRecord>(outcome.Record);
        Assert.Equal(PreviewStatus.Ok, record.Result.Status);
        Assert.Equal(0, Assert.Single(record.Result.Statements).ReturnValue);
        Assert.Equal(4, record.NetChanges.Count);
        Assert.Empty(record.Result.Residuals);
        var childFirings = record.Result.Firings.Where(f => f.Table == "dbo.ChildA").OrderBy(f => f.CaptureSeq).ToArray();

        if (variant != "branch")
        {
            var first = childFirings[0];
            Assert.Equal(directlyDeleted, first.RowCount);
            Assert.Equal((CauseKind.ProcDirect, Confidence.Certain), (first.Cause!.Kind, first.Cause.Confidence));
            var directEdge = Assert.Single(record.Result.Graph.Edges.Where(e => e.Target == first.Id));
            Assert.Equal(EdgeKind.ProcDirect, directEdge.Kind);
            Assert.IsType<ProcedureCallNode>(Assert.Single(record.Result.Graph.Nodes.Where(n => n.Id == directEdge.Source)));
            Assert.All(record.RowChanges.Where(r => r.FiringId == first.Id), r => Assert.Empty(r.ParentRowIds));
        }

        if (cascadeDeleted > 0)
        {
            var cascade = Assert.Single(childFirings.Where(f => f.RowCount == cascadeDeleted &&
                (variant == "branch" || f.Id != childFirings[0].Id)));
            Assert.Equal(CauseKind.FkCascade, cascade.Cause!.Kind);
            Assert.NotEqual(CauseKind.ProcDirect, cascade.Cause.Kind);
            Assert.Contains(record.Result.Graph.Edges, e => e.Target == cascade.Id && e.Kind == EdgeKind.Fk);
            Assert.All(record.RowChanges.Where(r => r.FiringId == cascade.Id), r => Assert.NotEmpty(r.ParentRowIds));
        }
        var childB = Assert.Single(record.Result.Firings.Where(f => f.Table == "dbo.ChildB" && f.RowCount > 0));
        Assert.Equal((CauseKind.FkCascade, Confidence.Certain), (childB.Cause!.Kind, childB.Cause.Confidence));

        // 원본·샌드박스의 프로시저를 재작성하지 않으며 모든 사용자 데이터가 롤백된다.
        var context = await lab.Manager.GetContextAsync(default);
        foreach (var database in new[] { lab.Origin, context.SandboxDb })
        {
            await using var connection = await TestDb.OpenAsync(database);
            Assert.Equal(definition, await TestDb.ScalarAsync<string>(connection, "SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.usp_DeleteParent'));"));
            Assert.Equal(2, await TestDb.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.Parent;"));
            Assert.Equal(3, await TestDb.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.ChildA;"));
            Assert.Equal(2, await TestDb.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.ChildB;"));
        }
        Assert.Equal(0, await lab.SandboxDiffCountAsync());
        var status = await lab.Manager.GetStatusAsync(default);
        Assert.Equal((SandboxState.Ready, false), (status.State, status.Locked));
    }
}
