using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using Microsoft.Data.SqlClient;

namespace DbRipple.Preview.Settings;

/// <summary>환경 변수 설정(개발·평가). 있으면 설정 API는 읽기 전용(source env).</summary>
public sealed record EnvSettings(ServerConnections Connections, string OriginDb, string SandboxDb, BackupSettings Backup);

/// <summary>설정 서비스 구성.</summary>
public sealed record ProfileSettingsOptions
{
    /// <summary>profile.json·profile.secret 폴더(%LOCALAPPDATA%\db-ripple).</summary>
    public required string DataDir { get; init; }
    /// <summary>환경 변수 설정(있으면 우선).</summary>
    public EnvSettings? Env { get; init; }
    /// <summary>설정이 없을 때 보이는 기본 원본 DB.</summary>
    public string DefaultOriginDb { get; init; } = "AW_Origin";
    /// <summary>기본 샌드박스 DB.</summary>
    public string DefaultSandboxDb { get; init; } = "AW_Sandbox";
    /// <summary>기본 샌드박스 서버(compose 127.0.0.1:14333).</summary>
    public string DefaultSandboxServer { get; init; } = "127.0.0.1,14333";
}

/// <summary>
/// 설정(프로필) 서비스(2026-11-13 N4). 저장: DataDir\profile.json(schemaVersion 1, 비밀값 없음) + profile.secret(비밀번호, <see cref="ISecretProtector"/>: Host는 DPAPI CurrentUser로 키를 보호한 ASP.NET Data Protection).
/// rememberPassword=false인 비밀번호는 이 프로세스 메모리에만 둔다. 연결 문자열은 SqlConnectionStringBuilder로만 만든다
/// (Encrypt=Mandatory, 원본 TrustServerCertificate는 사용자가 켤 때만, 샌드박스는 루프백일 때만 true).
/// 서버나 사용자 이름이 바뀌면 기존 비밀번호를 재사용하지 않는다(다른 서버로 비밀번호가 가지 않게).
/// </summary>
public sealed class ProfileSettingsService : ISettingsService
{
    /// <summary>프로필 스키마 버전.</summary>
    public const int SchemaVersion = 1;
    /// <summary>프로필 파일 이름.</summary>
    public const string ProfileFileName = "profile.json";
    /// <summary>비밀번호 파일 이름.</summary>
    public const string SecretFileName = "profile.secret";
    /// <summary>Split 샌드박스가 루프백이 아닐 때 오류 문구(계약 4절).</summary>
    public const string SandboxNotLoopback = "샌드박스는 이 PC의 SQL Server만 쓸 수 있습니다";

    static readonly JsonSerializerOptions FileJson = new(ContractJson.Options) { WriteIndented = true };

    readonly ProfileSettingsOptions options;
    readonly ISecretProtector protector;
    readonly ISqlServerProbe probe;
    readonly IConnectionSwitch target;
    readonly SemaphoreSlim saveGate = new(1, 1);
    readonly object gate = new();
    ProfileFile? profile;
    string? originPassword, sandboxPassword;

    /// <summary>만든다. 프로필은 <see cref="Initialize"/>에서 읽는다.</summary>
    public ProfileSettingsService(ProfileSettingsOptions options, ISecretProtector protector, ISqlServerProbe probe, IConnectionSwitch target)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
    }

    string ProfilePath => Path.Combine(options.DataDir, ProfileFileName);
    string SecretPath => Path.Combine(options.DataDir, SecretFileName);

    /// <summary>시작 때 읽기에 실패한 이유(비밀값 없음). 없으면 null.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>프로필·비밀번호를 읽고, 연결을 만들 수 있으면 실행 연결을 돌려준다(환경 변수 우선이면 null).</summary>
    public RuntimeProfile? Initialize()
    {
        if (options.Env is not null || !File.Exists(ProfilePath)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(ProfilePath), FileJson);
            if (p is null || p.SchemaVersion != SchemaVersion) { LoadWarning = "profile.json 형식·버전이 맞지 않아 무시함"; return null; }
            lock (gate) profile = p;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadWarning = $"profile.json을 읽지 못함({ex.GetType().Name})";
            return null;
        }
        if (File.Exists(SecretPath))
        {
            try
            {
                var secret = JsonSerializer.Deserialize<SecretFile>(protector.Unprotect(File.ReadAllBytes(SecretPath)));
                lock (gate) { originPassword = secret?.Origin; sandboxPassword = secret?.Sandbox; }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoadWarning = $"profile.secret을 풀지 못함({ex.GetType().Name}): 비밀번호를 다시 입력해야 함";
            }
        }
        lock (gate) return Runtime(profile!, originPassword, sandboxPassword);
    }

    /// <inheritdoc />
    public Task<SettingsView> GetAsync(CancellationToken ct)
    {
        if (options.Env is { } env) return Task.FromResult(EnvView(env));
        lock (gate)
        {
            if (profile is not { } p) return Task.FromResult(DefaultView());
            var oKnown = p.Origin.Auth == SqlAuthKind.Sql && originPassword is not null;
            var sKnown = p.Sandbox.Auth == SqlAuthKind.Sql && sandboxPassword is not null;
            return Task.FromResult(new SettingsView
            {
                Source = SettingsSource.Profile,
                Configured = Runtime(p, originPassword, sandboxPassword) is not null,
                Mode = p.Mode,
                Origin = new OriginSettingsView
                {
                    Server = p.Origin.Server, Database = p.Origin.Database, Auth = p.Origin.Auth, User = p.Origin.User,
                    HasStoredPassword = oKnown, TrustServerCertificate = p.Origin.TrustServerCertificate,
                },
                Sandbox = new SandboxSettingsView
                {
                    Server = p.Sandbox.Server, Database = p.Sandbox.Database, Auth = p.Sandbox.Auth, User = p.Sandbox.User, HasStoredPassword = sKnown,
                },
                Backup = p.Backup,
            });
        }
    }

    /// <inheritdoc />
    public async Task<SettingsSaveOutcome> SaveAsync(SettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options.Env is not null) return new SettingsSaveOutcome { EnvOverride = true };
        await saveGate.WaitAsync(ct);
        try
        {
            var (errors, n) = Normalize(request);
            if (errors.Count > 0) return new SettingsSaveOutcome { Errors = errors };
            target.EnsureIdle();
            Directory.CreateDirectory(options.DataDir);
            WriteAtomic(ProfilePath, JsonSerializer.SerializeToUtf8Bytes(n.File, FileJson));
            var remembered = new SecretFile(
                n.File.Mode == ServerMode.Split && n.File.Origin.RememberPassword ? n.OriginPassword : null,
                n.File.Sandbox.RememberPassword ? n.SandboxPassword : null);
            if (remembered.Origin is null && remembered.Sandbox is null) File.Delete(SecretPath);
            else WriteAtomic(SecretPath, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(remembered)));
            RuntimeProfile runtime;
            lock (gate)
            {
                profile = n.File;
                originPassword = n.OriginPassword;
                sandboxPassword = n.SandboxPassword;
                runtime = Runtime(n.File, originPassword, sandboxPassword)!;
            }
            target.Switch(runtime);
            return new SettingsSaveOutcome();
        }
        finally { saveGate.Release(); }
    }

    /// <inheritdoc />
    public async Task<SettingsTestOutcome> TestAsync(SettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (errors, n) = Normalize(request);
        if (errors.Count > 0) return new SettingsTestOutcome { Errors = errors };
        var f = n.File;
        var sandboxCs = SandboxConnection(f.Sandbox, n.SandboxPassword);
        var originCs = f.Mode == ServerMode.Split ? OriginConnection(f.Origin, n.OriginPassword) : sandboxCs;
        var origin = await probe.ProbeOriginAsync(originCs, f.Origin.Database, ct);
        var sandbox = await probe.ProbeSandboxAsync(sandboxCs, ServerConnections.IsLoopback(f.Sandbox.Server), ct);
        var warnings = new List<string>();
        if (origin.IsSysadmin == true) warnings.Add("원본 로그인이 sysadmin입니다. [적용]은 확인을 한 번 더 받습니다.");
        if (f.Mode == ServerMode.Split && origin.Ok && origin.CanBackup == false && f.Backup.ImportFile is null)
            warnings.Add("원본 DB에 BACKUP 권한이 없습니다. 기준선을 만들려면 백업 파일(importFile)을 지정하세요.");
        if (origin.Ok && origin.CanCreateDatabase == false)
            warnings.Add("원본에 CREATE DATABASE 권한이 없어 [적용] 검증은 KEYSET(약한 검증)이 됩니다.");
        return new SettingsTestOutcome { Result = new SettingsTestResult { Origin = origin, Sandbox = sandbox, Warnings = warnings } };
    }

    // 입력 검증·정리. 비밀번호는 입력 또는 같은 서버·사용자의 기존 값.
    (Dictionary<string, string[]> Errors, Normalized N) Normalize(SettingsRequest r)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Err(string key, string message) => errors[key] = [message];
        var split = r.Mode == ServerMode.Split;
        var o = r.Origin ?? new OriginSettingsInput();
        var s = r.Sandbox ?? new SandboxSettingsInput();
        var b = r.Backup ?? new BackupSettings();

        var oServer = Clean(o.Server); var oDb = Clean(o.Database); var oUser = Clean(o.User);
        var sServer = Clean(s.Server); var sDb = Clean(s.Database); var sUser = Clean(s.User);
        if (!Enum.IsDefined(r.Mode)) Err("mode", "split 또는 single_server");
        if (split)
        {
            if (!ValidServer(oServer)) Err("origin.server", "원본 서버를 입력하세요(제어 문자·세미콜론 없이 256자 이하).");
            if (o.Auth == SqlAuthKind.Sql && !ValidName(oUser)) Err("origin.user", "SQL 인증 사용자를 입력하세요(128자 이하).");
        }
        if (!ValidName(oDb) || !ValidDbName(oDb)) Err("origin.database", "원본 DB 이름을 입력하세요(128자 이하, ] ' \" 없이).");
        if (!ValidServer(sServer)) Err("sandbox.server", "샌드박스 서버를 입력하세요(제어 문자·세미콜론 없이 256자 이하).");
        else if (split && !ServerConnections.IsLoopback(sServer)) Err("sandbox.server", SandboxNotLoopback);
        if (!ValidName(sDb) || !ValidDbName(sDb)) Err("sandbox.database", "샌드박스 DB 이름을 입력하세요(128자 이하, ] ' \" 없이).");
        else if (!split && oDb is not null && (string.Equals(sDb, oDb, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(sDb + "_base", oDb, StringComparison.OrdinalIgnoreCase)))
            Err("sandbox.database", "한 인스턴스 모드에서는 샌드박스 DB가 원본 DB와 달라야 합니다.");
        if (s.Auth == SqlAuthKind.Sql && !ValidName(sUser)) Err("sandbox.user", "SQL 인증 사용자를 입력하세요(128자 이하).");
        if (o.Password is { Length: > 128 }) Err("origin.password", "비밀번호는 128자 이하입니다.");
        if (s.Password is { Length: > 128 }) Err("sandbox.password", "비밀번호는 128자 이하입니다.");

        var backup = new BackupSettings { ServerPath = Clean(b.ServerPath), SharePath = Clean(b.SharePath), ImportFile = Clean(b.ImportFile) };
        if (!ValidPath(backup.ServerPath)) Err("backup.serverPath", "경로가 올바르지 않습니다(260자 이하).");
        if (!ValidPath(backup.SharePath) || backup.SharePath is { } share && !share.StartsWith(@"\\", StringComparison.Ordinal))
            Err("backup.sharePath", @"UNC 경로(\\서버\공유)를 입력하세요.");
        if ((backup.ServerPath is null) != (backup.SharePath is null)) Err(backup.ServerPath is null ? "backup.serverPath" : "backup.sharePath", "serverPath와 sharePath는 함께 입력합니다.");
        if (!ValidPath(backup.ImportFile) || backup.ImportFile is { } importPath && !Path.IsPathFullyQualified(importPath))
            Err("backup.importFile", "전체 경로의 .bak 파일을 입력하세요.");

        var file = new ProfileFile
        {
            SchemaVersion = SchemaVersion,
            Mode = r.Mode,
            Origin = new OriginPart
            {
                Server = split ? oServer : null, Database = oDb ?? "", Auth = split ? o.Auth : SqlAuthKind.Windows,
                User = split && o.Auth == SqlAuthKind.Sql ? oUser : null,
                RememberPassword = split && o.Auth == SqlAuthKind.Sql && o.RememberPassword,
                TrustServerCertificate = split && o.TrustServerCertificate,
            },
            Sandbox = new SandboxPart
            {
                Server = sServer ?? "", Database = sDb ?? "", Auth = s.Auth, User = s.Auth == SqlAuthKind.Sql ? sUser : null,
                RememberPassword = s.Auth == SqlAuthKind.Sql && s.RememberPassword,
            },
            Backup = backup,
        };

        string? oPw = null, sPw = null;
        lock (gate)
        {
            if (split && o.Auth == SqlAuthKind.Sql)
            {
                oPw = string.IsNullOrEmpty(o.Password)
                    ? profile is { Mode: ServerMode.Split, Origin: { Auth: SqlAuthKind.Sql } po } && Same(po.Server, oServer) && Same(po.User, oUser) ? originPassword : null
                    : o.Password;
                if (oPw is null && !errors.ContainsKey("origin.password")) Err("origin.password", "원본 비밀번호를 입력하세요.");
            }
            if (s.Auth == SqlAuthKind.Sql)
            {
                sPw = string.IsNullOrEmpty(s.Password)
                    ? profile is { Sandbox: { Auth: SqlAuthKind.Sql } ps } && Same(ps.Server, sServer) && Same(ps.User, sUser) ? sandboxPassword : null
                    : s.Password;
                if (sPw is null && !errors.ContainsKey("sandbox.password")) Err("sandbox.password", "샌드박스 비밀번호를 입력하세요.");
            }
        }
        return (errors, new Normalized(file, oPw, sPw));
    }

    // 프로필 + 비밀번호 → 실행 연결. 필요한 비밀번호가 없으면 null.
    static RuntimeProfile? Runtime(ProfileFile p, string? originPw, string? sandboxPw)
    {
        if (p.Sandbox.Auth == SqlAuthKind.Sql && sandboxPw is null) return null;
        var sandboxCs = SandboxConnection(p.Sandbox, sandboxPw);
        ServerConnections connections;
        if (p.Mode == ServerMode.Split)
        {
            if (p.Origin.Auth == SqlAuthKind.Sql && originPw is null) return null;
            if (string.IsNullOrEmpty(p.Origin.Server)) return null;
            connections = ServerConnections.Split(OriginConnection(p.Origin, originPw), sandboxCs);
        }
        else connections = ServerConnections.SingleServer(sandboxCs);
        return new RuntimeProfile(connections, p.Origin.Database, p.Sandbox.Database, p.Sandbox.Database + "_base", p.Backup);
    }

    static string OriginConnection(OriginPart o, string? password) => Build(o.Server!, o.Auth, o.User, password, o.TrustServerCertificate);

    static string SandboxConnection(SandboxPart s, string? password) => Build(s.Server, s.Auth, s.User, password, ServerConnections.IsLoopback(s.Server));

    static string Build(string server, SqlAuthKind auth, string? user, string? password, bool trust)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = trust,
            ApplicationName = "db-ripple",
        };
        if (auth == SqlAuthKind.Windows) b.IntegratedSecurity = true;
        else
        {
            b.UserID = user;
            b.Password = password;
        }
        return b.ConnectionString;
    }

    SettingsView DefaultView() => new()
    {
        Source = SettingsSource.None,
        Configured = false,
        Mode = ServerMode.Split,
        Origin = new OriginSettingsView { Database = options.DefaultOriginDb, Auth = SqlAuthKind.Windows, HasStoredPassword = false, TrustServerCertificate = false },
        Sandbox = new SandboxSettingsView { Server = options.DefaultSandboxServer, Database = options.DefaultSandboxDb, Auth = SqlAuthKind.Sql, User = "sa", HasStoredPassword = false },
        Backup = new BackupSettings(),
    };

    static SettingsView EnvView(EnvSettings env)
    {
        var o = new SqlConnectionStringBuilder(env.Connections.Origin.ConnectionString);
        var s = new SqlConnectionStringBuilder(env.Connections.Sandbox.ConnectionString);
        return new SettingsView
        {
            Source = SettingsSource.Env,
            Configured = true,
            Mode = env.Connections.Mode,
            Origin = new OriginSettingsView
            {
                Server = o.DataSource, Database = env.OriginDb, Auth = o.IntegratedSecurity ? SqlAuthKind.Windows : SqlAuthKind.Sql,
                User = o.IntegratedSecurity ? null : o.UserID, HasStoredPassword = !string.IsNullOrEmpty(o.Password), TrustServerCertificate = o.TrustServerCertificate,
            },
            Sandbox = new SandboxSettingsView
            {
                Server = s.DataSource, Database = env.SandboxDb, Auth = s.IntegratedSecurity ? SqlAuthKind.Windows : SqlAuthKind.Sql,
                User = s.IntegratedSecurity ? null : s.UserID, HasStoredPassword = !string.IsNullOrEmpty(s.Password),
            },
            Backup = env.Backup,
        };
    }

    static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static bool NoControl(string v) => !v.Any(char.IsControl);
    static bool ValidServer(string? v) => v is { Length: <= 256 } && NoControl(v) && !v.Contains(';');
    static bool ValidName(string? v) => v is { Length: <= 128 } && NoControl(v);
    static bool ValidDbName(string? v) => v is not null && v.IndexOfAny([']', '\'', '"', ';']) < 0;
    static bool ValidPath(string? v) => v is null || v.Length <= 260 && NoControl(v);

    sealed record Normalized(ProfileFile File, string? OriginPassword, string? SandboxPassword);

    sealed record SecretFile(string? Origin, string? Sandbox);

    sealed record ProfileFile
    {
        public int SchemaVersion { get; init; }
        public ServerMode Mode { get; init; }
        public required OriginPart Origin { get; init; }
        public required SandboxPart Sandbox { get; init; }
        public required BackupSettings Backup { get; init; }
    }

    sealed record OriginPart
    {
        public string? Server { get; init; }
        public required string Database { get; init; }
        public SqlAuthKind Auth { get; init; }
        public string? User { get; init; }
        public bool RememberPassword { get; init; }
        public bool TrustServerCertificate { get; init; }
    }

    sealed record SandboxPart
    {
        public required string Server { get; init; }
        public required string Database { get; init; }
        public SqlAuthKind Auth { get; init; }
        public string? User { get; init; }
        public bool RememberPassword { get; init; }
    }
}
