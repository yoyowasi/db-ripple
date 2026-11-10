using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;

namespace DbRipple.Preview;

/// <summary>샌드박스 관리자 구성(배포 설정). 연결 문자열은 비밀값이라 로그·예외 메시지·ToString에 넣지 않는다.</summary>
public sealed record SandboxManagerOptions
{
    /// <summary>서버 연결 문자열(DBRIPPLE_SQL_CONNECTION).</summary>
    public required string ConnectionString { get; init; }
    /// <summary>원본 역할 DB(AW_Origin).</summary>
    public required string OriginDb { get; init; }
    /// <summary>샌드박스(AW_Sandbox).</summary>
    public required string SandboxDb { get; init; }
    /// <summary>기준 스냅샷(AW_Sandbox_base).</summary>
    public required string SnapshotDb { get; init; }
    /// <summary>기준선 백업 파일 컨테이너 경로.</summary>
    public required string BackupPath { get; init; }
    /// <summary>기준선 구축 때 쓴 manifest.json 경로(호스트 파일). 재구축하면 다시 쓴다.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>비밀값을 뺀 표시.</summary>
    public override string ToString() => $"{OriginDb} → {SandboxDb} / {SnapshotDb}";
}

/// <summary>
/// 샌드박스 관리자 실행 시점 측면(§4 C8: 상태, 잠금, 오염, 새로 고침). 기준선 구축은 Core BaselineBuilder, 매니페스트는 ManifestPath에서 읽는다.
/// 트리거 정적 분석은 주입받은 ITriggerAnalyzer로 기준선마다 한 번 계산해 BaselineContext에 담는다.
/// <list type="bullet">
/// <item>상태: building(재구축 요청부터 끝까지, 실행 중인 미리보기가 있으면 그 끝을 기다리는 동안 포함) → ready, 오염 표시가 있으면 dirty,
/// 기준선이 없거나 구축·읽기에 실패했으면 error(원인은 LastError, 비밀값 없음).</item>
/// <item>첫 호출 때 한 번 ManifestPath와 샌드박스 DB에서 기존 기준선을 읽는다(DB가 없거나 매니페스트가 없으면 error). 그 뒤에는 Refresh만 상태를 바꾼다.</item>
/// <item>기준 시각은 재구축이 ManifestPath 옆 "*.baseline.json"에 적은 백업 완료 시각이다. 파일이 없으면 스냅샷 DB 생성 시각으로 대신한다.</item>
/// <item>재구축은 호출자의 취소와 무관하게 끝까지 한다(중간에 끊으면 샌드박스가 반쯤 복원된 채 남는다). 호출자의 취소는 기다림만 그만둔다.</item>
/// <item>RefreshAsync는 미리보기 잠금이 풀릴 때까지 기다리므로, 잠금을 쥔 채 부르면 교착한다. 파이프라인은 잠금 전에 부른다.</item>
/// </list>
/// </summary>
public sealed class SandboxManager(SandboxManagerOptions options, ITriggerAnalyzer triggerAnalyzer, TimeProvider clock) : ISandboxManager
{
    static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    readonly object gate = new();           // 아래 필드(상태)의 보호
    readonly SemaphoreSlim exclusive = new(1, 1); // 미리보기 1건 잠금 = 재구축과 상호 배제
    readonly SemaphoreSlim loadGate = new(1, 1);  // 첫 읽기 직렬화
    BaselineContext? context;
    bool loaded, building, previewLocked, dirty;
    string? dirtyReason;
    Task? refreshTask;

    /// <summary>구성.</summary>
    public SandboxManagerOptions Options { get; } = options;
    /// <summary>트리거 정적 분석기(C6).</summary>
    public ITriggerAnalyzer TriggerAnalyzer { get; } = triggerAnalyzer;
    /// <summary>시계.</summary>
    public TimeProvider Clock { get; } = clock;
    /// <summary>마지막 구축·읽기 실패 원인(비밀값 없음). 성공하면 null.</summary>
    public string? LastError { get; private set; }
    /// <summary>오염으로 표시된 사유(오염이 아니면 null).</summary>
    public string? DirtyReason { get { lock (gate) return dirty ? dirtyReason : null; } }

    string BaselineInfoPath => Path.ChangeExtension(Options.ManifestPath, ".baseline.json");

    /// <inheritdoc />
    public async Task<SandboxStatus> GetStatusAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        lock (gate)
        {
            var c = context;
            var state = building ? SandboxState.Building : c is null ? SandboxState.Error : dirty ? SandboxState.Dirty : SandboxState.Ready;
            return new SandboxStatus
            {
                State = state,
                BaselineAt = c?.BackupFinishedAt,
                FingerprintMatch = c?.FingerprintMatch ?? false,
                Locked = previewLocked,
                Manifest = c is null ? null : Summarize(c.Manifest),
            };
        }
    }

    /// <inheritdoc />
    public async Task<BaselineContext> GetContextAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        lock (gate)
        {
            if (building) throw new InvalidOperationException("기준선을 구축하는 중입니다.");
            return context ?? throw new InvalidOperationException("기준선이 없습니다. 새로 고침으로 구축하세요.");
        }
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken ct)
    {
        Task task;
        lock (gate) task = refreshTask ??= StartBuild();
        await task.WaitAsync(ct);
    }

    /// <inheritdoc />
    public void StartRefresh()
    {
        lock (gate) refreshTask ??= StartBuild();
    }

    /// <inheritdoc />
    public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (building) throw new InvalidOperationException("기준선을 구축하는 중이라 미리보기를 시작할 수 없습니다.");
            if (!exclusive.Wait(0, ct)) throw new InvalidOperationException("다른 미리보기가 실행 중입니다.");
            previewLocked = true;
        }
        return Task.FromResult<IAsyncDisposable>(new Releaser(this));
    }

    /// <inheritdoc />
    public void MarkDirty(string reason)
    {
        lock (gate)
        {
            dirty = true;
            dirtyReason = reason;
        }
    }

    // gate 안에서만 부른다. building을 바로 켜서 새 미리보기를 막고, 본 작업은 잠금을 얻은 뒤 한다.
    Task StartBuild()
    {
        building = true;
        var task = Task.Run(async () =>
        {
            await exclusive.WaitAsync();
            try
            {
                var built = await BuildAsync();
                lock (gate) { context = built; dirty = false; dirtyReason = null; LastError = null; }
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
        task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // StartRefresh로 시작해 기다리는 쪽이 없어도 예외를 관측한다
        return task;
    }

    async Task<BaselineContext> BuildAsync()
    {
        var o = new BaselineOptions(Options.OriginDb, Options.SandboxDb, Options.SnapshotDb, Options.BackupPath);
        BaselineResult result;
        await using (var steps = new SqlBaselineSteps(Options.ConnectionString))
            result = await BaselineBuilder.BuildAsync(o, steps, () => Clock.GetLocalNow(), CancellationToken.None);
        var manifest = result.Install.Manifest;
        var dir = Path.GetDirectoryName(Path.GetFullPath(Options.ManifestPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Options.ManifestPath, manifest.ToJson());
        await File.WriteAllTextAsync(BaselineInfoPath, JsonSerializer.Serialize(new BaselineInfoFile(result.BackupCompletedAt), ManifestJson));
        return await ReadContextAsync(manifest, result.BackupCompletedAt, CancellationToken.None);
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
                if (building) return; // 그 사이 재구축이 시작됐다
                context = c;
                LastError = error;
                loaded = true;
            }
        }
        finally { loadGate.Release(); }
    }

    // 매니페스트 파일 + 샌드박스 DB에서 기존 기준선을 읽는다. 매니페스트가 없으면 "기준선 없음"(error).
    async Task<BaselineContext?> LoadExistingAsync(CancellationToken ct)
    {
        if (!File.Exists(Options.ManifestPath)) throw new FileNotFoundException("매니페스트 파일이 없습니다: " + Options.ManifestPath);
        var manifest = JsonSerializer.Deserialize<CaptureManifest>(await File.ReadAllTextAsync(Options.ManifestPath, ct), ManifestJson)
            ?? throw new InvalidDataException("매니페스트를 읽을 수 없습니다");
        DateTimeOffset? at = null;
        if (File.Exists(BaselineInfoPath))
            at = JsonSerializer.Deserialize<BaselineInfoFile>(await File.ReadAllTextAsync(BaselineInfoPath, ct), ManifestJson)?.BackupFinishedAt;
        return await ReadContextAsync(manifest, at, ct);
    }

    // 샌드박스 카탈로그, 두 DB의 지문, 트리거 분석으로 문맥을 만든다. backupAt이 없으면 스냅샷 DB 생성 시각.
    async Task<BaselineContext> ReadContextAsync(CaptureManifest manifest, DateTimeOffset? backupAt, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(Options.ConnectionString) { Pooling = false };
        DbCatalog catalog;
        await using (var conn = new SqlConnection(new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = Options.SandboxDb }.ConnectionString))
            catalog = await CatalogReader.ReadAsync(conn, ct);
        Fingerprint origin, sandbox;
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = BaselineBuilder.Master }.ConnectionString))
        {
            await master.OpenAsync(ct);
            origin = await Fingerprint.ReadAsync(master, Options.OriginDb, ct);
            sandbox = await Fingerprint.ReadAsync(master, Options.SandboxDb, ct);
            backupAt ??= await SnapshotCreatedAtAsync(master, ct);
        }
        return new BaselineContext
        {
            OriginDb = Options.OriginDb,
            SandboxDb = Options.SandboxDb,
            SnapshotDb = Options.SnapshotDb,
            BackupFinishedAt = backupAt.Value,
            Fingerprint = sandbox,
            FingerprintMatch = origin == sandbox,
            Catalog = catalog,
            Triggers = TriggerAnalyzer.Analyze(catalog),
            Manifest = manifest,
        };
    }

    async Task<DateTimeOffset> SnapshotCreatedAtAsync(SqlConnection master, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT TODATETIMEOFFSET(create_date, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())) FROM sys.databases WHERE name = @db;", master);
        cmd.Parameters.Add(new SqlParameter("@db", System.Data.SqlDbType.NVarChar, 128) { Value = Options.SnapshotDb });
        var created = await cmd.ExecuteScalarAsync(ct);
        return created is DateTimeOffset d ? d : throw new InvalidOperationException($"기준 스냅샷 DB가 없습니다: {Options.SnapshotDb}");
    }

    static ManifestSummary Summarize(CaptureManifest m) => new()
    {
        CoveredTables = m.Tables.Count(t => t.Covered),
        ExcludedTables = m.ExcludedTables.Count,
        AuditNotLast = m.Tables.Count(t => t.AuditOrder.Insert == CaptureManifest.None || t.AuditOrder.Update == CaptureManifest.None || t.AuditOrder.Delete == CaptureManifest.None),
    };

    // 예외 → 상태 화면용 문장. 연결 문자열 값이 섞여 있으면 가린다.
    string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        var b = new SqlConnectionStringBuilder(Options.ConnectionString);
        return string.IsNullOrEmpty(b.Password) ? text : text.Replace(b.Password, "***", StringComparison.Ordinal);
    }

    sealed record BaselineInfoFile(DateTimeOffset BackupFinishedAt);

    sealed class Releaser(SandboxManager owner) : IAsyncDisposable
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
