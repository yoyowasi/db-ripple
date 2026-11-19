using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using DbRipple.Contracts;
using Npgsql;

namespace DbRipple.Postgres;

/// <summary>
/// 미리보기 역할 로그인(설계 결정 3, 2026-11-19). 사용자 문장과 사용자 테이블 읽기는 미리보기 역할로 직접 로그인한 연결에서 돈다.
/// session user가 미리보기 역할이므로 SET ROLE NONE·RESET ROLE(set_config('role', 'none', …))은 그 역할로 돌아갈 뿐이고,
/// 역할이 다른 역할의 구성원이 아니어서 SET ROLE로 연결 계정이 될 수도 없다(서버가 막는다).
/// 비밀번호는 어디에도 두지 않는다: 연결마다 관리 연결에서 (1) 역할이 안전한지 보고 (2) 무작위 비밀번호의 SCRAM-SHA-256 검증값을 넣고
/// (3) 미리보기 연결을 연 뒤 (4) 곧바로 PASSWORD NULL로 지운다. 평문은 이 프로세스 메모리에만 잠깐 있고 서버·로그·응답에 나가지 않는다
/// (서버 로그에 ALTER ROLE이 남아도 검증값으로는 로그인할 수 없다).
/// </summary>
public static class PgPreviewLogin
{
    const int ScramIterations = 4096;
    // 같은 서버에서 같은 역할 이름을 쓰는 다른 db-ripple 프로세스가 사이에 비밀번호를 바꾸면 28P01이 난다. 그때만 다시 해 본다.
    const int OpenAttempts = 3;

    /// <summary>
    /// 사용자 문장용 샌드박스 연결(풀링 끔). 미리보기 역할이 없으면(테스트 전용 설정) 연결 계정으로 연다.
    /// 연결 실패는 SandboxUnavailableException, 역할이 경계가 되지 않으면 InvalidOperationException(메시지에 비밀값 없음).
    /// </summary>
    public static async Task<NpgsqlConnection> OpenAsync(PgOptions options, CancellationToken ct)
    {
        if (options.PreviewRole is not { } role)
            return await OpenOrUnavailableAsync(PgPreviewExecutor.SandboxConnectionString(options, options.SandboxDb), "샌드박스에 연결하지 못했습니다.", ct);

        await using var admin = await OpenOrUnavailableAsync(options.AdminConnectionString(), "비밀번호를 정할 관리 연결을 열지 못했습니다.", ct);
        await RequireSafeAsync(admin, null, role, ct);
        for (var attempt = 1; ; attempt++)
        {
            var password = NewPassword();
            NpgsqlConnection? conn = null;
            Exception? failure = null;
            var passwordSet = false;
            try
            {
                await SetPasswordAsync(admin, role, ScramVerifier(password, RandomNumberGenerator.GetBytes(16), ScramIterations), ct);
                passwordSet = true;
                conn = new NpgsqlConnection(LoginConnectionString(options, role, password));
                await conn.OpenAsync(ct);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // 로그인 성공·실패와 관계없이 비밀번호를 바로 지운다. 이미 열린 연결은 인증이 끝났으므로 그대로 쓴다.
            Exception? clearFailure = null;
            if (passwordSet)
            {
                try { await SetPasswordAsync(admin, role, null, CancellationToken.None); }
                catch (Exception ex) { clearFailure = ex; }
            }

            if (failure is null && clearFailure is null) return conn!;
            if (conn is not null) await conn.DisposeAsync();
            if (failure is null)
                throw new SandboxUnavailableException("미리보기 역할의 임시 비밀번호를 지우지 못했습니다.", clearFailure);
            if (failure is OperationCanceledException) ExceptionDispatchInfo.Throw(failure);
            if (!passwordSet)
                throw failure is PostgresException setError
                    ? new InvalidOperationException($"미리보기 역할 '{role}'의 임시 비밀번호를 정하지 못했습니다([{setError.SqlState}]). 연결 계정이 그 역할을 만든 계정(ADMIN 권한)이어야 합니다.", setError)
                    : new SandboxUnavailableException("미리보기 역할의 임시 비밀번호를 정하는 중 관리 연결이 끊겼습니다.", failure);
            if (failure is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword } && attempt < OpenAttempts) continue;
            throw failure switch
            {
                PostgresException { SqlState: PostgresErrorCodes.InvalidAuthorizationSpecification or PostgresErrorCodes.InvalidPassword } pg =>
                    new SandboxUnavailableException(
                        $"미리보기 역할 '{role}'로 샌드박스에 로그인하지 못했습니다([{pg.SqlState}]). 서버 pg_hba.conf가 이 역할의 비밀번호(SCRAM) 로그인을 허용해야 합니다.", pg),
                NpgsqlException or SocketException => new SandboxUnavailableException("미리보기 역할로 샌드박스에 연결하지 못했습니다.", failure),
                _ => new InvalidOperationException("미리보기 역할 로그인 실패", failure),
            };
        }
    }

    /// <summary>
    /// 역할이 경계가 되는지 서버 카탈로그로 확인한다(구축 때와 미리보기 연결마다). 역할은 클러스터 전역이라 구축 뒤 누가 바꿀 수 있으므로 연결마다 다시 본다.
    /// 공유 카탈로그(pg_roles·pg_auth_members·pg_shdepend)만 읽으므로 어느 DB에 붙은 연결이든 된다.
    /// </summary>
    internal static async Task RequireSafeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(RoleFactsSql, conn, tx) { Parameters = { new() { Value = role } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        RoleFacts? facts = null;
        if (await r.ReadAsync(ct))
            facts = new RoleFacts(r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt64(6), r.GetInt64(7));
        var problems = Problems(facts);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"미리보기 역할 '{role}'은(는) 사용자 문장의 권한 경계가 되지 않습니다: {string.Join(", ", problems)}. 새로 고침으로 다시 만들거나, 다른 역할 이름(Pg:PreviewRole)을 쓰세요.");
    }

    /// <summary>
    /// 역할 사실. 구성원 수는 이 역할이 다른 역할(미리 정의된 pg_* 역할 포함)의 구성원인 행 수, 소유 수는 어느 DB에서든 이 역할이 소유한 객체 수다
    /// (소유자는 그 테이블의 트리거를 끄거나 바꿀 수 있다).
    /// </summary>
    internal const string RoleFactsSql = """
        SELECT r.rolcanlogin, r.rolsuper, r.rolcreatedb, r.rolcreaterole, r.rolreplication, r.rolbypassrls,
               (SELECT count(*) FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid),
               (SELECT count(*) FROM pg_catalog.pg_shdepend d
                 WHERE d.refclassid = 'pg_catalog.pg_authid'::pg_catalog.regclass AND d.refobjid = r.oid AND d.deptype = 'o')
        FROM pg_catalog.pg_roles r WHERE r.rolname = $1
        """;

    internal sealed record RoleFacts(bool CanLogin, bool Super, bool CreateDb, bool CreateRole, bool Replication, bool BypassRls, long MemberOf, long Owns);

    /// <summary>역할이 경계가 되지 않는 이유(순수). 없으면 빈 목록.</summary>
    internal static IReadOnlyList<string> Problems(RoleFacts? f)
    {
        if (f is null) return ["역할이 없음"];
        var list = new List<string>();
        if (!f.CanLogin) list.Add("LOGIN이 아님(2026-11-19 이전 구조)");
        if (f.Super) list.Add("SUPERUSER");
        if (f.CreateDb) list.Add("CREATEDB");
        if (f.CreateRole) list.Add("CREATEROLE");
        if (f.Replication) list.Add("REPLICATION");
        if (f.BypassRls) list.Add("BYPASSRLS");
        if (f.MemberOf > 0) list.Add($"다른 역할의 구성원({f.MemberOf})");
        if (f.Owns > 0) list.Add($"객체 소유({f.Owns})");
        return list;
    }

    /// <summary>미리보기 역할 로그인 연결 문자열: 서버·TLS 설정은 연결 계정 것을 따르고 사용자·비밀번호·DB만 바꾼다(비밀번호 파일은 쓰지 않음).</summary>
    internal static string LoginConnectionString(PgOptions options, string role, string password) =>
        new NpgsqlConnectionStringBuilder(options.ServerConnectionString)
        {
            Database = options.SandboxDb, Pooling = false, Username = role, Password = password, Passfile = null,
        }.ConnectionString;

    /// <summary>
    /// SCRAM-SHA-256 검증값(RFC 5802·7677, PostgreSQL pg_authid 형식). 서버에는 이것만 보내므로 평문 비밀번호가 서버 로그에 남지 않는다.
    /// 비밀번호는 16진수(ASCII)라 SASLprep가 그대로다.
    /// </summary>
    internal static string ScramVerifier(string password, byte[] salt, int iterations)
    {
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var storedKey = SHA256.HashData(HMACSHA256.HashData(salted, "Client Key"u8));
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }

    static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // 유틸리티 문장은 매개변수를 받지 않아 리터럴로 쓴다. 검증값은 base64·숫자·$·:·-뿐이다.
    static async Task SetPasswordAsync(NpgsqlConnection admin, string role, string? verifier, CancellationToken ct)
    {
        var sql = $"ALTER ROLE {PgNames.Quote(role)} PASSWORD {(verifier is null ? "NULL" : PgNames.Literal(verifier))}";
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<NpgsqlConnection> OpenOrUnavailableAsync(string connectionString, string message, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            await conn.DisposeAsync();
            throw new SandboxUnavailableException(message, ex);
        }
    }
}
