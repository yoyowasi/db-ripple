using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Core.Baseline;

public sealed record BaselineOptions(string Source, string Sandbox, string Snapshot, string BackupPath)
{
    /// <summary>감사 장치·실행 사용자 설치 전 DB 범위 DDL 트리거 검사(복원된 샌드박스 카탈로그 → 멈출 이유, 통과면 null). null이면 검사하지 않는다.</summary>
    public Func<DbCatalog, string?>? DdlTriggerGuard { get; init; }
}

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

public sealed record BaselineResult(DateTimeOffset BackupCompletedAt, InstallPlan Install, bool FingerprintMatch)
{
    /// <summary>Split: 원본 연결로 읽은 지문(SingleServer는 null).</summary>
    public Fingerprint? OriginFingerprint { get; init; }
    /// <summary>Split: 샌드박스 연결로 읽은 지문(SingleServer는 null).</summary>
    public Fingerprint? SandboxFingerprint { get; init; }
    /// <summary>Split: 필드별 지문 판정(SingleServer는 null).</summary>
    public FingerprintComparison? Comparison { get; init; }
    /// <summary>Split: 원본 @@SERVERNAME(비밀값 아님).</summary>
    public string? OriginServer { get; init; }
}

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
        var (catalog, plan) = await InstallAsync(s, o.Sandbox, o.DdlTriggerGuard, clock, ct); // 4~5
        var match = await s.FingerprintMatchesAsync(o.Source, o.Sandbox, ct);             // 6
        var dataFiles = catalog.Files.Where(f => f.TypeDesc == "ROWS").Select(f => f.LogicalName);
        await s.ExecuteAsync(Master, BaselineSql.CreateSnapshot(o.Snapshot, o.Sandbox, dataFiles), ct); // 7
        return new BaselineResult(backupAt, plan, match);
    }

    /// <summary>
    /// Split 기준선(2026-11-13): 원본 단계(읽기 + COPY_ONLY 백업) → 가져오기(UNC 또는 지정 .bak) → 로컬 단계(HEADERONLY·FILELISTONLY 검사, RESTORE WITH MOVE,
    /// 소유자·서버 옵션 정리, 4b 실행 사용자, 5 감사 설치, 6 지문(두 연결 따로), 7 기준 스냅샷). 원본에는 백업 파일 말고 아무것도 쓰지 않는다.
    /// 파괴 단계는 sandbox(ISandboxSteps)로만 보낸다. origin(IOriginSteps)에는 임의 SQL 실행 메서드가 없다.
    /// </summary>
    public static async Task<BaselineResult> BuildSplitAsync(
        SplitBaselineOptions o, IOriginSteps origin, ISandboxSteps sandbox, IBackupImporter importer, Func<DateTimeOffset> clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.Validate();
        // 0. 같은 인스턴스 오인 방지: 이름(읽기만) → 이름이 다를 때만 로컬 master 표식 쓰기 → 원본에서 표식이 보이는지(읽기만)
        var originId = await origin.ReadIdentityAsync(ct);
        var local = await sandbox.ReadServerInfoAsync(ct);
        if (InstanceGuard.SameInstanceReason(originId, local.Identity, null, null) is { } same)
            throw new InvalidOperationException($"기준선을 만들지 않음: {same}. 샌드박스 연결은 내 PC Docker를 가리켜야 함");
        var marker = await sandbox.EnsureInstanceMarkerAsync(ct);
        if (InstanceGuard.SameInstanceReason(originId, local.Identity, marker, await origin.ReadInstanceMarkerAsync(ct)) is { } sameMarker)
            throw new InvalidOperationException($"기준선을 만들지 않음: {sameMarker}");

        // 1. 원본 사전 검사·지문(읽기만), 로컬 사전 검사
        await origin.PrecheckAsync(o.Source, ct);
        var special = await origin.ReadSpecialFileGroupsAsync(o.Source, ct);
        var originFingerprint = await origin.ReadFingerprintAsync(o.Source, ct);
        await sandbox.ExecuteAsync(Master, BaselineSql.SandboxPrecheck(o.Sandbox, o.Snapshot), ct);

        // 2. 백업 파일 → 가져오기 폴더
        var fileName = RunNames.BackupFile(o.MachineTag, clock());
        DateTimeOffset backupAt;
        if (o.ImportFile is { } given)
        {
            await importer.ImportAsync(given, fileName, ct);
            backupAt = importer.LastWriteTime(given);
        }
        else
        {
            await origin.BackupAsync(o.Source, ServerPaths.Join(o.BackupServerDir!, fileName), ct);
            backupAt = clock();
            await importer.ImportAsync(Path.Combine(o.SharePath!, fileName), fileName, ct);
        }

        // 3. 로컬 복원(1 기준 스냅샷 삭제 → HEADERONLY → FILELISTONLY → RESTORE WITH MOVE). 가져온 파일은 끝나면 지운다(옵션).
        var containerPath = ServerPaths.Join(o.ImportContainerDir, fileName);
        try
        {
            await sandbox.ExecuteAsync(Master, BaselineSql.DropSnapshot(o.Snapshot, o.Sandbox), ct);
            BackupHeaderCheck.Validate(await sandbox.ReadHeadersAsync(BaselineSql.HeaderOnly(containerPath), ct), local.MajorVersion, o.Source);
            var layout = new RestoreLayout(local.DataDir, local.LogDir, special);
            await RestoreCoreAsync(sandbox, o.Sandbox, containerPath, null, files => BaselineSql.Restore(o.Sandbox, containerPath, files, layout), ct);
        }
        finally
        {
            if (o.DeleteImportedBackup)
            {
                try { importer.Delete(fileName); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 이름에 회차가 있어 다음 실행과 겹치지 않는다 */ }
            }
        }
        await sandbox.ExecuteAsync(Master, BaselineSql.TrustworthyOff(o.Sandbox), ct); // 복원 직후(소유자를 로컬 sa로 바꾸기 전) TRUSTWORTHY 끄기
        await sandbox.ExecuteAsync(Master, BaselineSql.AfterRestoreLocal(o.Sandbox, originFingerprint.NestedTriggers, originFingerprint.DisallowResultsFromTriggers), ct);

        // 4~7
        var (catalog, plan) = await InstallAsync(sandbox, o.Sandbox, o.DdlTriggerGuard, clock, ct);
        var sandboxFingerprint = await sandbox.ReadFingerprintAsync(o.Sandbox, ct);
        var comparison = FingerprintComparison.Compare(originFingerprint, sandboxFingerprint, ServerMode.Split);
        var dataFiles = catalog.Files.Where(f => f.TypeDesc == "ROWS").Select(f => f.LogicalName);
        await sandbox.ExecuteAsync(Master, BaselineSql.CreateSnapshot(o.Snapshot, o.Sandbox, dataFiles, n => BaselineSql.SnapshotPath(o.Snapshot, n, local.DataDir)), ct);
        return new BaselineResult(backupAt, plan, comparison.Match)
        {
            OriginFingerprint = originFingerprint,
            SandboxFingerprint = sandboxFingerprint,
            Comparison = comparison,
            OriginServer = originId.ServerName,
        };
    }

    // 4. 카탈로그·감사 스키마 충돌·DDL 트리거 검사 → 4b. 실행 사용자 → 5. 수집 장치 설치(전후 행 수 기록)
    static async Task<(DbCatalog Catalog, InstallPlan Plan)> InstallAsync(
        IBaselineSteps s, string sandbox, Func<DbCatalog, string?>? ddlTriggerGuard, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        var catalog = await s.ReadCatalogAsync(sandbox, ct);
        if (catalog.Tables.FirstOrDefault(t => string.Equals(t.Schema, AuditTriggerGenerator.StoreSchema, StringComparison.OrdinalIgnoreCase)) is { } clash)
            throw new InvalidOperationException($"샌드박스에 이미 {clash.Schema} 스키마 테이블({clash.Schema}.{clash.Name})이 있음: 감사 저장소 스키마(§6.2)와 겹쳐 설치하지 않음");
        // 4a. 4b·5의 DDL(CREATE USER·GRANT·CREATE TRIGGER)은 DB 범위 DDL 트리거를 발동시킨다: 위험한 트리거면 아무 DDL도 하기 전에 멈춘다.
        if (ddlTriggerGuard?.Invoke(catalog) is { } risky)
            throw new InvalidOperationException($"기준선 구축을 멈춤: {risky}");
        var before = await s.ReadRowCountsAsync(sandbox, ct);
        // 4b. 실행 사용자(Q12 수정): DDL 트리거가 CREATE USER·GRANT에 남기는 자동 커밋 행이 기준 스냅샷에 들어가도록 C9 설치 전에 만든다.
        var input = catalog.ToCaptureInput();
        await s.ExecuteAsync(sandbox, PreviewUserSql.EnsureBatch(input.Select(t => t.Schema)), ct);
        var afterUser = await s.ReadRowCountsAsync(sandbox, ct);
        var plan = AuditTriggerGenerator.Generate(input, clock());
        foreach (var b in plan.Batches) await s.ExecuteAsync(sandbox, b.Sql, ct);
        var after = await s.ReadRowCountsAsync(sandbox, ct);
        plan = plan with { Manifest = plan.Manifest with { Notes = [.. plan.Manifest.Notes, .. PreviewUserNotes(catalog, before, afterUser), .. InstallNotes(catalog, afterUser, after)] } };
        return (catalog, plan);
    }

    /// <summary>
    /// 백업 파일을 database로 복원한다(기준선 3단계, dbripple restore 공용). 대상 가드 → 파일 목록 → 세션 끊기(비정상 상태면 DROP) → RESTORE.
    /// RESTORE가 실패하면 SINGLE_USER로 남은 대상을 MULTI_USER로 되돌리고 원래 오류를 다시 던진다.
    /// </summary>
    public static async Task RestoreAsync(IBaselineSteps s, string database, string backupPath, string? source = null, CancellationToken ct = default)
    {
        BaselineSql.ValidateName(database, "복원 대상");
        if (string.IsNullOrWhiteSpace(backupPath)) throw new ArgumentException("백업 경로가 필요함");
        await RestoreCoreAsync(s, database, backupPath, source, files => BaselineSql.Restore(database, backupPath, files), ct);
    }

    static async Task RestoreCoreAsync(
        IBaselineSteps s, string database, string backupPath, string? source, Func<IReadOnlyList<BackupFile>, string> buildRestore, CancellationToken ct)
    {
        await s.ExecuteAsync(Master, BaselineSql.RestoreTargetGuard(database, source), ct);
        var files = await s.ReadFileListAsync(BaselineSql.FileListOnly(backupPath), ct);
        var restore = buildRestore(files); // 지원하지 않는 파일 형식은 세션을 끊기 전에 실패
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
        foreach (var (name, events) in FiringDdlTriggers(c, InstallDdlEvents))
            yield return $"DDL 트리거 {name}가 설치 DDL({string.Join("·", events)})에 발동함(ALTER_TRIGGER는 sp_settriggerorder)";
        if (ChangedTables(before, after) is { } changed)
            yield return $"설치 전후 행 수가 바뀐 테이블(기준선에 포함, 원본과 다름): {changed}";
    }

    /// <summary>실행 사용자 준비(4b, Q12)의 부작용 기록: CREATE USER·GRANT에 발동하는 DDL 트리거와, 준비 전후 행 수가 바뀐 기존 테이블(AW: dbo.DatabaseLog).</summary>
    public static IEnumerable<string> PreviewUserNotes(DbCatalog c, IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after)
    {
        foreach (var (name, events) in FiringDdlTriggers(c, PreviewUserSql.DdlEvents))
            yield return $"DDL 트리거 {name}가 실행 사용자 {PreviewUserSql.UserName} 준비 DDL({string.Join("·", events)})에 발동함(C9 설치 전, Q12)";
        if (ChangedTables(before, after) is { } changed)
            yield return $"실행 사용자 {PreviewUserSql.UserName} 준비 전후 행 수가 바뀐 테이블(기준선에 포함, 원본과 다름): {changed}";
    }

    static IEnumerable<(string Name, List<string> Events)> FiringDdlTriggers(DbCatalog c, IReadOnlyList<string> ddlEvents)
    {
        foreach (var g in c.Triggers.Where(t => t.ParentClass == "DATABASE" && !t.IsDisabled).GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            var events = ddlEvents.Where(e => g.Any(t => string.Equals(t.Event, e, StringComparison.OrdinalIgnoreCase))).ToList();
            if (events.Count > 0) yield return (g.Key, events);
        }
    }

    static string? ChangedTables(IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after)
    {
        var changed = before.Where(kv => after.TryGetValue(kv.Key, out var n) && n != kv.Value)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} {after[kv.Key] - kv.Value:+#;-#}행").ToList();
        return changed.Count > 0 ? string.Join(", ", changed) : null;
    }
}
