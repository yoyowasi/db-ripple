using System.Text.RegularExpressions;
using DbRipple.Core.Catalog;
using DbRipple.Core.Sql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbRipple.TestSupport;

/// <summary>
/// 2주차 병렬 통합 테스트의 DB 격리 규칙.
/// <list type="bullet">
/// <item>테스트가 만드는 DB 이름은 "dbrt_{컴포넌트}_{태그}_{8자리 16진}"이다. 컴포넌트 코드는 <see cref="Components"/>뿐이다.</item>
/// <item>자기 컴포넌트 접두의 DB만 만들고 지운다. 공유 DB(AW_Origin, AW_Sandbox, AW_Sandbox_base, CascadeLab*, AW_Spike*)에는 쓰지 않는다.</item>
/// <item>컨테이너 백업 파일도 같은 이름 규칙(/var/opt/mssql/backup/dbrt_…)을 쓴다.</item>
/// <item>서버 수준 설정(sp_configure 등)은 바꾸지 않는다. DB 수준 설정(RECURSIVE_TRIGGERS)은 자기 DB에서만.</item>
/// </list>
/// 연결 문자열은 환경 변수 DBRIPPLE_SQL_CONNECTION(scripts/env.ps1이 프로세스 안에서 만든다)에서만 읽고 출력하지 않는다.
/// </summary>
public static class TestDb
{
    /// <summary>연결 문자열 환경 변수(Core CatalogReader와 같음).</summary>
    public const string ConnectionEnvVar = CatalogReader.ConnectionEnvVar;

    /// <summary>모든 2주차 테스트 DB의 공통 접두. 1주차 Core.Tests는 dbr_it_를 쓴다(겹치지 않음).</summary>
    public const string RootPrefix = "dbrt_";

    /// <summary>
    /// 컴포넌트 코드: ana(C6 Analysis), exe(C10·C11 Execution), dif(C12 Diff), att(C13 Attribution), api(C5·C14·Preview), eval(C16 Eval), sup(TestSupport 자체 검사), int(통합: 진짜 구현 파이프라인).
    /// </summary>
    public static readonly IReadOnlyList<string> Components = ["ana", "exe", "dif", "att", "api", "eval", "sup", "int", "apl"];

    /// <summary>컨테이너 백업 폴더(docs/dev-setup.md).</summary>
    public const string BackupDir = "/var/opt/mssql/backup/";

    static readonly Regex Tag = new("^[a-z0-9]{1,20}\\z", RegexOptions.CultureInvariant);

    /// <summary>DB 연결 환경 변수가 있는지.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar));

    /// <summary>컴포넌트 접두 "dbrt_{component}_". 모르는 코드면 ArgumentException.</summary>
    public static string Prefix(string component)
    {
        if (!Components.Contains(component)) throw new ArgumentException($"모르는 컴포넌트 코드: {component} (허용: {string.Join(", ", Components)})");
        return RootPrefix + component + "_";
    }

    /// <summary>새 DB 이름 "dbrt_{component}_{tag}_{8자리 16진}". tag는 소문자·숫자 1~20자.</summary>
    public static string NewName(string component, string tag)
    {
        if (!Tag.IsMatch(tag)) throw new ArgumentException($"태그는 소문자·숫자 1~20자: {tag}");
        return $"{Prefix(component)}{tag}_{Guid.NewGuid():N}"[..(Prefix(component).Length + tag.Length + 9)];
    }

    /// <summary>테스트용 연결 문자열(풀링 끔: RESTORE·DROP이 끊은 풀 연결을 다시 쓰지 않게). 환경 변수가 없으면 InvalidOperationException.</summary>
    public static string ConnectionString(string? database = null)
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException($"{ConnectionEnvVar} 없음(. .\\scripts\\env.ps1)");
        var b = new SqlConnectionStringBuilder(raw) { Pooling = false };
        if (database is not null) b.InitialCatalog = database;
        return b.ConnectionString;
    }

    /// <summary>연결을 연다.</summary>
    public static async Task<SqlConnection> OpenAsync(string? database = null, CancellationToken ct = default)
    {
        var conn = new SqlConnection(ConnectionString(database));
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>배치마다 별도 명령으로 실행(자동 커밋, CREATE TRIGGER는 배치 첫 문장).</summary>
    public static async Task ExecAsync(string database, params string[] batches)
    {
        await using var conn = await OpenAsync(database);
        foreach (var sql in batches)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>스칼라 하나.</summary>
    public static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql, SqlTransaction? tx = null)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    /// <summary>모든 행(값은 DBNull → null).</summary>
    public static async Task<List<object?[]>> RowsAsync(SqlConnection conn, string sql, SqlTransaction? tx = null)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>테스트 DB인지(dbrt_ + 허용 컴포넌트 접두, 영문자·숫자·밑줄만).</summary>
    public static bool IsTestDatabase(string name) =>
        Regex.IsMatch(name, "^[A-Za-z0-9_]+\\z", RegexOptions.CultureInvariant)
        && Components.Any(c => name.StartsWith(RootPrefix + c + "_", StringComparison.Ordinal));

    /// <summary>
    /// 테스트 DB를 지운다. 스냅샷·비정상 상태면 바로, ONLINE 일반 DB면 세션을 끊고 DROP. 없으면 아무것도 안 함.
    /// 일반 DB에 스냅샷이 남아 있으면 SQL Server가 거부하므로(오류 3709) 스냅샷부터 지울 것.
    /// </summary>
    public static Task DropAsync(string database)
    {
        if (!IsTestDatabase(database)) throw new ArgumentException($"테스트 DB(dbrt_<컴포넌트>_)가 아님: {database}");
        var name = SqlText.NLiteral(database);
        var quoted = SqlText.Quote(database);
        return ExecAsync("master", $"""
            IF DB_ID({name}) IS NOT NULL
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = {name} AND source_database_id IS NULL AND state_desc = N'ONLINE')
                    ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quoted};
            END;
            """);
    }

    /// <summary>
    /// 이 컴포넌트 접두로 남은 DB를 모두 지운다(스냅샷 먼저). 테스트가 중간에 죽어 남은 것을 손으로 치울 때만 쓴다.
    /// 같은 컴포넌트의 다른 테스트 실행이 돌고 있으면 그 DB도 지우므로 픽스처 안에서 자동으로 부르지 않는다. 지운 이름을 돌려준다.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DropOrphansAsync(string component)
    {
        var prefix = Prefix(component);
        List<object?[]> rows;
        await using (var conn = await OpenAsync("master"))
            rows = await RowsAsync(conn, $"""
                SELECT name FROM sys.databases
                WHERE name LIKE {SqlText.NLiteral(prefix.Replace("_", "[_]") + "%")}
                ORDER BY CASE WHEN source_database_id IS NULL THEN 1 ELSE 0 END, name;
                """);
        var names = rows.Select(r => (string)r[0]!).Where(IsTestDatabase).ToList();
        foreach (var n in names) await DropAsync(n);
        return names;
    }

    /// <summary>테스트 백업 파일 경로(/var/opt/mssql/backup/{database}.bak).</summary>
    public static string BackupPath(string database)
    {
        if (!IsTestDatabase(database)) throw new ArgumentException($"테스트 DB 이름이 아님: {database}");
        return $"{BackupDir}{database}.bak";
    }

    /// <summary>
    /// 테스트가 만든 컨테이너 백업 파일을 지운다(최선 노력). T-SQL에는 문서화된 파일 삭제 수단이 없어 docker exec rm으로 하고,
    /// docker가 없으면 고유 이름 파일만 남는다. 컨테이너 이름은 DBRIPPLE_SQL_CONTAINER(기본 dbripple-sql).
    /// </summary>
    public static bool DeleteContainerFile(string path)
    {
        if (!path.StartsWith(BackupDir + RootPrefix, StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)
            || !IsTestDatabase(Path.GetFileNameWithoutExtension(path)))
            throw new ArgumentException($"테스트 백업 파일이 아님: {path}");
        var container = Environment.GetEnvironmentVariable("DBRIPPLE_SQL_CONTAINER") ?? "dbripple-sql";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "exec", container, "rm", "-f", path }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            return p is not null && p.WaitForExit(30_000) && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>DBRIPPLE_SQL_CONNECTION이 없으면 건너뛰는 DB 통합 테스트.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    /// <summary>환경 변수가 없으면 Skip을 채운다.</summary>
    public SqlFactAttribute()
    {
        if (!TestDb.IsConfigured) Skip = $"{TestDb.ConnectionEnvVar} 없음";
    }
}

/// <summary>
/// 받은 순서대로(스냅샷 → 샌드박스 → 원본처럼 만든 순서의 반대로 넘길 것) 테스트 DB와 컨테이너 백업 파일을 지운다.
/// 정리 실패는 표준 오류에 모아 쓰고 던지지 않는다(정리 예외가 테스트 본문의 원래 실패를 덮지 않게).
/// </summary>
public sealed class TestDatabases(params string[] namesInDropOrder) : IAsyncDisposable
{
    /// <summary>함께 지울 컨테이너 백업 파일.</summary>
    public List<string> ContainerFiles { get; } = [];

    /// <summary>정리.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var n in namesInDropOrder)
        {
            try { await TestDb.DropAsync(n); }
            catch (Exception ex) { Console.Error.WriteLine($"테스트 DB {n} 정리 실패: {ex.Message}"); }
        }
        foreach (var f in ContainerFiles) TestDb.DeleteContainerFile(f);
    }
}
