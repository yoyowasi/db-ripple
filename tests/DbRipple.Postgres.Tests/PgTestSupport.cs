using System.Diagnostics;
using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Execution;
using DbRipple.Preview;
using Npgsql;
using Xunit.Abstractions;

namespace DbRipple.Postgres.Tests;

/// <summary>
/// DBRIPPLE_PG_TEST_CONNECTION이 없으면 건너뛰는 PostgreSQL 통합 테스트(SqlFact와 같은 모양).
/// db 잡(home-test)은 건너뜀이 10을 넘으면 WARN으로 실패하므로 이 속성을 단 테스트 수는 적게 둔다.
/// </summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (!PgTestDb.IsConfigured) Skip = $"{PgTestDb.ConnectionEnvVar} 없음";
    }
}

/// <summary>통합 테스트는 서버 하나를 같이 쓰므로 차례로 돈다(역할·DB 만들기 경합 방지).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PgCollection
{
    public const string Name = "postgres-integration";
}

/// <summary>통합 테스트 연결. 연결 문자열에는 비밀번호가 있으므로 출력하지 않는다.</summary>
static class PgTestDb
{
    public const string ConnectionEnvVar = "DBRIPPLE_PG_TEST_CONNECTION";
    /// <summary>테스트가 만드는 DB·역할 이름 접두. 이 접두가 있는 것만 지운다.</summary>
    public const string Prefix = "dbr_it_";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar));

    public static string Server => Environment.GetEnvironmentVariable(ConnectionEnvVar) is { Length: > 0 } s
        ? s : throw new InvalidOperationException($"{ConnectionEnvVar} 없음");

    static NpgsqlConnectionStringBuilder Builder => new(Server);

    public static string AdminDatabase => Builder.Database is { Length: > 0 } d ? d : "postgres";
    public static string User => Builder.Username ?? "";
    public static string Password => Builder.Password ?? "";

    /// <summary>실패 메시지에 넣기 전에 비밀번호를 가린다.</summary>
    public static string Mask(string text) => Password.Length > 0 ? text.Replace(Password, "***", StringComparison.Ordinal) : text;

    // 풀링 끔: TEMPLATE 복제는 원본에 다른 연결이 있으면 거절되므로 풀에 남은 연결이 없어야 한다.
    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(Server) { Database = database, Pooling = false }.ConnectionString;

    public static async Task<NpgsqlConnection> OpenAsync(string database)
    {
        var conn = new NpgsqlConnection(ConnectionString(database));
        await conn.OpenAsync();
        return conn;
    }

    public static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    public static async Task<long> CountAsync(NpgsqlConnection conn, string sql, params object[] args) =>
        Convert.ToInt64(await ScalarAsync(conn, sql, args), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>테이블마다 행 전체를 문자열로(정렬). 미리보기 전후 비교용.</summary>
    public static async Task<string> SnapshotAsync(string database, IEnumerable<string> tables)
    {
        await using var conn = await OpenAsync(database);
        var lines = new List<string>();
        foreach (var t in tables)
            lines.Add($"{t}: {await ScalarAsync(conn, $"SELECT coalesce(string_agg(x::text, ' ' ORDER BY x::text), '') FROM {t} x")}");
        return string.Join("\n", lines);
    }

    public static void RequireTestName(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) throw new ArgumentException($"테스트가 만든 이름이 아님: {name}");
    }
}

/// <summary>
/// 테스트 하나의 원본 DB·샌드박스 DB·미리보기 역할(모두 접두 dbr_it_). 만들다 실패해도, 테스트가 실패해도 Dispose에서 지운다.
/// 정리 실패는 출력에 쓰고 던지지 않는다(원래 실패를 덮지 않게).
/// </summary>
sealed class PgEnv : IAsyncDisposable
{
    public required string Origin { get; init; }
    public required string Sandbox { get; init; }
    public required string Role { get; init; }
    public required PgOptions Options { get; init; }
    public required ITestOutputHelper Output { get; init; }

    public static async Task<PgEnv> CreateAsync(string tag, ITestOutputHelper output, params string[] setupSql)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var origin = $"{PgTestDb.Prefix}{tag}_{suffix}";
        var env = new PgEnv
        {
            Origin = origin,
            Sandbox = origin + "_sbx",
            Role = $"{PgTestDb.Prefix}role_{suffix}",
            Options = new PgOptions
            {
                ServerConnectionString = PgTestDb.Server,
                OriginDb = origin,
                SandboxDb = origin + "_sbx",
                PreviewRole = $"{PgTestDb.Prefix}role_{suffix}",
                CommandTimeoutSeconds = 120,
                Log = m => output.WriteLine(m),
            },
            Output = output,
        };
        try
        {
            await using (var admin = await PgTestDb.OpenAsync(PgTestDb.AdminDatabase))
            {
                await RequireNonSuperuserAsync(admin, output);
                await PgTestDb.ExecAsync(admin, $"CREATE DATABASE {PgNames.Quote(origin)}");
            }
            await using (var conn = await PgTestDb.OpenAsync(origin))
                foreach (var sql in setupSql) await PgTestDb.ExecAsync(conn, sql);
            return env;
        }
        catch
        {
            await env.DisposeAsync();
            throw;
        }
    }

    // 연결 계정이 설계 결정 3의 권장 구성(비 superuser + CREATEDB·CREATEROLE)인지만 본다.
    // 문장이 미리보기 역할로 도는지(SET LOCAL ROLE)는 새로 고침 테스트의 s.role_probe INSERT 미리보기가 확인한다.
    static async Task RequireNonSuperuserAsync(NpgsqlConnection admin, ITestOutputHelper output)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT current_user::text, rolsuper, rolcreatedb, rolcreaterole, current_setting('server_version') FROM pg_roles WHERE rolname = current_user", admin);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException("pg_roles에서 연결 계정을 찾지 못했습니다.");
        var (user, super, createDb, createRole, version) = (r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetString(4));
        output.WriteLine($"PostgreSQL {version}, 연결 계정 {user}(superuser={super}, createdb={createDb}, createrole={createRole})");
        if (super || !createDb || !createRole)
            throw PgAssert.Fail($"통합 테스트는 superuser가 아닌 계정(LOGIN NOSUPERUSER CREATEDB CREATEROLE)으로 돌아야 합니다. 실제: {user} superuser={super}, createdb={createDb}, createrole={createRole}");
    }

    public PgSandboxManager NewManager() => new(Options, TimeProvider.System);

    /// <summary>Host CreatePgEngine과 같은 조립(저장소만 메모리 없이 버림).</summary>
    public PreviewPipeline Pipeline(PgSandboxManager sandbox) => new(
        sandbox, new PgBatchAnalyzer(), new PgPreviewExecutor(Options), new ChangeCollector(), new CausalResolver(), new NullPreviewStore(), TimeProvider.System);

    /// <summary>새로 고침 → ready여야 한다. 실패하면 예외 전체(SQLSTATE 포함)를 메시지에 담는다.</summary>
    public async Task<SandboxStatus> RefreshReadyAsync(PgSandboxManager sandbox)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await sandbox.RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw PgAssert.Fail($"새로 고침 실패(기대: ready). 원본 {Origin}, 샌드박스 {Sandbox}\n{PgTestDb.Mask(ex.ToString())}");
        }
        var status = await sandbox.GetStatusAsync(CancellationToken.None);
        Output.WriteLine($"새로 고침 {sw.ElapsedMilliseconds}ms, 상태 {status.State}");
        if (status.State != SandboxState.Ready)
            throw PgAssert.Fail($"새로 고침 뒤 상태 기대 Ready, 실제 {status.State}, lastError={status.LastError ?? "-"}");
        return status;
    }

    /// <summary>미리보기 실행. 거부·문장 실패면 규칙·오류 메시지를 담아 실패한다.</summary>
    public async Task<PreviewRecord> PreviewAsync(PreviewPipeline pipeline, string sql, long? imageCap = null)
    {
        var sw = Stopwatch.StartNew();
        var outcome = await pipeline.RunAsync(sql, new PreviewOptions { ImageCap = imageCap }, null, CancellationToken.None);
        sw.Stop();
        var record = outcome.Record ?? throw PgAssert.Fail(
            $"미리보기가 거부됨: {sql}\n{string.Join("\n", outcome.Analysis.Rejects.Select(r => $"문장 {r.StatementIndex}: {r.RuleId} {r.Message}"))}");
        var t = record.Result.TimingsMs;
        Output.WriteLine($"미리보기 {sw.ElapsedMilliseconds}ms(분석 {t.Analyze}, 실행 {t.Execute}, 감사 읽기 {t.ReadLog}, 최종 차이 {t.NetDiff}, 귀속 {t.Resolve}) "
                         + $"상태 {record.Result.Status}, 이미지 {record.Result.Capture.ImageCount}/{record.Result.Capture.Cap}, 묶음 {record.Result.Firings.Count}: {sql.ReplaceLineEndings(" ")}");
        var bad = record.Result.Statements.Where(s => s.Status != StatementStatus.Ok).ToList();
        if (bad.Count > 0)
            throw PgAssert.Fail($"문장 실패: {sql}\n{string.Join("\n", bad.Select(s => $"#{s.Index} {s.Status} {s.Error?.Message} (procedure={s.Error?.Procedure ?? "-"})"))}");
        return record;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var admin = await PgTestDb.OpenAsync(PgTestDb.AdminDatabase);
            foreach (var db in new[] { Sandbox, Origin })
                await TryAsync($"DB {db} 지우기", () =>
                {
                    PgTestDb.RequireTestName(db);
                    return PgTestDb.ExecAsync(admin, $"DROP DATABASE IF EXISTS {PgNames.Quote(db)} WITH (FORCE)");
                });
            // 역할은 클러스터 전역이라 DB를 지운 뒤(권한 의존이 사라진 뒤) 지운다.
            await TryAsync($"역할 {Role} 지우기", () =>
            {
                PgTestDb.RequireTestName(Role);
                return PgTestDb.ExecAsync(admin, $"DROP ROLE IF EXISTS {PgNames.Quote(Role)}");
            });
        }
        catch (Exception ex)
        {
            Output.WriteLine($"정리 연결 실패: {ex.GetType().Name}: {ex.Message}");
        }
    }

    async Task TryAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Output.WriteLine($"{what} 실패: {ex.GetType().Name}: {ex.Message}"); }
    }
}

/// <summary>실패 메시지에 기대값·실제값 전체를 담는다(CI 로그만 보고 고칠 수 있게, xunit 문자열 비교는 길면 잘린다).</summary>
static class PgAssert
{
    public static Exception Fail(string message) => new Xunit.Sdk.XunitException(message);

    public static void Same(string expected, string actual, string what)
    {
        var e = Normalize(expected);
        var a = Normalize(actual);
        if (!string.Equals(e, a, StringComparison.Ordinal))
            throw Fail($"{what}\n--- 기대\n{e}\n--- 실제\n{a}");
    }

    /// <summary>줄 순서와 상관없이 같은 줄 집합(다중집합)인지.</summary>
    public static void SameLines(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Same(string.Join("\n", expected.Select(Normalize).Order(StringComparer.Ordinal)), string.Join("\n", actual.Select(Normalize).Order(StringComparer.Ordinal)), what);

    // 원시 문자열 리터럴은 소스 파일 줄바꿈(CRLF일 수 있음)을 따른다.
    static string Normalize(string s) => s.ReplaceLineEndings("\n").Trim();
}

/// <summary>비교용 한 줄 표현.</summary>
static class PgFormat
{
    /// <summary>묶음: "테이블 연산 d깊이 n행수 원인 확신도 parent=부모테이블@d깊이 [트리거 후보]". 순번 순(byTable이면 테이블 이름순).</summary>
    public static string Firings(IReadOnlyList<Firing> firings, bool byTable = false)
    {
        var ordered = byTable ? firings.OrderBy(f => f.Table, StringComparer.Ordinal).ThenBy(f => f.CaptureSeq) : firings.OrderBy(f => f.CaptureSeq);
        return string.Join("\n", ordered.Select(f => Firing(f, firings)));
    }

    public static string Firing(Firing f, IReadOnlyList<Firing> all)
    {
        var parent = all.FirstOrDefault(p => p.Id == f.Cause?.ParentFiringId);
        return $"{f.Table} {f.Operation} d{f.Depth} n{f.RowCount} {f.Cause?.Kind} {f.Cause?.Confidence} "
               + $"parent={(parent is null ? "-" : $"{parent.Table}@d{parent.Depth}")} [{string.Join(",", f.Cause?.TriggerCandidates ?? [])}]";
    }

    public static string Summary(IEnumerable<NetSummaryRow> rows) => string.Join("\n", rows.OrderBy(r => r.Table, StringComparer.Ordinal)
        .Select(r => $"{r.Table} I{r.Inserted} U{r.Updated} D{r.Deleted} MR{r.MultisetRemoved} MA{r.MultisetAdded}"));

    /// <summary>최종 차이 한 건(설명 연결 ExplainedBy는 빼고 행 값만).</summary>
    public static string Net(NetChange n) =>
        $"{n.Table} {n.Change} x{n.Multiplicity} key({n.Key.Kind}:{string.Join(",", n.Key.Columns.Zip(n.Key.Values, (c, v) => $"{c}={v ?? "null"}"))}) "
        + $"before{Row(n.Before)} after{Row(n.After)} changed[{string.Join(",", n.ChangedColumns)}]";

    public static IEnumerable<string> NetLines(IEnumerable<NetChange> net) => net.Select(Net).Order(StringComparer.Ordinal);

    static string Row(IReadOnlyDictionary<string, string?>? row) => row is null ? "-"
        : "{" + string.Join(",", row.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value ?? "null"}")) + "}";

    public static string Status(SandboxStatus s) =>
        $"state={s.State} engine={s.Engine ?? "-"} covered={s.Manifest?.CoveredTables} excluded={s.Manifest?.ExcludedTables} auditNotLast={s.Manifest?.AuditNotLast} "
        + $"fingerprintMatch={s.FingerprintMatch} locked={s.Locked} lastError={s.LastError ?? "-"} origin={s.Origin?.Database ?? "-"} login={s.Origin?.Login ?? "-"}";
}

/// <summary>미리보기 저장소 자리(통합 테스트는 PreviewOutcome.Record를 바로 본다).</summary>
sealed class NullPreviewStore : IPreviewStore
{
    public Task SaveAsync(PreviewRecord record, CancellationToken ct) => Task.CompletedTask;
    public Task<PreviewRecord?> GetAsync(string previewId, CancellationToken ct) => Task.FromResult<PreviewRecord?>(null);
    public Task<RowPage<RowChange>?> PageEventsAsync(string previewId, RowPageQuery query, CancellationToken ct) => Task.FromResult<RowPage<RowChange>?>(null);
    public Task<RowPage<NetChange>?> PageNetAsync(string previewId, RowPageQuery query, CancellationToken ct) => Task.FromResult<RowPage<NetChange>?>(null);
    public Task<bool> ExportAsync(string previewId, Stream destination, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> RemoveAsync(string previewId, CancellationToken ct) => Task.FromResult(false);
}
