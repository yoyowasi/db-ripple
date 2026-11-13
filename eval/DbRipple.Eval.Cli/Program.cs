// 평가 실행 CLI(§10.6). 사용(저장소 루트, 한 명령 안에서 점 소싱):
//   . .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -- cascadelab [--systems preview,B0,B1,B2] [--cases CL01,CL02] [--out logs\week4\eval] [--repeat 20] [--run-id id] [--execute-as dbr_preview_user|none] [--keep-dbs]
//   . .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -- m5 [--sizes 1,100,1000,10000,50000,100000] [--cases CL20,CL20b] [--repeat 5] [--warmup 1] [--image-cap n] [--out logs\week4\m5] [--run-id id] [--execute-as …] [--keep-dbs]
//   dotnet run --project eval\DbRipple.Eval.Cli -- report --run <실행 폴더> [--m5 <m5 실행 폴더>[,<m5 실행 폴더>…]]
// cascadelab: 사례마다 (변형별 전용 기준선) → 미리보기(평가 훅) → 오라클(문장별 + 원문 배치) → 대조군 → 지표 → cases·m1·m2·m3.csv, 끝에 m4.csv·metrics.csv·env.json.
// m5: N마다 전용 원본에 cl20.usp_Generate @parents = N → 기준선 → 사례(scaling 문장)마다 워밍업 + 반복 측정(제품 모드, 훅 없음) + 오라클 execute 시간 → m5.csv·env.json.
// report: 실행 폴더의 CSV만 읽어 report.md(DB 연결 없음).
// 조립만 여기서 하고 비교·지표·CSV·보고서는 DbRipple.Eval만 쓴다(§10.3). 공유 DB(CascadeLab*, AW_*)는 쓰지 않고 dbrt_eval_* 전용 DB를 만들고 끝에 지운다.
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

const string Usage = """
    사용:
      dbripple-eval cascadelab [--systems preview,B0,B1,B2] [--cases CL01,CL02] [--out <dir>] [--repeat n] [--run-id id] [--execute-as <user>|none] [--keep-dbs]
      dbripple-eval m5 [--sizes 1,100,1000,10000,50000,100000] [--cases CL20,CL20b] [--repeat 5] [--warmup 1] [--image-cap n] [--out <dir>] [--run-id id] [--execute-as <user>|none] [--keep-dbs]
      dbripple-eval report --run <실행 폴더> [--m5 <m5 실행 폴더>[,<m5 실행 폴더>…]]
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] is not ("cascadelab" or "m5" or "report"))
{
    Console.WriteLine(Usage);
    return 2;
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int IntOpt(string name, int fallback) => Opt(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

if (args[0] == "report")
{
    var run = Opt("--run");
    if (run is null) { Console.Error.WriteLine(Usage); return 2; }
    var runDir = Path.GetFullPath(Path.IsPathRooted(run) ? run : Path.Combine(RepoPaths.Root, run));
    if (!Directory.Exists(runDir)) { Console.Error.WriteLine($"실행 폴더 없음: {runDir}"); return 2; }
    // --m5는 쉼표로 여러 실행 폴더를 받는다(크기별로 나눠 돈 M5를 한 표로, 행은 그대로 잇는다).
    var m5 = Opt("--m5")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(m => Path.GetFullPath(Path.IsPathRooted(m) ? m : Path.Combine(RepoPaths.Root, m))).ToList();
    if (m5?.FirstOrDefault(d => !Directory.Exists(d)) is { } missing) { Console.Error.WriteLine($"m5 실행 폴더 없음: {missing}"); return 2; }
    Console.WriteLine($"출력: {ReportBuilder.Write(runDir, m5)}");
    return 0;
}

var connection = Environment.GetEnvironmentVariable(TestDb.ConnectionEnvVar) ?? "";
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine($"{TestDb.ConnectionEnvVar} 없음(scripts/env.ps1 점 소싱)");
    return 2;
}

var command = args[0];
var startedAt = DateTimeOffset.Now;
var runId = Opt("--run-id") ?? (command == "m5" ? "m5-" : "eval-") + startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var outDir = Path.GetFullPath(Path.Combine(RepoPaths.Root, Opt("--out") ?? Path.Combine("logs", "week4", command == "m5" ? "m5" : "eval"), runId));
var executeAs = Opt("--execute-as") is { } ea ? (ea == "none" ? null : ea) : "dbr_preview_user"; // Q12
var keepDbs = args.Contains("--keep-dbs");
var only = Opt("--cases")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
var allCases = HandCase.LoadAll(Path.Combine(RepoPaths.CascadeLab, "cases"));
var ct = CancellationToken.None;
Directory.CreateDirectory(outDir);
var notesPath = Path.Combine(outDir, "differences.txt"); // 정답 정정 기록 후보(§10.3)·오류. 차이가 없으면 빈 파일로 남긴다.
File.WriteAllText(notesPath, "", MetricsCsv.Utf8NoBom);
var dbEnv = new JsonObject(); // DB별 설정은 전용 DB를 지우기 전에 읽어 둔다
var failures = 0;

void Note(string line)
{
    Console.WriteLine(line);
    File.AppendAllText(notesPath, line + "\r\n", MetricsCsv.Utf8NoBom);
}

var comparer = new HashSnapshotComparer();
var extra = new Dictionary<string, string?>
{
    ["runId"] = runId,
    ["startedAt"] = startedAt.ToString("o", CultureInfo.InvariantCulture),
    ["suite"] = command,
    ["executeAsUser"] = executeAs,
};

if (command == "cascadelab") await RunCascadeLabAsync();
else await RunM5Async();

extra["finishedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
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

// 전용 원본·샌드박스·스냅샷 이름과 정리 목록.
(string Origin, string Sandbox, string Snapshot, string Backup, TestDatabases Cleanup) NewDatabases(string tag)
{
    var origin = TestDb.NewName("eval", tag);
    var backup = TestDb.BackupPath(origin);
    var cleanup = new TestDatabases(origin + "_base", origin + "_sbx", origin);
    cleanup.ContainerFiles.Add(backup);
    return (origin, origin + "_sbx", origin + "_base", backup, cleanup);
}

SandboxManager NewSandbox((string Origin, string Sandbox, string Snapshot, string Backup, TestDatabases Cleanup) d) =>
    new(new SandboxManagerOptions
    {
        Connections = DbRipple.Core.Baseline.ServerConnections.SingleServer(connection), // 평가는 SingleServer(2026-11-13)
        OriginDb = d.Origin,
        SandboxDb = d.Sandbox,
        SnapshotDb = d.Snapshot,
        BackupPath = d.Backup,
        ManifestPath = Path.Combine(outDir, "baseline", d.Origin + ".manifest.json"),
    }, new TriggerAnalyzer(), TimeProvider.System);

async Task RunCascadeLabAsync()
{
    IReadOnlyList<string> systems;
    try { systems = Baselines.ParseSystems(Opt("--systems")); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); failures++; return; }
    var repeat = IntOpt("--repeat", 1);
    var cases = allCases.Where(c => only is null || only.Contains(c.Id)).ToList();
    // v0.3: 하네스가 만들지 않는 DB의 사례(CL25d, CascadeLab_Reach)는 건너뛴다. 분석 API 회귀(scripts/home-test.ps1 N1 단계)로 확인한다.
    foreach (var skipped in cases.Where(c => c.Database is not ("CascadeLab" or "CascadeLab_RT")).ToList())
    {
        cases.Remove(skipped);
        Note($"{skipped.Id}: database {skipped.Database}는 이 하네스가 만들지 않아 건너뜀(분석 API 회귀로 확인)");
    }
    if (cases.Count == 0) { Console.Error.WriteLine("고른 사례가 없음"); failures++; return; }
    extra["systems"] = string.Join(",", systems);
    extra["cases"] = string.Join(",", cases.Select(c => c.Id));
    extra["repeat"] = repeat.ToString(CultureInfo.InvariantCulture);

    var withPreview = systems.Contains(Baselines.Systems.Preview);
    var oracleRunner = new OracleRunner(comparer);
    var scorer = new CaseScorer(new MetricsCalculator());
    var suite = new SuiteAccumulator();
    var m4 = new M4Accumulator();
    var rejectRecords = new JsonArray(); // v0.3 거부 사례(expected.rejects)의 기대·실제·차이, 끝에 rejects.json으로 쓴다
    var casesCsv = Path.Combine(outDir, "cases.csv");
    var m1Csv = Path.Combine(outDir, "m1.csv");
    var m2Csv = Path.Combine(outDir, "m2.csv");
    var m3Csv = Path.Combine(outDir, "m3.csv");

    void Write(CaseScore score)
    {
        MetricsCsv.Append(casesCsv, MetricsCsv.CaseColumns, score.CaseRow);
        foreach (var row in score.M1Rows) MetricsCsv.Append(m1Csv, MetricsCsv.M1Columns, row);
        foreach (var row in score.M2Rows) MetricsCsv.Append(m2Csv, MetricsCsv.M2Columns, row);
        foreach (var row in score.M3Rows) MetricsCsv.Append(m3Csv, MetricsCsv.M3Columns, row);
    }

    foreach (var group in cases.GroupBy(c => c.Database))
    {
        var variant = group.Key switch
        {
            "CascadeLab" => CascadeLabVariant.Default,
            "CascadeLab_RT" => CascadeLabVariant.RecursiveTriggers,
            _ => throw new InvalidOperationException($"모르는 사례 DB {group.Key}"),
        };
        var d = NewDatabases(variant == CascadeLabVariant.Default ? "cli" : "clirt");
        try
        {
            Console.WriteLine($"[{group.Key}] 전용 원본 {d.Origin} 시드{(withPreview ? "·기준선" : "")} 구축");
            await CascadeLabSeed.CreateAsync(d.Origin, variant);
            IPreviewPipeline? pipeline = null;
            if (withPreview)
            {
                var sandbox = NewSandbox(d);
                await sandbox.RefreshAsync(ct);
                var executor = new PreviewExecutor(connection, new ExecutionSettings { EvaluationMode = true, ExecuteAsUser = executeAs }, new NetDiffer());
                var store = new PreviewStore(Path.Combine(outDir, "store", d.Origin), TimeProvider.System);
                pipeline = new PreviewPipeline(sandbox, new BatchAnalyzer(), executor, new ChangeCollector(), new CausalResolver(), store, TimeProvider.System);
            }
            // B1: 전용 원본의 FK 목록을 DB(변형)마다 한 번 읽어 캐시한다(읽기 전용 SELECT, 사례 사이에 FK는 바뀌지 않는다).
            var foreignKeys = systems.Contains(Baselines.Systems.B1) ? await FkStaticBaseline.ReadAsync(connection, d.Origin, ct) : null;
            var dbPart = await EvalEnvironment.ReadAsync(connection, withPreview ? new[] { d.Origin, d.Sandbox } : new[] { d.Origin }, null, ct);
            foreach (var (k, v) in dbPart["databases"]!.AsObject().ToList()) dbEnv[k] = v?.DeepClone();

            // 시작 상태(시드) 디지스트: 사례마다 오라클 시작 상태가 이 값이어야 한다(사례 사이 초기화 확인).
            var seedDigest = (await oracleRunner.RunAsync(connection, d.Origin, [], rawBatch: false, ct)).BeforeDigest;
            var residueSeen = false;

            // 롤백 뒤에도 원본에 행이 남는 사례(leavesResidue, CL12a)는 다른 사례의 시작 상태를 바꾸므로 맨 뒤에 돈다.
            foreach (var hc in group.OrderBy(c => c.LeavesResidue))
                for (var r = 1; r <= repeat; r++)
                {
                    var tag = $"{hc.Id} r{r}";
                    PreviewOutcome? outcome = null;
                    string? previewError = null;
                    EvaluationHook? hook = null;
                    if (pipeline is not null)
                    {
                        hook = new EvaluationHook(comparer);
                        try { outcome = await pipeline.RunAsync(string.Join("\r\n", hc.Statements), new PreviewOptions(), hook, ct); }
                        catch (Exception ex) { previewError = ex.GetType().Name + ": " + ex.Message; }
                    }

                    string? recordPath = null;
                    if (outcome?.Record is { } rec)
                    {
                        recordPath = Path.Combine(outDir, "records", $"{hc.Id}-r{r}.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
                        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(rec, ContractJson.Options), MetricsCsv.Utf8NoBom, ct);
                    }

                    // v0.3 거부 사례는 오라클을 실행하지 않는다(거부될 문장을 원본에서 실행하면 안 된다, 예: CL25b의 남는 COMMIT).
                    var oracle = hc.IsRejectCase ? OracleResult.Skipped(d.Origin) : await oracleRunner.RunAsync(connection, d.Origin, hc.Statements, rawBatch: false, ct);
                    var raw = hc.IsRejectCase ? OracleResult.Skipped(d.Origin) : await oracleRunner.RunAsync(connection, d.Origin, hc.Statements, rawBatch: true, ct);
                    foreach (var system in systems)
                    {
                        var isPreview = system == Baselines.Systems.Preview;
                        if (hc.IsRejectCase && !isPreview) continue; // 거부 사례는 대조군이 의미 없다
                        var score = scorer.Score(new CaseRun
                        {
                            RunId = runId,
                            System = system,
                            Repeat = r,
                            Hand = hc,
                            Outcome = isPreview ? outcome : null,
                            PreviewError = isPreview ? previewError : null,
                            HookChanges = isPreview ? hook?.LastSandboxChanges : null,
                            ForeignKeys = system == Baselines.Systems.B1 ? foreignKeys : null,
                            Oracle = oracle,
                            OracleRawBatch = raw,
                            RecordPath = isPreview && recordPath is not null ? Path.GetRelativePath(outDir, recordPath) : null,
                        }, suite);
                        Write(score);
                        if (score.Signature is { } sig) m4.Add(system, hc.Id, sig);
                        if (isPreview && r == 1)
                            foreach (var e in score.EdgeDifferences) Note($"{tag} M3 간선 {e}");
                        if (isPreview && hc.IsRejectCase)
                        {
                            if (r == 1)
                            {
                                foreach (var x in score.RejectDifferences) Note($"{tag} v0.3 거부: {x}");
                                rejectRecords.Add(new JsonObject
                                {
                                    ["case"] = hc.Id,
                                    ["database"] = hc.Database,
                                    ["expected"] = new JsonArray(hc.ExpectedRejects.Select(e => (JsonNode)new JsonObject
                                    {
                                        ["ruleId"] = e.RuleId, ["statementIndex"] = e.StatementIndex, ["ruleIdPending"] = e.Pending,
                                    }).ToArray()),
                                    ["actual"] = new JsonArray(score.ActualRejects.Select(a => (JsonNode)new JsonObject
                                    {
                                        ["ruleId"] = a.RuleId, ["statementIndex"] = a.StatementIndex,
                                    }).ToArray()),
                                    ["differences"] = new JsonArray(score.RejectDifferences.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                                });
                            }
                        }
                        else if (isPreview && r == 1)
                            foreach (var x in score.V03Differences) Note($"{tag} v0.3 {x}");
                        if (isPreview || !withPreview)
                        {
                            foreach (var x in score.HandDifferences) Note($"{tag} 손 정답 대 오라클: {x}");
                            foreach (var x in score.RawBatchDifferences) Note($"{tag} 원문 배치 대 문장별: {x}");
                        }
                    }

                    if (previewError is not null) { failures++; Note($"{tag} 미리보기 예외: {previewError}"); }
                    if (!hc.IsRejectCase && oracle.BeforeDigest != seedDigest)
                        Note($"{tag} 오라클 시작 상태가 시드와 다름({oracle.BeforeDigest} ≠ {seedDigest}){(residueSeen || hc.LeavesResidue ? ", 앞선 leavesResidue 사례의 남은 행(예상)" : "")}");
                    if (!hc.IsRejectCase && (!oracle.RestoredAfterRollback || !raw.RestoredAfterRollback))
                        Note($"{tag} 오라클 롤백 뒤 시작 상태로 돌아가지 않음{(hc.LeavesResidue ? "(leavesResidue, 정상)" : "")}");
                    foreach (var x in oracle.ComparatorIssues.Concat(hook?.LastIssues ?? [])) Note($"{tag} 비교기: {x}");
                    foreach (var x in oracle.Notes.Concat(hook?.LastNotes ?? []).Distinct(StringComparer.Ordinal)) Note($"{tag} 정정 기록 후보: {x}");
                    if (hook is not null && hook.Calls == 0 && outcome?.Record?.Result.Status is PreviewStatus.Ok or PreviewStatus.Truncated)
                        Note($"{tag} 성공 미리보기인데 평가 훅이 불리지 않음");
                    if (hc.LeavesResidue) residueSeen = true;
                    var previewText = pipeline is null ? "-" : outcome?.Record?.Result.Status.ToString() ?? (outcome is null ? "error" : "rejected");
                    Console.WriteLine($"{tag}: preview={previewText} oracle={(hc.IsRejectCase ? "-" : oracle.Status)} hook={hook?.Calls.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                }
        }
        catch (Exception ex)
        {
            failures++;
            Note($"[{group.Key}] 중단: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (keepDbs) Console.WriteLine($"[{group.Key}] --keep-dbs: {d.Origin}*를 남김(TestDb.DropOrphansAsync(\"eval\")로 치울 것)");
            else await d.Cleanup.DisposeAsync();
        }
    }

    var metricsCsv = Path.Combine(outDir, "metrics.csv");
    foreach (var row in suite.Rows(runId).Concat(m4.SuiteRows(runId))) MetricsCsv.Append(metricsCsv, MetricsCsv.SuiteColumns, row);
    foreach (var row in m4.Rows(runId)) MetricsCsv.Append(Path.Combine(outDir, "m4.csv"), MetricsCsv.M4Columns, row);
    if (rejectRecords.Count > 0)
        File.WriteAllText(Path.Combine(outDir, "rejects.json"), rejectRecords.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), MetricsCsv.Utf8NoBom);
}

async Task RunM5Async()
{
    var sizes = (Opt("--sizes") ?? "1,100,1000,10000,50000,100000")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => long.Parse(x, CultureInfo.InvariantCulture)).Distinct().Order().ToList();
    var repeat = IntOpt("--repeat", 5);
    var warmup = IntOpt("--warmup", 1);
    long? imageCap = Opt("--image-cap") is { } ic ? long.Parse(ic, CultureInfo.InvariantCulture) : null;
    var cases = allCases.Where(c => c.Scaling is not null && (only is null || only.Contains(c.Id))).ToList();
    if (cases.Count == 0) { Console.Error.WriteLine("scaling이 있는 사례가 없음(CL20 계열, answer-format v0.2)"); failures++; return; }
    if (cases.Any(c => c.Database != "CascadeLab")) { Console.Error.WriteLine("m5 사례는 database CascadeLab이어야 함"); failures++; return; }
    extra["cases"] = string.Join(",", cases.Select(c => c.Id));
    extra["sizes"] = string.Join(",", sizes);
    extra["repeat"] = repeat.ToString(CultureInfo.InvariantCulture);
    extra["warmup"] = warmup.ToString(CultureInfo.InvariantCulture);
    extra["imageCap"] = imageCap?.ToString(CultureInfo.InvariantCulture) ?? "default";

    var oracleRunner = new OracleRunner(comparer) { CommandTimeoutSeconds = 3600 };
    var samples = new List<M5Sample>();
    foreach (var n in sizes)
    {
        var d = NewDatabases("m5");
        try
        {
            Console.WriteLine($"[N={n}] 전용 원본 {d.Origin} 시드·생성·기준선 구축");
            await CascadeLabSeed.CreateAsync(d.Origin, CascadeLabVariant.Default);
            // 생성은 반드시 원본에서, 기준선(백업) 전에 한다(샌드박스에서 하면 감사 트리거가 수집한다, lab-1).
            foreach (var gen in cases.Select(c => c.Scaling!.Generate).OfType<string>().Select(g => HandScaling.Fill(g, n)).Distinct(StringComparer.Ordinal))
                await TestDb.ExecAsync(d.Origin, gen);
            var sandbox = NewSandbox(d);
            await sandbox.RefreshAsync(ct);
            var dbPart = await EvalEnvironment.ReadAsync(connection, [d.Origin, d.Sandbox], null, ct);
            foreach (var (k, v) in dbPart["databases"]!.AsObject().ToList()) dbEnv[$"N={n}:{k}"] = v?.DeepClone();
            // 제품 모드(평가 모드 아님, 훅 없음): 사용자가 겪는 시간을 잰다. 큰 N을 위해 문장 제한 시간을 늘린다.
            var executor = new PreviewExecutor(connection, new ExecutionSettings { EvaluationMode = false, ExecuteAsUser = executeAs, CommandTimeoutSeconds = 3600 }, new NetDiffer());
            var store = new PreviewStore(Path.Combine(outDir, "store", d.Origin), TimeProvider.System);
            var pipeline = new PreviewPipeline(sandbox, new BatchAnalyzer(), executor, new ChangeCollector(), new CausalResolver(), store, TimeProvider.System);

            foreach (var hc in cases)
            {
                var sql = HandScaling.Fill(hc.Scaling!.Statement, n);
                var op = sql.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "?";
                for (var i = 1; i <= warmup + repeat; i++)
                {
                    var isWarmup = i <= warmup;
                    PreviewOutcome? outcome = null;
                    string missing = "error";
                    try
                    {
                        outcome = await pipeline.RunAsync(sql, new PreviewOptions { ImageCap = imageCap }, null, ct);
                        if (outcome.Record is null) missing = "rejected";
                    }
                    catch (Exception ex)
                    {
                        Note($"{hc.Id} N={n} #{i} 미리보기 예외: {ex.GetType().Name}: {ex.Message}");
                        if (!isWarmup) failures++;
                    }
                    var (oracleMs, oracleErrors) = await oracleRunner.TimeAsync(connection, d.Origin, [sql], ct);
                    if (oracleErrors.Count > 0) Note($"{hc.Id} N={n} #{i} 오라클 오류 {string.Join(",", oracleErrors)}");
                    var sample = ResponseTime.FromResult(hc.Id, op, n, i - warmup, isWarmup, outcome?.Record?.Result, missing, oracleMs);
                    samples.Add(sample);
                    var total = sample.Timings is { } t ? t.Analyze + t.Execute + t.ReadLog + t.Reread + t.NetDiff + t.Resolve + t.Serialize : (long?)null;
                    Console.WriteLine($"{hc.Id} N={n} #{i}{(isWarmup ? "(워밍업)" : "")}: {sample.Status} total={total?.ToString(CultureInfo.InvariantCulture) ?? "-"}ms oracle={oracleMs}ms images={sample.ImageCount} truncated={sample.Truncated}");
                }
            }
        }
        catch (Exception ex)
        {
            failures++;
            Note($"[N={n}] 중단: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (keepDbs) Console.WriteLine($"[N={n}] --keep-dbs: {d.Origin}*를 남김(TestDb.DropOrphansAsync(\"eval\")로 치울 것)");
            else await d.Cleanup.DisposeAsync();
        }
    }
    foreach (var row in ResponseTime.Rows(runId, samples)) MetricsCsv.Append(Path.Combine(outDir, "m5.csv"), MetricsCsv.M5Columns, row);
}
