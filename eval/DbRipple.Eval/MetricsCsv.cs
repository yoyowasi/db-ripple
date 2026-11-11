using System.Globalization;
using System.Text;

namespace DbRipple.Eval;

/// <summary>
/// 평가 CSV 열 명세(§10.4 M1~M5, §10.6 cases.csv·metrics.csv). 보고서 표는 이 CSV에서만 만든다(손으로 쓴 수치 금지, ReportBuilder).
/// 공통 규칙: UTF-8(BOM 없음), 쉼표 구분, RFC 4180 인용, 소수는 InvariantCulture 소수점 4자리, 분모 0인 비율은 빈 칸.
/// system 열: preview(제품), B0(직접 대상만), B2(사본 + 최종 차이). 대조군도 같은 파일·같은 열을 쓴다(§10.5, Baselines). B1은 구현하지 않았다(자를 목록 1순위).
/// 파일: cases.csv·m1.csv·m2.csv·m3.csv·metrics.csv(cascadelab 실행마다), m4.csv(cascadelab, 반복 k회 묶음), m5.csv(m5 명령), report.md(report 명령, CSV만 읽음).
/// </summary>
public static class MetricsCsv
{
    /// <summary>
    /// cases.csv: (실행, 시스템, 사례, 반복)당 1행. 대조군 행의 status_actual·error_actual·statements_status_match는 오라클 값이고,
    /// preview_id·단계 시간·record_path는 빈 칸이다.
    /// </summary>
    public static readonly IReadOnlyList<string> CaseColumns =
    [
        "run_id", "system", "case_id", "pattern", "database", "repeat", "preview_id",
        "status_expected", "status_actual", "status_match", "error_expected", "error_actual",
        "statements_total", "statements_status_match",
        "oracle_vs_hand_keys_match", "raw_batch_match",
        "analyze_ms", "execute_ms", "read_log_ms", "reread_ms", "net_diff_ms", "resolve_ms", "serialize_ms", "oracle_execute_ms",
        "record_path",
    ];

    /// <summary>
    /// m1.csv: (실행, 시스템, 사례, 반복, comparison)당 1행. comparison: total(미리보기 대 오라클 = E_total), obs(평가 훅 대 오라클 = E_obs), cap(미리보기 대 평가 훅 = E_cap).
    /// 단위는 셀 사실 (테이블, 키, 열, 값)(CellFact). 비결정 셀은 tp/fp/fn에서 빼고 volatile_*로 따로 센다.
    /// identity_rows·identity_paired(3주차): 정답 쪽 IDENTITY 키 INSERTED 행 수와 예측 행과 짝지은 수(IdentityPairing, E2). 짝 매핑 뒤 셀을 비교한다. IDENTITY 테이블이 없으면 0.
    /// cap은 비교 전에 키 없는 테이블의 다중집합 키를 관측 가능 열로 다시 만든다(HeapKeys, 인계 2).
    /// 대조군 B0·B2는 total만 채우고 obs·cap은 빈 칸이다(평가 훅 없음).
    /// </summary>
    public static readonly IReadOnlyList<string> M1Columns =
    [
        "run_id", "system", "case_id", "repeat", "comparison",
        "truth_cells", "predicted_cells", "tp", "fp", "fn", "precision", "recall", "f1",
        "rows_union", "rows_exact", "row_exact_rate",
        "volatile_cells", "volatile_present_format_ok",
        "residual_filled_cells", "residual_fill_rate",
        "identity_rows", "identity_paired",
    ];

    /// <summary>
    /// m2.csv: (실행, 시스템, 사례, 반복, cause)당 1행. cause: DIRECT, FK_CASCADE, FK_SET_NULL, FK_SET_DEFAULT, TRIGGER, INSTEAD_OF(v0.2), SAME_TABLE_TRIGGER, ONE_OF(손 정답 oneOf).
    /// 단위는 (테이블, 키) 한 번(§10.4). found = 예측에 같은 (테이블, 키, 변경 종류)가 있음.
    /// 손 정답은 changes ∪ residualCommitted(v0.2)이고, 제품 예측은 NetChanges ∪ ResidualCommitted다. IDENTITY 행(v0.2 match)은 예측 쪽 키에 묶은 뒤(HandCase.Bind) 센다.
    /// 정의 차이(2026-11-11 검토, B0):
    /// <list type="bullet">
    /// <item>B0 필터는 테이블 단위다. 직접 대상 테이블에서 같은 테이블 트리거가 바꾼 행도 B0이 찾은 것으로 센다(OUTPUT 기반 도구보다 B0을 후하게 잰다).</item>
    /// <item>직접 대상은 문장 정규식(스키마로 한정한 대상)과 손 정답 DIRECT 행 테이블의 합집합이다. DELETE TOP (n) FROM s.t는 정규식이 못 잡아 손 정답 DIRECT 행에만 기댄다.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<string> M2Columns =
    [
        "run_id", "system", "case_id", "repeat", "cause",
        "truth_rows", "found_rows", "recall", "found_by_firing", "found_by_residual_only", "missed_keys",
    ];

    /// <summary>
    /// m3.csv(3주차): (실행, 시스템, 사례, 반복, confidence)당 1행. confidence: certain, inferred, ambiguous, unexplained, all(AttributionMetrics.Scopes).
    /// 단위는 간선 키 (문장, 부모, 자식, 종류, 이름)(EdgeKeys, §10.4). 제품 그래프는 answer-format 노드 참조 표기로 바꿔 비교한다(GraphEdges).
    /// <list type="bullet">
    /// <item>predicted_edges: 확신도가 그 범위인 예측 간선 수. predicted_matched: 그 가운데 키가 정답에 있는 수. precision = predicted_matched ÷ predicted_edges.</item>
    /// <item>truth_edges: 정답 확신도가 그 범위인 정답 간선 수. truth_found: 그 가운데 키가 예측에(확신도 무관) 있는 수. recall = truth_found ÷ truth_edges.</item>
    /// <item>confidence_match: truth_found 가운데 예측 확신도가 정답 확신도와 같은 수.</item>
    /// <item>ambiguous_groups·ambiguous_groups_hit·ambiguous_hit_rate(ambiguous·all 행): ambiguous 예측 간선을 (문장, 자식, 종류)로 묶은 후보 집합 수와 정답 간선이 든 집합 수.</item>
    /// <item>certain_wrong·certain_overclaim·certain_overconfident(certain·all 행): certain인데 정답에 없는 간선, 정답 확신도가 더 약한 간선, 둘의 합(목표 0).</item>
    /// <item>observed_mismatch(all 행): 맞힌 FIRES 간선 가운데 observed가 정답과 다른 수.</item>
    /// <item>manual_select_tables(all 행, B0·B2만): 직접 대상이 아닌데 바뀐 테이블 수(사용자가 직접 찾아 SELECT해야 했을 테이블, §10.5 B2 작업량). preview는 빈 칸.</item>
    /// <item>B2는 예측 간선 0(precision 빈 칸, recall 0). B0은 손 정답 DIRECT 간선만 certain으로 예측한다.</item>
    /// <item>정의 차이(2026-11-11 검토): B0의 M3 예측 간선은 손 정답 DIRECT 간선 그대로라 B0 precision은 구성상 1.0이다(비교할 값은 recall).</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<string> M3Columns =
    [
        "run_id", "system", "case_id", "repeat", "confidence",
        "predicted_edges", "truth_edges", "predicted_matched", "precision", "truth_found", "recall", "confidence_match",
        "ambiguous_groups", "ambiguous_groups_hit", "ambiguous_hit_rate",
        "certain_wrong", "certain_overclaim", "certain_overconfident",
        "observed_mismatch", "manual_select_tables",
    ];

    /// <summary>
    /// m4.csv(3주차): (실행, 시스템, 사례)당 1행(반복 k회를 모은다, OrderStability). preview만 쓴다(대조군은 결정적).
    /// <list type="bullet">
    /// <item>repeats: 전체 반복 수. error_runs: 파이프라인 예외(status error, 결과 없음) 반복 수. valid_runs = repeats − error_runs = k(아래 안정성 칸의 분모).
    /// 예외 반복은 빈 서명이라 넣으면 "모두 예외 = 안정"이 되므로 뺀다. 거부(rejected)는 결정적이라 유효 반복으로 센다(2026-11-11 검토 반영).
    /// status_distinct: 서로 다른 상태 수(예외 포함 전체 반복).</item>
    /// <item>canonical_distinct: 형제 순서를 뺀 정규 그래프 서명의 가짓수. canonical_stable: 1가지면 true. canonical_mode_share: 가장 흔한 서명의 비율.</item>
    /// <item>observed_only_edges: observed_only 간선 수(반복 중 최대). observed_only_distinct: observed_only 서명(간선 모양 + 자식 관측 순번) 가짓수.
    /// observed_only_variation_rate = (k − 가장 흔한 observed_only 서명 수) ÷ k.</item>
    /// <item>k = 0(모든 반복이 예외)이면 canonical_*·observed_only_*·canonical_mode_signature는 빈 칸이다.</item>
    /// <item>canonical_mode_signature: 가장 흔한 정규 그래프 서명(SHA-256 앞 16자리, 실행 사이 비교용).</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<string> M4Columns =
    [
        "run_id", "system", "case_id", "repeats", "error_runs", "valid_runs", "status_distinct",
        "canonical_distinct", "canonical_stable", "canonical_mode_share",
        "observed_only_edges", "observed_only_distinct", "observed_only_variation_rate",
        "canonical_mode_signature",
    ];

    /// <summary>
    /// m5.csv(3주차): (실행, 사례, 연산, N)당 1행(ResponseTime). N = 문장의 직접 대상 행 수(CL20 scaling, cl20.usp_Generate @parents = N).
    /// <list type="bullet">
    /// <item>samples: 워밍업을 뺀 측정 수. ok_samples: 미리보기 결과가 있는 수. statuses: 나온 상태(;로 구분).</item>
    /// <item>image_count·truncated·firings·net_changes: 측정 가운데 최댓값(truncated는 하나라도 true면 true, D8 KEYS 모드).</item>
    /// <item>{단계}_p50·{단계}_p95(ms, 소수 1자리): analyze, execute, read_log, reread, net_diff, resolve, serialize(PreviewResult.timingsMs), total(7단계 합),
    /// oracle_execute(같은 문장을 오라클에서 실행한 시간). 백분위수는 선형 보간.</item>
    /// <item>overhead_ratio_p50 = execute_p50 ÷ oracle_execute_p50(수집 오버헤드, §10.4). 정의 차이(2026-11-11 검토): 분자(샌드박스 execute)는 실행 봉투 비용
    /// (BEGIN TRAN, 제어 UPDATE, 경계 읽기, EXECUTE AS/REVERT)을 포함하고, 분모(오라클 TimeAsync)는 문장 실행만 잰다. 그래서 작은 N에서는 비율이 순수 수집 비용보다 크게 나온다.</item>
    /// <item>browser_first_graph_ms·browser_first_grid_ms: 빈 칸. SP9 화면 측정값을 손으로 기록한다(Q42).</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<string> M5Columns = BuildM5Columns();

    static List<string> BuildM5Columns()
    {
        var cols = new List<string> { "run_id", "case_id", "operation", "n", "samples", "ok_samples", "statuses", "image_count", "truncated", "firings", "net_changes" };
        foreach (var s in ResponseTime.Stages) { cols.Add(s + "_p50"); cols.Add(s + "_p95"); }
        cols.AddRange(["overhead_ratio_p50", "browser_first_graph_ms", "browser_first_grid_ms"]);
        return cols;
    }

    /// <summary>
    /// metrics.csv: 스위트 집계(마이크로 평균). metric: M1.precision·M1.recall·M1.f1·M1.row_exact_rate·M1.volatile_format_ok_rate·M1.residual_fill_rate·M1.identity_pair_rate(scope = comparison),
    /// M2.recall(scope = cause, ALL 포함), M3.precision·M3.recall(scope = 확신도 또는 all), M3.ambiguous_hit_rate(scope ambiguous),
    /// M3.certain_overconfident(scope certain, 분모 = certain 예측 간선), M3.manual_select_tables(scope all, 분모 = 사례·반복 수),
    /// M4.canonical_stable_rate(scope case, 분모 = 유효 반복이 하나라도 있는 사례), M4.observed_only_variation_rate(scope repeat, 분모 = 유효 반복),
    /// M4.error_run_rate(scope repeat, 예외 반복 ÷ 전체 반복), status_match_rate(scope case).
    /// </summary>
    public static readonly IReadOnlyList<string> SuiteColumns =
    [
        "run_id", "system", "metric", "scope", "numerator", "denominator", "value", "cases",
    ];

    /// <summary>비율 칸(분모 0이면 빈 칸).</summary>
    public static string Rate(long numerator, long denominator) =>
        denominator == 0 ? "" : ((double)numerator / denominator).ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>파일 인코딩: UTF-8, BOM 없음.</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>RFC 4180 줄 끝(CRLF).</summary>
    public const string NewLine = "\r\n";

    /// <summary>
    /// CSV 한 행을 덧붙인다. 파일이 없거나 비어 있으면 머리글(columns)부터 쓴다. 행 칸 수가 머리글과 다르면 ArgumentException.
    /// </summary>
    public static void Append(string path, IReadOnlyList<string> columns, IReadOnlyList<string?> row)
    {
        if (row.Count != columns.Count) throw new ArgumentException($"{Path.GetFileName(path)}: 칸 수 {row.Count} ≠ 열 수 {columns.Count}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var header = !File.Exists(path) || new FileInfo(path).Length == 0;
        using var w = new StreamWriter(path, append: true, Utf8NoBom);
        if (header) w.Write(Line(columns) + NewLine);
        w.Write(Line(row) + NewLine);
    }

    /// <summary>bool 칸("true"/"false", null이면 빈 칸).</summary>
    public static string Bool(bool? v) => v is bool b ? (b ? "true" : "false") : "";

    /// <summary>정수 칸(InvariantCulture, null이면 빈 칸).</summary>
    public static string Int(long? v) => v is long x ? x.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>RFC 4180 한 줄(쉼표·따옴표·줄바꿈이 있으면 인용).</summary>
    public static string Line(IEnumerable<string?> cells)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var c in cells)
        {
            if (!first) sb.Append(',');
            first = false;
            var v = c ?? "";
            if (v.AsSpan().IndexOfAny(",\"\r\n") >= 0) sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
            else sb.Append(v);
        }
        return sb.ToString();
    }

    /// <summary>
    /// CSV 파일을 읽는다(RFC 4180: 인용 칸 안의 쉼표·따옴표 두 개·줄바꿈 허용, 인용 밖 CR 무시). 첫 레코드는 머리글. 파일이 없으면 null.
    /// 행은 머리글 이름 → 값 사전(대소문자 무시)이다. 빈 줄은 건너뛴다.
    /// </summary>
    public static CsvTable? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var records = Parse(File.ReadAllText(path, Encoding.UTF8));
        if (records.Count == 0) return new CsvTable([], []);
        var header = records[0];
        var rows = records.Skip(1).Where(r => !(r.Count == 1 && r[0].Length == 0))
            .Select(r => (IReadOnlyDictionary<string, string>)header.Select((h, i) => (h, v: i < r.Count ? r[i] : ""))
                .GroupBy(x => x.h, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().v, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return new CsvTable(header, rows);
    }

    /// <summary>RFC 4180 본문을 레코드 목록으로 나눈다.</summary>
    public static List<List<string>> Parse(string text)
    {
        var records = new List<List<string>>();
        var cur = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var pending = false; // 현재 레코드에 칸이 시작됐는지
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; pending = true; }
            else if (c == ',') { cur.Add(cell.ToString()); cell.Clear(); pending = true; }
            else if (c == '\n')
            {
                cur.Add(cell.ToString());
                cell.Clear();
                records.Add(cur);
                cur = [];
                pending = false;
            }
            else if (c != '\r') { cell.Append(c); pending = true; }
        }
        if (pending || cell.Length > 0) { cur.Add(cell.ToString()); records.Add(cur); }
        return records;
    }
}

/// <summary>읽은 CSV(머리글과 행 사전).</summary>
public sealed record CsvTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);
