using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Baseline;

/// <summary>
/// IOriginSteps의 SQL Server 구현(Split 원본 연결). 정해진 읽기 SQL과 COPY_ONLY 백업만 보낸다(DDL·RESTORE·DROP·사용자 생성·가장 없음).
/// 필요한 원본 권한: 개발자 본인의 SELECT·DML, 백업을 원본에서 만들면 db_backupoperator. 연결마다 풀링 없이 열고 닫는다.
/// </summary>
public sealed class SqlOriginSteps(OriginConnection origin, Action<string>? log = null) : IOriginSteps
{
    readonly string connectionString = (origin ?? throw new ArgumentNullException(nameof(origin))).ConnectionString;

    /// <summary>BACKUP은 오래 걸릴 수 있다(초).</summary>
    public int CommandTimeoutSeconds { get; init; } = 600;

    public async Task<InstanceIdentity> ReadIdentityAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(BaselineBuilder.Master, ct);
        await using var cmd = new SqlCommand(BaselineSql.InstanceIdentity, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("원본 인스턴스 이름을 읽지 못함");
        return new InstanceIdentity(r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
    }

    public async Task<string?> ReadInstanceMarkerAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(BaselineBuilder.Master, ct);
        try
        {
            await using var cmd = new SqlCommand(BaselineSql.ReadInstanceMarker, conn) { CommandTimeout = CommandTimeoutSeconds };
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (SqlException ex)
        {
            log?.Invoke($"원본 master 표식 읽기 실패(오류 {ex.Number}): 이름 비교만으로 판정");
            return null;
        }
    }

    public async Task PrecheckAsync(string source, CancellationToken ct)
    {
        await ExecAsync(BaselineBuilder.Master, BaselineSql.SourcePrecheck(source), ct);
        await ExecAsync(source, BaselineSql.SourceGuard(source), ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadSpecialFileGroupsAsync(string source, CancellationToken ct)
    {
        await using var conn = await OpenAsync(source, ct);
        await using var cmd = new SqlCommand(BaselineSql.SpecialFileGroups, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public async Task<Fingerprint> ReadFingerprintAsync(string source, CancellationToken ct)
    {
        await using var conn = await OpenAsync(BaselineBuilder.Master, ct);
        return await Fingerprint.ReadAsync(conn, source, ct);
    }

    public Task BackupAsync(string source, string serverPath, CancellationToken ct) =>
        ExecAsync(BaselineBuilder.Master, BaselineSql.SplitBackup(source, serverPath), ct);

    async Task ExecAsync(string database, string sql, CancellationToken ct)
    {
        await using var conn = await OpenAsync(database, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        var started = DateTimeOffset.Now;
        await cmd.ExecuteNonQueryAsync(ct);
        log?.Invoke($"[원본 {database}] {(DateTimeOffset.Now - started).TotalMilliseconds:N0}ms {FirstLine(sql)}");
    }

    async Task<SqlConnection> OpenAsync(string database, CancellationToken ct)
    {
        var conn = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database, Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    static string FirstLine(string sql)
    {
        var line = sql.TrimStart().Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
