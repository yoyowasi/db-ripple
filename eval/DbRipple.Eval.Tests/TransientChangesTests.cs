using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Eval;

namespace DbRipple.Eval.Tests;

/// <summary>M6 중간 변경(TransientChanges): 발동 보기에는 있고 최종 차이에는 없는 행 변경 수. 가짜 묶음·행 변경으로 검사한다(DB 없음).</summary>
public class TransientChangesTests
{
    static Firing F(long seq, string table, ChangeOperation op, ImageMode mode = ImageMode.Full) => new()
    {
        Id = ContractIds.Firing(seq), CaptureSeq = seq, StatementIndex = 1, Depth = 1, Table = table, Operation = op,
        RowCount = 1, ImageMode = mode, AuditOrder = AuditOrder.Last,
    };

    static Dictionary<string, string?> V(params (string C, string? V)[] p) => p.ToDictionary(x => x.C, x => x.V);

    static int n;
    static RowChange RC(long seq, ChangeOperation op, string id, Dictionary<string, string?>? before, Dictionary<string, string?>? after) => new()
    {
        Id = ContractIds.RowChange(seq, Interlocked.Increment(ref n)), FiringId = ContractIds.Firing(seq), Operation = op,
        Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = [id] },
        Before = before, After = after, Pairing = Pairing.ByPk, FinalAfterSource = FinalAfterSource.Audit,
    };

    static NetChange Net(string table, string id, NetChangeKind kind, Dictionary<string, string?>? after) => new()
    {
        Table = table, Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = [id] }, Change = kind, Multiplicity = 1, After = after,
    };

    static PreviewRecord Rec(IReadOnlyList<Firing> fs, IReadOnlyList<RowChange> rcs, IReadOnlyList<NetChange> net, PreviewStatus status = PreviewStatus.Ok) => new()
    {
        Sql = "x", RowChanges = rcs, NetChanges = net,
        Result = new PreviewResult
        {
            PreviewId = "pv", Status = status, CreatedAt = DateTimeOffset.UnixEpoch, SqlHash = "sha256:0", Baseline = null!, Graph = null!,
            Capture = new CaptureInfo { ImageCount = rcs.Count, Cap = 10, Truncated = false, ManifestVersion = "m" }, TimingsMs = new TimingsMs(), Firings = fs,
        },
    };

    static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NoVol = new Dictionary<string, IReadOnlySet<string>>();

    static TransientCounts Run(PreviewRecord r) => TransientChanges.Compute(r, KeyFolding.None, NoVol)!;

    [Fact]
    public void Update_then_trigger_delete_leaves_update_as_transient()
    {
        // UPDATE로 v=1, 다른 트리거가 같은 행 DELETE: 최종 차이는 DELETED 하나, 발동 보기의 UPDATE는 사라진다
        var rec = Rec([F(1, "t.a", ChangeOperation.Update), F(2, "t.a", ChangeOperation.Delete)],
            [RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0")), V(("id", "1"), ("v", "1"))), RC(2, ChangeOperation.Delete, "1", V(("id", "1"), ("v", "1")), null)],
            [Net("t.a", "1", NetChangeKind.Deleted, null)]);
        var c = Run(rec);
        Assert.Equal((2L, 2L, 1L), (c.Events, c.Measured, c.Transient));
        Assert.StartsWith("UPDATE t.a", c.TransientKeys.Single());
    }

    [Fact]
    public void Value_changed_and_restored_is_all_transient()
    {
        var rec = Rec([F(1, "t.a", ChangeOperation.Update), F(2, "t.a", ChangeOperation.Update)],
            [RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0")), V(("id", "1"), ("v", "5"))), RC(2, ChangeOperation.Update, "1", V(("id", "1"), ("v", "5")), V(("id", "1"), ("v", "0")))],
            []);
        var c = Run(rec);
        Assert.Equal((2L, 1L), (c.Measured, c.Transient)); // 최종 = 원래 값(v0)이라 v5로 간 이벤트만 중간
    }

    [Fact]
    public void Intermediate_value_update_counts_only_the_intermediate_step()
    {
        var rec = Rec([F(1, "t.a", ChangeOperation.Update), F(2, "t.a", ChangeOperation.Update)],
            [RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0")), V(("id", "1"), ("v", "5"))), RC(2, ChangeOperation.Update, "1", V(("id", "1"), ("v", "5")), V(("id", "1"), ("v", "9")))],
            [Net("t.a", "1", NetChangeKind.Updated, V(("id", "1"), ("v", "9")))]);
        var c = Run(rec);
        Assert.Equal((2L, 1L), (c.Measured, c.Transient));
    }

    [Fact]
    public void Plain_changes_and_duplicate_events_are_not_transient()
    {
        // 같은 서명이 두 번 나와도 한 번만 세고, 최종 차이와 같으면 중간 변경이 아니다
        var rec = Rec([F(1, "t.a", ChangeOperation.Insert), F(2, "t.a", ChangeOperation.Insert)],
            [RC(1, ChangeOperation.Insert, "1", null, V(("id", "1"), ("v", "1"))), RC(2, ChangeOperation.Insert, "1", null, V(("id", "1"), ("v", "1")))],
            [Net("t.a", "1", NetChangeKind.Inserted, V(("id", "1"), ("v", "1")))]);
        var c = Run(rec);
        Assert.Equal((2L, 1L, 0L), (c.Events, c.Measured, c.Transient));
    }

    [Fact]
    public void Failed_status_and_keys_mode_are_not_measured()
    {
        var failed = Rec([F(1, "t.a", ChangeOperation.Update)], [RC(1, ChangeOperation.Update, "1", V(("id", "1")), V(("id", "1")))], [], PreviewStatus.Failed);
        Assert.Null(TransientChanges.Compute(failed, KeyFolding.None, NoVol));
        var keys = Rec([F(1, "t.a", ChangeOperation.Update, ImageMode.Keys)], [RC(1, ChangeOperation.Update, "1", null, null)], []);
        var c = Run(keys);
        Assert.Equal((1L, 0L, 0L), (c.Events, c.Measured, c.Transient));
        Assert.Null(TransientChanges.Compute(null, KeyFolding.None, NoVol));
    }

    [Fact]
    public void Suite_row_and_case_row_report_the_rate()
    {
        var rec = Rec([F(1, "t.a", ChangeOperation.Update), F(2, "t.a", ChangeOperation.Delete)],
            [RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0")), V(("id", "1"), ("v", "1"))), RC(2, ChangeOperation.Delete, "1", V(("id", "1"), ("v", "1")), null)],
            [Net("t.a", "1", NetChangeKind.Deleted, null)]);
        var suite = new SuiteAccumulator();
        suite.AddTransient("preview", "CLX", Run(rec));
        var row = suite.Rows("r").Single(r => r[2] == "M6.transient_changes");
        Assert.Equal(["r", "preview", "M6.transient_changes", "all", "1", "2", "0.5000", "1"], row);
        Assert.Equal(MetricsCsv.M6Columns.Count, 4 + Run(rec).CsvCells().Count);
    }

    [Fact]
    public void Insert_then_trigger_update_counts_only_the_insert()
    {
        var rec = Rec([F(1, "t.a", ChangeOperation.Insert), F(2, "t.a", ChangeOperation.Update)],
            [RC(1, ChangeOperation.Insert, "1", null, V(("id", "1"), ("v", "1"))), RC(2, ChangeOperation.Update, "1", V(("id", "1"), ("v", "1")), V(("id", "1"), ("v", "2")))],
            [Net("t.a", "1", NetChangeKind.Inserted, V(("id", "1"), ("v", "2")))]);
        var c = Run(rec);
        Assert.Equal((2L, 1L), (c.Measured, c.Transient));
        Assert.StartsWith("INSERT t.a", c.TransientKeys.Single());
    }

    [Fact]
    public void Delete_then_reinsert_same_values_counts_only_the_delete()
    {
        var rec = Rec([F(1, "t.a", ChangeOperation.Delete), F(2, "t.a", ChangeOperation.Insert)],
            [RC(1, ChangeOperation.Delete, "1", V(("id", "1"), ("v", "1")), null), RC(2, ChangeOperation.Insert, "1", null, V(("id", "1"), ("v", "1")))],
            []);
        var c = Run(rec);
        Assert.Equal((2L, 1L), (c.Measured, c.Transient));
        Assert.StartsWith("DELETE t.a", c.TransientKeys.Single());
    }

    [Fact]
    public void Columns_missing_from_event_images_are_not_compared()
    {
        // text 열은 발동 보기 이미지에 없고 최종 차이에는 있다: 정상 변경은 0
        var rec = Rec([F(1, "t.a", ChangeOperation.Update)],
            [RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0")), V(("id", "1"), ("v", "1")))],
            [Net("t.a", "1", NetChangeKind.Updated, V(("id", "1"), ("v", "1"), ("body", "긴 글")))]);
        Assert.Equal((1L, 0L), (Run(rec).Measured, Run(rec).Transient));
    }

    [Fact]
    public void Keyless_table_rows_use_the_shared_columns_for_identity()
    {
        RowChange Row(long seq, ChangeOperation op, Dictionary<string, string?>? b, Dictionary<string, string?>? a) =>
            RC(seq, op, "", b, a) with { Key = new RowKey { Kind = KeyKind.ROW } };
        var rec = Rec([F(1, "t.k", ChangeOperation.Insert)],
            [Row(1, ChangeOperation.Insert, null, V(("x", "1")))],
            [new NetChange { Table = "t.k", Key = new RowKey { Kind = KeyKind.ROW }, Change = NetChangeKind.MultisetAdded, Multiplicity = 1, After = V(("x", "1"), ("body", "b")) }]);
        Assert.Equal(0L, Run(rec).Transient);
    }

    [Fact]
    public void Volatile_columns_from_the_map_are_ignored()
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase) { ["t.a"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ts" } };
        var rec = Rec([F(1, "t.a", ChangeOperation.Update)],
            [RC(1, ChangeOperation.Update, "1", V(("id", "1"), ("v", "0"), ("ts", "a")), V(("id", "1"), ("v", "1"), ("ts", "a")))],
            [Net("t.a", "1", NetChangeKind.Updated, V(("id", "1"), ("v", "1"), ("ts", "b")))]);
        Assert.Equal(0L, TransientChanges.Compute(rec, KeyFolding.None, map)!.Transient);
    }

    [Fact]
    public void Unpaired_pk_change_halves_are_excluded()
    {
        var rec = Rec([F(1, "t.a", ChangeOperation.Update)],
            [RC(1, ChangeOperation.Update, "1", V(("id", "1")), null) with { Pairing = Pairing.Unpaired }, RC(1, ChangeOperation.Update, "2", null, V(("id", "2"))) with { Pairing = Pairing.Unpaired }],
            [Net("t.a", "1", NetChangeKind.Deleted, null), Net("t.a", "2", NetChangeKind.Inserted, V(("id", "2")))]);
        var c = Run(rec);
        Assert.Equal((2L, 0L, 0L), (c.Events, c.Measured, c.Transient));
    }
}
