using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Diff.Tests;

/// <summary>
/// 같은 스크립트로 만든 두 DB(live, snap). C12는 두 DB의 같은 이름 테이블을 비교하므로 스냅샷 대신 일반 DB 사본으로 형식·키 경우를 검사한다.
/// (진짜 데이터베이스 스냅샷 경로는 CascadeLab·SP7 테스트가 확인한다.)
/// </summary>
internal sealed class DiffTestDbs : IAsyncDisposable
{
    readonly TestDatabases cleanup;

    DiffTestDbs(string live, string snap)
    {
        Live = live; Snap = snap;
        cleanup = new TestDatabases(snap, live);
    }

    public string Live { get; }
    public string Snap { get; }
    public IReadOnlyList<TableInfo> Tables { get; private set; } = [];

    public static async Task<DiffTestDbs> CreateAsync(string tag, params string[] setupBatches)
    {
        var live = TestDb.NewName("dif", tag);
        var dbs = new DiffTestDbs(live, live + "_s");
        try
        {
            foreach (var db in new[] { dbs.Live, dbs.Snap })
            {
                await TestDb.ExecAsync("master", $"CREATE DATABASE [{db}];");
                await TestDb.ExecAsync(db, setupBatches);
            }
            await using var conn = await TestDb.OpenAsync(dbs.Live);
            dbs.Tables = (await CatalogReader.ReadAsync(conn)).ToCaptureInput();
            return dbs;
        }
        catch
        {
            await dbs.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => cleanup.DisposeAsync();
}

internal static class Sql
{
    public static async Task ExecAsync(SqlConnection conn, SqlTransaction? tx, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(SqlConnection conn, SqlTransaction? tx, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 600 };
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    /// <summary>NetChange 목록의 결정적 문자열(비교·로그용).</summary>
    public static string Dump(IEnumerable<NetChange> changes) => string.Join("\n", changes.Select(c =>
        $"{c.Table} {Name(c.Change)} x{c.Multiplicity} key[{string.Join(",", c.Key.Columns)}]=({string.Join(",", c.Key.Values)}) " +
        $"cols[{string.Join(",", c.ChangedColumns)}]"));

    /// <summary>계약 직렬화 이름(INSERTED, MULTISET_ADDED …).</summary>
    public static string Name(NetChangeKind k) => System.Text.Json.JsonSerializer.Serialize(k, ContractJson.Options).Trim('"');
}
