using DbRipple.Core.Catalog;
using DbRipple.Core.Sql;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Tests;

/// <summary>DBRIPPLE_SQL_CONNECTION이 없으면 건너뛰는 DB 통합 테스트.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CatalogReader.ConnectionEnvVar)))
            Skip = $"{CatalogReader.ConnectionEnvVar} 없음";
    }
}

/// <summary>통합 테스트용 연결(풀링 없음: RESTORE·DROP이 끊은 풀 연결을 다시 쓰지 않게).</summary>
static class TestSql
{
    /// <summary>임시 DB 이름 접두. 테스트가 만들고 끝나면 지운다.</summary>
    public const string Prefix = "dbr_it_";

    public static string ConnectionString(string? database = null)
    {
        var b = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(CatalogReader.ConnectionEnvVar)) { Pooling = false };
        if (database is not null) b.InitialCatalog = database;
        return b.ConnectionString;
    }

    public static string NewName(string tag) => $"{Prefix}{tag}_{Guid.NewGuid():N}"[..(Prefix.Length + tag.Length + 13)];

    public static async Task<SqlConnection> OpenAsync(string? database = null)
    {
        var conn = new SqlConnection(ConnectionString(database));
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>배치마다 별도 명령으로 실행(CREATE TRIGGER는 배치 첫 문장).</summary>
    public static async Task ExecAsync(string database, params string[] batches)
    {
        await using var conn = await OpenAsync(database);
        foreach (var sql in batches)
        {
            await using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql, SqlTransaction? tx = null)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

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

    /// <summary>스냅샷·비정상 상태면 바로, ONLINE 일반 DB면 세션을 끊고 DROP. 없으면 아무것도 안 함. 임시 DB(접두 dbr_it_)만.</summary>
    public static Task DropAsync(string database)
    {
        if (!database.StartsWith(Prefix, StringComparison.Ordinal)) throw new ArgumentException($"임시 DB가 아님: {database}");
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

    /// <summary>임시 DB 이름으로 만든 컨테이너 백업 파일 경로(실행마다 고유).</summary>
    public static string BackupPath(string database) => $"/var/opt/mssql/backup/{database}.bak";

    /// <summary>
    /// 테스트가 만든 컨테이너 백업 파일을 지운다. T-SQL에는 문서화된 파일 삭제 수단이 없어 docker exec로 하고(최선 노력),
    /// docker가 없으면 고유 이름 파일만 남는다. 컨테이너 이름은 DBRIPPLE_SQL_CONTAINER(기본 dbripple-sql).
    /// </summary>
    public static bool DeleteContainerFile(string path)
    {
        if (!path.StartsWith("/var/opt/mssql/backup/" + Prefix, StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
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

/// <summary>만든 순서와 반대로(스냅샷 → 샌드박스 → 원본) 임시 DB와 컨테이너 백업 파일을 지운다. 정리 실패는 표준 오류에 모아 쓰고 던지지 않는다
/// (await using의 정리 예외가 테스트 본문의 원래 실패를 덮지 않게). 남은 dbr_it_ DB는 이름으로 찾아 지울 수 있다.</summary>
sealed class TempDatabases(params string[] namesInDropOrder) : IAsyncDisposable
{
    public List<string> ContainerFiles { get; } = [];

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        foreach (var n in namesInDropOrder)
        {
            try { await TestSql.DropAsync(n); }
            catch (Exception ex) { errors.Add(new InvalidOperationException($"임시 DB {n} 정리 실패: {ex.Message}", ex)); }
        }
        foreach (var f in ContainerFiles) TestSql.DeleteContainerFile(f); // 최선 노력(남아도 고유 이름)
        foreach (var e in errors) Console.Error.WriteLine(e.Message);
    }
}
