using System.Globalization;
using System.Text;

namespace DbRipple.Eval;

/// <summary>
/// 평가 CSV 열 명세(§10.4 M1·M2, §10.6 cases.csv·metrics.csv). 보고서 표는 이 CSV에서만 만든다(손으로 쓴 수치 금지).
/// 공통 규칙: UTF-8(BOM 없음), 쉼표 구분, RFC 4180 인용, 소수는 InvariantCulture 소수점 4자리, 분모 0인 비율은 빈 칸.
/// system 열: preview(제품), B0, B2(4주차 대조군, 같은 열을 쓴다).
/// </summary>
public static class MetricsCsv
{
    /// <summary>cases.csv: (실행, 시스템, 사례, 반복)당 1행.</summary>
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
    /// </summary>
    public static readonly IReadOnlyList<string> M1Columns =
    [
        "run_id", "system", "case_id", "repeat", "comparison",
        "truth_cells", "predicted_cells", "tp", "fp", "fn", "precision", "recall", "f1",
        "rows_union", "rows_exact", "row_exact_rate",
        "volatile_cells", "volatile_present_format_ok",
        "residual_filled_cells", "residual_fill_rate",
    ];

    /// <summary>
    /// m2.csv: (실행, 시스템, 사례, 반복, cause)당 1행. cause: DIRECT, FK_CASCADE, FK_SET_NULL, FK_SET_DEFAULT, TRIGGER, SAME_TABLE_TRIGGER, ONE_OF(손 정답 oneOf).
    /// 단위는 (테이블, 키) 한 번(§10.4). found = 제품 최종 차이(NetChange)에 같은 (테이블, 키, 변경 종류)가 있음.
    /// </summary>
    public static readonly IReadOnlyList<string> M2Columns =
    [
        "run_id", "system", "case_id", "repeat", "cause",
        "truth_rows", "found_rows", "recall", "found_by_firing", "found_by_residual_only", "missed_keys",
    ];

    /// <summary>metrics.csv: 스위트 집계(마이크로 평균). metric 예: M1.f1, M1.row_exact_rate, M2.recall. scope: comparison 또는 cause.</summary>
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
}
