using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;

// dbripple capture-plan [--database <db>] [--out <dir>]   : 카탈로그 → install.sql + manifest.json
// dbripple catalog stats --database <db>                   : 카탈로그 수치(A24 재확인)
// dbripple baseline build --source <db> --sandbox <db> --snapshot <db> --backup <path> [--out <dir>] [--timeout <초>] [--dry-run]
// dbripple restore --backup <path> --database <db> [--timeout <초>]
return await Cli.RunAsync(args);

static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args switch
            {
                ["capture-plan", .. var rest] => await CapturePlanAsync(Opt(rest, "--database"), Opt(rest, "--out") ?? "."),
                ["catalog", "stats", .. var rest] => await CatalogStatsAsync(Req(rest, "--database")),
                ["baseline", "build", .. var rest] => await BaselineBuildAsync(rest),
                ["restore", .. var rest] => await RestoreAsync(Req(rest, "--backup"), Req(rest, "--database"), TimeoutSeconds(rest)),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine("오류: " + ex.Message);
            return 1;
        }
    }

    static async Task<DbCatalog> ReadCatalogAsync(string? database)
    {
        var b = new SqlConnectionStringBuilder(ConnectionString());
        if (database is not null) b.InitialCatalog = database;
        await using var conn = new SqlConnection(b.ConnectionString);
        return await CatalogReader.ReadAsync(conn);
    }

    static async Task<int> CapturePlanAsync(string? database, string outDir)
    {
        var plan = AuditTriggerGenerator.Generate((await ReadCatalogAsync(database)).ToCaptureInput(), DateTimeOffset.Now);
        await WritePlanAsync(plan, outDir);
        Console.WriteLine($"배치 {plan.Batches.Count}개, 대상 테이블 {plan.Manifest.Tables.Count}개, 제외 {plan.Manifest.ExcludedTables.Count}개 → {outDir}");
        foreach (var n in plan.Manifest.Notes) Console.WriteLine("  note: " + n);
        return 0;
    }

    static async Task<int> CatalogStatsAsync(string database)
    {
        Console.WriteLine($"[{database}] C7 카탈로그 수치");
        foreach (var line in CatalogSummary.From(await ReadCatalogAsync(database)).Lines()) Console.WriteLine(line);
        return 0;
    }

    // §5.1 기준선 구축. --dry-run은 앞 단계 SQL만 보인다. --out이 있으면 install.sql·manifest.json을 남긴다.
    static async Task<int> BaselineBuildAsync(string[] rest)
    {
        var o = new BaselineOptions(Req(rest, "--source"), Req(rest, "--sandbox"), Req(rest, "--snapshot"), Req(rest, "--backup"));
        var timeout = TimeoutSeconds(rest);
        if (rest.Contains("--dry-run"))
        {
            BaselineBuilder.Validate(o);
            Console.WriteLine($"-- [master] 사전 검사\n{BaselineSql.Precheck(o.Source, o.Sandbox, o.Snapshot)}");
            Console.WriteLine($"-- [{o.Source}] 원본 스키마 검사(읽기만)\n{BaselineSql.SourceGuard(o.Source)}");
            Console.WriteLine($"-- [master] 1\n{BaselineSql.DropSnapshot(o.Snapshot, o.Sandbox)}");
            Console.WriteLine($"-- [master] 2\n{BaselineSql.Backup(o.Source, o.BackupPath)}");
            Console.WriteLine($"-- [master] 3\n{BaselineSql.RestoreTargetGuard(o.Sandbox, o.Source)}\n{BaselineSql.FileListOnly(o.BackupPath)}\n{BaselineSql.PrepareRestoreTarget(o.Sandbox)}");
            Console.WriteLine("-- 이후: RESTORE … WITH MOVE, REPLACE(실패 시 MULTI_USER 복구) → 카탈로그 → 행 수 → 설치 → 행 수 → 환경 지문 → CREATE DATABASE … AS SNAPSHOT OF");
            return 0;
        }
        await using var steps = new SqlBaselineSteps(new SandboxConnection(ConnectionString()), m => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {m}")) { CommandTimeoutSeconds = timeout };
        var started = DateTimeOffset.Now;
        var result = await BaselineBuilder.BuildAsync(o, steps, () => DateTimeOffset.Now);
        var m = result.Install.Manifest;
        Console.WriteLine($"기준 시각(백업 완료): {result.BackupCompletedAt:O}");
        Console.WriteLine($"원본 지문: {steps.SourceFingerprint}");
        Console.WriteLine($"샌드박스 지문: {steps.SandboxFingerprint}");
        Console.WriteLine($"fingerprintMatch={result.FingerprintMatch}");
        Console.WriteLine($"매니페스트 {m.Version}: 대상 {m.Tables.Count}개, 제외 {m.ExcludedTables.Count}개, auditNotLast {m.Tables.Count(t => t.AuditOrder.Insert == CaptureManifest.None || t.AuditOrder.Update == CaptureManifest.None || t.AuditOrder.Delete == CaptureManifest.None)}개, 배치 {result.Install.Batches.Count}개");
        foreach (var e in m.ExcludedTables) Console.WriteLine($"  excluded: {e.Table} ({e.Reason})");
        foreach (var n in m.Notes) Console.WriteLine("  note: " + n);
        Console.WriteLine($"총 소요: {(DateTimeOffset.Now - started).TotalSeconds:N1}s");
        if (Opt(rest, "--out") is { } outDir) await WritePlanAsync(result.Install, outDir);
        return result.FingerprintMatch ? 0 : 3;
    }

    // 백업 파일을 논리 파일마다 WITH MOVE로 복원한다(C8 3단계와 같은 함수: 이름 규칙, 대상 가드, 세션 끊기, 실패 시 MULTI_USER 복구).
    static async Task<int> RestoreAsync(string backup, string database, int timeout)
    {
        BaselineSql.ValidateName(database, "복원 대상"); // 연결하기 전에 이름 규칙부터
        await using var steps = new SqlBaselineSteps(new SandboxConnection(ConnectionString()), m => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {m}")) { CommandTimeoutSeconds = timeout };
        await BaselineBuilder.RestoreAsync(steps, database, backup);
        Console.WriteLine($"{database} 복원 완료");
        return 0;
    }

    // BACKUP·RESTORE·설치 배치 명령 시간 제한(초, 기본 600)
    static int TimeoutSeconds(string[] rest) => Opt(rest, "--timeout") is not { } t ? 600
        : int.TryParse(t, out var s) && s > 0 ? s : throw new ArgumentException($"--timeout은 양의 정수(초)여야 함: {t}");

    static async Task WritePlanAsync(InstallPlan plan, string outDir)
    {
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "install.sql"), string.Concat(plan.Batches.Select(b => $"-- {b.Label}\n{b.Sql.TrimEnd()}\nGO\n")));
        await File.WriteAllTextAsync(Path.Combine(outDir, "manifest.json"), plan.Manifest.ToJson());
    }

    static string ConnectionString() => Environment.GetEnvironmentVariable(CatalogReader.ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{CatalogReader.ConnectionEnvVar} 환경 변수가 없음");

    static string? Opt(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static string Req(string[] a, string name) => Opt(a, name) ?? throw new ArgumentException($"{name} 값이 필요함");

    static int Usage()
    {
        Console.Error.WriteLine("사용법: dbripple capture-plan [--database <db>] [--out <dir>] | dbripple catalog stats --database <db> | dbripple baseline build --source <db> --sandbox <db> --snapshot <db> --backup <path> [--out <dir>] [--timeout <초>] [--dry-run] | dbripple restore --backup <path> --database <db> [--timeout <초>]");
        return 64;
    }
}
