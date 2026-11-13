using System.Data.Common;
using DbRipple.Core.Capture;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Catalog;

/// <summary>C7: 연결된 DB의 카탈로그를 읽는다(읽기 전용).</summary>
public static class CatalogReader
{
    public const string ConnectionEnvVar = "DBRIPPLE_SQL_CONNECTION";

    public static async Task<DbCatalog> ReadAsync(SqlConnection conn, CancellationToken ct = default)
    {
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        var tables = await Read(conn, CatalogQueries.Tables, r => new CatalogTable(
            r.GetInt32(0), r.GetString(1), r.GetString(2), (TemporalType)r.GetByte(3), r.GetBoolean(4), r.IsDBNull(5) ? "" : r.GetString(5)), ct);
        var columns = await Read(conn, CatalogQueries.Columns, r => new CatalogColumn(
            r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
            r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9)), ct);
        var keys = await Read(conn, CatalogQueries.KeyIndexes, r => new CatalogIndexColumn(
            r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetByte(5), r.GetString(6), r.GetBoolean(7)), ct);
        var fks = await Read(conn, CatalogQueries.ForeignKeys, r => new CatalogForeignKeyColumn(
            r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetString(5),
            r.GetBoolean(6), r.GetBoolean(7), r.GetInt32(8), r.GetString(9), r.GetString(10)), ct);
        var triggers = await Read(conn, CatalogQueries.Triggers, r => new CatalogTriggerEvent(
            r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
            r.GetString(6), r.GetBoolean(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9)), ct);
        var files = await Read(conn, CatalogQueries.Files, r => new CatalogFile(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);
        var procedures = await Read(conn, CatalogQueries.Procedures, r => new CatalogModule(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)), ct);
        var functions = await Read(conn, CatalogQueries.Functions, r => new CatalogFunction(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5)), ct);
        var synonyms = await Read(conn, CatalogQueries.Synonyms, r => new CatalogSynonym(r.GetString(0), r.GetString(1), r.GetString(2)), ct);
        var views = await Read(conn, CatalogQueries.Views, r => new CatalogView(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)), ct);
        var tableFunctions = await Read(conn, CatalogQueries.TableFunctions, r => new CatalogTableFunction(
            r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)), ct);
        return new DbCatalog(tables, columns, keys, fks, triggers, files, procedures, functions, synonyms, views, tableFunctions);
    }

    static async Task<List<T>> Read<T>(SqlConnection conn, string sql, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }
}
