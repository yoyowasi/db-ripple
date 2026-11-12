using System.Globalization;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Eval;

namespace DbRipple.Eval.Tests;

/// <summary>3주차 하네스(eval-2): IDENTITY 짝 매핑, 키 정렬 규칙, 힙 키, M3, 대조군, M4, M5, CSV 읽기, 보고서. 가짜 제품 결과로 검사한다(DB 없음).</summary>
public class Week3MetricsTests
{
    static Dictionary<string, string?> V(params (string C, string? V)[] p) => p.ToDictionary(x => x.C, x => x.V, StringComparer.OrdinalIgnoreCase);
    static string K(string col, string v) => RowKeys.Canonical([new KeyValuePair<string, string?>(col, v)]);
    static OracleColumn Col(string name, int id, string type = "int", bool identity = false, bool computed = false, bool ndDefault = false, string? collation = null) =>
        new(name, id, type, identity, false, computed, false, ndDefault) { Collation = collation };

    // cl16.Ord(id IDENTITY PK, memo, created_at GETDATE) ← cl16.OrdLine(id IDENTITY PK, ord_id FK, memo)
    static readonly OracleTable Ord = new("cl16", "Ord", OracleKeyKind.PK, ["id"], [Col("id", 1, identity: true), Col("memo", 2, "varchar"), Col("created_at", 3, "datetime2", ndDefault: true)]);
    static readonly OracleTable OrdLine = new("cl16", "OrdLine", OracleKeyKind.PK, ["id"], [Col("id", 1, identity: true), Col("ord_id", 2), Col("memo", 3, "varchar")])
    {
        ForeignKeys = [new OracleForeignKey("FK_OrdLine_Ord", ["ord_id"], "cl16.Ord", ["id"])],
    };
    static readonly OracleTable Seen = new("cl16", "Seen", OracleKeyKind.PK, ["title"], [Col("title", 1, "varchar"), Col("seen_identity", 2)]);

    static OracleRowChange Ins(string t, string keyCol, string key, Dictionary<string, string?> after) => new(t, K(keyCol, key), ChangeKinds.Inserted, 1, null, after, []);

    static (IReadOnlyList<OracleRowChange> Truth, IReadOnlyList<OracleRowChange> Pred) Cl16(string truthBase, string predBase)
    {
        string Id(string b, int d) => (int.Parse(b, CultureInfo.InvariantCulture) + d).ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<OracleRowChange> Side(string b, string at) =>
        [
            Ins("cl16.Ord", "id", Id(b, 0), V(("id", Id(b, 0)), ("memo", "o1"), ("created_at", at))),
            Ins("cl16.Ord", "id", Id(b, 1), V(("id", Id(b, 1)), ("memo", "o2"), ("created_at", at))),
            Ins("cl16.OrdLine", "id", Id(b, 0), V(("id", Id(b, 0)), ("ord_id", Id(b, 1)), ("memo", "l1"))),
            Ins("cl16.Seen", "title", "t", V(("title", "t"), ("seen_identity", Id(b, 0)))),
        ];
        return (Side(truthBase, "2026-11-11T10:00:00.1"), Side(predBase, "2026-11-11T10:00:05.2"));
    }

    [Fact]
    public void Identity_pairing_maps_parent_ids_and_substitutes_child_fk_then_m1_is_perfect()
    {
        var (truth, pred) = Cl16("105", "101");
        var tables = new[] { Ord, OrdLine, Seen };
        var extra = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["cl16.Seen"] = new Dictionary<string, string> { ["seen_identity"] = "cl16.OrdLine" } };
        var a = IdentityPairing.Align(truth, pred, tables, extra);
        Assert.Equal((3L, 3L), (a.Rows, a.Paired));
        Assert.Empty(a.Unpaired);
        var line = a.Truth.Single(r => r.Table == "cl16.OrdLine");
        Assert.Equal(K("id", "101"), line.Key);
        Assert.Equal("102", line.After!["ord_id"]); // 부모 106 → 102
        Assert.Equal("101", a.Truth.Single(r => r.Table == "cl16.Seen").After!["seen_identity"]); // 손 정답 identityRefs(FK 아님)

        var vol = MetricsCalculator.VolatileMap(tables);
        var c = new MetricsCalculator().M1(a.Truth, pred.Select(PredictedChange.Plain).ToList(), vol);
        Assert.Equal((0L, 0L), (c.Fp, c.Fn));
        Assert.Equal(c.RowsUnion, c.RowsExact);

        // 짝 매핑 없이 비교하면 키가 달라 모두 어긋난다
        var raw = new MetricsCalculator().M1(truth, pred.Select(PredictedChange.Plain).ToList(), vol);
        Assert.True(raw.Fn > 0 && raw.Fp > 0);
    }

    [Fact]
    public void Identity_pairing_leaves_rows_with_different_values_unpaired()
    {
        var (truth, pred) = Cl16("105", "101");
        var badPred = pred.Select(r => r.Table == "cl16.Ord" && r.After!["memo"] == "o2" ? r with { After = V(("id", "102"), ("memo", "zz"), ("created_at", "x")) } : r).ToList();
        var a = IdentityPairing.Align(truth, badPred, [Ord, OrdLine, Seen]);
        // Ord o1만 짝. o2는 값이 달라 짝이 없고, o2를 참조하는 OrdLine도 FK 값을 옮길 수 없어 짝이 없다(보수적으로 오류로 남김).
        Assert.Equal((3L, 1L), (a.Rows, a.Paired));
        Assert.Contains(a.Unpaired, u => u.StartsWith("cl16.Ord(", StringComparison.Ordinal));
        Assert.Contains(a.Unpaired, u => u.StartsWith("cl16.OrdLine(", StringComparison.Ordinal));
        Assert.Equal(0, IdentityPairing.Align(truth, pred, [Seen]).Rows); // IDENTITY 테이블이 없으면 그대로
    }

    static HandCase Hand(IReadOnlyList<HandRow> rows, IReadOnlyList<HandEdge>? edges = null, IReadOnlyList<string>? statements = null) =>
        new("CLX", "CLX", "CascadeLab", "x.json", statements ?? ["DELETE FROM cl.A WHERE id = 1;"], "ok", null, new Dictionary<int, string> { [1] = "ok" },
            new Dictionary<string, IReadOnlyList<string>>(), rows)
        { Edges = edges ?? [] };

    static HandRow MatchRow(string table, string memo, string? @ref, Dictionary<string, HandIdentityRef>? refs = null, params string[] volatileCols) =>
        new(table, ChangeKinds.Inserted, HandRow.UnboundPrefix + RowKeys.Canonical(V(("memo", memo))), ["DIRECT"], [], new Dictionary<string, string?>())
        {
            Match = V(("memo", memo)), Identity = ["id"], Ref = @ref, IdentityRefs = refs ?? new Dictionary<string, HandIdentityRef>(), Volatile = volatileCols,
        };

    [Fact]
    public void Hand_identity_rows_bind_to_target_keys_through_match_and_identity_refs()
    {
        var (truth, pred) = Cl16("105", "101");
        var hand = Hand([
            MatchRow("cl16.Ord", "o1", "o1", null, "created_at"),
            MatchRow("cl16.Ord", "o2", "o2"),
            MatchRow("cl16.OrdLine", "l1", "l1", new() { ["ord_id"] = new HandIdentityRef("cl16.Ord", "o2") }),
            new HandRow("cl16.Seen", ChangeKinds.Inserted, K("title", "t"), ["TRIGGER"], [], new Dictionary<string, string?>())
                { IdentityRefs = new Dictionary<string, HandIdentityRef> { ["seen_identity"] = new("cl16.OrdLine", "l1") } },
        ]);
        Assert.Empty(hand.DiffAgainst(truth));
        var (bound, issues) = hand.Bind(pred);
        Assert.Empty(issues);
        Assert.Equal([K("id", "101"), K("id", "102"), K("id", "101"), K("title", "t")], bound.Rows.Select(r => r.Key));

        // 자식 FK가 부모 짝과 다르면 짝이 없다
        var wrong = truth.Select(r => r.Table == "cl16.OrdLine" ? r with { After = V(("id", "105"), ("ord_id", "105"), ("memo", "l1")) } : r).ToList();
        Assert.Contains(hand.DiffAgainst(wrong), d => d.StartsWith("IDENTITY 짝 없음", StringComparison.Ordinal));
        // volatile 열이 NULL이면 차이
        var nullAt = truth.Select(r => r.Table == "cl16.Ord" && r.After!["memo"] == "o1" ? r with { After = V(("id", "105"), ("memo", "o1"), ("created_at", null)) } : r).ToList();
        Assert.Contains(hand.DiffAgainst(nullAt), d => d.StartsWith("volatile 열 값 없음", StringComparison.Ordinal));
    }

    [Fact]
    public void Hand_case_v02_loads_match_identity_multiset_residual_scaling_and_observed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbripple-eval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "CL99-x.json");
            File.WriteAllText(path, """
                {
                  "id": "CL99", "pattern": "CL99", "database": "CascadeLab", "leavesResidue": true,
                  "statements": [ { "index": 1, "sql": "DELETE FROM cl99.P WHERE id <= {N};" } ],
                  "scaling": { "generate": "EXEC cl99.usp_Generate @parents = {N};", "statement": "DELETE FROM cl99.P WHERE id <= {N};", "counts": {} },
                  "expected": {
                    "status": "trigger_blocked", "errorNumber": 3609,
                    "statementStatus": [ { "index": 1, "status": "trigger_blocked" } ],
                    "warnings": ["WARN_TRIGGER_DYNAMIC_SQL"],
                    "changes": [
                      { "table": "cl99.H", "keyKind": "ROW", "keyColumns": [], "inserted": [], "updated": [], "deleted": [],
                        "multisetRemoved": [ { "row": { "a": 1, "b": "x" }, "multiplicity": 2, "causes": ["DIRECT"] } ],
                        "multisetAdded": [ { "row": { "a": 11, "b": "x" }, "multiplicity": 2, "causes": ["DIRECT"] } ] },
                      { "table": "cl99.U", "keyColumns": ["id"], "inserted": [], "deleted": [],
                        "updated": [ { "key": { "id": 1 }, "causes": ["INSTEAD_OF"], "changedColumns": ["v", "touched_at"], "volatile": ["touched_at"] } ] }
                    ],
                    "residualCommitted": [
                      { "table": "cl99.ErrLog", "keyColumns": ["id"], "updated": [], "deleted": [],
                        "inserted": [ { "match": { "msg": "boom" }, "identity": ["id"], "ref": "e1", "causes": ["TRIGGER"], "volatile": ["at"] } ] }
                    ],
                    "edges": [
                      { "statementIndex": 1, "parent": "F:cl99.U:UPDATE", "child": "T:cl99.trg#1", "kind": "FIRES", "name": "cl99.trg", "confidence": "inferred", "observed": false }
                    ]
                  }
                }
                """);
            var hc = HandCase.Load(path);
            Assert.True(hc.LeavesResidue);
            Assert.Equal(["WARN_TRIGGER_DYNAMIC_SQL"], hc.ExpectedWarnings);
            Assert.Equal("DELETE FROM cl99.P WHERE id <= 100;", HandScaling.Fill(hc.Scaling!.Statement, 100));
            Assert.Equal("EXEC cl99.usp_Generate @parents = 7;", HandScaling.Fill(hc.Scaling.Generate!, 7));
            var removed = hc.Rows.Single(r => r.Kind == ChangeKinds.MultisetRemoved);
            Assert.Equal((2L, RowKeys.Canonical(V(("a", "1"), ("b", "x")))), (removed.Multiplicity, removed.Key));
            Assert.Equal(["INSTEAD_OF"], hc.Rows.Single(r => r.Table == "cl99.U").Causes);
            Assert.Equal(["touched_at"], hc.Rows.Single(r => r.Table == "cl99.U").Volatile);
            var err = hc.Rows.Single(r => r.ResidualCommitted);
            Assert.True(err.IsUnbound);
            Assert.Equal(("e1", "id"), (err.Ref, err.Identity[0]));
            var edge = Assert.Single(hc.Edges);
            Assert.False(edge.Observed);
            Assert.Equal("T:cl99.trg", edge.Child); // #1 정규화

            // 오라클 대조: 다중집합 개수, residualCommitted IDENTITY 행 묶기
            IReadOnlyList<OracleRowChange> oracle =
            [
                new("cl99.H", removed.Key, ChangeKinds.MultisetRemoved, 2, V(("a", "1"), ("b", "x")), null, []),
                new("cl99.H", RowKeys.Canonical(V(("a", "11"), ("b", "x"))), ChangeKinds.MultisetAdded, 1, null, V(("a", "11"), ("b", "x")), []),
                new("cl99.U", K("id", "1"), ChangeKinds.Updated, 1, V(("v", "0"), ("touched_at", "a")), V(("v", "1"), ("touched_at", "b")), ["v", "touched_at"]),
                Ins("cl99.ErrLog", "id", "77", V(("id", "77"), ("msg", "boom"), ("at", "2026-11-11T00:00:00"))),
            ];
            Assert.Equal(["개수 다름: MULTISET_ADDED cl99.H(A=11, B=x) 오라클 1 손 2"], hc.DiffAgainst(oracle));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Key_folding_follows_collation_and_is_idempotent()
    {
        Assert.Equal("ABC", KeyFolding.Fold("SQL_Latin1_General_CP1_CI_AS", "abc  "));
        Assert.Equal("abc", KeyFolding.Fold("Latin1_General_BIN2", "abc "));
        Assert.Equal("CAFE", KeyFolding.Fold("Latin1_General_CI_AI", "café"));
        Assert.Null(KeyFolding.Fold("x_CI_AS", null));
        Assert.Equal("5", KeyFolding.Fold(null, "5"));

        var t = new OracleTable("cl", "Code", OracleKeyKind.PK, ["code"], [Col("code", 1, "varchar", collation: "SQL_Latin1_General_CP1_CI_AS"), Col("v", 2)]);
        var f = KeyFolding.For([t]);
        var once = f.Key("cl.Code", K("code", "abc"));
        Assert.Equal(K("code", "ABC"), once);
        Assert.Equal(once, f.Key("cl.Code", once));
        Assert.Equal(K("id", "x"), f.Key("cl.Other", K("id", "x")));
        Assert.True(KeyFolding.For([Ord]).IsEmpty);

        // 대소문자만 바꾼 키 UPDATE: 제품 UPDATED 키 "Abc"와 오라클 접힌 키가 같은 행이 된다(인계 6)
        var product = new OracleRowChange("cl.Code", K("code", "Abc"), ChangeKinds.Updated, 1, V(("code", "abc")), V(("code", "Abc")), ["code"]);
        Assert.Equal(K("code", "ABC"), f.Apply(product).Key);
    }

    [Fact]
    public void Heap_keys_drop_unobservable_and_computed_columns_and_merge_counts()
    {
        var heap = new OracleTable("cl", "H", OracleKeyKind.ROW, [],
            [Col("a", 1), Col("note", 2, "text"), Col("c", 3, computed: true), Col("g", 4, "geography"), Col("a.b", 5)]);
        var cols = HeapKeys.ObservableColumns(heap);
        Assert.Equal(new[] { "a", "g" }.Order(), cols.Order());

        // 훅(모든 열)은 text 열만 바뀐 행을 제거 1 + 추가 1로 보지만, 관측 가능 열로 묶으면 상쇄된다. 생성 속성 g@srid는 남는다.
        var before = V(("a", "1"), ("note", "x"), ("g", "POINT (1 2)"), ("g@srid", "4326"), ("a.b", "9"));
        var after = V(("a", "1"), ("note", "y"), ("g", "POINT (1 2)"), ("g@srid", "4326"), ("a.b", "9"));
        IReadOnlyList<OracleRowChange> hook =
        [
            new("cl.H", RowKeys.Canonical(before), ChangeKinds.MultisetRemoved, 1, before, null, []),
            new("cl.H", RowKeys.Canonical(after), ChangeKinds.MultisetAdded, 1, null, after, []),
            new("cl.X", K("id", "1"), ChangeKinds.Deleted, 1, V(("id", "1")), null, []),
        ];
        var rekeyed = HeapKeys.Rekey(hook, [heap]);
        Assert.Equal(["cl.X"], rekeyed.Select(r => r.Table));

        var added = HeapKeys.Rekey([new("cl.H", "k", ChangeKinds.MultisetAdded, 2, null, after, [])], [heap]).Single();
        Assert.Equal(RowKeys.Canonical(V(("a", "1"), ("g", "POINT (1 2)"), ("g@srid", "4326"))), added.Key);
        Assert.Equal(2, added.Multiplicity);
    }

    static Firing F(long seq, int stmt, int depth, string table, ChangeOperation op) => new()
    {
        Id = ContractIds.Firing(seq), CaptureSeq = seq, StatementIndex = stmt, Depth = depth, Table = table, Operation = op,
        RowCount = 1, ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
    };

    static GraphEdge E(int n, string s, string t, EdgeKind k, Confidence c, OrderGuarantee o = OrderGuarantee.Guaranteed, string? fk = null) => new()
    {
        Id = ContractIds.Edge(n), Source = s, Target = t, Kind = k, Confidence = c, OrderGuarantee = o, Fk = fk,
    };

    static FiringNode FN(Firing f) => new() { Id = f.Id, FiringId = f.Id, Table = f.Table, Operation = f.Operation, RowCount = f.RowCount, Depth = f.Depth, CaptureSeq = f.CaptureSeq };

    static PreviewResult Result(IReadOnlyList<Firing> firings, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, PreviewStatus status = PreviewStatus.Ok) => new()
    {
        PreviewId = "pv", Status = status, CreatedAt = DateTimeOffset.UnixEpoch, SqlHash = "sha256:0", Baseline = null!,
        Capture = new CaptureInfo { ImageCount = 4, Cap = 100, Truncated = false, ManifestVersion = "m" },
        TimingsMs = new TimingsMs { Analyze = 1, Execute = 10, ReadLog = 2, Reread = 1, NetDiff = 3, Resolve = 1, Serialize = 1 },
        Firings = firings,
        Graph = new GraphDto { Nodes = nodes, Edges = edges },
    };

    // CL08형: 같은 테이블 UPDATE가 자기 트리거로 두 번(깊이 1, 2), 그리고 FK 자식 두 갈래(CL05형 형제).
    static PreviewResult SelfAndSiblings(long seqA, long seqB)
    {
        var a1 = F(10, 1, 2, "cl.Acct", ChangeOperation.Update);
        var a0 = F(11, 1, 1, "cl.Acct", ChangeOperation.Update);
        var c1 = F(seqA, 2, 2, "cl.C1", ChangeOperation.Delete);
        var c2 = F(seqB, 2, 2, "cl.C2", ChangeOperation.Delete);
        var p = F(30, 2, 1, "cl.P", ChangeOperation.Delete);
        var firings = new[] { a1, a0, c1, c2, p };
        GraphNode[] nodes =
        [
            new StatementNode { Id = "st-1", StatementIndex = 1, Label = "u", Status = StatementStatus.Ok },
            FN(a0), FN(a1),
            new TriggerNode { Id = "t-1", Trigger = "cl.trg_Acct", ParentFiringId = a0.Id, StatementIndex = 1, Observed = true },
            new TriggerNode { Id = "t-2", Trigger = "cl.trg_Acct", ParentFiringId = a1.Id, StatementIndex = 1, Observed = false },
            new StatementNode { Id = "st-2", StatementIndex = 2, Label = "d", Status = StatementStatus.Ok },
            FN(p), FN(c1), FN(c2),
            new ResidualNode { Id = "r-1", ResidualId = "r-1", Table = "cl.Acct", Category = ResidualCategory.SameTableTrigger },
        ];
        GraphEdge[] edges =
        [
            E(1, "st-1", a0.Id, EdgeKind.Direct, Confidence.Certain),
            E(2, a0.Id, "t-1", EdgeKind.Fires, Confidence.Inferred),
            E(3, "t-1", a1.Id, EdgeKind.Writes, Confidence.Inferred),
            E(4, a1.Id, "t-2", EdgeKind.Fires, Confidence.Inferred),
            E(5, "t-1", "r-1", EdgeKind.Residual, Confidence.Ambiguous, OrderGuarantee.ObservedOnly),
            E(6, "st-2", p.Id, EdgeKind.Direct, Confidence.Certain),
            E(7, p.Id, c1.Id, EdgeKind.Fk, Confidence.Certain, OrderGuarantee.ObservedOnly, "FK_C1_P"),
            E(8, p.Id, c2.Id, EdgeKind.Fk, Confidence.Certain, OrderGuarantee.ObservedOnly, "FK_C2_P"),
        ];
        return Result(firings, nodes, edges);
    }

    [Fact]
    public void Graph_edges_use_answer_format_refs_with_causal_k_numbering()
    {
        var keys = GraphEdges.FromResult(SelfAndSiblings(20, 21)).Select(e => e.Key).ToList();
        Assert.Equal(new[]
        {
            EdgeKeys.Compose(1, "S", "F:cl.Acct:UPDATE#1", "DIRECT", null),
            EdgeKeys.Compose(1, "F:cl.Acct:UPDATE#1", "T:cl.trg_Acct#1", "FIRES", "cl.trg_Acct"),
            EdgeKeys.Compose(1, "T:cl.trg_Acct#1", "F:cl.Acct:UPDATE#2", "WRITES", "cl.trg_Acct"),
            EdgeKeys.Compose(1, "F:cl.Acct:UPDATE#2", "T:cl.trg_Acct#2", "FIRES", "cl.trg_Acct"),
            EdgeKeys.Compose(1, "T:cl.trg_Acct#1", "R:cl.Acct", "RESIDUAL", "cl.trg_Acct"),
            EdgeKeys.Compose(2, "S", "F:cl.P:DELETE", "DIRECT", null),
            EdgeKeys.Compose(2, "F:cl.P:DELETE", "F:cl.C1:DELETE", "FK", "FK_C1_P"),
            EdgeKeys.Compose(2, "F:cl.P:DELETE", "F:cl.C2:DELETE", "FK", "FK_C2_P"),
        }, keys);
        var fires2 = GraphEdges.FromResult(SelfAndSiblings(20, 21)).Single(e => e.Child == "T:cl.trg_Acct#2");
        Assert.False(fires2.Observed);
        Assert.Empty(GraphEdges.FromResult(null));
    }

    [Fact]
    public void M3_counts_precision_recall_by_confidence_ambiguous_hits_and_overconfidence()
    {
        HandEdge H(string p, string c, string k, string? n, string conf, bool observed = true) => new(1, p, c, k, n, conf) { Observed = observed };
        IReadOnlyList<HandEdge> truth =
        [
            H("S", "F:a:DELETE", "DIRECT", null, "certain"),
            H("F:a:DELETE", "F:b:DELETE", "FK", "FK_b", "certain"),
            H("F:a:DELETE", "T:t1", "FIRES", "t1", "inferred", observed: false),
            H("T:t1", "F:c:UPDATE", "WRITES", "t1", "ambiguous"),
            H("F:b:DELETE", "F:d:DELETE", "FK", "FK_d", "inferred"),
        ];
        PredictedEdge P(string p, string c, string k, string? n, string conf, bool? observed = null) => new(1, p, c, k, n, conf, "guaranteed", observed);
        IReadOnlyList<PredictedEdge> pred =
        [
            P("S", "F:a:DELETE", "DIRECT", null, "certain"),
            P("F:a:DELETE", "F:b:DELETE", "FK", "FK_b", "certain"),
            P("F:a:DELETE", "T:t1", "FIRES", "t1", "inferred", observed: true),   // observed 불일치
            P("T:t1", "F:c:UPDATE", "WRITES", "t1", "ambiguous"),                  // 후보 1(정답)
            P("T:t2", "F:c:UPDATE", "WRITES", "t2", "ambiguous"),                  // 후보 2
            P("T:t9", "F:z:UPDATE", "WRITES", "t9", "ambiguous"),                  // 정답 없는 후보 묶음
            P("F:b:DELETE", "F:d:DELETE", "FK", "FK_d", "certain"),                // 과신(정답 inferred)
            P("F:a:DELETE", "F:x:DELETE", "FK", "FK_x", "certain"),                // 과신(정답에 없음)
            P("S", "F:a:DELETE", "DIRECT", null, "inferred"),                      // 같은 키 중복: 강한 쪽만
        ];
        var m = AttributionMetrics.Compute(truth, pred, null).ToDictionary(c => c.Confidence);
        Assert.Equal(AttributionMetrics.Scopes, m.Keys);
        Assert.Equal((4L, 3L, 2L, 2L), (m["certain"].PredictedEdges, m["certain"].PredictedMatched, m["certain"].TruthEdges, m["certain"].TruthFound));
        Assert.Equal((1L, 1L), (m["certain"].CertainWrong!.Value, m["certain"].CertainOverclaim!.Value));
        Assert.Equal("2", m["certain"].CsvCells()[MetricsCsv.M3Columns.ToList().IndexOf("certain_overconfident") - 5]);
        Assert.Equal((3L, 1L), (m["ambiguous"].PredictedEdges, m["ambiguous"].PredictedMatched));
        Assert.Equal((2L, 1L), (m["ambiguous"].AmbiguousGroups!.Value, m["ambiguous"].AmbiguousGroupsHit!.Value));
        Assert.Equal((2L, 2L, 1L), (m["inferred"].TruthEdges, m["inferred"].TruthFound, m["inferred"].ConfidenceMatch)); // FK_d는 certain으로 예측
        Assert.Equal((8L, 5L, 5L, 5L), (m["all"].PredictedEdges, m["all"].PredictedMatched, m["all"].TruthEdges, m["all"].TruthFound));
        Assert.Equal(1L, m["all"].ObservedMismatch);
        Assert.Null(m["inferred"].AmbiguousGroups);
        Assert.All(m.Values, c => Assert.Equal(MetricsCsv.M3Columns.Count - 5, c.CsvCells().Count));
    }

    [Fact]
    public void Baselines_filter_direct_targets_count_manual_tables_and_parse_systems()
    {
        var hand = Hand([new HandRow("cl.B", ChangeKinds.Deleted, K("id", "1"), ["FK_CASCADE"], [], new Dictionary<string, string?>())],
            [new HandEdge(1, "S", "F:cl.A:DELETE", "DIRECT", null, "certain"), new HandEdge(1, "F:cl.A:DELETE", "F:cl.B:DELETE", "FK", "FK_B", "certain")],
            ["DELETE FROM [cl].[A] WHERE id = 1;", "update TOP (5) cl.Z set v = 1", "UPDATE a SET v = 1 FROM cl.Q a"]);
        Assert.Equal(new[] { "cl.A", "cl.Z" }.Order(), Baselines.DirectTargets(hand).Order());
        IReadOnlyList<OracleRowChange> oracle =
        [
            new("cl.A", K("id", "1"), ChangeKinds.Deleted, 1, V(("id", "1")), null, []),
            new("cl.B", K("id", "1"), ChangeKinds.Deleted, 1, V(("id", "1")), null, []),
            new("cl.C", K("id", "1"), ChangeKinds.Deleted, 1, V(("id", "1")), null, []),
        ];
        Assert.Equal(["cl.A"], Baselines.B0Changes(hand, oracle).Select(r => r.Table));
        Assert.Equal(2, Baselines.ManualSelectTables(hand, oracle));
        Assert.Equal([EdgeKeys.Compose(1, "S", "F:cl.A:DELETE", "DIRECT", null)], Baselines.B0Edges(hand).Select(e => e.Key));
        Assert.Equal(["preview", "B0", "B2"], Baselines.ParseSystems("preview, b0,B2,B0"));
        Assert.Equal(["preview"], Baselines.ParseSystems(null));
        Assert.Throws<ArgumentException>(() => Baselines.ParseSystems("preview,B1"));
    }

    [Fact]
    public void Case_scorer_scores_baselines_from_oracle_with_same_columns()
    {
        var hand = Hand([
            new HandRow("cl.A", ChangeKinds.Deleted, K("id", "1"), ["DIRECT"], [], new Dictionary<string, string?>()),
            new HandRow("cl.B", ChangeKinds.Deleted, K("id", "1"), ["FK_CASCADE"], [], new Dictionary<string, string?>()),
        ], [new HandEdge(1, "S", "F:cl.A:DELETE", "DIRECT", null, "certain"), new HandEdge(1, "F:cl.A:DELETE", "F:cl.B:DELETE", "FK", "FK_B", "certain")]);
        IReadOnlyList<OracleRowChange> changes =
        [
            new("cl.A", K("id", "1"), ChangeKinds.Deleted, 1, V(("id", "1")), null, []),
            new("cl.B", K("id", "1"), ChangeKinds.Deleted, 1, V(("id", "1")), null, []),
        ];
        var oracle = new OracleResult("db", [], changes, [new OracleStatement(1, "ok", 1, [])], [], false, 3, "d", true, []);
        var suite = new SuiteAccumulator();
        var scorer = new CaseScorer(new MetricsCalculator());
        string Col(IReadOnlyList<string?> row, IReadOnlyList<string> cols, string name) => row[cols.ToList().IndexOf(name)]!;

        var b2 = scorer.Score(new CaseRun { RunId = "r", System = "B2", Hand = hand, Oracle = oracle }, suite);
        Assert.Equal("ok", Col(b2.CaseRow, MetricsCsv.CaseColumns, "status_actual"));
        Assert.Equal("", Col(b2.CaseRow, MetricsCsv.CaseColumns, "analyze_ms"));
        Assert.Equal("1.0000", Col(b2.M1Rows[0], MetricsCsv.M1Columns, "f1"));
        Assert.Equal("", Col(b2.M1Rows[1], MetricsCsv.M1Columns, "f1"));
        var b2all = b2.M3Rows.Single(r => r[4] == "all");
        Assert.Equal(("0", "0.0000", "", "1"), (Col(b2all, MetricsCsv.M3Columns, "predicted_edges"), Col(b2all, MetricsCsv.M3Columns, "recall"),
            Col(b2all, MetricsCsv.M3Columns, "precision"), Col(b2all, MetricsCsv.M3Columns, "manual_select_tables")));
        Assert.Null(b2.Signature);

        var b0 = scorer.Score(new CaseRun { RunId = "r", System = "B0", Hand = hand, Oracle = oracle }, suite);
        Assert.Equal("0.5000", Col(b0.M1Rows[0], MetricsCsv.M1Columns, "recall"));
        Assert.Equal("0.0000", Col(b0.M2Rows.Single(r => r[4] == "FK_CASCADE"), MetricsCsv.M2Columns, "recall"));
        Assert.Equal("0.5000", Col(b0.M3Rows.Single(r => r[4] == "all"), MetricsCsv.M3Columns, "recall"));

        var rows = suite.Rows("r").ToList();
        Assert.Contains(rows, r => r[1] == "B2" && r[2] == "M3.manual_select_tables" && r[6] == "1.0000");
        Assert.Contains(rows, r => r[1] == "B0" && r[2] == "M2.recall" && r[3] == "FK_CASCADE" && r[6] == "0.0000");
    }

    [Fact]
    public void M4_canonical_signature_ignores_sibling_order_but_observed_only_signature_does_not()
    {
        var x = OrderStability.Signature(SelfAndSiblings(20, 21), "error");
        var y = OrderStability.Signature(SelfAndSiblings(21, 20), "error"); // 형제 C1·C2의 관측 순서만 바뀜
        Assert.Equal(x.Canonical, y.Canonical);
        Assert.NotEqual(x.ObservedOnly, y.ObservedOnly);
        Assert.Equal(3, x.ObservedOnlyEdges);
        Assert.Equal("ok", x.Status);
        var missing = OrderStability.Signature(null, "rejected");
        Assert.Equal(("rejected", 0), (missing.Status, missing.ObservedOnlyEdges));

        var acc = new M4Accumulator();
        acc.Add("preview", "CL05", x);
        acc.Add("preview", "CL05", x);
        acc.Add("preview", "CL05", x);
        acc.Add("preview", "CL05", y);
        acc.Add("preview", "CL01", missing);
        var rows = acc.Rows("r").ToList();
        Assert.All(rows, r => Assert.Equal(MetricsCsv.M4Columns.Count, r.Count));
        string Col(IReadOnlyList<string> row, string name) => row[MetricsCsv.M4Columns.ToList().IndexOf(name)];
        var cl05 = rows.Single(r => r[2] == "CL05");
        Assert.Equal(("4", "1", "true", "1.0000", "2", "0.2500"), (Col(cl05, "repeats"), Col(cl05, "canonical_distinct"), Col(cl05, "canonical_stable"),
            Col(cl05, "canonical_mode_share"), Col(cl05, "observed_only_distinct"), Col(cl05, "observed_only_variation_rate")));
        var suite = acc.SuiteRows("r").ToList();
        Assert.Equal(["2", "2", "1.0000"], suite.Single(r => r[2] == "M4.canonical_stable_rate").Skip(4).Take(3));
        Assert.Equal(["1", "5", "0.2000"], suite.Single(r => r[2] == "M4.observed_only_variation_rate").Skip(4).Take(3));
        Assert.Equal(["0", "5", "0.0000"], suite.Single(r => r[2] == "M4.error_run_rate").Skip(4).Take(3));
        var cl01 = rows.Single(r => r[2] == "CL01"); // 거부는 결정적이라 유효 반복으로 센다
        Assert.Equal(("1", "0", "1", "true"), (Col(cl01, "repeats"), Col(cl01, "error_runs"), Col(cl01, "valid_runs"), Col(cl01, "canonical_stable")));
    }

    [Fact]
    public void M4_excludes_error_runs_from_stability_and_leaves_cells_blank_when_all_runs_failed()
    {
        var ok = OrderStability.Signature(SelfAndSiblings(20, 21), "error");
        var error = OrderStability.Signature(null, M4Accumulator.ErrorStatus);
        var acc = new M4Accumulator();
        for (var i = 0; i < 20; i++) acc.Add("preview", "CLE", error); // 20회 모두 예외: 예전에는 "안정"으로 나왔다
        acc.Add("preview", "CLM", ok);
        acc.Add("preview", "CLM", error);
        acc.Add("preview", "CLM", ok);
        var rows = acc.Rows("r").ToList();
        Assert.All(rows, r => Assert.Equal(MetricsCsv.M4Columns.Count, r.Count));
        string Col(IReadOnlyList<string> row, string name) => row[MetricsCsv.M4Columns.ToList().IndexOf(name)];

        var cle = rows.Single(r => r[2] == "CLE");
        Assert.Equal(("20", "20", "0", "1"), (Col(cle, "repeats"), Col(cle, "error_runs"), Col(cle, "valid_runs"), Col(cle, "status_distinct")));
        Assert.All(new[] { "canonical_distinct", "canonical_stable", "canonical_mode_share", "observed_only_edges", "observed_only_distinct",
            "observed_only_variation_rate", "canonical_mode_signature" }, c => Assert.Equal("", Col(cle, c)));

        var clm = rows.Single(r => r[2] == "CLM");
        Assert.Equal(("3", "1", "2", "2"), (Col(clm, "repeats"), Col(clm, "error_runs"), Col(clm, "valid_runs"), Col(clm, "status_distinct")));
        Assert.Equal(("1", "true", "1.0000", "0.0000"), (Col(clm, "canonical_distinct"), Col(clm, "canonical_stable"), Col(clm, "canonical_mode_share"), Col(clm, "observed_only_variation_rate")));
        Assert.Equal(ok.Canonical, Col(clm, "canonical_mode_signature"));

        var suite = acc.SuiteRows("r").ToList();
        Assert.Equal(["1", "1", "1.0000", "2"], suite.Single(r => r[2] == "M4.canonical_stable_rate").Skip(4)); // 분모: 유효 반복이 있는 사례 1개, cases 2
        Assert.Equal(["0", "2", "0.0000"], suite.Single(r => r[2] == "M4.observed_only_variation_rate").Skip(4).Take(3));
        Assert.Equal(["21", "23", "0.9130"], suite.Single(r => r[2] == "M4.error_run_rate").Skip(4).Take(3));

        // 모든 사례가 예외면 안정 사례 비율은 빈 칸(분모 0)
        var allError = new M4Accumulator();
        allError.Add("preview", "CLE", error);
        Assert.Equal(["0", "0", ""], allError.SuiteRows("r").Single(r => r[2] == "M4.canonical_stable_rate").Skip(4).Take(3));
    }

    [Fact]
    public void Report_has_metric_definition_section_with_known_definition_gaps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbripple-eval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var md = ReportBuilder.Build(dir);
            Assert.Contains("## 지표 정의", md);
            Assert.Contains("구성상 1.0", md);
            Assert.Contains("테이블 단위", md);
            Assert.Contains("DELETE TOP (n) FROM s.t", md);
            Assert.Contains("EXECUTE AS/REVERT", md);
            Assert.Contains("예외(error) 반복", md);
            Assert.True(md.IndexOf("## 지표 정의", StringComparison.Ordinal) < md.IndexOf("## M", StringComparison.Ordinal) || !md.Contains("## M", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void M5_rows_use_interpolated_percentiles_without_warmup_and_leave_browser_columns_blank()
    {
        Assert.Equal(3.0, ResponseTime.Percentile([5, 1, 3], 0.5));
        Assert.Equal(4.8, ResponseTime.Percentile([1, 2, 3, 4, 5], 0.95)!.Value, 6);
        Assert.Null(ResponseTime.Percentile([], 0.5));

        var samples = new List<M5Sample>
        {
            ResponseTime.FromResult("CL20", "DELETE", 100, 0, true, Result([], [], []) with { TimingsMs = new TimingsMs { Execute = 999 } }, "error", 999),
        };
        for (var i = 1; i <= 5; i++)
            samples.Add(ResponseTime.FromResult("CL20", "DELETE", 100, i, false,
                Result([], [], []) with { TimingsMs = new TimingsMs { Analyze = 1, Execute = 10 * i } }, "error", 5 * i));
        samples.Add(ResponseTime.FromResult("CL20", "DELETE", 1, 1, false, null, "error", 2));
        var rows = ResponseTime.Rows("m5", samples).ToList();
        Assert.All(rows, r => Assert.Equal(MetricsCsv.M5Columns.Count, r.Count));
        string Col(IReadOnlyList<string> row, string name) => row[MetricsCsv.M5Columns.ToList().IndexOf(name)];
        Assert.Equal(["1", "100"], rows.Select(r => Col(r, "n")));
        var big = rows[1];
        Assert.Equal(("5", "5", "ok"), (Col(big, "samples"), Col(big, "ok_samples"), Col(big, "statuses")));
        Assert.Equal(("30.0", "48.0", "15.0"), (Col(big, "execute_p50"), Col(big, "execute_p95"), Col(big, "oracle_execute_p50")));
        Assert.Equal("31.0", Col(big, "total_p50"));
        Assert.Equal("2.0000", Col(big, "overhead_ratio_p50"));
        Assert.Equal(("", ""), (Col(big, "browser_first_graph_ms"), Col(big, "browser_first_grid_ms")));
        var failed = rows[0];
        Assert.Equal(("1", "0", "error", "", "2.0"), (Col(failed, "samples"), Col(failed, "ok_samples"), Col(failed, "statuses"), Col(failed, "execute_p50"), Col(failed, "oracle_execute_p50")));
    }

    [Fact]
    public void Csv_reader_round_trips_quotes_commas_and_newlines()
    {
        var text = "a,b,c\r\n1,\"x,\"\"y\"\"\n한글\",\r\n\r\n2,,z\r\n";
        var recs = MetricsCsv.Parse(text);
        Assert.Equal(["1", "x,\"y\"\n한글", ""], recs[1]);
        var dir = Path.Combine(Path.GetTempPath(), "dbripple-eval-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "t.csv");
            MetricsCsv.Append(path, ["a", "b"], ["1", "p,\"q\"\r\nr"]);
            MetricsCsv.Append(path, ["a", "b"], ["2", null]);
            var t = MetricsCsv.Read(path)!;
            Assert.Equal(["a", "b"], t.Header);
            Assert.Equal("p,\"q\"\r\nr", t.Rows[0]["B"]); // 인용 안의 CR·LF는 그대로
            Assert.Equal("", t.Rows[1]["b"]);
            Assert.Null(MetricsCsv.Read(Path.Combine(dir, "none.csv")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Report_is_built_only_from_csv_files_in_the_run_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbripple-eval-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var hand = Hand([new HandRow("cl.A", ChangeKinds.Deleted, K("id", "1"), ["DIRECT"], [], new Dictionary<string, string?>())],
                [new HandEdge(1, "S", "F:cl.A:DELETE", "DIRECT", null, "certain")]);
            var oracle = new OracleResult("db", [], [new("cl.A", K("id", "1"), ChangeKinds.Deleted, 1, V(("id", "1")), null, [])],
                [new OracleStatement(1, "ok", 1, [])], [], false, 3, "d", true, []);
            var suite = new SuiteAccumulator();
            var scorer = new CaseScorer(new MetricsCalculator());
            foreach (var system in new[] { "preview", "B2" })
            {
                var s = scorer.Score(new CaseRun { RunId = "run9", System = system, Hand = hand, Oracle = oracle }, suite);
                MetricsCsv.Append(Path.Combine(dir, "cases.csv"), MetricsCsv.CaseColumns, s.CaseRow);
            }
            foreach (var r in suite.Rows("run9")) MetricsCsv.Append(Path.Combine(dir, "metrics.csv"), MetricsCsv.SuiteColumns, r);
            var md = ReportBuilder.Build(dir);
            Assert.Contains("# 평가 보고서 — run9", md);
            Assert.Contains("`m5.csv`: 없음", md);
            Assert.Contains("## M1 일치율", md);
            Assert.Contains("| B2 | total | 1.0000 (1/1) |", md);
            Assert.Contains("## M3 귀속 정확도", md);
            Assert.Contains("| CLX | 1 | ok | error | false |", md); // preview: Outcome 없음 → error
            var path = ReportBuilder.Write(dir);
            Assert.Equal(Path.Combine(dir, "report.md"), path);
            Assert.False(File.ReadAllBytes(path) is [0xEF, 0xBB, 0xBF, ..]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // 크기별로 나눠 돈 M5 실행 폴더 여럿을 한 표로 잇는다(2026-11-12, 10만 행만 따로 실행). 값은 각 m5.csv 그대로, 순서는 (사례, N).
    [Fact]
    public void Report_joins_m5_rows_from_several_run_folders_in_case_and_size_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbripple-eval-tests", Guid.NewGuid().ToString("N"));
        string M5Dir(string runId, long n, long totalExecute)
        {
            var d = Path.Combine(root, runId);
            var samples = new List<M5Sample> { new("CL20", "DELETE", n, 1, false, "ok", new TimingsMs { Execute = totalExecute }, 1) };
            foreach (var row in ResponseTime.Rows(runId, samples)) MetricsCsv.Append(Path.Combine(d, "m5.csv"), MetricsCsv.M5Columns, row);
            return d;
        }
        try
        {
            var run = Path.Combine(root, "eval");
            Directory.CreateDirectory(run);
            var big = M5Dir("m5-big", 100000, 900);
            var small = M5Dir("m5-small", 10, 7);
            var md = ReportBuilder.Build(run, [big, small]);
            Assert.Contains("# 평가 보고서 — m5-big, m5-small", md);
            Assert.Contains("`m5.csv`: 2행 (`../m5-big, ../m5-small`)", md);
            var i10 = md.IndexOf("| CL20 | DELETE | 10 | 1 | ok | false | 7.0 |", StringComparison.Ordinal);
            var i100k = md.IndexOf("| CL20 | DELETE | 100000 | 1 | ok | false | 900.0 |", StringComparison.Ordinal);
            Assert.True(i10 >= 0 && i100k > i10, md);
            Assert.Equal(ReportBuilder.Build(run, big), ReportBuilder.Build(run, [big])); // 한 폴더는 기존 호출과 같다
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
