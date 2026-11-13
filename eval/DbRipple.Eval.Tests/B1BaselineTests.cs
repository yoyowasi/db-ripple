using DbRipple.Eval;
using DbRipple.TestSupport;

namespace DbRipple.Eval.Tests;

/// <summary>대조군 B1(FK 메타데이터 정적 예측). 순수 함수(Predict, Seeds)는 DB 없이 검사하고, 간선 키 형식은 CascadeLab 손 정답 파일과 맞춘다(DB 없음).</summary>
public class B1BaselineTests
{
    static ForeignKeyMeta Fk(string name, string child, string parent, string del = "NO_ACTION", string upd = "NO_ACTION", bool disabled = false) =>
        new(name, child, parent, del, upd, disabled);

    static B1Seed Seed(string table, string op, int stmt = 1) => new(stmt, table, op);

    static string Key(PredictedEdge e) => e.Key;

    static string FkKey(string parent, string child, string name, int stmt = 1) => EdgeKeys.Compose(stmt, parent, child, "FK", name);

    static IReadOnlyList<string> Keys(B1Prediction p) => p.Edges.Select(Key).Order(StringComparer.Ordinal).ToList();

    static IReadOnlyList<string> Sorted(params string[] keys) => keys.Select(k => k.ToUpperInvariant()).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Cascade_chain_of_two_levels_reaches_grandchild_with_delete()
    {
        var p = FkStaticBaseline.Predict([Seed("s.A", "DELETE")],
        [
            Fk("FK_B_A", "s.B", "s.A", del: "CASCADE"),
            Fk("FK_C_B", "s.C", "s.B", del: "CASCADE"),
        ]);
        Assert.Equal(["s.A", "s.B", "s.C"], p.Nodes.Select(n => n.Table));
        Assert.All(p.Nodes, n => Assert.Equal("DELETE", n.Operation));
        Assert.Equal(Sorted(FkKey("F:s.A:DELETE", "F:s.B:DELETE", "FK_B_A"), FkKey("F:s.B:DELETE", "F:s.C:DELETE", "FK_C_B")), Keys(p));
        Assert.All(p.Edges, e => Assert.Equal(("FK", "certain"), (e.Kind, e.Confidence)));
    }

    [Fact]
    public void Set_null_and_set_default_on_delete_make_child_update_and_stop_without_on_update_action()
    {
        var p = FkStaticBaseline.Predict([Seed("s.P", "DELETE")],
        [
            Fk("FK_N", "s.N", "s.P", del: "SET_NULL"),
            Fk("FK_D", "s.D", "s.P", del: "SET_DEFAULT"),
            Fk("FK_NN", "s.NN", "s.N", del: "CASCADE", upd: "NO_ACTION"), // 자식은 UPDATE이므로 부모 UPDATE 규칙을 따른다: NO ACTION이라 멈춘다
        ]);
        Assert.Equal(Sorted(FkKey("F:s.P:DELETE", "F:s.N:UPDATE", "FK_N"), FkKey("F:s.P:DELETE", "F:s.D:UPDATE", "FK_D")), Keys(p));
        Assert.DoesNotContain(p.Nodes, n => n.Table == "s.NN");
    }

    [Fact]
    public void Update_of_child_continues_through_on_update_cascade_only()
    {
        var p = FkStaticBaseline.Predict([Seed("s.P", "DELETE")],
        [
            Fk("FK_N", "s.N", "s.P", del: "SET_NULL"),
            Fk("FK_NN", "s.NN", "s.N", del: "CASCADE", upd: "CASCADE"),
        ]);
        Assert.Contains(FkKey("F:s.N:UPDATE", "F:s.NN:UPDATE", "FK_NN").ToUpperInvariant(), Keys(p));
    }

    [Fact]
    public void No_action_and_disabled_foreign_keys_do_not_widen()
    {
        var p = FkStaticBaseline.Predict([Seed("s.P", "DELETE"), Seed("s.Q", "UPDATE", 2)],
        [
            Fk("FK_NA", "s.A", "s.P"), // NO ACTION
            Fk("FK_OFF", "s.B", "s.P", del: "CASCADE", disabled: true),
            Fk("FK_OFF_U", "s.C", "s.Q", upd: "CASCADE", disabled: true),
        ]);
        Assert.Empty(p.Edges);
        Assert.Equal(["s.P", "s.Q"], p.Nodes.Select(n => n.Table));
    }

    [Fact]
    public void Update_cascade_follows_on_update_but_ignores_on_delete_and_insert_does_not_widen()
    {
        var up = FkStaticBaseline.Predict([Seed("s.P", "UPDATE")],
        [
            Fk("FK_U", "s.U", "s.P", upd: "CASCADE"),
            Fk("FK_DEL", "s.X", "s.P", del: "CASCADE"), // ON DELETE만 있는 FK는 UPDATE에 반응하지 않는다
        ]);
        Assert.Equal(Sorted(FkKey("F:s.P:UPDATE", "F:s.U:UPDATE", "FK_U")), Keys(up));

        var ins = FkStaticBaseline.Predict([Seed("s.P", "INSERT")], [Fk("FK_U", "s.U", "s.P", del: "CASCADE", upd: "CASCADE")]);
        Assert.Empty(ins.Edges);
        Assert.Equal(["s.P"], ins.Nodes.Select(n => n.Table));
    }

    [Fact]
    public void Cyclic_and_self_referencing_foreign_keys_reach_a_fixed_point()
    {
        var cycle = FkStaticBaseline.Predict([Seed("s.A", "DELETE")],
        [
            Fk("FK_B_A", "s.B", "s.A", del: "CASCADE"),
            Fk("FK_A_B", "s.A", "s.B", del: "CASCADE"),
        ]);
        Assert.Equal(["s.A", "s.B"], cycle.Nodes.Select(n => n.Table));
        Assert.Equal(Sorted(FkKey("F:s.A:DELETE", "F:s.B:DELETE", "FK_B_A"), FkKey("F:s.B:DELETE", "F:s.A:DELETE", "FK_A_B")), Keys(cycle));

        // 자기 참조: DELETE → (SET NULL) → 같은 테이블 UPDATE → (ON UPDATE CASCADE) → 같은 UPDATE 상태(한 번만 처리)
        var self = FkStaticBaseline.Predict([Seed("s.T", "DELETE")], [Fk("FK_T_T", "s.T", "s.T", del: "SET_NULL", upd: "CASCADE")]);
        Assert.Equal([("s.T", "DELETE"), ("s.T", "UPDATE")], self.Nodes.Select(n => (n.Table, n.Operation)));
        Assert.Equal(Sorted(FkKey("F:s.T:DELETE", "F:s.T:UPDATE", "FK_T_T"), FkKey("F:s.T:UPDATE", "F:s.T:UPDATE", "FK_T_T")), Keys(self));
    }

    [Fact]
    public void Table_names_match_case_insensitively_and_statements_are_independent()
    {
        var p = FkStaticBaseline.Predict([Seed("S.a", "DELETE", 1), Seed("s.A", "DELETE", 2)], [Fk("FK_B_A", "s.B", "s.a", del: "CASCADE")]);
        Assert.Equal(2, p.Edges.Count);
        Assert.Equal([1, 2], p.Edges.Select(e => e.StatementIndex).Order());
    }

    [Fact]
    public void Changes_are_filtered_to_direct_targets_union_reached_tables()
    {
        var hand = new HandCase("CLX", "CLX", "CascadeLab", "x.json", ["DELETE FROM s.A WHERE id = 1;"], "ok", null, new Dictionary<int, string> { [1] = "ok" },
            new Dictionary<string, IReadOnlyList<string>>(), []);
        OracleRowChange Del(string t) => new(t, "k", ChangeKinds.Deleted, 1, null, null, []);
        IReadOnlyList<OracleRowChange> oracle = [Del("s.A"), Del("s.B"), Del("s.Log")]; // s.Log은 트리거가 쓴 테이블(FK 없음)
        var p = FkStaticBaseline.Predict(FkStaticBaseline.Seeds(hand), [Fk("FK_B_A", "s.B", "s.A", del: "CASCADE")]);
        Assert.Equal(["s.A", "s.B"], FkStaticBaseline.Changes(hand, oracle, p).Select(c => c.Table));
        Assert.Equal(1, FkStaticBaseline.ManualSelectTables(hand, oracle, p)); // 범위 밖은 s.Log 하나
        Assert.Equal(2, FkStaticBaseline.ManualSelectTables(hand, oracle, FkStaticBaseline.Predict([], []))); // FK로 닿은 테이블이 없으면 직접 대상(s.A)만 범위: s.B·s.Log가 밖
    }

    static IReadOnlyDictionary<string, HandCase> Cases() =>
        HandCase.LoadAll(Path.Combine(RepoPaths.CascadeLab, "cases")).ToDictionary(c => c.Id);

    static IReadOnlyList<string> HandFkKeys(HandCase hand) => hand.Edges.Where(e => e.Kind == "FK").Select(e => e.Key).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Seeds_follow_statement_text_alias_fallback_and_proc_rows()
    {
        var cases = Cases();
        Assert.Equal([new B1Seed(1, "cl04.P", "UPDATE")], FkStaticBaseline.Seeds(cases["CL04"]));
        Assert.Equal([new B1Seed(1, "cl14.Tag", "DELETE")], FkStaticBaseline.Seeds(cases["CL14b"])); // DELETE TOP (1) FROM …: 정규식이 못 잡아 손 정답 DIRECT 간선
        Assert.Equal([new B1Seed(1, "cl17.Txn", "INSERT"), new B1Seed(2, "cl17.Acct", "UPDATE"), new B1Seed(3, "cl17.Txn", "DELETE")], FkStaticBaseline.Seeds(cases["CL17"])); // 2번은 별칭 UPDATE
        Assert.Equal(["cl22.ChildA:DELETE", "cl22.Parent:DELETE"], FkStaticBaseline.Seeds(cases["CL22"]).Select(s => s.Table + ":" + s.Operation).Order(StringComparer.Ordinal)); // EXEC: PROC_DIRECT 행
        Assert.Equal(["cl21.Log:INSERT", "cl21.Parent:UPDATE", "cl21.Touch:UPDATE"], FkStaticBaseline.Seeds(cases["CL21"]).Select(s => s.Table + ":" + s.Operation).Order(StringComparer.Ordinal));
    }

    /// <summary>예측 FK 간선 키가 손 정답 FK 간선 키와 같은 형식(문장|부모|자식|FK|FK 이름, 대문자)인지, CascadeLab 사례의 FK 정의(cascadelab/sql)로 확인한다.</summary>
    [Fact]
    public void Predicted_fk_edge_keys_equal_hand_fk_edge_keys_on_cascadelab_cases()
    {
        var cases = Cases();
        ForeignKeyMeta[] cl01 =
        [
            Fk("FK_cl01_B_A", "cl01.B", "cl01.A", del: "CASCADE"), Fk("FK_cl01_C_B", "cl01.C", "cl01.B", del: "CASCADE"), Fk("FK_cl01_D_C", "cl01.D", "cl01.C", del: "CASCADE"),
        ];
        var p1 = FkStaticBaseline.Predict(FkStaticBaseline.Seeds(cases["CL01"]), cl01);
        Assert.Equal(HandFkKeys(cases["CL01"]), Keys(p1));
        Assert.Equal(3, p1.Edges.Count);

        var p4 = FkStaticBaseline.Predict(FkStaticBaseline.Seeds(cases["CL04"]), [Fk("FK_cl04_Ch_P", "cl04.Ch", "cl04.P", upd: "CASCADE")]);
        Assert.Equal(HandFkKeys(cases["CL04"]), Keys(p4));

        ForeignKeyMeta[] cl10 =
        [
            Fk("FK_cl10_Store_Region", "cl10.Store", "cl10.Region", del: "CASCADE"), Fk("FK_cl10_Shelf_Store", "cl10.Shelf", "cl10.Store", del: "CASCADE"),
            Fk("FK_cl10_Slot_Shelf", "cl10.Slot", "cl10.Shelf", del: "CASCADE"), Fk("FK_cl10_Manager_Region", "cl10.Manager", "cl10.Region", del: "SET_NULL"),
        ];
        var p10 = FkStaticBaseline.Predict(FkStaticBaseline.Seeds(cases["CL10"]), cl10);
        Assert.Equal(HandFkKeys(cases["CL10"]), Keys(p10));
        // B1은 트리거(CL10의 Counter·RegionLog)를 못 본다: 손 정답 FIRES·WRITES 간선은 예측에 없다
        var predicted = Baselines.B0Edges(cases["CL10"]).Concat(p10.Edges).Select(Key).ToHashSet(StringComparer.Ordinal);
        var missing = cases["CL10"].Edges.Where(e => !predicted.Contains(e.Key)).Select(e => e.Kind).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(["FIRES", "WRITES"], missing);

        // EXEC(CL22): 손 정답 FK 간선(Parent→ChildB)은 맞히고, 프로시저가 직접 지운 ChildA에도 FK 간선을 낸다(정적 근사의 거짓 양성)
        var p22 = FkStaticBaseline.Predict(FkStaticBaseline.Seeds(cases["CL22"]),
            [Fk("FK_cl22_ChildA_Parent", "cl22.ChildA", "cl22.Parent", del: "CASCADE"), Fk("FK_cl22_ChildB_Parent", "cl22.ChildB", "cl22.Parent", del: "CASCADE")]);
        Assert.Contains(HandFkKeys(cases["CL22"]).Single(), Keys(p22));
        Assert.Equal(2, p22.Edges.Count);
    }

    [Fact]
    public void Scorer_scores_b1_with_fk_edges_and_requires_foreign_keys()
    {
        static string K(string v) => RowKeys.Canonical([new KeyValuePair<string, string?>("id", v)]);
        var hand = new HandCase("CLX", "CLX", "CascadeLab", "x.json", ["DELETE FROM cl.A WHERE id = 1;"], "ok", null, new Dictionary<int, string> { [1] = "ok" },
            new Dictionary<string, IReadOnlyList<string>>(),
            [
                new HandRow("cl.A", ChangeKinds.Deleted, K("1"), ["DIRECT"], [], new Dictionary<string, string?>()),
                new HandRow("cl.B", ChangeKinds.Deleted, K("1"), ["FK_CASCADE"], [], new Dictionary<string, string?>()),
                new HandRow("cl.Log", ChangeKinds.Inserted, K("1"), ["TRIGGER"], [], new Dictionary<string, string?>()),
            ])
        {
            Edges = [new HandEdge(1, "S", "F:cl.A:DELETE", "DIRECT", null, "certain"), new HandEdge(1, "F:cl.A:DELETE", "F:cl.B:DELETE", "FK", "FK_B", "certain")],
        };
        IReadOnlyList<OracleRowChange> changes =
        [
            new("cl.A", K("1"), ChangeKinds.Deleted, 1, new Dictionary<string, string?> { ["id"] = "1" }, null, []),
            new("cl.B", K("1"), ChangeKinds.Deleted, 1, new Dictionary<string, string?> { ["id"] = "1" }, null, []),
            new("cl.Log", K("1"), ChangeKinds.Inserted, 1, null, new Dictionary<string, string?> { ["id"] = "1" }, []),
        ];
        var oracle = new OracleResult("db", [], changes, [new OracleStatement(1, "ok", 1, [])], [], false, 3, "d", true, []);
        var scorer = new CaseScorer(new MetricsCalculator());
        string Col(IReadOnlyList<string?> row, string name) => row[MetricsCsv.M3Columns.ToList().IndexOf(name)]!;

        var score = scorer.Score(new CaseRun
        {
            RunId = "r", System = "B1", Hand = hand, Oracle = oracle,
            ForeignKeys = [Fk("FK_B", "cl.B", "cl.A", del: "CASCADE")],
        }, new SuiteAccumulator());
        var all = score.M3Rows.Single(r => r[4] == "all");
        Assert.Equal(("2", "1.0000", "1.0000", "1"), (Col(all, "predicted_edges"), Col(all, "precision"), Col(all, "recall"), Col(all, "manual_select_tables")));
        Assert.Equal("B1", score.CaseRow[1]);
        Assert.Equal("ok", score.CaseRow[MetricsCsv.CaseColumns.ToList().IndexOf("status_actual")]);
        Assert.Equal("0.6667", score.M1Rows[0][MetricsCsv.M1Columns.ToList().IndexOf("recall")]); // 3개 중 A·B만(Log는 트리거라 놓침)
        Assert.Null(score.Signature);

        Assert.Throws<InvalidOperationException>(() => scorer.Score(new CaseRun { RunId = "r", System = "B1", Hand = hand, Oracle = oracle }, new SuiteAccumulator()));
    }
}
