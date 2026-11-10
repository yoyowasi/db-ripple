// 평가 실행 CLI(§10.6). 사용(저장소 루트, 한 명령 안에서 점 소싱):
//   . .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -- cascadelab [--cases CL01,CL02] [--out logs\week2\eval] [--repeat 1] [--run-id id] [--execute-as dbr_preview_user|none] [--keep-dbs]
// 사례마다: (변형별 전용 기준선) → 미리보기(평가 훅) → 오라클(문장별 + 원문 배치) → 지표 → cases.csv·m1.csv·m2.csv, 스위트 끝에 metrics.csv·env.json.
// 조립만 여기서 하고 비교·지표·CSV는 DbRipple.Eval만 쓴다(§10.3). 공유 DB(CascadeLab*, AW_*)는 쓰지 않고 dbrt_eval_cli* 전용 DB를 만들고 끝에 지운다.
// 연결 문자열은 환경 변수에서만 읽고 출력하지 않는다.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbRipple.Analysis;
using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Diff;
using DbRipple.Eval;
using DbRipple.Execution;
using DbRipple.Preview;
using DbRipple.TestSupport;

if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] != "cascadelab")
{
    Console.WriteLine("사용: dbripple-eval cascadelab [--cases CL01,CL02] [--out <dir>] [--repeat n] [--run-id id] [--execute-as <user>|none] [--keep-dbs]");
    return 2;
}

var connection = Environment.GetEnvironmentVariable(TestDb.ConnectionEnvVar);
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine($"{TestDb.ConnectionEnvVar} 없음(scripts/env.ps1 점 소싱)");
    return 2;
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var startedAt = DateTimeOffset.Now;
var runId = Opt("--run-id") ?? "eval-" + startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var outDir = Path.GetFullPath(Path.Combine(RepoPaths.Root, Opt("--out") ?? Path.Combine("logs", "week2", "eval"), runId));
var repeat = int.Parse(Opt("--repeat") ?? "1", CultureInfo.InvariantCulture);
var executeAs = Opt("--execute-as") is { } ea ? (ea == "none" ? null : ea) : "dbr_preview_user"; // Q12
var keepDbs = args.Contains("--keep-dbs");
var only = Opt("--cases")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

var cases = HandCase.LoadAll(Path.Combine(RepoPaths.CascadeLab, "cases")).Where(c => only is null || only.Contains(c.Id)).ToList();
if (cases.Count == 0) { Console.Error.WriteLine("고른 사례가 없음"); return 2; }
Directory.CreateDirectory(outDir);

var comparer = new HashSnapshotComparer();
var oracleRunner = new OracleRunner(comparer);
var scorer = new CaseScorer(new MetricsCalculator());
var suite = new SuiteAccumulator();
var casesCsv = Path.Combine(outDir, "cases.csv");
var m1Csv = Path.Combine(outDir, "m1.csv");
var m2Csv = Path.Combine(outDir, "m2.csv");
var notesPath = Path.Combine(outDir, "differences.txt"); // 정답 정정 기록 후보(§10.3)·오류. 차이가 없으면 빈 파일로 남긴다.
File.WriteAllText(notesPath, "", MetricsCsv.Utf8NoBom);
var dbEnv = new JsonObject(); // DB별 설정은 전용 DB를 지우기 전에 읽어 둔다
var failures = 0;
var ct = CancellationToken.None;

void Note(string line)
{
    Console.WriteLine(line);
    File.AppendAllText(notesPath, line + "\r\n", MetricsCsv.Utf8NoBom);
}

foreach (var group in cases.GroupBy(c => c.Database))
{
    var variant = group.Key switch
    {
        "CascadeLab" => CascadeLabVariant.Default,
        "CascadeLab_RT" => CascadeLabVariant.RecursiveTriggers,
        _ => throw new InvalidOperationException($"모르는 사례 DB {group.Key}"),
    };
    var origin = TestDb.NewName("eval", variant == CascadeLabVariant.Default ? "cli" : "clirt");
    var sandboxDb = origin + "_sbx";
    var snapshotDb = origin + "_base";
    var backup = TestDb.BackupPath(origin);
    var cleanup = new TestDatabases(snapshotDb, sandboxDb, origin);
    cleanup.ContainerFiles.Add(backup);
    try
    {
        Console.WriteLine($"[{group.Key}] 전용 원본 {origin} 시드·기준선 구축");
        await CascadeLabSeed.CreateAsync(origin, variant);
        var sandbox = new SandboxManager(new SandboxManagerOptions
        {
            ConnectionString = connection,
            OriginDb = origin,
            SandboxDb = sandboxDb,
            SnapshotDb = snapshotDb,
            BackupPath = backup,
            ManifestPath = Path.Combine(outDir, "baseline", origin + ".manifest.json"),
        }, new TriggerAnalyzer(), TimeProvider.System);
        await sandbox.RefreshAsync(ct);
        var dbPart = await EvalEnvironment.ReadAsync(connection, [origin, sandboxDb], null, ct);
        foreach (var (k, v) in dbPart["databases"]!.AsObject().ToList()) dbEnv[k] = v?.DeepClone();

        var executor = new PreviewExecutor(connection, new ExecutionSettings { EvaluationMode = true, ExecuteAsUser = executeAs }, new NetDiffer());
        var store = new PreviewStore(Path.Combine(outDir, "store", origin), TimeProvider.System);
        var pipeline = new PreviewPipeline(sandbox, new BatchAnalyzer(), executor, new ChangeCollector(), new CausalResolver(), store, TimeProvider.System);

        // 시작 상태(시드) 디지스트: 사례마다 오라클 시작 상태가 이 값이어야 한다(사례 사이 초기화 확인).
        var seedDigest = (await oracleRunner.RunAsync(connection, origin, [], rawBatch: false, ct)).BeforeDigest;

        foreach (var hc in group)
            for (var r = 1; r <= repeat; r++)
            {
                var hook = new EvaluationHook(comparer);
                PreviewOutcome? outcome = null;
                string? previewError = null;
                try
                {
                    outcome = await pipeline.RunAsync(string.Join("\r\n", hc.Statements), new PreviewOptions(), hook, ct);
                }
                catch (Exception ex)
                {
                    previewError = ex.GetType().Name + ": " + ex.Message;
                }

                string? recordPath = null;
                if (outcome?.Record is { } rec)
                {
                    recordPath = Path.Combine(outDir, "records", $"{hc.Id}-r{r}.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
                    await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(rec, ContractJson.Options), MetricsCsv.Utf8NoBom, ct);
                }

                var oracle = await oracleRunner.RunAsync(connection, origin, hc.Statements, rawBatch: false, ct);
                var raw = await oracleRunner.RunAsync(connection, origin, hc.Statements, rawBatch: true, ct);
                var score = scorer.Score(new CaseRun
                {
                    RunId = runId,
                    Repeat = r,
                    Hand = hc,
                    Outcome = outcome,
                    PreviewError = previewError,
                    HookChanges = hook.LastSandboxChanges,
                    Oracle = oracle,
                    OracleRawBatch = raw,
                    RecordPath = recordPath is null ? null : Path.GetRelativePath(outDir, recordPath),
                }, suite);

                MetricsCsv.Append(casesCsv, MetricsCsv.CaseColumns, score.CaseRow);
                foreach (var row in score.M1Rows) MetricsCsv.Append(m1Csv, MetricsCsv.M1Columns, row);
                foreach (var row in score.M2Rows) MetricsCsv.Append(m2Csv, MetricsCsv.M2Columns, row);

                var tag = $"{hc.Id} r{r}";
                if (previewError is not null) { failures++; Note($"{tag} 미리보기 예외: {previewError}"); }
                if (oracle.BeforeDigest != seedDigest) Note($"{tag} 오라클 시작 상태가 시드와 다름({oracle.BeforeDigest} ≠ {seedDigest})");
                if (!oracle.RestoredAfterRollback || !raw.RestoredAfterRollback) Note($"{tag} 오라클 롤백 뒤 시작 상태로 돌아가지 않음");
                foreach (var d in score.HandDifferences) Note($"{tag} 손 정답 대 오라클: {d}");
                foreach (var d in score.RawBatchDifferences) Note($"{tag} 원문 배치 대 문장별: {d}");
                foreach (var d in oracle.ComparatorIssues.Concat(hook.LastIssues)) Note($"{tag} 비교기: {d}");
                if (hook.Calls == 0 && outcome?.Record?.Result.Status is PreviewStatus.Ok or PreviewStatus.Truncated)
                    Note($"{tag} 성공 미리보기인데 평가 훅이 불리지 않음");
                Console.WriteLine($"{tag}: preview={(outcome?.Record?.Result.Status.ToString() ?? (outcome is null ? "error" : "rejected"))} oracle={oracle.Status} hook={hook.Calls}");
            }
    }
    catch (Exception ex)
    {
        failures++;
        Note($"[{group.Key}] 중단: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        if (keepDbs) Console.WriteLine($"[{group.Key}] --keep-dbs: {origin}*를 남김(TestDb.DropOrphansAsync(\"eval\")로 치울 것)");
        else await cleanup.DisposeAsync();
    }
}

var metricsCsv = Path.Combine(outDir, "metrics.csv");
foreach (var row in suite.Rows(runId)) MetricsCsv.Append(metricsCsv, MetricsCsv.SuiteColumns, row);

var extra = new Dictionary<string, string?>
{
    ["runId"] = runId,
    ["startedAt"] = startedAt.ToString("o", CultureInfo.InvariantCulture),
    ["finishedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
    ["suite"] = "cascadelab",
    ["cases"] = string.Join(",", cases.Select(c => c.Id)),
    ["repeat"] = repeat.ToString(CultureInfo.InvariantCulture),
    ["executeAsUser"] = executeAs,
};
try
{
    var env = await EvalEnvironment.ReadAsync(connection, [], extra, ct);
    env["databases"] = dbEnv;
    await EvalEnvironment.WriteAsync(Path.Combine(outDir, "env.json"), env, ct);
}
catch (Exception ex)
{
    failures++;
    Note($"env.json 실패: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine($"출력: {outDir}");
return failures == 0 ? 0 : 1;
