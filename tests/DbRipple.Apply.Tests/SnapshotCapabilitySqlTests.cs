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
        await using var cleanup = new TestDatabases(origin);
        try
        {
            await TestDb.ExecAsync("master", $"CREATE DATABASE [{origin}];", $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;");
            await TestDb.ExecAsync("master", $"CREATE USER [{login}] FOR LOGIN [{login}];", $"GRANT CREATE ANY DATABASE TO [{login}];");
            await TestDb.ExecAsync(origin, $"CREATE USER [{login}] FOR LOGIN [{login}];", $"ALTER ROLE db_datareader ADD MEMBER [{login}];");

            var (create1, control1) = await ReadAsync(login, password, origin);
            Assert.True(create1);
            Assert.False(control1);                       // CREATE ANY DATABASE만으로는 스냅샷이 1817로 실패한다
            Assert.False(OriginSnapshot.Decide(create1, 0, 0, control1).CanSnapshot);

            await TestDb.ExecAsync(origin, $"GRANT CONTROL ON DATABASE::[{origin}] TO [{login}];");
            var (create2, control2) = await ReadAsync(login, password, origin);
            Assert.True(create2);
            Assert.True(control2);
            Assert.True(OriginSnapshot.Decide(create2, 0, 0, control2).CanSnapshot);
        }
        finally
        {
            try { await TestDb.ExecAsync("master", $"IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{login}') DROP USER [{login}];"); } catch { /* 정리 실패는 무시 */ }
            try { await TestDb.ExecAsync("master", $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{login}') DROP LOGIN [{login}];"); } catch { /* 정리 실패는 무시 */ }
        }
    }

    static async Task<(bool Create, bool Control)> ReadAsync(string login, string password, string origin)
    {
        var b = new SqlConnectionStringBuilder(TestDb.ConnectionString("master")) { UserID = login, Password = password, IntegratedSecurity = false };
        await using var conn = new SqlConnection(b.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(OriginSnapshot.CapabilitySql(origin), conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetInt32(0) == 1, r.GetInt32(1) == 1);
    }
}
