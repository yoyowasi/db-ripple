using System.Data;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Sql;
using Microsoft.Data.SqlClient;

namespace DbRipple.Apply;

/// <summary>
/// 적용 서비스가 원본 서버에 하는 일(선행 조건 읽기, 원본 스냅샷, 전용 연결). 서비스 흐름을 DB 없이 시험하려고 둔 내부 이음매다.
/// 실제 구현은 <see cref="SqlOriginDatabase"/>이고, 단위 테스트는 가짜를 넣는다(InternalsVisibleTo).
/// </summary>
internal interface IOriginDatabase
{
    /// <summary>원본 환경 지문(Core Fingerprint, master 연결에서 읽어 SandboxManager와 같은 세션 옵션).</summary>
    Task<Fingerprint> ReadFingerprintAsync(string originDb, CancellationToken ct);
    /// <summary>원본에 EXECUTE AS 대상 사용자가 있는지(읽기만, Q30: 만들지 않는다).</summary>
    Task<bool> UserExistsAsync(string originDb, string user, CancellationToken ct);
    /// <summary>원본 스냅샷을 만들 수 있는지(실행 전 검증 모드 결정, 읽기만): CREATE DATABASE 권한, FILESTREAM·메모리 최적화 파일 그룹.</summary>
    Task<SnapshotCapability> CheckSnapshotCapabilityAsync(string originDb, CancellationToken ct);
    /// <summary>원본 스냅샷 생성. 실패하면 Failed(error로 끝낸다, KEYSET으로 조용히 낮추지 않는다).</summary>
    Task<(SnapshotOutcome Outcome, string? Reason)> CreateSnapshotAsync(string originDb, string snapshotDb, CancellationToken ct);
    /// <summary>원본 스냅샷 삭제.</summary>
    Task DropSnapshotAsync(string originDb, string snapshotDb, CancellationToken ct);
    /// <summary>원본 전용 연결(풀링 끔, InitialCatalog = 원본). 열 수 없으면 SandboxUnavailableException.</summary>
    Task<IOriginConnection> OpenAsync(string originDb, CancellationToken ct);
}

/// <summary>원본 전용 연결 하나(도구 소유 T-SQL 트랜잭션). Dispose는 연결을 닫는다(서버가 남은 트랜잭션을 롤백).</summary>
internal interface IOriginConnection : IAsyncDisposable
{
    /// <summary>실행 봉투·최종 차이에 넘기는 세션(Transaction = null, T-SQL BEGIN TRAN).</summary>
    SqlSession Session { get; }
    /// <summary>"BEGIN TRAN;" 후 CURRENT_TRANSACTION_ID().</summary>
    Task<long?> BeginTransactionAsync(CancellationToken ct);
    /// <summary>"COMMIT;" 후 @@TRANCOUNT = 0 확인(아니면 InvalidOperationException).</summary>
    Task CommitAsync(CancellationToken ct);
    /// <summary>"IF @@TRANCOUNT > 0 ROLLBACK;"(이미 끝났으면 아무것도 안 함).</summary>
    Task RollbackAsync(CancellationToken ct);
    /// <summary>§5.3 드리프트 검사(UPDLOCK, HOLDLOCK).</summary>
    Task<IReadOnlyList<DriftRow>> CheckDriftAsync(PreviewRecord preview, IReadOnlyList<TableInfo> tables, CancellationToken ct);
    /// <summary>KEYSET: 모든 사용자 테이블 COUNT_BIG(*).</summary>
    Task<IReadOnlyDictionary<string, long>> CountAllAsync(IReadOnlyList<TableInfo> tables, CancellationToken ct);
    /// <summary>KEYSET: 건수 변화와 키 집합 after 대조.</summary>
    Task<(IReadOnlyList<TableCountChange> Counts, IReadOnlyList<ApplyMismatch> Mismatches)> VerifyKeysetAsync(
        PreviewRecord preview, IReadOnlyDictionary<string, long> before, IReadOnlyList<TableInfo> tables, BaselineContext baseline, CancellationToken ct);
}

/// <summary>연결 열기(연결 실패 → SandboxUnavailableException, 메시지에 연결 문자열을 넣지 않는다).</summary>
internal static class OriginConnections
{
    public static async Task<SqlConnection> OpenAsync(string connectionString, string database, CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database, Pooling = false };
        var conn = new SqlConnection(csb.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (SqlException ex)
        {
            await conn.DisposeAsync();
            throw new SandboxUnavailableException($"원본 서버에 연결할 수 없음(DB {database}, 오류 {ex.Number})", ex);
        }
    }
}

/// <summary><see cref="IOriginDatabase"/>의 SQL Server 구현.</summary>
internal sealed class SqlOriginDatabase(string connectionString, ApplySettings settings) : IOriginDatabase
{
    public async Task<SnapshotCapability> CheckSnapshotCapabilityAsync(string originDb, CancellationToken ct)
    {
        OriginNames.Validate(originDb, "원본");
        await using var conn = await OriginConnections.OpenAsync(connectionString, "master", ct);
        await using var cmd = new SqlCommand(OriginSnapshot.CapabilitySql(originDb), conn) { CommandTimeout = settings.ToolCommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("원본 스냅샷 가능 여부를 읽지 못함");
        return OriginSnapshot.Decide(r.GetInt32(0) == 1, r.GetInt32(1), r.GetInt32(2));
    }

    public async Task<Fingerprint> ReadFingerprintAsync(string originDb, CancellationToken ct)
    {
        await using var conn = await OriginConnections.OpenAsync(connectionString, "master", ct);
        return await Fingerprint.ReadAsync(conn, originDb, ct);
    }

    public async Task<bool> UserExistsAsync(string originDb, string user, CancellationToken ct)
    {
        OriginNames.Validate(originDb, "원본");
        await using var conn = await OriginConnections.OpenAsync(connectionString, "master", ct);
        // 사용자 종류(SQL 사용자, Windows 사용자·그룹, 외부, 인증서·키 매핑)만. 역할(R)·응용 역할(A)은 EXECUTE AS USER 대상이 아니다.
        await using var cmd = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM {SqlText.Quote(originDb)}.sys.database_principals WHERE name = @u AND type IN (N'S', N'U', N'G', N'E', N'X', N'C', N'K');",
            conn) { CommandTimeout = settings.ToolCommandTimeoutSeconds };
        cmd.Parameters.Add(new SqlParameter("@u", SqlDbType.NVarChar, 128) { Value = user });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public Task<(SnapshotOutcome Outcome, string? Reason)> CreateSnapshotAsync(string originDb, string snapshotDb, CancellationToken ct) =>
        OriginSnapshot.CreateAsync(connectionString, originDb, snapshotDb, settings.ToolCommandTimeoutSeconds, ct);

    public Task DropSnapshotAsync(string originDb, string snapshotDb, CancellationToken ct) =>
        OriginSnapshot.DropAsync(connectionString, originDb, snapshotDb, settings.ToolCommandTimeoutSeconds, ct);

    public async Task<IOriginConnection> OpenAsync(string originDb, CancellationToken ct)
    {
        OriginNames.Validate(originDb, "원본");
        var conn = await OriginConnections.OpenAsync(connectionString, originDb, ct);
        if (settings.Mode == ServerMode.Split && settings.OriginLockTimeoutMs > 0)
        {
            // 원본은 팀 서버일 수 있다: 남의 잠금을 무한히 기다리지 않는다(초과는 오류 1222 → 문장 실패·롤백).
            await using var cmd = new SqlCommand($"SET LOCK_TIMEOUT {settings.OriginLockTimeoutMs};", conn) { CommandTimeout = settings.ToolCommandTimeoutSeconds };
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new SqlOriginConnection(conn, settings.ToolCommandTimeoutSeconds);
    }
}

/// <summary><see cref="IOriginConnection"/>의 SQL Server 구현.</summary>
internal sealed class SqlOriginConnection(SqlConnection connection, int toolTimeoutSeconds) : IOriginConnection
{
    public SqlSession Session { get; } = new() { Connection = connection };

    public async Task<long?> BeginTransactionAsync(CancellationToken ct)
    {
        await ExecAsync("BEGIN TRAN;", ct);
        await using var cmd = Session.CreateCommand("SELECT CURRENT_TRANSACTION_ID();", toolTimeoutSeconds);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await ExecAsync("COMMIT;", ct);
        await using var cmd = Session.CreateCommand("SELECT @@TRANCOUNT;", toolTimeoutSeconds);
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        if (n != 0) throw new InvalidOperationException($"COMMIT 뒤 @@TRANCOUNT = {n}(중첩 트랜잭션이 남음)");
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open) return;
        await ExecAsync("IF @@TRANCOUNT > 0 ROLLBACK;", ct);
    }

    public Task<IReadOnlyList<DriftRow>> CheckDriftAsync(PreviewRecord preview, IReadOnlyList<TableInfo> tables, CancellationToken ct) =>
        DriftChecker.CheckAsync(Session, preview, tables, toolTimeoutSeconds, ct);

    public Task<IReadOnlyDictionary<string, long>> CountAllAsync(IReadOnlyList<TableInfo> tables, CancellationToken ct) =>
        KeysetVerifier.CountAllAsync(Session, tables, toolTimeoutSeconds, ct);

    public Task<(IReadOnlyList<TableCountChange> Counts, IReadOnlyList<ApplyMismatch> Mismatches)> VerifyKeysetAsync(
        PreviewRecord preview, IReadOnlyDictionary<string, long> before, IReadOnlyList<TableInfo> tables, BaselineContext baseline, CancellationToken ct) =>
        KeysetVerifier.VerifyAsync(Session, preview, before, tables, baseline, toolTimeoutSeconds, ct);

    async Task ExecAsync(string sql, CancellationToken ct)
    {
        await using var cmd = Session.CreateCommand(sql, toolTimeoutSeconds);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
