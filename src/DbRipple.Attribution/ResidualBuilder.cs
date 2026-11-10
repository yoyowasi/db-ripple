using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.Attribution;

/// <summary>잔차 노드의 후보 원인(RESIDUAL 간선 출발점). Trigger면 그 트리거의 실행 노드 전부, FiringId면 그 묶음 노드.</summary>
internal sealed record ResidualSource(string ResidualId, string? Trigger, string? FiringId);

/// <summary>
/// §7.2 6단계: 재조회 차이(C11 RereadDifference)와 최종 차이에만 있는 변경을 잔차로 만들고, NetChange.ExplainedBy를 채운다.
/// 범주는 EXCLUDED_TABLE → UNOBSERVABLE_COLUMN → SAME_TABLE_TRIGGER(그 테이블에 쓸 수 있는 트리거가 실행됨) → UNKNOWN 순서로 따진다.
/// 확신도는 후보 수(1 → inferred, 여럿 → ambiguous, 0 → unexplained)이고, 쓰기 대상 미상 트리거가 후보면 ambiguous 이하(Q6).
/// </summary>
internal sealed class ResidualBuilder
{
    readonly CatalogIndex _cat;
    readonly IReadOnlyList<Firing> _firings;
    readonly Dictionary<string, Firing> _byId;
    readonly Dictionary<string, List<RowChange>> _rowsByTable = new(CatalogIndex.Names);
    readonly IReadOnlyList<TriggerInfo> _executed;
    readonly Dictionary<(string Table, string Cols), Dictionary<string, List<RowChange>>> _keyIndex = new();

    public List<Residual> Residuals { get; } = [];
    public List<ResidualSource> Sources { get; } = [];

    public ResidualBuilder(CatalogIndex cat, IReadOnlyList<Firing> firings, IReadOnlyList<RowChange> rowChanges, IReadOnlyList<TriggerInfo> executedTriggers)
    {
        _cat = cat;
        _firings = firings;
        _byId = firings.ToDictionary(f => f.Id);
        _executed = executedTriggers;
        foreach (var rc in rowChanges)
        {
            if (!_byId.TryGetValue(rc.FiringId, out var f)) continue;
            if (!_rowsByTable.TryGetValue(f.Table, out var l)) _rowsByTable[f.Table] = l = [];
            l.Add(rc);
        }
    }

    public IReadOnlyList<NetChange> Build(IReadOnlyList<RereadDifference> rereads, IReadOnlyList<NetChange> net)
    {
        var rereadByRow = new Dictionary<(string, string), int>();
        foreach (var rd in rereads)
        {
            var touching = Touching(rd.Table, rd.Key, null);
            var id = ContractIds.Residual(Residuals.Count + 1);
            Residuals.Add(Categorize(id, rd.Table, rd.Key, rd.Columns, ResidualDetectedBy.Reread, rd.RowChangeId, touching));
            rereadByRow.TryAdd((rd.Table.ToUpperInvariant(), _cat.Values.KeyTuple(rd.Key.Values)), Residuals.Count - 1);
        }

        var result = new List<NetChange>(net.Count);
        foreach (var n in net)
        {
            var touching = Touching(n.Table, n.Key, n);
            var firingIds = touching.Select(r => _byId[r.FiringId]).Distinct().OrderBy(f => f.CaptureSeq).Select(f => f.Id).ToList();
            var remaining = Unexplained(n, touching);

            string? residualId = null;
            if (n.Key.Kind != KeyKind.ROW && rereadByRow.TryGetValue((n.Table.ToUpperInvariant(), _cat.Values.KeyTuple(n.Key.Values)), out var ri))
            {
                var r = Residuals[ri];
                remaining = remaining.Where(c => !r.Columns.Contains(c, CatalogIndex.Names)).ToList();
                if (remaining.Count > 0) Residuals[ri] = r with { Columns = [.. r.Columns, .. remaining] };
                residualId = r.Id;
            }
            else if (remaining.Count > 0 || firingIds.Count == 0)
            {
                residualId = ContractIds.Residual(Residuals.Count + 1);
                Residuals.Add(Categorize(residualId, n.Table, n.Key, remaining, ResidualDetectedBy.NetDiff, null, touching));
            }
            result.Add(n with { ExplainedBy = new ExplainedBy { FiringIds = firingIds, ResidualId = residualId } });
        }
        return result;
    }

    /// <summary>순 변경의 열 가운데 묶음으로 설명되지 않는 열.</summary>
    List<string> Unexplained(NetChange n, List<RowChange> touching)
    {
        switch (n.Change)
        {
            case NetChangeKind.Updated:
                var changed = n.ChangedColumns.Count > 0 ? n.ChangedColumns : (n.After?.Keys.ToList() ?? []);
                if (touching.Count == 0) return [.. changed];
                // INSERT·DELETE가 그 행에 닿았거나 이미지가 없는 행(KEYS)이 있으면 열 단위로 따질 수 없어 설명된 것으로 본다.
                if (touching.Any(r => r.Operation != ChangeOperation.Update || r.Before is null || r.After is null)) return [];
                var explained = new HashSet<string>(touching.SelectMany(r => r.ChangedColumns.Concat(r.Derived)), CatalogIndex.Names);
                return changed.Where(c => !explained.Contains(c) && _cat.Column(n.Table, c)?.IsComputed != true).ToList();
            case NetChangeKind.Inserted:
            case NetChangeKind.MultisetAdded:
                return touching.Count > 0 ? [] : (n.After?.Keys.ToList() ?? [.. n.Key.Columns]);
            default:
                return touching.Count > 0 ? [] : (n.Before?.Keys.ToList() ?? [.. n.Key.Columns]);
        }
    }

    /// <summary>같은 테이블에서 이 키(또는 키 없는 테이블이면 같은 행 값)에 닿은 행 변경.</summary>
    List<RowChange> Touching(string table, RowKey key, NetChange? n)
    {
        if (!_rowsByTable.TryGetValue(table, out var rows)) return [];
        if (key.Kind == KeyKind.ROW)
        {
            if (n is null) return [];
            var image = n.Change is NetChangeKind.MultisetRemoved or NetChangeKind.Deleted ? n.Before : n.After;
            if (image is null) return [];
            return rows.Where(r =>
            {
                var other = n.Change is NetChangeKind.MultisetRemoved or NetChangeKind.Deleted ? r.Before : r.After;
                return other is not null && image.All(kv => RowValues.TryGet(other, kv.Key, out var v) && _cat.Values.AreEqual(v, kv.Value));
            }).ToList();
        }

        var cols = string.Join('\u001F', key.Columns.Select(c => c.ToUpperInvariant()));
        if (!_keyIndex.TryGetValue((table.ToUpperInvariant(), cols), out var index))
        {
            index = [];
            foreach (var r in rows)
            {
                var tuples = new HashSet<string>();
                if (RowValues.SameColumns(r.Key.Columns, key.Columns)) tuples.Add(_cat.Values.KeyTuple(r.Key.Values));
                if (r.Before is not null && RowValues.Extract(r, true, key.Columns) is { } b) tuples.Add(_cat.Values.KeyTuple(b));
                if (r.After is not null && RowValues.Extract(r, false, key.Columns) is { } a) tuples.Add(_cat.Values.KeyTuple(a));
                foreach (var t in tuples)
                {
                    if (!index.TryGetValue(t, out var l)) index[t] = l = [];
                    l.Add(r);
                }
            }
            _keyIndex[(table.ToUpperInvariant(), cols)] = index;
        }
        return index.TryGetValue(_cat.Values.KeyTuple(key.Values), out var hit) ? hit : [];
    }

    Residual Categorize(string id, string table, RowKey key, IReadOnlyList<string> columns, ResidualDetectedBy by, string? rowChangeId, List<RowChange> touching)
    {
        var candidates = new List<string>();
        var unknownTrigger = false;
        ResidualCategory category;

        void AddTriggers()
        {
            foreach (var t in _executed.Where(t => t.CanWrite(table)))
            {
                candidates.Add(t.Name);
                unknownTrigger |= t.Unknown;
                Sources.Add(new ResidualSource(id, t.Name, null));
            }
        }

        if (_cat.IsExcluded(table))
        {
            category = ResidualCategory.ExcludedTable;
            AddTriggers();
            foreach (var fk in _cat.FksByChild(table))
            {
                var parents = _firings.Where(p => CatalogIndex.Names.Equals(p.Table, fk.Parent)
                    && ((fk.OnDelete is not null && p.Operation == ChangeOperation.Delete) || (fk.OnUpdate is not null && p.Operation == ChangeOperation.Update))).ToList();
                if (parents.Count == 0) continue;
                candidates.Add(fk.Name);
                foreach (var p in parents) Sources.Add(new ResidualSource(id, null, p.Id));
            }
        }
        else if (columns.Count > 0 && columns.All(c => _cat.IsUnobservable(table, c)))
        {
            category = ResidualCategory.UnobservableColumn;
            foreach (var f in touching.Select(r => _byId[r.FiringId]).Distinct().OrderBy(f => f.CaptureSeq))
            {
                candidates.Add(f.Id);
                Sources.Add(new ResidualSource(id, null, f.Id));
            }
        }
        else if (_executed.Any(t => t.CanWrite(table)))
        {
            category = ResidualCategory.SameTableTrigger;
            AddTriggers();
        }
        else
        {
            category = ResidualCategory.Unknown;
        }

        var confidence = Conf.ByCount(candidates.Distinct(CatalogIndex.Names).Count());
        if (unknownTrigger && confidence != Confidence.Unexplained) confidence = Conf.Weaker(confidence, Confidence.Ambiguous);
        return new Residual
        {
            Id = id,
            Table = table,
            Key = key,
            Columns = [.. columns],
            DetectedBy = by,
            Candidates = candidates.Distinct(CatalogIndex.Names).ToList(),
            Category = category,
            Confidence = confidence,
            RowChangeId = rowChangeId,
        };
    }
}
