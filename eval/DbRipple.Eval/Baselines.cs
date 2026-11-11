using System.Text.RegularExpressions;

namespace DbRipple.Eval;

/// <summary>
/// 대조군(§10.5). 같은 사례·같은 지표로 잰다. system 열 값은 Systems의 상수다.
/// <list type="bullet">
/// <item>B0(직접 대상만, OUTPUT 기반 도구 모사): 오라클 순 변경을 문장의 직접 대상 테이블로 거른 것을 예측으로 쓴다. M3 예측 간선은 손 정답의 DIRECT 간선(certain)뿐이다.</item>
/// <item>B2(사본 + 최종 차이, Data Compare 자동화판): 오라클 순 변경을 그대로 예측으로 쓴다(M1은 오라클과 같다). 원인·순서는 없으므로 M3 예측 간선은 0개다.
/// 작업량 지표 manual_select_tables = 직접 대상이 아닌데 바뀐 테이블 수(사용자가 원인을 알려면 직접 찾아 SELECT해야 했을 테이블).</item>
/// <item>B1(FK 메타데이터 정적 예측)은 구현하지 않았다(자를 목록 1순위, §11.4).</item>
/// </list>
/// 상태(status_actual)는 둘 다 오라클 상태다.
/// </summary>
public static class Baselines
{
    /// <summary>system 열 값.</summary>
    public static class Systems
    {
        /// <summary>제품.</summary>
        public const string Preview = "preview";
        /// <summary>직접 대상만.</summary>
        public const string B0 = "B0";
        /// <summary>사본 + 최종 차이.</summary>
        public const string B2 = "B2";
        /// <summary>CLI가 받는 값.</summary>
        public static readonly IReadOnlyList<string> All = [Preview, B0, B2];
    }

    // 스키마로 한정한 대상 이름만 잡는다(별칭 UPDATE a … FROM t a는 손 정답 DIRECT 행으로 보완).
    static readonly Regex Target = new(
        @"^\s*(?:UPDATE|DELETE(?:\s+FROM)?|INSERT(?:\s+INTO)?|MERGE(?:\s+INTO)?)\s+(?:TOP\s*\([^)]*\)\s*(?:PERCENT\s+)?)?(?<t>(?:\[[^\]]+\]|[\w#@$]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w#@$]+))+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// 문장의 직접 대상 테이블("스키마.테이블", 대소문자 무시): 문장 첫 DML의 스키마 한정 대상(대괄호 제거, 세 부분 이름이면 뒤 두 부분) ∪ 손 정답에서 원인이 DIRECT인 행의 테이블.
    /// </summary>
    public static IReadOnlySet<string> DirectTargets(HandCase hand)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sql in hand.Statements)
        {
            var m = Target.Match(sql);
            if (!m.Success) continue;
            var parts = m.Groups["t"].Value.Split('.').Select(p => p.Trim().Trim('[', ']')).ToList();
            if (parts.Count >= 2) set.Add(parts[^2] + "." + parts[^1]);
        }
        foreach (var r in hand.Rows.Where(r => r.Causes.Contains("DIRECT"))) set.Add(r.Table);
        return set;
    }

    /// <summary>B0 예측: 직접 대상 테이블의 오라클 순 변경만.</summary>
    public static IReadOnlyList<OracleRowChange> B0Changes(HandCase hand, IReadOnlyList<OracleRowChange> oracle)
    {
        var direct = DirectTargets(hand);
        return oracle.Where(o => direct.Contains(o.Table)).ToList();
    }

    /// <summary>B0 예측 간선: 손 정답의 DIRECT 간선(직접 대상 변경은 확실히 안다).</summary>
    public static IReadOnlyList<PredictedEdge> B0Edges(HandCase hand) =>
        hand.Edges.Where(e => e.Kind == "DIRECT")
            .Select(e => new PredictedEdge(e.StatementIndex, e.Parent, e.Child, e.Kind, e.Name, "certain", "guaranteed", null))
            .ToList();

    /// <summary>작업량: 직접 대상이 아닌데 오라클에서 바뀐 테이블 수.</summary>
    public static long ManualSelectTables(HandCase hand, IReadOnlyList<OracleRowChange> oracle)
    {
        var direct = DirectTargets(hand);
        return oracle.Select(o => o.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count(t => !direct.Contains(t));
    }

    /// <summary>"preview,B0,B2" 형식을 읽는다. 모르는 값이면 ArgumentException.</summary>
    public static IReadOnlyList<string> ParseSystems(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return [Systems.Preview];
        var result = new List<string>();
        foreach (var s in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = Systems.All.FirstOrDefault(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"모르는 system {s}(허용: {string.Join(",", Systems.All)}, B1은 구현하지 않음)");
            if (!result.Contains(hit)) result.Add(hit);
        }
        return result;
    }
}
