using DbRipple.Contracts;
using DbRipple.Preview;

namespace DbRipple.Api.Tests;

/// <summary>파이프라인(§5.2 조립): 가짜 컴포넌트로 순서, 거부 시 저장 안 함, 오염·잠금·지문, sqlHash, netSummary, timings.</summary>
public sealed class PreviewPipelineTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dbrt_api_pipe_" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> calls = [];
    readonly FakeClock clock = new(TestData.Start);

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    sealed class FakeSandbox(List<string> calls) : ISandboxManager
    {
        public readonly List<string> Calls = calls;
        public SandboxState State = SandboxState.Ready;
        public bool Locked;
        public bool FingerprintMatch = true;
        public int RefreshCount, DirtyMarks;
        public string? DirtyReason;
        public Func<SandboxState>? OnRefresh;
        public Action? AfterAcquire;

        public Task<SandboxStatus> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new SandboxStatus { State = State, FingerprintMatch = FingerprintMatch, Locked = Locked });
        public Task<BaselineContext> GetContextAsync(CancellationToken ct)
        {
            Calls.Add("context");
            return Task.FromResult(TestData.Baseline(FingerprintMatch));
        }
        public Task RefreshAsync(CancellationToken ct)
        {
            Calls.Add(Locked ? "refresh-while-locked" : "refresh");
            RefreshCount++;
            State = OnRefresh?.Invoke() ?? SandboxState.Ready;
            return Task.CompletedTask;
        }
        public void StartRefresh() { }
        public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct)
        {
            if (Locked) throw new PreviewConflictException(ConflictReason.Locked, "다른 미리보기가 실행 중입니다.");
            Locked = true;
            Calls.Add("lock");
            AfterAcquire?.Invoke();
            return Task.FromResult<IAsyncDisposable>(new Release(this));
        }
        public void MarkDirty(string reason)
        {
            DirtyMarks++;
            DirtyReason = reason;
            State = SandboxState.Dirty;
            Calls.Add("markDirty");
        }

        sealed class Release(FakeSandbox s) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                s.Locked = false;
                s.Calls.Add("unlock");
                return ValueTask.CompletedTask;
            }
        }
    }

    sealed class FakeAnalyzer(List<string> calls, AnalysisResult result) : IBatchAnalyzer
    {
        public AnalysisResult Analyze(string sql, BaselineContext baseline)
        {
            calls.Add("analyze");
            return result;
        }
    }

    sealed class FakeExecutor(List<string> calls, Func<RawCapture> run) : IPreviewExecutor
    {
        public int Runs;
        public IEvaluationHook? HookSeen;
        public PreviewOptions? OptionsSeen;
        public Task<RawCapture> ExecuteAsync(AnalysisResult analysis, BaselineContext baseline, PreviewOptions options, IEvaluationHook? hook, CancellationToken ct)
        {
            calls.Add("execute");
            Runs++;
            HookSeen = hook;
            OptionsSeen = options;
            return Task.FromResult(run());
        }
    }

    sealed class FakeCollector(List<string> calls) : IChangeCollector
    {
        public CollectResult Result = new() { Firings = [TestData.Firing(1001) with { Cause = null, OrderGuarantee = null }], RowChanges = [TestData.Row("f-1001", 1)] };
        public RawCapture? RawSeen;
        public CollectResult Collect(RawCapture raw, AnalysisResult analysis, BaselineContext baseline)
        {
            calls.Add("collect");
            RawSeen = raw;
            return Result;
        }
    }

    sealed class FakeResolver(List<string> calls) : ICausalResolver
    {
        public ResolveInput? Input;
        public Resolution Resolution = new()
        {
            Model = new CausalModel { Firings = [TestData.Firing(1001)], RowChanges = [TestData.Row("f-1001", 1)] },
            Graph = new GraphDto(),
        };
        public Resolution Resolve(ResolveInput input)
        {
            calls.Add("resolve");
            Input = input;
            return Resolution;
        }
    }

    sealed class Rig
    {
        public required FakeSandbox Sandbox;
        public required FakeAnalyzer Analyzer;
        public required FakeExecutor Executor;
        public required FakeCollector Collector;
        public required FakeResolver Resolver;
        public required PreviewStore Store;
        public required PreviewPipeline Pipeline;
    }

    Rig NewRig(AnalysisResult? analysis = null, Func<RawCapture>? raw = null)
    {
        var sandbox = new FakeSandbox(calls);
        var analyzer = new FakeAnalyzer(calls, analysis ?? TestData.Accepted());
        var executor = new FakeExecutor(calls, raw ?? (() => TestData.Raw()));
        var collector = new FakeCollector(calls);
        var resolver = new FakeResolver(calls);
        var store = new PreviewStore(dir, clock);
        return new Rig
        {
            Sandbox = sandbox, Analyzer = analyzer, Executor = executor, Collector = collector, Resolver = resolver, Store = store,
            Pipeline = new PreviewPipeline(sandbox, analyzer, executor, collector, resolver, store, clock),
        };
    }

    [Fact]
    public async Task Runs_components_in_order_and_saves_the_assembled_record()
    {
        var rig = NewRig(TestData.Accepted("W_ONE"));
        var outcome = await rig.Pipeline.RunAsync("DELETE FROM dbo.T;", new PreviewOptions { ImageCap = 50 }, null, default);

        Assert.Equal(["lock", "context", "analyze", "execute", "collect", "resolve", "unlock"], calls);
        Assert.False(outcome.IsRejected);
        var result = outcome.Record!.Result;
        Assert.Matches("^pv_20261110_[0-9a-f]{8}$", result.PreviewId);
        Assert.Equal(PreviewStatus.Ok, result.Status);
        Assert.Equal(TestData.Start, result.CreatedAt);
        Assert.Equal("Sandbox", result.Baseline.SandboxDb);
        Assert.Equal(TestData.Fingerprint(), result.Baseline.Fingerprint);
        Assert.Equal("W_ONE", Assert.Single(result.Warnings).RuleId);
        Assert.Equal(50, rig.Executor.OptionsSeen!.ImageCap);
        Assert.Single(result.Statements);
        Assert.Equal("f-1001", Assert.Single(result.Firings).Id);
        Assert.NotNull(result.Firings[0].Cause); // C13 결과를 그대로 싣는다
        Assert.Equal(11, result.TimingsMs.Execute);
        Assert.Equal(12, result.TimingsMs.ReadLog);
        Assert.Equal(13, result.TimingsMs.Reread);
        Assert.Equal(14, result.TimingsMs.NetDiff);
        Assert.Equal("DELETE FROM dbo.T;", outcome.Record.Sql);
        Assert.Single(outcome.Record.RowChanges);

        var stored = await rig.Store.GetAsync(result.PreviewId, default);
        Assert.Equal(result.PreviewId, stored!.Result.PreviewId);
        Assert.Equal(result.TimingsMs, stored.Result.TimingsMs);
    }

    [Fact]
    public async Task Resolver_gets_statements_collected_net_and_baseline()
    {
        var net = new[] { TestData.Net("dbo.T", NetChangeKind.Deleted) };
        var rig = NewRig(raw: () => TestData.Raw(net: net));
        await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);
        Assert.Same(rig.Collector.Result, rig.Resolver.Input!.Collected);
        Assert.Equal(net, rig.Resolver.Input.Net);
        Assert.Equal("Origin", rig.Resolver.Input.Baseline.OriginDb);
        Assert.Single(rig.Resolver.Input.Statements);
    }

    [Fact]
    public async Task Rejected_analysis_returns_without_executing_or_saving_and_releases_the_lock()
    {
        var rig = NewRig(TestData.Rejected());
        var outcome = await rig.Pipeline.RunAsync("TRUNCATE TABLE t;", new PreviewOptions(), null, default);

        Assert.True(outcome.IsRejected);
        Assert.Null(outcome.Record);
        Assert.Equal("REJECT_TRUNCATE", outcome.Analysis.Rejects[0].RuleId);
        Assert.Equal(0, rig.Executor.Runs);
        Assert.DoesNotContain("collect", calls);
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        Assert.False(rig.Sandbox.Locked);
    }

    [Fact]
    public async Task Dirty_sandbox_is_rebuilt_before_the_lock_is_taken()
    {
        var rig = NewRig();
        rig.Sandbox.State = SandboxState.Dirty;
        await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);
        Assert.Equal(["refresh", "lock", "context", "analyze", "execute", "collect", "resolve", "unlock"], calls);
        Assert.Equal(1, rig.Sandbox.RefreshCount);
    }

    [Fact]
    public async Task Dirtied_between_check_and_lock_releases_and_retries_instead_of_deadlocking()
    {
        var rig = NewRig();
        rig.Sandbox.State = SandboxState.Dirty;
        var first = true;
        rig.Sandbox.AfterAcquire = () => { if (first) { first = false; rig.Sandbox.State = SandboxState.Dirty; } };
        await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);
        Assert.Equal(["refresh", "lock", "unlock", "refresh", "lock", "context", "analyze", "execute", "collect", "resolve", "unlock"], calls);
        Assert.DoesNotContain("refresh-while-locked", calls);
    }

    [Fact]
    public async Task Persistently_dirty_sandbox_gives_up_with_conflict_dirty_and_holds_no_lock()
    {
        var rig = NewRig();
        rig.Sandbox.State = SandboxState.Dirty;
        rig.Sandbox.OnRefresh = () => SandboxState.Dirty;
        var ex = await Assert.ThrowsAsync<PreviewConflictException>(() => rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default));
        Assert.Equal(ConflictReason.Dirty, ex.Reason);
        Assert.Equal(PreviewPipeline.MaxRefreshAttempts, rig.Sandbox.RefreshCount);
        Assert.False(rig.Sandbox.Locked);
        Assert.Equal(0, rig.Executor.Runs);
    }

    [Fact]
    public async Task Lock_conflict_is_conflict_locked()
    {
        var rig = NewRig();
        rig.Sandbox.Locked = true;
        var ex = await Assert.ThrowsAsync<PreviewConflictException>(() => rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default));
        Assert.Equal(ConflictReason.Locked, ex.Reason);
        Assert.True(rig.Sandbox.Locked); // 남의 잠금을 풀지 않는다
        Assert.Equal(0, rig.Executor.Runs);
    }

    [Fact]
    public async Task Fingerprint_mismatch_is_blocked_and_releases_the_lock()
    {
        var rig = NewRig();
        rig.Sandbox.FingerprintMatch = false;
        var ex = await Assert.ThrowsAsync<PreviewConflictException>(() => rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default));
        Assert.Equal(ConflictReason.FingerprintMismatch, ex.Reason);
        Assert.False(rig.Sandbox.Locked);
        Assert.DoesNotContain("analyze", calls);
    }

    [Fact]
    public async Task Sandbox_dirty_flag_from_executor_marks_dirty_and_failed_status_is_kept()
    {
        var residual = new[] { TestData.Net("dbo.ErrorLog", NetChangeKind.Inserted) };
        var rig = NewRig(raw: () => TestData.Raw(PreviewStatus.Failed, dirty: true, residual: residual));
        var outcome = await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);

        Assert.Equal(1, rig.Sandbox.DirtyMarks);
        Assert.Contains("1건", rig.Sandbox.DirtyReason);
        Assert.Equal(PreviewStatus.Failed, outcome.Record!.Result.Status);
        Assert.Equal(residual, outcome.Record.Result.ResidualCommitted);
        Assert.Equal(["lock", "context", "analyze", "execute", "markDirty", "collect", "resolve", "unlock"], calls);
        Assert.NotNull(await rig.Store.GetAsync(outcome.Record.Result.PreviewId, default)); // 실패 결과도 저장(거부만 저장 안 함)
    }

    [Fact]
    public async Task Residual_check_failure_is_carried_into_the_result_and_marks_dirty_once()
    {
        var rig = NewRig(raw: () => TestData.Raw(PreviewStatus.Failed, dirty: true, residualCheckFailed: true, residualCheckMessage: "8115: 산술 오버플로"));
        var outcome = await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);

        var result = outcome.Record!.Result;
        Assert.True(result.ResidualCheckFailed);
        Assert.Equal("8115: 산술 오버플로", result.ResidualCheckMessage);
        Assert.Equal(PreviewStatus.Failed, result.Status);
        Assert.Equal(1, rig.Sandbox.DirtyMarks); // SandboxDirty와 겹쳐도 한 번만
        Assert.Contains("8115", rig.Sandbox.DirtyReason);
        Assert.Equal(["lock", "context", "analyze", "execute", "markDirty", "collect", "resolve", "unlock"], calls);
        var stored = await rig.Store.GetAsync(result.PreviewId, default);
        Assert.True(stored!.Result.ResidualCheckFailed);
    }

    [Fact]
    public async Task Residual_check_failure_marks_dirty_even_without_the_sandbox_dirty_flag()
    {
        var rig = NewRig(raw: () => TestData.Raw(PreviewStatus.Failed, residualCheckFailed: true, residualCheckMessage: "x"));
        await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);
        Assert.Equal(1, rig.Sandbox.DirtyMarks);
    }

    [Fact]
    public async Task Normal_result_has_no_residual_check_failure()
    {
        var rig = NewRig();
        var outcome = await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);
        Assert.False(outcome.Record!.Result.ResidualCheckFailed);
        Assert.Null(outcome.Record.Result.ResidualCheckMessage);
        Assert.Equal(0, rig.Sandbox.DirtyMarks);
    }

    [Fact]
    public void SqlHash_and_Summarize_delegate_to_the_shared_contract_formulas()
    {
        Assert.Equal(ContractFormulas.SqlHash("abc"), PreviewPipeline.SqlHash("abc"));
        var net = new[] { TestData.Net("sales.B", NetChangeKind.Inserted, "1"), TestData.Net("dbo.H", NetChangeKind.MultisetAdded, "a", multiplicity: 2) };
        Assert.Equal(ContractFormulas.Summarize(net), PreviewPipeline.Summarize(net));
    }

    [Fact]
    public async Task Executor_exception_marks_dirty_rethrows_and_releases_the_lock_without_saving()
    {
        var rig = NewRig(raw: () => throw new IOException("연결 끊김"));
        await Assert.ThrowsAsync<IOException>(() => rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default));
        Assert.Equal(1, rig.Sandbox.DirtyMarks);
        Assert.False(rig.Sandbox.Locked);
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
    }

    [Fact]
    public async Task Hook_is_passed_through_to_the_executor()
    {
        var rig = NewRig();
        var hook = new NoHook();
        await rig.Pipeline.RunAsync("x", new PreviewOptions(), hook, default);
        Assert.Same(hook, rig.Executor.HookSeen);
    }

    sealed class NoHook : IEvaluationHook
    {
        public Task OnBeforeRollbackAsync(EvaluationHookContext context, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Second_preview_replaces_the_first_in_the_store()
    {
        var rig = NewRig();
        var first = (await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default)).Record!.Result.PreviewId;
        var second = (await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default)).Record!.Result.PreviewId;
        Assert.NotEqual(first, second);
        Assert.Null(await rig.Store.GetAsync(first, default));
        Assert.NotNull(await rig.Store.GetAsync(second, default));
    }

    [Fact]
    public void SqlHash_is_sha256_of_utf8_without_normalization()
    {
        Assert.Equal("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PreviewPipeline.SqlHash("abc"));
        // 공백·줄바꿈·유니코드 정규화를 하지 않는다: 줄 끝 CRLF와 LF는 다른 해시, 조합형·분해형 한글도 다른 해시
        Assert.NotEqual(PreviewPipeline.SqlHash("a\r\nb"), PreviewPipeline.SqlHash("a\nb"));
        var composed = char.ConvertFromUtf32(0xAC00); // 가(조합형)
        var decomposed = char.ConvertFromUtf32(0x1100) + char.ConvertFromUtf32(0x1161); // ㄱ+ㅏ(분해형)
        Assert.NotEqual(PreviewPipeline.SqlHash(composed), PreviewPipeline.SqlHash(decomposed));
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(composed))), PreviewPipeline.SqlHash(composed));
    }

    [Fact]
    public async Task Result_sql_hash_is_the_hash_of_the_original_text()
    {
        var rig = NewRig();
        const string sql = "DELETE FROM dbo.T;\r\n-- 주석\r\n";
        var outcome = await rig.Pipeline.RunAsync(sql, new PreviewOptions(), null, default);
        Assert.Equal(PreviewPipeline.SqlHash(sql), outcome.Record!.Result.SqlHash);
    }

    [Fact]
    public void NetSummary_counts_per_table_and_sums_multiplicity_for_multisets()
    {
        var net = new[]
        {
            TestData.Net("sales.B", NetChangeKind.Inserted, "1"),
            TestData.Net("sales.B", NetChangeKind.Inserted, "2"),
            TestData.Net("sales.B", NetChangeKind.Updated, "3"),
            TestData.Net("dbo.A", NetChangeKind.Deleted, "1"),
            TestData.Net("dbo.H", NetChangeKind.MultisetRemoved, "r", multiplicity: 3),
            TestData.Net("dbo.H", NetChangeKind.MultisetAdded, "a", multiplicity: 2),
        };
        var rows = PreviewPipeline.Summarize(net);
        Assert.Equal(["dbo.A", "dbo.H", "sales.B"], rows.Select(r => r.Table).ToArray());
        var a = rows[0]; var h = rows[1]; var b = rows[2];
        Assert.Equal((0, 0, 1, 0, 0), (a.Inserted, a.Updated, a.Deleted, a.MultisetRemoved, a.MultisetAdded));
        Assert.Equal((0, 0, 0, 3, 2), (h.Inserted, h.Updated, h.Deleted, h.MultisetRemoved, h.MultisetAdded));
        Assert.Equal((2, 1, 0, 0, 0), (b.Inserted, b.Updated, b.Deleted, b.MultisetRemoved, b.MultisetAdded));
    }

    [Fact]
    public async Task NetSummary_in_the_result_comes_from_the_resolved_net_changes()
    {
        var rig = NewRig();
        rig.Resolver.Resolution = rig.Resolver.Resolution with
        {
            Model = rig.Resolver.Resolution.Model with { NetChanges = [TestData.Net("dbo.T", NetChangeKind.Deleted, "1", 1, "f-1001"), TestData.Net("dbo.T", NetChangeKind.Deleted, "2", 1, "f-1001")] },
        };
        var outcome = await rig.Pipeline.RunAsync("x", new PreviewOptions(), null, default);
        var row = Assert.Single(outcome.Record!.Result.NetSummary);
        Assert.Equal(("dbo.T", 2L), (row.Table, row.Deleted));
        Assert.Equal(2, outcome.Record.NetChanges.Count);
    }
}
