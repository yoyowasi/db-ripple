using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>사례 한 번(시스템·반복 하나)의 입력. 제품 결과는 PreviewOutcome 그대로 받고, 비교·지표는 하네스 코드로만 계산한다.</summary>
public sealed record CaseRun
{
    /// <summary>실행 id.</summary>
    public required string RunId { get; init; }
    /// <summary>preview, B0, B1, B2(Baselines.Systems). 대조군이면 Outcome·HookChanges를 쓰지 않고 오라클과 손 정답만으로 예측을 만든다.</summary>
    public string System { get; init; } = Baselines.Systems.Preview;
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
    /// <summary>B1이 읽는 사례 DB의 FK 목록(CLI가 DB별로 한 번 읽어 준다). System이 B1일 때만 쓰고, 그때 null이면 예외다.</summary>
    public IReadOnlyList<ForeignKeyMeta>? ForeignKeys { get; init; }
    /// <summary>오라클(문장별 경로).</summary>
    public required OracleResult Oracle { get; init; }
    /// <summary>오라클 원문 배치 경로. 적용하지 않으면 null.</summary>
    public OracleResult? OracleRawBatch { get; init; }
    /// <summary>저장한 PreviewRecord JSON 경로.</summary>
    public string? RecordPath { get; init; }
}

/// <summary>사례 한 번의 CSV 행들.</summary>
public sealed record CaseScore(IReadOnlyList<string?> CaseRow, IReadOnlyList<IReadOnlyList<string?>> M1Rows, IReadOnlyList<IReadOnlyList<string?>> M2Rows,
    IReadOnlyList<string> HandDifferences, IReadOnlyList<string> RawBatchDifferences)
{
    /// <summary>m3.csv 행들(확신도 범위 5개).</summary>
    public IReadOnlyList<IReadOnlyList<string?>> M3Rows { get; init; } = [];
    /// <summary>M4 서명(preview만, 대조군은 null).</summary>
    public GraphSignature? Signature { get; init; }
    /// <summary>M3 간선 차이("예측에만: 키", "정답에만: 키", 정정 기록 후보).</summary>
    public IReadOnlyList<string> EdgeDifferences { get; init; } = [];
    /// <summary>v0.3 거부 차이(기대한 거부 없음, 예상 밖 거부). 거부 사례(HandCase.IsRejectCase)일 때만 채운다. 비면 일치.</summary>
    public IReadOnlyList<string> RejectDifferences { get; init; } = [];
    /// <summary>제품이 낸 거부(ruleId, 문장 번호). 거부 사례의 rejects.json 기록용.</summary>
    public IReadOnlyList<(string RuleId, int? StatementIndex)> ActualRejects { get; init; } = [];
    /// <summary>v0.3 returnValue·isToolFault 차이(preview만, 손 정답에 그 필드가 있는 사례만).</summary>
    public IReadOnlyList<string> V03Differences { get; init; } = [];
}

/// <summary>
/// 사례 채점(§10.6 6~7단계). cases.csv 1행, m1.csv 3행(total, obs, cap), m2.csv 8행(원인 8, ALL은 metrics.csv에만), m3.csv 5행(확신도 4 + all)을 만들고 스위트 누적기에 더한다.
/// <list type="bullet">
/// <item>키 정렬 규칙(인계 6): 오라클 테이블의 정렬 규칙으로 모든 쪽(오라클·훅·제품·재구성·손 정답)의 키를 접은 뒤 비교한다(KeyFolding).</item>
/// <item>total(E_total): 미리보기 최종 차이 + 롤백 뒤 남은 변경(사용자가 보는 결과) 대 오라클. 잔차 칸 = NetChanges 셀 중 잔차로 메운 셀(ResidualByExplanation).</item>
/// <item>obs(E_obs): 평가 훅 대 오라클. 잔차 칸은 빈 칸.</item>
/// <item>cap(E_cap): 행 변경만으로 접은 재구성(Reconstruction) 대 평가 훅. 키 없는 테이블은 양쪽을 관측 가능 열로 다시 묶는다(HeapKeys, 인계 2).
/// 잔차 칸 = NetChanges에는 있고 재구성에는 없는 셀(ResidualByAbsence, 이 셀은 fn으로도 잡힘).</item>
/// <item>M1 세 비교 모두 정답 쪽을 예측 쪽 IDENTITY 값으로 옮긴 뒤 비교한다(IdentityPairing, E2).</item>
/// <item>M2: 손 정답의 IDENTITY 행(v0.2 match)을 예측 쪽 키에 묶은 뒤 센다(HandCase.Bind).</item>
/// <item>M3: 제품 그래프(GraphEdges) 대 손 정답 간선(AttributionMetrics).</item>
/// <item>대조군(B0·B1·B2): 예측은 Baselines가 오라클·손 정답에서 만들고, 상태·오류·문장 상태는 오라클 값이다. obs·cap은 빈 칸이다.</item>
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
        if (run.Hand.IsRejectCase) return ScoreRejectCase(run);
        var system = run.System;
        var isPreview = system == Baselines.Systems.Preview;
        var tables = run.Oracle.Tables;
        var vol = VolatileMap(tables, run.Hand); // 카탈로그 비결정 열 + 손 정답 volatile(트리거 GETDATE 같은, answer-format v0.2)
        var folding = KeyFolding.For(tables);
        var oracle = folding.Apply(run.Oracle.Changes);
        var hand = folding.Apply(run.Hand);
        var record = isPreview ? run.Outcome?.Record : null;
        var identityRefs = IdentityRefMap(hand);

        IReadOnlyList<PredictedChange> predicted;
        IReadOnlyList<PredictedEdge> edges;
        long? manualSelect = null;
        string statusActual;
        int? errorActual;
        int stMatch;
        if (isPreview)
        {
            predicted = folding.Apply(PredictedChange.FromRecord(record));
            edges = GraphEdges.FromResult(record?.Result);
            statusActual = run.Outcome is null ? "error" : record is null ? "rejected" : ContractEnumText(record.Result.Status);
            var statements = record?.Result.Statements ?? [];
            errorActual = statements.FirstOrDefault(s => s.Error is not null)?.Error?.ErrorNumber;
            stMatch = statements.Count(s => hand.ExpectedStatementStatus.TryGetValue(s.Index, out var e) && e == ContractEnumText(s.Status));
        }
        else
        {
            if (system == Baselines.Systems.B1)
            {
                var b1 = FkStaticBaseline.Predict(FkStaticBaseline.Seeds(hand),
                    run.ForeignKeys ?? throw new InvalidOperationException("B1은 CaseRun.ForeignKeys(사례 DB의 FK 목록)가 필요하다"));
                predicted = FkStaticBaseline.Changes(hand, oracle, b1).Select(PredictedChange.Plain).ToList();
                edges = FkStaticBaseline.Edges(hand, b1);
                manualSelect = FkStaticBaseline.ManualSelectTables(hand, oracle, b1);
            }
            else
            {
                predicted = (system == Baselines.Systems.B0 ? Baselines.B0Changes(hand, oracle) : oracle).Select(PredictedChange.Plain).ToList();
                edges = system == Baselines.Systems.B0 ? Baselines.B0Edges(hand) : [];
                manualSelect = Baselines.ManualSelectTables(hand, oracle);
            }
            statusActual = run.Oracle.Status;
            errorActual = run.Oracle.ErrorNumbers.Count > 0 ? run.Oracle.ErrorNumbers[0] : null;
            stMatch = run.Oracle.Statements.Count(s => hand.ExpectedStatementStatus.TryGetValue(s.Index, out var e) && e == s.Status);
        }

        var handDiff = hand.DiffAgainst(oracle);
        var rawDiff = run.OracleRawBatch is null ? [] : RawBatchDiff(run.Oracle, run.OracleRawBatch, hand);
        var statusMatch = statusActual == hand.ExpectedStatus;
        suite.AddStatus(system, hand.Id, statusMatch);
        var t = record?.Result.TimingsMs;

        var caseRow = new List<string?>
        {
            run.RunId, system, hand.Id, hand.Pattern, hand.Database, MetricsCsv.Int(run.Repeat), record?.Result.PreviewId,
            hand.ExpectedStatus, statusActual, MetricsCsv.Bool(statusMatch),
            MetricsCsv.Int(hand.ExpectedErrorNumber), MetricsCsv.Int(errorActual),
            MetricsCsv.Int(hand.Statements.Count), MetricsCsv.Int(stMatch),
            MetricsCsv.Bool(handDiff.Count == 0), run.OracleRawBatch is null ? "" : MetricsCsv.Bool(rawDiff.Count == 0),
            MetricsCsv.Int(t?.Analyze), MetricsCsv.Int(t?.Execute), MetricsCsv.Int(t?.ReadLog), MetricsCsv.Int(t?.Reread),
            MetricsCsv.Int(t?.NetDiff), MetricsCsv.Int(t?.Resolve), MetricsCsv.Int(t?.Serialize),
            MetricsCsv.Int(run.Oracle.ExecuteMs), isPreview ? run.RecordPath : null,
        };

        var m1Rows = new List<IReadOnlyList<string?>>();
        void M1(string comparison, IReadOnlyList<OracleRowChange>? truth, IReadOnlyList<PredictedChange>? pred, (long Filled, long Base)? residual)
        {
            IReadOnlyList<string?> head = [run.RunId, system, hand.Id, MetricsCsv.Int(run.Repeat), comparison];
            if (truth is null || pred is null)
            {
                m1Rows.Add([.. head, .. Enumerable.Repeat("", MetricsCsv.M1Columns.Count - head.Count)]);
                return;
            }
            var aligned = IdentityPairing.Align(truth, pred.Select(p => p.Row).ToList(), tables, identityRefs, vol);
            var c = calculator.M1(aligned.Truth, pred, vol) with { IdentityRows = aligned.Rows, IdentityPaired = aligned.Paired };
            if (residual is { } rf) c = c with { ResidualFilledCells = rf.Filled, ResidualBaseCells = rf.Base };
            suite.AddM1(system, hand.Id, comparison, c);
            m1Rows.Add([.. head, .. c.CsvCells()]);
        }
        var hook = isPreview && run.HookChanges is { } hc ? folding.Apply(hc) : null;
        var reconstructed = folding.Apply(Reconstruction.FromRecord(record));
        M1("total", oracle, predicted, isPreview ? MetricsCalculator.ResidualByExplanation(predicted, vol) : null);
        M1("obs", oracle, hook?.Select(PredictedChange.Plain).ToList(), null);
        M1("cap", hook is null ? null : HeapKeys.Rekey(hook, tables),
            hook is null ? null : HeapKeys.Rekey(reconstructed, tables).Select(PredictedChange.Plain).ToList(),
            MetricsCalculator.ResidualByAbsence(predicted, reconstructed, vol));

        var m2Rows = new List<IReadOnlyList<string?>>();
        var (boundForM2, _) = hand.Bind(predicted.Select(p => p.Row).ToList());
        foreach (var b in calculator.M2(boundForM2, predicted))
        {
            suite.AddM2(system, hand.Id, b);
            if (b.Cause == "ALL") continue; // ALL은 metrics.csv에만(m2.csv cause 값은 명세의 8종)
            m2Rows.Add([run.RunId, system, hand.Id, MetricsCsv.Int(run.Repeat), b.Cause, .. b.CsvCells()]);
        }

        var m3 = AttributionMetrics.Compute(hand.Edges, edges, manualSelect);
        suite.AddM3(system, hand.Id, m3);
        var m3Rows = m3.Select(c => (IReadOnlyList<string?>)[run.RunId, system, hand.Id, MetricsCsv.Int(run.Repeat), c.Confidence, .. c.CsvCells()]).ToList();
        var edgeDiff = new List<string>();
        if (isPreview && record is not null)
        {
            var (extra, missing) = AttributionMetrics.Differences(hand.Edges, edges);
            edgeDiff.AddRange(extra.Select(k => "예측에만: " + k));
            edgeDiff.AddRange(missing.Select(k => "정답에만: " + k));
        }

        return new CaseScore(caseRow, m1Rows, m2Rows, handDiff, rawDiff)
        {
            M3Rows = m3Rows,
            Signature = isPreview ? OrderStability.Signature(record?.Result, statusActual) : null,
            EdgeDifferences = edgeDiff,
            V03Differences = isPreview ? V03Checks.CompareStatements(hand, record?.Result) : [],
        };
    }

    /// <summary>
    /// v0.3 거부 사례 채점. 오라클을 실행하지 않았으므로(OracleResult.Skipped) M1·M2·M3·대조군과 스위트 누적에 넣지 않는다.
    /// cases.csv 한 행(상태 기대 rejected 대 실제)만 만들고, (ruleId, 문장 번호) 비교는 RejectDifferences로 돌려준다. 대조군(B0·B2)은 의미가 없어 호출하지 않는다.
    /// M4 서명은 낸다(거부는 결정적이라 유효 반복, 반복마다 서명이 같아야 한다).
    /// </summary>
    CaseScore ScoreRejectCase(CaseRun run)
    {
        var hand = run.Hand;
        var record = run.Outcome?.Record;
        var statusActual = run.Outcome is null ? "error" : record is null ? "rejected" : ContractEnumText(record.Result.Status);
        var actual = V03Checks.ActualRejects(run.Outcome?.Analysis);
        var caseRow = new List<string?>
        {
            run.RunId, run.System, hand.Id, hand.Pattern, hand.Database, MetricsCsv.Int(run.Repeat), record?.Result.PreviewId,
            hand.ExpectedStatus, statusActual, MetricsCsv.Bool(statusActual == hand.ExpectedStatus),
            MetricsCsv.Int(hand.ExpectedErrorNumber), MetricsCsv.Int((long?)null),
            MetricsCsv.Int(hand.Statements.Count), MetricsCsv.Int(0),
            "", "", // oracle_vs_hand_keys_match, raw_batch_match: 오라클을 실행하지 않음
            "", "", "", "", "", "", "", // 단계별 시간
            "", // oracle_execute_ms
            run.RecordPath,
        };
        return new CaseScore(caseRow, [], [], [], [])
        {
            Signature = run.System == Baselines.Systems.Preview ? OrderStability.Signature(record?.Result, statusActual) : null,
            RejectDifferences = hand.RejectDiff(actual),
            ActualRejects = actual,
        };
    }

    /// <summary>손 정답 identityRefs → 테이블 → 열 → 참조 IDENTITY 테이블(IdentityPairing extraRefs).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> IdentityRefMap(HandCase hand) =>
        hand.Rows.Where(r => r.IdentityRefs.Count > 0)
            .GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.SelectMany(r => r.IdentityRefs)
                .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().Value.Table, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 비결정 열(테이블 → 열): 오라클 카탈로그로 아는 것(IDENTITY, rowversion, 비결정 기본값)에 손 정답 volatile을 더한다
    /// (예: 트리거가 GETDATE()로 채우는 CL16 OrdLine.added_at, 문장의 SYSDATETIME()인 CL16b touched_at). 테이블 이름은 대소문자 무시.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> VolatileMap(IReadOnlyList<OracleTable> tables, HandCase? hand)
    {
        var map = tables.ToDictionary(t => t.Display, t => t.VolatileColumns.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var r in hand?.Rows ?? [])
        {
            if (r.Volatile.Count == 0) continue;
            if (!map.TryGetValue(r.Table, out var set)) map[r.Table] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.UnionWith(r.Volatile);
        }
        return map.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 문장별 경로와 원문 배치 경로의 차이(상태, 순 변경 집합: 테이블·키·종류·바뀐 열·after 값).
    /// 두 경로는 차례로 실행되고 롤백해도 IDENTITY 값이 되돌아가지 않으므로, 원문 배치 쪽을 문장별 쪽 IDENTITY 값으로 옮긴 뒤 비교한다(IdentityPairing).
    /// hand가 있으면 그 identityRefs로 IDENTITY를 담는 비 FK 열(CL12c seen_identity)도 옮기고, 비결정 열(VolatileMap, IDENTITY 키 열 제외) 값은 비교하지 않는다
    /// (두 실행의 시각·시퀀스·NEWID 값은 늘 다르다, CL16·CL16b).
    /// </summary>
    public static IReadOnlyList<string> RawBatchDiff(OracleResult perStatement, OracleResult raw, HandCase? hand = null)
    {
        var vol = VolatileMap(perStatement.Tables, hand);
        raw = raw with { Changes = IdentityPairing.Align(raw.Changes, perStatement.Changes, perStatement.Tables, hand is null ? null : IdentityRefMap(hand), vol).Truth };
        var d = new List<string>();
        if (perStatement.Status != raw.Status) d.Add($"status: 문장별 {perStatement.Status}, 원문 배치 {raw.Status}");
        var idKeys = perStatement.Tables.ToDictionary(t => t.Display, t => t.IdentityKeyColumns, StringComparer.OrdinalIgnoreCase);
        bool Compared(OracleRowChange c, string column) =>
            vol.GetValueOrDefault(c.Table) is not { } v || !v.Contains(column)
            || (idKeys.GetValueOrDefault(c.Table) ?? []).Contains(column, StringComparer.OrdinalIgnoreCase);
        string Sig(OracleRowChange c) => c.Table.ToUpperInvariant() + "|" + c.Key + "|" + c.Change + "|" + c.Multiplicity + "|" +
            string.Join(",", c.ChangedColumns.Order(StringComparer.Ordinal)) + "|" +
            string.Join(",", (c.After ?? new Dictionary<string, string?>()).Where(p => Compared(c, p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + (p.Value ?? "\u0000")));
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
