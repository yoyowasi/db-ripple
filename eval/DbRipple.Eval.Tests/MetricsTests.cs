using System.Globalization;
using System.Text;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Eval;
using DbRipple.TestSupport;

namespace DbRipple.Eval.Tests;

/// <summary>M1·M2 계산과 CSV 형식을 가짜 제품 결과(PreviewRecord)로 검사한다(DB 없음).</summary>
public class MetricsTests
{
    static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NoVolatile = new Dictionary<string, IReadOnlySet<string>>();
    static string K(int id) => RowKeys.Canonical([new KeyValuePair<string, string?>("id", id.ToString(CultureInfo.InvariantCulture))]);
    static Dictionary<string, string?> V(params (string C, string? V)[] p) => p.ToDictionary(x => x.C, x => x.V);

    static OracleRowChange Del(string t, int id) => new(t, K(id), ChangeKinds.Deleted, 1, V(("id", id.ToString(CultureInfo.InvariantCulture))), null, []);
    static OracleRowChange Ins(string t, int id, params (string, string?)[] cols) => new(t, K(id), ChangeKinds.Inserted, 1, null, V(cols), []);
    static OracleRowChange Upd(string t, int id, string col, string? before, string? after) =>
        new(t, K(id), ChangeKinds.Updated, 1, V((col, before)), V((col, after)), [col]);

    static NetChange Net(string t, int id, NetChangeKind kind, Dictionary<string, string?>? after = null, string[]? changed = null, ExplainedBy? by = null) => new()
    {
        Table = t,
        Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = [id.ToString(CultureInfo.InvariantCulture)] },
        Change = kind,
        Multiplicity = 1,
        After = after,
        ChangedColumns = changed ?? [],
        ExplainedBy = by,
    };

    static PreviewRecord Record(IReadOnlyList<NetChange> net, IReadOnlyList<Residual>? residuals = null, PreviewStatus status = PreviewStatus.Ok) => new()
    {
        Sql = "x",
        NetChanges = net,
        Result = new PreviewResult
        {
            PreviewId = "pv_test",
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
            SqlHash = "sha256:0",
            Baseline = null!,
            Graph = null!,
            Capture = new CaptureInfo { ImageCount = 0, Cap = 1, Truncated = false, ManifestVersion = "m" },
            TimingsMs = new TimingsMs { Analyze = 1, Execute = 2 },
            Residuals = residuals ?? [],
            Statements = [new StatementResult
            {
                Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Delete, Target = "cl.A",
                Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2 }, Status = StatementStatus.Ok, RowsAffected = 1,
            }],
        },
    };

    static readonly ExplainedBy ByFiring = new() { FiringIds = ["f-1"] };

    [Fact]
    public void Perfect_match_gives_one_and_deleted_is_one_fact()
    {
        IReadOnlyList<OracleRowChange> truth = [Del("cl.A", 1), Upd("cl.B", 10, "v", "0", "1"), Ins("cl.C", 5, ("id", "5"), ("x", null))];
        var pred = PredictedChange.FromRecord(Record([
            Net("cl.A", 1, NetChangeKind.Deleted, by: ByFiring),
            Net("cl.B", 10, NetChangeKind.Updated, V(("v", "1")), ["v"], ByFiring),
            Net("cl.C", 5, NetChangeKind.Inserted, V(("id", "5"), ("x", null)), by: ByFiring)]));
        var (filled, baseCells) = MetricsCalculator.ResidualByExplanation(pred, NoVolatile);
        var c = new MetricsCalculator().M1(truth, pred, NoVolatile) with { ResidualFilledCells = filled, ResidualBaseCells = baseCells };
        Assert.Equal(4, c.TruthCells); // DELETED 1 + UPDATED 1 + INSERTED 2
        Assert.Equal((4L, 0L, 0L), (c.Tp, c.Fp, c.Fn));
        Assert.Equal((3L, 3L), (c.RowsUnion, c.RowsExact));
        Assert.Equal(0, c.ResidualFilledCells);
        var cells = c.CsvCells();
        Assert.Equal(["1.0000", "1.0000", "1.0000"], cells.Skip(5).Take(3));
        Assert.Equal("0.0000", cells[MetricsCsv.M1Columns.ToList().IndexOf("residual_fill_rate") - 5]);
        Assert.Equal(["0", "0"], cells.TakeLast(2)); // identity_rows, identity_paired
    }

    [Fact]
    public void Wrong_value_counts_as_fp_and_fn_missing_and_extra_rows_count_once()
    {
        IReadOnlyList<OracleRowChange> truth = [Upd("cl.B", 10, "v", "0", "1"), Del("cl.A", 1), Del("cl.A", 2)];
        var pred = PredictedChange.FromRecord(Record([
            Net("cl.B", 10, NetChangeKind.Updated, V(("v", "2")), ["v"]),
            Net("cl.A", 1, NetChangeKind.Deleted),
            Net("cl.A", 1, NetChangeKind.Deleted),   // 중복은 한 번만 센다
            Net("cl.A", 3, NetChangeKind.Deleted)])); // 정답에 없음
        var c = new MetricsCalculator().M1(truth, pred, NoVolatile);
        Assert.Equal((1L, 2L, 2L), (c.Tp, c.Fp, c.Fn)); // tp: A1, fp: B10 값·A3, fn: B10 값·A2
        Assert.Equal(3, c.TruthCells);
        Assert.Equal(3, c.PredictedCells);
        Assert.Equal((4L, 1L), (c.RowsUnion, c.RowsExact));
        Assert.Null(c.ResidualFilledCells);
        Assert.Equal("", c.CsvCells()[MetricsCsv.M1Columns.ToList().IndexOf("residual_fill_rate") - 5]);
    }

    [Fact]
    public void Volatile_cells_are_excluded_and_checked_for_presence_and_format()
    {
        var vol = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase) { ["cl.L"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "at" } };
        IReadOnlyList<OracleRowChange> truth = [Ins("cl.L", 7, ("id", "7"), ("at", "2026-11-10T10:00:00.123"), ("x", "a"))];
        var good = PredictedChange.FromRecord(Record([Net("cl.L", 7, NetChangeKind.Inserted, V(("id", "7"), ("at", "2026-11-10T10:00:01.45"), ("x", "a")))]));
        var c = new MetricsCalculator().M1(truth, good, vol);
        Assert.Equal((1L, 1L, 0L, 0L), (c.TruthCells, c.Tp, c.Fp, c.Fn));
        Assert.Equal((2L, 2L), (c.VolatileCells, c.VolatilePresentFormatOk));
        Assert.Equal(1, c.RowsExact);

        var bad = PredictedChange.FromRecord(Record([Net("cl.L", 7, NetChangeKind.Inserted, V(("id", "7"), ("at", null), ("x", "a")))]));
        var b = new MetricsCalculator().M1(truth, bad, vol);
        Assert.Equal(1, b.VolatilePresentFormatOk);
        Assert.Equal(0, b.RowsExact);
        Assert.Equal("x-x-xTx:x:x.x", MetricsCalculator.FormatShape("2026-11-10T10:00:00.123"));
    }

    [Fact]
    public void Residual_filled_cells_are_counted_only_for_residual_explanations()
    {
        IReadOnlyList<OracleRowChange> truth = [Upd("cl.S", 1, "rev", "8", "9")];
        var residual = new Residual
        {
            Id = "r-1", Table = "cl.S", Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = ["1"] }, Columns = ["rev"],
            DetectedBy = ResidualDetectedBy.Reread, Category = ResidualCategory.SameTableTrigger, Confidence = Confidence.Inferred,
        };
        var pred = PredictedChange.FromRecord(Record([Net("cl.S", 1, NetChangeKind.Updated, V(("rev", "9")), ["rev"], new ExplainedBy { ResidualId = "r-1" })], [residual]));
        Assert.True(pred[0].ResidualOnly);
        Assert.Equal((1L, 1L), MetricsCalculator.ResidualByExplanation(pred, NoVolatile));
        // 같은 행이 묶음으로도 설명되면 Residual.Columns의 열만 잔차로 센다
        var both = PredictedChange.FromRecord(Record([Net("cl.S", 1, NetChangeKind.Updated, V(("rev", "9"), ("x", "1")), ["rev", "x"], new ExplainedBy { FiringIds = ["f-1"], ResidualId = "r-1" })], [residual]));
        Assert.Equal((1L, 2L), MetricsCalculator.ResidualByExplanation(both, NoVolatile));
        // cap: 재구성에 없는 최종 차이 셀
        Assert.Equal((1L, 1L), MetricsCalculator.ResidualByAbsence(pred, [], NoVolatile));
        Assert.Equal((0L, 1L), MetricsCalculator.ResidualByAbsence(pred, truth, NoVolatile));
    }

    [Fact]
    public void Heap_rows_from_product_match_harness_multiset_keys()
    {
        var after = V(("a", "3"), ("b", null));
        var harness = new OracleRowChange("cl.H", RowKeys.Canonical(after), ChangeKinds.MultisetAdded, 2, null, after, []);
        var product = new NetChange { Table = "cl.H", Key = new RowKey { Kind = KeyKind.ROW }, Change = NetChangeKind.MultisetAdded, Multiplicity = 2, After = after };
        var pred = PredictedChange.FromRecord(Record([product]));
        Assert.Equal(ChangeKinds.MultisetAdded, pred[0].Row.Change);
        var c = new MetricsCalculator().M1([harness], pred, NoVolatile);
        Assert.Equal((4L, 4L, 0L, 0L), (c.TruthCells, c.Tp, c.Fp, c.Fn)); // 2행 × 2열
        Assert.Equal(2, c.RowsUnion);
    }

    static HandCase Hand(params HandRow[] rows) =>
        new("CLX", "CLX", "CascadeLab", "x.json", ["DELETE FROM cl.A WHERE id = 1;"], "ok", null, new Dictionary<int, string> { [1] = "ok" },
            new Dictionary<string, IReadOnlyList<string>>(), rows);

    static HandRow HR(string t, int id, string kind, params string[] causes) => new(t, kind, K(id), causes, [], new Dictionary<string, string?>());

    [Fact]
    public void M2_buckets_by_cause_with_one_of_multi_cause_and_kind_check()
    {
        var hand = Hand(
            HR("cl.A", 1, ChangeKinds.Deleted, "DIRECT"),
            HR("cl.B", 10, ChangeKinds.Deleted, "FK_CASCADE"),
            HR("cl.B", 11, ChangeKinds.Deleted, "FK_CASCADE"),
            HR("cl.S", 1, ChangeKinds.Updated, "DIRECT", "TRIGGER"),
            HR("cl.O", 5, ChangeKinds.Inserted, "ONE_OF"));
        var residual = new Residual
        {
            Id = "r-1", Table = "cl.O", Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = ["5"] },
            DetectedBy = ResidualDetectedBy.NetDiff, Category = ResidualCategory.Unknown, Confidence = Confidence.Unexplained,
        };
        var pred = PredictedChange.FromRecord(Record([
            Net("cl.A", 1, NetChangeKind.Deleted, by: ByFiring),
            Net("cl.B", 10, NetChangeKind.Deleted, by: ByFiring),
            Net("cl.B", 11, NetChangeKind.Updated, V(("x", "1")), ["x"], ByFiring), // 종류가 달라 못 찾음
            Net("cl.S", 1, NetChangeKind.Updated, V(("v", "1")), ["v"], ByFiring),
            Net("cl.O", 5, NetChangeKind.Inserted, V(("id", "5")), by: new ExplainedBy { ResidualId = "r-1" })], [residual]));
        var m2 = new MetricsCalculator().M2(hand, pred).ToDictionary(b => b.Cause);
        Assert.Equal(HandCase.Causes.Count + 1, m2.Count);
        Assert.Equal((2L, 2L, 2L), (m2["DIRECT"].TruthRows, m2["DIRECT"].FoundRows, m2["DIRECT"].FoundByFiring));
        Assert.Equal((2L, 1L), (m2["FK_CASCADE"].TruthRows, m2["FK_CASCADE"].FoundRows));
        Assert.Equal(["cl.B(ID=11)"], m2["FK_CASCADE"].MissedKeys);
        Assert.Equal((1L, 1L), (m2["TRIGGER"].TruthRows, m2["TRIGGER"].FoundRows));
        Assert.Equal((1L, 1L, 1L), (m2["ONE_OF"].TruthRows, m2["ONE_OF"].FoundRows, m2["ONE_OF"].FoundByResidualOnly));
        Assert.Equal((5L, 4L), (m2["ALL"].TruthRows, m2["ALL"].FoundRows)); // cl.S는 ALL에서 한 번
        Assert.Equal("", m2["FK_SET_NULL"].CsvCells()[2]); // 분모 0 → 빈 칸
        Assert.Equal("0.5000", m2["FK_CASCADE"].CsvCells()[2]);
    }

    [Fact]
    public void Hand_cases_load_with_causes_and_keys()
    {
        var cases = HandCase.LoadAll(Path.Combine(RepoPaths.CascadeLab, "cases"));
        Assert.True(cases.Count >= 11); // CL01~CL08b + 3주차 CL09~CL20(lab-1)
        var cl08a = cases.Single(c => c.Id == "CL08a");
        var row = Assert.Single(cl08a.Rows);
        Assert.Equal(["DIRECT", "TRIGGER"], row.Causes);
        Assert.Equal("1", row.After["touched"]);
        var cl02 = cases.Single(c => c.Id == "CL02");
        Assert.Contains(cl02.Rows, r => r.Kind == ChangeKinds.Updated && r.After.ContainsKey("dept_id") && r.After["dept_id"] is null);
        var cl03b = cases.Single(c => c.Id == "CL03b");
        Assert.Equal((("failed", 547)), (cl03b.ExpectedStatus, cl03b.ExpectedErrorNumber!.Value));
        Assert.Empty(cl03b.Rows);
        Assert.All(cases.SelectMany(c => c.Rows).SelectMany(r => r.Causes), c => Assert.Contains(c, HandCase.Causes));

        // 노드 참조 #k 정규화(C13과 같은 규칙): 같은 문장에 #2가 없으면 #1을 뗀다. 2026-11-10 기준으로 바뀌는 것은 CL08a의 트리거 실행 하나뿐이다.
        Assert.Contains("CL08a: 문장 1: T:cl08.trg_Acct_upd#1 → T:cl08.trg_Acct_upd",
            cases.SelectMany(c => c.NormalizedRefs.Select(x => c.Id + ": " + x)));
        Assert.Contains(cl08a.Edges, e => e.Kind == "FIRES" && e.Parent == "F:cl08.Acct:UPDATE#1" && e.Child == "T:cl08.trg_Acct_upd");
        Assert.Contains(cl08a.Edges, e => e.Kind == "WRITES" && e.Parent == "T:cl08.trg_Acct_upd" && e.Child == "F:cl08.Acct:UPDATE#2");
        var cl08b = cases.Single(c => c.Id == "CL08b");
        Assert.Contains(cl08b.Edges, e => e.Child == "T:cl08.trg_Acct_upd#1");
        Assert.Equal(cases.Sum(c => c.Edges.Count), cases.Sum(c => c.Edges.Select(e => (e.StatementIndex, e.Parent, e.Child, e.Kind, e.Name)).Distinct().Count()));
        Assert.Equal(new Dictionary<string, string> { ["S"] = "S", ["F:x#1"] = "F:x#1", ["F:x#2"] = "F:x#2", ["T:y#1"] = "T:y" },
            NodeRefs.Normalize(["S", "F:x#1", "F:x#2", "T:y#1"]));
    }

    [Fact]
    public void Case_scorer_writes_rows_with_spec_column_counts_and_blank_hook_rows()
    {
        var hand = Hand(HR("cl.A", 1, ChangeKinds.Deleted, "DIRECT"));
        var oracle = new OracleResult("db", [], [Del("cl.A", 1)], [new OracleStatement(1, "ok", 1, [])], [], false, 3, "d", true, []);
        var suite = new SuiteAccumulator();
        var scorer = new CaseScorer(new MetricsCalculator());
        var s = scorer.Score(new CaseRun
        {
            RunId = "run1", Hand = hand, Oracle = oracle, OracleRawBatch = oracle with { RawBatchPath = true },
            Outcome = new PreviewOutcome { Analysis = null!, Record = Record([Net("cl.A", 1, NetChangeKind.Deleted, by: ByFiring)]) },
            HookChanges = null, RecordPath = "r.json",
        }, suite);
        Assert.Equal(MetricsCsv.CaseColumns.Count, s.CaseRow.Count);
        Assert.All(s.M1Rows, r => Assert.Equal(MetricsCsv.M1Columns.Count, r.Count));
        Assert.All(s.M2Rows, r => Assert.Equal(MetricsCsv.M2Columns.Count, r.Count));
        Assert.Equal(HandCase.Causes.Count, s.M2Rows.Count); // 8(INSTEAD_OF 포함), ALL은 metrics.csv에만
        Assert.Equal(AttributionMetrics.Scopes.Count, s.M3Rows.Count);
        Assert.All(s.M3Rows, r => Assert.Equal(MetricsCsv.M3Columns.Count, r.Count));
        string Col(IReadOnlyList<string?> row, IReadOnlyList<string> cols, string name) => row[cols.ToList().IndexOf(name)]!;
        Assert.Equal("ok", Col(s.CaseRow, MetricsCsv.CaseColumns, "status_actual"));
        Assert.Equal("true", Col(s.CaseRow, MetricsCsv.CaseColumns, "status_match"));
        Assert.Equal("true", Col(s.CaseRow, MetricsCsv.CaseColumns, "oracle_vs_hand_keys_match"));
        Assert.Equal("true", Col(s.CaseRow, MetricsCsv.CaseColumns, "raw_batch_match"));
        Assert.Equal("1", Col(s.CaseRow, MetricsCsv.CaseColumns, "statements_status_match"));
        Assert.Equal("", Col(s.CaseRow, MetricsCsv.CaseColumns, "error_actual"));
        Assert.Equal("1.0000", Col(s.M1Rows[0], MetricsCsv.M1Columns, "f1"));
        Assert.Equal("", Col(s.M1Rows[1], MetricsCsv.M1Columns, "f1")); // obs: 훅 없음
        Assert.Equal("", Col(s.M1Rows[2], MetricsCsv.M1Columns, "truth_cells")); // cap: 훅 없음

        var rejected = scorer.Score(new CaseRun { RunId = "run1", Hand = hand, Oracle = oracle, Outcome = new PreviewOutcome { Analysis = null! } }, suite);
        Assert.Equal("rejected", Col(rejected.CaseRow, MetricsCsv.CaseColumns, "status_actual"));
        Assert.Equal("0", Col(rejected.M1Rows[0], MetricsCsv.M1Columns, "tp"));

        var rows = suite.Rows("run1").ToList();
        Assert.All(rows, r => Assert.Equal(MetricsCsv.SuiteColumns.Count, r.Count));
        var f1 = rows.Single(r => r[2] == "M1.f1" && r[3] == "total");
        Assert.Equal(["2", "3", "0.6667", "1"], f1.Skip(4)); // 2tp=2, 2tp+fp+fn=2+0+1
        Assert.Equal(["1", "2", "0.5000"], rows.Single(r => r[2] == "M2.recall" && r[3] == "DIRECT").Skip(4).Take(3));
        Assert.Equal(["1", "2", "0.5000"], rows.Single(r => r[2] == "status_match_rate").Skip(4).Take(3));
    }

    [Fact]
    public void Csv_files_are_utf8_without_bom_crlf_rfc4180_and_invariant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbripple-eval-tests", Guid.NewGuid().ToString("N"));
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var path = Path.Combine(dir, "m.csv");
            MetricsCsv.Append(path, ["a", "b"], [MetricsCsv.Rate(2, 3), "x,\"y\"\n한글"]);
            MetricsCsv.Append(path, ["a", "b"], [MetricsCsv.Int(1234567), null]);
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("a,b\r\n0.6667,\"x,\"\"y\"\"\n한글\"\r\n1234567,\r\n", Encoding.UTF8.GetString(bytes));
            Assert.Throws<ArgumentException>(() => MetricsCsv.Append(path, ["a", "b"], ["1"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
