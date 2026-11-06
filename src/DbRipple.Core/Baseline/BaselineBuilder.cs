using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Core.Baseline;

public sealed record BaselineOptions(string Source, string Sandbox, string Snapshot, string BackupPath);

/// <summary>DB 접근 단계. database는 실행 문맥(master 또는 샌드박스).</summary>
public interface IBaselineSteps
{
    Task ExecuteAsync(string database, string sql, CancellationToken ct);
    Task<IReadOnlyList<BackupFile>> ReadFileListAsync(string sql, CancellationToken ct);
    Task<DbCatalog> ReadCatalogAsync(string database, CancellationToken ct);
    Task<bool> FingerprintMatchesAsync(string source, string sandbox, CancellationToken ct);
}

public sealed record BaselineResult(DateTimeOffset BackupCompletedAt, InstallPlan Install, bool FingerprintMatch);

/// <summary>C8: §5.1 실행 순서.</summary>
public static class BaselineBuilder
{
    public const string Master = "master";

    public static async Task<BaselineResult> BuildAsync(BaselineOptions o, IBaselineSteps s, Func<DateTimeOffset> clock, CancellationToken ct = default)
    {
        await s.ExecuteAsync(Master, BaselineSql.DropSnapshot(o.Snapshot), ct);            // 1
        await s.ExecuteAsync(Master, BaselineSql.Backup(o.Source, o.BackupPath), ct);      // 2
        var backupAt = clock();
        var files = await s.ReadFileListAsync(BaselineSql.FileListOnly(o.BackupPath), ct); // 3
        await s.ExecuteAsync(Master, BaselineSql.Restore(o.Sandbox, o.BackupPath, files), ct);
        var catalog = await s.ReadCatalogAsync(o.Sandbox, ct);                             // 4
        var plan = AuditTriggerGenerator.Generate(catalog.ToCaptureInput(), clock());     // 5
        plan = plan with { Manifest = plan.Manifest with { Notes = [.. plan.Manifest.Notes, .. DdlTriggerNotes(catalog)] } };
        foreach (var b in plan.Batches) await s.ExecuteAsync(o.Sandbox, b.Sql, ct);
        var match = await s.FingerprintMatchesAsync(o.Source, o.Sandbox, ct);             // 6
        var dataFiles = catalog.Files.Where(f => f.TypeDesc == "ROWS").Select(f => f.LogicalName);
        await s.ExecuteAsync(Master, BaselineSql.CreateSnapshot(o.Snapshot, o.Sandbox, dataFiles), ct); // 7
        return new BaselineResult(backupAt, plan, match);
    }

    static IEnumerable<string> DdlTriggerNotes(DbCatalog c) =>
        c.Triggers.Where(t => t.ParentClass == "DATABASE").Select(t => t.Name).Distinct()
            .Select(n => $"DDL 트리거 {n}가 설치 DDL을 기록할 수 있음(기준선에 포함, 원본과 다름)");
}
