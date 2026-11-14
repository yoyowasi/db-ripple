using System.Text.RegularExpressions;

namespace DbRipple.Core.Baseline;

/// <summary>서버 쪽 경로 조립(서버가 Windows면 \, Linux면 /). 호스트 OS의 Path를 쓰지 않는다.</summary>
public static class ServerPaths
{
    public static string Join(string dir, string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        if (dir.EndsWith('/') || dir.EndsWith('\\')) return dir + file;
        return dir + (dir.Contains('\\') && !dir.Contains('/') ? "\\" : "/") + file;
    }

    /// <summary>파일 경로의 폴더(끝 구분자 포함). 구분자가 없으면 빈 문자열.</summary>
    public static string Directory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i < 0 ? "" : path[..(i + 1)];
    }
}

/// <summary>PC·회차 접미사가 붙은 이름(여러 개발자가 같은 서버 경로·원본을 써도 남의 파일·스냅샷을 덮거나 지우지 않게).</summary>
public static partial class RunNames
{
    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotAlnum();

    /// <summary>기계 이름 → 이름 조각(영문자·숫자·밑줄, 소문자, 최대 32자). 비면 "pc".</summary>
    public static string MachineTag(string? machineName)
    {
        var t = NotAlnum().Replace(machineName ?? "", "_").Trim('_').ToLowerInvariant();
        if (t.Length > 32) t = t[..32];
        return t.Length == 0 ? "pc" : t;
    }

    /// <summary>백업 파일 이름 dbr_&lt;pc&gt;_&lt;UTC yyyyMMddHHmmss&gt;.bak.</summary>
    public static string BackupFile(string machineTag, DateTimeOffset at) =>
        $"dbr_{MachineTag(machineTag)}_{at.UtcDateTime:yyyyMMddHHmmss}.bak";

    /// <summary>[적용] 원본 스냅샷 이름 &lt;원본&gt;&lt;접미사&gt;_&lt;pc&gt;_&lt;적용id&gt;(Split). 128자를 넘으면 오류.</summary>
    public static string OriginSnapshot(string originDb, string suffix, string machineTag, string applyId)
    {
        var name = $"{originDb}{suffix}_{MachineTag(machineTag)}_{applyId}";
        if (name.Length > 128) throw new InvalidOperationException($"원본 스냅샷 이름이 128자를 넘음({name.Length}자): 원본 이름이나 접미사를 줄일 것");
        BaselineSql.ValidateName(name, "원본 스냅샷");
        return name;
    }
}

/// <summary>RESTORE HEADERONLY 한 행에서 쓰는 열.</summary>
public sealed record BackupHeader(
    int BackupType, string? KeyAlgorithm, int SoftwareVersionMajor, int CompatibilityLevel, string? Collation, string? DatabaseName, DateTime? BackupFinishDate);

/// <summary>HEADERONLY 사전 검사(로컬 단계, 세션을 끊기 전에 실패한다).</summary>
public static class BackupHeaderCheck
{
    /// <summary>
    /// 백업 세트 1개, 전체 DB 백업(BackupType 1), 암호화 없음(KeyAlgorithm 없음·NO_Encryption), 백업 주버전 ≤ 로컬 주버전, 원본 이름 일치.
    /// 통과하면 그 행(CompatibilityLevel·Collation 기록용)을 돌려준다.
    /// </summary>
    public static BackupHeader Validate(IReadOnlyList<BackupHeader> rows, int localMajorVersion, string expectedDatabase)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidOperationException("백업 파일에 백업 세트가 없음");
        if (rows.Count > 1) throw new NotSupportedException($"백업 파일에 백업 세트가 {rows.Count}개 있음(INIT으로 만든 세트 1개만 지원)");
        var h = rows[0];
        if (h.BackupType != 1)
            throw new NotSupportedException($"전체 DB 백업(BackupType 1)만 복원함: 이 파일은 BackupType {h.BackupType}");
        if (!string.IsNullOrWhiteSpace(h.KeyAlgorithm) && !h.KeyAlgorithm.Equals("NO_Encryption", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"암호화된 백업({h.KeyAlgorithm})은 지원하지 않음(로컬에 인증서를 옮기지 않는다)");
        if (h.SoftwareVersionMajor > localMajorVersion)
            throw new NotSupportedException($"백업한 서버 주버전 {h.SoftwareVersionMajor}이(가) 로컬 {localMajorVersion}보다 높아 복원할 수 없음(로컬 이미지를 올릴 것)");
        if (!string.Equals(h.DatabaseName, expectedDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"백업 파일의 DB 이름 {h.DatabaseName}이(가) 원본 {expectedDatabase}와 다름(다른 DB의 백업)");
        return h;
    }
}

/// <summary>원본 서버의 인스턴스 이름(@@SERVERNAME, SERVERPROPERTY('MachineName')).</summary>
public sealed record InstanceIdentity(string ServerName, string MachineName);

/// <summary>로컬(샌드박스) 인스턴스 정보.</summary>
public sealed record LocalServerInfo(InstanceIdentity Identity, int MajorVersion, string DataDir, string LogDir);

/// <summary>같은 인스턴스 오인 방지(Split). 두 연결이 같은 인스턴스를 가리키면 기준선을 만들지 않는다.</summary>
public static class InstanceGuard
{
    /// <summary>같은 인스턴스로 보이면 이유, 아니면 null. markerSeenFromOrigin은 원본 연결에서 읽은 표식(안 보이면 null).</summary>
    public static string? SameInstanceReason(InstanceIdentity origin, InstanceIdentity sandbox, string? sandboxMarker, string? markerSeenFromOrigin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sandbox);
        if (string.Equals(origin.ServerName, sandbox.ServerName, StringComparison.OrdinalIgnoreCase))
            return $"원본과 샌드박스 연결의 @@SERVERNAME이 같음({origin.ServerName})";
        if (string.Equals(origin.MachineName, sandbox.MachineName, StringComparison.OrdinalIgnoreCase))
            return $"원본과 샌드박스 연결의 MachineName이 같음({origin.MachineName})";
        if (sandboxMarker is not null && string.Equals(sandboxMarker, markerSeenFromOrigin, StringComparison.OrdinalIgnoreCase))
            return "샌드박스 인스턴스 표식이 원본 연결에서도 보임(같은 인스턴스)";
        return null;
    }
}

/// <summary>
/// Split 기준선 구성. 백업 파일 출처는 둘 중 하나: ImportFile(사용자가 넘긴 .bak, 원본 백업 권한 불필요) 또는 BackupServerDir + SharePath(원본 서버가 쓴 파일을 UNC로 읽어 옴).
/// </summary>
public sealed record SplitBaselineOptions
{
    public required string Source { get; init; }
    public required string Sandbox { get; init; }
    public required string Snapshot { get; init; }
    /// <summary>원본 서버가 백업 파일을 쓸 폴더(서버 쪽 경로, DbRipple:Backup:ServerPath).</summary>
    public string? BackupServerDir { get; init; }
    /// <summary>같은 폴더를 이 PC에서 읽는 UNC 경로(DbRipple:Backup:SharePath).</summary>
    public string? SharePath { get; init; }
    /// <summary>사용자가 지정한 .bak(DbRipple:Backup:ImportFile). 있으면 원본에서 백업하지 않는다.</summary>
    public string? ImportFile { get; init; }
    /// <summary>가져오기 폴더의 컨테이너 안 경로(compose 바인드 마운트, 예: /var/opt/mssql/import).</summary>
    public required string ImportContainerDir { get; init; }
    /// <summary>복원 뒤 가져온 .bak 삭제(기본 true). 자기가 복사한 파일만 지운다.</summary>
    public bool DeleteImportedBackup { get; init; } = true;
    /// <summary>PC 이름 조각(RunNames.MachineTag).</summary>
    public required string MachineTag { get; init; }
    /// <summary>설치 전 DB 범위 DDL 트리거 검사(BaselineOptions.DdlTriggerGuard와 같음). null이면 검사하지 않는다.</summary>
    public Func<Catalog.DbCatalog, string?>? DdlTriggerGuard { get; init; }

    public void Validate()
    {
        BaselineBuilder.Validate(new BaselineOptions(Source, Sandbox, Snapshot, ImportContainerDir));
        if (ImportFile is null && (string.IsNullOrWhiteSpace(BackupServerDir) || string.IsNullOrWhiteSpace(SharePath)))
            throw new ArgumentException("Split 기준선에는 DbRipple:Backup:ImportFile, 또는 DbRipple:Backup:ServerPath와 DbRipple:Backup:SharePath가 필요함");
    }
}

/// <summary>원본 연결로 하는 일(읽기 + COPY_ONLY 백업). 임의 SQL 실행 메서드가 없어 RESTORE·DROP을 원본에 보낼 수 없다.</summary>
public interface IOriginSteps
{
    Task<InstanceIdentity> ReadIdentityAsync(CancellationToken ct);
    /// <summary>원본 master에서 보이는 dbripple 표식(없거나 안 보이면 null).</summary>
    Task<string?> ReadInstanceMarkerAsync(CancellationToken ct);
    /// <summary>BaselineSql.SourcePrecheck(master) + SourceGuard(원본 DB) 실행. 읽기만.</summary>
    Task PrecheckAsync(string source, CancellationToken ct);
    /// <summary>FILESTREAM(FD)·메모리 최적화(FX) 파일 그룹 이름 → 형식.</summary>
    Task<IReadOnlyDictionary<string, string>> ReadSpecialFileGroupsAsync(string source, CancellationToken ct);
    Task<Fingerprint> ReadFingerprintAsync(string source, CancellationToken ct);
    /// <summary>BaselineSql.SplitBackup.</summary>
    Task BackupAsync(string source, string serverPath, CancellationToken ct);
}

/// <summary>샌드박스 연결로 하는 일(파괴 단계 포함). SingleServer 단계(IBaselineSteps)에 Split 단계를 더한다.</summary>
public interface ISandboxSteps : IBaselineSteps
{
    Task<LocalServerInfo> ReadServerInfoAsync(CancellationToken ct);
    /// <summary>로컬 master 표식을 보장하고 값을 돌려준다.</summary>
    Task<string> EnsureInstanceMarkerAsync(CancellationToken ct);
    Task<IReadOnlyList<BackupHeader>> ReadHeadersAsync(string sql, CancellationToken ct);
    Task<Fingerprint> ReadFingerprintAsync(string database, CancellationToken ct);
}

/// <summary>백업 파일을 가져오기 폴더(컨테이너에 바인드 마운트되는 호스트 폴더)로 둔다.</summary>
public interface IBackupImporter
{
    /// <summary>sourceFile을 가져오기 폴더의 fileName으로 복사한다.</summary>
    Task ImportAsync(string sourceFile, string fileName, CancellationToken ct);
    /// <summary>가져오기 폴더의 fileName을 지운다(이 실행이 복사한 파일만 부른다).</summary>
    void Delete(string fileName);
    /// <summary>원본 파일의 마지막 수정 시각(ImportFile 기준 시각용).</summary>
    DateTimeOffset LastWriteTime(string sourceFile);
}

/// <summary>호스트 폴더 복사 구현.</summary>
public sealed class FileBackupImporter(string importDir) : IBackupImporter
{
    public string ImportDir { get; } = Path.GetFullPath(importDir);

    public async Task ImportAsync(string sourceFile, string fileName, CancellationToken ct)
    {
        if (!File.Exists(sourceFile)) throw new FileNotFoundException("백업 파일을 찾지 못함(UNC 공유·경로·권한 확인): " + sourceFile);
        Directory.CreateDirectory(ImportDir);
        var target = Target(fileName);
        await using var from = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await from.CopyToAsync(to, ct);
    }

    public void Delete(string fileName)
    {
        var target = Target(fileName);
        if (File.Exists(target)) File.Delete(target);
    }

    public DateTimeOffset LastWriteTime(string sourceFile) => new(File.GetLastWriteTimeUtc(sourceFile), TimeSpan.Zero);

    string Target(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.Contains('/') || fileName.Contains('\\') || fileName != Path.GetFileName(fileName))
            throw new ArgumentException("가져오기 파일 이름에 경로를 쓸 수 없음", nameof(fileName));
        return Path.Combine(ImportDir, fileName);
    }
}
