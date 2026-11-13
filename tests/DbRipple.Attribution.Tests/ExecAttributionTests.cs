using System.Text.Json;
using DbRipple.Contracts;

namespace DbRipple.Attribution.Tests;

using Op = ChangeOperation;

/// <summary>
/// EXEC 미리보기(2026-11-13) 원인 귀속·그래프: 문장 창 안 깊이 1, FK로 설명되지 않음, 정적 쓰기 대상 안 → PROC_DIRECT(certain).
/// 프로시저가 자식 테이블에 쓸 수 있으면 그 FK 판정은 certain이 아니다(inferred). 그래프는 statement 대신 procedureCall 노드와 PROC_DIRECT 간선.
/// </summary>
public class ExecAttributionTests
{
    static Firing F(Resolution r, long seq) => r.Model.Firings.Single(f => f.CaptureSeq == seq);

    /// <summary>t.P ← t.C ON DELETE CASCADE, t.A, t.L.</summary>
    static Scenario Base()
    {
        var s = new Scenario();
        s.Cat.Table("t.P", ["id"], "id").Table("t.C", ["id"], "id", "p_id").Table("t.A", ["id"], "id", "v").Table("t.L", ["id"], "id")
             .Fk("FK_C_P", "t.C", "p_id", "t.P", "id", "CASCADE");
        return s;
    }

    [Fact]
    public void Depth_one_writes_inside_exec_window_are_proc_direct_certain()
    {
        var s = Base();
        s.Exec(1, "dbo.usp", ["t.A", "t.L"])
         .Firing(10, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"))
         .Firing(11, 1, 1, "t.L", Op.Insert, (null, "id=5"));
        var r = s.Resolve();
        foreach (var seq in new long[] { 10, 11 })
        {
            var c = F(r, seq).Cause!;
            Assert.Equal((CauseKind.ProcDirect, Confidence.Certain, (string?)null), (c.Kind, c.Confidence, c.ParentFiringId));
            Assert.Equal(OrderGuarantee.Guaranteed, F(r, seq).OrderGuarantee);
        }

        var node = Assert.IsType<ProcedureCallNode>(r.Graph.Nodes[0]);
        Assert.Equal(("st-1", 1, "EXEC dbo.usp", "dbo.usp", StatementStatus.Ok, (int?)0, (int?)0),
            (node.Id, node.StatementIndex, node.Label, node.Procedure, node.Status, node.ReturnValue, node.ResultSetCount));
        Assert.Equal(["t.A", "t.L"], node.WriteTargets);
        Assert.Equal(["dbo.inner"], node.ExpandedCalls);
        Assert.Empty(node.UnobservedWriteTargets);
        Assert.DoesNotContain(r.Graph.Nodes, n => n is StatementNode);
        Assert.DoesNotContain(r.Graph.Edges, e => e.Kind == EdgeKind.Direct);
        var edges = r.Graph.Edges.Where(e => e.Kind == EdgeKind.ProcDirect).ToList();
        Assert.Equal(["f-10", "f-11"], edges.Select(e => e.Target).Order());
        Assert.All(edges, e => Assert.Equal(("st-1", Confidence.Certain), (e.Source, e.Confidence)));
    }

    [Fact]
    public void Static_write_targets_without_firings_are_unobserved()
    {
        var s = Base();
        s.Exec(1, "dbo.usp", ["t.A", "t.L"]).Firing(10, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"));
        var node = Assert.IsType<ProcedureCallNode>(s.Resolve().Graph.Nodes[0]);
        Assert.Equal(["t.L"], node.UnobservedWriteTargets);
    }

    [Theory]
    [InlineData(false, Confidence.Certain)]  // 프로시저는 t.P만 쓴다 → 자식 t.C는 FK 연쇄로 확실
    [InlineData(true, Confidence.Inferred)]  // 프로시저가 t.C에도 쓸 수 있다 → 본문의 직접 DELETE와 구별 못 함
    public void Fk_cascade_inside_exec_is_downgraded_when_procedure_can_write_the_child(bool writesChild, Confidence expected)
    {
        var s = Base();
        s.Exec(1, "dbo.usp", writesChild ? ["t.C", "t.P"] : ["t.P"])
         .Firing(10, 1, 1, "t.C", Op.Delete, ("id=1,p_id=7", null))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        var r = s.Resolve();
        Assert.Equal((CauseKind.FkCascade, expected), (F(r, 10).Cause!.Kind, F(r, 10).Cause!.Confidence));
        Assert.Equal((CauseKind.ProcDirect, Confidence.Certain), (F(r, 11).Cause!.Kind, F(r, 11).Cause!.Confidence));
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Fk && e.Source == "f-11" && e.Target == "f-10" && e.Confidence == expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Depth_one_firing_outside_static_targets_or_without_plan_is_unexplained(bool plan)
    {
        var s = Base();
        s.Exec(1, "dbo.usp", ["t.L"], plan: plan).Firing(10, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"));
        var c = F(s.Resolve(), 10).Cause!;
        Assert.Equal((CauseKind.Unexplained, Confidence.Unexplained), (c.Kind, c.Confidence));
    }

    [Fact]
    public void Triggers_below_procedure_writes_are_attributed_as_before()
    {
        var s = Base();
        s.Cat.Trigger("t.trA", "t.A", [Op.Update], ["t.L"]);
        s.Exec(1, "dbo.usp", ["t.A"])
         .Firing(10, 1, 2, "t.L", Op.Insert, (null, "id=5"))
         .Firing(11, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"))
         .Link(10, Confidence.Inferred, 11);
        var r = s.Resolve();
        Assert.Equal(CauseKind.ProcDirect, F(r, 11).Cause!.Kind);
        Assert.Equal((CauseKind.Trigger, Confidence.Inferred, "f-11"), (F(r, 10).Cause!.Kind, F(r, 10).Cause!.Confidence, F(r, 10).Cause!.ParentFiringId));
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Fires && e.Source == "f-11");
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Writes && e.Target == "f-10");
    }

    [Fact]
    public void Failed_exec_has_procedure_call_node_and_blocked_error()
    {
        var s = Base();
        s.Exec(1, "dbo.usp", ["t.A"], StatementStatus.Failed);
        var r = s.Resolve();
        var node = Assert.IsType<ProcedureCallNode>(r.Graph.Nodes[0]);
        Assert.Equal((StatementStatus.Failed, (int?)null), (node.Status, node.ReturnValue));
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Blocked && e.Source == "st-1");
    }

    [Fact]
    public void Dml_statements_next_to_exec_keep_statement_nodes_and_direct()
    {
        var s = Base();
        s.Statement(1, Op.Update, "t.A").Exec(2, "dbo.usp", ["t.A"])
         .Firing(10, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"))
         .Firing(11, 2, 1, "t.A", Op.Update, ("id=1,v=1", "id=1,v=2"));
        var r = s.Resolve();
        Assert.Equal((CauseKind.Direct, Confidence.Certain), (F(r, 10).Cause!.Kind, F(r, 10).Cause!.Confidence));
        Assert.Equal(CauseKind.ProcDirect, F(r, 11).Cause!.Kind);
        Assert.IsType<StatementNode>(r.Graph.Nodes.Single(n => n.Id == "st-1"));
        Assert.IsType<ProcedureCallNode>(r.Graph.Nodes.Single(n => n.Id == "st-2"));
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Direct && e.Source == "st-1" && e.Target == "f-10");
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.ProcDirect && e.Source == "st-2" && e.Target == "f-11");
    }

    [Fact]
    public void Json_names_follow_the_contract_and_hide_the_plan()
    {
        var s = Base();
        s.Exec(1, "dbo.usp", ["t.A"]).Firing(10, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"));
        var r = s.Resolve();
        var graph = JsonSerializer.Serialize(r.Graph, ContractJson.Options);
        Assert.Contains("\"type\":\"procedureCall\"", graph);
        Assert.Contains("\"kind\":\"PROC_DIRECT\"", graph);
        Assert.Contains("\"unobservedWriteTargets\":[]", graph);
        Assert.Contains("\"kind\":\"PROC_DIRECT\"", JsonSerializer.Serialize(F(r, 10).Cause, ContractJson.Options));

        var st = s.Input().Statements[0];
        var stJson = JsonSerializer.Serialize(st, ContractJson.Options);
        Assert.Contains("\"kind\":\"EXEC\"", stJson);
        Assert.Contains("\"target\":null", stJson);
        Assert.Contains("\"returnValue\":0", stJson);
        Assert.Contains("\"resultSetCount\":0", stJson);
        Assert.Contains("\"procedure\":\"dbo.usp\"", stJson); // 2026-11-13: 문장 결과만으로 프로시저 이름을 안다
        Assert.DoesNotContain("\"procedure\"", JsonSerializer.Serialize(st with { Kind = Op.Update, Target = "t.A", Procedure = null }, ContractJson.Options)); // DML은 필드 없음
        Assert.DoesNotContain("plan", stJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("execSql", stJson, StringComparison.OrdinalIgnoreCase);

        var analyzed = new AnalyzedStatement
        {
            Index = 1, Batch = 1, Position = 1, Kind = Op.Exec, Target = null, Span = st.Span, Sql = "EXEC dbo.usp @a = 1;",
            Procedure = "dbo.usp", Parameters = ["@a = 1"], Plan = st.Plan,
        };
        var aJson = JsonSerializer.Serialize(analyzed, ContractJson.Options);
        Assert.Contains("\"procedure\":\"dbo.usp\"", aJson);
        Assert.Contains("\"parameters\":[\"@a = 1\"]", aJson);
        Assert.DoesNotContain("plan", aJson, StringComparison.OrdinalIgnoreCase);
    }
}
