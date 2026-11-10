using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Core.Baseline;

public sealed record BaselineOptions(string Source, string Sandbox, string Snapshot, string BackupPath);

/// <summary>DB 접근 단계. database는 실행 문맥(master, 원본 또는 샌드박스).</summary>
public interface IBaselineSteps
{
    Task ExecuteAsync(string database, string sql, CancellationToken ct);
    Task<IReadOnlyList<BackupFile>> ReadFileListAsync(string sql, CancellationToken ct);
    Task<DbCatalog> ReadCatalogAsync(string database, CancellationToken ct);

    /// <summary>사용자 테이블별 행 수("스키마.테이블" → 행 수).</summary>
    Task<IReadOnlyDictionary<string, long>> ReadRowCountsAsync(string database, CancellationToken ct);

    Task<bool> FingerprintMatchesAsync(string source, string sandbox, CancellationToken ct);
}

public sealed record BaselineResult(DateTimeOffset BackupCompletedAt, InstallPlan Install, bool FingerprintMatch);

/// <summary>C8: §5.1 실행 순서.</summary>
public static class BaselineBuilder
{
    public const string Master = "master";

    /// <summary>수집 장치 설치 DDL이 내는 DDL 이벤트(sp_settriggerorder는 ALTER_TRIGGER를 냄).</summary>
    public static readonly IReadOnlyList<string> InstallDdlEvents = ["CREATE_SCHEMA", "CREATE_SEQUENCE", "CREATE_TABLE", "CREATE_TRIGGER", "ALTER_TRIGGER"];

    static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    public static async Task<BaselineResult> BuildAsync(BaselineOptions o, IBaselineSteps s, Func<DateTimeOffset> clock, CancellationToken ct = default)
    {
        Validate(o);
        // 0. 아무것도 바꾸기 전 서버 사전 검사(원본 오타·별칭·시스템 DB·남의 스냅샷), 원본의 audit 스키마 충돌(읽기만)
        await s.ExecuteAsync(Master, BaselineSql.Precheck(o.Source, o.Sandbox, o.Snapshot), ct);
        await s.ExecuteAsync(o.Source, BaselineSql.SourceGuard(o.Source), ct);
        await s.ExecuteAsync(Master, BaselineSql.DropSnapshot(o.Snapshot, o.Sandbox), ct); // 1
        await s.ExecuteAsync(Master, BaselineSql.Backup(o.Source, o.BackupPath), ct);      // 2
        var backupAt = clock();
        await RestoreAsync(s, o.Sandbox, o.BackupPath, o.Source, ct);                       // 3
        var catalog = await s.ReadCatalogAsync(o.Sandbox, ct);                             // 4
        if (catalog.Tables.FirstOrDefault(t => string.Equals(t.Schema, AuditTriggerGenerator.StoreSchema, StringComparison.OrdinalIgnoreCase)) is { } clash)
            throw new InvalidOperationException($"샌드박스에 이미 {clash.Schema} 스키마 테이블({clash.Schema}.{clash.Name})이 있음: 감사 저장소 스키마(§6.2)와 겹쳐 설치하지 않음");
        var before = await s.ReadRowCountsAsync(o.Sandbox, ct);
        var plan = AuditTriggerGenerator.Generate(catalog.ToCaptureInput(), clock());     // 5
        foreach (var b in plan.Batches) await s.ExecuteAsync(o.Sandbox, b.Sql, ct);
        var after = await s.ReadRowCountsAsync(o.Sandbox, ct);
        plan = plan with { Manifest = plan.Manifest with { Notes = [.. plan.Manifest.Notes, .. InstallNotes(catalog, before, after)] } };
        var match = await s.FingerprintMatchesAsync(o.Source, o.Sandbox, ct);             // 6
        var dataFiles = catalog.Files.Where(f => f.TypeDesc == "ROWS").Select(f => f.LogicalName);
        await s.ExecuteAsync(Master, BaselineSql.CreateSnapshot(o.Snapshot, o.Sandbox, dataFiles), ct); // 7
        return new BaselineResult(backupAt, plan, match);
    }

    /// <summary>
    /// 백업 파일을 database로 복원한다(기준선 3단계, dbripple restore 공용). 대상 가드 → 파일 목록 → 세션 끊기(비정상 상태면 DROP) → RESTORE.
    /// RESTORE가 실패하면 SINGLE_USER로 남은 대상을 MULTI_USER로 되돌리고 원래 오류를 다시 던진다.
    /// </summary>
    public static async Task RestoreAsync(IBaselineSteps s, string database, string backupPath, string? source = null, CancellationToken ct = default)
    {
        BaselineSql.ValidateName(database, "복원 대상");
        if (string.IsNullOrWhiteSpace(backupPath)) throw new ArgumentException("백업 경로가 필요함");
        await s.ExecuteAsync(Master, BaselineSql.RestoreTargetGuard(database, source), ct);
        var files = await s.ReadFileListAsync(BaselineSql.FileListOnly(backupPath), ct);
        var restore = BaselineSql.Restore(database, backupPath, files); // 지원하지 않는 파일 형식은 세션을 끊기 전에 실패
        await s.ExecuteAsync(Master, BaselineSql.PrepareRestoreTarget(database), ct);
        try
        {
            await s.ExecuteAsync(Master, restore, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            try { await s.ExecuteAsync(Master, BaselineSql.AfterRestoreFailure(database), ct); }
            catch (Exception) { /* 되돌리기 실패는 원래 오류를 가리지 않는다 */ }
            throw;
        }
    }

    /// <summary>
    /// 원본을 덮어쓰거나 지우는 실수를 막는 클라이언트 검사: 이름 규칙(영문자·숫자·밑줄), 세 이름은 서로 다르고(대소문자 무시) 시스템 DB가 아님.
    /// 서버 쪽 판정(별칭·존재·상태)은 BaselineSql.Precheck.
    /// </summary>
    public static void Validate(BaselineOptions o)
    {
        BaselineSql.ValidateName(o.Source, "원본");
        BaselineSql.ValidateName(o.Sandbox, "샌드박스");
        BaselineSql.ValidateName(o.Snapshot, "스냅샷");
        if (string.IsNullOrWhiteSpace(o.BackupPath)) throw new ArgumentException("백업 경로가 필요함");
        string[] names = [o.Source, o.Sandbox, o.Snapshot];
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException($"원본·샌드박스·스냅샷 이름은 서로 달라야 함: {o.Source}, {o.Sandbox}, {o.Snapshot}");
        if (names.FirstOrDefault(n => SystemDatabases.Contains(n, StringComparer.OrdinalIgnoreCase)) is { } sys)
            throw new ArgumentException($"시스템 DB는 쓸 수 없음: {sys}");
    }

    /// <summary>설치 DDL의 부작용 기록: 그 DDL에 발동하는 DDL 트리거와, 설치 전후 행 수가 바뀐 기존 테이블.</summary>
    public static IEnumerable<string> InstallNotes(DbCatalog c, IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after)
    {
        foreach (var g in c.Triggers.Where(t => t.ParentClass == "DATABASE" && !t.IsDisabled).GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            var events = InstallDdlEvents.Where(e => g.Any(t => string.Equals(t.Event, e, StringComparison.OrdinalIgnoreCase))).ToList();
            if (events.Count > 0)
                yield return $"DDL 트리거 {g.Key}가 설치 DDL({string.Join("·", events)})에 발동함(ALTER_TRIGGER는 sp_settriggerorder)";
        }
        var changed = before.Where(kv => after.TryGetValue(kv.Key, out var n) && n != kv.Value)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} {after[kv.Key] - kv.Value:+#;-#}행").ToList();
        if (changed.Count > 0)
            yield return $"설치 전후 행 수가 바뀐 테이블(기준선에 포함, 원본과 다름): {string.Join(", ", changed)}";
    }
}
