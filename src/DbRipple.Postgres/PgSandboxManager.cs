using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using Npgsql;

namespace DbRipple.Postgres;

/// <summary>
/// PostgreSQL 샌드박스 관리자(설계 결정 1). 상태 흐름은 SQL Server SandboxManager와 같다(building → ready/dirty/error, 미리보기 1건 잠금, 409 경로).
/// 기준선 = CREATE DATABASE 샌드박스 TEMPLATE 원본(원본에 다른 연결이 있으면 55006으로 실패하며, 원본 세션은 절대 끊지 않는다).
/// 스냅샷 DB는 없다(최종 차이는 감사 이미지로 계산). 매니페스트와 기준 시각은 샌드박스의 dbr_audit.meta에 둔다.
/// 샌드박스 DB가 이미 있으면 db-ripple이 만든 것(meta.owner = db-ripple)일 때만 지운다.
/// </summary>
public sealed class PgSandboxManager(PgOptions options, TimeProvider clock) : ISandboxManager
{
    public const string EngineName = "postgres";
    const string OwnerMark = "db-ripple";
    /// <summary>샌드박스 DB 주석(소유 표시). 이 주석이 있는 DB만 새로 고침 때 지운다.</summary>
    public const string OwnerComment = "db-ripple sandbox: 새로 고침 때 지우고 다시 만듭니다";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly HashSet<string> SystemDatabases = new(StringComparer.Ordinal) { "postgres", "template0", "template1" };

    readonly object gate = new();
    readonly SemaphoreSlim exclusive = new(1, 1);
    readonly SemaphoreSlim loadGate = new(1, 1);
    BaselineContext? context;
    bool loaded, building, previewLocked, dirty;
    Task? refreshTask;

    public PgOptions Options { get; } = options;
    /// <summary>마지막 구축·읽기 실패 원인(비밀값 없음).</summary>
    public string? LastError { get; private set; }
    /// <summary>미리보기 잠금 중이거나 구축 중.</summary>
    public bool IsBusy { get { lock (gate) return building || previewLocked; } }

    public async Task<SandboxStatus> GetStatusAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        lock (gate)
        {
            var c = context;
            return new SandboxStatus
            {
                State = building ? SandboxState.Building : c is null ? SandboxState.Error : dirty ? SandboxState.Dirty : SandboxState.Ready,
                BaselineAt = c?.BackupFinishedAt,
                FingerprintMatch = c?.FingerprintMatch ?? false,
                Locked = previewLocked,
                Manifest = c is null ? null : Summarize(c.Manifest),
                Origin = c is null ? null : new OriginInfo { Database = c.OriginDb, Login = c.OriginLogin },
                Engine = EngineName,
            };
        }
    }

    public async Task<BaselineContext> GetContextAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        lock (gate)
        {
            if (building) throw new PreviewConflictException(ConflictReason.Building, "기준선을 구축하는 중입니다.");
            return context ?? throw new PreviewConflictException(ConflictReason.NoBaseline, "기준선이 없습니다. 새로 고침으로 구축하세요.");
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        Task task;
        lock (gate) task = refreshTask ??= StartBuild();
        await task.WaitAsync(ct);
    }

    public void StartRefresh()
    {
        lock (gate) refreshTask ??= StartBuild();
    }

    public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (building) throw new PreviewConflictException(ConflictReason.Building, "기준선을 구축하는 중이라 미리보기를 시작할 수 없습니다.");
            if (!exclusive.Wait(0, ct)) throw new PreviewConflictException(ConflictReason.Locked, "다른 미리보기가 실행 중입니다.");
            previewLocked = true;
        }
        return Task.FromResult<IAsyncDisposable>(new Releaser(this));
    }

    public void MarkDirty(string reason)
    {
        lock (gate) dirty = true;
    }

    // gate 안에서만 부른다(SQL Server SandboxManager.StartBuild와 같은 규칙).
    Task StartBuild()
    {
        building = true;
        var task = Task.Run(async () =>
        {
            await exclusive.WaitAsync();
            try
            {
                var built = await BuildAsync(CancellationToken.None);
                lock (gate) { context = built; dirty = false; LastError = null; }
            }
            catch (Exception ex)
            {
                lock (gate) { context = null; LastError = Describe(ex); }
                throw;
            }
            finally
            {
                lock (gate) { building = false; loaded = true; refreshTask = null; }
                exclusive.Release();
            }
        });
        task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }

    async Task<BaselineContext> BuildAsync(CancellationToken ct)
    {
        Options.Validate();
        if (SystemDatabases.Contains(Options.SandboxDb)) throw new InvalidOperationException($"샌드박스 DB 이름으로 시스템 DB '{Options.SandboxDb}'를 쓸 수 없습니다.");
        var origin = PgNames.Quote(Options.OriginDb);
        var sandbox = PgNames.Quote(Options.SandboxDb);

        await using (var admin = new NpgsqlConnection(Admin()))
        {
            await admin.OpenAsync(ct);
            if (await DatabaseExistsAsync(admin, Options.SandboxDb, ct))
            {
                if (await CommentAsync(admin, Options.SandboxDb, ct) != OwnerComment)
                    throw new InvalidOperationException($"샌드박스 DB '{Options.SandboxDb}'가 이미 있지만 db-ripple이 만든 DB가 아닙니다. 지우지 않았습니다. 다른 이름을 쓰세요.");
                await Step(admin, $"DROP DATABASE {sandbox} WITH (FORCE)", ct);
            }
            try
            {
                await Step(admin, $"CREATE DATABASE {sandbox} TEMPLATE {origin}", ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse)
            {
                throw new InvalidOperationException(
                    $"원본 DB '{Options.OriginDb}'에 다른 연결이 있어 복제하지 못했습니다. TEMPLATE 복제는 원본에 연결이 없어야 합니다. 원본 연결을 닫고 새로 고침하세요(도구는 원본 세션을 끊지 않습니다).", ex);
            }
            // 소유 표시는 만든 직후 DB 주석으로 남긴다(구축이 중간에 실패해도 다음 새로 고침이 지울 수 있게). 주석은 TEMPLATE 복제로 따라오지 않는다.
            await Step(admin, $"COMMENT ON DATABASE {sandbox} IS {PgNames.Literal(OwnerComment)}", ct);
        }

        var at = clock.GetUtcNow();
        await using var conn = new NpgsqlConnection(PgPreviewExecutor.SandboxConnectionString(Options, Options.SandboxDb));
        await conn.OpenAsync(ct);
        var pg = await PgCatalogReader.ReadAsync(conn, ct);
        var (statements, manifest) = PgCapture.Plan(pg, at);

        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            if (Options.PreviewRole is { } role)
                foreach (var sql in PgCapture.PreviewRoleSql(role, pg.Tables.Select(t => t.Schema)))
                    await Step(conn, sql, ct, tx);
            await Step(conn, PgCapture.StoreSql(Options.PreviewRole), ct, tx);
            foreach (var sql in statements) await Step(conn, sql, ct, tx);
            await using (var meta = new NpgsqlCommand(
                "INSERT INTO dbr_audit.meta (k, v) VALUES ('owner', $1), ('baseline_at', $2), ('manifest', $3), ('origin_db', $4)", conn, tx))
            {
                meta.Parameters.Add(new() { Value = OwnerMark });
                meta.Parameters.Add(new() { Value = at.ToString("O") });
                meta.Parameters.Add(new() { Value = JsonSerializer.Serialize(manifest, Json) });
                meta.Parameters.Add(new() { Value = Options.OriginDb });
                await meta.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return await ContextAsync(conn, pg, manifest, at, ct);
    }

    async Task EnsureLoadedAsync(CancellationToken ct)
    {
        lock (gate) if (loaded || building) return;
        await loadGate.WaitAsync(ct);
        try
        {
            lock (gate) if (loaded || building) return;
            BaselineContext? c = null;
            string? error = null;
            try { c = await LoadExistingAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = Describe(ex); }
            lock (gate)
            {
                if (building) return;
                context = c;
                LastError = error;
                loaded = true;
            }
        }
        finally { loadGate.Release(); }
    }

    // 이미 있는 샌드박스에서 기준선을 읽는다. meta가 없으면 기준선 없음(error).
    async Task<BaselineContext?> LoadExistingAsync(CancellationToken ct)
    {
        Options.Validate();
        await using (var admin = new NpgsqlConnection(Admin()))
        {
            await admin.OpenAsync(ct);
            if (!await DatabaseExistsAsync(admin, Options.SandboxDb, ct))
                throw new InvalidOperationException($"샌드박스 DB '{Options.SandboxDb}'가 없습니다. 새로 고침으로 구축하세요.");
        }
        await using var conn = new NpgsqlConnection(PgPreviewExecutor.SandboxConnectionString(Options, Options.SandboxDb));
        await conn.OpenAsync(ct);
        var meta = await ReadMetaAsync(conn, ct);
        if (meta.GetValueOrDefault("owner") != OwnerMark || !meta.TryGetValue("manifest", out var manifestJson) || !meta.TryGetValue("baseline_at", out var atText))
            throw new InvalidOperationException("샌드박스에 db-ripple 기준선 정보가 없습니다. 새로 고침으로 구축하세요.");
        if (meta.GetValueOrDefault("origin_db") != Options.OriginDb)
            throw new InvalidOperationException("샌드박스가 다른 원본 DB로 만들어졌습니다. 새로 고침으로 다시 구축하세요.");
        var manifest = JsonSerializer.Deserialize<CaptureManifest>(manifestJson, Json) ?? throw new InvalidDataException("매니페스트를 읽을 수 없습니다");
        var pg = await PgCatalogReader.ReadAsync(conn, ct);
        return await ContextAsync(conn, pg, manifest, DateTimeOffset.Parse(atText, System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    async Task<BaselineContext> ContextAsync(NpgsqlConnection conn, PgCatalog pg, CaptureManifest manifest, DateTimeOffset at, CancellationToken ct) => new()
    {
        OriginDb = Options.OriginDb,
        SandboxDb = Options.SandboxDb,
        // 스냅샷 DB는 없다. 계약 필드라 샌드박스 이름을 둔다(PG 실행기는 쓰지 않는다).
        SnapshotDb = Options.SandboxDb,
        BackupFinishedAt = at,
        Fingerprint = await FingerprintAsync(conn, ct),
        // 같은 서버에서 TEMPLATE로 복제했으므로 서버 설정이 같다.
        FingerprintMatch = true,
        Catalog = pg.Catalog,
        Triggers = PgTriggerAnalyzer.Analyze(pg),
        Manifest = manifest,
        OriginLogin = LoginOf(Options.ServerConnectionString),
    };

    static async Task<Fingerprint> FingerprintAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT current_setting('server_version'), current_setting('server_version_num')::int / 10000,
                   (SELECT datcollate FROM pg_database WHERE datname = current_database())
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var major = r.GetInt32(1);
        return new Fingerprint(r.GetString(0), "PostgreSQL", major, IsRecursiveTriggersOn: false, NestedTriggers: 1, DisallowResultsFromTriggers: 0, SessionOptions: 0, r.GetString(2))
        {
            MajorVersion = major,
        };
    }

    static async Task<bool> DatabaseExistsAsync(NpgsqlConnection admin, string db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", admin) { Parameters = { new() { Value = db } } };
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    static async Task<string?> CommentAsync(NpgsqlConnection admin, string db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = $1", admin)
        {
            Parameters = { new() { Value = db } },
        };
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    static async Task<Dictionary<string, string>> ReadMetaAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var check = new NpgsqlCommand("SELECT to_regclass('dbr_audit.meta') IS NOT NULL", conn))
            if (await check.ExecuteScalarAsync(ct) is not true) return d;
        await using var cmd = new NpgsqlCommand("SELECT k, v FROM dbr_audit.meta", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    async Task Step(NpgsqlConnection conn, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
        Options.Log?.Invoke($"[pg] {sw.ElapsedMilliseconds}ms {FirstLine(sql)}");
    }

    static string FirstLine(string sql)
    {
        var line = sql.TrimStart().Split('\n')[0].Trim();
        return line.Length <= 120 ? line : line[..120];
    }

    string Admin() => new NpgsqlConnectionStringBuilder(Options.ServerConnectionString) { Pooling = false }.ConnectionString;

    /// <summary>표시용 로그인 이름(비밀값 없음).</summary>
    public static string? LoginOf(string connectionString)
    {
        try { return new NpgsqlConnectionStringBuilder(connectionString).Username; }
        catch (ArgumentException) { return null; }
    }

    static ManifestSummary Summarize(CaptureManifest m) => new()
    {
        CoveredTables = m.Tables.Count(t => t.Covered),
        ExcludedTables = m.ExcludedTables.Count,
        AuditNotLast = m.Tables.Count(t => t.AuditOrder.Insert == CaptureManifest.None || t.AuditOrder.Update == CaptureManifest.None || t.AuditOrder.Delete == CaptureManifest.None),
    };

    // 예외 → 상태 화면 문장. 비밀번호가 섞여 있으면 가린다.
    string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        try
        {
            var password = new NpgsqlConnectionStringBuilder(Options.ServerConnectionString).Password;
            if (!string.IsNullOrEmpty(password)) text = text.Replace(password, "***", StringComparison.Ordinal);
        }
        catch (ArgumentException) { }
        return text;
    }

    sealed class Releaser(PgSandboxManager owner) : IAsyncDisposable
    {
        int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                lock (owner.gate) owner.previewLocked = false;
                owner.exclusive.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
