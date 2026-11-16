using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Apply.Tests;

/// <summary>검증 모드 사전 결정 SQL이 실제 스냅샷 조건(CREATE DATABASE AND 원본 DB의 CONTROL, 2026-11-16 실측)과 맞는지 실제 로그인으로 본다.</summary>
public sealed class SnapshotCapabilitySqlTests
{
    [SqlFact]
    public async Task Capability_requires_origin_control_in_addition_to_create_database()
    {
        var origin = TestDb.NewName("apl", "cap");
        var login = TestDb.NewName("apl", "lg");
        var password = "Pw!" + Guid.NewGuid().ToString("N") + "aA1";
        var snap = origin + "_snap";
        await using var cleanup = new TestDatabases(snap, origin);
        try
        {
            await TestDb.ExecAsync("master", $"CREATE DATABASE [{origin}];", $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;");
            await TestDb.ExecAsync("master", $"CREATE USER [{login}] FOR LOGIN [{login}];", $"GRANT CREATE ANY DATABASE TO [{login}];");
            await TestDb.ExecAsync(origin, $"CREATE USER [{login}] FOR LOGIN [{login}];", $"ALTER ROLE db_datareader ADD MEMBER [{login}];");

            var (create1, control1) = await ReadAsync(login, password, origin);
            Assert.True(create1);
            Assert.False(control1);                       // CREATE ANY DATABASE만으로는 스냅샷이 1817로 실패한다
            Assert.False(OriginSnapshot.Decide(create1, 0, 0, control1).CanSnapshot);
            var (denied, deniedReason) = await CreateSnapshotAsync(login, password, origin, snap);
            Assert.False((await new DbRipple.Preview.Settings.SqlServerProbe().ProbeOriginAsync(LoginConnection(login, password), origin, default)).CanCreateDatabase);   // 설정 연결 시험도 같은 규칙
            Assert.Equal(SnapshotOutcome.Failed, denied);                   // 실측 s09: CREATE ANY DATABASE만 → 1817
            Assert.Contains("1817", deniedReason);
            Assert.Contains("권한이 없음", deniedReason);

            await TestDb.ExecAsync(origin, $"GRANT CONTROL ON DATABASE::[{origin}] TO [{login}];");
            var (create2, control2) = await ReadAsync(login, password, origin);
            Assert.True(create2);
            Assert.True(control2);
            Assert.True(OriginSnapshot.Decide(create2, 0, 0, control2).CanSnapshot);
            var (created, createdReason) = await CreateSnapshotAsync(login, password, origin, snap);   // 실측 s13: db_owner·소유자가 아니어도 CONTROL이면 성공
            Assert.True((await new DbRipple.Preview.Settings.SqlServerProbe().ProbeOriginAsync(LoginConnection(login, password), origin, default)).CanCreateDatabase);
            Assert.True(created == SnapshotOutcome.Created, createdReason);
            await OriginSnapshot.DropAsync(TestDb.ConnectionString("master"), origin, snap, 60, default);

            // db_owner 역할 멤버(CONTROL 직접 부여 없음)도 성공한다(실측 s10).
            await TestDb.ExecAsync(origin, $"REVOKE CONTROL ON DATABASE::[{origin}] FROM [{login}];", $"ALTER ROLE db_owner ADD MEMBER [{login}];");
            var (create3, control3) = await ReadAsync(login, password, origin);
            Assert.True(create3 && control3);
            var (viaRole, viaRoleReason) = await CreateSnapshotAsync(login, password, origin, snap);
            Assert.True(viaRole == SnapshotOutcome.Created, viaRoleReason);
        }
        finally
        {
            // 정리 실패를 삼키지 않는다(CREATE ANY DATABASE 로그인이 서버에 남지 않게 드러낸다). 스냅샷·원본은 TestDatabases가 지운다.
            await OriginSnapshot.DropAsync(TestDb.ConnectionString("master"), origin, snap, 60, default);
            await TestDb.ExecAsync("master", $"IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{login}') DROP USER [{login}];");
            await TestDb.ExecAsync("master", $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{login}') DROP LOGIN [{login}];");
        }
    }

    static string LoginConnection(string login, string password) =>
        new SqlConnectionStringBuilder(TestDb.ConnectionString("master")) { UserID = login, Password = password, IntegratedSecurity = false }.ConnectionString;

    static Task<(SnapshotOutcome, string?)> CreateSnapshotAsync(string login, string password, string origin, string snap) =>
        OriginSnapshot.CreateAsync(LoginConnection(login, password), origin, snap, 60, default);

    static async Task<(bool Create, bool Control)> ReadAsync(string login, string password, string origin)
    {
        await using var conn = new SqlConnection(LoginConnection(login, password));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(OriginSnapshot.CapabilitySql(origin), conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetInt32(0) == 1, r.GetInt32(1) == 1);
    }
}
