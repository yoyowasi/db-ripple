using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace DbRipple.Attribution.Tests;

/// <summary>
/// 큰 DELETE C13 합성 측정(DB 없음, 2026-11-17). 기본은 꺼져 있다(CI 시간). 켜기: DBR_PERF=1.
/// DBR_PERF_SIZES(부모 행 수, 기본 "10000,50000,100000" = M5 N), DBR_PERF_REPEAT(기본 3), DBR_PERF_OUT(CSV 경로, 선택).
/// 입력 만들기는 재지 않고 CausalResolver.Resolve만 잰다. 크기마다 워밍업 1회 뒤 반복하고 중앙값을 낸다.
/// </summary>
public class LargeDeletePerfTests(ITestOutputHelper output)
{
    [Fact]
    public void Cl20_resolve_scaling()
    {
        if (Environment.GetEnvironmentVariable("DBR_PERF") != "1")
        {
            output.WriteLine("DBR_PERF=1이 아니라 건너뜀");
            return;
        }
        var sizes = (Environment.GetEnvironmentVariable("DBR_PERF_SIZES") ?? "10000,50000,100000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
        var repeat = int.Parse(Environment.GetEnvironmentVariable("DBR_PERF_REPEAT") ?? "3", CultureInfo.InvariantCulture);
        var csv = Environment.GetEnvironmentVariable("DBR_PERF_OUT");
        var lines = new List<string> { "parents,rows,sample,resolve_ms,alloc_mb,peak_ws_mb" };

        foreach (var n in sizes)
        {
            var samples = new List<long>();
            for (var i = 0; i <= repeat; i++)
            {
                var input = LargeDeleteData.Cl20(n);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                var r = new CausalResolver().Resolve(input);
                sw.Stop();
                var allocMb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / (1024 * 1024);
                Assert.Equal(5 * n, r.Model.RowChanges.Count);
                var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
                output.WriteLine($"N={n} #{i}{(i == 0 ? "(워밍업)" : "")} {sw.ElapsedMilliseconds}ms alloc={allocMb}MB peakWS={peakMb}MB");
                if (i == 0) continue; // 워밍업
                samples.Add(sw.ElapsedMilliseconds);
                lines.Add($"{n},{5 * n},{i},{sw.ElapsedMilliseconds},{allocMb},{peakMb}");
            }
            var sorted = samples.Order().ToList();
            output.WriteLine($"N={n} rows={5 * n} resolve ms: {string.Join(" / ", samples)} median {sorted[sorted.Count / 2]}");
        }
        if (csv is not null) File.WriteAllLines(csv, lines);
    }
}
