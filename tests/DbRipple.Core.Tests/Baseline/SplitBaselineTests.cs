using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Core.Tests.Baseline;

/// <summary>Split 모드(2026-11-13): 연결·모드 선택, 같은 인스턴스 판정, 지문 v2 판정, 이름 접미사, HEADERONLY 판정, Split 기준선 순서(DB 없음).</summary>
public class SplitBaselineTests
{
    const string Secret = "S3cret!pw";
    static readonly string Remote = $"Data Source=dev-sql.corp,1433;User ID=me;Password={Secret};Encrypt=True";
    static readonly string Local = $"Data Source=127.0.0.1,14333;User ID=sa;Password={Secret};Encrypt=True;TrustServerCertificate=True";

    static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        k => vars.FirstOrDefault(v => v.Key == k).Value;

    static void Parse(string sql)
    {
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Message)) + "\n" + sql);
    }

    // ---- 모드·연결 선택 ----

    [Fact]
    public void Default_mode_is_split_and_uses_two_typed_connections()
    {
        var c = ServerConnections.Resolve(null, Env((ServerConnections.OriginEnvVar, Remote), (ServerConnections.SandboxEnvVar, Local)));
        Assert.Equal(ServerMode.Split, c.Mode);
        Assert.Equal(Remote, c.Origin.ConnectionString);
        Assert.Equal(Local, c.Sandbox.ConnectionString);
        Assert.Empty(c.Warnings());
    }

    [Fact]
    public void Single_server_wraps_the_existing_variable_in_both_types()
    {
        var c = ServerConnections.Resolve("SingleServer", Env((ServerConnections.SingleEnvVar, Local)));
        Assert.Equal(ServerMode.SingleServer, c.Mode);
        Assert.Equal(c.Origin.ConnectionString, c.Sandbox.ConnectionString);
    }

    [Theory]
    [InlineData(ServerConnections.OriginEnvVar)]
    [InlineData(ServerConnections.SandboxEnvVar)]
    [InlineData(ServerConnections.SingleEnvVar)]
    public void Split_with_one_variable_is_a_clear_error_without_secrets(string only)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServerConnections.Resolve("Split", Env((only, Remote))));
        Assert.Contains(ServerConnections.OriginEnvVar, ex.Message);
        Assert.Contains(ServerConnections.SandboxEnvVar, ex.Message);
        Assert.Contains("SingleServer", ex.Message);
        Assert.Contains(only, ex.Message); // 지금 있는 변수 이름
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public void Unknown_mode_and_missing_single_variable_fail()
    {
        Assert.Throws<InvalidOperationException>(() => ServerConnections.ParseMode("Both"));
        var ex = Assert.Throws<InvalidOperationException>(() => ServerConnections.Resolve("singleserver", Env((ServerConnections.OriginEnvVar, Remote))));
        Assert.Contains(ServerConnections.SingleEnvVar, ex.Message);
    }

    [Theory]
    [InlineData("127.0.0.1,14333", true)]
    [InlineData("tcp:localhost,1433", true)]
    [InlineData("(local)", true)]
    [InlineData(".", true)]
    [InlineData("::1", true)]
    [InlineData(@".\SQLEXPRESS", true)]
    [InlineData("dev-sql.corp,1433", false)]
    [InlineData("10.0.0.5", false)]
    public void Loopback_detection(string dataSource, bool loopback) => Assert.Equal(loopback, ServerConnections.IsLoopback(dataSource));

    [Fact]
    public void Non_loopback_sandbox_is_a_warning_and_nothing_prints_secrets()
    {
        var c = ServerConnections.Split(Remote, Remote);
        var w = Assert.Single(c.Warnings());
        Assert.Contains("루프백", w);
        Assert.DoesNotContain(Secret, w);
        Assert.DoesNotContain(Secret, c.ToString());
        Assert.DoesNotContain(Secret, c.Origin.ToString());
        Assert.DoesNotContain(Secret, c.Sandbox.ToString());
        Assert.Empty(ServerConnections.SingleServer(Remote).Warnings()); // SingleServer는 경고하지 않는다
    }

    // ---- 같은 인스턴스 ----

    [Fact]
    public void Same_instance_is_detected_by_name_machine_or_marker()
    {
        var origin = new InstanceIdentity("DEVSQL", "devsql-host");
        Assert.Null(InstanceGuard.SameInstanceReason(origin, new("a1b2c3", "a1b2c3"), "m", null));
        Assert.Contains("@@SERVERNAME", InstanceGuard.SameInstanceReason(origin, new("devsql", "x"), null, null));
        Assert.Contains("MachineName", InstanceGuard.SameInstanceReason(origin, new("other", "DEVSQL-HOST"), null, null));
        Assert.Contains("표식", InstanceGuard.SameInstanceReason(origin, new("other", "x"), "guid-1", "guid-1"));
        Assert.Null(InstanceGuard.SameInstanceReason(origin, new("other", "x"), "guid-1", "guid-2"));
    }

    // ---- 지문 v2 ----

    static Fingerprint Fp(string version = "17.0.1000.7") => new(version, "Developer", 170, false, 1, 0, 5496, "Korean_Wansung_CI_AS")
    {
        ServerCollation = "Korean_Wansung_CI_AS", HostPlatform = "Linux", MajorVersion = 17, Language = "us_english", DateFormat = "mdy", DateFirst = 7,
        HasUserClrAssemblies = false,
    };

    [Fact]
    public void Split_fingerprint_warns_on_version_edition_platform_but_matches()
    {
        var origin = Fp("16.0.4000.1") with { Edition = "Standard", HostPlatform = "Windows", MajorVersion = 16, SessionOptions = 0 };
        var c = FingerprintComparison.Compare(origin, Fp(), ServerMode.Split);
        Assert.True(c.Match);
        Assert.Empty(c.Mismatches);
        Assert.Equal(5, c.Warnings.Count);
        Assert.Contains(c.Warnings, w => w.StartsWith("ProductVersion", StringComparison.Ordinal));
        Assert.Contains(c.Warnings, w => w.StartsWith("HostPlatform", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("compat")]
    [InlineData("dbcoll")]
    [InlineData("srvcoll")]
    [InlineData("nested")]
    [InlineData("disallow")]
    [InlineData("recursive")]
    [InlineData("language")]
    [InlineData("dateformat")]
    [InlineData("datefirst")]
    public void Split_fingerprint_blocks_each_semantic_field(string field)
    {
        var s = Fp();
        var o = field switch
        {
            "compat" => s with { CompatibilityLevel = 150 },
            "dbcoll" => s with { Collation = "SQL_Latin1_General_CP1_CI_AS" },
            "srvcoll" => s with { ServerCollation = "SQL_Latin1_General_CP1_CI_AS" },
            "nested" => s with { NestedTriggers = 0 },
            "disallow" => s with { DisallowResultsFromTriggers = 1 },
            "recursive" => s with { IsRecursiveTriggersOn = true },
            "language" => s with { Language = "한국어" },
            "dateformat" => s with { DateFormat = "ymd" },
            _ => s with { DateFirst = 1 },
        };
        var c = FingerprintComparison.Compare(o, s, ServerMode.Split);
        Assert.False(c.Match);
        Assert.Single(c.Mismatches);
    }

    [Fact]
    public void Language_mismatch_suggests_current_language_and_clr_is_a_warning()
    {
        var c = FingerprintComparison.Compare(Fp() with { Language = "Korean", HasUserClrAssemblies = true }, Fp(), ServerMode.Split);
        Assert.Contains("Current Language=Korean", Assert.Single(c.Mismatches));
        Assert.Contains(c.Warnings, w => w.Contains("CLR", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_server_keeps_whole_record_equality()
    {
        Assert.True(FingerprintComparison.Compare(Fp(), Fp(), ServerMode.SingleServer).Match);
        var c = FingerprintComparison.Compare(Fp("16.0"), Fp(), ServerMode.SingleServer);
        Assert.False(c.Match);           // 기존 동작: 버전이 달라도 불일치
        Assert.Empty(c.Mismatches);      // 차단 항목은 같다(경고만)
    }

    [Fact]
    public void Fingerprint_sql_parses_and_reads_v2_fields()
    {
        var sql = Fingerprint.SqlFor("AW_Origin");
        Parse(sql);
        foreach (var part in new[] { "SERVERPROPERTY('Collation')", "on Linux", "ProductMajorVersion", "@@LANGUAGE", "date_format", "@@DATEFIRST", "[AW_Origin].sys.assemblies" })
            Assert.Contains(part, sql);
        Assert.Throws<ArgumentException>(() => Fingerprint.SqlFor("AW Origin"));
    }

    // ---- 이름 접미사 ----

    [Fact]
    public void Backup_file_and_origin_snapshot_names_carry_pc_and_run()
    {
        Assert.Equal("dbr_dev_pc_01_20261113010203.bak", RunNames.BackupFile("DEV-PC.01", new DateTimeOffset(2026, 11, 13, 10, 2, 3, TimeSpan.FromHours(9))));
        Assert.Equal("pc", RunNames.MachineTag("--"));
        Assert.Equal(32, RunNames.MachineTag(new string('a', 50)).Length);
        Assert.Equal("AW_Origin_pre_dev_pc_ab12cd34", RunNames.OriginSnapshot("AW_Origin", "_pre", "DEV-PC", "ab12cd34"));
        Assert.Throws<InvalidOperationException>(() => RunNames.OriginSnapshot(new string('a', 120), "_pre", "pc", "ab12cd34"));
    }

    [Theory]
    [InlineData("/var/opt/mssql/backup", "f.bak", "/var/opt/mssql/backup/f.bak")]
    [InlineData("/var/opt/mssql/backup/", "f.bak", "/var/opt/mssql/backup/f.bak")]
    [InlineData(@"D:\Backup", "f.bak", @"D:\Backup\f.bak")]
    [InlineData(@"\\srv\share\", "f.bak", @"\\srv\share\f.bak")]
    public void Server_paths_follow_the_server_separator(string dir, string file, string expected) =>
        Assert.Equal(expected, ServerPaths.Join(dir, file));

    [Fact]
    public void Server_path_directory_keeps_trailing_separator()
    {
        Assert.Equal(@"D:\Data\", ServerPaths.Directory(@"D:\Data\AW.mdf"));
        Assert.Equal("/var/opt/mssql/data/", ServerPaths.Directory("/var/opt/mssql/data/AW.mdf"));
    }

    // ---- HEADERONLY ----

    static BackupHeader Header(int type = 1, string? key = null, int major = 16, string? db = "AW_Origin") =>
        new(type, key, major, 160, "Korean_Wansung_CI_AS", db, new DateTime(2026, 11, 13));

    [Fact]
    public void Header_check_accepts_full_unencrypted_older_backup()
    {
        Assert.Equal(160, BackupHeaderCheck.Validate([Header()], 17, "AW_Origin").CompatibilityLevel);
        BackupHeaderCheck.Validate([Header(key: "NO_Encryption")], 17, "aw_origin");
    }

    [Theory]
    [InlineData(2, null, 16, "AW_Origin", "BackupType")]
    [InlineData(1, "aes_256", 16, "AW_Origin", "암호화")]
    [InlineData(1, null, 18, "AW_Origin", "주버전")]
    [InlineData(1, null, 16, "Other", "DB 이름")]
    public void Header_check_rejects(int type, string? key, int major, string db, string why)
    {
        var ex = Record.Exception(() => BackupHeaderCheck.Validate([Header(type, key, major, db)], 17, "AW_Origin"));
        Assert.NotNull(ex);
        Assert.Contains(why, ex.Message);
    }

    [Fact]
    public void Header_check_rejects_empty_or_multiple_sets()
    {
        Assert.Throws<InvalidOperationException>(() => BackupHeaderCheck.Validate([], 17, "AW_Origin"));
        Assert.Throws<NotSupportedException>(() => BackupHeaderCheck.Validate([Header(), Header()], 17, "AW_Origin"));
    }

    // ---- 복원 SQL ----

    [Fact]
    public void Split_sql_parses_and_restore_uses_detected_paths()
    {
        var layout = new RestoreLayout("/var/opt/mssql/data", "/var/opt/mssql/log/");
        var r = BaselineSql.Restore("AW_Sandbox", "/var/opt/mssql/import/f.bak", [new("AW_Data", 'D', 1, "PRIMARY"), new("AW_Log", 'L', 2)], layout);
        Assert.Contains("MOVE N'AW_Data' TO N'/var/opt/mssql/data/AW_Sandbox_AW_Data.mdf'", r);
        Assert.Contains("MOVE N'AW_Log' TO N'/var/opt/mssql/log/AW_Sandbox_AW_Log.ldf'", r);
        Parse(r);
        Parse(BaselineSql.SplitBackup("AW_Origin", "/b/f.bak"));
        Assert.Contains("WITH COPY_ONLY, COMPRESSION, CHECKSUM, INIT", BaselineSql.SplitBackup("AW_Origin", "/b/f.bak"));
        Parse(BaselineSql.HeaderOnly("/b/f.bak"));
        Parse(BaselineSql.SourcePrecheck("AW_Origin"));
        Parse(BaselineSql.SandboxPrecheck("AW_Sandbox", "AW_Sandbox_base"));
        Parse(BaselineSql.AfterRestoreLocal("AW_Sandbox", 1, 0));
        Parse(BaselineSql.EnsureInstanceMarker(Guid.Empty));
        Parse(BaselineSql.LocalServerInfo);
        Assert.Throws<ArgumentException>(() => BaselineSql.AfterRestoreLocal("AW_Sandbox", 2, 0));
        Assert.Equal("CREATE DATABASE [s] ON (NAME = [d], FILENAME = N'D:\\Data\\s_d.ss') AS SNAPSHOT OF [o];",
            BaselineSql.CreateSnapshot("s", "o", ["d"], n => BaselineSql.SnapshotPath("s", n, @"D:\Data\")));
    }

    [Theory]
    [InlineData("FD", "FILESTREAM 파일 그룹", "오프라인")]
    [InlineData("FX", "메모리 최적화 파일 그룹", "스냅샷을 만들 수 없어")]
    public void Filestream_and_memory_optimized_are_rejected_with_distinct_reasons(string fgType, string kind, string why)
    {
        var layout = new RestoreLayout("/d", "/l", new Dictionary<string, string> { ["FG"] = fgType });
        var ex = Assert.Throws<NotSupportedException>(() => BaselineSql.Restore("sb", "/b.bak", [new("AW_Data", 'D', 1, "PRIMARY"), new("C", 'S', 3, "FG")], layout));
        Assert.Contains(kind, ex.Message);
        Assert.Contains(why, ex.Message);
    }

    // ---- Split 기준선 순서(가짜 단계) ----

    sealed class FakeOrigin(List<string> log) : IOriginSteps
    {
        public InstanceIdentity Identity = new("DEVSQL", "devsql-host");
        public string? SeenMarker;
        public Fingerprint Fingerprint = Fp() with { NestedTriggers = 0 };
        public Task<InstanceIdentity> ReadIdentityAsync(CancellationToken ct) { log.Add("origin identity"); return Task.FromResult(Identity); }
        public Task<string?> ReadInstanceMarkerAsync(CancellationToken ct) { log.Add("origin marker"); return Task.FromResult(SeenMarker); }
        public Task PrecheckAsync(string source, CancellationToken ct) { log.Add("origin precheck " + source); return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string, string>> ReadSpecialFileGroupsAsync(string source, CancellationToken ct)
        { log.Add("origin filegroups"); return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()); }
        public Task<Fingerprint> ReadFingerprintAsync(string source, CancellationToken ct) { log.Add("origin fingerprint"); return Task.FromResult(Fingerprint); }
        public Task BackupAsync(string source, string serverPath, CancellationToken ct) { log.Add("origin backup " + serverPath); return Task.CompletedTask; }
    }

    sealed class FakeSandbox(List<string> log) : ISandboxSteps
    {
        public InstanceIdentity Identity = new("a1b2c3d4", "a1b2c3d4");
        public IReadOnlyList<BackupHeader> Headers = [Header(db: "AW_Origin")];
        public DbCatalog Catalog = new(
            [new(10, "dbo", "T", TemporalType.None, false)],
            [new(10, 1, "Id", "int", false, false, false, false, false, null)],
            [new(10, 1, "PK_T", true, true, 1, "Id", false)],
            [], [],
            [new(1, "AW_Data", "ROWS", "/d.mdf"), new(2, "AW_Log", "LOG", "/l.ldf")]);

        public Task ExecuteAsync(string database, string sql, CancellationToken ct) { log.Add($"sandbox {database}: {sql.TrimStart()}"); return Task.CompletedTask; }
        public Task<IReadOnlyList<BackupFile>> ReadFileListAsync(string sql, CancellationToken ct)
        { log.Add("sandbox filelist " + sql); return Task.FromResult<IReadOnlyList<BackupFile>>([new("AW_Data", 'D', 1, "PRIMARY"), new("AW_Log", 'L', 2)]); }
        public Task<DbCatalog> ReadCatalogAsync(string database, CancellationToken ct) => Task.FromResult(Catalog);
        public Task<IReadOnlyDictionary<string, long>> ReadRowCountsAsync(string database, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["dbo.T"] = 1 });
        public Task<bool> FingerprintMatchesAsync(string source, string sandbox, CancellationToken ct) => throw new InvalidOperationException("Split은 같은 연결로 두 지문을 읽지 않는다");
        public Task<LocalServerInfo> ReadServerInfoAsync(CancellationToken ct) { log.Add("sandbox info"); return Task.FromResult(new LocalServerInfo(Identity, 17, "/var/opt/mssql/data/", "/var/opt/mssql/data/")); }
        public Task<string> EnsureInstanceMarkerAsync(CancellationToken ct) { log.Add("sandbox marker"); return Task.FromResult("guid-local"); }
        public Task<IReadOnlyList<BackupHeader>> ReadHeadersAsync(string sql, CancellationToken ct) { log.Add("sandbox headeronly " + sql); return Task.FromResult(Headers); }
        public Task<Fingerprint> ReadFingerprintAsync(string database, CancellationToken ct) { log.Add("sandbox fingerprint " + database); return Task.FromResult(Fp() with { NestedTriggers = 0, ProductVersion = "17.0.9" }); }
    }

    sealed class FakeImporter(List<string> log) : IBackupImporter
    {
        public Task ImportAsync(string sourceFile, string fileName, CancellationToken ct) { log.Add($"import {sourceFile} -> {fileName}"); return Task.CompletedTask; }
        public void Delete(string fileName) => log.Add("delete " + fileName);
        public DateTimeOffset LastWriteTime(string sourceFile) => new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
    }

    static readonly DateTimeOffset Now = new(2026, 11, 13, 1, 2, 3, TimeSpan.Zero);

    static SplitBaselineOptions Options(string? importFile = null) => new()
    {
        Source = "AW_Origin", Sandbox = "AW_Sandbox", Snapshot = "AW_Sandbox_base",
        BackupServerDir = importFile is null ? @"D:\Backup" : null, SharePath = importFile is null ? @"\\devsql\backup" : null, ImportFile = importFile,
        ImportContainerDir = "/var/opt/mssql/import", MachineTag = "devpc",
    };

    static readonly string[] Destructive = ["RESTORE DATABASE", "DROP DATABASE", "SINGLE_USER", "CREATE DATABASE", "ALTER AUTHORIZATION", "sp_configure", "CREATE TRIGGER", "CREATE USER"];

    [Fact]
    public async Task Split_build_sends_destructive_steps_only_to_the_sandbox()
    {
        var log = new List<string>();
        var result = await BaselineBuilder.BuildSplitAsync(Options(), new FakeOrigin(log), new FakeSandbox(log), new FakeImporter(log), () => Now);

        Assert.All(log.Where(l => l.StartsWith("origin", StringComparison.Ordinal)), l => Assert.DoesNotContain(Destructive, d => l.Contains(d, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(@"origin backup D:\Backup\dbr_devpc_20261113010203.bak", log);
        Assert.Contains(@"import \\devsql\backup\dbr_devpc_20261113010203.bak -> dbr_devpc_20261113010203.bak", log);
        int I(string prefix) => log.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(I("origin identity") < I("sandbox marker") && I("sandbox marker") < I("origin marker") && I("origin marker") < I("origin precheck"));
        Assert.True(I("origin backup") < I("sandbox master: IF DB_ID(N'AW_Sandbox_base') IS NOT NULL"));
        Assert.True(I("sandbox headeronly RESTORE HEADERONLY FROM DISK = N'/var/opt/mssql/import/dbr_devpc_20261113010203.bak'") < I("sandbox filelist"));
        Assert.True(I("sandbox master: RESTORE DATABASE [AW_Sandbox]") < I("delete dbr_devpc_20261113010203.bak"));
        Assert.True(I("delete ") < I("sandbox master: DECLARE @sa sysname")); // 복원 뒤 정리(소유자·서버 옵션)
        Assert.Contains(log, l => l.Contains("sp_configure N'nested triggers', 0", StringComparison.Ordinal));
        Assert.StartsWith("sandbox master: CREATE DATABASE [AW_Sandbox_base] ON (NAME = [AW_Data], FILENAME = N'/var/opt/mssql/data/AW_Sandbox_base_AW_Data.ss')", log[^1]);
        Assert.True(result.FingerprintMatch);                                 // 버전만 다르다(경고)
        Assert.Contains(result.Comparison!.Warnings, w => w.StartsWith("ProductVersion", StringComparison.Ordinal));
        Assert.Equal("DEVSQL", result.OriginServer);
        Assert.Equal(Now, result.BackupCompletedAt);
    }

    [Fact]
    public async Task Split_build_with_import_file_does_not_back_up_on_origin()
    {
        var log = new List<string>();
        var result = await BaselineBuilder.BuildSplitAsync(Options(@"C:\given\aw.bak"), new FakeOrigin(log), new FakeSandbox(log), new FakeImporter(log), () => Now);
        Assert.DoesNotContain(log, l => l.StartsWith("origin backup", StringComparison.Ordinal));
        Assert.Contains(@"import C:\given\aw.bak -> dbr_devpc_20261113010203.bak", log);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), result.BackupCompletedAt);
    }

    [Fact]
    public async Task Same_server_name_refuses_before_writing_the_local_marker()
    {
        var log = new List<string>();
        var sandbox = new FakeSandbox(log) { Identity = new("devsql", "other") };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BaselineBuilder.BuildSplitAsync(Options(), new FakeOrigin(log), sandbox, new FakeImporter(log), () => Now));
        Assert.Contains("@@SERVERNAME", ex.Message);
        Assert.Equal(["origin identity", "sandbox info"], log);
    }

    [Fact]
    public async Task Marker_visible_from_origin_refuses_before_backup()
    {
        var log = new List<string>();
        var origin = new FakeOrigin(log) { SeenMarker = "guid-local" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BaselineBuilder.BuildSplitAsync(Options(), origin, new FakeSandbox(log), new FakeImporter(log), () => Now));
        Assert.DoesNotContain(log, l => l.StartsWith("origin precheck", StringComparison.Ordinal) || l.StartsWith("origin backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Encrypted_backup_is_rejected_before_sessions_are_evicted_and_import_is_deleted()
    {
        var log = new List<string>();
        var sandbox = new FakeSandbox(log) { Headers = [Header(key: "AES_256")] };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            BaselineBuilder.BuildSplitAsync(Options(), new FakeOrigin(log), sandbox, new FakeImporter(log), () => Now));
        Assert.DoesNotContain(log, l => l.Contains("SINGLE_USER", StringComparison.Ordinal) || l.Contains("RESTORE DATABASE", StringComparison.Ordinal));
        Assert.Contains("delete dbr_devpc_20261113010203.bak", log);
    }

    // ---- 샌드박스 안전 점검(2026-11-13) ----

    [Fact]
    public async Task Split_turns_trustworthy_off_on_the_sandbox_right_after_restore()
    {
        var log = new List<string>();
        await BaselineBuilder.BuildSplitAsync(Options(), new FakeOrigin(log), new FakeSandbox(log), new FakeImporter(log), () => Now);

        var i = log.FindIndex(l => l.Contains("TRUSTWORTHY", StringComparison.Ordinal));
        Assert.StartsWith("sandbox master: IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'AW_Sandbox' AND is_trustworthy_on = 1)", log[i]);
        Assert.Contains("ALTER DATABASE [AW_Sandbox] SET TRUSTWORTHY OFF;", log[i]);
        Assert.Single(log, l => l.Contains("TRUSTWORTHY", StringComparison.Ordinal));     // 원본 쪽에는 보내지 않는다
        Assert.True(log.FindIndex(l => l.StartsWith("sandbox master: RESTORE DATABASE", StringComparison.Ordinal)) < i);
        Assert.True(i < log.FindIndex(l => l.StartsWith("sandbox master: DECLARE @sa sysname", StringComparison.Ordinal))); // 소유자를 sa로 바꾸기 전
    }

    [Fact]
    public void Trustworthy_off_sql_validates_the_name()
    {
        Assert.Throws<ArgumentException>(() => BaselineSql.TrustworthyOff("AW]; DROP"));
    }

    [Fact]
    public async Task Split_ddl_trigger_guard_stops_before_any_install_ddl()
    {
        var log = new List<string>();
        DbCatalog? seen = null;
        var o = Options() with { DdlTriggerGuard = c => { seen = c; return "ddlShell: 외부 부작용(xp_:xp_cmdshell)"; } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BaselineBuilder.BuildSplitAsync(o, new FakeOrigin(log), new FakeSandbox(log), new FakeImporter(log), () => Now));

        Assert.Contains("기준선 구축을 멈춤: ddlShell", ex.Message);
        Assert.NotNull(seen);                                                             // 복원된 샌드박스 카탈로그로 판정
        Assert.DoesNotContain(log, l => l.Contains("CREATE USER", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("CREATE TRIGGER", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("CREATE DATABASE [AW_Sandbox_base]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Split_ddl_trigger_guard_null_continues()
    {
        var log = new List<string>();
        var o = Options() with { DdlTriggerGuard = _ => null };
        await BaselineBuilder.BuildSplitAsync(o, new FakeOrigin(log), new FakeSandbox(log), new FakeImporter(log), () => Now);
        Assert.Contains(log, l => l.Contains("CREATE USER", StringComparison.Ordinal));
    }

    [Fact]
    public void Instance_check_warns_for_risky_options_and_linked_servers()
    {
        var none = SandboxInstanceCheck.Warnings(new Dictionary<string, int>
        {
            ["xp_cmdshell"] = 0, ["Ole Automation Procedures"] = 0, ["Ad Hoc Distributed Queries"] = 0, ["clr enabled"] = 0, ["external scripts enabled"] = 0,
        }, []);
        Assert.Empty(none);

        var warnings = SandboxInstanceCheck.Warnings(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["xp_cmdshell"] = 1, ["Ole Automation Procedures"] = 1, ["Ad Hoc Distributed Queries"] = 1, ["clr enabled"] = 1, ["external scripts enabled"] = 1,
        }, ["DEVSQL", "LNK"]);
        Assert.Equal(6, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("'xp_cmdshell'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'clr enabled'", StringComparison.Ordinal));
        Assert.Contains("연결된 서버 2개(DEVSQL, LNK)", warnings[^1]);
    }

    [Fact]
    public void Instance_check_sql_reads_only()
    {
        Assert.Contains("FROM sys.configurations", SandboxInstanceCheck.Sql);
        Assert.Contains("FROM sys.servers WHERE server_id <> 0", SandboxInstanceCheck.Sql);
        foreach (var w in new[] { "sp_configure", "RECONFIGURE", "EXEC", "ALTER" })
            Assert.DoesNotContain(w, SandboxInstanceCheck.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Split_requires_a_backup_source()
    {
        var o = Options() with { BackupServerDir = null };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BaselineBuilder.BuildSplitAsync(o, new FakeOrigin([]), new FakeSandbox([]), new FakeImporter([]), () => Now));
    }

    [Fact]
    public async Task Importer_copies_into_import_dir_and_deletes_only_its_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbrt_import_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var src = Path.Combine(root, "src.bak");
            File.WriteAllText(src, "x");
            var importer = new FileBackupImporter(Path.Combine(root, "import"));
            await importer.ImportAsync(src, "dbr_pc_1.bak", default);
            Assert.True(File.Exists(Path.Combine(root, "import", "dbr_pc_1.bak")));
            Assert.Throws<ArgumentException>(() => importer.Delete(@"..\src.bak"));
            importer.Delete("dbr_pc_1.bak");
            Assert.False(File.Exists(Path.Combine(root, "import", "dbr_pc_1.bak")));
            Assert.True(File.Exists(src));
        }
        finally { Directory.Delete(root, true); }
    }
}
