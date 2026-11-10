using DbRipple.Contracts;

namespace DbRipple.Attribution;

/// <summary>
/// §6.3 그래프 조립. 노드는 발동 단위(문장, 묶음, 트리거 실행, 잔차, 오류)이고 간선 7종을 만든다.
/// WRITES·RESIDUAL(트리거 출발) 확신도는 FIRES·REPLACES(inferred)를 넘지 않는다. 변경을 남기지 않은 트리거 실행은 observed = false.
/// 노드·간선 순서: 문장 순, 문장 안은 인과 순(깊이 오름차순, captureSeq 오름차순), 잔차는 끝.
/// </summary>
internal static class GraphBuilder
{
    const Confidence TriggerMax = Confidence.Inferred; // FIRES·REPLACES 확신도(카탈로그 추론, §7.3 트리거 귀속 상한)

    /// <summary>statement 노드 label 최대 길이(줄임표 제외).</summary>
    public const int LabelMaxLength = 80;

    /// <summary>
    /// §6.3 "문장 앞부분": 공백 문자열(줄바꿈 포함)을 공백 하나로 접고 앞뒤를 자른 뒤, <see cref="LabelMaxLength"/>자를 넘으면
    /// 그 안의 마지막 공백에서 자르고(값·이름 중간에서 자르면 다른 값처럼 보인다: "= 4365 …") " …"를 붙인다. 끝 세미콜론은 뺀다. 빈 문장이면 null.
    /// </summary>
    public static string? StatementLabel(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';').TrimEnd();
        if (flat.Length == 0) return null;
        if (flat.Length <= LabelMaxLength) return flat;
        var cut = flat.LastIndexOf(' ', LabelMaxLength);
        return flat[..(cut > 0 ? cut : LabelMaxLength)].TrimEnd() + " …";
    }

    public static GraphDto Build(
        IReadOnlyList<StatementResult> statements,
        IReadOnlyList<int> statementOrder,
        IReadOnlyList<Firing> firings,
        IReadOnlyDictionary<string, Verdict> verdicts,
        IReadOnlyList<(string? ParentFiringId, int StatementIndex, TriggerInfo Trigger)> executions,
        IReadOnlyList<Residual> residuals,
        IReadOnlyList<ResidualSource> sources,
        IReadOnlyDictionary<int, string>? statementSql = null)
    {
        var stmts = statements.GroupBy(s => s.Index).ToDictionary(g => g.Key, g => g.First());

        // 트리거 실행 노드 id를 먼저 정한다(t-1부터, 실행 목록 순).
        var execId = new Dictionary<(string Parent, string Trigger), string>();
        var execList = new List<(string Id, string? Parent, int Stmt, TriggerInfo Trigger)>();
        foreach (var (parent, si, t) in executions)
        {
            var key = (parent ?? ContractIds.StatementNode(si), t.Name.ToUpperInvariant());
            if (execId.ContainsKey(key)) continue;
            var id = ContractIds.TriggerNode(execList.Count + 1);
            execId[key] = id;
            execList.Add((id, parent, si, t));
        }

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var observed = new HashSet<string>();
        var pendingTriggerNodes = new List<(int Index, string Id)>();

        void Edge(string source, string target, EdgeKind kind, Confidence c, OrderGuarantee o, IEnumerable<Evidence> ev,
                  string? fk = null, FkAction? action = null, long? matched = null) =>
            edges.Add(new GraphEdge
            {
                Id = "", Source = source, Target = target, Kind = kind, Confidence = c, OrderGuarantee = o,
                Evidence = ev.Distinct().ToList(), Fk = fk, Action = action, MatchedRows = matched,
            });

        void TriggerNodesOf(string? parentFiringId, int si)
        {
            foreach (var e in execList.Where(e => e.Parent == parentFiringId && e.Stmt == si))
            {
                pendingTriggerNodes.Add((nodes.Count, e.Id));
                nodes.Add(null!); // 자리만 잡고 observed를 안 뒤 채운다.
            }
        }

        foreach (var si in statementOrder)
        {
            var stId = ContractIds.StatementNode(si);
            var hasStatement = stmts.TryGetValue(si, out var st);
            if (hasStatement)
            {
                var label = statementSql is not null && statementSql.TryGetValue(si, out var sql) ? StatementLabel(sql) : null;
                nodes.Add(new StatementNode
                {
                    Id = stId, StatementIndex = si,
                    Label = label ?? $"{st!.Kind.ToString().ToUpperInvariant()} {st.Target}",
                    RowsAffected = st!.RowsAffected, Status = st.Status,
                });
                if (st.Status is StatementStatus.Failed or StatementStatus.TriggerBlocked || st.Error is not null)
                {
                    var errId = ContractIds.ErrorNode(si);
                    nodes.Add(new ErrorNode
                    {
                        Id = errId, ErrorNumber = st.Error?.ErrorNumber ?? 0,
                        Message = st.Error?.Message ?? st.Status.ToString(), IsToolFault = st.Error?.IsToolFault ?? false,
                    });
                    Edge(stId, errId, EdgeKind.Blocked, Confidence.Certain, OrderGuarantee.Guaranteed, []);
                }
                foreach (var e in execList.Where(e => e.Parent is null && e.Stmt == si))
                    Edge(stId, e.Id, EdgeKind.Replaces, TriggerMax, OrderGuarantee.Guaranteed, [Evidence.CatalogInsteadOf]);
                TriggerNodesOf(null, si);
            }

            foreach (var f in firings.Where(f => f.StatementIndex == si).OrderBy(f => f.Depth).ThenBy(f => f.CaptureSeq))
            {
                nodes.Add(new FiringNode
                {
                    Id = f.Id, FiringId = f.Id, Table = f.Table, Operation = f.Operation,
                    RowCount = f.RowCount, Depth = f.Depth, CaptureSeq = f.CaptureSeq,
                });
                if (verdicts.TryGetValue(f.Id, out var v))
                {
                    if (v.Kind == CauseKind.Direct && hasStatement)
                        Edge(stId, f.Id, EdgeKind.Direct, Confidence.Certain, OrderGuarantee.Guaranteed, v.Evidence);
                    foreach (var l in v.FkLinks)
                        Edge(l.ParentFiringId, f.Id, EdgeKind.Fk, l.Confidence, v.Order, [Evidence.FkKeyMatch], l.Fk.Name, l.Action, l.MatchedRows);
                    foreach (var p in v.TriggerPairs)
                    {
                        if (!execId.TryGetValue((p.ParentFiringId ?? ContractIds.StatementNode(p.StatementIndex), p.Trigger.ToUpperInvariant()), out var tid)) continue;
                        var c = Conf.Weaker(v.WritesConfidence, TriggerMax);
                        var o = v.Kind is CauseKind.Trigger or CauseKind.InsteadOf && c == Confidence.Inferred ? OrderGuarantee.Guaranteed : OrderGuarantee.ObservedOnly;
                        Evidence[] ev = p.ParentFiringId is null
                            ? [Evidence.Depth, Evidence.CatalogInsteadOf, Evidence.StaticWriteSet]
                            : [Evidence.Depth, Evidence.PostorderBracket, Evidence.StaticWriteSet];
                        Edge(tid, f.Id, EdgeKind.Writes, c, o, ev);
                        observed.Add(tid);
                    }
                }
                foreach (var e in execList.Where(e => e.Parent == f.Id))
                    Edge(f.Id, e.Id, EdgeKind.Fires, TriggerMax, OrderGuarantee.Guaranteed, []);
                TriggerNodesOf(f.Id, si);
            }
        }

        foreach (var r in residuals)
        {
            nodes.Add(new ResidualNode { Id = r.Id, ResidualId = r.Id, Table = r.Table, Category = r.Category });
            Evidence ev = r.DetectedBy == ResidualDetectedBy.Reread ? Evidence.Reread : Evidence.NetDiff;
            var seen = new HashSet<string>();
            foreach (var s in sources.Where(s => s.ResidualId == r.Id))
            {
                if (s.Trigger is not null)
                {
                    foreach (var e in execList.Where(e => CatalogIndex.Names.Equals(e.Trigger.Name, s.Trigger)))
                        if (seen.Add(e.Id)) Edge(e.Id, r.Id, EdgeKind.Residual, Conf.Weaker(r.Confidence, TriggerMax), OrderGuarantee.ObservedOnly, [ev]);
                }
                else if (s.FiringId is not null && seen.Add(s.FiringId))
                    Edge(s.FiringId, r.Id, EdgeKind.Residual, Conf.Weaker(r.Confidence, Confidence.Inferred), OrderGuarantee.ObservedOnly, [ev]);
            }
        }

        foreach (var (index, id) in pendingTriggerNodes)
        {
            var e = execList.First(x => x.Id == id);
            nodes[index] = new TriggerNode
            {
                Id = id, Trigger = e.Trigger.Name, ParentFiringId = e.Parent, StatementIndex = e.Stmt, Observed = observed.Contains(id),
            };
        }

        return new GraphDto { Nodes = nodes, Edges = edges.Select((e, i) => e with { Id = ContractIds.Edge(i + 1) }).ToList() };
    }
}

/// <summary>
/// 노드가 200개를 넘으면 같은 (문장, 테이블, 연산, 깊이) 묶음 노드를 group(g-n)으로 접는다(§6.3, D8).
/// 설계는 "같은 테이블·연산·깊이"이고, 여기서는 DIRECT 간선이 문장마다 남도록 문장 번호까지 같은 것끼리만 접는다.
/// 접힌 묶음이 발동한 같은 이름의 트리거 실행도 하나로 합친다. 겹친 간선은 약한 확신도·observed_only 쪽으로 합치고 일치 행 수는 더한다.
/// </summary>
internal static class GraphFolder
{
    public static GraphDto Fold(GraphDto graph, IReadOnlyList<Firing> firings)
    {
        var stmtOf = firings.ToDictionary(f => f.Id, f => f.StatementIndex);
        var groups = graph.Nodes.OfType<FiringNode>()
            .GroupBy(n => (stmtOf.TryGetValue(n.Id, out var s) ? s : 0, n.Table.ToUpperInvariant(), n.Operation, n.Depth))
            .Where(g => g.Count() >= 2)
            .ToList();
        if (groups.Count == 0) return graph;

        var map = new Dictionary<string, string>();
        var groupNodes = new Dictionary<string, GroupNode>(); // 첫 멤버 id → 그룹 노드
        var n = 0;
        foreach (var g in groups)
        {
            var gid = ContractIds.GroupNode(++n);
            var members = g.ToList();
            foreach (var m in members) map[m.Id] = gid;
            groupNodes[members[0].Id] = new GroupNode { Id = gid, Members = members.Select(m => m.Id).ToList(), RowCount = members.Sum(m => m.RowCount) };
        }

        var triggerKeep = new Dictionary<(string Group, string Trigger), string>();
        var triggerObserved = new Dictionary<string, bool>();
        foreach (var t in graph.Nodes.OfType<TriggerNode>())
        {
            if (t.ParentFiringId is null || !map.TryGetValue(t.ParentFiringId, out var gid)) continue;
            var key = (gid, t.Trigger.ToUpperInvariant());
            if (triggerKeep.TryGetValue(key, out var keep)) map[t.Id] = keep;
            else triggerKeep[key] = t.Id;
            var kid = triggerKeep[key];
            triggerObserved[kid] = (triggerObserved.TryGetValue(kid, out var o) && o) || t.Observed;
        }

        var nodes = new List<GraphNode>();
        foreach (var node in graph.Nodes)
        {
            switch (node)
            {
                case FiringNode f when map.ContainsKey(f.Id):
                    if (groupNodes.TryGetValue(f.Id, out var gn)) nodes.Add(gn);
                    break;
                case TriggerNode t when map.ContainsKey(t.Id):
                    break; // 합쳐진 실행
                case TriggerNode t when t.ParentFiringId is not null && map.TryGetValue(t.ParentFiringId, out var gid):
                    nodes.Add(t with { ParentFiringId = gid, Observed = triggerObserved[t.Id] });
                    break;
                default:
                    nodes.Add(node);
                    break;
            }
        }

        string M(string id) => map.TryGetValue(id, out var x) ? x : id;
        var merged = new List<GraphEdge>();
        var index = new Dictionary<(string, string, EdgeKind, string?), int>();
        foreach (var e in graph.Edges)
        {
            var s = M(e.Source);
            var t = M(e.Target);
            if (s == t) continue;
            var key = (s, t, e.Kind, e.Fk);
            if (index.TryGetValue(key, out var i))
            {
                var p = merged[i];
                merged[i] = p with
                {
                    Confidence = Conf.Weaker(p.Confidence, e.Confidence),
                    OrderGuarantee = p.OrderGuarantee == OrderGuarantee.ObservedOnly || e.OrderGuarantee == OrderGuarantee.ObservedOnly ? OrderGuarantee.ObservedOnly : OrderGuarantee.Guaranteed,
                    Evidence = p.Evidence.Concat(e.Evidence).Distinct().ToList(),
                    MatchedRows = p.MatchedRows is null && e.MatchedRows is null ? null : (p.MatchedRows ?? 0) + (e.MatchedRows ?? 0),
                };
            }
            else
            {
                index[key] = merged.Count;
                merged.Add(e with { Source = s, Target = t });
            }
        }
        return new GraphDto { Nodes = nodes, Edges = merged.Select((e, i) => e with { Id = ContractIds.Edge(i + 1) }).ToList() };
    }
}
