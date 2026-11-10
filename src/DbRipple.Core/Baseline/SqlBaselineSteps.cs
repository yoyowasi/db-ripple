using System.Data;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Baseline;

/// <summary>
/// IBaselineSteps의 SQL Server 구현. 모든 단계는 자동 커밋, 배치마다 별도 명령(A16).
/// 연결은 풀링 없이 마지막에 쓴 DB 하나만 열어 두고 다시 쓴다(설치 배치 수백 개마다 로그인하지 않게). DB가 바뀌면 이전 연결을 닫는다.
/// </summary>
public sealed class SqlBaselineSteps(string connectionString, Action<string>? log = null) : IBaselineSteps, IAsyncDisposable
{
    SqlConnection? current;
    string? currentDatabase;

    /// <summary>BACKUP·RESTORE·스냅샷 생성은 오래 걸릴 수 있어 시간 제한을 둔다(초).</summary>
    public int CommandTimeoutSeconds { get; init; } = 600;

    public Fingerprint? SourceFingerprint { get; private set; }
    public Fingerprint? SandboxFingerprint { get; private set; }

    public async Task ExecuteAsync(string database, string sql, CancellationToken ct)
    {
        var conn = await ConnectionAsync(database, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        var started = DateTimeOffset.Now;
        await cmd.ExecuteNonQueryAsync(ct);
        log?.Invoke($"[{database}] {(DateTimeOffset.Now - started).TotalMilliseconds:N0}ms {FirstLine(sql)}");
    }

    public async Task<IReadOnlyList<BackupFile>> ReadFileListAsync(string sql, CancellationToken ct)
    {
        var conn = await ConnectionAsync(BaselineBuilder.Master, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        int name = r.GetOrdinal("LogicalName"), type = r.GetOrdinal("Type"), id = r.GetOrdinal("FileId");
        var files = new List<BackupFile>();
        while (await r.ReadAsync(ct))
            files.Add(new BackupFile(r.GetString(name), r.GetString(type)[0], Convert.ToInt64(r.GetValue(id))));
        log?.Invoke("FILELISTONLY: " + string.Join(", ", files.Select(f => $"{f.LogicalName}({f.Type},{f.FileId})")));
        return files;
    }

    public async Task<DbCatalog> ReadCatalogAsync(string database, CancellationToken ct) =>
        await CatalogReader.ReadAsync(await ConnectionAsync(database, ct), ct);

    public async Task<IReadOnlyDictionary<string, long>> ReadRowCountsAsync(string database, CancellationToken ct)
    {
        var conn = await ConnectionAsync(database, ct);
        await using var cmd = new SqlCommand(BaselineSql.RowCounts, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct)) counts[r.GetString(0) + "." + r.GetString(1)] = r.GetInt64(2);
        return counts;
    }

    public async Task<bool> FingerprintMatchesAsync(string source, string sandbox, CancellationToken ct)
    {
        var conn = await ConnectionAsync(BaselineBuilder.Master, ct);
        SourceFingerprint = await Fingerprint.ReadAsync(conn, source, ct);
        SandboxFingerprint = await Fingerprint.ReadAsync(conn, sandbox, ct);
        return SourceFingerprint == SandboxFingerprint;
    }

    async Task<SqlConnection> ConnectionAsync(string database, CancellationToken ct)
    {
        if (current is { State: ConnectionState.Open } && string.Equals(currentDatabase, database, StringComparison.Ordinal)) return current;
        await CloseAsync();
        var b = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database, Pooling = false };
        var conn = new SqlConnection(b.ConnectionString);
        await conn.OpenAsync(ct);
        (current, currentDatabase) = (conn, database);
        return conn;
    }

    async Task CloseAsync()
    {
        if (current is not null) await current.DisposeAsync();
        (current, currentDatabase) = (null, null);
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    static string FirstLine(string sql)
    {
        var line = sql.TrimStart().Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
