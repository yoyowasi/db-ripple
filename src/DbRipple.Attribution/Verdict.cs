using DbRipple.Contracts;

namespace DbRipple.Attribution;

/// <summary>FK 간선 하나(부모 묶음 → 자식 묶음).</summary>
internal sealed record FkLink(string ParentFiringId, FkInfo Fk, FkAction Action, FkEvent Event, long MatchedRows, Confidence Confidence);

/// <summary>트리거 후보 한 쌍. ParentFiringId가 null이면 INSTEAD OF 실행(문장 노드에서 REPLACES).</summary>
internal sealed record TriggerPair(string? ParentFiringId, string Trigger, int StatementIndex);

/// <summary>묶음 하나의 귀속 판정(Firing.Cause와 그래프 간선의 재료).</summary>
internal sealed class Verdict
{
    public required CauseKind Kind { get; init; }
    public required Confidence Confidence { get; set; }
    public required OrderGuarantee Order { get; set; }
    public string? ParentFiringId { get; init; }
    public ForeignKeyCause? ForeignKey { get; init; }
    public List<FkLink> FkLinks { get; } = [];
    public List<TriggerPair> TriggerPairs { get; } = [];
    public List<Evidence> Evidence { get; } = [];
    /// <summary>트리거 후보(TriggerPairs)의 확신도. FK로 일부만 설명된 묶음에서만 Confidence와 다르다.</summary>
    public Confidence? TriggerConfidence { get; set; }
    public Confidence WritesConfidence => TriggerConfidence ?? Confidence;

    public Cause ToCause() => new()
    {
        Kind = Kind,
        Confidence = Confidence,
        ParentFiringId = ParentFiringId,
        ForeignKey = ForeignKey,
        TriggerCandidates = TriggerPairs.Select(p => p.Trigger).Distinct(CatalogIndex.Names).ToList(),
        Evidence = Evidence.Distinct().ToList(),
    };
}

internal static class Conf
{
    /// <summary>둘 중 약한 확신도(certain &lt; inferred &lt; ambiguous &lt; unexplained).</summary>
    public static Confidence Weaker(Confidence a, Confidence b) => (Confidence)Math.Max((int)a, (int)b);

    /// <summary>후보 수로 정하는 트리거 확신도(§7.2 4단계): 1 → inferred, 여럿 → ambiguous, 0 → unexplained.</summary>
    public static Confidence ByCount(int n) => n switch { 0 => Confidence.Unexplained, 1 => Confidence.Inferred, _ => Confidence.Ambiguous };
}
