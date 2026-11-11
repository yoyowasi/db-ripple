using System.Globalization;
using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>
/// M5 측정 한 번(§10.4). Timings는 제품 PreviewResult.TimingsMs 그대로, OracleExecuteMs는 같은 문장을 오라클(수집 장치 없는 원본)에서 실행한 시간.
/// Warmup이면 집계에서 뺀다. Status는 미리보기 상태 문자열(ok, truncated …, 예외면 error).
/// </summary>
public sealed record M5Sample(string CaseId, string Operation, long N, int Iteration, bool Warmup, string Status, TimingsMs? Timings, long? OracleExecuteMs)
{
    /// <summary>행 이미지 수(capture.imageCount).</summary>
    public long ImageCount { get; init; }
    /// <summary>상한 초과로 KEYS 모드가 됐는지(capture.truncated).</summary>
    public bool Truncated { get; init; }
    /// <summary>묶음 수.</summary>
    public long Firings { get; init; }
    /// <summary>순 변경 수(netSummary 합).</summary>
    public long NetChanges { get; init; }
}

/// <summary>
/// M5 집계(m5.csv). (사례, 연산, N)마다 1행. 단계별 p50·p95는 워밍업을 뺀 측정값의 선형 보간 백분위수(정렬 값 x, 위치 (n−1)·p)다.
/// total = 7단계 합. overhead_ratio_p50 = execute p50 ÷ oracle_execute p50(§10.4 "샌드박스 execute ÷ 오라클 execute", 오라클 p50이 0이면 빈 칸).
/// 브라우저 열(browser_first_graph_ms, browser_first_grid_ms)은 비워 두고 SP9 화면 측정값으로 손으로 채운다(Q42).
/// </summary>
public static class ResponseTime
{
    /// <summary>단계 이름(m5.csv 열 머리 "{단계}_p50"·"{단계}_p95").</summary>
    public static readonly IReadOnlyList<string> Stages = ["analyze", "execute", "read_log", "reread", "net_diff", "resolve", "serialize", "total", "oracle_execute"];

    /// <summary>선형 보간 백분위수(p는 0~1). 값이 없으면 null.</summary>
    public static double? Percentile(IEnumerable<long> values, double p)
    {
        var s = values.Order().ToList();
        if (s.Count == 0) return null;
        var pos = (s.Count - 1) * p;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    static long? Stage(M5Sample x, string stage)
    {
        if (stage == "oracle_execute") return x.OracleExecuteMs;
        if (x.Timings is not { } t) return null;
        return stage switch
        {
            "analyze" => t.Analyze,
            "execute" => t.Execute,
            "read_log" => t.ReadLog,
            "reread" => t.Reread,
            "net_diff" => t.NetDiff,
            "resolve" => t.Resolve,
            "serialize" => t.Serialize,
            "total" => t.Analyze + t.Execute + t.ReadLog + t.Reread + t.NetDiff + t.Resolve + t.Serialize,
            _ => throw new ArgumentException(stage),
        };
    }

    /// <summary>밀리초 칸(소수 1자리, null이면 빈 칸).</summary>
    public static string Ms(double? v) => v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "";

    /// <summary>m5.csv 행들(MetricsCsv.M5Columns 순서, 사례·N 순).</summary>
    public static IEnumerable<IReadOnlyList<string>> Rows(string runId, IEnumerable<M5Sample> samples)
    {
        foreach (var g in samples.Where(s => !s.Warmup).GroupBy(s => (s.CaseId, s.Operation, s.N))
                     .OrderBy(g => g.Key.CaseId, StringComparer.Ordinal).ThenBy(g => g.Key.N))
        {
            var list = g.ToList();
            var ok = list.Where(s => s.Timings is not null).ToList();
            var row = new List<string>
            {
                runId, g.Key.CaseId, g.Key.Operation, MetricsCsv.Int(g.Key.N), MetricsCsv.Int(list.Count), MetricsCsv.Int(ok.Count),
                string.Join(";", list.Select(s => s.Status).Distinct().Order(StringComparer.Ordinal)),
                MetricsCsv.Int(ok.Count == 0 ? null : ok.Max(s => s.ImageCount)), MetricsCsv.Bool(ok.Count == 0 ? null : ok.Any(s => s.Truncated)),
                MetricsCsv.Int(ok.Count == 0 ? null : ok.Max(s => s.Firings)), MetricsCsv.Int(ok.Count == 0 ? null : ok.Max(s => s.NetChanges)),
            };
            foreach (var stage in Stages)
            {
                var vals = (stage == "oracle_execute" ? list : ok).Select(s => Stage(s, stage)).OfType<long>().ToList();
                row.Add(Ms(Percentile(vals, 0.5)));
                row.Add(Ms(Percentile(vals, 0.95)));
            }
            var ex = Percentile(ok.Select(s => s.Timings!.Execute), 0.5);
            var or = Percentile(list.Select(s => s.OracleExecuteMs).OfType<long>(), 0.5);
            row.Add(ex is double e && or is double o && o > 0 ? (e / o).ToString("0.0000", CultureInfo.InvariantCulture) : "");
            row.Add(""); // browser_first_graph_ms(Q42)
            row.Add(""); // browser_first_grid_ms(Q42)
            yield return row;
        }
    }

    /// <summary>미리보기 결과에서 표본 하나를 만든다(결과가 없으면 Timings = null).</summary>
    public static M5Sample FromResult(string caseId, string operation, long n, int iteration, bool warmup, PreviewResult? result, string statusWhenMissing, long? oracleExecuteMs) =>
        new(caseId, operation, n, iteration, warmup, result is null ? statusWhenMissing : CaseScorer.ContractEnumText(result.Status), result?.TimingsMs, oracleExecuteMs)
        {
            ImageCount = result?.Capture?.ImageCount ?? 0,
            Truncated = result?.Capture?.Truncated ?? false,
            Firings = result?.Firings.Count ?? 0,
            NetChanges = result?.NetSummary.Sum(r => r.Inserted + r.Updated + r.Deleted + r.MultisetAdded + r.MultisetRemoved) ?? 0,
        };
}
