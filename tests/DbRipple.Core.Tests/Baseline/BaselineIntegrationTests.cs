using DbRipple.Core.Baseline;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Tests.Baseline;

/// <summary>
/// §5.1 기준선 구축을 실제 SQL Server의 임시 DB(dbr_it_*)로 돌린다. 원본에는 AW의 ddlDatabaseTriggerLog를 흉내 낸 DDL 트리거를 둔다.
/// 위험 경로(원본·다른 DB 덮어쓰기)는 임시 DB로만 확인하고, 백업 파일은 실행마다 고유 이름으로 만들고 끝에 지운다.
/// </summary>
public class BaselineIntegrationTests
{
    static readonly string[] SourceSetup =
    [
        "CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, v int NULL);",
        "INSERT INTO dbo.T (Id, v) VALUES (1, 10), (2, 20);",
        "CREATE TABLE dbo.DdlLog (Id int IDENTITY(1, 1) NOT NULL PRIMARY KEY, EventType sysname NULL);",
        """
        CREATE TRIGGER ddlLog ON DATABASE FOR DDL_DATABASE_LEVEL_EVENTS AS
        BEGIN
            SET NOCOUNT ON;
            INSERT INTO dbo.DdlLog (EventType) VALUES (EVENTDATA().value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'));
        END;
        """,
    ];

    [SqlFact]
    public async Task Builds_rebuilds_and_recovers_baseline_on_real_server()
    {
        string src = TestSql.NewName("src"), sb = TestSql.NewName("sb"), ss = TestSql.NewName("ss"), other = TestSql.NewName("other");
        await using var cleanup = new TempDatabases(ss, sb, src, other);
        var backup = TestSql.BackupPath(src);
        cleanup.ContainerFiles.Add(backup);
        await TestSql.ExecAsync("master", $"CREATE DATABASE [{src}];", $"CREATE DATABASE [{other}];");
        await TestSql.ExecAsync(src, SourceSetup);
        var o = new BaselineOptions(src, sb, ss, backup);

        await using (var steps = new SqlBaselineSteps(TestSql.ConnectionString()))
        {
            var first = await BaselineBuilder.BuildAsync(o, steps, () => DateTimeOffset.Now);
            Assert.True(first.FingerprintMatch);
            Assert.Equal(steps.SourceFingerprint, steps.SandboxFingerprint);
            await AssertBaselineAsync(first);
        }

        // 다시 만들기: 스냅샷이 있고 샌드박스에 열린 세션이 있어도 지우고 끊고 복원한다
        await using (var held = await TestSql.OpenAsync(sb))
        await using (var steps = new SqlBaselineSteps(TestSql.ConnectionString()))
            await AssertBaselineAsync(await BaselineBuilder.BuildAsync(o, steps, () => DateTimeOffset.Now));

        // 원본은 BACKUP만 했으므로 그대로다(감사 저장소 없음, DDL 로그 0행)
        await using (var c = await TestSql.OpenAsync(src))
        {
            Assert.Equal(0, await TestSql.ScalarAsync<int>(c, "SELECT COUNT(*) FROM sys.schemas WHERE name = N'audit';"));
            Assert.Equal(0, await TestSql.ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.DdlLog;"));
        }

        // 사전 검사: 아무것도 바꾸기 전에 멈춘다
        //  - 스냅샷 이름이 샌드박스의 스냅샷이 아닌 일반 DB → 50014, 그 DB는 그대로
        Assert.Equal(50014, (await BuildFailsAsync(o with { Snapshot = other })).Number);
        //  - 샌드박스 이름이 대소문자만 다른 다른 DB(서버가 같은 DB로 해석, 별칭) → 50013, 그 DB를 덮어쓰지 않음
        Assert.Equal(50013, (await BuildFailsAsync(o with { Sandbox = other.ToUpperInvariant() })).Number);
        //  - 원본 오타 → 50010, 기존 기준 스냅샷부터 지우지 않음(#8)
        Assert.Equal(50010, (await BuildFailsAsync(o with { Source = TestSql.NewName("nosuch") })).Number);
        // 서버 판정만(클라이언트 이름 규칙을 거치지 않은 이름): 끝 공백·대소문자·전각 별칭을 서버 가드가 거부한다.
        // 끝 공백은 BIN2 비교에서도 무시되므로 DATALENGTH 비교가 잡는다
        foreach (var alias in new[] { other + " ", other.ToUpperInvariant(), FullWidthLetters(other) })
        {
            Assert.Equal(50013, (await Assert.ThrowsAsync<SqlException>(() => TestSql.ExecAsync("master", BaselineSql.RestoreTargetGuard(alias, src)))).Number);
            Assert.Equal(50011, (await Assert.ThrowsAsync<SqlException>(() => TestSql.ExecAsync("master", BaselineSql.Precheck(alias, sb, ss)))).Number);
        }
        await using (var m = await TestSql.OpenAsync("master"))
        {
            Assert.Equal(sb, await TestSql.ScalarAsync<string>(m, $"SELECT DB_NAME(source_database_id) FROM sys.databases WHERE name = N'{ss}';"));
            Assert.Equal("ONLINE/MULTI_USER", await TestSql.ScalarAsync<string>(m, $"SELECT state_desc + N'/' + user_access_desc FROM sys.databases WHERE name = N'{other}';"));
        }

        // RESTORE 실패(대상에 스냅샷이 있어 5094) → SINGLE_USER로 남지 않고 MULTI_USER로 돌아오며 원래 오류가 그대로 나온다
        var restoreError = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var steps = new SqlBaselineSteps(TestSql.ConnectionString());
            await BaselineBuilder.RestoreAsync(steps, sb, backup);
        });
        Assert.Contains(restoreError.Errors.Cast<SqlError>(), e => e.Number == 5094);
        await using (var m = await TestSql.OpenAsync("master"))
            Assert.Equal("ONLINE/MULTI_USER", await TestSql.ScalarAsync<string>(m, $"SELECT state_desc + N'/' + user_access_desc FROM sys.databases WHERE name = N'{sb}';"));

        // RESTORING으로 남은 샌드박스(복원 중단)는 ALTER가 5052로 실패하므로 지우고 새로 복원한다
        await TestSql.DropAsync(ss);
        await using (var steps = new SqlBaselineSteps(TestSql.ConnectionString()))
        {
            var files = await steps.ReadFileListAsync(BaselineSql.FileListOnly(backup), default);
            await steps.ExecuteAsync("master", BaselineSql.PrepareRestoreTarget(sb), default);
            await steps.ExecuteAsync("master", BaselineSql.Restore(sb, backup, files).Replace(", REPLACE;", ", REPLACE, NORECOVERY;"), default);
        }
        await using (var m = await TestSql.OpenAsync("master"))
            Assert.Equal("RESTORING", await TestSql.ScalarAsync<string>(m, $"SELECT state_desc FROM sys.databases WHERE name = N'{sb}';"));
        await using (var steps = new SqlBaselineSteps(TestSql.ConnectionString()))
            await AssertBaselineAsync(await BaselineBuilder.BuildAsync(o, steps, () => DateTimeOffset.Now));

        async Task AssertBaselineAsync(BaselineResult r)
        {
            var notes = r.Install.Manifest.Notes;
            Assert.Contains("DDL 트리거 ddlLog가 설치 DDL(CREATE_SCHEMA·CREATE_SEQUENCE·CREATE_TABLE·CREATE_TRIGGER·ALTER_TRIGGER)에 발동함(ALTER_TRIGGER는 sp_settriggerorder)", notes);
            // 스키마 1 + SEQUENCE 1 + 감사 테이블 3 + 트리거 2×3 + sp_settriggerorder 2×3 = 17
            Assert.Contains("설치 전후 행 수가 바뀐 테이블(기준선에 포함, 원본과 다름): dbo.DdlLog +17행", notes);
            Assert.Equal(2, r.Install.Manifest.Tables.Count);

            await using var c = await TestSql.OpenAsync(sb);
            Assert.Equal(0L, await TestSql.ScalarAsync<long>(c, "SELECT COUNT_BIG(*) FROM audit.AuditHeader;"));   // 설치 중 기록은 비움
            Assert.Equal(0L, await TestSql.ScalarAsync<long>(c, "SELECT COUNT_BIG(*) FROM audit.AuditRowImage;"));
            Assert.True(await TestSql.ScalarAsync<long>(c, "SELECT CAST(current_value AS bigint) FROM sys.sequences WHERE name = N'CaptureSeq';") > 1); // 순번은 재설정 안 함
            Assert.Equal(6, await TestSql.ScalarAsync<int>(c, """
                SELECT COUNT(*) FROM sys.triggers AS tr JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
                WHERE tr.name LIKE N'dbr[_]audit[_]%' AND te.is_last = 1 AND te.is_first = 0;
                """));
            Assert.Equal(17, await TestSql.ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.DdlLog;"));

            await using var m = await TestSql.OpenAsync("master");
            Assert.Equal(sb, await TestSql.ScalarAsync<string>(m, $"SELECT DB_NAME(source_database_id) FROM sys.databases WHERE name = N'{ss}';"));
            Assert.Equal("ONLINE/MULTI_USER", await TestSql.ScalarAsync<string>(m, $"SELECT state_desc + N'/' + user_access_desc FROM sys.databases WHERE name = N'{sb}';"));
            await using var s = await TestSql.OpenAsync(ss);
            Assert.Equal(2, await TestSql.ScalarAsync<int>(s, "SELECT COUNT(*) FROM dbo.T;"));
            Assert.Equal(17, await TestSql.ScalarAsync<int>(s, "SELECT COUNT(*) FROM dbo.DdlLog;"));
            Assert.Equal(0L, await TestSql.ScalarAsync<long>(s, "SELECT COUNT_BIG(*) FROM audit.AuditHeader;"));
        }
    }

    // 원본에 audit 스키마가 있으면(빈 스키마라도) 감사 저장소와 겹치므로 아무것도 바꾸기 전에 멈춘다
    [SqlFact]
    public async Task Refuses_source_with_audit_schema_before_any_change()
    {
        string src = TestSql.NewName("src"), sb = TestSql.NewName("sb"), ss = TestSql.NewName("ss");
        await using var cleanup = new TempDatabases(ss, sb, src);
        var backup = TestSql.BackupPath(src);
        cleanup.ContainerFiles.Add(backup);
        await TestSql.ExecAsync("master", $"CREATE DATABASE [{src}];");
        await TestSql.ExecAsync(src, "CREATE SCHEMA Audit;", "CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY);");

        Assert.Equal(50030, (await BuildFailsAsync(new BaselineOptions(src, sb, ss, backup))).Number);
        await using var m = await TestSql.OpenAsync("master");
        Assert.Equal(0, await TestSql.ScalarAsync<int>(m, $"SELECT COUNT(*) FROM sys.databases WHERE name IN (N'{sb}', N'{ss}');"));
    }

    // 영문 소문자를 전각으로(서버 정렬 규칙은 폭을 구분하지 않아 같은 DB로 해석: DB_ID(N'ＡＷ_Origin') = 원본)
    static string FullWidthLetters(string s) => string.Concat(s.Select(ch => ch is >= 'a' and <= 'z' ? (char)(ch + 0xFEE0) : ch));

    static async Task<SqlException> BuildFailsAsync(BaselineOptions o) =>
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var steps = new SqlBaselineSteps(TestSql.ConnectionString());
            await BaselineBuilder.BuildAsync(o, steps, () => DateTimeOffset.Now);
        });
}
