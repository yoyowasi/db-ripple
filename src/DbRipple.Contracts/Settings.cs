using System.Text.Json.Serialization;
using DbRipple.Core.Baseline;

namespace DbRipple.Contracts;

// 2026-11-13 N4 설정(프로필) API 계약(GET/PUT /api/settings, POST /api/settings/test). 비밀번호·연결 문자열은 응답에 넣지 않는다.

/// <summary>설정 출처: profile(%LOCALAPPDATA%\db-ripple\profile.json), env(DBRIPPLE_*_CONNECTION, 읽기 전용), none(아직 없음).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsSource>))]
public enum SettingsSource
{
    /// <summary>프로필 파일.</summary>
    [JsonStringEnumMemberName("profile")] Profile,
    /// <summary>환경 변수(개발·평가용). 설정 API는 읽기 전용.</summary>
    [JsonStringEnumMemberName("env")] Env,
    /// <summary>설정 없음.</summary>
    [JsonStringEnumMemberName("none")] None,
}

/// <summary>SQL Server 인증 방식.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SqlAuthKind>))]
public enum SqlAuthKind
{
    /// <summary>Windows 통합 인증.</summary>
    [JsonStringEnumMemberName("windows")] Windows,
    /// <summary>SQL 인증(사용자·비밀번호).</summary>
    [JsonStringEnumMemberName("sql")] Sql,
}

/// <summary>원본 연결 표시(비밀값 없음).</summary>
public sealed record OriginSettingsView
{
    /// <summary>서버(Data Source).</summary>
    public string? Server { get; init; }
    /// <summary>원본 DB.</summary>
    public required string Database { get; init; }
    /// <summary>인증 방식.</summary>
    public required SqlAuthKind Auth { get; init; }
    /// <summary>SQL 인증 사용자(Windows 인증이면 null).</summary>
    public string? User { get; init; }
    /// <summary>서버가 이 연결의 비밀번호를 알고 있음(profile.secret 또는 이번 실행 메모리).</summary>
    public required bool HasStoredPassword { get; init; }
    /// <summary>서버 인증서를 검증 없이 믿음(원본에만, 사용자가 명시적으로 켤 때만).</summary>
    public required bool TrustServerCertificate { get; init; }
}

/// <summary>샌드박스 연결 표시(비밀값 없음).</summary>
public sealed record SandboxSettingsView
{
    /// <summary>서버(Data Source). Split이면 루프백만.</summary>
    public string? Server { get; init; }
    /// <summary>샌드박스 DB.</summary>
    public required string Database { get; init; }
    /// <summary>인증 방식.</summary>
    public required SqlAuthKind Auth { get; init; }
    /// <summary>SQL 인증 사용자.</summary>
    public string? User { get; init; }
    /// <summary>서버가 이 연결의 비밀번호를 알고 있음.</summary>
    public required bool HasStoredPassword { get; init; }
}

/// <summary>Split 기준선 백업 경로(비밀값 아님).</summary>
public sealed record BackupSettings
{
    /// <summary>원본 서버가 .bak을 쓰는 서버 기준 폴더.</summary>
    public string? ServerPath { get; init; }
    /// <summary>같은 폴더를 이 PC에서 읽는 UNC 경로.</summary>
    public string? SharePath { get; init; }
    /// <summary>이미 있는 .bak(있으면 원본에서 백업하지 않는다).</summary>
    public string? ImportFile { get; init; }
}

/// <summary>GET /api/settings 응답.</summary>
public sealed record SettingsView
{
    /// <summary>설정 출처.</summary>
    public required SettingsSource Source { get; init; }
    /// <summary>연결을 만들 수 있는 상태(필요한 비밀번호까지 있음).</summary>
    public required bool Configured { get; init; }
    /// <summary>서버 배치.</summary>
    public required ServerMode Mode { get; init; }
    /// <summary>원본.</summary>
    public required OriginSettingsView Origin { get; init; }
    /// <summary>샌드박스.</summary>
    public required SandboxSettingsView Sandbox { get; init; }
    /// <summary>백업 경로.</summary>
    public required BackupSettings Backup { get; init; }
}

/// <summary>원본 입력(PUT·test).</summary>
public sealed record OriginSettingsInput
{
    /// <summary>서버.</summary>
    public string? Server { get; init; }
    /// <summary>원본 DB.</summary>
    public string? Database { get; init; }
    /// <summary>인증 방식(기본 windows).</summary>
    public SqlAuthKind Auth { get; init; } = SqlAuthKind.Windows;
    /// <summary>SQL 인증 사용자.</summary>
    public string? User { get; init; }
    /// <summary>비밀번호(바꿀 때만). 비우면 같은 서버·사용자의 기존 비밀번호를 쓴다.</summary>
    public string? Password { get; init; }
    /// <summary>비밀번호를 DPAPI로 저장(false면 이번 실행 메모리에만).</summary>
    public bool RememberPassword { get; init; }
    /// <summary>서버 인증서를 검증 없이 믿음.</summary>
    public bool TrustServerCertificate { get; init; }
}

/// <summary>샌드박스 입력(PUT·test).</summary>
public sealed record SandboxSettingsInput
{
    /// <summary>서버.</summary>
    public string? Server { get; init; }
    /// <summary>샌드박스 DB.</summary>
    public string? Database { get; init; }
    /// <summary>인증 방식(기본 sql).</summary>
    public SqlAuthKind Auth { get; init; } = SqlAuthKind.Sql;
    /// <summary>SQL 인증 사용자.</summary>
    public string? User { get; init; }
    /// <summary>비밀번호(바꿀 때만).</summary>
    public string? Password { get; init; }
    /// <summary>비밀번호를 DPAPI로 저장.</summary>
    public bool RememberPassword { get; init; }
}

/// <summary>PUT /api/settings, POST /api/settings/test 본문.</summary>
public sealed record SettingsRequest
{
    /// <summary>서버 배치(기본 split).</summary>
    public ServerMode Mode { get; init; } = ServerMode.Split;
    /// <summary>원본.</summary>
    public OriginSettingsInput? Origin { get; init; }
    /// <summary>샌드박스.</summary>
    public SandboxSettingsInput? Sandbox { get; init; }
    /// <summary>백업 경로.</summary>
    public BackupSettings? Backup { get; init; }
}

/// <summary>원본 연결 시험 결과(읽기만).</summary>
public sealed record OriginTestResult
{
    /// <summary>연결 성공.</summary>
    public required bool Ok { get; init; }
    /// <summary>@@SERVERNAME.</summary>
    public string? ServerName { get; init; }
    /// <summary>SERVERPROPERTY('ProductVersion').</summary>
    public string? ProductVersion { get; init; }
    /// <summary>SERVERPROPERTY('Edition').</summary>
    public string? Edition { get; init; }
    /// <summary>IS_SRVROLEMEMBER('sysadmin').</summary>
    public bool? IsSysadmin { get; init; }
    /// <summary>원본 DB BACKUP DATABASE 권한(db_backupoperator 포함).</summary>
    public bool? CanBackup { get; init; }
    /// <summary>CREATE DATABASE(스냅샷) 권한.</summary>
    public bool? CanCreateDatabase { get; init; }
    /// <summary>오류(번호와 짧은 설명만, 비밀값 없음).</summary>
    public string? Error { get; init; }
}

/// <summary>샌드박스 연결 시험 결과(읽기만).</summary>
public sealed record SandboxTestResult
{
    /// <summary>연결 성공.</summary>
    public required bool Ok { get; init; }
    /// <summary>@@SERVERNAME.</summary>
    public string? ServerName { get; init; }
    /// <summary>SERVERPROPERTY('ProductVersion').</summary>
    public string? ProductVersion { get; init; }
    /// <summary>Data Source가 이 PC인지.</summary>
    public required bool IsLoopback { get; init; }
    /// <summary>오류(번호와 짧은 설명만).</summary>
    public string? Error { get; init; }
}

/// <summary>POST /api/settings/test 응답.</summary>
public sealed record SettingsTestResult
{
    /// <summary>원본.</summary>
    public required OriginTestResult Origin { get; init; }
    /// <summary>샌드박스.</summary>
    public required SandboxTestResult Sandbox { get; init; }
    /// <summary>경고(sysadmin, 권한 부족 등).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>저장 결과. Errors가 있으면 400, EnvOverride면 409.</summary>
public sealed record SettingsSaveOutcome
{
    /// <summary>필드별 오류(점 표기 키: origin.server 등). 없으면 null.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
    /// <summary>환경 변수 설정이 우선이라 저장하지 않음.</summary>
    public bool EnvOverride { get; init; }
}

/// <summary>시험 결과. Errors가 있으면 400(입력 오류로 연결하지 않음).</summary>
public sealed record SettingsTestOutcome
{
    /// <summary>필드별 오류. 없으면 null.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
    /// <summary>시험 결과(Errors가 있으면 null).</summary>
    public SettingsTestResult? Result { get; init; }
}

/// <summary>설정(프로필) 서비스(2026-11-13 N4). 구현은 Preview 프로젝트 ProfileSettingsService.</summary>
public interface ISettingsService
{
    /// <summary>현재 설정(비밀값 없음).</summary>
    Task<SettingsView> GetAsync(CancellationToken ct);
    /// <summary>검증 → 저장 → 연결 다시 만들기(샌드박스는 기준선 재구축 필요 표시).</summary>
    Task<SettingsSaveOutcome> SaveAsync(SettingsRequest request, CancellationToken ct);
    /// <summary>저장하지 않고 두 서버에 연결해 권한을 읽기만 한다.</summary>
    Task<SettingsTestOutcome> TestAsync(SettingsRequest request, CancellationToken ct);
}
