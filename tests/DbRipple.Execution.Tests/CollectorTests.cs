using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Execution.Tests;

/// <summary>C11 순수 단위 테스트(DB 없음): §6.1 숫자 원문, 짝짓기 4종과 Q11, volatile·derived, 괄호(NONE 보완), 문장 나누기, 최종 after와 Q14.</summary>
public partial class CollectorTests
{
    // ---- 작은 카탈로그 ----
    // dbo.T(id PK, code NOT NULL UNIQUE, v int, amt decimal, c AS 계산 열) object 1
    // dbo.H(a, b) 키 없음 object 2
    // dbo.I(id IDENTITY PK, at datetime2 DEFAULT SYSDATETIME(), note) object 3
    // dbo.A(id PK) object 4
    static BaselineContext Ctx(string updateOrder = CaptureManifest.Last, IReadOnlyList<TriggerStaticInfo>? triggers = null)
    {
        CatalogColumn Col(int obj, int id, string name, string type, bool computed = false, bool identity = false, string? def = null) =>
            new(obj, id, name, type, false, false, computed, identity, false, def);
        var catalog = new DbCatalog(
            [new CatalogTable(1, "dbo", "T", TemporalType.None, false), new CatalogTable(2, "dbo", "H", TemporalType.None, false),
             new CatalogTable(3, "dbo", "I", TemporalType.None, false), new CatalogTable(4, "dbo", "A", TemporalType.None, false)],
            [Col(1, 1, "id", "int"), Col(1, 2, "code", "varchar"), Col(1, 3, "v", "int"), Col(1, 4, "amt", "decimal"), Col(1, 5, "c", "int", computed: true),
             Col(2, 1, "a", "int"), Col(2, 2, "b", "int"),
             Col(3, 1, "id", "int", identity: true), Col(3, 2, "at", "datetime2", def: "(sysdatetime())"), Col(3, 3, "note", "nvarchar"),
             Col(4, 1, "id", "int")],
            [new CatalogIndexColumn(1, 1, "PK_T", true, true, 1, "id", false), new CatalogIndexColumn(1, 2, "UQ_T_code", false, true, 1, "code", false),
             new CatalogIndexColumn(3, 1, "PK_I", true, true, 1, "id", false), new CatalogIndexColumn(4, 1, "PK_A", true, true, 1, "id", false)],
            [], [], []);
        var tables = catalog.ToCaptureInput();
        var manifest = AuditTriggerGenerator.Generate(tables, DateTimeOffset.Parse("2026-11-10T09:00:00+09:00")).Manifest;
        manifest = manifest with
        {
            Tables = manifest.Tables.Select(t => t.Table == "dbo.A" ? t with { AuditOrder = t.AuditOrder with { Update = updateOrder } } : t).ToList(),
        };
        return new BaselineContext
        {
            OriginDb = "o", SandboxDb = "s", SnapshotDb = "b", BackupFinishedAt = DateTimeOffset.UnixEpoch,
            Fingerprint = new Fingerprint("17", "Dev", 170, false, 1, 0, 5432, "SQL_Latin1_General_CP1_CI_AS"), FingerprintMatch = true,
            Catalog = catalog, Triggers = triggers ?? [], Manifest = manifest,
        };
    }

    sealed class RawBuilder
    {
        readonly List<AuditHeaderRow> headers = [];
        readonly List<AuditRowImageRow> images = [];
        readonly List<RereadRow> rereads = [];
        readonly List<long> boundaries = [];
        static readonly Dictionary<int, string> Names = new() { [1] = "[dbo].[T]", [2] = "[dbo].[H]", [3] = "[dbo].[I]", [4] = "[dbo].[A]" };

        public RawBuilder Firing(long seq, int obj, ChangeOperation op, int depth, string[]? deleted = null, string[]? inserted = null, bool keys = false)
        {
            deleted ??= []; inserted ??= [];
            headers.Add(new AuditHeaderRow
            {
                CaptureSeq = seq, TableObjectId = obj, TableName = Names[obj], Operation = op,
                InsertedCount = inserted.Length, DeletedCount = deleted.Length, NestLevel = depth, TranCount = 2, ImageMode = keys ? ImageMode.Keys : ImageMode.Full,
            });
            void Add(ImageKind k, string[] rows)
            {
                for (var i = 0; i < rows.Length; i++)
                {
                    var key = obj == 2 ? null : KeyOf(rows[i]);
                    images.Add(new AuditRowImageRow { CaptureSeq = seq, ImageKind = k, Ordinal = i + 1, KeyJson = key, RowJson = keys ? null : rows[i] });
                }
            }
            Add(ImageKind.Deleted, deleted);
            Add(ImageKind.Inserted, inserted);
            return this;
        }

        static string KeyOf(string row) => "{\"id\":" + System.Text.Json.JsonDocument.Parse(row).RootElement.GetProperty("id").GetRawText() + "}";

        public RawBuilder EndStatement(long boundary) { boundaries.Add(boundary); return this; }
        public RawBuilder Reread(string table, string key, string? row) { rereads.Add(new RereadRow { Table = table, KeyJson = key, RowJson = row }); return this; }

        public RawCapture Build() => new()
        {
            Status = PreviewStatus.Ok,
            Executions = boundaries.Select((b, i) => new StatementExecution
            {
                Result = new StatementResult
                {
                    Index = i + 1, Batch = 1, Position = i + 1, Kind = ChangeOperation.Update, Target = "dbo.T",
                    Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2 }, Status = StatementStatus.Ok,
                },
                Boundary = b,
            }).ToList(),
            Boundary0 = 100,
            Headers = headers, Images = images, Rereads = rereads,
            SandboxDirty = false,
            Capture = new CaptureInfo { ImageCount = 0, Cap = 10, Truncated = false, ManifestVersion = "m" },
            Timings = new ExecutionTimings(),
        };
    }

    static AnalysisResult Analysis(params AnalyzedStatement[] s) => new() { Statements = s };

    static CollectResult Collect(RawCapture raw, BaselineContext? ctx = null, AnalysisResult? analysis = null) =>
        new ChangeCollector().Collect(raw, analysis ?? new AnalysisResult(), ctx ?? Ctx());

    static void Link(TriggerContextLink l, string[] candidates, Confidence cap)
    {
        Assert.Equal(candidates, l.CandidateParentFiringIds);
        Assert.Equal(cap, l.ConfidenceCap);
    }

    static void Diff(RereadDifference d, string rowChangeId, string[] columns, bool undetermined)
    {
        Assert.Equal(rowChangeId, d.RowChangeId);
        Assert.Equal(columns, d.Columns);
        Assert.Equal(undetermined, d.OrderUndetermined);
    }

    const string T1 = "{\"id\":1,\"code\":\"a\",\"v\":1,\"amt\":12.50,\"c\":10}";

    [Fact]
    public void Numbers_keep_their_json_text()
    {
        var row = ChangeCollector.ParseRow("{\"id\":9007199254740993,\"amt\":12.50,\"m\":0.0000,\"f\":\"1.0000000000000000E+000\",\"b\":true,\"n\":null,\"x\":\"y\"}");
        Assert.Equal("9007199254740993", row["id"]);
        Assert.Equal("12.50", row["amt"]);
        Assert.Equal("0.0000", row["m"]);
        Assert.Equal("1.0000000000000000E+000", row["f"]);
        Assert.Equal("true", row["b"]);
        Assert.Null(row["n"]);
        Assert.Equal("y", row["x"]);
        Assert.Equal(["id", "amt", "m", "f", "b", "n", "x"], row.Keys);
    }

    [Fact]
    public void Update_with_unchanged_key_pairs_by_pk_and_computes_changed_columns_by_value()
    {
        var raw = new RawBuilder()
            .Firing(101, 1, ChangeOperation.Update, 1,
                deleted: [T1, "{\"id\":2,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}"],
                inserted: ["{\"id\":2,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}", "{\"id\":1,\"code\":\"a\",\"v\":2,\"amt\":12.50,\"c\":20}"])
            .EndStatement(101).Build();
        var r = Collect(raw);
        Assert.Equal(["rc-101-1", "rc-101-2"], r.RowChanges.Select(x => x.Id));
        var row1 = r.RowChanges[0];
        Assert.Equal(["1"], row1.Key.Values);
        Assert.Equal(Pairing.ByPk, row1.Pairing);
        Assert.Equal(["v", "c"], row1.ChangedColumns);
        Assert.Equal(["c"], row1.Derived);
        Assert.Empty(r.RowChanges[1].ChangedColumns); // 값이 같으면 UPDATE 대상이어도 바뀐 열 없음(A12)
        Assert.Empty(r.RowChanges[1].Derived);
        var f = Assert.Single(r.Firings);
        Assert.Equal(("dbo.T", 1, 2L, AuditOrder.Last, 1), (f.Table, f.Depth, f.RowCount, f.AuditOrder, f.StatementIndex));
    }

    [Fact]
    public void Pk_change_pairs_by_unchanged_unique_key_else_unpaired_and_single_row_is_paired()
    {
        // 다중 행 PK 변경, code 그대로 → BY_UNIQUE, Q11: 키는 새 키
        var byUnique = Collect(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1,
                deleted: [T1, "{\"id\":2,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}"],
                inserted: ["{\"id\":101,\"code\":\"a\",\"v\":1,\"amt\":12.50,\"c\":10}", "{\"id\":102,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}"])
            .EndStatement(101).Build());
        Assert.All(byUnique.RowChanges, x => Assert.Equal(Pairing.ByUnique, x.Pairing));
        Assert.Equal([["101"], ["102"]], byUnique.RowChanges.Select(x => x.Key.Values.ToArray()));
        Assert.Equal(["id"], byUnique.RowChanges[0].ChangedColumns);
        Assert.Equal("1", byUnique.RowChanges[0].Before!["id"]);

        // code도 바뀌면 UNPAIRED(옛 키 before 행 + 새 키 after 행)
        var unpaired = Collect(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1,
                deleted: [T1, "{\"id\":2,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}"],
                inserted: ["{\"id\":101,\"code\":\"x\",\"v\":1,\"amt\":12.50,\"c\":10}", "{\"id\":102,\"code\":\"y\",\"v\":5,\"amt\":1.00,\"c\":50}"])
            .EndStatement(101).Build());
        Assert.Equal(4, unpaired.RowChanges.Count);
        Assert.All(unpaired.RowChanges, x => { Assert.Equal(Pairing.Unpaired, x.Pairing); Assert.Empty(x.ChangedColumns); Assert.True(x.Before is null ^ x.After is null); });
        Assert.Equal(["1", "2", "101", "102"], unpaired.RowChanges.Select(x => x.Key.Values[0]));

        // 한 행이면 PK가 바뀌어도 짝 확정
        var single = Collect(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: ["{\"id\":300,\"code\":\"z\",\"v\":1,\"amt\":12.50,\"c\":10}"])
            .EndStatement(101).Build());
        var s = Assert.Single(single.RowChanges);
        Assert.Equal((Pairing.SingleRow, "300"), (s.Pairing, s.Key.Values[0]));
        Assert.Equal(["id", "code"], s.ChangedColumns);
    }

    [Fact]
    public void Keyless_table_and_insert_delete_pairing()
    {
        var r = Collect(new RawBuilder()
            .Firing(101, 2, ChangeOperation.Update, 1, deleted: ["{\"a\":1,\"b\":1}"], inserted: ["{\"a\":1,\"b\":2}"])
            .Firing(102, 2, ChangeOperation.Update, 1, deleted: ["{\"a\":1,\"b\":1}", "{\"a\":2,\"b\":1}"], inserted: ["{\"a\":1,\"b\":3}", "{\"a\":2,\"b\":3}"])
            .Firing(103, 1, ChangeOperation.Insert, 1, inserted: [T1])
            .Firing(104, 2, ChangeOperation.Delete, 1, deleted: ["{\"a\":1,\"b\":1}", "{\"a\":1,\"b\":1}"])
            .EndStatement(104).Build());
        var one = Assert.Single(r.RowChanges, x => x.FiringId == "f-101");
        Assert.Equal((Pairing.SingleRow, KeyKind.ROW), (one.Pairing, one.Key.Kind));
        Assert.Equal(["b"], one.ChangedColumns);
        Assert.All(r.RowChanges.Where(x => x.FiringId == "f-102"), x => Assert.Equal(Pairing.Unpaired, x.Pairing));
        Assert.Equal(4, r.RowChanges.Count(x => x.FiringId == "f-102"));
        Assert.Equal(Pairing.ByPk, Assert.Single(r.RowChanges, x => x.FiringId == "f-103").Pairing);
        Assert.Equal(["c"], r.RowChanges.Single(x => x.FiringId == "f-103").Derived);
        Assert.Equal(2, r.RowChanges.Count(x => x.FiringId == "f-104" && x.Pairing == Pairing.Unpaired));
    }

    [Fact]
    public void Keys_mode_rows_have_keys_only()
    {
        var r = Collect(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: [T1], keys: true).EndStatement(101).Build());
        var x = Assert.Single(r.RowChanges);
        Assert.Equal((Pairing.ByPk, ImageMode.Keys), (x.Pairing, r.Firings[0].ImageMode));
        Assert.Null(x.Before);
        Assert.Null(x.After);
        Assert.Equal(["1"], x.Key.Values);
        Assert.Empty(x.ChangedColumns);
        Assert.Empty(x.Derived);
    }

    [Fact]
    public void Volatile_cells_from_manifest_statement_and_trigger()
    {
        var triggers = new List<TriggerStaticInfo>
        {
            new() { Trigger = "dbo.trg_A", ObjectId = 9, Table = "dbo.A", IsInsteadOf = false, IsDisabled = false, IsEncrypted = false, IsAuditTrigger = false,
                HasDynamicSql = false, HasRollback = false, NondeterministicWrites = [new TableColumn { Table = "dbo.I", Column = "note" }] },
        };
        var analysis = Analysis(new AnalyzedStatement
        {
            Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Update, Target = "dbo.T", Sql = "UPDATE dbo.T SET v = DATEPART(ms, SYSDATETIME())",
            Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2 }, NondeterministicColumns = ["v"],
        });
        var r = Collect(new RawBuilder()
            .Firing(101, 3, ChangeOperation.Insert, 2, inserted: ["{\"id\":7,\"at\":\"2026-11-10T10:00:00.123\",\"note\":\"x\"}"])
            .Firing(102, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":7,\"amt\":12.50,\"c\":70}"])
            .EndStatement(102).Build(), Ctx(triggers: triggers), analysis);
        var ins = r.RowChanges.Single(x => x.FiringId == "f-101");
        Assert.Equal([("id", VolatileReason.Identity), ("at", VolatileReason.NondeterministicDefault), ("note", VolatileReason.TriggerGetdate)],
            ins.Volatile.Select(v => (v.Column, v.Reason)));
        var upd = r.RowChanges.Single(x => x.FiringId == "f-102");
        Assert.Equal([("v", VolatileReason.StatementFunction)], upd.Volatile.Select(v => (v.Column, v.Reason)));
    }

    [Fact]
    public void Statements_are_split_by_boundaries()
    {
        var r = Collect(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: [T1]).EndStatement(101)
            .EndStatement(101) // 0행·감사 없음 문장
            .Firing(102, 4, ChangeOperation.Update, 1, deleted: ["{\"id\":1}"], inserted: ["{\"id\":1}"]).EndStatement(102).Build());
        Assert.Equal([1, 3], r.Firings.Select(f => f.StatementIndex));
    }

    [Fact]
    public void Bracket_assigns_postorder_children_and_none_supplement()
    {
        // 한 문장: 101 d2(F 자식), 102 d1 F=A UPDATE(NONE), 103 d2(F 뒤 기록 = F 또는 G의 자식), 104 d1 G=T UPDATE(LAST), 105 d2 고아(앞 묶음 G는 LAST)
        var raw = new RawBuilder()
            .Firing(101, 2, ChangeOperation.Insert, 2, inserted: ["{\"a\":1,\"b\":1}"])
            .Firing(102, 4, ChangeOperation.Update, 1, deleted: ["{\"id\":1}"], inserted: ["{\"id\":1}"])
            .Firing(103, 2, ChangeOperation.Insert, 2, inserted: ["{\"a\":2,\"b\":1}"])
            .Firing(104, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: [T1])
            .Firing(105, 2, ChangeOperation.Insert, 2, inserted: ["{\"a\":3,\"b\":1}"])
            .EndStatement(105).Build();
        var r = Collect(raw, Ctx(updateOrder: CaptureManifest.None));
        Assert.Equal(AuditOrder.None, r.Firings.Single(f => f.Id == "f-102").AuditOrder);
        var links = r.ContextLinks.ToDictionary(l => l.ChildFiringId);
        Assert.Equal(["f-101", "f-103", "f-105"], r.ContextLinks.Select(l => l.ChildFiringId));
        Link(links["f-101"], ["f-102"], Confidence.Ambiguous);
        Link(links["f-103"], ["f-102", "f-104"], Confidence.Ambiguous);
        Link(links["f-105"], [], Confidence.Inferred);

        // F가 마지막 형제면 뒤에 기록된 자식은 미배정 → 후보 {F}, ambiguous(v1.3 보완)
        var tail = Collect(new RawBuilder()
            .Firing(104, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: [T1])
            .Firing(105, 4, ChangeOperation.Update, 1, deleted: ["{\"id\":1}"], inserted: ["{\"id\":1}"])
            .Firing(106, 2, ChangeOperation.Insert, 2, inserted: ["{\"a\":3,\"b\":1}"])
            .EndStatement(106).Build(), Ctx(updateOrder: CaptureManifest.None));
        var l = Assert.Single(tail.ContextLinks);
        Link(l, ["f-105"], Confidence.Ambiguous);

        // 모두 LAST: 앞 형제 영향 없음, 상한 inferred
        var last = Collect(raw);
        Assert.All(last.ContextLinks.Where(x => x.ChildFiringId != "f-105"), x => Assert.Equal(Confidence.Inferred, x.ConfidenceCap));
        Assert.Equal(["f-104"], last.ContextLinks.Single(x => x.ChildFiringId == "f-103").CandidateParentFiringIds);
    }

    [Fact]
    public void Final_after_is_preorder_last_and_reread_difference_fixes_it()
    {
        // CL08a 모양: 101 d2(같은 행 v 1→2), 102 d1(v 0→1, 바깥 문장). 전위 순서 마지막 = 101.
        RawBuilder Shape() => new RawBuilder()
            .Firing(101, 1, ChangeOperation.Update, 2, deleted: ["{\"id\":1,\"code\":\"a\",\"v\":1,\"amt\":1,\"c\":10}"], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":2,\"amt\":1,\"c\":20}"])
            .Firing(102, 1, ChangeOperation.Update, 1, deleted: ["{\"id\":1,\"code\":\"a\",\"v\":0,\"amt\":1,\"c\":0}"], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":1,\"amt\":1,\"c\":10}"])
            .EndStatement(102);
        var same = Collect(Shape().Reread("dbo.T", "{\"id\":1}", "{\"id\":1,\"code\":\"a\",\"v\":2,\"amt\":1,\"c\":20}").Build());
        Assert.Empty(same.RereadDifferences); // captureSeq 최댓값(102, v=1)과 비교했다면 거짓 잔차
        Assert.All(same.RowChanges, x => Assert.Equal(FinalAfterSource.Audit, x.FinalAfterSource));

        var other = Collect(Shape().Reread("dbo.T", "{\"id\":1}", "{\"id\":1,\"code\":\"a\",\"v\":2,\"amt\":9,\"c\":20}").Build());
        var d = Assert.Single(other.RereadDifferences);
        Diff(d, "rc-101-1", ["amt"], false);
        var fixedRow = other.RowChanges.Single(x => x.Id == "rc-101-1");
        Assert.Equal((FinalAfterSource.Reread, "9"), (fixedRow.FinalAfterSource, fixedRow.After!["amt"]));
        Assert.Equal(["v", "amt", "c"], fixedRow.ChangedColumns);
        Assert.Equal(FinalAfterSource.Audit, other.RowChanges.Single(x => x.Id == "rc-102-1").FinalAfterSource);

        // 재조회에 행이 없으면(그 뒤 삭제) 고치지 않는다
        Assert.Empty(Collect(Shape().Reread("dbo.T", "{\"id\":1}", null).Build()).RereadDifferences);
    }

    [Fact]
    public void Q14_sibling_order_undetermined_uses_reread()
    {
        // 부모 P(A UPDATE, d1, 103) 아래 형제 101·102가 같은 T 행을 바꿈 → 마지막 묶음을 정할 수 없음
        RawBuilder Shape() => new RawBuilder()
            .Firing(101, 1, ChangeOperation.Update, 2, deleted: ["{\"id\":1,\"code\":\"a\",\"v\":0,\"amt\":1,\"c\":0}"], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":1,\"amt\":1,\"c\":10}"])
            .Firing(102, 1, ChangeOperation.Update, 2, deleted: ["{\"id\":1,\"code\":\"a\",\"v\":1,\"amt\":1,\"c\":10}"], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":2,\"amt\":1,\"c\":20}"])
            .Firing(103, 4, ChangeOperation.Update, 1, deleted: ["{\"id\":1}"], inserted: ["{\"id\":1}"])
            .EndStatement(103);
        var matched = Collect(Shape().Reread("dbo.T", "{\"id\":1}", "{\"id\":1,\"code\":\"a\",\"v\":2,\"amt\":1,\"c\":20}").Build());
        Assert.Empty(matched.RereadDifferences); // 모든 열이 어떤 묶음 after와 맞음
        Assert.Equal(FinalAfterSource.Reread, matched.RowChanges.Single(x => x.Id == "rc-102-1").FinalAfterSource); // 대표(전위 마지막)는 재조회 값
        Assert.Equal(FinalAfterSource.Audit, matched.RowChanges.Single(x => x.Id == "rc-101-1").FinalAfterSource);

        var unmatched = Collect(Shape().Reread("dbo.T", "{\"id\":1}", "{\"id\":1,\"code\":\"a\",\"v\":3,\"amt\":1,\"c\":30}").Build());
        var d = Assert.Single(unmatched.RereadDifferences);
        Diff(d, "rc-102-1", ["v", "c"], true);
    }
}
