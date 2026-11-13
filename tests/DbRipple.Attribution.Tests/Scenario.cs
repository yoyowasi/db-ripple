using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.Attribution.Tests;

/// <summary>규칙 단위 테스트용 입력 조립기. 행은 "id=1,a_id=5" 형식(null은 "null").</summary>
internal sealed class Scenario
{
    public CatalogBuilder Cat { get; } = new();
    readonly List<StatementResult> _st = [];
    readonly List<Firing> _f = [];
    readonly List<RowChange> _r = [];
    readonly List<TriggerContextLink> _l = [];
    readonly List<NetChange> _n = [];
    readonly List<RereadDifference> _rd = [];

    public Scenario Statement(int index, ChangeOperation kind, string target, StatementStatus status = StatementStatus.Ok, StatementError? error = null)
    {
        _st.Add(new StatementResult
        {
            Index = index, Batch = 1, Position = index, Kind = kind, Target = target,
            Span = new SourceSpan { StartLine = index, StartColumn = 1, EndLine = index, EndColumn = 10 }, Status = status,
            RowsAffected = status == StatementStatus.Ok ? 1 : null, Error = error,
        });
        return this;
    }

    /// <summary>EXEC 문장(2026-11-13). writes = 프로시저 정적 쓰기 대상. plan = false면 Plan 없음(계약 위반·JSON 복원 흉내).</summary>
    public Scenario Exec(int index, string procedure, string[] writes, StatementStatus status = StatementStatus.Ok, bool plan = true, int? returnValue = 0)
    {
        _st.Add(new StatementResult
        {
            Index = index, Batch = 1, Position = index, Kind = ChangeOperation.Exec, Target = null,
            Span = new SourceSpan { StartLine = index, StartColumn = 1, EndLine = index, EndColumn = 10 }, Status = status,
            ReturnValue = status == StatementStatus.Ok ? returnValue : null, ResultSetCount = status == StatementStatus.Ok ? 0 : null,
            Plan = plan ? new ProcedurePlan { Procedure = procedure, ExecSql = "x", WriteTargets = writes, ExpandedCalls = ["dbo.inner"] } : null,
        });
        return this;
    }

    /// <summary>rows: (before, after). INSERT면 before = null, DELETE면 after = null. 키는 첫 열.</summary>
    public Scenario Firing(long seq, int stmt, int depth, string table, ChangeOperation op, params (string? Before, string? After)[] rows)
        => FiringWith(seq, stmt, depth, table, op, ImageMode.Full, rows);

    public Scenario FiringWith(long seq, int stmt, int depth, string table, ChangeOperation op, ImageMode mode, params (string? Before, string? After)[] rows)
    {
        var id = ContractIds.Firing(seq);
        _f.Add(new Firing
        {
            Id = id, CaptureSeq = seq, StatementIndex = stmt, Depth = depth, Table = table, Operation = op,
            RowCount = rows.Length, ImageMode = mode, AuditOrder = AuditOrder.Last,
        });
        var n = 0;
        foreach (var (b, a) in rows)
        {
            var bd = b is null ? null : Parse(b);
            var ad = a is null ? null : Parse(a);
            var keySrc = (ad ?? bd)!;
            var first = keySrc.First();
            var full = mode == ImageMode.Full;
            _r.Add(new RowChange
            {
                Id = ContractIds.RowChange(seq, ++n), FiringId = id, Operation = op,
                Key = new RowKey { Kind = KeyKind.PK, Columns = [first.Key], Values = [first.Value] },
                Before = full ? bd : null, After = full ? ad : null,
                ChangedColumns = full && bd is not null && ad is not null ? ad.Keys.Where(k => bd.GetValueOrDefault(k) != ad[k]).ToList() : [],
                Pairing = rows.Length == 1 ? Pairing.SingleRow : Pairing.ByPk, FinalAfterSource = FinalAfterSource.Audit,
            });
        }
        return this;
    }

    public Scenario Link(long child, Confidence cap = Confidence.Inferred, params long[] parents)
    {
        _l.Add(new TriggerContextLink { ChildFiringId = ContractIds.Firing(child), CandidateParentFiringIds = parents.Select(ContractIds.Firing).ToList(), ConfidenceCap = cap });
        return this;
    }

    public Scenario Net(string table, NetChangeKind change, string key, string? before = null, string? after = null, params string[] changed)
    {
        var k = Parse(key);
        _n.Add(new NetChange
        {
            Table = table, Key = new RowKey { Kind = KeyKind.PK, Columns = k.Keys.ToList(), Values = k.Values.ToList() }, Change = change, Multiplicity = 1,
            Before = before is null ? null : Parse(before), After = after is null ? null : Parse(after), ChangedColumns = changed,
        });
        return this;
    }

    public Scenario Reread(string rowChangeId, string table, string key, params string[] columns)
    {
        var k = Parse(key);
        _rd.Add(new RereadDifference { RowChangeId = rowChangeId, Table = table, Key = new RowKey { Kind = KeyKind.PK, Columns = k.Keys.ToList(), Values = k.Values.ToList() }, Columns = columns });
        return this;
    }

    public ResolveInput Input(bool recursiveTriggers = false, int nestedTriggers = 1, bool bracket = false) => new()
    {
        Statements = _st,
        Collected = new CollectResult { Firings = _f, RowChanges = _r, ContextLinks = bracket ? CascadeLabCases.Bracket(_f) : _l, RereadDifferences = _rd },
        Net = _n,
        Baseline = Cat.Build(recursiveTriggers, nestedTriggers),
    };

    public Resolution Resolve(bool recursiveTriggers = false, int nestedTriggers = 1, bool bracket = false)
    {
        var r = new CausalResolver().Resolve(Input(recursiveTriggers, nestedTriggers, bracket));
        GraphInvariants.Check(r);
        return r;
    }

    static Dictionary<string, string?> Parse(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).ToDictionary(p => p[0].Trim(), p => p[1].Trim() == "null" ? null : p[1].Trim());
}

/// <summary>모든 결과에 걸리는 불변식(§6.3, §7.3).</summary>
internal static class GraphInvariants
{
    public static void Check(Resolution r)
    {
        var g = r.Graph;
        var ids = g.Nodes.Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var nodes = g.Nodes.ToDictionary(n => n.Id);
        Assert.All(g.Edges, e => { Assert.Contains(e.Source, nodes.Keys); Assert.Contains(e.Target, nodes.Keys); });
        Assert.Equal(Enumerable.Range(1, g.Edges.Count).Select(i => $"e{i}"), g.Edges.Select(e => e.Id));
        foreach (var n in g.Nodes)
        {
            var prefix = n switch { StatementNode or ProcedureCallNode => "st-", FiringNode => "f-", TriggerNode => "t-", ResidualNode => "r-", ErrorNode => "err-", GroupNode => "g-", _ => "?" };
            Assert.StartsWith(prefix, n.Id);
        }
        // WRITES는 그 트리거로 들어오는 FIRES·REPLACES보다 확신도가 높지 않다.
        foreach (var w in g.Edges.Where(e => e.Kind is EdgeKind.Writes or EdgeKind.Residual && nodes[e.Source] is TriggerNode))
        {
            var incoming = g.Edges.Where(e => e.Target == w.Source && e.Kind is EdgeKind.Fires or EdgeKind.Replaces).ToList();
            Assert.NotEmpty(incoming);
            Assert.All(incoming, i => Assert.True((int)w.Confidence >= (int)i.Confidence, $"{w.Id} {w.Confidence} > {i.Id} {i.Confidence}"));
        }
        // 트리거 귀속 최고 등급은 inferred(§7.3).
        Assert.DoesNotContain(g.Edges, e => e.Kind is EdgeKind.Fires or EdgeKind.Writes or EdgeKind.Replaces or EdgeKind.Residual && e.Confidence == Confidence.Certain);
        Assert.DoesNotContain(r.Model.Firings, f => f.Cause!.Kind is CauseKind.Trigger or CauseKind.InsteadOf or CauseKind.Unexplained && f.Cause.Confidence == Confidence.Certain);
        Assert.All(r.Model.Firings, f => { Assert.NotNull(f.Cause); Assert.NotNull(f.OrderGuarantee); });
        Assert.DoesNotContain(r.Model.Residuals, x => x.Confidence == Confidence.Certain);
    }
}
