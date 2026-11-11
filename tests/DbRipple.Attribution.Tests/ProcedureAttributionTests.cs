using DbRipple.Contracts;

namespace DbRipple.Attribution.Tests;

using Op = ChangeOperation;

/// <summary>
/// 2주차 인계 1(3주차): 프로시저를 부르는 트리거. FK 쓰기 경합은 못 펼친 프로시저가 있으면 미상을 유지하고,
/// WRITES·TRIGGER 하향은 진짜 미상(WriteTargetsUnknown, 또는 못 펼친 프로시저가 있고 그 테이블이 정적 쓰기 대상에 없음)에만 한다. 호환 Q38. E22 비활성 FK·트리거.
/// </summary>
public class ProcedureAttributionTests
{
    static Firing F(Resolution r, long seq) => r.Model.Firings.Single(f => f.CaptureSeq == seq);

    /// <summary>UPDATE t.A → trg_A가 t.B를 갱신(깊이 2). 트리거 정의는 호출자가 붙인다.</summary>
    static Scenario TriggerBase()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id", "v").Table("t.B", ["id"], "id", "x");
        s.Statement(1, Op.Update, "t.A")
         .Firing(20, 1, 2, "t.B", Op.Update, ("id=1,x=0", "id=1,x=1"))
         .Firing(21, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"));
        return s;
    }

    /// <summary>DELETE t.X → trg_X(깊이 2에 t.C·t.P DELETE). t.C → t.P ON DELETE CASCADE(fkDisabled면 비활성).</summary>
    static Scenario Contention(bool fkDisabled = false)
    {
        var s = new Scenario();
        s.Cat.Table("t.P", ["id"], "id").Table("t.C", ["id"], "id", "p_id").Table("t.L", ["id"], "id").Table("t.X", ["id"], "id")
             .Fk("FK_C_P", "t.C", "p_id", "t.P", "id", "CASCADE", disabled: fkDisabled);
        s.Statement(1, Op.Delete, "t.X");
        s.Firing(20, 1, 2, "t.C", Op.Delete, ("id=1,p_id=7", null))
         .Firing(21, 1, 2, "t.P", Op.Delete, ("id=7", null))
         .Firing(22, 1, 1, "t.X", Op.Delete, ("id=1", null));
        return s;
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("compat")]   // Q38: 펼치기 전 분석기(두 목록 빔) → 전부 미상
    public void Static_write_target_keeps_inferred_even_with_unresolved_procedure(string mode)
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"], procedureCalls: ["dbo.usp"], unresolvedCalls: mode == "unresolved" ? ["dbo.usp"] : null);
        s.Link(20, Confidence.Inferred, 21);
        var r = s.Resolve();
        var c = F(r, 20).Cause!;
        Assert.Equal((CauseKind.Trigger, Confidence.Inferred), (c.Kind, c.Confidence)); // 2주차에는 ambiguous(정밀도 퇴행)
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Writes && e.Target == "f-20" && e.Confidence == Confidence.Inferred);
        GraphInvariants.Check(r);
    }

    [Fact]
    public void Table_outside_static_targets_with_unresolved_procedure_is_ambiguous()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.Other"], procedureCalls: ["dbo.usp"], unresolvedCalls: ["dbo.usp"]);
        s.Link(20, Confidence.Inferred, 21);
        var c = F(s.Resolve(), 20).Cause!;
        Assert.Equal((CauseKind.Trigger, Confidence.Ambiguous), (c.Kind, c.Confidence));
    }

    [Fact]
    public void Expanded_procedure_only_is_not_unknown()
    {
        // 펼친 프로시저만 부르고 그 쓰기 대상(t.B)이 합쳐져 있으면 일반 트리거와 같다(AW uspLogError → dbo.ErrorLog)
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"], procedureCalls: ["dbo.uspLogError"], expandedCalls: ["dbo.uspLogError"]);
        s.Link(20, Confidence.Inferred, 21);
        Assert.Equal(Confidence.Inferred, F(s.Resolve(), 20).Cause!.Confidence);
    }

    [Theory]
    [InlineData("expanded", true)]
    [InlineData("unresolved", false)]
    [InlineData("compat", false)]
    public void Fk_write_contention_stays_unknown_only_for_unresolved_procedures(string mode, bool certain)
    {
        // 못 펼친 프로시저는 자식 다음 부모를 지울 수 있어 FK 연쇄와 구별할 수 없다 → certain 금지. 펼친 프로시저의 쓰기 대상이 t.L뿐이면 경합 없음.
        var s = Contention();
        s.Cat.Trigger("t.trg_X", "t.X", [Op.Delete], ["t.L"], procedureCalls: ["dbo.usp"],
            expandedCalls: mode == "expanded" ? ["dbo.usp"] : null, unresolvedCalls: mode == "unresolved" ? ["dbo.usp"] : null);
        var r = s.Resolve();
        var c = F(r, 20).Cause!;
        Assert.Equal(CauseKind.FkCascade, c.Kind);
        if (certain) Assert.Equal(Confidence.Certain, c.Confidence);
        else Assert.NotEqual(Confidence.Certain, c.Confidence);
    }

    // ---- E22 비활성 FK·트리거 ----

    [Fact]
    public void E22_disabled_trigger_with_unresolved_procedure_does_not_cause_contention()
    {
        var s = Contention();
        s.Cat.Trigger("t.trg_X", "t.X", [Op.Delete], [], disabled: true, procedureCalls: ["dbo.usp"], unresolvedCalls: ["dbo.usp"]);
        var r = s.Resolve();
        Assert.Equal((CauseKind.FkCascade, Confidence.Certain), (F(r, 20).Cause!.Kind, F(r, 20).Cause!.Confidence));
        Assert.Empty(r.Graph.Nodes.OfType<TriggerNode>());
    }

    [Fact]
    public void E22_disabled_fk_leaves_trigger_as_the_only_candidate()
    {
        var s = Contention(fkDisabled: true);
        s.Cat.Trigger("t.trg_X", "t.X", [Op.Delete], ["t.C", "t.P"]);
        s.Link(20, Confidence.Inferred, 22).Link(21, Confidence.Inferred, 22);
        var r = s.Resolve();
        var c = F(r, 20).Cause!;
        Assert.Equal(CauseKind.Trigger, c.Kind);
        Assert.Null(c.ForeignKey);
        Assert.DoesNotContain(r.Graph.Edges, e => e.Kind == EdgeKind.Fk);
        GraphInvariants.Check(r);
    }

    [Fact]
    public void E22_disabled_catalog_only_trigger_is_not_a_candidate()
    {
        // 정적 분석이 없는(카탈로그에만 있는) 비활성 트리거도 빠진다
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], [], disabled: true, staticInfo: false);
        s.Link(20, Confidence.Inferred, 21);
        var r = s.Resolve();
        Assert.Equal(CauseKind.Unexplained, F(r, 20).Cause!.Kind);
        Assert.Empty(r.Graph.Nodes.OfType<TriggerNode>());
    }
}
