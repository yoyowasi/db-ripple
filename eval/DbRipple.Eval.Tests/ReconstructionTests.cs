using System.Globalization;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Eval;

namespace DbRipple.Eval.Tests;

/// <summary>E_cap 예측(행 변경만으로 접은 순 변경)의 하네스 독립 구현 검사(가짜 묶음·행 변경, DB 없음).</summary>
public class ReconstructionTests
{
    static Firing F(long seq, int stmt, int depth, string table, ChangeOperation op) => new()
    {
        Id = ContractIds.Firing(seq), CaptureSeq = seq, StatementIndex = stmt, Depth = depth, Table = table, Operation = op,
        RowCount = 1, ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
    };

    static Dictionary<string, string?> V(params (string C, string? V)[] p) => p.ToDictionary(x => x.C, x => x.V);

    static int n;
    static RowChange RC(long seq, ChangeOperation op, string keyValue, Dictionary<string, string?>? before, Dictionary<string, string?>? after, KeyKind kind = KeyKind.PK) => new()
    {
        Id = ContractIds.RowChange(seq, Interlocked.Increment(ref n)), FiringId = ContractIds.Firing(seq), Operation = op,
        Key = kind == KeyKind.ROW ? new RowKey { Kind = KeyKind.ROW } : new RowKey { Kind = kind, Columns = ["id"], Values = [keyValue] },
        Before = before, After = after, Pairing = Pairing.ByPk, FinalAfterSource = FinalAfterSource.Audit,
    };

    static string K(string v) => RowKeys.Canonical([new KeyValuePair<string, string?>("id", v)]);

    [Fact]
    public void Preorder_is_built_from_postorder_capture_and_depth()
    {
        // 후위 순서(captureSeq) a(d3) b(d2) c(d2) d(d1) e(d1) → 트리 d{b{a}, c}, e → 전위 d b a c e
        var fs = new[] { F(1, 1, 3, "t.a", ChangeOperation.Update), F(2, 1, 2, "t.b", ChangeOperation.Update), F(3, 1, 2, "t.c", ChangeOperation.Update),
                         F(4, 1, 1, "t.d", ChangeOperation.Update), F(5, 1, 1, "t.e", ChangeOperation.Update), F(6, 2, 1, "t.f", ChangeOperation.Update) };
        var idx = Reconstruction.PreorderIndex(fs);
        Assert.Equal(["f-4", "f-2", "f-1", "f-3", "f-5"], idx.Where(x => x.Value.Statement == 1).OrderBy(x => x.Value.Preorder).Select(x => x.Key));
        Assert.Equal((2, 0), idx["f-6"]);
    }

    [Fact]
    public void Nested_self_update_takes_first_before_and_preorder_last_after()
    {
        // CL08a 모양: 바깥 UPDATE(f-2, 깊이 1)가 먼저 기록되지 않고, 트리거 안 같은 테이블 UPDATE(f-1, 깊이 2)가 먼저 기록된다(H1).
        var fs = new[] { F(1, 1, 2, "cl08.Acct", ChangeOperation.Update), F(2, 1, 1, "cl08.Acct", ChangeOperation.Update) };
        var rcs = new[]
        {
            RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("bal", "110"), ("touched", "0")), V(("id", "1"), ("bal", "110"), ("touched", "1"))),
            RC(2, ChangeOperation.Update, "1", V(("id", "1"), ("bal", "100"), ("touched", "0")), V(("id", "1"), ("bal", "110"), ("touched", "0"))),
        };
        var r = Assert.Single(Reconstruction.Fold(fs, rcs));
        Assert.Equal((ChangeKinds.Updated, K("1")), (r.Change, r.Key));
        Assert.Equal(["bal", "touched"], r.ChangedColumns);
        Assert.Equal(("100", "0"), (r.Before!["bal"], r.Before["touched"]));
        Assert.Equal(("110", "1"), (r.After!["bal"], r.After["touched"]));
    }

    [Fact]
    public void Insert_then_delete_cancels_and_reverted_columns_drop_out()
    {
        var fs = new[] { F(1, 1, 1, "t.a", ChangeOperation.Insert), F(2, 2, 1, "t.a", ChangeOperation.Delete),
                         F(3, 1, 1, "t.b", ChangeOperation.Update), F(4, 2, 1, "t.b", ChangeOperation.Update) };
        var rcs = new[]
        {
            RC(1, ChangeOperation.Insert, "5", null, V(("id", "5"), ("v", "x"))),
            RC(2, ChangeOperation.Delete, "5", V(("id", "5"), ("v", "x")), null),
            RC(3, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0"), ("w", "0")), V(("id", "1"), ("v", "1"), ("w", "0"))),
            RC(4, ChangeOperation.Update, "1", V(("id", "1"), ("v", "1"), ("w", "0")), V(("id", "1"), ("v", "0"), ("w", "2"))),
        };
        var r = Assert.Single(Reconstruction.Fold(fs, rcs));
        Assert.Equal("t.b", r.Table);
        Assert.Equal(["w"], r.ChangedColumns);
    }

    [Fact]
    public void Pk_change_becomes_delete_old_and_insert_new_key()
    {
        var fs = new[] { F(1, 1, 1, "cl04.P", ChangeOperation.Update) };
        var rcs = new[] { RC(1, ChangeOperation.Update, "101", V(("id", "1"), ("n", "a")), V(("id", "101"), ("n", "a"))) };
        var res = Reconstruction.Fold(fs, rcs).ToDictionary(x => x.Key);
        Assert.Equal(ChangeKinds.Deleted, res[K("1")].Change);
        Assert.Equal(ChangeKinds.Inserted, res[K("101")].Change);
        Assert.Equal(2, res.Count);
    }

    [Fact]
    public void Heap_rows_fold_to_multiset_counts_and_keys_mode_delete_keeps_key()
    {
        var fs = new[] { F(1, 1, 1, "t.h", ChangeOperation.Delete), F(2, 1, 1, "t.h", ChangeOperation.Insert), F(3, 1, 1, "t.k", ChangeOperation.Delete),
                         F(4, 1, 1, "t.u", ChangeOperation.Update) };
        var rcs = new[]
        {
            RC(1, ChangeOperation.Delete, "", V(("a", "1"), ("b", "1")), null, KeyKind.ROW),
            RC(2, ChangeOperation.Insert, "", null, V(("a", "3"), ("b", null)), KeyKind.ROW),
            RC(2, ChangeOperation.Insert, "", null, V(("a", "3"), ("b", null)), KeyKind.ROW),
            RC(3, ChangeOperation.Delete, "9", null, null),      // KEYS 모드 DELETE
            RC(4, ChangeOperation.Update, "7", null, null),      // KEYS 모드 UPDATE: 값을 몰라 변경을 만들지 않음
        };
        var res = Reconstruction.Fold(fs, rcs);
        Assert.Equal(1, res.Single(x => x.Change == ChangeKinds.MultisetRemoved).Multiplicity);
        Assert.Equal(2, res.Single(x => x.Change == ChangeKinds.MultisetAdded).Multiplicity);
        Assert.Equal(K("9"), res.Single(x => x.Table == "t.k" && x.Change == ChangeKinds.Deleted).Key);
        Assert.DoesNotContain(res, x => x.Table == "t.u");
        Assert.Equal(3, res.Count);
    }

    [Fact]
    public void Cap_compares_reconstruction_to_hook_and_counts_residual_only_cells_as_fn()
    {
        // 행 변경은 v만 바꿨고, 같은 테이블 트리거가 바꾼 rev는 잔차(NetChanges)로만 나온다
        var fs = new[] { F(1, 1, 1, "cl.S", ChangeOperation.Update) };
        var rcs = new[] { RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0"), ("rev", "8")), V(("id", "1"), ("v", "1"), ("rev", "8"))) };
        var hook = new OracleRowChange("cl.S", K("1"), ChangeKinds.Updated, 1, V(("v", "0"), ("rev", "8")), V(("v", "1"), ("rev", "9")), ["v", "rev"]);
        var net = new NetChange
        {
            Table = "cl.S", Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = ["1"] }, Change = NetChangeKind.Updated, Multiplicity = 1,
            After = V(("v", "1"), ("rev", "9")), ChangedColumns = ["v", "rev"], ExplainedBy = new ExplainedBy { FiringIds = ["f-1"], ResidualId = "r-1" },
        };
        var record = new PreviewRecord
        {
            Sql = "x", RowChanges = rcs, NetChanges = [net],
            Result = new PreviewResult
            {
                PreviewId = "pv", Status = PreviewStatus.Ok, CreatedAt = DateTimeOffset.UnixEpoch, SqlHash = "sha256:0", Baseline = null!, Graph = null!,
                Capture = new CaptureInfo { ImageCount = 2, Cap = 10, Truncated = false, ManifestVersion = "m" }, TimingsMs = new TimingsMs(),
                Firings = fs,
                Residuals = [new Residual { Id = "r-1", Table = "cl.S", Key = net.Key, Columns = ["rev"], DetectedBy = ResidualDetectedBy.NetDiff, Category = ResidualCategory.SameTableTrigger, Confidence = Confidence.Inferred }],
            },
        };
        var hand = new HandCase("CLY", "CLY", "CascadeLab", "y.json", ["UPDATE cl.S SET v = 1 WHERE id = 1;"], "ok", null, new Dictionary<int, string> { [1] = "ok" },
            new Dictionary<string, IReadOnlyList<string>>(), [new HandRow("cl.S", ChangeKinds.Updated, K("1"), ["DIRECT"], ["v", "rev"], new Dictionary<string, string?>())]);
        var oracle = new OracleResult("db", [], [hook], [new OracleStatement(1, "ok", 1, [])], [], false, 1, "d", true, []);
        var score = new CaseScorer(new MetricsCalculator()).Score(new CaseRun
        {
            RunId = "r", Hand = hand, Oracle = oracle, HookChanges = [hook], Outcome = new PreviewOutcome { Analysis = null!, Record = record },
        }, new SuiteAccumulator());
        string Col(IReadOnlyList<string?> row, string name) => row[MetricsCsv.M1Columns.ToList().IndexOf(name)]!;
        var total = score.M1Rows.Single(r => r[4] == "total");
        var cap = score.M1Rows.Single(r => r[4] == "cap");
        Assert.Equal(("2", "0", "0"), (Col(total, "tp"), Col(total, "fp"), Col(total, "fn")));
        Assert.Equal(("1", "2", "0.5000"), (Col(total, "residual_filled_cells"), Col(total, "truth_cells"), Col(total, "residual_fill_rate")));
        Assert.Equal(("1", "0", "1"), (Col(cap, "tp"), Col(cap, "fp"), Col(cap, "fn")));
        Assert.Equal(("1", "0.5000"), (Col(cap, "residual_filled_cells"), Col(cap, "residual_fill_rate")));
        Assert.Equal("", Col(score.M1Rows.Single(r => r[4] == "obs"), "residual_fill_rate"));
        _ = CultureInfo.InvariantCulture;
    }

    [Fact]
    public void Unpaired_update_halves_fold_to_old_key_deleted_and_new_key_inserted()
    {
        // CL04 모양: UPDATE cl04.P SET id = id + 100 WHERE id IN (1, 2) → C11은 짝을 못 지어 before만·after만 있는 UNPAIRED 행 변경 4개를 낸다
        var fs = new[] { F(10, 1, 1, "cl04.P", ChangeOperation.Update) };
        RowChange U(string key, Dictionary<string, string?>? b, Dictionary<string, string?>? a) => RC(10, ChangeOperation.Update, key, b, a) with { Pairing = Pairing.Unpaired };
        var rcs = new[]
        {
            U("1", V(("id", "1"), ("name", "p1")), null), U("2", V(("id", "2"), ("name", "p2")), null),
            U("101", null, V(("id", "101"), ("name", "p1"))), U("102", null, V(("id", "102"), ("name", "p2"))),
        };
        var r = Reconstruction.Fold(fs, rcs);
        var expected = new[] { (ChangeKinds.Deleted, K("1")), (ChangeKinds.Deleted, K("2")), (ChangeKinds.Inserted, K("101")), (ChangeKinds.Inserted, K("102")) };
        Assert.Equal(expected.Order(), r.Select(x => (x.Change, x.Key)).Order());
        Assert.Equal("p1", r.Single(x => x.Key == K("101")).After!["name"]);
    }

    [Fact]
    public void Reread_corrected_cells_are_left_out_of_cap_prediction()
    {
        // 감사 after는 (v 1, w 0)인데 재조회가 w를 2로 고쳤다(FinalAfterSource = REREAD, 잔차 Columns = [w]) → w 셀은 재조회 값이라 cap 예측에 넣지 않는다
        var fs = new[] { F(1, 1, 1, "t.b", ChangeOperation.Update), F(2, 1, 1, "t.c", ChangeOperation.Insert) };
        var upd = RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0"), ("w", "0")), V(("id", "1"), ("v", "1"), ("w", "2"))) with { FinalAfterSource = FinalAfterSource.Reread };
        var ins = RC(2, ChangeOperation.Insert, "7", null, V(("id", "7"), ("v", "9"))) with { FinalAfterSource = FinalAfterSource.Reread };
        Residual Res(string id, string table, string rowChangeId, params string[] cols) => new()
        {
            Id = id, Table = table, Key = upd.Key, Columns = cols, DetectedBy = ResidualDetectedBy.Reread, Category = ResidualCategory.SameTableTrigger,
            Confidence = Confidence.Ambiguous, RowChangeId = rowChangeId,
        };
        var r = Reconstruction.Fold(fs, [upd, ins], [Res("r-1", "t.b", upd.Id, "w")]);
        var u = Assert.Single(r, x => x.Table == "t.b");
        Assert.Equal(["v"], u.ChangedColumns);
        Assert.DoesNotContain(r, x => x.Table == "t.c"); // 고친 열을 모르면(잔차 없음) 그 행의 after 셀을 모두 뺀다
        // 같은 행이 감사 after면(REREAD 아님) 그대로 예측한다
        Assert.Equal(["v", "w"], Reconstruction.Fold(fs, [upd with { FinalAfterSource = FinalAfterSource.Audit }]).Single().ChangedColumns);
    }
}
