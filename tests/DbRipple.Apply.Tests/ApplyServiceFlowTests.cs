using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using Microsoft.Data.SqlClient;
using static DbRipple.Apply.Tests.TestData;

namespace DbRipple.Apply.Tests;

/// <summary>
/// 적용 서비스 흐름 단위 테스트(DB 없음). 원본 서버 작업(IOriginDatabase), 실행 봉투, 최종 차이, 저장소, 샌드박스를 가짜로 바꾸고
/// 호출 순서를 한 목록(log)에 적어 §5.3 절차와 finally 정리를 확인한다.
/// </summary>
public sealed class ApplyServiceFlowTests
{
    readonly List<string> log = [];

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class FakeStore(List<string> log, PreviewRecord? record) : IPreviewStore
    {
        public PreviewRecord? Record = record;
        public Task SaveAsync(PreviewRecord r, CancellationToken ct) { Record = r; return Task.CompletedTask; }
        public Task<PreviewRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Record?.Result.PreviewId == id ? Record : null);
        public Task<RowPage<RowChange>?> PageEventsAsync(string id, RowPageQuery q, CancellationToken ct) => Task.FromResult<RowPage<RowChange>?>(null);
        public Task<RowPage<NetChange>?> PageNetAsync(string id, RowPageQuery q, CancellationToken ct) => Task.FromResult<RowPage<NetChange>?>(null);
        public Task<bool> ExportAsync(string id, Stream destination, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RemoveAsync(string id, CancellationToken ct)
        {
            log.Add("remove");
            var had = Record?.Result.PreviewId == id;
            Record = null;
            return Task.FromResult(had);
        }
    }

    sealed class FakeSandbox(List<string> log, BaselineContext context) : ISandboxManager
    {
        public Task<SandboxStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BaselineContext> GetContextAsync(CancellationToken ct) { log.Add("context"); return Task.FromResult(context); }
        public Task RefreshAsync(CancellationToken ct) => throw new NotSupportedException();
        public void StartRefresh() => log.Add("refresh");
        public void MarkDirty(string reason) => log.Add("dirty");
        public void MarkStale(string reason) => log.Add("stale");
        public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct)
        {
            log.Add("lock");
            return Task.FromResult<IAsyncDisposable>(new Unlock(log));
        }

        sealed class Unlock(List<string> log) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { log.Add("unlock"); return ValueTask.CompletedTask; }
        }
    }

    sealed class FakeAnalyzer(List<string> log) : IBatchAnalyzer
    {
        public Func<string, AnalysisResult>? Result;
        public AnalysisResult Analyze(string sql, BaselineContext baseline)
        {
            log.Add("analyze");
            return Result?.Invoke(sql) ?? new AnalysisResult { Statements = [Analyzed(Statement(1, 1))] };
        }
    }

    sealed class FakeRunner(List<string> log) : IStatementRunner
    {
        public Func<AnalyzedStatement, StatementRunOutcome>? Outcome;
        public StatementRunContext? Context;
        public Task PrepareAsync(SqlSession session, CancellationToken ct) { log.Add("prepare"); return Task.CompletedTask; }
        public Task<StatementRunOutcome> RunAsync(SqlSession session, AnalyzedStatement statement, StatementRunContext context, CancellationToken ct)
        {
            log.Add($"run {statement.Index}");
            Context = context;
            return Task.FromResult(Outcome?.Invoke(statement) ?? new StatementRunOutcome
            {
                Result = Statement(statement.Index, 1, statement.Target, statement.Kind),
                TransactionOpen = true,
            });
        }
    }

    sealed class FakeDiffer(List<string> log) : INetDiffer
    {
        public readonly Queue<Func<IReadOnlyList<NetChange>>> Results = new();
        public Task<IReadOnlyList<NetChange>> DiffAsync(SqlSession session, string liveDb, string snapshotDb, IReadOnlyList<TableInfo> tables, CancellationToken ct)
        {
            log.Add($"diff {liveDb} {snapshotDb}");
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue()() : (IReadOnlyList<NetChange>)[]);
        }
    }

    sealed class FakeConnection(List<string> log) : IOriginConnection
    {
        public IReadOnlyList<DriftRow> Drift = [];
        public Exception? CommitError;
        public IReadOnlyList<ApplyMismatch> KeysetMismatches = [];
        public SqlSession Session { get; } = new() { Connection = new SqlConnection() };
        public Task<long?> BeginTransactionAsync(CancellationToken ct) { log.Add("begin"); return Task.FromResult<long?>(42); }
        public Task CommitAsync(CancellationToken ct)
        {
            log.Add("commit");
            return CommitError is null ? Task.CompletedTask : Task.FromException(CommitError);
        }
        public Task RollbackAsync(CancellationToken ct) { log.Add("rollback"); return Task.CompletedTask; }
        public Task<IReadOnlyList<DriftRow>> CheckDriftAsync(PreviewRecord preview, IReadOnlyList<TableInfo> tables, CancellationToken ct) { log.Add("drift"); return Task.FromResult(Drift); }
        public Task<IReadOnlyDictionary<string, long>> CountAllAsync(IReadOnlyList<TableInfo> tables, CancellationToken ct)
        {
            log.Add("count");
            return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["dbo.T"] = 3 });
        }
        public Task<(IReadOnlyList<TableCountChange> Counts, IReadOnlyList<ApplyMismatch> Mismatches)> VerifyKeysetAsync(
            PreviewRecord preview, IReadOnlyDictionary<string, long> before, IReadOnlyList<TableInfo> tables, BaselineContext baseline, CancellationToken ct)
        {
            log.Add("keyset");
            return Task.FromResult<(IReadOnlyList<TableCountChange>, IReadOnlyList<ApplyMismatch>)>(([], KeysetMismatches));
        }
        public ValueTask DisposeAsync() { log.Add("close"); return ValueTask.CompletedTask; }
    }

    sealed class FakeOrigin(List<string> log) : IOriginDatabase
    {
        public Fingerprint Fingerprint = TestData.Fingerprint();
        public bool UserExists = true;
        public (SnapshotOutcome, string?) Snapshot = (SnapshotOutcome.Created, null);
        public SnapshotCapability Capability = new(true, null);
        public readonly FakeConnection Connection = new(log);
        public Task<SnapshotCapability> CheckSnapshotCapabilityAsync(string originDb, CancellationToken ct) => Task.FromResult(Capability);
        public Exception? Unreachable;
        public Task<Fingerprint> ReadFingerprintAsync(string originDb, CancellationToken ct) =>
            Unreachable is { } ex ? Task.FromException<Fingerprint>(ex) : Task.FromResult(Fingerprint);
        public Task<bool> UserExistsAsync(string originDb, string user, CancellationToken ct) => Task.FromResult(UserExists);
        public Task<(SnapshotOutcome Outcome, string? Reason)> CreateSnapshotAsync(string originDb, string snapshotDb, CancellationToken ct) { log.Add($"snapshot {snapshotDb}"); return Task.FromResult(Snapshot); }
        public Task DropSnapshotAsync(string originDb, string snapshotDb, CancellationToken ct) { log.Add($"drop {snapshotDb}"); return Task.CompletedTask; }
        public Task<IOriginConnection> OpenAsync(string originDb, CancellationToken ct) { log.Add("open"); return Task.FromResult<IOriginConnection>(Connection); }
    }

    sealed class Rig
    {
        public required FakeStore Store;
        public required FakeSandbox Sandbox;
        public required FakeAnalyzer Analyzer;
        public required FakeRunner Runner;
        public required FakeDiffer Differ;
        public required FakeOrigin Origin;
        public required Clock Clock;
        public required ApplyService Service;

        public Task<ApplyResult?> ApplyAsync(ApplyRequest? request = null) =>
            Service.ApplyAsync("pv_20261111_0001", request ?? new ApplyRequest(), CancellationToken.None);
    }

    Rig NewRig(PreviewRecord? record = null, ApplySettings? settings = null, BaselineContext? context = null)
    {
        record ??= Record();
        var store = new FakeStore(log, record);
        var sandbox = new FakeSandbox(log, context ?? Context());
        var analyzer = new FakeAnalyzer(log);
        var runner = new FakeRunner(log);
        var differ = new FakeDiffer(log);
        var origin = new FakeOrigin(log);
        var clock = new Clock(Start.AddMinutes(5));
        // 기본: 원본 최종 차이가 미리보기와 같다
        differ.Results.Enqueue(() => record.NetChanges);
        var service = new ApplyService(origin, settings ?? new ApplySettings { ExecuteAsUser = PreviewUserSql.UserName }, store, sandbox, analyzer, runner, differ, clock);
        return new Rig { Store = store, Sandbox = sandbox, Analyzer = analyzer, Runner = runner, Differ = differ, Origin = origin, Clock = clock, Service = service };
    }

    void AssertNothingRanOnOrigin()
    {
        Assert.DoesNotContain(log, e => e.StartsWith("snapshot", StringComparison.Ordinal));
        Assert.DoesNotContain("open", log);
        Assert.DoesNotContain(log, e => e.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Committed_path_follows_design_order_and_refreshes_after_unlock()
    {
        var rig = NewRig();

        var result = await rig.ApplyAsync(new ApplyRequest { SqlHash = ContractFormulas.SqlHash(Sql) });

        Assert.NotNull(result);
        Assert.Equal(ApplyStatus.Committed, result.Status);
        Assert.Equal(VerificationMode.SnapshotNetdiff, result.Verification.Mode);
        Assert.True(result.Verification.NetSummaryMatch);
        Assert.True(result.SandboxRefreshStarted);
        Assert.Equal(
            ["lock", "context", "analyze", "snapshot Origin_pre", "open", "prepare", "begin", "drift", "run 1", "diff Origin Origin_pre", "commit", "remove",
             "rollback", "close", "drop Origin_pre", "unlock", "refresh"],
            log);
        Assert.Equal(42, rig.Runner.Context!.TransactionId);
        Assert.Equal(PreviewUserSql.UserName, rig.Runner.Context.ExecuteAsUser);
    }

    [Fact]
    public async Task Committed_preview_is_not_applied_twice()
    {
        var rig = NewRig();
        var record = rig.Store.Record!;
        await rig.ApplyAsync();
        rig.Store.Record = record; // 저장소가 지우지 못했다고 가정

        Assert.Null(await rig.ApplyAsync());
    }

    [Fact]
    public async Task Missing_preview_is_null()
    {
        var rig = NewRig();
        rig.Store.Record = null;

        Assert.Null(await rig.ApplyAsync());
        Assert.DoesNotContain("lock", log);
    }

    [Fact]
    public async Task Precondition_preview_not_ok_includes_truncated()
    {
        var rig = NewRig(Record(status: PreviewStatus.Truncated, truncated: true));

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.PreconditionFailed, result!.Status);
        Assert.Equal([ApplyPrecondition.PreviewNotOk], result.PreconditionFailures);
        AssertNothingRanOnOrigin();
        Assert.Equal("unlock", log[^1]);
    }

    [Fact]
    public async Task Precondition_sql_hash_mismatch_from_request()
    {
        var rig = NewRig();

        var result = await rig.ApplyAsync(new ApplyRequest { SqlHash = ContractFormulas.SqlHash("DELETE FROM dbo.T;") });

        Assert.Equal([ApplyPrecondition.SqlHashMismatch], result!.PreconditionFailures);
        AssertNothingRanOnOrigin();
    }

    [Fact]
    public async Task Precondition_sql_hash_mismatch_from_reanalysis()
    {
        var rig = NewRig();
        rig.Analyzer.Result = _ => new AnalysisResult { Statements = [Analyzed(Statement(1, 1)), Analyzed(Statement(2, 1))] };

        var result = await rig.ApplyAsync();

        Assert.Equal([ApplyPrecondition.SqlHashMismatch], result!.PreconditionFailures);
        Assert.Contains("재분석", result.Message);
        AssertNothingRanOnOrigin();
    }

    [Fact]
    public async Task Precondition_preview_expired()
    {
        var rig = NewRig();
        rig.Clock.Now = Start.AddMinutes(31);

        var result = await rig.ApplyAsync();

        Assert.Equal([ApplyPrecondition.PreviewExpired], result!.PreconditionFailures);
        AssertNothingRanOnOrigin();
    }

    [Fact]
    public async Task Precondition_fingerprint_mismatch()
    {
        var rig = NewRig();
        rig.Origin.Fingerprint = Fingerprint("16.0.4000.1");

        var result = await rig.ApplyAsync();

        Assert.Equal([ApplyPrecondition.FingerprintMismatch], result!.PreconditionFailures);
        AssertNothingRanOnOrigin();
    }

    [Fact]
    public async Task Precondition_execute_as_user_missing()
    {
        var rig = NewRig();
        rig.Origin.UserExists = false;

        var result = await rig.ApplyAsync();

        Assert.Equal([ApplyPrecondition.ExecuteAsUserMissing], result!.PreconditionFailures);
        AssertNothingRanOnOrigin();
    }

    [Fact]
    public async Task Drift_rolls_back_without_running_statements()
    {
        var rig = NewRig();
        rig.Origin.Connection.Drift = [new DriftRow { Table = "dbo.T", Key = Key("1"), Kind = DriftKind.Changed, Columns = ["v"] }];

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.DriftDetected, result!.Status);
        Assert.Single(result.Verification.DriftRows);
        Assert.DoesNotContain(log, e => e.StartsWith("run", StringComparison.Ordinal));
        Assert.DoesNotContain("commit", log);
        Assert.Contains("rollback", log);
        Assert.Contains("drop Origin_pre", log);
        Assert.DoesNotContain("refresh", log);
        Assert.False(result.SandboxRefreshStarted);
        Assert.NotNull(rig.Store.Record); // 미리보기는 남겨 둔다(Q36)
    }

    [Fact]
    public async Task Mismatch_rolls_back_and_keeps_preview()
    {
        var rig = NewRig();
        rig.Differ.Results.Clear();
        rig.Differ.Results.Enqueue(() => [Updated("dbo.T", "1", Row(("id", "1"), ("v", "1")), Row(("id", "1"), ("v", "9")), "v")]);

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.RolledBackMismatch, result!.Status);
        Assert.Contains(result.Verification.Mismatches, m => m.Kind == MismatchKind.RowValue);
        Assert.DoesNotContain("commit", log);
        Assert.Contains("rollback", log);
        Assert.DoesNotContain("refresh", log);
        Assert.DoesNotContain("remove", log);
    }

    [Fact]
    public async Task Error_checks_residual_and_corrects_unconfirmed_3609()
    {
        var record = Record(statements: [Statement(1, 1), Statement(2, 1)]);
        var rig = NewRig(record);
        rig.Analyzer.Result = _ => new AnalysisResult { Statements = [Analyzed(Statement(1, 1)), Analyzed(Statement(2, 1))] };
        rig.Runner.Outcome = st => new StatementRunOutcome
        {
            Result = Statement(st.Index, null, status: StatementStatus.TriggerBlocked) with
            {
                Error = new StatementError { ErrorNumber = 3609, Message = "트랜잭션이 트리거에서 끝남", IsToolFault = false },
            },
            TransactionOpen = false,
            TriggerRollbackConfirmed = false,
        };
        rig.Differ.Results.Clear();
        rig.Differ.Results.Enqueue(() => [Inserted("dbo.X", "1", Row(("id", "1")))]); // 롤백 뒤 남은 변경

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.Equal(StatementStatus.Failed, result.Statements[0].Status);       // 3609 보정
        Assert.Equal(StatementStatus.NotRun, result.Statements[1].Status);
        Assert.Single(result.Verification.ResidualCommitted);
        Assert.False(result.Verification.ResidualCheckFailed);
        Assert.True(result.SandboxRefreshStarted);
        Assert.DoesNotContain("commit", log);
        Assert.Equal(["run 1"], log.Where(e => e.StartsWith("run", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Error_keeps_trigger_blocked_when_rollback_confirmed_and_no_residual()
    {
        var rig = NewRig();
        rig.Runner.Outcome = st => new StatementRunOutcome
        {
            Result = Statement(st.Index, null, status: StatementStatus.TriggerBlocked) with
            {
                Error = new StatementError { ErrorNumber = 3609, Message = "거부", IsToolFault = false },
            },
            TransactionOpen = false,
            TriggerRollbackConfirmed = true,
        };
        rig.Differ.Results.Clear();

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.Equal(StatementStatus.TriggerBlocked, result.Statements[0].Status);
        Assert.Empty(result.Verification.ResidualCommitted);
        Assert.False(result.SandboxRefreshStarted);
    }

    [Fact]
    public async Task Residual_check_failure_is_reported_not_thrown()
    {
        var rig = NewRig();
        rig.Runner.Outcome = st => new StatementRunOutcome
        {
            Result = Statement(st.Index, null, status: StatementStatus.Failed) with { Error = new StatementError { ErrorNumber = 547, Message = "FK", IsToolFault = false } },
            TransactionOpen = false,
        };
        rig.Differ.Results.Clear();
        rig.Differ.Results.Enqueue(() => throw new InvalidOperationException("8115 산술 오버플로"));

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.True(result.Verification.ResidualCheckFailed);
        Assert.Contains("8115", result.Message);
        Assert.True(result.SandboxRefreshStarted);
    }

    [Fact]
    public async Task No_snapshot_capability_without_consent_is_precondition_failed()
    {
        var rig = NewRig();
        rig.Origin.Capability = OriginSnapshot.Decide(canCreateDatabase: false, filestreamGroups: 0, memoryOptimizedGroups: 0);

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.PreconditionFailed, result!.Status);
        Assert.Equal([ApplyPrecondition.WeakVerificationNotAccepted], result.PreconditionFailures);
        Assert.Equal(VerificationMode.Keyset, result.Verification.Mode);
        Assert.Contains("allowKeysetVerification", result.Message);
        AssertNothingRanOnOrigin();
    }

    [Fact]
    public void Snapshot_outcome_has_no_silent_downgrade_value()
    {
        // 2026-11-13: 생성 실패는 Failed(error) 하나뿐. KEYSET은 실행 전 결정(CheckSnapshotCapability)으로만 간다.
        Assert.Equal(["Created", "Failed"], Enum.GetNames<SnapshotOutcome>());
    }

    [Theory]
    [InlineData(true, 0, 0, true, null)]
    [InlineData(false, 0, 0, false, "CREATE DATABASE")]
    [InlineData(true, 1, 0, false, "FILESTREAM")]
    [InlineData(true, 0, 2, false, "메모리 최적화")]
    public void Snapshot_capability_is_decided_from_permission_and_filegroups(bool perm, int fd, int fx, bool can, string? reason)
    {
        var c = OriginSnapshot.Decide(perm, fd, fx);
        Assert.Equal(can, c.CanSnapshot);
        if (reason is null) Assert.Null(c.Reason);
        else Assert.Contains(reason, c.Reason);
    }

    [Fact]
    public async Task Unexpected_snapshot_failure_is_error_not_keyset()
    {
        var rig = NewRig();
        rig.Origin.Snapshot = (SnapshotOutcome.Failed, "원본 스냅샷 생성 실패(오류 5119)");

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.Equal(VerificationMode.SnapshotNetdiff, result.Verification.Mode);
        Assert.DoesNotContain("open", log);
        Assert.DoesNotContain("count", log);
    }

    [Fact]
    public async Task No_snapshot_capability_with_consent_uses_keyset()
    {
        var rig = NewRig();
        rig.Origin.Capability = OriginSnapshot.Decide(canCreateDatabase: true, filestreamGroups: 1, memoryOptimizedGroups: 0);

        var result = await rig.ApplyAsync(new ApplyRequest { AllowKeysetVerification = true });

        Assert.Equal(ApplyStatus.Committed, result!.Status);
        Assert.Equal(VerificationMode.Keyset, result.Verification.Mode);
        Assert.Null(result.Verification.NetSummaryMatch);
        Assert.Contains(result.Verification.Limitations, l => l.Contains("FILESTREAM", StringComparison.Ordinal));
        Assert.Contains(KeysetVerifier.Limitation, result.Verification.Limitations);
        Assert.Contains("count", log);
        Assert.Contains("keyset", log);
        Assert.DoesNotContain(log, e => e.StartsWith("diff", StringComparison.Ordinal));
        Assert.DoesNotContain(log, e => e.StartsWith("drop", StringComparison.Ordinal)); // 만들지 않은 스냅샷은 지우지 않는다
        Assert.DoesNotContain(log, e => e.StartsWith("snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Split_commit_marks_baseline_stale_without_rebuild_and_runs_as_own_login()
    {
        var rig = NewRig(settings: new ApplySettings { Mode = ServerMode.Split, MachineTag = "devpc", ExecuteAsUser = PreviewUserSql.UserName });

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Committed, result!.Status);
        Assert.False(result.SandboxRefreshStarted);
        Assert.DoesNotContain("refresh", log);
        Assert.Equal("stale", log[^1]);
        Assert.Contains("기준선이 오래됨", result.Message);
        Assert.Null(rig.Runner.Context!.ExecuteAsUser);                 // 원본에서 가장하지 않는다
        Assert.Contains(ApplyService.PrincipalLimitation, result.Verification.Limitations);
        var snapshot = Assert.Single(log, e => e.StartsWith("snapshot ", StringComparison.Ordinal))["snapshot ".Length..];
        Assert.Matches("^Origin_pre_devpc_[0-9a-f]{8}$", snapshot);       // PC·적용 id 접미사
        Assert.Contains($"drop {snapshot}", log);                       // 자기가 만든 이름만 지운다
    }

    [Fact]
    public async Task Split_skips_execute_as_user_precondition_and_compares_blocking_fingerprint_fields_only()
    {
        var rig = NewRig(settings: new ApplySettings { Mode = ServerMode.Split });
        rig.Origin.UserExists = false;                                  // Split에서는 원본에 dbr_preview_user가 없어도 된다
        rig.Origin.Fingerprint = Fingerprint("16.0.4000.1");            // 버전 차이는 경고 항목

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Committed, result!.Status);

        var blocked = NewRig(settings: new ApplySettings { Mode = ServerMode.Split });
        blocked.Origin.Fingerprint = Fingerprint() with { CompatibilityLevel = 150 };
        var r2 = await blocked.ApplyAsync();
        Assert.Equal([ApplyPrecondition.FingerprintMismatch], r2!.PreconditionFailures);
        Assert.Contains("호환성 수준", r2.Message);
    }

    [Fact]
    public async Task Origin_unreachable_is_precondition_failed_without_touching_origin()
    {
        // N4: 원본에 연결할 수 없으면 ORIGIN_UNREACHABLE로 막는다(검증 모드 판단·사용자 확인도 하지 않는다).
        var rig = NewRig(settings: new ApplySettings { Mode = ServerMode.Split });
        rig.Origin.Unreachable = new SandboxUnavailableException("원본 서버에 연결할 수 없음(DB master, 오류 53)");
        rig.Origin.Capability = OriginSnapshot.Decide(canCreateDatabase: false, filestreamGroups: 0, memoryOptimizedGroups: 0);

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.PreconditionFailed, result!.Status);
        Assert.Equal([ApplyPrecondition.OriginUnreachable], result.PreconditionFailures);
        Assert.Contains("오류 53", result.Message);
        AssertNothingRanOnOrigin();
        Assert.Contains("\"ORIGIN_UNREACHABLE\"", System.Text.Json.JsonSerializer.Serialize(result.PreconditionFailures, ContractJson.Options));
    }

    [Fact]
    public async Task Split_sysadmin_origin_login_adds_limitation_but_does_not_block()
    {
        var rig = NewRig(settings: new ApplySettings { Mode = ServerMode.Split }, context: Context() with { OriginIsSysadmin = true });

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Committed, result!.Status);
        Assert.Contains(ApplyService.SysadminLimitation, result.Verification.Limitations);
    }

    [Fact]
    public async Task Force_keyset_skips_snapshot_and_keyset_mismatch_rolls_back()
    {
        var rig = NewRig(settings: new ApplySettings { ForceKeyset = true });
        rig.Origin.Connection.KeysetMismatches = [new ApplyMismatch { Kind = MismatchKind.KeysetTableCount, Table = "dbo.T", Message = "건수" }];

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.RolledBackMismatch, result!.Status);
        Assert.Equal(VerificationMode.Keyset, result.Verification.Mode);
        Assert.DoesNotContain(log, e => e.StartsWith("snapshot", StringComparison.Ordinal));
        Assert.DoesNotContain("commit", log);
    }

    [Fact]
    public async Task Keyset_error_marks_residual_check_failed()
    {
        var rig = NewRig(settings: new ApplySettings { ForceKeyset = true });
        rig.Runner.Outcome = st => new StatementRunOutcome
        {
            Result = Statement(st.Index, null, status: StatementStatus.Failed) with { Error = new StatementError { ErrorNumber = 547, Message = "FK", IsToolFault = false } },
            TransactionOpen = false,
        };

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.True(result.Verification.ResidualCheckFailed);
        Assert.True(result.SandboxRefreshStarted);
    }

    [Fact]
    public async Task Exception_still_rolls_back_drops_snapshot_and_unlocks()
    {
        var rig = NewRig();
        rig.Runner.Outcome = _ => throw new SandboxUnavailableException("연결 끊김");

        await Assert.ThrowsAsync<SandboxUnavailableException>(() => rig.ApplyAsync());

        var rollback = log.IndexOf("rollback");
        Assert.True(rollback > log.IndexOf("run 1"));
        Assert.True(log.IndexOf("drop Origin_pre") > rollback);
        Assert.True(log.IndexOf("unlock") > log.IndexOf("drop Origin_pre"));
        Assert.Equal("refresh", log[^1]); // 문장을 실행했으므로 원본 상태를 모름 → 잠금을 푼 뒤 새로 고침
        Assert.DoesNotContain("commit", log);
    }

    [Fact]
    public async Task Commit_exception_is_unknown_outcome_and_closes_reapply()
    {
        var rig = NewRig();
        var record = rig.Store.Record!;
        rig.Origin.Connection.CommitError = new SandboxUnavailableException("COMMIT 응답 전에 연결이 끊김");

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.Contains("커밋됐는지 알 수 없음", result.Message);
        Assert.Contains(result.Verification.Limitations, l => l.Contains("커밋됐는지 알 수 없음", StringComparison.Ordinal));
        Assert.DoesNotContain("롤백함", result.Message);
        Assert.Null(rig.Store.Record);              // 닫힌 쪽: 미리보기 삭제
        Assert.True(result.SandboxRefreshStarted);  // 원본 상태를 모르므로 새로 고침
        Assert.Equal("refresh", log[^1]);
        Assert.True(log.IndexOf("unlock") < log.IndexOf("refresh"));
        Assert.Equal(1, log.Count(e => e.StartsWith("diff", StringComparison.Ordinal))); // 검증 diff 한 번뿐(롤백 단정 검사 없음)

        rig.Store.Record = record;                  // 저장소 삭제가 안 됐다고 가정해도
        Assert.Null(await rig.ApplyAsync());        // 다시 적용하지 않는다
    }

    [Fact]
    public async Task Snapshot_name_conflict_or_permission_is_error_not_keyset()
    {
        var rig = NewRig();
        rig.Origin.Snapshot = (SnapshotOutcome.Failed, "오류 50001");

        var result = await rig.ApplyAsync();

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.Equal(VerificationMode.SnapshotNetdiff, result.Verification.Mode);
        Assert.Contains("50001", result.Message);
        Assert.DoesNotContain("open", log);
        Assert.DoesNotContain(log, e => e.StartsWith("drop", StringComparison.Ordinal));
        Assert.DoesNotContain(log, e => e.StartsWith("run", StringComparison.Ordinal));
        Assert.False(result.SandboxRefreshStarted);
        Assert.NotNull(rig.Store.Record);
    }

    [Fact]
    public void Server_configured_names_fail_as_invalid_operation_not_argument()
    {
        var bad = Assert.Throws<InvalidOperationException>(() => OriginNames.Validate("AW Origin", "원본"));
        Assert.IsNotType<ArgumentException>(bad);
        Assert.IsType<ArgumentException>(bad.InnerException);
    }

    [Fact]
    public async Task Snapshot_name_equal_to_origin_is_invalid_operation_before_connecting()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OriginSnapshot.CreateAsync("Server=unused.invalid;Connect Timeout=1", "Origin", "origin", 5, CancellationToken.None));
    }
}
