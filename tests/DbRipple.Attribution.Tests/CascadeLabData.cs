using System.Text.Json;
using System.Text.RegularExpressions;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Attribution.Tests;

/// <summary>저장소 경로.</summary>
internal static class Repo
{
    public static string Root { get; } = Find();

    static string Find()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DbRipple.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("저장소 루트(DbRipple.slnx)를 찾지 못함");
    }

    public static string Path_(params string[] parts) => Path.Combine([Root, .. parts]);
}

/// <summary>테스트용 카탈로그 조립기(Core DbCatalog + C6 TriggerStaticInfo + 매니페스트).</summary>
internal sealed class CatalogBuilder
{
    readonly List<CatalogTable> _tables = [];
    readonly List<CatalogColumn> _columns = [];
    readonly List<CatalogIndexColumn> _keys = [];
    readonly List<CatalogForeignKeyColumn> _fks = [];
    readonly List<CatalogTriggerEvent> _triggers = [];
    readonly List<TriggerStaticInfo> _static = [];
    readonly List<ManifestTable> _manifest = [];
    readonly List<ExcludedTable> _excluded = [];
    readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    int _next = 1000;

    public int Id(string table) => _ids[table];

    /// <summary>열: "name", "name?"(NULL 허용), "name=((0))"(기본값), "name~"(계산 열). 첫 pk 열들이 PK.</summary>
    public CatalogBuilder Table(string table, string[] pk, params string[] cols)
    {
        var (schema, name) = Split(table);
        var id = _next++;
        _ids[table] = id;
        _tables.Add(new CatalogTable(id, schema, name, TemporalType.None, false));
        var n = 0;
        foreach (var raw in cols)
        {
            var c = raw;
            string? def = null;
            var eq = c.IndexOf('=');
            if (eq >= 0) { def = c[(eq + 1)..]; c = c[..eq]; }
            var nullable = c.EndsWith('?');
            var computed = c.EndsWith('~');
            c = c.TrimEnd('?', '~');
            _columns.Add(new CatalogColumn(id, ++n, c, "int", false, nullable, computed, false, false, def));
        }
        for (var i = 0; i < pk.Length; i++) _keys.Add(new CatalogIndexColumn(id, 1, "PK_" + name, true, true, i + 1, pk[i], false));
        _manifest.Add(new ManifestTable(table, true, new AuditOrderInfo(CaptureManifest.Last, CaptureManifest.Last, CaptureManifest.Last), [], []));
        // 감사 트리거(C9)도 카탈로그·정적 분석에 들어 있다. C13은 이것을 걸러야 한다.
        foreach (var (op, ev) in new[] { ("I", "INSERT"), ("U", "UPDATE"), ("D", "DELETE") })
        {
            var tid = _next++;
            var tn = $"dbr_audit_{name}_{op}";
            _triggers.Add(new CatalogTriggerEvent(tid, tn, id, "OBJECT_OR_COLUMN", false, false, ev, false, true, "audit"));
            _static.Add(new TriggerStaticInfo
            {
                Trigger = $"{schema}.{tn}", ObjectId = tid, Table = table, Events = [ParseOp(ev)], IsInsteadOf = false, IsDisabled = false,
                IsEncrypted = false, IsAuditTrigger = true, HasDynamicSql = false, HasRollback = false,
            });
        }
        return this;
    }

    public CatalogBuilder Fk(string name, string child, string childCol, string parent, string parentCol, string onDelete = "NO_ACTION", string onUpdate = "NO_ACTION", bool disabled = false)
    {
        _fks.Add(new CatalogForeignKeyColumn(_next++, name, _ids[child], _ids[parent], onDelete, onUpdate, disabled, false, 1, childCol, parentCol));
        return this;
    }

    public CatalogBuilder Trigger(string trigger, string table, ChangeOperation[] events, string[] writes,
        bool insteadOf = false, bool dynamicSql = false, bool encrypted = false, bool unparsed = false, bool disabled = false, bool staticInfo = true,
        string[]? procedureCalls = null)
    {
        var tid = _next++;
        var (_, tn) = Split(trigger);
        foreach (var e in events)
            _triggers.Add(new CatalogTriggerEvent(tid, tn, _ids[table], "OBJECT_OR_COLUMN", insteadOf, disabled, e.ToString().ToUpperInvariant(), false, false, encrypted ? null : "body"));
        if (staticInfo)
            _static.Add(new TriggerStaticInfo
            {
                Trigger = trigger, ObjectId = tid, Table = table, Events = events, IsInsteadOf = insteadOf, IsDisabled = disabled,
                IsEncrypted = encrypted, IsAuditTrigger = false, WriteTargets = writes, WriteTargetsUnknown = encrypted || unparsed, HasDynamicSql = dynamicSql || encrypted || unparsed,
                HasRollback = false, ExternalSideEffects = unparsed ? ["unparsed"] : [], ProcedureCalls = procedureCalls ?? [],
            });
        return this;
    }

    public CatalogBuilder Exclude(string table, string reason = "OWNER_NOT_DBO")
    {
        _excluded.Add(new ExcludedTable(table, reason));
        _manifest.RemoveAll(m => string.Equals(m.Table, table, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    public CatalogBuilder Unobservable(string table, params string[] cols)
    {
        var i = _manifest.FindIndex(m => string.Equals(m.Table, table, StringComparison.OrdinalIgnoreCase));
        _manifest[i] = _manifest[i] with { UnobservableColumns = cols };
        return this;
    }

    public BaselineContext Build(bool recursiveTriggers = false, int nestedTriggers = 1) => new()
    {
        OriginDb = "CascadeLab", SandboxDb = "CascadeLab_Sandbox", SnapshotDb = "CascadeLab_Sandbox_base",
        BackupFinishedAt = DateTimeOffset.Parse("2026-11-10T00:00:00+09:00"),
        Fingerprint = new Fingerprint("17.0.5035.5", "Enterprise Developer Edition (64-bit)", 170, recursiveTriggers, nestedTriggers, 0, 5432, "SQL_Latin1_General_CP1_CI_AS"),
        FingerprintMatch = true,
        Catalog = new DbCatalog(_tables, _columns, _keys, _fks, _triggers, []),
        Triggers = _static,
        Manifest = new CaptureManifest("test", _manifest, _excluded, []),
    };

    static (string, string) Split(string n) { var i = n.IndexOf('.'); return (n[..i], n[(i + 1)..]); }

    public static ChangeOperation ParseOp(string s) => s.ToUpperInvariant() switch
    {
        "INSERT" or "I" => ChangeOperation.Insert,
        "UPDATE" or "U" => ChangeOperation.Update,
        _ => ChangeOperation.Delete,
    };

    /// <summary>CascadeLab 카탈로그(cascadelab/sql/cl01~cl08.sql을 손으로 옮김).</summary>
    public static CatalogBuilder CascadeLab()
    {
        var b = new CatalogBuilder();
        string[] id = ["id"];
        b.Table("cl01.A", id, "id", "name").Table("cl01.B", id, "id", "a_id").Table("cl01.C", id, "id", "b_id").Table("cl01.D", id, "id", "c_id")
         .Fk("FK_cl01_B_A", "cl01.B", "a_id", "cl01.A", "id", "CASCADE")
         .Fk("FK_cl01_C_B", "cl01.C", "b_id", "cl01.B", "id", "CASCADE")
         .Fk("FK_cl01_D_C", "cl01.D", "c_id", "cl01.C", "id", "CASCADE");
        b.Table("cl02.Dept", id, "id", "name").Table("cl02.Emp", id, "id", "dept_id?", "name")
         .Fk("FK_cl02_Emp_Dept", "cl02.Emp", "dept_id", "cl02.Dept", "id", "SET_NULL");
        b.Table("cl03.Category", id, "id", "name").Table("cl03.Product", id, "id", "category_id=((0))")
         .Table("cl03.Grp", id, "id").Table("cl03.Item", id, "id", "grp_id=((99))")
         .Fk("FK_cl03_Product_Category", "cl03.Product", "category_id", "cl03.Category", "id", "SET_DEFAULT")
         .Fk("FK_cl03_Item_Grp", "cl03.Item", "grp_id", "cl03.Grp", "id", "SET_DEFAULT");
        b.Table("cl04.P", id, "id", "name").Table("cl04.Ch", id, "id", "p_id")
         .Fk("FK_cl04_Ch_P", "cl04.Ch", "p_id", "cl04.P", "id", onUpdate: "CASCADE");
        b.Table("cl05.P", id, "id").Table("cl05.X", id, "id", "p_id").Table("cl05.X2", id, "id", "x_id")
         .Table("cl05.Y", id, "id", "p_id").Table("cl05.Y2", id, "id", "y_id")
         .Fk("FK_cl05_X_P", "cl05.X", "p_id", "cl05.P", "id", "CASCADE")
         .Fk("FK_cl05_X2_X", "cl05.X2", "x_id", "cl05.X", "id", "CASCADE")
         .Fk("FK_cl05_Y_P", "cl05.Y", "p_id", "cl05.P", "id", "CASCADE")
         .Fk("FK_cl05_Y2_Y", "cl05.Y2", "y_id", "cl05.Y", "id", "CASCADE");
        b.Table("cl06.Ord", id, "id").Table("cl06.Line", id, "id", "ord_id", "qty").Table("cl06.LineLog", ["line_id"], "line_id", "action")
         .Fk("FK_cl06_Line_Ord", "cl06.Line", "ord_id", "cl06.Ord", "id", "CASCADE")
         .Trigger("cl06.trg_Line_del", "cl06.Line", [ChangeOperation.Delete], ["cl06.LineLog"]);
        b.Table("cl07.A", id, "id", "v").Table("cl07.B", id, "id", "a_id", "total").Table("cl07.C", ["b_id"], "b_id", "note").Table("cl07.D", id, "id", "cnt")
         .Trigger("cl07.trg_A_upd", "cl07.A", [ChangeOperation.Update], ["cl07.B"])
         .Trigger("cl07.trg_B_upd", "cl07.B", [ChangeOperation.Update], ["cl07.C"])
         .Trigger("cl07.trg_C_ins", "cl07.C", [ChangeOperation.Insert], ["cl07.D"]);
        b.Table("cl08.Acct", id, "id", "bal", "touched=((0))")
         .Trigger("cl08.trg_Acct_upd", "cl08.Acct", [ChangeOperation.Update], ["cl08.Acct"]);
        return b;
    }
}

/// <summary>손 정답 간선 하나(answer-format.md edges).</summary>
internal sealed record ExpectedEdge(int StatementIndex, string Parent, string Child, string Kind, string? Name, string Confidence, bool? Observed)
{
    public string Key => $"{StatementIndex}|{Parent}|{Child}|{Kind}|{Name}";
}

/// <summary>사례 하나의 입력과 손 정답.</summary>
internal sealed record CaseData(string Id, ResolveInput Input, IReadOnlyList<ExpectedEdge> Edges, IReadOnlyList<(string Node, string CauseKind)> Firings);

/// <summary>
/// CascadeLab 사례 입력 합성. C10·C11 픽스처가 나오기 전에는 1주차 실DB 감사 기록(cascadelab/verification/logs/audit-2026-11-10.json:
/// 헤더·행 이미지·순 변경·문장 결과)과 손 카탈로그로 CollectResult를 만든다. 괄호(§7.2 2단계)는 C11 대역으로 최소 구현을 둔다.
/// </summary>
internal static class CascadeLabCases
{
    public static readonly string[] Ids = ["CL01", "CL02", "CL03", "CL03b", "CL04", "CL04b", "CL05", "CL06", "CL07", "CL08a", "CL08b"];

    static readonly Lazy<JsonDocument> Audit = new(() =>
        JsonDocument.Parse(File.ReadAllText(Repo.Path_("cascadelab", "verification", "logs", "audit-2026-11-10.json"))));

    public static CaseData Load(string id)
    {
        var caseFile = Directory.GetFiles(Repo.Path_("cascadelab", "cases"), id + "-*.json").Single();
        using var cdoc = JsonDocument.Parse(File.ReadAllText(caseFile));
        var c = cdoc.RootElement;
        var rt = c.GetProperty("settings").GetProperty("RECURSIVE_TRIGGERS").GetString() == "ON";
        var baseline = CatalogBuilder.CascadeLab().Build(recursiveTriggers: rt);

        var a = Audit.Value.RootElement.GetProperty("Cases").EnumerateArray().Single(x => x.GetProperty("Id").GetString() == id);
        var input = FromAudit(a, baseline);
        return new CaseData(id, input, ExpectedEdges(c), ExpectedFirings(c));
    }

    public static IReadOnlyList<ExpectedEdge> ExpectedEdges(JsonElement c) =>
        c.GetProperty("expected").GetProperty("edges").EnumerateArray().Select(e => new ExpectedEdge(
            e.GetProperty("statementIndex").GetInt32(), e.GetProperty("parent").GetString()!, e.GetProperty("child").GetString()!,
            e.GetProperty("kind").GetString()!, e.GetProperty("name").ValueKind == JsonValueKind.Null ? null : e.GetProperty("name").GetString(),
            e.GetProperty("confidence").GetString()!,
            e.TryGetProperty("observed", out var o) ? o.GetBoolean() : null)).ToList();

    static IReadOnlyList<(string, string)> ExpectedFirings(JsonElement c) =>
        c.GetProperty("expected").TryGetProperty("firings", out var f)
            ? f.EnumerateArray().Select(x => (x.GetProperty("node").GetString()!, x.GetProperty("causeKind").GetString()!)).ToList()
            : [];

    static readonly Regex StmtRe = new(@"^\s*(DELETE\s+FROM|UPDATE|INSERT\s+INTO)\s+([\w\.]+)", RegexOptions.IgnoreCase);
    static readonly Regex ImageRe = new(@"^(DELETED|INSERTED) key=(\{.*?\}) row=(\{.*\})$");

    static ResolveInput FromAudit(JsonElement a, BaselineContext baseline)
    {
        var statements = new List<StatementResult>();
        foreach (var s in a.GetProperty("Statements").EnumerateArray())
        {
            var sql = s.GetProperty("Sql").GetString()!;
            var m = StmtRe.Match(sql);
            var status = s.GetProperty("Status").GetString() switch
            {
                "failed" => StatementStatus.Failed, "trigger_blocked" => StatementStatus.TriggerBlocked, "not_run" => StatementStatus.NotRun, _ => StatementStatus.Ok,
            };
            var err = s.GetProperty("Errors").EnumerateArray().FirstOrDefault(e => e.GetProperty("Class").GetInt32() > 10);
            statements.Add(new StatementResult
            {
                Index = s.GetProperty("Index").GetInt32(), Batch = 1, Position = s.GetProperty("Index").GetInt32(),
                Kind = CatalogBuilder.ParseOp(m.Groups[1].Value.Split(' ')[0]), Target = m.Groups[2].Value,
                Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = sql.Length },
                Status = status,
                RowsAffected = s.TryGetProperty("RowsAffected", out var ra) && ra.ValueKind == JsonValueKind.Number ? ra.GetInt64() : null,
                Error = err.ValueKind == JsonValueKind.Undefined ? null : new StatementError
                {
                    ErrorNumber = err.GetProperty("Number").GetInt32(), Message = err.GetProperty("Message").GetString()!, IsToolFault = false,
                },
            });
        }

        var firings = new List<Firing>();
        var rcs = new List<RowChange>();
        foreach (var f in a.GetProperty("Firings").EnumerateArray())
        {
            var seq = f.GetProperty("Seq").GetInt64();
            var op = CatalogBuilder.ParseOp(f.GetProperty("Operation").GetString()!);
            var firing = new Firing
            {
                Id = ContractIds.Firing(seq), CaptureSeq = seq, StatementIndex = 1, Depth = f.GetProperty("NestLevel").GetInt32(),
                Table = f.GetProperty("Table").GetString()!, Operation = op, RowCount = f.GetProperty("RowCount").GetInt64(),
                ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
            };
            firings.Add(firing);
            var images = f.GetProperty("Images").EnumerateArray().Select(i => ImageRe.Match(i.GetString()!))
                .Select(m => (Kind: m.Groups[1].Value, Key: Obj(m.Groups[2].Value), Row: Obj(m.Groups[3].Value))).ToList();
            rcs.AddRange(BuildRows(firing, images));
        }

        var net = new List<NetChange>();
        foreach (var n in a.GetProperty("NetChanges").EnumerateArray())
        {
            var table = n.GetProperty("Table").GetString()!;
            var cols = n.GetProperty("KeyColumns").EnumerateArray().Select(x => x.GetString()!).ToList();
            RowKey K(string json) { var d = Obj(json); return new RowKey { Kind = KeyKind.PK, Columns = cols, Values = cols.Select(c => d[c]).ToList() }; }
            foreach (var k in n.GetProperty("Deleted").EnumerateArray())
                net.Add(new NetChange { Table = table, Key = K(k.GetString()!), Change = NetChangeKind.Deleted, Multiplicity = 1, Before = Obj(k.GetString()!) });
            foreach (var k in n.GetProperty("Inserted").EnumerateArray())
                net.Add(new NetChange { Table = table, Key = K(k.GetString()!), Change = NetChangeKind.Inserted, Multiplicity = 1, After = Obj(k.GetString()!) });
            foreach (var u in n.GetProperty("Updated").EnumerateArray())
                net.Add(new NetChange
                {
                    Table = table, Key = K(u.GetProperty("Key").GetString()!), Change = NetChangeKind.Updated, Multiplicity = 1,
                    Before = Dict(u.GetProperty("Before")), After = Dict(u.GetProperty("After")),
                    ChangedColumns = u.GetProperty("ChangedColumns").EnumerateArray().Select(x => x.GetString()!).ToList(),
                });
        }

        var collected = new CollectResult { Firings = firings, RowChanges = rcs, ContextLinks = Bracket(firings) };
        return new ResolveInput { Statements = statements, Collected = collected, Net = net, Baseline = baseline };
    }

    /// <summary>행 이미지 → 행 변경(C11 짝짓기의 단순판: 키가 같으면 BY_PK, 한 행이면 SINGLE_ROW, 아니면 UNPAIRED로 따로 둔다).</summary>
    static IEnumerable<RowChange> BuildRows(Firing f, List<(string Kind, Dictionary<string, string?> Key, Dictionary<string, string?> Row)> images)
    {
        var n = 0;
        RowKey Key(Dictionary<string, string?> k) => new() { Kind = KeyKind.PK, Columns = k.Keys.ToList(), Values = k.Values.ToList() };
        string KS(Dictionary<string, string?> k) => string.Join("|", k.Values);
        var del = images.Where(i => i.Kind == "DELETED").ToList();
        var ins = images.Where(i => i.Kind == "INSERTED").ToList();
        RowChange Rc(RowKey key, Dictionary<string, string?>? b, Dictionary<string, string?>? a, Pairing p) => new()
        {
            Id = ContractIds.RowChange(f.CaptureSeq, ++n), FiringId = f.Id, Operation = f.Operation, Key = key, Before = b, After = a,
            ChangedColumns = b is not null && a is not null ? a.Keys.Where(c => b.GetValueOrDefault(c) != a[c]).ToList() : [],
            Pairing = p, FinalAfterSource = FinalAfterSource.Audit,
        };
        var single = images.Count <= 2 && del.Count <= 1 && ins.Count <= 1;
        if (f.Operation == ChangeOperation.Delete) { foreach (var d in del) yield return Rc(Key(d.Key), d.Row, null, single ? Pairing.SingleRow : Pairing.ByPk); yield break; }
        if (f.Operation == ChangeOperation.Insert) { foreach (var i in ins) yield return Rc(Key(i.Key), null, i.Row, single ? Pairing.SingleRow : Pairing.ByPk); yield break; }
        var byKey = ins.ToDictionary(i => KS(i.Key));
        if (del.All(d => byKey.ContainsKey(KS(d.Key))))
        {
            foreach (var d in del) yield return Rc(Key(d.Key), d.Row, byKey[KS(d.Key)].Row, single ? Pairing.SingleRow : Pairing.ByPk);
        }
        else if (single)
        {
            yield return Rc(Key(ins[0].Key), del[0].Row, ins[0].Row, Pairing.SingleRow);
        }
        else
        {
            foreach (var d in del) yield return Rc(Key(d.Key), d.Row, null, Pairing.Unpaired);
            foreach (var i in ins) yield return Rc(Key(i.Key), null, i.Row, Pairing.Unpaired);
        }
    }

    /// <summary>
    /// §7.2 2단계 괄호의 최소 대역(C11 대신, 감사 순서 LAST만 가정): captureSeq 순으로 훑어 깊이 d 묶음이 나오면 그때까지 쌓인 깊이 d+1 미배정 묶음을 자식으로 잡는다.
    /// 더 얕은 묶음이 끼면 그 아래 미배정은 부모 없음(빈 후보)으로 끝낸다.
    /// </summary>
    public static IReadOnlyList<TriggerContextLink> Bracket(IReadOnlyList<Firing> firings)
    {
        var links = new List<TriggerContextLink>();
        foreach (var st in firings.GroupBy(f => f.StatementIndex))
        {
            var pending = new Dictionary<int, List<Firing>>();
            void Orphan(int depth)
            {
                if (!pending.TryGetValue(depth, out var l)) return;
                foreach (var x in l) if (x.Depth >= 2) links.Add(new TriggerContextLink { ChildFiringId = x.Id, CandidateParentFiringIds = [], ConfidenceCap = Confidence.Inferred });
                l.Clear();
            }
            foreach (var f in st.OrderBy(f => f.CaptureSeq))
            {
                if (pending.TryGetValue(f.Depth + 1, out var kids))
                {
                    foreach (var k in kids) links.Add(new TriggerContextLink { ChildFiringId = k.Id, CandidateParentFiringIds = [f.Id], ConfidenceCap = Confidence.Inferred });
                    kids.Clear();
                }
                foreach (var d in pending.Keys.Where(d => d > f.Depth + 1).ToList()) Orphan(d);
                if (!pending.TryGetValue(f.Depth, out var mine)) pending[f.Depth] = mine = [];
                mine.Add(f);
            }
            foreach (var d in pending.Keys.ToList()) Orphan(d);
        }
        return links;
    }

    static Dictionary<string, string?> Obj(string json)
    {
        using var d = JsonDocument.Parse(json);
        return Dict(d.RootElement);
    }

    static Dictionary<string, string?> Dict(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => p.Value.GetString(),
            _ => p.Value.GetRawText(),
        });
}

/// <summary>그래프 → M3 간선 키(answer-format.md 노드 참조 표기). 같은 표기가 문장 안에 여럿이면 인과 순(깊이, captureSeq)으로 #k를 붙인다.</summary>
internal static class M3
{
    public sealed record Edge(string Key, string Confidence, bool? Observed);

    public static IReadOnlyList<Edge> Keys(Resolution r)
    {
        var g = r.Graph;
        var firings = r.Model.Firings.ToDictionary(f => f.Id);
        var nodes = g.Nodes.ToDictionary(n => n.Id);
        var labels = new Dictionary<string, string>();
        var stmtOf = new Dictionary<string, int>();

        foreach (var n in g.Nodes)
        {
            switch (n)
            {
                case StatementNode s: labels[n.Id] = "S"; stmtOf[n.Id] = s.StatementIndex; break;
                case ErrorNode e: labels[n.Id] = $"E:{e.ErrorNumber}"; stmtOf[n.Id] = int.Parse(n.Id["err-".Length..]); break;
                case ResidualNode rn: labels[n.Id] = $"R:{rn.Table}"; break;
                case GroupNode gn: labels[n.Id] = $"G:{gn.Id}"; break;
            }
        }
        foreach (var grp in g.Nodes.OfType<FiringNode>().GroupBy(f => (firings[f.Id].StatementIndex, $"F:{f.Table}:{f.Operation.ToString().ToUpperInvariant()}")))
        {
            var list = grp.OrderBy(f => f.Depth).ThenBy(f => f.CaptureSeq).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                labels[list[i].Id] = list.Count == 1 ? grp.Key.Item2 : $"{grp.Key.Item2}#{i + 1}";
                stmtOf[list[i].Id] = grp.Key.StatementIndex;
            }
        }
        (int, long) Order(TriggerNode t) => t.ParentFiringId is { } p && firings.TryGetValue(p, out var f) ? (f.Depth, f.CaptureSeq) : (0, 0);
        foreach (var grp in g.Nodes.OfType<TriggerNode>().GroupBy(t => (t.StatementIndex, $"T:{t.Trigger}")))
        {
            var list = grp.OrderBy(Order).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                labels[list[i].Id] = list.Count == 1 ? grp.Key.Item2 : $"{grp.Key.Item2}#{i + 1}";
                stmtOf[list[i].Id] = grp.Key.StatementIndex;
            }
        }

        return g.Edges.Select(e =>
        {
            var src = nodes[e.Source];
            var dst = nodes[e.Target];
            string? name = e.Kind switch
            {
                EdgeKind.Fk => e.Fk,
                EdgeKind.Fires or EdgeKind.Replaces => (dst as TriggerNode)?.Trigger,
                EdgeKind.Writes or EdgeKind.Residual => (src as TriggerNode)?.Trigger,
                _ => null,
            };
            var si = stmtOf.TryGetValue(e.Source, out var a) ? a : stmtOf.GetValueOrDefault(e.Target);
            var kind = JsonSerializer.Serialize(e.Kind, ContractJson.Options).Trim('"');
            var conf = JsonSerializer.Serialize(e.Confidence, ContractJson.Options).Trim('"');
            bool? observed = e.Kind == EdgeKind.Fires && dst is TriggerNode tn ? tn.Observed : null;
            return new Edge($"{si}|{labels[e.Source]}|{labels[e.Target]}|{kind}|{name}", conf, observed);
        }).ToList();
    }

    /// <summary>손 정답은 실행이 한 번뿐이어도 "#1"을 붙인 곳이 있다(CL08a T:…#1). 같은 바탕 표기에 #2가 없으면 #1을 뗀다.</summary>
    public static string NormalizeKey(string key, IEnumerable<string> allKeys)
    {
        var parts = key.Split('|');
        var all = allKeys.SelectMany(k => k.Split('|').Skip(1).Take(2)).ToHashSet();
        for (var i = 1; i <= 2; i++)
            if (parts[i].EndsWith("#1") && !all.Contains(parts[i][..^2] + "#2")) parts[i] = parts[i][..^2];
        return string.Join('|', parts);
    }
}
