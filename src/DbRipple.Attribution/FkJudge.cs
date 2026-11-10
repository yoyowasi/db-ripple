using DbRipple.Contracts;

namespace DbRipple.Attribution;

/// <summary>
/// §7.2 3단계 FK 판정(문장 안 전체, 순서와 무관). 짝 없이 집합으로 행을 맞추고, certain은 다음을 모두 만족할 때만 준다:
/// 일치한 (FK, 부모 묶음) 쌍이 하나, 그 FK·이벤트의 부모 후보 묶음(같은 문장의 부모 테이블·연산 묶음)이 하나, 모든 행이 검증으로 일치,
/// 같은 문장에서 실행될 수 있는 트리거 가운데 자식 테이블에 쓸 수 있는 것이 없음, 부모·자식 깊이가 같음(H2), 기본값 비교가 확정.
/// </summary>
internal sealed class FkJudge(CatalogIndex cat, IReadOnlyDictionary<string, List<RowChange>> rows)
{
    internal sealed record Result(Verdict Verdict, IReadOnlyDictionary<string, List<string>> ParentRowIds, IReadOnlyList<string> UnmatchedRowIds);

    sealed record Pair(FkInfo Fk, FkEvent Event, FkAction Action, Firing Parent, int CandidateCount)
    {
        public int Matched;
        public int Unverifiable;
        public bool Weak;
    }

    enum RowMatch { Match, NoMatch, Unverifiable }

    List<RowChange> RowsOf(string firingId) => rows.TryGetValue(firingId, out var l) ? l : [];

    public Result? Judge(Firing child, IReadOnlyList<Firing> statementFirings, bool writeContention)
    {
        var pairs = new List<Pair>();
        foreach (var fk in cat.FksByChild(child.Table))
        {
            foreach (var ev in new[] { FkEvent.Delete, FkEvent.Update })
            {
                var action = ev == FkEvent.Delete ? fk.OnDelete : fk.OnUpdate;
                if (action is not { } a) continue;
                var childOp = a == FkAction.Cascade && ev == FkEvent.Delete ? ChangeOperation.Delete : ChangeOperation.Update;
                if (child.Operation != childOp) continue;
                var parentOp = ev == FkEvent.Delete ? ChangeOperation.Delete : ChangeOperation.Update;
                var parents = statementFirings
                    .Where(p => p.Id != child.Id && CatalogIndex.Names.Equals(p.Table, fk.Parent) && p.Operation == parentOp)
                    .ToList();
                foreach (var p in parents) pairs.Add(new Pair(fk, ev, a, p, parents.Count));
            }
        }
        if (pairs.Count == 0) return null;

        var childRows = RowsOf(child.Id);
        var rowMatches = childRows.ToDictionary(r => r.Id, _ => new List<Pair>());
        var rowUnverifiable = childRows.ToDictionary(r => r.Id, _ => new List<Pair>());
        var parentRowIds = new Dictionary<string, List<string>>();

        foreach (var pair in pairs)
        {
            var sets = ParentSets(pair);
            foreach (var rc in childRows)
            {
                var (m, weak, parentIds) = MatchRow(pair, sets, rc);
                if (m == RowMatch.Match)
                {
                    pair.Matched++;
                    pair.Weak |= weak;
                    rowMatches[rc.Id].Add(pair);
                    if (parentIds.Count > 0)
                    {
                        if (!parentRowIds.TryGetValue(rc.Id, out var l)) parentRowIds[rc.Id] = l = [];
                        foreach (var id in parentIds) if (!l.Contains(id)) l.Add(id);
                    }
                }
                else if (m == RowMatch.Unverifiable)
                {
                    pair.Unverifiable++;
                    rowUnverifiable[rc.Id].Add(pair);
                }
            }
        }

        var matchedRows = childRows.Count(r => rowMatches[r.Id].Count > 0);
        var unverifiableRows = childRows.Count(r => rowMatches[r.Id].Count == 0 && rowUnverifiable[r.Id].Count > 0);
        var unmatched = childRows.Where(r => rowMatches[r.Id].Count == 0 && rowUnverifiable[r.Id].Count == 0).Select(r => r.Id).ToList();
        if (matchedRows == 0 && unverifiableRows == 0) return null;

        var active = pairs.Where(p => p.Matched + p.Unverifiable > 0).ToList();
        var primary = active.OrderByDescending(p => p.Matched).ThenByDescending(p => p.Unverifiable).ThenBy(p => p.Parent.CaptureSeq).First();
        var distinctParents = active.Select(p => p.Parent.Id).Distinct().Count();

        var certain = active.Count == 1
            && primary.CandidateCount == 1
            && unverifiableRows == 0
            && unmatched.Count == 0
            && !primary.Weak
            && !writeContention
            && child.Depth == primary.Parent.Depth
            && childRows.Count == child.RowCount
            && childRows.Count > 0;
        var confidence = certain ? Confidence.Certain : Confidence.Inferred;
        // 문장이 시작한 1차 연쇄(깊이 1)이고 루트 후보가 하나일 때만 부모 → 자식 순서를 보장으로 둔다. 2차 체인은 observed_only(§7.2 7단계, H5).
        var order = child.Depth == 1 && distinctParents == 1 && primary.CandidateCount == 1 ? OrderGuarantee.Guaranteed : OrderGuarantee.ObservedOnly;

        var verdict = new Verdict
        {
            Kind = primary.Action switch
            {
                FkAction.Cascade => CauseKind.FkCascade,
                FkAction.SetNull => CauseKind.FkSetNull,
                _ => CauseKind.FkSetDefault,
            },
            Confidence = confidence,
            Order = order,
            ParentFiringId = primary.Parent.Id,
            ForeignKey = new ForeignKeyCause
            {
                Name = primary.Fk.Name,
                Parent = primary.Fk.Parent,
                Child = primary.Fk.Child,
                Action = primary.Action,
                Event = primary.Event,
                MatchedRows = primary.Matched,
                UnmatchedRows = childRows.Count - primary.Matched - primary.Unverifiable,
            },
        };
        verdict.Evidence.Add(Evidence.FkKeyMatch);
        verdict.Evidence.Add(Evidence.Depth);
        foreach (var p in active)
            verdict.FkLinks.Add(new FkLink(p.Parent.Id, p.Fk, p.Action, p.Event, p.Matched,
                active.Count == 1 ? confidence : Confidence.Inferred));

        return new Result(verdict, parentRowIds, unmatched);
    }

    sealed record ParentSet(bool Complete, HashSet<string> Before, HashSet<string> After, List<(RowChange Row, string? B, string? A)> Rows);

    ParentSet ParentSets(Pair pair)
    {
        var prows = RowsOf(pair.Parent.Id);
        var before = new HashSet<string>();
        var after = new HashSet<string>();
        var list = new List<(RowChange, string?, string?)>();
        var complete = prows.Count == pair.Parent.RowCount;
        foreach (var r in prows)
        {
            var b = RowValues.Extract(r, before: true, pair.Fk.ParentColumns);
            var a = pair.Event == FkEvent.Update ? RowValues.Extract(r, before: false, pair.Fk.ParentColumns) : null;
            // UPDATE 이벤트: 짝을 짓지 못한 행(UNPAIRED)은 한쪽 이미지만 있을 수 있다. 둘 다 없으면 불완전.
            if (b is null && (pair.Event == FkEvent.Delete || a is null)) complete = false;
            var bt = b is null ? null : cat.Values.Tuple(b);
            var at = a is null ? null : cat.Values.Tuple(a);
            if (bt is not null) before.Add(bt);
            if (at is not null) after.Add(at);
            list.Add((r, bt, at));
        }
        return new ParentSet(complete, before, after, list);
    }

    (RowMatch, bool Weak, List<string> ParentIds) MatchRow(Pair pair, ParentSet ps, RowChange rc)
    {
        var none = new List<string>();
        var cb = RowValues.Extract(rc, before: true, pair.Fk.ChildColumns);
        if (cb is null) return (RowMatch.Unverifiable, false, none);
        var tb = cat.Values.Tuple(cb);
        if (tb is null) return (RowMatch.NoMatch, false, none); // NULL FK 값은 부모를 참조하지 않는다.
        if (!ps.Before.Contains(tb)) return ps.Complete ? (RowMatch.NoMatch, false, none) : (RowMatch.Unverifiable, false, none);

        var weak = false;
        string? ta = null;
        if (!(pair.Event == FkEvent.Delete && pair.Action == FkAction.Cascade))
        {
            var ca = RowValues.Extract(rc, before: false, pair.Fk.ChildColumns);
            if (ca is null) return (RowMatch.Unverifiable, false, none);
            if (cat.Values.KeyTuple(cb) == cat.Values.KeyTuple(ca)) return (RowMatch.NoMatch, false, none); // FK 값이 바뀌지 않음
            switch (pair.Action)
            {
                case FkAction.SetNull:
                    if (ca.Any(v => v is not null)) return (RowMatch.NoMatch, false, none);
                    break;
                case FkAction.SetDefault:
                    for (var i = 0; i < ca.Count; i++)
                    {
                        var col = cat.Column(pair.Fk.Child, pair.Fk.ChildColumns[i]);
                        var (parsed, def) = CatalogIndex.ParseDefault(col?.DefaultDefinition);
                        if (col is null || !parsed) { weak = true; continue; }
                        if (!cat.Values.AreEqual(def, ca[i])) return (RowMatch.NoMatch, false, none);
                    }
                    break;
                default: // UPDATE CASCADE
                    ta = cat.Values.Tuple(ca);
                    if (ta is null || !ps.After.Contains(ta)) return ps.Complete ? (RowMatch.NoMatch, false, none) : (RowMatch.Unverifiable, false, none);
                    break;
            }
        }

        // 행 단위 부모(parentRowIds): before 키가 같은 부모 행. UPDATE 이벤트는 부모 짝이 확정된 행만(UNPAIRED 제외), CASCADE는 after까지 같아야 한다.
        var ids = new List<string>();
        foreach (var (row, b, a) in ps.Rows)
        {
            if (b != tb) continue;
            if (pair.Event == FkEvent.Update)
            {
                if (row.Pairing == Pairing.Unpaired || row.Before is null || row.After is null) continue;
                if (pair.Action == FkAction.Cascade && a != ta) continue;
            }
            ids.Add(row.Id);
        }
        return (RowMatch.Match, weak, ids);
    }
}
