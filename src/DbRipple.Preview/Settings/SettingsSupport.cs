using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using Microsoft.Data.SqlClient;

namespace DbRipple.Preview.Settings;

/// <summary>설정에서 만든 실행 연결 한 벌(Host가 서비스 묶음을 다시 만든다). ToString에 비밀값 없음.</summary>
public sealed record RuntimeProfile(ServerConnections Connections, string OriginDb, string SandboxDb, string SnapshotDb, BackupSettings Backup)
{
    /// <summary>비밀값 없는 표시.</summary>
    public override string ToString() => $"{Connections} ({OriginDb} → {SandboxDb})";
}

/// <summary>연결 교체 대상(Host의 서비스 묶음).</summary>
public interface IConnectionSwitch
{
    /// <summary>미리보기·적용·구축 중이면 PreviewConflictException(409).</summary>
    void EnsureIdle();
    /// <summary>새 연결로 서비스 묶음을 다시 만든다. 샌드박스는 기준선 재구축 필요로 표시한다.</summary>
    void Switch(RuntimeProfile profile);
}

/// <summary>비밀번호 암호화. Host 구현은 키 링을 DPAPI(CurrentUser)로 보호한 Data Protection(새 패키지 없음). 시험은 가짜를 넣는다.</summary>
public interface ISecretProtector
{
    /// <summary>암호화.</summary>
    byte[] Protect(byte[] data);
    /// <summary>복호화(다른 계정·PC면 CryptographicException).</summary>
    byte[] Unprotect(byte[] data);
}

/// <summary>연결 시험(읽기만). 시험은 가짜를 넣는다.</summary>
public interface ISqlServerProbe
{
    /// <summary>원본: 서버 이름·버전·에디션·sysadmin·BACKUP·CREATE DATABASE 권한.</summary>
    Task<OriginTestResult> ProbeOriginAsync(string connectionString, string originDb, CancellationToken ct);
    /// <summary>샌드박스: 서버 이름·버전.</summary>
    Task<SandboxTestResult> ProbeSandboxAsync(string connectionString, bool isLoopback, CancellationToken ct);
}

/// <summary>SQL Server 연결 시험. 오류는 번호와 짧은 설명만(예외 메시지·연결 문자열을 넣지 않는다).</summary>
public sealed class SqlServerProbe : ISqlServerProbe
{
    const string Sql = """
        SELECT CAST(@@SERVERNAME AS nvarchar(128)), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
               IS_SRVROLEMEMBER(N'sysadmin'),
               HAS_PERMS_BY_NAME(@db, N'DATABASE', N'BACKUP DATABASE'),
               CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE') = 1
                      OR HAS_PERMS_BY_NAME(N'master', N'DATABASE', N'CREATE DATABASE') = 1 THEN 1 ELSE 0 END,
               DB_ID(@db);
        """;

    /// <inheritdoc />
    public async Task<OriginTestResult> ProbeOriginAsync(string connectionString, string originDb, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(connectionString, ct);
            await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = 15 };
            cmd.Parameters.Add(new SqlParameter("@db", System.Data.SqlDbType.NVarChar, 128) { Value = originDb });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var dbExists = !r.IsDBNull(6);
            return new OriginTestResult
            {
                Ok = true,
                ServerName = Str(r, 0), ProductVersion = Str(r, 1), Edition = Str(r, 2),
                IsSysadmin = Bool(r, 3), CanBackup = Bool(r, 4) ?? false, CanCreateDatabase = Bool(r, 5),
                Error = dbExists ? null : $"원본 DB {originDb}이(가) 없거나 볼 수 없음",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new OriginTestResult { Ok = false, Error = Describe(ex) };
        }
    }

    /// <inheritdoc />
    public async Task<SandboxTestResult> ProbeSandboxAsync(string connectionString, bool isLoopback, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(connectionString, ct);
            await using var cmd = new SqlCommand("SELECT CAST(@@SERVERNAME AS nvarchar(128)), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));", conn) { CommandTimeout = 15 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return new SandboxTestResult { Ok = true, ServerName = Str(r, 0), ProductVersion = Str(r, 1), IsLoopback = isLoopback };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SandboxTestResult { Ok = false, IsLoopback = isLoopback, Error = Describe(ex) };
        }
    }

    static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false, ConnectTimeout = 10 }.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static bool? Bool(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i)) == 1;

    /// <summary>예외 → 짧은 설명(번호만, 메시지 원문 없음).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        SqlException { Number: 18456 } s => $"로그인 실패(오류 {s.Number})",
        SqlException { Number: 4060 } s => $"DB를 열 수 없음(오류 {s.Number})",
        SqlException { Number: 2 or 53 or 258 or -1 or -2 or 10060 or 10061 or 11001 } s => $"서버에 연결할 수 없음(오류 {s.Number})",
        SqlException { InnerException: System.Security.Authentication.AuthenticationException } s => $"TLS 인증서를 믿을 수 없음(오류 {s.Number})",
        SqlException s => $"연결 실패(오류 {s.Number})",
        _ => $"연결 실패({ex.GetType().Name})",
    };
}
