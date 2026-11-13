using System.Data;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Baseline;

/// <summary>
/// IBaselineSteps·ISandboxSteps의 SQL Server 구현(샌드박스 연결만 받는다: 파괴 단계가 원본 연결로 가지 않게). 모든 단계는 자동 커밋, 배치마다 별도 명령(A16).
/// 연결은 풀링 없이 마지막에 쓴 DB 하나만 열어 두고 다시 쓴다(설치 배치 수백 개마다 로그인하지 않게). DB가 바뀌면 이전 연결을 닫는다.
/// SingleServer에서는 원본도 같은 인스턴스라 이 연결로 백업·지문까지 한다(기존 동작).
/// </summary>
public sealed class SqlBaselineSteps(SandboxConnection connection, Action<string>? log = null) : ISandboxSteps, IAsyncDisposable
{
    readonly string connectionString = (connection ?? throw new ArgumentNullException(nameof(connection))).ConnectionString;

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
        int name = r.GetOrdinal("LogicalName"), type = r.GetOrdinal("Type"), id = r.GetOrdinal("FileId"), group = r.GetOrdinal("FileGroupName");
        var files = new List<BackupFile>();
        while (await r.ReadAsync(ct))
            files.Add(new BackupFile(r.GetString(name), r.GetString(type)[0], Convert.ToInt64(r.GetValue(id)), r.IsDBNull(group) ? null : r.GetString(group)));
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
        return FingerprintComparison.Compare(SourceFingerprint, SandboxFingerprint, ServerMode.SingleServer).Match;
    }

    public async Task<LocalServerInfo> ReadServerInfoAsync(CancellationToken ct)
    {
        var conn = await ConnectionAsync(BaselineBuilder.Master, ct);
        await using var cmd = new SqlCommand(BaselineSql.LocalServerInfo, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("로컬 서버 정보를 읽지 못함");
        var info = new LocalServerInfo(new InstanceIdentity(r.GetString(0), r.GetString(1)), r.GetInt32(2),
            r.IsDBNull(3) ? BaselineSql.DataDir : r.GetString(3), r.IsDBNull(4) ? BaselineSql.DataDir : r.GetString(4));
        log?.Invoke($"로컬 인스턴스 {info.Identity.ServerName}, 주버전 {info.MajorVersion}, 데이터 {info.DataDir}, 로그 {info.LogDir}");
        return info;
    }

    public async Task<string> EnsureInstanceMarkerAsync(CancellationToken ct)
    {
        var conn = await ConnectionAsync(BaselineBuilder.Master, ct);
        await using var cmd = new SqlCommand(BaselineSql.EnsureInstanceMarker(Guid.NewGuid()), conn) { CommandTimeout = CommandTimeoutSeconds };
        return await cmd.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException("로컬 master 인스턴스 표식을 만들지 못함");
    }

    public async Task<IReadOnlyList<BackupHeader>> ReadHeadersAsync(string sql, CancellationToken ct)
    {
        var conn = await ConnectionAsync(BaselineBuilder.Master, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        int Ord(string n) { try { return r.GetOrdinal(n); } catch (IndexOutOfRangeException) { return -1; } }
        int type = Ord("BackupType"), key = Ord("KeyAlgorithm"), major = Ord("SoftwareVersionMajor"), compat = Ord("CompatibilityLevel"),
            coll = Ord("Collation"), db = Ord("DatabaseName"), finish = Ord("BackupFinishDate");
        string? Str(int i) => i < 0 || r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        int Int(int i) => i < 0 || r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        var rows = new List<BackupHeader>();
        while (await r.ReadAsync(ct))
            rows.Add(new BackupHeader(Int(type), Str(key), Int(major), Int(compat), Str(coll), Str(db), finish < 0 || r.IsDBNull(finish) ? null : r.GetDateTime(finish)));
        log?.Invoke("HEADERONLY: " + string.Join(", ", rows.Select(h => $"{h.DatabaseName} type {h.BackupType} v{h.SoftwareVersionMajor} compat {h.CompatibilityLevel} {h.Collation}")));
        return rows;
    }

    public async Task<Fingerprint> ReadFingerprintAsync(string database, CancellationToken ct) =>
        await Fingerprint.ReadAsync(await ConnectionAsync(BaselineBuilder.Master, ct), database, ct);

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
