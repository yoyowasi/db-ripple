using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;

// dbripple capture-plan [--out <dir>]   : 카탈로그 → install.sql + manifest.json
// dbripple baseline build --source <db> --sandbox <db> --snapshot <db> --backup <path> [--dry-run]
return await Cli.RunAsync(args);

static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args switch
            {
                ["capture-plan", .. var rest] => await CapturePlanAsync(Opt(rest, "--out") ?? "."),
                ["baseline", "build", .. var rest] => BaselineBuild(rest),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("오류: " + ex.Message);
            return 1;
        }
    }

    static async Task<int> CapturePlanAsync(string outDir)
    {
        var cs = Environment.GetEnvironmentVariable(CatalogReader.ConnectionEnvVar)
            ?? throw new InvalidOperationException($"{CatalogReader.ConnectionEnvVar} 환경 변수가 없음");
        await using var conn = new SqlConnection(cs);
        var catalog = await CatalogReader.ReadAsync(conn);
        var plan = AuditTriggerGenerator.Generate(catalog.ToCaptureInput(), DateTimeOffset.Now);
        Directory.CreateDirectory(outDir);
        var sql = string.Concat(plan.Batches.Select(b => $"-- {b.Label}\n{b.Sql.TrimEnd()}\nGO\n"));
        await File.WriteAllTextAsync(Path.Combine(outDir, "install.sql"), sql);
        await File.WriteAllTextAsync(Path.Combine(outDir, "manifest.json"), plan.Manifest.ToJson());
        Console.WriteLine($"배치 {plan.Batches.Count}개, 대상 테이블 {plan.Manifest.Tables.Count}개, 제외 {plan.Manifest.ExcludedTables.Count}개 → {outDir}");
        return 0;
    }

    // 뼈대: 실행기(SqlBaselineSteps·환경 지문)는 미구현. --dry-run은 앞 단계 SQL만 보인다.
    static int BaselineBuild(string[] rest)
    {
        var o = new BaselineOptions(Req(rest, "--source"), Req(rest, "--sandbox"), Req(rest, "--snapshot"), Req(rest, "--backup"));
        if (!rest.Contains("--dry-run"))
        {
            Console.Error.WriteLine("미구현: DB 실행기(환경 지문 §6.2 포함). --dry-run으로 SQL만 확인할 수 있음");
            return 2;
        }
        Console.WriteLine(BaselineSql.DropSnapshot(o.Snapshot));
        Console.WriteLine(BaselineSql.Backup(o.Source, o.BackupPath));
        Console.WriteLine(BaselineSql.FileListOnly(o.BackupPath));
        Console.WriteLine("-- 이후: RESTORE … WITH MOVE, REPLACE → 카탈로그 → 설치 → 환경 지문 → CREATE DATABASE … AS SNAPSHOT OF");
        return 0;
    }

    static string? Opt(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static string Req(string[] a, string name) => Opt(a, name) ?? throw new ArgumentException($"{name} 값이 필요함");

    static int Usage()
    {
        Console.Error.WriteLine("사용법: dbripple capture-plan [--out <dir>] | dbripple baseline build --source <db> --sandbox <db> --snapshot <db> --backup <path> [--dry-run]");
        return 64;
    }
}
