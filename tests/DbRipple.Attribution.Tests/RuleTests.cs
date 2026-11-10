using DbRipple.Contracts;

namespace DbRipple.Attribution.Tests;

using Op = ChangeOperation;

/// <summary>§7.2 3~7단계와 §6.3 규칙 단위 테스트(CascadeLab에 없는 경로).</summary>
public class RuleTests
{
    static Scenario ParentChild(string onDelete = "CASCADE")
    {
        var s = new Scenario();
        s.Cat.Table("t.P", ["id"], "id").Table("t.C", ["id"], "id", "p_id?=((0))").Table("t.L", ["id"], "id")
             .Fk("FK_C_P", "t.C", "p_id", "t.P", "id", onDelete);
        s.Statement(1, Op.Delete, "t.P");
        return s;
    }

    static Firing F(Resolution r, long seq) => r.Model.Firings.Single(f => f.CaptureSeq == seq);

    // ---- 3단계 FK ----

    [Fact]
    public void Fk_all_rows_match_single_parent_no_contention_is_certain()
    {
        var s = ParentChild();
        s.Firing(10, 1, 1, "t.C", Op.Delete, ("id=1,p_id=7", null), ("id=2,p_id=7", null))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        var r = s.Resolve();
        var c = F(r, 10).Cause!;
        Assert.Equal(CauseKind.FkCascade, c.Kind);
        Assert.Equal(Confidence.Certain, c.Confidence);
        Assert.Equal("f-11", c.ParentFiringId);
        Assert.Equal(2, c.ForeignKey!.MatchedRows);
        Assert.Equal(0, c.ForeignKey.UnmatchedRows);
        Assert.Equal(OrderGuarantee.Guaranteed, F(r, 10).OrderGuarantee);
        Assert.All(r.Model.RowChanges.Where(x => x.FiringId == "f-10"), x => Assert.Equal(["rc-11-1"], x.ParentRowIds));
        var e = r.Graph.Edges.Single(x => x.Kind == EdgeKind.Fk);
        Assert.Equal(("f-11", "f-10", "FK_C_P", FkAction.Cascade, 2L), (e.Source, e.Target, e.Fk, e.Action!.Value, e.MatchedRows!.Value));
    }

    [Fact]
    public void Fk_partial_match_is_inferred_and_counts_unmatched()
    {
        var s = ParentChild();
        s.Firing(10, 1, 1, "t.C", Op.Delete, ("id=1,p_id=7", null), ("id=2,p_id=8", null))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        var c = F(s.Resolve(), 10).Cause!;
        Assert.Equal(CauseKind.FkCascade, c.Kind);
        Assert.Equal(Confidence.Inferred, c.Confidence);
        Assert.Equal((1L, 1L), (c.ForeignKey!.MatchedRows, c.ForeignKey.UnmatchedRows));
    }

    [Fact]
    public void Fk_two_parent_candidate_firings_is_inferred_and_observed_only()
    {
        // 같은 문장에 부모 테이블 DELETE 묶음이 둘(문장 직접 + 트리거가 만든 2차 체인) → 루트 후보 둘 이상(§7.2 3단계 inferred).
        var s = ParentChild();
        s.Cat.Trigger("t.trg_P_del", "t.P", [Op.Delete], ["t.P"]);
        s.Firing(10, 1, 2, "t.C", Op.Delete, ("id=2,p_id=8", null))
         .Firing(11, 1, 2, "t.P", Op.Delete, ("id=8", null))
         .Firing(12, 1, 1, "t.P", Op.Delete, ("id=7", null))
         .Link(10, Confidence.Inferred, 12).Link(11, Confidence.Inferred, 12);
        var r = s.Resolve();
        var c = F(r, 10).Cause!;
        Assert.Equal(CauseKind.FkCascade, c.Kind);
        Assert.Equal(Confidence.Inferred, c.Confidence);
        Assert.Equal("f-11", c.ParentFiringId);
        Assert.Equal(OrderGuarantee.ObservedOnly, F(r, 10).OrderGuarantee);
        Assert.Equal(CauseKind.Trigger, F(r, 11).Cause!.Kind);
    }

    [Fact]
    public void Fk_with_trigger_that_can_write_child_is_inferred()
    {
        // 같은 문맥의 트리거가 자식 테이블에 쓸 수 있으면 certain을 주지 않는다(쓰기 경합).
        var s = ParentChild();
        s.Cat.Trigger("t.trg_P_del", "t.P", [Op.Delete], ["t.C", "t.L"]);
        s.Firing(10, 1, 1, "t.C", Op.Delete, ("id=1,p_id=7", null))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        Assert.Equal(Confidence.Inferred, F(s.Resolve(), 10).Cause!.Confidence);
    }

    [Fact]
    public void Fk_with_dynamic_sql_trigger_in_statement_is_inferred()
    {
        var s = ParentChild();
        s.Cat.Trigger("t.trg_P_del", "t.P", [Op.Delete], [], dynamicSql: true);
        s.Firing(10, 1, 1, "t.C", Op.Delete, ("id=1,p_id=7", null))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        Assert.Equal(Confidence.Inferred, F(s.Resolve(), 10).Cause!.Confidence);
    }

    [Fact]
    public void Fk_keys_mode_rows_are_unverifiable_so_inferred()
    {
        var s = ParentChild();
        s.FiringWith(10, 1, 1, "t.C", Op.Delete, ImageMode.Keys, ("id=1,p_id=7", null))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        var c = F(s.Resolve(), 10).Cause!;
        Assert.Equal(CauseKind.FkCascade, c.Kind);
        Assert.Equal(Confidence.Inferred, c.Confidence);
    }

    [Fact]
    public void Disabled_fk_is_not_a_candidate()
    {
        var s = new Scenario();
        s.Cat.Table("t.P", ["id"], "id").Table("t.C", ["id"], "id", "p_id").Fk("FK_C_P", "t.C", "p_id", "t.P", "id", "CASCADE", disabled: true);
        s.Statement(1, Op.Delete, "t.P")
         .Firing(10, 1, 1, "t.C", Op.Delete, ("id=1,p_id=7", null))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        var c = F(s.Resolve(), 10).Cause!;
        Assert.Equal(CauseKind.Unexplained, c.Kind);
        Assert.Equal(Confidence.Unexplained, c.Confidence);
    }

    [Fact]
    public void Set_default_requires_after_equal_to_default()
    {
        var s = ParentChild("SET_DEFAULT");
        s.Firing(10, 1, 1, "t.C", Op.Update, ("id=1,p_id=7", "id=1,p_id=5"))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        Assert.Equal(CauseKind.Unexplained, F(s.Resolve(), 10).Cause!.Kind);

        var ok = ParentChild("SET_DEFAULT");
        ok.Firing(10, 1, 1, "t.C", Op.Update, ("id=1,p_id=7", "id=1,p_id=0"))
          .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        var c = F(ok.Resolve(), 10).Cause!;
        Assert.Equal((CauseKind.FkSetDefault, Confidence.Certain), (c.Kind, c.Confidence));
    }

    [Fact]
    public void Set_null_requires_null_after()
    {
        var s = ParentChild("SET_NULL");
        s.Firing(10, 1, 1, "t.C", Op.Update, ("id=1,p_id=7", "id=1,p_id=null"), ("id=2,p_id=7", "id=2,p_id=3"))
         .Firing(11, 1, 1, "t.P", Op.Delete, ("id=7", null));
        var c = F(s.Resolve(), 10).Cause!;
        Assert.Equal((CauseKind.FkSetNull, Confidence.Inferred, 1L), (c.Kind, c.Confidence, c.ForeignKey!.UnmatchedRows));
    }

    [Fact]
    public void Update_cascade_fills_parent_row_ids_only_when_parent_is_paired()
    {
        var single = CascadeLabCases.Load("CL04b");
        var r = new CausalResolver().Resolve(single.Input);
        var ch = r.Model.RowChanges.Single(x => x.FiringId == "f-311");
        Assert.Equal(["rc-312-1"], ch.ParentRowIds);

        var multi = CascadeLabCases.Load("CL04");
        var r2 = new CausalResolver().Resolve(multi.Input);
        Assert.All(r2.Model.RowChanges.Where(x => x.FiringId == "f-301"), x => Assert.Empty(x.ParentRowIds));
        Assert.Equal(Confidence.Certain, r2.Model.Firings.Single(f => f.Id == "f-301").Cause!.Confidence);
    }

    // ---- 4단계 트리거 ----

    static Scenario TriggerBase()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id", "v").Table("t.B", ["id"], "id", "x");
        s.Statement(1, Op.Update, "t.A")
         .Firing(20, 1, 2, "t.B", Op.Update, ("id=1,x=0", "id=1,x=1"))
         .Firing(21, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"));
        return s;
    }

    [Fact]
    public void Single_trigger_candidate_is_inferred_and_writes_not_above_fires()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"]);
        s.Link(20, Confidence.Inferred, 21);
        var r = s.Resolve();
        var c = F(r, 20).Cause!;
        Assert.Equal((CauseKind.Trigger, Confidence.Inferred, "f-21"), (c.Kind, c.Confidence, c.ParentFiringId));
        Assert.Equal(["t.trg_A"], c.TriggerCandidates);
        var t = Assert.Single(r.Graph.Nodes.OfType<TriggerNode>());
        Assert.True(t.Observed);
        Assert.Equal("f-21", t.ParentFiringId);
    }

    [Fact]
    public void Two_trigger_candidates_are_ambiguous()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A1", "t.A", [Op.Update], ["t.B"]).Trigger("t.trg_A2", "t.A", [Op.Update], ["t.B"]);
        s.Link(20, Confidence.Inferred, 21);
        var r = s.Resolve();
        var c = F(r, 20).Cause!;
        Assert.Equal((CauseKind.Trigger, Confidence.Ambiguous), (c.Kind, c.Confidence));
        Assert.Equal(["t.trg_A1", "t.trg_A2"], c.TriggerCandidates);
        var writes = r.Graph.Edges.Where(e => e.Kind == EdgeKind.Writes).ToList();
        Assert.Equal(2, writes.Count);
        Assert.All(writes, w => Assert.Equal((Confidence.Ambiguous, OrderGuarantee.ObservedOnly), (w.Confidence, w.OrderGuarantee)));
        Assert.All(r.Graph.Edges.Where(e => e.Kind == EdgeKind.Fires), f => Assert.Equal(Confidence.Inferred, f.Confidence));
    }

    [Fact]
    public void Trigger_that_does_not_write_the_table_is_not_a_candidate_and_shows_unobserved()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.A"]);
        s.Link(20, Confidence.Inferred, 21);
        var r = s.Resolve();
        Assert.Equal(CauseKind.Unexplained, F(r, 20).Cause!.Kind);
        var t = Assert.Single(r.Graph.Nodes.OfType<TriggerNode>());
        Assert.False(t.Observed);
        Assert.DoesNotContain(r.Graph.Edges, e => e.Target == "f-20");
    }

    [Fact]
    public void Link_cap_ambiguous_caps_trigger_attribution()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"]);
        s.Link(20, Confidence.Ambiguous, 21);
        Assert.Equal(Confidence.Ambiguous, F(s.Resolve(), 20).Cause!.Confidence);
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("unparsed")]
    [InlineData("no-static-info")]
    public void Opaque_trigger_candidate_is_ambiguous_at_most(string kind)
    {
        // Q6: 암호화·파싱 실패(·정적 분석 누락) 트리거가 낸 후보는 ambiguous 이하.
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], [], encrypted: kind == "encrypted", unparsed: kind == "unparsed", staticInfo: kind != "no-static-info");
        s.Link(20, Confidence.Inferred, 21);
        var c = F(s.Resolve(), 20).Cause!;
        Assert.Equal((CauseKind.Trigger, Confidence.Ambiguous), (c.Kind, c.Confidence));
    }

    [Fact]
    public void Disabled_trigger_is_not_a_candidate()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"], disabled: true);
        s.Link(20, Confidence.Inferred, 21);
        var r = s.Resolve();
        Assert.Equal(CauseKind.Unexplained, F(r, 20).Cause!.Kind);
        Assert.Empty(r.Graph.Nodes.OfType<TriggerNode>());
    }

    [Fact]
    public void Nested_triggers_off_means_depth_2_firings_fire_no_trigger()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"]).Trigger("t.trg_B", "t.B", [Op.Update], []);
        s.Link(20, Confidence.Inferred, 21);
        var r = s.Resolve(nestedTriggers: 0);
        Assert.Equal(["t.trg_A"], r.Graph.Nodes.OfType<TriggerNode>().Select(t => t.Trigger));
        var r2 = s.Resolve(nestedTriggers: 1);
        Assert.Equal(["t.trg_A", "t.trg_B"], r2.Graph.Nodes.OfType<TriggerNode>().Select(t => t.Trigger));
    }

    [Fact]
    public void Missing_context_link_falls_back_to_ambiguous()
    {
        var s = TriggerBase();
        s.Cat.Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"]);
        Assert.Equal(Confidence.Ambiguous, F(s.Resolve(), 20).Cause!.Confidence);
    }

    // ---- 5단계 직접 대상·INSTEAD OF ----

    [Fact]
    public void Instead_of_creates_virtual_trigger_node_with_replaces_edge()
    {
        var s = new Scenario();
        s.Cat.Table("t.V", ["id"], "id").Table("t.T", ["id"], "id")
             .Trigger("t.io_V", "t.V", [Op.Insert], ["t.T"], insteadOf: true);
        s.Statement(1, Op.Insert, "t.V")
         .Firing(30, 1, 2, "t.T", Op.Insert, (null, "id=1"))
         .Link(30, Confidence.Inferred);
        var r = s.Resolve();
        var c = F(r, 30).Cause!;
        Assert.Equal((CauseKind.InsteadOf, Confidence.Inferred, (string?)null), (c.Kind, c.Confidence, c.ParentFiringId));
        Assert.Equal(["t.io_V"], c.TriggerCandidates);
        var t = Assert.Single(r.Graph.Nodes.OfType<TriggerNode>());
        Assert.Null(t.ParentFiringId);
        Assert.True(t.Observed);
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Replaces && e.Source == "st-1" && e.Target == t.Id && e.Confidence == Confidence.Inferred);
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Writes && e.Source == t.Id && e.Target == "f-30" && e.Confidence == Confidence.Inferred);
        Assert.DoesNotContain(r.Graph.Edges, e => e.Kind == EdgeKind.Direct);
    }

    [Fact]
    public void Instead_of_blocked_statement_has_error_node_and_unobserved_trigger()
    {
        var s = new Scenario();
        s.Cat.Table("t.E", ["id"], "id").Trigger("t.dE", "t.E", [Op.Delete], [], insteadOf: true);
        s.Statement(1, Op.Delete, "t.E", StatementStatus.TriggerBlocked,
                new StatementError { ErrorNumber = 3609, Message = "The transaction ended in the trigger.", Procedure = "dE", IsToolFault = false })
         .Statement(2, Op.Delete, "t.E", StatementStatus.NotRun);
        var r = s.Resolve();
        var err = Assert.Single(r.Graph.Nodes.OfType<ErrorNode>());
        Assert.Equal(("err-1", 3609, false), (err.Id, err.ErrorNumber, err.IsToolFault));
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Blocked && e.Source == "st-1" && e.Target == "err-1" && e.Confidence == Confidence.Certain);
        var t = Assert.Single(r.Graph.Nodes.OfType<TriggerNode>());
        Assert.False(t.Observed);
        Assert.Equal(1, t.StatementIndex);
        Assert.DoesNotContain(r.Graph.Edges, e => e.Source == "st-2" || e.Target == "st-2");
    }

    [Fact]
    public void Depth_1_firing_that_is_not_target_and_has_no_fk_is_unexplained()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id").Table("t.Z", ["id"], "id");
        s.Statement(1, Op.Delete, "t.A")
         .Firing(1, 1, 1, "t.Z", Op.Delete, ("id=1", null))
         .Firing(2, 1, 1, "t.A", Op.Delete, ("id=1", null));
        var r = s.Resolve();
        Assert.Equal((CauseKind.Unexplained, Confidence.Unexplained, OrderGuarantee.ObservedOnly), (F(r, 1).Cause!.Kind, F(r, 1).Cause!.Confidence, F(r, 1).OrderGuarantee!.Value));
        Assert.Equal((CauseKind.Direct, Confidence.Certain), (F(r, 2).Cause!.Kind, F(r, 2).Cause!.Confidence));
    }

    // ---- 6단계 잔차 ----

    [Fact]
    public void Residual_category_order_excluded_then_unobservable_then_same_table_trigger_then_unknown()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id", "v").Table("t.X", ["id"], "id").Table("t.U", ["id"], "id", "doc").Table("t.W", ["id"], "id").Table("t.Q", ["id"], "id")
             .Trigger("t.trg_A", "t.A", [Op.Update], ["t.X", "t.W"])
             .Exclude("t.X").Unobservable("t.U", "doc");
        s.Statement(1, Op.Update, "t.A")
         .Firing(1, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"))
         .Firing(2, 1, 1, "t.U", Op.Update, ("id=5", "id=5"))
         .Net("t.A", NetChangeKind.Updated, "id=1", "v=0", "v=1", "v")
         .Net("t.X", NetChangeKind.Inserted, "id=9", after: "id=9")        // 제외 테이블(트리거도 씀) → EXCLUDED_TABLE이 먼저
         .Net("t.U", NetChangeKind.Updated, "id=5", "doc=a", "doc=b", "doc") // 수집 불가 열
         .Net("t.W", NetChangeKind.Inserted, "id=3", after: "id=3")        // 그 테이블에 쓰는 트리거가 실행됨
         .Net("t.Q", NetChangeKind.Deleted, "id=4", before: "id=4");        // 후보 없음
        var r = s.Resolve();
        var res = r.Model.Residuals.ToDictionary(x => x.Table);
        Assert.Equal(4, res.Count);
        Assert.Equal((ResidualCategory.ExcludedTable, Confidence.Inferred), (res["t.X"].Category, res["t.X"].Confidence));
        Assert.Equal(["t.trg_A"], res["t.X"].Candidates);
        Assert.Equal((ResidualCategory.UnobservableColumn, Confidence.Inferred), (res["t.U"].Category, res["t.U"].Confidence));
        Assert.Equal(["f-2"], res["t.U"].Candidates);
        Assert.Equal((ResidualCategory.SameTableTrigger, Confidence.Inferred), (res["t.W"].Category, res["t.W"].Confidence));
        Assert.Equal((ResidualCategory.Unknown, Confidence.Unexplained), (res["t.Q"].Category, res["t.Q"].Confidence));
        Assert.All(res.Values, x => Assert.Equal(ResidualDetectedBy.NetDiff, x.DetectedBy));
        Assert.Equal(["r-1", "r-2", "r-3", "r-4"], r.Model.Residuals.Select(x => x.Id));

        var netA = r.Model.NetChanges.Single(n => n.Table == "t.A");
        Assert.Equal(["f-1"], netA.ExplainedBy!.FiringIds);
        Assert.Null(netA.ExplainedBy.ResidualId);
        var netU = r.Model.NetChanges.Single(n => n.Table == "t.U");
        Assert.Equal(["f-2"], netU.ExplainedBy!.FiringIds);
        Assert.Equal(res["t.U"].Id, netU.ExplainedBy.ResidualId);

        Assert.Equal(4, r.Graph.Nodes.OfType<ResidualNode>().Count());
        var residualEdges = r.Graph.Edges.Where(e => e.Kind == EdgeKind.Residual).ToList();
        Assert.All(residualEdges, e => Assert.Equal(OrderGuarantee.ObservedOnly, e.OrderGuarantee));
        Assert.Contains(residualEdges, e => e.Target == res["t.U"].Id && e.Source == "f-2");
        Assert.DoesNotContain(residualEdges, e => e.Target == res["t.Q"].Id);
    }

    [Fact]
    public void Reread_difference_becomes_reread_residual_linked_from_net_change()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id", "v", "w").Trigger("t.trg_A", "t.A", [Op.Update], ["t.A"]);
        s.Statement(1, Op.Update, "t.A")
         .Firing(1, 1, 1, "t.A", Op.Update, ("id=1,v=0,w=0", "id=1,v=1,w=0"))
         .Reread("rc-1-1", "t.A", "id=1", "w")
         .Net("t.A", NetChangeKind.Updated, "id=1", "v=0,w=0", "v=1,w=5", "v", "w");
        var r = s.Resolve();
        var res = Assert.Single(r.Model.Residuals);
        Assert.Equal((ResidualDetectedBy.Reread, "rc-1-1", ResidualCategory.SameTableTrigger, Confidence.Inferred), (res.DetectedBy, res.RowChangeId, res.Category, res.Confidence));
        Assert.Equal(["w"], res.Columns);
        var n = Assert.Single(r.Model.NetChanges);
        Assert.Equal(["f-1"], n.ExplainedBy!.FiringIds);
        Assert.Equal("r-1", n.ExplainedBy.ResidualId);
        Assert.Contains(r.Graph.Edges, e => e.Kind == EdgeKind.Residual && e.Target == "r-1" && e.Evidence.Contains(Evidence.Reread));
    }

    [Fact]
    public void Net_update_with_column_changed_outside_audit_is_residual()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id", "v", "w", "c~");
        s.Statement(1, Op.Update, "t.A")
         .Firing(1, 1, 1, "t.A", Op.Update, ("id=1,v=0,w=0", "id=1,v=1,w=0"))
         .Net("t.A", NetChangeKind.Updated, "id=1", "v=0,w=0,c=0", "v=1,w=9,c=1", "v", "w", "c");
        var r = s.Resolve();
        var res = Assert.Single(r.Model.Residuals);
        Assert.Equal(["w"], res.Columns); // 계산 열 c는 파생이라 빠진다
        Assert.Equal(ResidualCategory.Unknown, res.Category);
    }

    // ---- §6.3 접기 ----

    [Fact]
    public void Graph_over_200_nodes_folds_same_table_operation_depth_into_group()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id", "v").Table("t.B", ["id"], "id").Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"]);
        s.Statement(1, Op.Update, "t.A");
        for (var i = 1; i <= 210; i++) s.Firing(i, 1, 2, "t.B", Op.Insert, (null, $"id={i}")).Link(i, Confidence.Inferred, 1000);
        s.Firing(1000, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"));
        var r = s.Resolve();
        Assert.Equal(4, r.Graph.Nodes.Count); // st-1, f-1000, t-1, g-1
        var g = Assert.Single(r.Graph.Nodes.OfType<GroupNode>());
        Assert.Equal(("g-1", 210, 210L), (g.Id, g.Members.Count, g.RowCount));
        Assert.Equal(3, r.Graph.Edges.Count); // DIRECT, FIRES, WRITES(합침)
        var w = r.Graph.Edges.Single(e => e.Kind == EdgeKind.Writes);
        Assert.Equal(("t-1", "g-1", Confidence.Inferred), (w.Source, w.Target, w.Confidence));
        Assert.Equal(211, r.Model.Firings.Count); // 모델은 접지 않는다
    }

    [Fact]
    public void Graph_at_or_below_200_nodes_is_not_folded()
    {
        var s = new Scenario();
        s.Cat.Table("t.A", ["id"], "id", "v").Table("t.B", ["id"], "id").Trigger("t.trg_A", "t.A", [Op.Update], ["t.B"]);
        s.Statement(1, Op.Update, "t.A");
        for (var i = 1; i <= 197; i++) s.Firing(i, 1, 2, "t.B", Op.Insert, (null, $"id={i}")).Link(i, Confidence.Inferred, 1000);
        s.Firing(1000, 1, 1, "t.A", Op.Update, ("id=1,v=0", "id=1,v=1"));
        var r = s.Resolve();
        Assert.Equal(200, r.Graph.Nodes.Count);
        Assert.Empty(r.Graph.Nodes.OfType<GroupNode>());
    }

    [Fact]
    public void Graph_serializes_with_type_discriminators()
    {
        var r = new CausalResolver().Resolve(CascadeLabCases.Load("CL06").Input);
        var json = System.Text.Json.JsonSerializer.Serialize(r.Graph, ContractJson.Options);
        Assert.Contains("\"type\":\"statement\"", json);
        Assert.Contains("\"type\":\"trigger\"", json);
        Assert.Contains("\"kind\":\"FIRES\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<GraphDto>(json, ContractJson.Options)!;
        Assert.Equal(r.Graph.Nodes.Count, back.Nodes.Count);
    }

    [Fact]
    public void Result_is_deterministic()
    {
        var input = CascadeLabCases.Load("CL07").Input;
        var a = System.Text.Json.JsonSerializer.Serialize(new CausalResolver().Resolve(input).Graph, ContractJson.Options);
        var reversed = input with { Collected = input.Collected with { Firings = input.Collected.Firings.Reverse().ToList(), RowChanges = input.Collected.RowChanges.Reverse().ToList() } };
        var b = System.Text.Json.JsonSerializer.Serialize(new CausalResolver().Resolve(reversed).Graph, ContractJson.Options);
        Assert.Equal(a, b);
    }

    [Theory]
    [MemberData(nameof(CascadeLabM3Tests.Cases), MemberType = typeof(CascadeLabM3Tests))]
    public void CascadeLab_results_satisfy_graph_invariants(string id) =>
        GraphInvariants.Check(new CausalResolver().Resolve(CascadeLabCases.Load(id).Input));

    // ---- §6.3 statement label(문장 앞부분, 2026-11-10 통합) ----

    [Fact]
    public void Statement_label_is_head_of_statement_sql_when_given_else_kind_and_target()
    {
        var s = ParentChild();
        s.Statement(2, Op.Delete, "t.L");
        s.Statement(3, Op.Delete, "t.L");
        var sql = "DELETE   FROM t.P\r\n  WHERE id = 7 AND id IN (SELECT p_id FROM t.C WHERE p_id IS NOT NULL AND id > 0);";
        var r = new CausalResolver().Resolve(s.Input() with { StatementSql = new Dictionary<int, string> { [1] = sql, [3] = "DELETE FROM t.L WHERE id = 1 ;" } });
        var nodes = r.Graph.Nodes.OfType<StatementNode>().ToDictionary(n => n.StatementIndex);
        Assert.Equal("DELETE FROM t.P WHERE id = 7 AND id IN (SELECT p_id FROM t.C WHERE p_id IS NOT …", nodes[1].Label); // 공백 접기, 80자 안 마지막 공백에서 자름 + " …"
        Assert.Equal("DELETE t.L", nodes[2].Label);                   // 원문이 없는 문장
        Assert.Equal("DELETE FROM t.L WHERE id = 1", nodes[3].Label); // 끝 세미콜론 제외
        Assert.Equal("DELETE t.P", s.Resolve().Graph.Nodes.OfType<StatementNode>().Single(n => n.StatementIndex == 1).Label); // StatementSql 없음
    }

    // ---- 프로시저를 부르는 트리거(2026-11-10 통합 검토): 쓰기 대상을 펼치지 않으므로 모든 테이블에 쓸 수 있다고 본다 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trigger_calling_a_procedure_is_fk_write_contention_and_its_writes_are_at_most_ambiguous(bool callsProcedure)
    {
        // 검토 시나리오: DELETE t.X → trg_X가 EXEC dbo.usp → usp가 t.C(p_id = 7)를 지운 뒤 t.P(id = 7)를 지운다. 기록은 FK 연쇄와 구별되지 않는다.
        var s = ParentChild();
        s.Cat.Table("t.X", ["id"], "id")
             .Trigger("t.trg_X", "t.X", [Op.Delete], [], procedureCalls: callsProcedure ? ["dbo.usp"] : null);
        s.Statement(2, Op.Delete, "t.X");
        s.Firing(20, 2, 2, "t.C", Op.Delete, ("id=1,p_id=7", null))
         .Firing(21, 2, 2, "t.P", Op.Delete, ("id=7", null))
         .Firing(22, 2, 1, "t.X", Op.Delete, ("id=1", null));
        var r = s.Resolve();
        var c = F(r, 20).Cause!;
        if (!callsProcedure)
        {
            Assert.Equal((CauseKind.FkCascade, Confidence.Certain), (c.Kind, c.Confidence)); // 대조: 프로시저 호출이 없으면 경합 없음
            return;
        }
        Assert.NotEqual(Confidence.Certain, c.Confidence);
        Assert.DoesNotContain(r.Graph.Edges, e => e.Kind == EdgeKind.Fk && e.Target == "f-20" && e.Confidence == Confidence.Certain);
        Assert.All(r.Graph.Edges.Where(e => e.Kind == EdgeKind.Writes), e => Assert.True(e.Confidence is not (Confidence.Certain or Confidence.Inferred), $"{e.Source}->{e.Target} {e.Confidence}"));
    }
}
