using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Catalog;
using DbRipple.Preview;
using DbRipple.TestSupport;

namespace DbRipple.Api.Tests;

/// <summary>
/// C8 실행 시점 측면(SqlFact): dbrt_api_ CascadeLab 기준선(원본·샌드박스·스냅샷 한 벌)에서 매니페스트·카탈로그·트리거 분석·지문·상태·잠금·오염·새 로 고침.
/// 기준선은 클래스 안에서 한 번만 만들고(수 초) 테스트마다 매니저를 새로 만든다. 새로 고침 시험은 같은 기준선을 다시 만든다(결과는 같은 형태).
/// </summary>
public sealed class SandboxManagerTests : IClassFixture<SandboxManagerTests.Fixture>, IDisposable
{
    public sealed class Fixture : IAsyncLifetime
    {
        public TestBaseline? Baseline;

        public async Task InitializeAsync()
        {
            if (TestDb.IsConfigured) Baseline = await TestBaseline.BuildCascadeLabAsync("api");
        }

        public async Task DisposeAsync()
        {
            if (Baseline is not null) await Baseline.DisposeAsync();
        }
    }

    sealed class CountingTriggerAnalyzer : ITriggerAnalyzer
    {
        public int Calls;
        public DbCatalog? Seen;
        public IReadOnlyList<TriggerStaticInfo> Analyze(DbCatalog catalog)
        {
            Calls++;
            Seen = catalog;
            return [];
        }
    }

    readonly Fixture fx;
    readonly string dir = Path.Combine(Path.GetTempPath(), "dbrt_api_sbx_" + Guid.NewGuid().ToString("N")[..8]);

    public SandboxManagerTests(Fixture fixture)
    {
        fx = fixture;
        Directory.CreateDirectory(dir);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string ManifestPath => Path.Combine(dir, "manifest.json");

    SandboxManagerOptions Options(string? origin = null) => new()
    {
        Connections = ServerConnections.SingleServer(TestDb.ConnectionString()),
        OriginDb = origin ?? fx.Baseline!.Origin, SandboxDb = fx.Baseline!.Sandbox, SnapshotDb = fx.Baseline.Snapshot,
        BackupPath = fx.Baseline.BackupPath, ManifestPath = ManifestPath,
    };

    SandboxManager NewManager(CountingTriggerAnalyzer analyzer, SandboxManagerOptions? options = null) => new(options ?? Options(), analyzer, TimeProvider.System);

    Task WriteManifestAsync() => File.WriteAllTextAsync(ManifestPath, fx.Baseline!.Result.Install.Manifest.ToJson());

    [SqlFact]
    public async Task Loads_existing_baseline_manifest_catalog_triggers_and_fingerprint()
    {
        await WriteManifestAsync();
        var analyzer = new CountingTriggerAnalyzer();
        var sbx = NewManager(analyzer);

        var status = await sbx.GetStatusAsync(default);
        var expected = fx.Baseline!.Result.Install.Manifest;
        Assert.Equal(SandboxState.Ready, status.State);
        Assert.True(status.FingerprintMatch);
        Assert.False(status.Locked);
        Assert.Equal(expected.Tables.Count(t => t.Covered), status.Manifest!.CoveredTables);
        Assert.Equal(expected.ExcludedTables.Count, status.Manifest.ExcludedTables);
        Assert.True(status.Manifest.CoveredTables > 0);
        // 기준 시각: 사이드카가 없으니 스냅샷 DB 생성 시각(방금 만든 기준선)
        Assert.InRange(status.BaselineAt!.Value, DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now.AddMinutes(1));

        var ctx = await sbx.GetContextAsync(default);
        Assert.Equal(fx.Baseline.Origin, ctx.OriginDb);
        Assert.Equal(fx.Baseline.Sandbox, ctx.SandboxDb);
        Assert.Equal(fx.Baseline.Snapshot, ctx.SnapshotDb);
        Assert.True(ctx.FingerprintMatch);
        Assert.Equal(expected.Version, ctx.Manifest.Version); // 매니페스트는 파일에서(카탈로그로 다시 만들지 않는다)
        Assert.Equal(expected.Tables.Count, ctx.Manifest.Tables.Count);
        Assert.Contains(ctx.Catalog.Tables, t => t.Schema == "cl01"); // CascadeLab 샌드박스 카탈로그
        Assert.Contains(ctx.Catalog.Triggers, t => t.Name.StartsWith("dbr_audit_", StringComparison.Ordinal)); // 감사 트리거도 들어 있다
        Assert.Equal(1, analyzer.Calls);
        Assert.Same(ctx.Catalog, analyzer.Seen);

        await sbx.GetContextAsync(default);
        await sbx.GetStatusAsync(default);
        Assert.Equal(1, analyzer.Calls); // 기준선마다 한 번만 분석한다
    }

    [SqlFact]
    public async Task Baseline_time_comes_from_sidecar_file_when_present()
    {
        await WriteManifestAsync();
        var at = DateTimeOffset.Parse("2026-11-06T09:58:02+09:00");
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.baseline.json"), JsonSerializer.Serialize(new { backupFinishedAt = at }));
        Assert.Equal(at, (await NewManager(new CountingTriggerAnalyzer()).GetStatusAsync(default)).BaselineAt);
    }

    [SqlFact]
    public async Task Without_manifest_file_state_is_error_and_context_is_409_material_without_secrets()
    {
        var sbx = NewManager(new CountingTriggerAnalyzer());
        var status = await sbx.GetStatusAsync(default);
        Assert.Equal(SandboxState.Error, status.State);
        Assert.Null(status.BaselineAt);
        Assert.Null(status.Manifest);
        Assert.False(status.FingerprintMatch);
        var noBaseline = await Assert.ThrowsAsync<PreviewConflictException>(() => sbx.GetContextAsync(default));
        Assert.Equal(ConflictReason.NoBaseline, noBaseline.Reason);
        Assert.NotNull(sbx.LastError);
        Assert.DoesNotContain("Password", sbx.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [SqlFact]
    public async Task Preview_lock_is_exclusive_reports_locked_and_releases_on_dispose()
    {
        await WriteManifestAsync();
        var sbx = NewManager(new CountingTriggerAnalyzer());
        await using (await sbx.AcquirePreviewLockAsync(default))
        {
            Assert.True((await sbx.GetStatusAsync(default)).Locked);
            var busy = await Assert.ThrowsAsync<PreviewConflictException>(() => sbx.AcquirePreviewLockAsync(default)); // 기다리지 않고 거부
            Assert.Equal(ConflictReason.Locked, busy.Reason);
        }
        Assert.False((await sbx.GetStatusAsync(default)).Locked);
        var again = await sbx.AcquirePreviewLockAsync(default);
        await again.DisposeAsync();
        await again.DisposeAsync(); // 두 번 풀어도 다음 잠금을 깨뜨리지 않는다
        await using (await sbx.AcquirePreviewLockAsync(default))
            await Assert.ThrowsAsync<PreviewConflictException>(() => sbx.AcquirePreviewLockAsync(default));
    }

    [SqlFact]
    public async Task MarkDirty_sets_dirty_state_and_keeps_context()
    {
        await WriteManifestAsync();
        var sbx = NewManager(new CountingTriggerAnalyzer());
        Assert.Equal(SandboxState.Ready, (await sbx.GetStatusAsync(default)).State);
        sbx.MarkDirty("롤백 뒤 남은 변경");
        var status = await sbx.GetStatusAsync(default);
        Assert.Equal(SandboxState.Dirty, status.State);
        Assert.Equal("롤백 뒤 남은 변경", sbx.DirtyReason);
        Assert.NotNull(await sbx.GetContextAsync(default));
        Assert.True(status.FingerprintMatch);
    }

    [SqlFact]
    public async Task RefreshAsync_rebuilds_baseline_writes_manifest_and_clears_dirty()
    {
        var analyzer = new CountingTriggerAnalyzer();
        var sbx = NewManager(analyzer);
        sbx.MarkDirty("시험");
        Assert.False(File.Exists(ManifestPath));

        var before = DateTimeOffset.Now;
        await sbx.RefreshAsync(default);

        var status = await sbx.GetStatusAsync(default);
        Assert.Equal(SandboxState.Ready, status.State);
        Assert.Null(sbx.DirtyReason);
        Assert.True(status.FingerprintMatch);
        Assert.True(status.BaselineAt >= before.AddSeconds(-1)); // 백업 완료 시각 = 이번 재구축
        Assert.True(File.Exists(ManifestPath));
        Assert.True(File.Exists(Path.Combine(dir, "manifest.baseline.json")));
        Assert.Equal(1, analyzer.Calls);
        var ctx = await sbx.GetContextAsync(default);
        Assert.Equal(status.BaselineAt, ctx.BackupFinishedAt);
        Assert.Equal(ctx.Manifest.Version, JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(ManifestPath)).GetProperty("version").GetString());

        // 재시작해도 같은 기준 시각으로 읽는다(사이드카)
        var restarted = NewManager(new CountingTriggerAnalyzer());
        Assert.Equal(status.BaselineAt, (await restarted.GetStatusAsync(default)).BaselineAt);
    }

    [SqlFact]
    public async Task StartRefresh_returns_immediately_in_building_state_and_blocks_new_previews_until_done()
    {
        var sbx = NewManager(new CountingTriggerAnalyzer());
        sbx.StartRefresh();
        sbx.StartRefresh(); // 이미 building이면 아무것도 안 함

        var status = await sbx.GetStatusAsync(default);
        Assert.Equal(SandboxState.Building, status.State);
        Assert.Equal(ConflictReason.Building, (await Assert.ThrowsAsync<PreviewConflictException>(() => sbx.GetContextAsync(default))).Reason);
        Assert.Equal(ConflictReason.Building, (await Assert.ThrowsAsync<PreviewConflictException>(() => sbx.AcquirePreviewLockAsync(default))).Reason);

        await sbx.RefreshAsync(default); // 진행 중인 재구축에 합류해 끝까지 기다린다
        Assert.Equal(SandboxState.Ready, (await sbx.GetStatusAsync(default)).State);
        Assert.NotNull(await sbx.GetContextAsync(default));
    }

    [SqlFact]
    public async Task Refresh_waits_for_the_running_preview_to_release_the_lock()
    {
        await WriteManifestAsync();
        var sbx = NewManager(new CountingTriggerAnalyzer());
        var held = await sbx.AcquirePreviewLockAsync(default);
        sbx.StartRefresh();
        await Task.Delay(500);
        Assert.Equal(SandboxState.Building, (await sbx.GetStatusAsync(default)).State);
        Assert.True((await sbx.GetStatusAsync(default)).Locked); // 미리보기는 아직 실행 중, 재구축은 대기
        await held.DisposeAsync();
        await sbx.RefreshAsync(default);
        var status = await sbx.GetStatusAsync(default);
        Assert.Equal(SandboxState.Ready, status.State);
        Assert.False(status.Locked);
    }

    [SqlFact]
    public async Task Failed_refresh_is_error_state_with_last_error_and_the_next_refresh_recovers()
    {
        // 원본 이름이 없는 DB: 사전 검사(0단계)에서 멈추므로 어떤 DB도 바뀌지 않는다
        var missing = TestDb.NewName("api", "nope");
        var bad = NewManager(new CountingTriggerAnalyzer(), Options(missing));
        await Assert.ThrowsAnyAsync<Exception>(() => bad.RefreshAsync(default));
        var status = await bad.GetStatusAsync(default);
        Assert.Equal(SandboxState.Error, status.State);
        Assert.NotNull(bad.LastError);
        await Assert.ThrowsAsync<PreviewConflictException>(() => bad.GetContextAsync(default));
        Assert.False((await bad.GetStatusAsync(default)).Locked);
        await using (await bad.AcquirePreviewLockAsync(default)) { } // 실패한 재구축이 잠금을 쥐고 있지 않다

        var good = NewManager(new CountingTriggerAnalyzer());
        await good.RefreshAsync(default);
        Assert.Equal(SandboxState.Ready, (await good.GetStatusAsync(default)).State);
        Assert.Null(good.LastError);
    }

    [SqlFact]
    public async Task Environment_fingerprint_difference_is_reported_as_mismatch_and_blocks_nothing_by_itself()
    {
        // 지문 비교 자체: 같은 서버의 원본·샌드박스는 일치(위 시험들). 여기서는 RECURSIVE_TRIGGERS가 다른 DB를 원본으로 지정해 불일치를 만든다.
        await WriteManifestAsync();
        var other = TestDb.NewName("api", "fp");
        await using var cleanup = new TestDatabases(other);
        await TestDb.ExecAsync("master", $"CREATE DATABASE [{other}];", $"ALTER DATABASE [{other}] SET RECURSIVE_TRIGGERS ON;");
        var sbx = NewManager(new CountingTriggerAnalyzer(), Options(other));
        var status = await sbx.GetStatusAsync(default);
        Assert.Equal(SandboxState.Ready, status.State); // §5.1 6: state는 ready를 유지하고 미리보기만 막는다(파이프라인이 막음)
        Assert.False(status.FingerprintMatch);
        Assert.False((await sbx.GetContextAsync(default)).FingerprintMatch);
    }
}
