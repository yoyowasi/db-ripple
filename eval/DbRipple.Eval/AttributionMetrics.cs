using System.Globalization;
using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>M3 간선 키(§10.4: 문장, 부모, 자식, 종류, FK 또는 트리거 이름). 대소문자는 무시한다(키 문자열을 대문자로 만든다).</summary>
public static class EdgeKeys
{
    /// <summary>"문장|부모|자식|종류|이름"(대문자). 이름이 없으면 빈 칸.</summary>
    public static string Compose(int statementIndex, string parent, string child, string kind, string? name) =>
        string.Join('|', statementIndex.ToString(CultureInfo.InvariantCulture), parent, child, kind, name ?? "").ToUpperInvariant();
}

/// <summary>
/// 예측 간선 하나(하네스 형식). 노드는 answer-format.md 노드 참조 표기(S, F:schema.table:OP, T:schema.trigger, R:schema.table, E:번호)로 바꾼 것이다.
/// Confidence·OrderGuarantee는 설계 문자열(certain, observed_only …). Observed는 FIRES 간선의 트리거 노드 observed, 그 밖은 null.
/// ChildBase는 #k를 뗀 자식 표기(M4 정규 그래프용), ChildRank는 자식 묶음의 문장 안 captureSeq 순번(관측 순서, M4 observed_only 서명용).
/// </summary>
public sealed record PredictedEdge(int StatementIndex, string Parent, string Child, string Kind, string? Name, string Confidence, string OrderGuarantee, bool? Observed)
{
    /// <summary>M3 간선 키.</summary>
    public string Key => EdgeKeys.Compose(StatementIndex, Parent, Child, Kind, Name);
    /// <summary>#k 없는 부모 표기(깊이 붙음, M4).</summary>
    public string ParentShape { get; init; } = "";
    /// <summary>#k 없는 자식 표기(깊이 붙음, M4).</summary>
    public string ChildShape { get; init; } = "";
    /// <summary>자식 노드의 관측 순번(문장 안 captureSeq 순, 묶음이 아니면 -1).</summary>
    public int ChildRank { get; init; } = -1;
}

/// <summary>
/// 제품 그래프(PreviewResult.Graph)를 하네스 간선 형식으로 바꾼다. 제품 C13 코드를 쓰지 않고 계약 DTO만 읽는다.
/// <list type="bullet">
/// <item>묶음 노드 F:{table}:{OP}, 트리거 노드 T:{trigger}, 잔차 R:{table}, 오류 E:{번호}, 문장 S, 접힌 묶음 G:{id}.</item>
/// <item>한 문장에 같은 표기가 여럿이면 인과 순서로 #k(1부터)를 붙인다. 인과 순서 = (깊이, 문장 안 트리거 문맥 트리 전위 순번). 트리거는 부모 묶음의 그 순서
/// (INSTEAD OF처럼 부모가 없으면 맨 앞). 하나뿐이면 #k를 붙이지 않는다(손 정답 정규화 NodeRefs와 같은 결과).</item>
/// <item>이름: FK는 FK 이름, FIRES·REPLACES는 도착 트리거, WRITES·RESIDUAL은 출발 트리거(출발이 묶음이면 null), DIRECT·BLOCKED는 null.</item>
/// <item>문장 번호: 출발 노드의 문장, 없으면 도착 노드의 문장. 잔차 노드는 간선으로만 문장이 정해진다.</item>
/// </list>
/// </summary>
public static class GraphEdges
{
    /// <summary>결과의 간선(그래프가 없으면 빈 목록).</summary>
    public static IReadOnlyList<PredictedEdge> FromResult(PreviewResult? result)
    {
        var g = result?.Graph;
        if (result is null || g is null) return [];
        var firings = result.Firings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var preorder = Reconstruction.PreorderIndex(result.Firings);
        var nodes = g.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var label = new Dictionary<string, string>(StringComparer.Ordinal);
        var shape = new Dictionary<string, string>(StringComparer.Ordinal);
        var stmtOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var st in result.Firings.GroupBy(f => f.StatementIndex))
        {
            var i = 0;
            foreach (var f in st.OrderBy(f => f.CaptureSeq)) rank[f.Id] = i++;
        }

        (int, int) Causal(string? firingId) =>
            firingId is not null && firings.TryGetValue(firingId, out var f)
                ? (f.Depth, preorder.TryGetValue(f.Id, out var p) ? p.Preorder : int.MaxValue)
                : (0, -1);

        foreach (var n in g.Nodes)
            switch (n)
            {
                case StatementNode s:
                    label[n.Id] = shape[n.Id] = "S";
                    stmtOf[n.Id] = s.StatementIndex;
                    break;
                case ErrorNode e:
                    label[n.Id] = shape[n.Id] = "E:" + e.ErrorNumber.ToString(CultureInfo.InvariantCulture);
                    if (n.Id.StartsWith("err-", StringComparison.Ordinal) && int.TryParse(n.Id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var si))
                        stmtOf[n.Id] = si;
                    break;
                case GroupNode gn:
                    label[n.Id] = "G:" + gn.Id;
                    shape[n.Id] = "G:" + gn.Members.Count.ToString(CultureInfo.InvariantCulture);
                    break;
            }

        foreach (var grp in g.Nodes.OfType<FiringNode>()
                     .GroupBy(f => (Stmt: firings.TryGetValue(f.FiringId, out var x) ? x.StatementIndex : 0, Base: $"F:{f.Table}:{CaseScorer.ContractEnumText(f.Operation)}")))
        {
            var list = grp.OrderBy(f => Causal(f.FiringId)).ThenBy(f => f.CaptureSeq).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                label[list[i].Id] = list.Count == 1 ? grp.Key.Base : $"{grp.Key.Base}#{i + 1}";
                shape[list[i].Id] = $"{grp.Key.Base}@{list[i].Depth.ToString(CultureInfo.InvariantCulture)}";
                stmtOf[list[i].Id] = grp.Key.Stmt;
                if (rank.TryGetValue(list[i].FiringId, out var r)) rank[list[i].Id] = r;
            }
        }
        foreach (var grp in g.Nodes.OfType<TriggerNode>().GroupBy(t => (t.StatementIndex, Base: "T:" + t.Trigger)))
        {
            var list = grp.OrderBy(t => Causal(t.ParentFiringId)).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                label[list[i].Id] = list.Count == 1 ? grp.Key.Base : $"{grp.Key.Base}#{i + 1}";
                var parentDepth = list[i].ParentFiringId is { } p && firings.TryGetValue(p, out var pf) ? pf.Depth : 0;
                shape[list[i].Id] = $"{grp.Key.Base}@{parentDepth.ToString(CultureInfo.InvariantCulture)}";
                stmtOf[list[i].Id] = grp.Key.StatementIndex;
            }
        }
        // 잔차: 문장은 들어오는 간선의 출발 노드로 정한다. 같은 문장·테이블 잔차가 여럿이면 id 순으로 #k.
        foreach (var r in g.Nodes.OfType<ResidualNode>())
        {
            var src = g.Edges.FirstOrDefault(e => e.Target == r.Id && stmtOf.ContainsKey(e.Source));
            if (src is not null) stmtOf[r.Id] = stmtOf[src.Source];
        }
        foreach (var grp in g.Nodes.OfType<ResidualNode>().GroupBy(r => (Stmt: stmtOf.GetValueOrDefault(r.Id), Base: "R:" + r.Table)))
        {
            var list = grp.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                label[list[i].Id] = list.Count == 1 ? grp.Key.Base : $"{grp.Key.Base}#{i + 1}";
                shape[list[i].Id] = grp.Key.Base;
            }
        }

        var edges = new List<PredictedEdge>();
        foreach (var e in g.Edges)
        {
            nodes.TryGetValue(e.Source, out var src);
            nodes.TryGetValue(e.Target, out var dst);
            string? name = e.Kind switch
            {
                EdgeKind.Fk => e.Fk,
                EdgeKind.Fires or EdgeKind.Replaces => (dst as TriggerNode)?.Trigger,
                EdgeKind.Writes or EdgeKind.Residual => (src as TriggerNode)?.Trigger,
                _ => null,
            };
            var si = stmtOf.TryGetValue(e.Source, out var a) ? a : stmtOf.GetValueOrDefault(e.Target);
            edges.Add(new PredictedEdge(si, label.GetValueOrDefault(e.Source) ?? "?" + e.Source, label.GetValueOrDefault(e.Target) ?? "?" + e.Target,
                CaseScorer.ContractEnumText(e.Kind), name, CaseScorer.ContractEnumText(e.Confidence), CaseScorer.ContractEnumText(e.OrderGuarantee),
                e.Kind == EdgeKind.Fires && dst is TriggerNode tn ? tn.Observed : null)
            {
                ParentShape = shape.GetValueOrDefault(e.Source) ?? "?",
                ChildShape = shape.GetValueOrDefault(e.Target) ?? "?",
                ChildRank = rank.GetValueOrDefault(e.Target, -1),
            });
        }
        return edges;
    }
}

/// <summary>M3 한 확신도 범위(§10.4). Confidence = certain, inferred, ambiguous, unexplained 또는 all.</summary>
public sealed record M3Counts(string Confidence, long PredictedEdges, long TruthEdges, long PredictedMatched, long TruthFound, long ConfidenceMatch)
{
    /// <summary>ambiguous 후보 묶음 수((문장, 자식, 종류)마다 하나). ambiguous·all 행에만.</summary>
    public long? AmbiguousGroups { get; init; }
    /// <summary>정답 간선이 후보 묶음 안에 든 묶음 수.</summary>
    public long? AmbiguousGroupsHit { get; init; }
    /// <summary>certain인데 정답에 없는 간선 수(과신 오류). certain·all 행에만.</summary>
    public long? CertainWrong { get; init; }
    /// <summary>certain인데 정답 확신도가 certain보다 약한 간선 수(과신 오류).</summary>
    public long? CertainOverclaim { get; init; }
    /// <summary>맞힌 FIRES 간선 가운데 observed가 정답과 다른 수. all 행에만.</summary>
    public long? ObservedMismatch { get; init; }
    /// <summary>B2 작업량: 직접 대상이 아닌데 바뀐 테이블 수(사용자가 직접 찾아 SELECT해야 했을 테이블). all 행, B0·B2에만.</summary>
    public long? ManualSelectTables { get; init; }

    /// <summary>m3.csv 지표 칸(predicted_edges부터 manual_select_tables까지).</summary>
    public IReadOnlyList<string> CsvCells() =>
    [
        MetricsCsv.Int(PredictedEdges), MetricsCsv.Int(TruthEdges),
        MetricsCsv.Int(PredictedMatched), MetricsCsv.Rate(PredictedMatched, PredictedEdges),
        MetricsCsv.Int(TruthFound), MetricsCsv.Rate(TruthFound, TruthEdges),
        MetricsCsv.Int(ConfidenceMatch),
        MetricsCsv.Int(AmbiguousGroups), MetricsCsv.Int(AmbiguousGroupsHit),
        AmbiguousGroups is long g ? MetricsCsv.Rate(AmbiguousGroupsHit ?? 0, g) : "",
        MetricsCsv.Int(CertainWrong), MetricsCsv.Int(CertainOverclaim),
        CertainWrong is null && CertainOverclaim is null ? "" : MetricsCsv.Int((CertainWrong ?? 0) + (CertainOverclaim ?? 0)),
        MetricsCsv.Int(ObservedMismatch), MetricsCsv.Int(ManualSelectTables),
    ];
}

/// <summary>
/// M3 계산(§10.4). 단위는 간선 키(EdgeKeys). 손 정답 간선(HandCase.Edges)이 정답이다.
/// <list type="bullet">
/// <item>범위 c(certain·inferred·ambiguous·unexplained): predicted = 확신도 c인 예측 간선, predicted_matched = 그 가운데 키가 정답에 있는 수(정밀도 분자).
/// truth = 정답 확신도가 c인 간선, truth_found = 그 가운데 키가 예측에(확신도 무관) 있는 수(재현율 분자), confidence_match = 그 가운데 예측 확신도까지 c인 수.</item>
/// <item>all: 확신도 무관 합계. 같은 키가 예측에 여러 번 나오면 한 번만(가장 강한 확신도) 센다.</item>
/// <item>ambiguous 후보 포함: ambiguous 예측 간선을 (문장, 자식, 종류)로 묶어 후보 집합으로 보고, 정답 간선 키가 그 집합에 있으면 hit.</item>
/// <item>certain 과신 오류(목표 0): certain 예측인데 정답에 없는 키(certain_wrong) + 정답에는 있으나 정답 확신도가 certain이 아닌 키(certain_overclaim).</item>
/// </list>
/// </summary>
public static class AttributionMetrics
{
    /// <summary>확신도 범위(행 순서).</summary>
    public static readonly IReadOnlyList<string> Scopes = ["certain", "inferred", "ambiguous", "unexplained", "all"];

    static int Strength(string c) => c switch { "certain" => 3, "inferred" => 2, "ambiguous" => 1, _ => 0 };

    /// <summary>범위별 계수(Scopes 순서). manualSelectTables는 all 행에 싣는다(null이면 빈 칸).</summary>
    public static IReadOnlyList<M3Counts> Compute(IReadOnlyList<HandEdge> truth, IReadOnlyList<PredictedEdge> predicted, long? manualSelectTables = null)
    {
        var t = new Dictionary<string, HandEdge>(StringComparer.Ordinal);
        foreach (var e in truth) t.TryAdd(e.Key, e);
        var p = new Dictionary<string, PredictedEdge>(StringComparer.Ordinal);
        foreach (var e in predicted)
            if (!p.TryGetValue(e.Key, out var cur) || Strength(e.Confidence) > Strength(cur.Confidence)) p[e.Key] = e;

        var groups = predicted.Where(e => e.Confidence == "ambiguous")
            .GroupBy(e => (e.StatementIndex, Child: e.Child.ToUpperInvariant(), e.Kind))
            .Select(g => g.Select(x => x.Key).ToHashSet(StringComparer.Ordinal)).ToList();
        long ambGroups = groups.Count, ambHit = groups.Count(g => g.Any(t.ContainsKey));
        long certainWrong = p.Values.Count(e => e.Confidence == "certain" && !t.ContainsKey(e.Key));
        long certainOver = p.Values.Count(e => e.Confidence == "certain" && t.TryGetValue(e.Key, out var h) && h.Confidence != "certain");
        long observedMismatch = p.Values.Count(e => e.Observed is bool o && t.TryGetValue(e.Key, out var h) && h.Observed != o);

        var list = new List<M3Counts>();
        foreach (var scope in Scopes)
        {
            bool In(string c) => scope == "all" || c == scope;
            var pe = p.Values.Where(e => In(e.Confidence)).ToList();
            var te = t.Values.Where(e => In(e.Confidence)).ToList();
            var c = new M3Counts(scope, pe.Count, te.Count,
                pe.Count(e => t.ContainsKey(e.Key)),
                te.Count(e => p.ContainsKey(e.Key)),
                te.Count(e => p.TryGetValue(e.Key, out var x) && (scope == "all" ? x.Confidence == e.Confidence : x.Confidence == scope)));
            if (scope is "ambiguous" or "all") c = c with { AmbiguousGroups = ambGroups, AmbiguousGroupsHit = ambHit };
            if (scope is "certain" or "all") c = c with { CertainWrong = certainWrong, CertainOverclaim = certainOver };
            if (scope == "all") c = c with { ObservedMismatch = observedMismatch, ManualSelectTables = manualSelectTables };
            list.Add(c);
        }
        return list;
    }

    /// <summary>예측 간선 가운데 정답에 없는 것과 정답 가운데 예측에 없는 것(보고·정정 기록용).</summary>
    public static (IReadOnlyList<string> Extra, IReadOnlyList<string> Missing) Differences(IReadOnlyList<HandEdge> truth, IReadOnlyList<PredictedEdge> predicted)
    {
        var tk = truth.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var pk = predicted.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return (pk.Except(tk).Order(StringComparer.Ordinal).ToList(), tk.Except(pk).Order(StringComparer.Ordinal).ToList());
    }
}
