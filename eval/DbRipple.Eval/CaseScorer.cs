using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>사례 한 번(시스템·반복 하나)의 입력. 제품 결과는 PreviewOutcome 그대로 받고, 비교·지표는 하네스 코드로만 계산한다.</summary>
public sealed record CaseRun
{
    /// <summary>실행 id.</summary>
    public required string RunId { get; init; }
    /// <summary>preview, B0, B2.</summary>
    public string System { get; init; } = "preview";
    /// <summary>반복 번호(1부터).</summary>
    public int Repeat { get; init; } = 1;
    /// <summary>손 정답.</summary>
    public required HandCase Hand { get; init; }
    /// <summary>제품 미리보기 결과. 파이프라인이 예외로 끝났으면 null(PreviewError에 사유).</summary>
    public PreviewOutcome? Outcome { get; init; }
    /// <summary>파이프라인 예외 요약(메시지에 비밀값이 없어야 한다).</summary>
    public string? PreviewError { get; init; }
    /// <summary>평가 훅 샌드박스 순 변경. 훅이 불리지 않았으면 null(실패 사례 Q13 등).</summary>
    public IReadOnlyList<OracleRowChange>? HookChanges { get; init; }
    /// <summary>오라클(문장별 경로).</summary>
    public required OracleResult Oracle { get; init; }
    /// <summary>오라클 원문 배치 경로. 적용하지 않으면 null.</summary>
    public OracleResult? OracleRawBatch { get; init; }
    /// <summary>저장한 PreviewRecord JSON 경로.</summary>
    public string? RecordPath { get; init; }
}

/// <summary>사례 한 번의 CSV 행들.</summary>
public sealed record CaseScore(IReadOnlyList<string?> CaseRow, IReadOnlyList<IReadOnlyList<string?>> M1Rows, IReadOnlyList<IReadOnlyList<string?>> M2Rows,
    IReadOnlyList<string> HandDifferences, IReadOnlyList<string> RawBatchDifferences);

/// <summary>
/// 사례 채점(§10.6 6~7단계). cases.csv 1행, m1.csv 3행(total, obs, cap), m2.csv 8행(원인 7 + ALL)을 만들고 스위트 누적기에 더한다.
/// <list type="bullet">
/// <item>total(E_total): 미리보기 최종 차이(PreviewRecord.NetChanges, 사용자가 보는 결과) 대 오라클. 잔차 칸 = NetChanges 셀 중 잔차로 메운 셀(ResidualByExplanation).</item>
/// <item>obs(E_obs): 평가 훅 대 오라클. 잔차 칸은 빈 칸.</item>
/// <item>cap(E_cap): 행 변경만으로 접은 재구성(Reconstruction, 잔차·NetChanges 안 씀) 대 평가 훅. 잔차 칸 = NetChanges에는 있고 재구성에는 없는 셀(ResidualByAbsence, 이 셀은 fn으로도 잡힘).</item>
/// <item>훅이 불리지 않았으면 obs·cap 행의 지표 칸은 빈 칸이다. 미리보기가 거부·예외면 예측은 빈 집합이다.</item>
/// <item>status_actual: 미리보기 상태, 거부면 "rejected", 예외면 "error".</item>
/// <item>oracle_vs_hand_keys_match: HandCase.DiffAgainst가 비었는지. raw_batch_match: 원문 배치 경로와 문장별 경로의 순 변경·상태가 같은지.</item>
/// </list>
/// </summary>
public sealed class CaseScorer(MetricsCalculator calculator)
{
    /// <summary>채점하고 누적한다.</summary>
    public CaseScore Score(CaseRun run, SuiteAccumulator suite)
    {
        var hand = run.Hand;
        var record = run.Outcome?.Record;
        var vol = MetricsCalculator.VolatileMap(run.Oracle.Tables);
        var predicted = PredictedChange.FromRecord(record);
        var oracle = run.Oracle.Changes;

        var statusActual = run.Outcome is null ? "error"
            : record is null ? "rejected"
            : ContractEnumText(record.Result.Status);
        var statements = record?.Result.Statements ?? [];
        var errorActual = statements.FirstOrDefault(s => s.Error is not null)?.Error?.ErrorNumber;
        var stMatch = statements.Count(s => hand.ExpectedStatementStatus.TryGetValue(s.Index, out var e) && e == ContractEnumText(s.Status));
        var handDiff = hand.DiffAgainst(oracle);
        var rawDiff = run.OracleRawBatch is null ? [] : RawBatchDiff(run.Oracle, run.OracleRawBatch);
        var statusMatch = statusActual == hand.ExpectedStatus;
        suite.AddStatus(run.System, hand.Id, statusMatch);
        var t = record?.Result.TimingsMs;

        var caseRow = new List<string?>
        {
            run.RunId, run.System, hand.Id, hand.Pattern, hand.Database, MetricsCsv.Int(run.Repeat), record?.Result.PreviewId,
            hand.ExpectedStatus, statusActual, MetricsCsv.Bool(statusMatch),
            MetricsCsv.Int(hand.ExpectedErrorNumber), MetricsCsv.Int(errorActual),
            MetricsCsv.Int(hand.Statements.Count), MetricsCsv.Int(stMatch),
            MetricsCsv.Bool(handDiff.Count == 0), run.OracleRawBatch is null ? "" : MetricsCsv.Bool(rawDiff.Count == 0),
            MetricsCsv.Int(t?.Analyze), MetricsCsv.Int(t?.Execute), MetricsCsv.Int(t?.ReadLog), MetricsCsv.Int(t?.Reread),
            MetricsCsv.Int(t?.NetDiff), MetricsCsv.Int(t?.Resolve), MetricsCsv.Int(t?.Serialize),
            MetricsCsv.Int(run.Oracle.ExecuteMs), run.RecordPath,
        };

        var m1Rows = new List<IReadOnlyList<string?>>();
        void M1(string comparison, IReadOnlyList<OracleRowChange>? truth, IReadOnlyList<PredictedChange>? pred, (long Filled, long Base)? residual)
        {
            IReadOnlyList<string?> head = [run.RunId, run.System, hand.Id, MetricsCsv.Int(run.Repeat), comparison];
            if (truth is null || pred is null)
            {
                m1Rows.Add([.. head, .. Enumerable.Repeat("", MetricsCsv.M1Columns.Count - head.Count)]);
                return;
            }
            var c = calculator.M1(truth, pred, vol);
            if (residual is { } rf) c = c with { ResidualFilledCells = rf.Filled, ResidualBaseCells = rf.Base };
            suite.AddM1(run.System, hand.Id, comparison, c);
            m1Rows.Add([.. head, .. c.CsvCells()]);
        }
        var hook = run.HookChanges;
        var reconstructed = Reconstruction.FromRecord(record);
        M1("total", oracle, predicted, MetricsCalculator.ResidualByExplanation(predicted, vol));
        M1("obs", oracle, hook?.Select(PredictedChange.Plain).ToList(), null);
        M1("cap", hook, hook is null ? null : reconstructed.Select(PredictedChange.Plain).ToList(), MetricsCalculator.ResidualByAbsence(predicted, reconstructed, vol));

        var m2Rows = new List<IReadOnlyList<string?>>();
        foreach (var b in calculator.M2(hand, predicted))
        {
            suite.AddM2(run.System, hand.Id, b);
            if (b.Cause == "ALL") continue; // ALL은 metrics.csv에만(m2.csv cause 값은 명세의 7종)
            m2Rows.Add([run.RunId, run.System, hand.Id, MetricsCsv.Int(run.Repeat), b.Cause, .. b.CsvCells()]);
        }
        return new CaseScore(caseRow, m1Rows, m2Rows, handDiff, rawDiff);
    }

    /// <summary>문장별 경로와 원문 배치 경로의 차이(상태, 순 변경 집합: 테이블·키·종류·바뀐 열·after 값).</summary>
    public static IReadOnlyList<string> RawBatchDiff(OracleResult perStatement, OracleResult raw)
    {
        var d = new List<string>();
        if (perStatement.Status != raw.Status) d.Add($"status: 문장별 {perStatement.Status}, 원문 배치 {raw.Status}");
        static string Sig(OracleRowChange c) => c.Table.ToUpperInvariant() + "|" + c.Key + "|" + c.Change + "|" + c.Multiplicity + "|" +
            string.Join(",", c.ChangedColumns.Order(StringComparer.Ordinal)) + "|" +
            string.Join(",", (c.After ?? new Dictionary<string, string?>()).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + (p.Value ?? "\u0000")));
        var a = perStatement.Changes.Select(Sig).ToHashSet(StringComparer.Ordinal);
        var b = raw.Changes.Select(Sig).ToHashSet(StringComparer.Ordinal);
        d.AddRange(a.Except(b).Select(x => "문장별에만: " + x));
        d.AddRange(b.Except(a).Select(x => "원문 배치에만: " + x));
        return d;
    }

    /// <summary>계약 열거형의 설계 문자열(ok, failed, trigger_blocked, INSERTED …). ContractJson 직렬화와 같은 철자.</summary>
    public static string ContractEnumText<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonSerializer.Serialize(value, ContractJson.Options).Trim('"');
}
