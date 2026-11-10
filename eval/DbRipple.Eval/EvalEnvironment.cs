using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace DbRipple.Eval;

/// <summary>
/// 실행 환경 기록 env.json(§10.2 환경 고정, §10.6). 버전·에디션·CU, nested triggers, disallow results from triggers, @@OPTIONS(이 연결),
/// DB별 호환성 수준·RECURSIVE_TRIGGERS·데이터 정렬·스냅숏 격리, 서버 CPU·메모리, 클라이언트 OS·런타임·논리 프로세서 수.
/// 연결 문자열·호스트 이름·사용자 이름은 넣지 않는다(공개 저장소).
/// </summary>
public static class EvalEnvironment
{
    /// <summary>환경을 읽는다. extra는 그대로 덧붙인다(run_id, 커밋 등).</summary>
    public static async Task<JsonObject> ReadAsync(string connectionString, IEnumerable<string> databases, IReadOnlyDictionary<string, string?>? extra, CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(connectionString) { Pooling = false, InitialCatalog = "master" };
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        var env = new JsonObject();
        if (extra is not null) foreach (var (k, v) in extra) env[k] = v;

        await FillAsync(conn, env, """
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS productVersion,
                   CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)) AS productLevel,
                   CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(128)) AS productUpdateLevel,
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition,
                   CAST(SERVERPROPERTY('EngineEdition') AS int) AS engineEdition,
                   (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'nested triggers') AS nestedTriggers,
                   (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'disallow results from triggers') AS disallowResultsFromTriggers,
                   @@OPTIONS AS sessionOptions;
            """, ct);
        try
        {
            await FillAsync(conn, env, "SELECT cpu_count AS serverCpuCount, physical_memory_kb AS serverPhysicalMemoryKb, committed_target_kb AS serverCommittedTargetKb FROM sys.dm_os_sys_info;", ct);
        }
        catch (SqlException ex)
        {
            env["serverSysInfoError"] = ex.Number; // VIEW SERVER STATE 권한이 없을 수 있다
        }

        var dbs = new JsonObject();
        foreach (var db in databases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var o = new JsonObject();
            await using var cmd = new SqlCommand("""
                SELECT compatibility_level, is_recursive_triggers_on, collation_name, is_read_committed_snapshot_on, snapshot_isolation_state_desc
                FROM sys.databases WHERE name = @n;
                """, conn);
            cmd.Parameters.AddWithValue("@n", db);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                for (var i = 0; i < r.FieldCount; i++) o[r.GetName(i)] = r.IsDBNull(i) ? null : JsonValue.Create(r.GetValue(i).ToString());
            else o["missing"] = true;
            dbs[db] = o;
        }
        env["databases"] = dbs;
        env["clientOs"] = RuntimeInformation.OSDescription;
        env["clientRuntime"] = RuntimeInformation.FrameworkDescription;
        env["clientProcessorCount"] = Environment.ProcessorCount;
        return env;
    }

    /// <summary>env.json을 쓴다(UTF-8 BOM 없음, 들여쓰기).</summary>
    public static Task WriteAsync(string path, JsonObject env, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return File.WriteAllTextAsync(path, env.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), MetricsCsv.Utf8NoBom, ct);
    }

    static async Task FillAsync(SqlConnection conn, JsonObject env, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return;
        for (var i = 0; i < r.FieldCount; i++)
            env[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i) switch
            {
                int x => JsonValue.Create(x),
                long x => JsonValue.Create(x),
                bool x => JsonValue.Create(x),
                var v => JsonValue.Create(v.ToString()),
            };
    }
}
