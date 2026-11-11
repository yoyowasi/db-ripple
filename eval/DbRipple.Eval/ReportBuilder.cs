using System.Globalization;
using System.Text;

namespace DbRipple.Eval;

/// <summary>
/// 보고서 생성(§10.6 "보고서 표는 CSV에서만 만든다"). 실행 폴더의 CSV(cases·metrics·m4·m5)만 읽어 report.md를 만든다. DB·제품 코드·env.json은 쓰지 않는다.
/// 수치는 CSV 칸을 그대로 옮긴다(다시 계산하지 않는다). 비율 칸이 비면 "—"로 쓴다. 손으로 채울 칸(M5 브라우저 열, Q42)은 CSV에 값이 생기면 그대로 나온다.
/// 스파이크 결과·정답 정정 기록("확인한 사실과 추정" 절)은 이 생성기 밖에서 사람이 덧붙인다.
/// </summary>
public static class ReportBuilder
{
    /// <summary>보고서 파일 이름.</summary>
    public const string FileName = "report.md";

    /// <summary>runDir의 CSV로 report.md를 쓰고 경로를 돌려준다. m5Dir가 있으면 거기의 m5.csv도 싣는다(없으면 runDir의 m5.csv).</summary>
    public static string Write(string runDir, string? m5Dir = null)
    {
        var path = Path.Combine(runDir, FileName);
        File.WriteAllText(path, Build(runDir, m5Dir), MetricsCsv.Utf8NoBom);
        return path;
    }

    /// <summary>보고서 본문(Markdown).</summary>
    public static string Build(string runDir, string? m5Dir = null)
    {
        var cases = MetricsCsv.Read(Path.Combine(runDir, "cases.csv"));
        var metrics = MetricsCsv.Read(Path.Combine(runDir, "metrics.csv"));
        var m4 = MetricsCsv.Read(Path.Combine(runDir, "m4.csv"));
        var m5 = MetricsCsv.Read(Path.Combine(m5Dir ?? runDir, "m5.csv"));
        var runIds = new[] { cases, metrics, m4, m5 }.Where(t => t is not null).SelectMany(t => t!.Rows.Select(r => r.GetValueOrDefault("run_id") ?? ""))
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.Append("# 평가 보고서").Append(runIds.Count > 0 ? " — " + string.Join(", ", runIds) : "").Append("\n\n");
        sb.Append("이 보고서의 수치는 아래 CSV에서만 옮겼다(손으로 쓴 수치 없음, 설계 §10.6). 열 정의는 `eval/DbRipple.Eval/MetricsCsv.cs` 주석에 있다.\n\n");
        foreach (var (name, t, dir) in new[] { ("cases.csv", cases, runDir), ("metrics.csv", metrics, runDir), ("m4.csv", m4, runDir), ("m5.csv", m5, m5Dir ?? runDir) })
            sb.Append("- `").Append(name).Append("`: ").Append(t is null ? "없음" : $"{t.Rows.Count}행").Append(dir != runDir ? $" (`{dir}`)" : "").Append('\n');
        sb.Append('\n');

        Definitions(sb);
        if (cases is not null) CaseSummary(sb, cases);
        if (metrics is not null)
        {
            var rows = metrics.Rows;
            Section(sb, "M1 일치율(셀 단위, 마이크로 평균)", "comparison: total = 미리보기 대 오라클(E_total), obs = 평가 훅 대 오라클(E_obs), cap = 미리보기 재구성 대 평가 훅(E_cap).",
                ["system", "comparison", "precision", "recall", "f1", "행 완전 일치", "비결정 셀 존재·형식", "잔차로 메운 셀", "IDENTITY 짝"],
                Pivot(rows, "M1.", ["M1.precision", "M1.recall", "M1.f1", "M1.row_exact_rate", "M1.volatile_format_ok_rate", "M1.residual_fill_rate", "M1.identity_pair_rate"]));
            Section(sb, "M2 탐지(원인별 재현율, (테이블, 키)당 한 번)", null,
                ["system", "cause", "recall"], Pivot(rows, "M2.", ["M2.recall"]));
            Section(sb, "M3 귀속 정확도(간선 키, 확신도별)", "ambiguous 후보 포함률은 정답 간선이 후보 집합에 든 비율, certain 과신은 목표 0. 작업량은 대조군의 직접 찾아 SELECT할 테이블 수(사례·반복 평균).",
                ["system", "확신도", "precision", "recall", "ambiguous 후보 포함", "certain 과신", "작업량(테이블)"],
                Pivot(rows, "M3.", ["M3.precision", "M3.recall", "M3.ambiguous_hit_rate", "M3.certain_overconfident", "M3.manual_select_tables"], countMetric: "M3.certain_overconfident"));
            Section(sb, "M4 순서 안정성(스위트)", null,
                ["system", "범위", "정규 그래프 안정 사례", "observed_only 변동률", "예외 반복"], Pivot(rows, "M4.", ["M4.canonical_stable_rate", "M4.observed_only_variation_rate", "M4.error_run_rate"]));
            Section(sb, "사례 상태 일치", null, ["system", "범위", "상태 일치"], Pivot(rows, "status_match_rate", ["status_match_rate"]));
        }
        if (m4 is not null)
            Section(sb, "M4 사례별", "canonical = 형제 순서를 뺀 정규 그래프, observed_only 변동률 = (k − 가장 흔한 서명 수) ÷ k, k = 예외를 뺀 유효 반복.",
                ["system", "case", "반복", "예외", "상태 가짓수", "정규 그래프 가짓수", "안정", "observed_only 간선", "observed_only 변동률"],
                m4.Rows.Select(r => (IReadOnlyList<string>)[Get(r, "system"), Get(r, "case_id"), Get(r, "repeats"), Get(r, "error_runs"), Get(r, "status_distinct"),
                    Dash(Get(r, "canonical_distinct")), Dash(Get(r, "canonical_stable")), Dash(Get(r, "observed_only_edges")), Dash(Get(r, "observed_only_variation_rate"))]).ToList());
        if (m5 is not null)
            Section(sb, "M5 응답 시간(ms, 워밍업 제외 p50·p95)", "수집 오버헤드 = execute p50 ÷ 오라클 execute p50. 브라우저 열은 SP9 측정값을 손으로 기록한다(Q42, 비어 있으면 —).",
                ["case", "연산", "N", "표본", "상태", "KEYS(상한 초과)", "total p50", "total p95", "execute p50", "oracle p50", "오버헤드", "첫 그래프", "첫 그리드"],
                m5.Rows.Select(r => (IReadOnlyList<string>)[Get(r, "case_id"), Get(r, "operation"), Get(r, "n"), Get(r, "samples"), Get(r, "statuses"), Get(r, "truncated"),
                    Dash(Get(r, "total_p50")), Dash(Get(r, "total_p95")), Dash(Get(r, "execute_p50")), Dash(Get(r, "oracle_execute_p50")),
                    Dash(Get(r, "overhead_ratio_p50")), Dash(Get(r, "browser_first_graph_ms")), Dash(Get(r, "browser_first_grid_ms"))]).ToList());
        if (cases is not null)
            Section(sb, "사례별 상태(preview)", null,
                ["case", "반복", "기대", "실제", "일치", "오류 기대", "오류 실제", "오라클 대 손 정답", "원문 배치"],
                cases.Rows.Where(r => Get(r, "system") == Baselines.Systems.Preview)
                    .Select(r => (IReadOnlyList<string>)[Get(r, "case_id"), Get(r, "repeat"), Get(r, "status_expected"), Get(r, "status_actual"), Get(r, "status_match"),
                        Dash(Get(r, "error_expected")), Dash(Get(r, "error_actual")), Get(r, "oracle_vs_hand_keys_match"), Dash(Get(r, "raw_batch_match"))]).ToList());
        return sb.ToString();
    }

    /// <summary>"지표 정의" 절(고정 문구, 수치 없음). 지표 사이 정의 차이를 표와 함께 읽도록 적는다(2026-11-11 검토 반영).</summary>
    static void Definitions(StringBuilder sb)
    {
        sb.Append("## 지표 정의\n\n");
        foreach (var line in DefinitionNotes) sb.Append("- ").Append(line).Append('\n');
        sb.Append('\n');
    }

    /// <summary>보고서 "지표 정의" 절의 문장(MetricsCsv 주석과 같은 내용).</summary>
    public static readonly IReadOnlyList<string> DefinitionNotes =
    [
        "M1은 (테이블, 키, 열) 셀 단위다. 비결정 셀은 존재·형식만 보고, IDENTITY는 짝 매핑 뒤 비교한다. M2는 (테이블, 키)당 한 번 센다.",
        "M3는 간선 키(문장, 부모, 자식, 종류, 이름) 단위이고 확신도별로 나눈다. certain 과신은 정답에 없는 certain 간선과 정답 확신도가 더 약한 certain 간선의 합이다.",
        "B0의 M3 예측 간선은 손 정답 DIRECT 간선 그대로라 B0 precision은 구성상 1.0이다(비교할 값은 recall).",
        "B0 필터는 테이블 단위라 직접 대상 테이블에서 같은 테이블 트리거가 바꾼 행도 B0 탐지로 센다(OUTPUT 기반 도구보다 후하게 잰다).",
        "직접 대상은 문장 정규식(스키마로 한정한 대상)과 손 정답 DIRECT 행 테이블의 합집합이다. `DELETE TOP (n) FROM s.t`는 정규식이 못 잡아 손 정답 DIRECT 행에만 기댄다.",
        "M4는 예외(error) 반복을 안정성 분모에서 빼고 따로 센다. 거부(rejected)는 결정적이라 그대로 센다.",
        "M5 오버헤드의 분자(샌드박스 execute)는 실행 봉투 비용(BEGIN TRAN, 제어 UPDATE, 경계 읽기, EXECUTE AS/REVERT)을 포함하고, 분모(오라클 execute)는 문장 실행만 잰다.",
    ];

    static void CaseSummary(StringBuilder sb, CsvTable cases)
    {
        var rows = cases.Rows.GroupBy(r => Get(r, "system")).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<string>)[g.Key,
                g.Select(r => Get(r, "case_id")).Distinct(StringComparer.Ordinal).Count().ToString(CultureInfo.InvariantCulture),
                g.Count().ToString(CultureInfo.InvariantCulture),
                Frac(g.Count(r => Get(r, "status_match") == "true"), g.Count()),
                Frac(g.Count(r => Get(r, "oracle_vs_hand_keys_match") == "true"), g.Count()),
                Frac(g.Count(r => Get(r, "raw_batch_match") == "true"), g.Count(r => Get(r, "raw_batch_match").Length > 0))]).ToList();
        Section(sb, "사례 요약", "행 = (사례, 반복). 오라클 대 손 정답이 다르면 정답 정정 기록 후보다(differences.txt).",
            ["system", "사례", "행", "상태 일치", "오라클 대 손 정답 일치", "원문 배치 일치"], rows);
    }

    // metrics.csv의 (system, scope)마다 한 줄, metric마다 한 칸("값 (분자/분모)").
    static List<IReadOnlyList<string>> Pivot(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string prefix, IReadOnlyList<string> metricOrder,
        string? countMetric = null)
    {
        var sel = rows.Where(r => Get(r, "metric").StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var keys = sel.Select(r => (System: Get(r, "system"), Scope: Get(r, "scope"))).Distinct().ToList();
        var result = new List<IReadOnlyList<string>>();
        foreach (var k in keys)
        {
            var line = new List<string> { k.System, k.Scope };
            foreach (var m in metricOrder)
            {
                var r = sel.FirstOrDefault(x => Get(x, "system") == k.System && Get(x, "scope") == k.Scope && Get(x, "metric") == m);
                if (r is null) { line.Add(""); continue; }
                if (m == countMetric) line.Add($"{Get(r, "numerator")} / {Get(r, "denominator")}");
                else line.Add($"{Dash(Get(r, "value"))} ({Get(r, "numerator")}/{Get(r, "denominator")})");
            }
            result.Add(line);
        }
        return result;
    }

    static void Section(StringBuilder sb, string title, string? note, IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        sb.Append("## ").Append(title).Append("\n\n");
        if (note is not null) sb.Append(note).Append("\n\n");
        if (rows.Count == 0) { sb.Append("(행 없음)\n\n"); return; }
        sb.Append("| ").Append(string.Join(" | ", header.Select(Cell))).Append(" |\n");
        sb.Append('|').Append(string.Concat(header.Select(_ => "---|"))).Append('\n');
        foreach (var r in rows) sb.Append("| ").Append(string.Join(" | ", r.Select(Cell))).Append(" |\n");
        sb.Append('\n');
    }

    static string Get(IReadOnlyDictionary<string, string> r, string col) => r.TryGetValue(col, out var v) ? v : "";
    static string Dash(string v) => v.Length == 0 ? "—" : v;
    static string Cell(string v) => v.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    static string Frac(int a, int b) => b == 0 ? "—" : $"{a}/{b}";
}
