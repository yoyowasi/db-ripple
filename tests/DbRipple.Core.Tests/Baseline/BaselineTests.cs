using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Core.Tests.Baseline;

public class BaselineTests
{
    static readonly BackupFile[] Files = [new("AW_Data", 'D', 1), new("AW_Extra", 'D', 3), new("AW_Log", 'L', 2)];

    static void Parse(string sql)
    {
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Message)) + "\n" + sql);
    }

    [Fact]
    public void All_statements_parse()
    {
        Parse(BaselineSql.Precheck("AW_Origin", "AW_Sandbox", "AW_Sandbox_base"));
        Parse(BaselineSql.SourceGuard("AW_Origin"));
        Parse(BaselineSql.RestoreTargetGuard("AW_Origin"));
        Parse(BaselineSql.DropSnapshot("AW_Sandbox_base", "AW_Sandbox"));
        Parse(BaselineSql.RowCounts);
        Parse(BaselineSql.Backup("AW_Origin", "/var/opt/mssql/backup/AW_Origin.bak"));
        Parse(BaselineSql.FileListOnly("/var/opt/mssql/backup/AW_Origin.bak"));
        Parse(BaselineSql.PrepareRestoreTarget("AW_Sandbox"));
        Parse(BaselineSql.Restore("AW_Sandbox", "/var/opt/mssql/backup/AW_Origin.bak", Files));
        Parse(BaselineSql.AfterRestoreFailure("AW_Sandbox"));
        Parse(BaselineSql.CreateSnapshot("AW_Sandbox_base", "AW_Sandbox", ["AW_Data", "AW_Extra"]));
    }

    [Fact]
    public void Backup_is_copy_only_and_restore_moves_every_file()
    {
        Assert.Contains("WITH COPY_ONLY, INIT", BaselineSql.Backup("AW_Origin", "/b.bak"));
        var r = BaselineSql.Restore("AW_Sandbox", "/b.bak", Files);
        Assert.Contains("MOVE N'AW_Data' TO N'/var/opt/mssql/data/AW_Sandbox_AW_Data.mdf'", r);
        Assert.Contains("MOVE N'AW_Extra' TO N'/var/opt/mssql/data/AW_Sandbox_AW_Extra.ndf'", r);
        Assert.Contains("MOVE N'AW_Log' TO N'/var/opt/mssql/data/AW_Sandbox_AW_Log.ldf'", r);
        Assert.EndsWith(", REPLACE;", r);
    }

    [Fact]
    public void Snapshot_lists_data_files_as_sparse_files()
    {
        var s = BaselineSql.CreateSnapshot("AW_Sandbox_base", "AW_Sandbox", ["AW_Data"]);
        Assert.Equal("CREATE DATABASE [AW_Sandbox_base] ON (NAME = [AW_Data], FILENAME = N'/var/opt/mssql/data/AW_Sandbox_base_AW_Data.ss') AS SNAPSHOT OF [AW_Sandbox];", s);
    }

    [Fact]
    public void Names_and_paths_are_escaped()
    {
        var b = BaselineSql.Backup("we]ird", "/x/it's.bak");
        Assert.Contains("[we]]ird]", b);
        Assert.Contains("N'/x/it''s.bak'", b);
        Parse(b);
        var r = BaselineSql.Restore("sb", "/b.bak", [new("a/../b'c", 'D', 1)]);
        Assert.Contains("MOVE N'a/../b''c' TO N'/var/opt/mssql/data/sb_a____b_c.mdf'", r);
        Parse(r);
    }

    [Theory]
    [InlineData('S', "FILESTREAM")]
    [InlineData('F', "전체 텍스트")]
    public void Unsupported_backup_file_types_say_what_and_why(char type, string kind)
    {
        var ex = Assert.Throws<NotSupportedException>(() => BaselineSql.Restore("sb", "/b.bak", [new("AW_Data", 'D', 1), new("Odd", type, 3)]));
        Assert.Contains($"Odd의 형식 {type}({kind}", ex.Message);
        Assert.Contains("데이터(D)·로그(L) 파일만", ex.Message);
    }

    [Fact]
    public async Task Single_server_ddl_trigger_guard_stops_before_preview_user_ddl()
    {
        var steps = new FakeSteps();
        var o = new BaselineOptions("AW_Origin", "AW_Sandbox", "AW_Sandbox_base", "/b.bak") { DdlTriggerGuard = c => c.Triggers.Count > 0 ? "ddlDatabaseTriggerLog: 동적 SQL" : null };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BaselineBuilder.BuildAsync(o, steps, () => DateTimeOffset.UnixEpoch));

        Assert.Contains("기준선 구축을 멈춤", ex.Message);
        Assert.Equal("catalog: AW_Sandbox", steps.Log[^1]);                              // 카탈로그를 읽은 직후, 행 수·DDL 전
        Assert.DoesNotContain(steps.Log, l => l.Contains("CREATE USER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_runs_steps_in_design_order()
    {
        var steps = new FakeSteps();
        var times = new Queue<DateTimeOffset>([new(2026, 11, 6, 9, 58, 0, TimeSpan.Zero), new(2026, 11, 6, 9, 59, 0, TimeSpan.Zero)]);
        var result = await BaselineBuilder.BuildAsync(new("AW_Origin", "AW_Sandbox", "AW_Sandbox_base", "/b.bak"), steps, times.Dequeue);

        var log = steps.Log;
        // 0. 바꾸기 전 사전 검사(master)와 원본 스키마 검사(원본 문맥, 읽기만)
        Assert.StartsWith("master: IF DB_ID(N'AW_Origin') IS NULL", log[0]);
        Assert.StartsWith("AW_Origin: IF EXISTS (SELECT 1 FROM sys.schemas WHERE name COLLATE Latin1_General_CI_AS = N'audit')", log[1]);
        Assert.StartsWith("master: IF DB_ID(N'AW_Sandbox_base')", log[2]);
        Assert.Contains("source_database_id = DB_ID(N'AW_Sandbox')", log[2]);
        Assert.StartsWith("master: BACKUP DATABASE [AW_Origin]", log[3]);
        Assert.StartsWith("master: IF DB_ID(N'AW_Sandbox') IS NOT NULL", log[4]); // 복원 대상 가드
        Assert.Contains("THROW 50013", log[4]);
        Assert.StartsWith("filelist: RESTORE FILELISTONLY", log[5]);
        Assert.Contains("SET SINGLE_USER WITH ROLLBACK IMMEDIATE", log[6]);
        Assert.StartsWith("master: RESTORE DATABASE [AW_Sandbox]", log[7]);
        Assert.Equal("catalog: AW_Sandbox", log[8]);
        Assert.Equal("rowcounts: AW_Sandbox", log[9]);
        // 4b. 실행 사용자(Q12 수정): C9 설치 전에 샌드박스에서 만든다(DDL 트리거 부작용이 기준 스냅샷에 들어가게)
        Assert.StartsWith("AW_Sandbox: DECLARE @ddl bit = 0;", log[10]);
        Assert.Contains("CREATE USER [dbr_preview_user] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo]", log[10]);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [dbr_preview_user]", log[10]);
        Assert.Equal("rowcounts: AW_Sandbox", log[11]);
        Assert.StartsWith("AW_Sandbox: SET ANSI_NULLS ON;", log[12]);
        Assert.StartsWith("AW_Sandbox: CREATE SCHEMA audit", log[13]);
        var fp = log.IndexOf("fingerprint");
        Assert.Equal("rowcounts: AW_Sandbox", log[fp - 1]);
        Assert.StartsWith("AW_Sandbox: DELETE FROM audit.AuditRowImage;", log[fp - 2]); // 설치 마지막 = 감사 기록 비우기
        Assert.StartsWith("master: CREATE DATABASE [AW_Sandbox_base] ON (NAME = [AW_Data]", log[fp + 1]);
        Assert.DoesNotContain("AW_Log", log[fp + 1]);
        Assert.Equal(fp + 2, log.Count);
        Assert.DoesNotContain(log, l => l.Contains("MULTI_USER")); // 성공 경로에서는 복구 배치 없음
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 9, 58, 0, TimeSpan.Zero), result.BackupCompletedAt);
        var notes = result.Install.Manifest.Notes;
        Assert.Contains(notes, n => n.StartsWith("DDL 트리거 ddlDatabaseTriggerLog가 설치 DDL(CREATE_TABLE·ALTER_TRIGGER)"));
        Assert.Contains("DDL 트리거 ddlDatabaseTriggerLog가 실행 사용자 dbr_preview_user 준비 DDL(CREATE_USER·GRANT_DATABASE)에 발동함(C9 설치 전, Q12)", notes);
        Assert.Contains("실행 사용자 dbr_preview_user 준비 전후 행 수가 바뀐 테이블(기준선에 포함, 원본과 다름): dbo.DatabaseLog +2행", notes);
        Assert.Contains("설치 전후 행 수가 바뀐 테이블(기준선에 포함, 원본과 다름): dbo.DatabaseLog +431행", notes);
    }

    [Fact]
    public void Preview_user_batch_parses_is_idempotent_and_grants_dml_only_outside_audit()
    {
        var sql = PreviewUserSql.EnsureBatch(["Sales", "dbo", "audit", "sales", "we]ird"]);
        Parse(sql);
        Assert.Contains("IF USER_ID(N'dbr_preview_user') IS NULL BEGIN CREATE USER", sql); // 이미 있으면 DDL 없음
        Assert.Contains("ON SCHEMA::[Sales]", sql);
        Assert.Contains("ON SCHEMA::[we]]ird]", sql);
        Assert.DoesNotContain("SCHEMA::[audit]", sql); // 감사 저장소에는 권한 없음
        Assert.DoesNotContain("SCHEMA::[sales]", sql);  // 대소문자 무시 중복 제거
        Assert.Contains("THROW 50000", sql);           // 역할 소속이면 실패
        Assert.EndsWith("SELECT @ddl;\n", sql);
    }

    [Fact]
    public void Preview_user_notes_skip_when_no_ddl_trigger_fires_and_nothing_changed()
    {
        var cat = new FakeSteps().Catalog with { Triggers = [new(91, "ddlTableOnly", 0, "DATABASE", false, false, "CREATE_TABLE", false, false, "x")] };
        Assert.Empty(BaselineBuilder.PreviewUserNotes(cat, new Dictionary<string, long> { ["dbo.T"] = 5 }, new Dictionary<string, long> { ["dbo.T"] = 5 }));
    }

    // 서버 정렬 규칙은 끝 공백·전각·대소문자를 같은 이름으로 본다(DB_ID(N'AW_Origin ') = 원본) → 클라이언트에서 영문자·숫자·밑줄만 받는다
    [Theory]
    [InlineData("AW_Origin", "AW_Origin ", "AW_Sandbox_base")]   // 끝 공백 = 원본
    [InlineData("AW_Origin", "ＡＷ_Origin", "AW_Sandbox_base")]  // 전각 = 원본
    [InlineData("AW_Origin", "AW_Sandbox", "model ")]             // 끝 공백 = model
    [InlineData("AW_Origin", "AW_Sandbox", "AW_Sandbox_base\n")]  // 끝 줄바꿈($ 함정)
    [InlineData("AW_Origin", "AW-Sandbox", "AW_Sandbox_base")]
    [InlineData("AW_Origin", "[AW_Sandbox]", "AW_Sandbox_base")]
    [InlineData("AW_Origin", "", "AW_Sandbox_base")]
    [InlineData("AW_Origin", "AW_Origin", "AW_Sandbox_base")]
    [InlineData("AW_Origin", "AW_Sandbox", "aw_origin")]
    [InlineData("AW_Origin", "AW_Sandbox", "AW_SANDBOX")]
    [InlineData("AW_Origin", "master", "AW_Sandbox_base")]
    [InlineData("AW_Origin", "AW_Sandbox", "tempdb")]
    [InlineData("MODEL", "AW_Sandbox", "AW_Sandbox_base")]
    public async Task Build_refuses_names_that_could_reach_source_or_system_db(string source, string sandbox, string snapshot)
    {
        var steps = new FakeSteps();
        await Assert.ThrowsAsync<ArgumentException>(() => BaselineBuilder.BuildAsync(new(source, sandbox, snapshot, "/b.bak"), steps, () => DateTimeOffset.Now));
        Assert.Empty(steps.Log); // DB에 아무것도 보내지 않음
    }

    [Theory]
    [InlineData("AW_Origin ")]
    [InlineData("ＡＷ_Origin")]
    [InlineData("model;DROP")]
    public async Task Restore_refuses_bad_target_names_before_touching_server(string database)
    {
        var steps = new FakeSteps();
        await Assert.ThrowsAsync<ArgumentException>(() => BaselineBuilder.RestoreAsync(steps, database, "/b.bak"));
        Assert.Empty(steps.Log);
    }

    [Fact]
    public void Precheck_checks_source_sandbox_and_snapshot_on_server_before_any_change()
    {
        var sql = BaselineSql.Precheck("AW_Origin", "AW_Sandbox", "AW_Sandbox_base");
        Assert.Contains("THROW 50010", sql); // 원본 없음(오타) → 스냅샷부터 지우지 않음
        Assert.Contains("DB_NAME(DB_ID(N'AW_Origin')) COLLATE Latin1_General_BIN2 <> N'AW_Origin'", sql); // 별칭 거부(대소문자·전각)
        Assert.Contains("DATALENGTH(DB_NAME(DB_ID(N'AW_Origin'))) <> DATALENGTH(N'AW_Origin')", sql);    // 끝 공백은 BIN2도 무시
        Assert.Contains("DATALENGTH(DB_NAME(DB_ID(N'AW_Sandbox'))) <> DATALENGTH(N'AW_Sandbox')", sql);
        Assert.Contains("DATALENGTH(DB_NAME(DB_ID(N'AW_Sandbox_base'))) <> DATALENGTH(N'AW_Sandbox_base')", sql);
        Assert.Contains("state_desc = N'ONLINE'", sql);
        Assert.Contains("DB_ID(N'AW_Sandbox') = DB_ID(N'AW_Origin')", sql);
        Assert.Contains("DB_ID(N'AW_Sandbox') <= 4", sql);
        Assert.Contains("source_database_id = DB_ID(N'AW_Sandbox')", sql);
        Assert.Contains("THROW 50014", sql);
        Assert.DoesNotContain("DROP", sql);
        Assert.DoesNotContain("ALTER", sql);
    }

    [Fact]
    public void Drop_snapshot_only_drops_a_snapshot_of_the_sandbox()
    {
        var sql = BaselineSql.DropSnapshot("AW_Sandbox_base", "AW_Sandbox");
        var check = sql.IndexOf("source_database_id = DB_ID(N'AW_Sandbox')", StringComparison.Ordinal);
        var drop = sql.IndexOf("DROP DATABASE [AW_Sandbox_base]", StringComparison.Ordinal);
        Assert.True(check > 0 && drop > check, sql);
        Assert.Contains("THROW 50001", sql);
        Parse(BaselineSql.DropSnapshot("we]ird'snap", "sb'x"));
    }

    [Fact]
    public void Restore_target_is_dropped_only_when_not_online_and_reset_after_failure()
    {
        var prep = BaselineSql.PrepareRestoreTarget("AW_Sandbox");
        int online = prep.IndexOf("<> N'ONLINE'", StringComparison.Ordinal), drop = prep.IndexOf("DROP DATABASE [AW_Sandbox]", StringComparison.Ordinal),
            other = prep.IndexOf("ELSE", StringComparison.Ordinal), single = prep.IndexOf("ALTER DATABASE [AW_Sandbox] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", StringComparison.Ordinal);
        Assert.True(online > 0 && online < drop && drop < other && other < single, prep);
        var after = BaselineSql.AfterRestoreFailure("AW_Sandbox");
        Assert.Contains("user_access_desc = N'SINGLE_USER'", after);
        Assert.Contains("SET MULTI_USER", after);
    }

    [Fact]
    public async Task Failed_restore_returns_target_to_multi_user_and_rethrows_original_error()
    {
        var steps = new FakeSteps { FailOn = "RESTORE DATABASE" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BaselineBuilder.RestoreAsync(steps, "AW_Sandbox", "/b.bak"));
        Assert.Equal("RESTORE 실패(가짜)", ex.Message);
        Assert.Contains("SET MULTI_USER", steps.Log[^1]);
        Assert.StartsWith("master: RESTORE DATABASE [AW_Sandbox]", steps.Log[^2]);
    }

    [Fact]
    public async Task Unsupported_file_type_fails_before_sessions_are_evicted()
    {
        var steps = new FakeSteps { BackupFiles = [new("AW_Data", 'D', 1), new("Stream", 'S', 3)] };
        await Assert.ThrowsAsync<NotSupportedException>(() => BaselineBuilder.RestoreAsync(steps, "AW_Sandbox", "/b.bak"));
        Assert.DoesNotContain(steps.Log, l => l.Contains("SINGLE_USER") || l.Contains("RESTORE DATABASE"));
    }

    [Fact]
    public async Task Build_stops_before_install_when_sandbox_already_has_audit_schema_tables()
    {
        var steps = new FakeSteps();
        steps.Catalog = steps.Catalog with { Tables = [.. steps.Catalog.Tables, new(12, "Audit", "Log", TemporalType.None, false)] };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BaselineBuilder.BuildAsync(new("AW_Origin", "AW_Sandbox", "AW_Sandbox_base", "/b.bak"), steps, () => DateTimeOffset.Now));
        Assert.Contains("Audit.Log", ex.Message);
        Assert.DoesNotContain(steps.Log, l => l.Contains("CREATE SCHEMA audit"));
    }

    [Fact]
    public void Install_notes_skip_disabled_or_unrelated_ddl_triggers_and_unchanged_tables()
    {
        var cat = new FakeSteps().Catalog with
        {
            Triggers =
            [
                new(90, "ddlOff", 0, "DATABASE", false, true, "CREATE_TABLE", false, false, "x"),
                new(91, "ddlDropOnly", 0, "DATABASE", false, false, "DROP_TABLE", false, false, "x"),
            ],
        };
        var notes = BaselineBuilder.InstallNotes(cat, new Dictionary<string, long> { ["dbo.T"] = 5 }, new Dictionary<string, long> { ["dbo.T"] = 5, ["audit.AuditHeader"] = 0 }).ToList();
        Assert.Empty(notes);
    }

    sealed class FakeSteps : IBaselineSteps
    {
        public List<string> Log { get; } = [];
        public string? FailOn { get; init; }
        public IReadOnlyList<BackupFile> BackupFiles { get; init; } = [new("AW_Data", 'D', 1), new("AW_Log", 'L', 2)];
        int rowCountCalls;

        public DbCatalog Catalog { get; set; } = new(
            [new(10, "dbo", "T", TemporalType.None, false), new(11, "dbo", "DatabaseLog", TemporalType.None, false)],
            [new(10, 1, "Id", "int", false, false, false, false, false, null), new(11, 1, "DatabaseLogID", "int", false, false, false, true, false, null)],
            [new(10, 1, "PK_T", true, true, 1, "Id", false), new(11, 2, "PK_DatabaseLog", true, true, 1, "DatabaseLogID", false)],
            [],
            [
                new(90, "ddlDatabaseTriggerLog", 0, "DATABASE", false, false, "CREATE_TABLE", false, false, "CREATE TRIGGER ..."),
                new(90, "ddlDatabaseTriggerLog", 0, "DATABASE", false, false, "ALTER_TRIGGER", false, false, "CREATE TRIGGER ..."),
                new(90, "ddlDatabaseTriggerLog", 0, "DATABASE", false, false, "DROP_TABLE", false, false, "CREATE TRIGGER ..."),
                new(90, "ddlDatabaseTriggerLog", 0, "DATABASE", false, false, "CREATE_USER", false, false, "CREATE TRIGGER ..."),
                new(90, "ddlDatabaseTriggerLog", 0, "DATABASE", false, false, "GRANT_DATABASE", false, false, "CREATE TRIGGER ..."),
            ],
            [new(1, "AW_Data", "ROWS", "/d.mdf"), new(2, "AW_Log", "LOG", "/l.ldf")]);

        public Task ExecuteAsync(string database, string sql, CancellationToken ct)
        {
            Log.Add($"{database}: {sql}");
            return FailOn is not null && sql.StartsWith(FailOn, StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("RESTORE 실패(가짜)"))
                : Task.CompletedTask;
        }

        public Task<IReadOnlyList<BackupFile>> ReadFileListAsync(string sql, CancellationToken ct)
        { Log.Add("filelist: " + sql); return Task.FromResult(BackupFiles); }

        public Task<DbCatalog> ReadCatalogAsync(string database, CancellationToken ct)
        {
            Log.Add("catalog: " + database);
            return Task.FromResult(Catalog);
        }

        // 1차(사용자 준비 전) DatabaseLog 1597행, 2차(사용자 준비 뒤) 1599행, 3차(설치 뒤) 2030행 = DDL 트리거가 2행·431행 기록
        public Task<IReadOnlyDictionary<string, long>> ReadRowCountsAsync(string database, CancellationToken ct)
        {
            Log.Add("rowcounts: " + database);
            IReadOnlyDictionary<string, long> counts = rowCountCalls++ switch
            {
                0 => new Dictionary<string, long> { ["dbo.T"] = 3, ["dbo.DatabaseLog"] = 1597 },
                1 => new Dictionary<string, long> { ["dbo.T"] = 3, ["dbo.DatabaseLog"] = 1599 },
                _ => new Dictionary<string, long> { ["dbo.T"] = 3, ["dbo.DatabaseLog"] = 2030, ["audit.AuditHeader"] = 0 },
            };
            return Task.FromResult(counts);
        }

        public Task<bool> FingerprintMatchesAsync(string source, string sandbox, CancellationToken ct) { Log.Add("fingerprint"); return Task.FromResult(true); }
    }
}
