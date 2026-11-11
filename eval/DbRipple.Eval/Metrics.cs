using System.Globalization;
using System.Text.RegularExpressions;
using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>
/// 제품 결과를 하네스 형식으로 바꾼 순 변경 한 건. 제품의 셀 값·키는 그대로 문자열로 옮기고, 비교·지표 계산은 하네스 코드만 한다.
/// ByFiring = 묶음이 설명함(ExplainedBy.FiringIds가 있음). ResidualColumns = 잔차가 설명한 열("*"면 그 행 전체). 둘 다 없으면 설명 없음.
/// </summary>
public sealed record PredictedChange(OracleRowChange Row, bool ByFiring, string? ResidualId, IReadOnlySet<string> ResidualColumns)
{
    /// <summary>잔차만으로 설명됨(M2 found_by_residual_only).</summary>
    public bool ResidualOnly => !ByFiring && ResidualId is not null;

    /// <summary>그 열 셀이 잔차로 메워졌는지(M1 residual_filled_cells).</summary>
    public bool IsResidualFilled(string column) =>
        ResidualId is not null && (!ByFiring || ResidualColumns.Contains("*") || ResidualColumns.Contains(column));

    /// <summary>하네스 순 변경(평가 훅·오라클)을 설명 정보 없이 감싼다.</summary>
    public static PredictedChange Plain(OracleRowChange r) => new(r, false, null, new HashSet<string>());

    /// <summary>
    /// 제품 미리보기 기록의 최종 차이(PreviewRecord.NetChanges, ExplainedBy 채워짐)와 롤백 뒤 남은 변경(PreviewResult.ResidualCommitted, 3주차)을 하네스 형식으로 바꾼다.
    /// 실패 사례에서 사용자가 보는 "실제로 남는 변경"은 ResidualCommitted이고, 오라클 after도 롤백 뒤 상태다(인계 4). 성공 사례에서는 ResidualCommitted가 비어 있다.
    /// 키 없는 테이블(Kind = ROW)의 키는 행 전체 값으로 만든다(하네스 비교기와 같은 규칙, 해시는 쓰지 않음).
    /// </summary>
    public static IReadOnlyList<PredictedChange> FromRecord(PreviewRecord? record)
    {
        if (record is null) return [];
        var residuals = record.Result.Residuals.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var list = new List<PredictedChange>();
        foreach (var n in record.NetChanges.Concat(record.Result.ResidualCommitted ?? []))
        {
            var key = n.Key.Kind.ToString() == "ROW"
                ? RowKeys.Canonical(n.After ?? n.Before ?? new Dictionary<string, string?>())
                : RowKeys.Canonical(n.Key.Columns, n.Key.Values);
            var row = new OracleRowChange(n.Table, key, CaseScorer.ContractEnumText(n.Change), n.Multiplicity, n.Before, n.After, n.ChangedColumns);
            var rid = n.ExplainedBy?.ResidualId;
            IReadOnlySet<string> cols = rid is not null && residuals.TryGetValue(rid, out var res)
                ? (res.Columns.Count == 0 ? new HashSet<string> { "*" } : res.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase))
                : new HashSet<string>();
            list.Add(new PredictedChange(row, n.ExplainedBy?.FiringIds.Count > 0, rid, cols));
        }
        return list;
    }
}

/// <summary>M1 한 비교의 계수(§10.4). 비율은 CSV 쓸 때 MetricsCsv.Rate로 계산한다.</summary>
public sealed record M1Counts(
    long TruthCells, long PredictedCells, long Tp, long Fp, long Fn,
    long RowsUnion, long RowsExact, long VolatileCells, long VolatilePresentFormatOk,
    long? ResidualFilledCells,
    long? ResidualBaseCells = null)
{
    /// <summary>정답 쪽 IDENTITY 키 INSERTED 행 수(IdentityPairing, 3주차).</summary>
    public long IdentityRows { get; init; }
    /// <summary>그 가운데 예측 행과 짝지은 수.</summary>
    public long IdentityPaired { get; init; }

    /// <summary>m1.csv 지표 칸(truth_cells부터 identity_paired까지).</summary>
    public IReadOnlyList<string> CsvCells() =>
    [
        I(TruthCells), I(PredictedCells), I(Tp), I(Fp), I(Fn),
        MetricsCsv.Rate(Tp, Tp + Fp), MetricsCsv.Rate(Tp, Tp + Fn), MetricsCsv.Rate(2 * Tp, 2 * Tp + Fp + Fn),
        I(RowsUnion), I(RowsExact), MetricsCsv.Rate(RowsExact, RowsUnion),
        I(VolatileCells), I(VolatilePresentFormatOk),
        ResidualFilledCells is long r ? I(r) : "", ResidualFilledCells is long r2 ? MetricsCsv.Rate(r2, ResidualBaseCells ?? 0) : "",
        I(IdentityRows), I(IdentityPaired),
    ];

    static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>M2 한 원인 버킷(§10.4).</summary>
public sealed record M2Counts(string Cause, long TruthRows, long FoundRows, long FoundByFiring, long FoundByResidualOnly, IReadOnlyList<string> MissedKeys)
{
    /// <summary>m2.csv 지표 칸(truth_rows부터 missed_keys까지).</summary>
    public IReadOnlyList<string> CsvCells() =>
    [
        TruthRows.ToString(CultureInfo.InvariantCulture), FoundRows.ToString(CultureInfo.InvariantCulture),
        MetricsCsv.Rate(FoundRows, TruthRows),
        FoundByFiring.ToString(CultureInfo.InvariantCulture), FoundByResidualOnly.ToString(CultureInfo.InvariantCulture),
        string.Join("; ", MissedKeys),
    ];
}

/// <summary>
/// M1·M2 계산(§10.4, docs/week2-plan.md §5). 입력은 하네스 형식뿐이다(제품 결과는 PredictedChange.FromRecord로 먼저 바꾼다).
/// <list type="bullet">
/// <item>셀 사실 = (테이블, 키, 열, 값). INSERTED는 모든 열, UPDATED는 바뀐 열, DELETED는 ("*", "DELETED") 하나. 키 없는 테이블은 개수만큼 "키#n"으로 편다.</item>
/// <item>(테이블, 키, 열)당 한 번만 센다(같은 셀이 여러 번 나오면 처음 것). 테이블·열 이름은 대소문자 무시, 값은 서수 비교.</item>
/// <item>비결정 셀(volatile 열)은 tp·fp·fn에서 빼고, 정답의 비결정 셀마다 예측에 같은 셀이 있고 형식이 같으면 volatile_present_format_ok로 센다.</item>
/// <item>값이 다른 셀은 fp와 fn에 하나씩 들어간다. 행 완전 일치 = 양쪽에 있고 그 행의 셀이 모두 맞음(비결정 셀은 존재·형식).</item>
/// </list>
/// </summary>
public sealed class MetricsCalculator
{
    /// <summary>DELETED 셀 사실의 열 이름.</summary>
    public const string DeletedColumn = "*";
    /// <summary>DELETED 셀 사실의 값.</summary>
    public const string DeletedValue = "DELETED";

    /// <summary>테이블 표시 이름 → 비결정 열(대소문자 무시)을 만든다.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> VolatileMap(IEnumerable<OracleTable> tables) =>
        tables.ToDictionary(t => t.Display, t => (IReadOnlySet<string>)t.VolatileColumns.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>순 변경 한 건의 셀 사실.</summary>
    public static IEnumerable<CellFact> Cells(OracleRowChange r, IReadOnlyDictionary<string, IReadOnlySet<string>> volatileMap)
    {
        var vol = volatileMap.GetValueOrDefault(r.Table) ?? new HashSet<string>();
        var keys = r.Change is ChangeKinds.MultisetAdded or ChangeKinds.MultisetRemoved
            ? Enumerable.Range(1, (int)Math.Max(1, r.Multiplicity)).Select(i => r.Key + "#" + i.ToString(CultureInfo.InvariantCulture)).ToList()
            : [r.Key];
        foreach (var key in keys)
            switch (r.Change)
            {
                case ChangeKinds.Deleted or ChangeKinds.MultisetRemoved:
                    yield return new CellFact(r.Table, key, DeletedColumn, DeletedValue, false);
                    break;
                case ChangeKinds.Inserted or ChangeKinds.MultisetAdded:
                    foreach (var (c, v) in r.After ?? new Dictionary<string, string?>())
                        yield return new CellFact(r.Table, key, c, v, vol.Contains(BaseColumn(c)));
                    break;
                case ChangeKinds.Updated:
                    foreach (var c in r.ChangedColumns)
                    {
                        string? v = null;
                        var has = r.After is not null && r.After.TryGetValue(c, out v);
                        yield return new CellFact(r.Table, key, c, has ? v : MissingValue, vol.Contains(BaseColumn(c)));
                    }
                    break;
                default:
                    throw new ArgumentException($"모르는 변경 종류 {r.Change}");
            }
    }

    /// <summary>after에 없는 바뀐 열의 값 표시(어떤 실제 값과도 같지 않다).</summary>
    public const string MissingValue = "\u0000MISSING";

    /// <summary>
    /// M1 한 비교. truth는 정답 쪽(오라클 또는 평가 훅), predicted는 예측 쪽(미리보기 또는 평가 훅).
    /// 잔차 칸(ResidualFilledCells·ResidualBaseCells)은 null로 두고, 비교 종류에 맞게 ResidualByExplanation·ResidualByAbsence로 채운다.
    /// </summary>
    public M1Counts M1(IReadOnlyList<OracleRowChange> truth, IReadOnlyList<PredictedChange> predicted,
        IReadOnlyDictionary<string, IReadOnlySet<string>> volatileMap)
    {
        var t = Index(truth.Select(r => (r, (PredictedChange?)null)), volatileMap);
        var p = Index(predicted.Select(x => (x.Row, (PredictedChange?)x)), volatileMap);

        long tp = 0, fp = 0, fn = 0, volCells = 0, volOk = 0, truthCells = 0, predCells = 0;
        var badRows = new HashSet<(string, string)>();
        foreach (var (id, tc) in t)
        {
            var hasP = p.TryGetValue(id, out var pc);
            if (tc.Fact.Volatile)
            {
                volCells++;
                if (hasP && FormatShape(pc.Fact.Value) == FormatShape(tc.Fact.Value)) volOk++;
                else badRows.Add((id.Table, id.Key));
                continue;
            }
            truthCells++;
            if (hasP && !pc.Fact.Volatile && string.Equals(pc.Fact.Value, tc.Fact.Value, StringComparison.Ordinal)) tp++;
            else { fn++; badRows.Add((id.Table, id.Key)); }
        }
        foreach (var (id, pc) in p)
        {
            if (pc.Fact.Volatile) continue;
            predCells++;
            if (t.TryGetValue(id, out var tc) && !tc.Fact.Volatile && string.Equals(pc.Fact.Value, tc.Fact.Value, StringComparison.Ordinal)) continue;
            fp++;
            badRows.Add((id.Table, id.Key));
        }
        var truthRows = t.Keys.Select(k => (k.Table, k.Key)).ToHashSet();
        var predRows = p.Keys.Select(k => (k.Table, k.Key)).ToHashSet();
        var union = truthRows.Union(predRows).ToList();
        var exact = union.Count(r => truthRows.Contains(r) && predRows.Contains(r) && !badRows.Contains(r));
        return new M1Counts(truthCells, predCells, tp, fp, fn, union.Count, exact, volCells, volOk, null);
    }

    /// <summary>
    /// total(E_total)의 잔차 칸: 최종 차이(NetChanges) 셀 가운데 잔차로 메운 셀(묶음 설명 없이 잔차만, 또는 Residual.Columns의 열). 분모는 최종 차이 셀(비결정 제외).
    /// </summary>
    public static (long Filled, long Base) ResidualByExplanation(IReadOnlyList<PredictedChange> net, IReadOnlyDictionary<string, IReadOnlySet<string>> volatileMap)
    {
        var cells = Index(net.Select(x => (x.Row, (PredictedChange?)x)), volatileMap).Values.Where(c => !c.Fact.Volatile).ToList();
        return (cells.Count(c => c.Source!.IsResidualFilled(c.Fact.Column)), cells.Count);
    }

    /// <summary>
    /// cap(E_cap)의 잔차 칸: 최종 차이(NetChanges)에는 있고 행 변경 재구성(Reconstruction)에는 없는 셀((테이블, 키, 열) 기준). 분모는 최종 차이 셀(비결정 제외).
    /// 재구성에 없으니 cap 비교에서는 fn으로도 잡힌다.
    /// </summary>
    public static (long Filled, long Base) ResidualByAbsence(IReadOnlyList<PredictedChange> net, IReadOnlyList<OracleRowChange> reconstruction,
        IReadOnlyDictionary<string, IReadOnlySet<string>> volatileMap)
    {
        var rec = Index(reconstruction.Select(r => (r, (PredictedChange?)null)), volatileMap);
        var cells = Index(net.Select(x => (x.Row, (PredictedChange?)x)), volatileMap).Where(c => !c.Value.Fact.Volatile).ToList();
        return (cells.Count(c => !rec.ContainsKey(c.Key)), cells.Count);
    }

    /// <summary>
    /// M2(§10.4): 손 정답 행을 원인 버킷(HandCase.Causes)으로 묶어, 예측(제품 최종 차이)에 같은 (테이블, 키, 변경 종류)가 있으면 found.
    /// 한 행에 원인이 여럿이면(CL08 DIRECT+TRIGGER) 버킷마다 한 번씩 센다. 버킷 안에서는 (테이블, 키)당 한 번(종류가 여럿이면 모두 찾아야 found).
    /// 마지막 원소는 scope "ALL"(원인과 무관하게 (테이블, 키)당 한 번)이다.
    /// </summary>
    public IReadOnlyList<M2Counts> M2(HandCase hand, IReadOnlyList<PredictedChange> predicted)
    {
        var pred = new Dictionary<(string, string, string), PredictedChange>();
        foreach (var p in predicted) pred.TryAdd((p.Row.Table.ToUpperInvariant(), p.Row.Key, p.Row.Change), p);

        M2Counts Bucket(string cause, IEnumerable<HandRow> rows)
        {
            long truth = 0, found = 0, byFiring = 0, residualOnly = 0;
            var missed = new List<string>();
            foreach (var g in rows.GroupBy(r => (r.Table.ToUpperInvariant(), r.Key)).OrderBy(g => g.First().Display, StringComparer.Ordinal))
            {
                truth++;
                var hits = g.Select(r => pred.GetValueOrDefault((g.Key.Item1, r.Key, r.Kind))).ToList();
                if (hits.Any(h => h is null)) { missed.Add(g.First().Display); continue; }
                found++;
                if (hits.Any(h => h!.ByFiring)) byFiring++;
                else if (hits.Any(h => h!.ResidualOnly)) residualOnly++;
            }
            return new M2Counts(cause, truth, found, byFiring, residualOnly, missed);
        }

        var list = HandCase.Causes.Select(c => Bucket(c, hand.Rows.Where(r => r.Causes.Contains(c)))).ToList();
        list.Add(Bucket("ALL", hand.Rows));
        return list;
    }

    /// <summary>
    /// 비결정 셀의 형식 모양: NULL은 "NULL", 아니면 16진 문자·숫자의 연속을 "x" 하나로 줄인 문자열.
    /// 예: "2026-11-06T10:15:30.117" → "x-x-xTx:x:x.x", "213443" → "x", GUID → "x-x-x-x-x". 값이 아니라 형식만 맞는지 본다(E1).
    /// </summary>
    public static string FormatShape(string? value) => value is null ? "NULL" : HexRun.Replace(value, "x");

    static readonly Regex HexRun = new("[0-9A-Fa-f]+", RegexOptions.CultureInvariant);

    // "열@srid"·"열@type" 생성 속성은 원래 열의 비결정 여부를 따른다.
    static string BaseColumn(string c) => c.IndexOf('@') is var i and > 0 ? c[..i] : c;

    readonly record struct CellId(string Table, string Key, string Column);
    readonly record struct Indexed(CellFact Fact, PredictedChange? Source);

    static Dictionary<CellId, Indexed> Index(IEnumerable<(OracleRowChange Row, PredictedChange? Source)> rows, IReadOnlyDictionary<string, IReadOnlySet<string>> volatileMap)
    {
        var d = new Dictionary<CellId, Indexed>();
        foreach (var (row, src) in rows)
            foreach (var f in Cells(row, volatileMap))
                d.TryAdd(new CellId(f.Table.ToUpperInvariant(), f.Key, f.Column.ToUpperInvariant()), new Indexed(f, src));
        return d;
    }
}

/// <summary>스위트 마이크로 평균(metrics.csv). (system, comparison)·(system, cause)·(system, 확신도)마다 계수를 더한다.</summary>
public sealed class SuiteAccumulator
{
    sealed class M3Sum
    {
        public long Predicted, PredictedMatched, Truth, TruthFound, AmbGroups, AmbHit, CertainPredicted, CertainOver, Manual, ManualRuns;
        public readonly HashSet<string> Cases = [];
    }

    readonly Dictionary<(string System, string Scope), M3Sum> m3 = [];
    readonly Dictionary<(string System, string Comparison), (M1Counts Sum, HashSet<string> Cases)> m1 = [];
    readonly Dictionary<(string System, string Cause), (long Truth, long Found, HashSet<string> Cases)> m2 = [];
    readonly Dictionary<string, (long Match, long Total, HashSet<string> Cases)> status = [];

    /// <summary>M1 한 행을 더한다.</summary>
    public void AddM1(string system, string caseId, string comparison, M1Counts c)
    {
        var k = (system, comparison);
        if (!m1.TryGetValue(k, out var cur)) cur = (new M1Counts(0, 0, 0, 0, 0, 0, 0, 0, 0, c.ResidualFilledCells is null ? null : 0, c.ResidualBaseCells is null ? null : 0), new HashSet<string>());
        var s = cur.Sum;
        cur.Sum = new M1Counts(s.TruthCells + c.TruthCells, s.PredictedCells + c.PredictedCells, s.Tp + c.Tp, s.Fp + c.Fp, s.Fn + c.Fn,
            s.RowsUnion + c.RowsUnion, s.RowsExact + c.RowsExact, s.VolatileCells + c.VolatileCells, s.VolatilePresentFormatOk + c.VolatilePresentFormatOk,
            s.ResidualFilledCells is null || c.ResidualFilledCells is null ? null : s.ResidualFilledCells + c.ResidualFilledCells,
            s.ResidualBaseCells is null || c.ResidualBaseCells is null ? null : s.ResidualBaseCells + c.ResidualBaseCells)
        {
            IdentityRows = s.IdentityRows + c.IdentityRows,
            IdentityPaired = s.IdentityPaired + c.IdentityPaired,
        };
        cur.Cases.Add(caseId);
        m1[k] = cur;
    }

    /// <summary>M2 한 버킷을 더한다.</summary>
    public void AddM2(string system, string caseId, M2Counts c)
    {
        var k = (system, c.Cause);
        if (!m2.TryGetValue(k, out var cur)) cur = (0, 0, new HashSet<string>());
        cur.Truth += c.TruthRows;
        cur.Found += c.FoundRows;
        if (c.TruthRows > 0) cur.Cases.Add(caseId);
        m2[k] = cur;
    }

    /// <summary>M3 한 사례·반복(범위별 계수, AttributionMetrics.Compute 결과)을 더한다.</summary>
    public void AddM3(string system, string caseId, IReadOnlyList<M3Counts> counts)
    {
        var certainPredicted = counts.FirstOrDefault(c => c.Confidence == "certain")?.PredictedEdges ?? 0;
        foreach (var c in counts)
        {
            if (!m3.TryGetValue((system, c.Confidence), out var s)) m3[(system, c.Confidence)] = s = new M3Sum();
            s.Predicted += c.PredictedEdges;
            s.PredictedMatched += c.PredictedMatched;
            s.Truth += c.TruthEdges;
            s.TruthFound += c.TruthFound;
            s.AmbGroups += c.AmbiguousGroups ?? 0;
            s.AmbHit += c.AmbiguousGroupsHit ?? 0;
            s.CertainPredicted += certainPredicted;
            s.CertainOver += (c.CertainWrong ?? 0) + (c.CertainOverclaim ?? 0);
            if (c.ManualSelectTables is long m) { s.Manual += m; s.ManualRuns++; }
            s.Cases.Add(caseId);
        }
    }

    /// <summary>사례 상태 일치 한 건을 더한다.</summary>
    public void AddStatus(string system, string caseId, bool match)
    {
        if (!status.TryGetValue(system, out var cur)) cur = (0, 0, new HashSet<string>());
        cur.Total++;
        if (match) cur.Match++;
        cur.Cases.Add(caseId);
        status[system] = cur;
    }

    /// <summary>metrics.csv 행들(MetricsCsv.SuiteColumns 순서).</summary>
    public IEnumerable<IReadOnlyList<string>> Rows(string runId)
    {
        static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<string> Row(string system, string metric, string scope, long num, long den, int cases) =>
            [runId, system, metric, scope, I(num), I(den), MetricsCsv.Rate(num, den), cases.ToString(CultureInfo.InvariantCulture)];

        foreach (var ((system, cmp), (s, cases)) in m1.OrderBy(x => x.Key.System, StringComparer.Ordinal).ThenBy(x => x.Key.Comparison, StringComparer.Ordinal))
        {
            yield return Row(system, "M1.precision", cmp, s.Tp, s.Tp + s.Fp, cases.Count);
            yield return Row(system, "M1.recall", cmp, s.Tp, s.Tp + s.Fn, cases.Count);
            yield return Row(system, "M1.f1", cmp, 2 * s.Tp, 2 * s.Tp + s.Fp + s.Fn, cases.Count);
            yield return Row(system, "M1.row_exact_rate", cmp, s.RowsExact, s.RowsUnion, cases.Count);
            yield return Row(system, "M1.volatile_format_ok_rate", cmp, s.VolatilePresentFormatOk, s.VolatileCells, cases.Count);
            if (s.ResidualFilledCells is long r) yield return Row(system, "M1.residual_fill_rate", cmp, r, s.ResidualBaseCells ?? 0, cases.Count);
            if (s.IdentityRows > 0) yield return Row(system, "M1.identity_pair_rate", cmp, s.IdentityPaired, s.IdentityRows, cases.Count);
        }
        foreach (var ((system, cause), (truth, found, cases)) in m2.OrderBy(x => x.Key.System, StringComparer.Ordinal)
                     .ThenBy(x => x.Key.Cause == "ALL" ? int.MaxValue : HandCase.Causes.ToList().IndexOf(x.Key.Cause)))
            yield return Row(system, "M2.recall", cause, found, truth, cases.Count);
        foreach (var ((system, scope), s) in m3.OrderBy(x => x.Key.System, StringComparer.Ordinal)
                     .ThenBy(x => AttributionMetrics.Scopes.ToList().IndexOf(x.Key.Scope)))
        {
            yield return Row(system, "M3.precision", scope, s.PredictedMatched, s.Predicted, s.Cases.Count);
            yield return Row(system, "M3.recall", scope, s.TruthFound, s.Truth, s.Cases.Count);
            if (scope == "ambiguous") yield return Row(system, "M3.ambiguous_hit_rate", scope, s.AmbHit, s.AmbGroups, s.Cases.Count);
            if (scope == "certain") yield return Row(system, "M3.certain_overconfident", scope, s.CertainOver, s.CertainPredicted, s.Cases.Count);
            if (scope == "all" && s.ManualRuns > 0) yield return Row(system, "M3.manual_select_tables", scope, s.Manual, s.ManualRuns, s.Cases.Count);
        }
        foreach (var (system, (match, total, cases)) in status.OrderBy(x => x.Key, StringComparer.Ordinal))
            yield return Row(system, "status_match_rate", "case", match, total, cases.Count);
    }
}
