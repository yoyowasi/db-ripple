using DbRipple.Contracts;

namespace DbRipple.Attribution;

/// <summary>
/// C13 원인 귀속·그래프 조립(§4 C13, §7.2 3~7단계, §6.3). 1·2단계(문장 나누기, 괄호)는 C11이 했다(CollectResult.ContextLinks, AD4).
/// 판정 순서는 문장마다 5단계 직접 대상 → 3단계 FK(문장 안 전체, 순서와 무관) → 4단계 트리거·5단계 INSTEAD OF(깊이 오름차순, 부모 판정이 먼저 끝나야
/// RECURSIVE_TRIGGERS OFF의 직접 재귀 제외를 알 수 있다) → 6단계 잔차 → 7단계 순서 표시다. 순수 함수이고 결과 순서는 결정적이다.
/// </summary>
public sealed class CausalResolver : ICausalResolver
{
    /// <summary>그래프를 group으로 접는 노드 수 기준(§6.3, D8).</summary>
    public const int FoldThreshold = 200;

    /// <inheritdoc />
    public Resolution Resolve(ResolveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cat = new CatalogIndex(input.Baseline);
        var firings = input.Collected.Firings.OrderBy(f => f.CaptureSeq).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var byId = firings.ToDictionary(f => f.Id);
        var rows = input.Collected.RowChanges.GroupBy(r => r.FiringId).ToDictionary(g => g.Key, g => g.ToList());
        var links = input.Collected.ContextLinks.GroupBy(l => l.ChildFiringId).ToDictionary(g => g.Key, g => g.First());
        var statements = input.Statements.GroupBy(s => s.Index).ToDictionary(g => g.Key, g => g.First());

        var verdicts = new Dictionary<string, Verdict>();
        var parentRowIds = new Dictionary<string, List<string>>();
        var fkJudge = new FkJudge(cat, rows);

        IReadOnlyList<TriggerInfo> Fired(Firing p) => FiredTriggers(cat, p, verdicts);

        foreach (var group in firings.GroupBy(f => f.StatementIndex).OrderBy(g => g.Key))
        {
            var sf = group.ToList();
            statements.TryGetValue(group.Key, out var st);
            var isExec = st?.Kind == ChangeOperation.Exec;
            var insteadOf = st?.Target is { } stTarget && !isExec ? cat.InsteadOf(stTarget, st.Kind) : null;
            // EXEC: 펼친 프로시저의 정적 쓰기 대상(분석기 ProcedurePlan). 모르면(계약 위반·JSON에서 복원) 빈 집합 → PROC_DIRECT를 주지 않는다(닫힌 쪽).
            var procWrites = new HashSet<string>(isExec ? st!.Plan?.WriteTargets ?? [] : [], CatalogIndex.Names);

            // 5단계 직접 대상(certain).
            foreach (var f in sf)
            {
                if (st is null || isExec || f.Depth != 1 || f.Operation != st.Kind || !CatalogIndex.Names.Equals(f.Table, st.Target)) continue;
                var v = new Verdict { Kind = CauseKind.Direct, Confidence = Confidence.Certain, Order = OrderGuarantee.Guaranteed };
                v.Evidence.Add(Evidence.Depth);
                verdicts[f.Id] = v;
            }

            // 3단계 FK. 쓰기 경합 = 이 문장에서 실행될 수 있는 트리거(재귀 제외 전 상위 집합) 가운데 자식 테이블에 쓸 수 있는 것.
            var possibleWriters = sf.SelectMany(p => cat.NestedTriggers || p.Depth == 1 ? cat.AfterTriggers(p.Table, p.Operation) : [])
                .Concat(insteadOf is null ? [] : [insteadOf]).Distinct().ToList();
            var partialFk = new Dictionary<string, FkJudge.Result>();
            foreach (var f in sf)
            {
                if (verdicts.ContainsKey(f.Id)) continue;
                // EXEC: 프로시저가 자식 테이블에 직접 쓸 수 있으면 FK 연쇄와 구별할 수 없다(자식 다음 부모를 지우는 본문) → certain을 주지 않는다.
                var res = fkJudge.Judge(f, sf, possibleWriters.Any(t => t.CanWrite(f.Table)) || procWrites.Contains(f.Table));
                if (res is null) continue;
                verdicts[f.Id] = res.Verdict;
                foreach (var (rc, ids) in res.ParentRowIds) parentRowIds[rc] = ids;
                if (res.UnmatchedRowIds.Count > 0) partialFk[f.Id] = res;
            }

            // EXEC: 문장 창 안 깊이 1이고 FK로 설명되지 않은 묶음 = 프로시저 본문의 직접 쓰기(PROC_DIRECT, certain).
            // 근거: 프로시저 안 DML의 감사 트리거는 TRIGGER_NESTLEVEL() = 1이고, 분석기가 쓰기 대상을 모두 안다고 확인한 뒤에만 실행한다. SP12 뒤 재검토.
            // 정적 쓰기 대상에 없는 테이블이면 설명할 근거가 없으므로 아래에서 UNEXPLAINED로 남는다.
            if (isExec)
                foreach (var f in sf.Where(f => f.Depth == 1 && !verdicts.ContainsKey(f.Id) && procWrites.Contains(f.Table)))
                {
                    var v = new Verdict { Kind = CauseKind.ProcDirect, Confidence = Confidence.Certain, Order = OrderGuarantee.Guaranteed };
                    v.Evidence.AddRange([Evidence.Depth, Evidence.StaticWriteSet]);
                    verdicts[f.Id] = v;
                }

            // 4단계 트리거, 5단계 INSTEAD OF. 깊이 오름차순.
            foreach (var f in sf.OrderBy(f => f.Depth).ThenBy(f => f.CaptureSeq))
            {
                var isPartial = partialFk.ContainsKey(f.Id);
                if (verdicts.ContainsKey(f.Id) && !isPartial) continue;

                if (f.Depth < 2)
                {
                    if (!isPartial) verdicts[f.Id] = Unexplained();
                    continue;
                }

                IReadOnlyList<string> parents;
                Confidence cap;
                if (links.TryGetValue(f.Id, out var link))
                {
                    parents = link.CandidateParentFiringIds;
                    cap = link.ConfidenceCap;
                }
                else
                {
                    // C11 링크 누락(계약 위반) 대비: 같은 문장의 깊이 d-1 묶음 전부를 후보로, 상한 ambiguous.
                    parents = sf.Where(p => p.Depth == f.Depth - 1).Select(p => p.Id).ToList();
                    cap = Confidence.Ambiguous;
                }

                if (parents.Count == 0)
                {
                    if (!isPartial && f.Depth == 2 && insteadOf is not null && st is not null)
                    {
                        var conf = Conf.Weaker(Confidence.Inferred, cap);
                        if (insteadOf.IsUnknownFor(f.Table) || !insteadOf.CanWrite(f.Table)) conf = Conf.Weaker(conf, Confidence.Ambiguous);
                        var v = new Verdict { Kind = CauseKind.InsteadOf, Confidence = conf, Order = conf == Confidence.Inferred ? OrderGuarantee.Guaranteed : OrderGuarantee.ObservedOnly };
                        v.Evidence.AddRange([Evidence.Depth, Evidence.CatalogInsteadOf, Evidence.StaticWriteSet]);
                        v.TriggerPairs.Add(new TriggerPair(null, insteadOf.Name, st.Index));
                        verdicts[f.Id] = v;
                    }
                    else if (!isPartial) verdicts[f.Id] = Unexplained();
                    continue;
                }

                var pairs = new List<TriggerPair>();
                var unknown = false;
                foreach (var pid in parents)
                {
                    if (!byId.TryGetValue(pid, out var p)) continue;
                    foreach (var t in Fired(p).Where(t => t.CanWrite(f.Table)))
                    {
                        pairs.Add(new TriggerPair(p.Id, t.Name, f.StatementIndex));
                        unknown |= t.IsUnknownFor(f.Table);
                    }
                }
                var tconf = Conf.ByCount(pairs.Count);
                if (pairs.Count > 0)
                {
                    tconf = Conf.Weaker(tconf, cap);
                    if (unknown) tconf = Conf.Weaker(tconf, Confidence.Ambiguous); // Q6
                }

                if (isPartial)
                {
                    // FK로 일부만 설명된 묶음: 나머지 행의 트리거 후보를 덧붙인다(원인 종류는 FK, 확신도는 이미 inferred 이하).
                    var v = verdicts[f.Id];
                    v.TriggerPairs.AddRange(pairs);
                    v.TriggerConfidence = Conf.Weaker(tconf, Confidence.Ambiguous);
                    if (pairs.Count > 0) v.Evidence.AddRange([Evidence.PostorderBracket, Evidence.StaticWriteSet]);
                    continue;
                }

                if (pairs.Count == 0) { verdicts[f.Id] = Unexplained(); continue; }
                var tv = new Verdict
                {
                    Kind = CauseKind.Trigger,
                    Confidence = tconf,
                    Order = tconf == Confidence.Inferred ? OrderGuarantee.Guaranteed : OrderGuarantee.ObservedOnly,
                    ParentFiringId = pairs[0].ParentFiringId,
                };
                tv.Evidence.AddRange([Evidence.Depth, Evidence.PostorderBracket, Evidence.StaticWriteSet]);
                tv.TriggerPairs.AddRange(pairs);
                verdicts[f.Id] = tv;
            }
        }

        // 트리거 실행(발동했을 것) 목록: 그래프 trigger 노드와 SAME_TABLE_TRIGGER 판정에 쓴다.
        var executions = new List<(string? ParentFiringId, int StatementIndex, TriggerInfo Trigger)>();
        var statementOrder = statements.Keys.Concat(firings.Select(f => f.StatementIndex)).Distinct().Order().ToList();
        foreach (var si in statementOrder)
        {
            if (statements.TryGetValue(si, out var st) && st.Status != StatementStatus.NotRun && st.Kind != ChangeOperation.Exec && st.Target is { } t0 && cat.InsteadOf(t0, st.Kind) is { } io)
                executions.Add((null, si, io));
            foreach (var f in firings.Where(f => f.StatementIndex == si))
                foreach (var t in Fired(f)) executions.Add((f.Id, si, t));
        }

        var residuals = new ResidualBuilder(cat, firings, input.Collected.RowChanges, executions.Select(e => e.Trigger).Distinct().ToList());
        var net = residuals.Build(input.Collected.RereadDifferences, input.Net);

        var outFirings = firings.Select(f =>
        {
            var v = verdicts.TryGetValue(f.Id, out var x) ? x : Unexplained();
            return f with { Cause = v.ToCause(), OrderGuarantee = v.Order };
        }).ToList();
        var outRows = input.Collected.RowChanges.Select(r =>
            parentRowIds.TryGetValue(r.Id, out var ids) ? r with { ParentRowIds = ids } : r).ToList();

        var model = new CausalModel { Firings = outFirings, RowChanges = outRows, Residuals = residuals.Residuals, NetChanges = net };
        var graph = GraphBuilder.Build(input.Statements, statementOrder, firings, verdicts, executions, residuals.Residuals, residuals.Sources, input.StatementSql);
        if (graph.Nodes.Count > FoldThreshold) graph = GraphFolder.Fold(graph, firings);
        return new Resolution { Model = model, Graph = graph };
    }

    static Verdict Unexplained()
    {
        var v = new Verdict { Kind = CauseKind.Unexplained, Confidence = Confidence.Unexplained, Order = OrderGuarantee.ObservedOnly };
        v.Evidence.Add(Evidence.Depth);
        return v;
    }

    /// <summary>
    /// 묶음 P가 발동시켰을 활성 사용자 AFTER 트리거(카탈로그 추론, §6.3 FIRES). nested triggers = 0이면 깊이 1 묶음만 발동한다.
    /// RECURSIVE_TRIGGERS OFF면 직접 재귀를 막으므로(A8) P를 쓴 트리거가 하나로 정해질 때 그 트리거는 P로 다시 발동하지 않는다.
    /// </summary>
    static IReadOnlyList<TriggerInfo> FiredTriggers(CatalogIndex cat, Firing p, IReadOnlyDictionary<string, Verdict> verdicts)
    {
        if (!cat.NestedTriggers && p.Depth > 1) return [];
        var list = cat.AfterTriggers(p.Table, p.Operation);
        if (!cat.RecursiveTriggers && verdicts.TryGetValue(p.Id, out var v) && v.Kind == CauseKind.Trigger)
        {
            var writers = v.TriggerPairs.Select(t => t.Trigger).Distinct(CatalogIndex.Names).ToList();
            if (writers.Count == 1) list = list.Where(t => !CatalogIndex.Names.Equals(t.Name, writers[0])).ToList();
        }
        return list;
    }
}
