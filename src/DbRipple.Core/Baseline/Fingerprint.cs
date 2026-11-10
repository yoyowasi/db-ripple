using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Baseline;

/// <summary>환경 지문(§6.2). 원본과 샌드박스에서 모두 같아야 미리보기·[적용]을 허용한다.</summary>
public sealed record Fingerprint(
    string ProductVersion,
    string Edition,
    int CompatibilityLevel,
    bool IsRecursiveTriggersOn,
    int NestedTriggers,
    int DisallowResultsFromTriggers,
    int SessionOptions,
    string Collation)
{
    public const string Sql = """
        SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
               d.compatibility_level,
               d.is_recursive_triggers_on,
               (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'nested triggers'),
               (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'disallow results from triggers'),
               @@OPTIONS,
               CAST(d.collation_name AS nvarchar(128))
        FROM sys.databases AS d
        WHERE d.name = @db;
        """;

    public static async Task<Fingerprint> ReadAsync(SqlConnection conn, string database, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand(Sql, conn);
        cmd.Parameters.Add(new SqlParameter("@db", System.Data.SqlDbType.NVarChar, 128) { Value = database });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"DB 없음: {database}");
        return new Fingerprint(r.GetString(0), r.GetString(1), r.GetByte(2), r.GetBoolean(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7));
    }
}
