using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;

namespace DbRipple.Apply;

/// <summary>
/// C15 적용 서비스(§4 C15, §5.3, D7).
/// <para>절차(§5.3 그림 순서, 이 순서를 바꾸지 않는다):</para>
/// <list type="number">
/// <item>store.GetAsync(previewId) → 없으면 null(404). 이 서비스가 이미 커밋한 id도 null(재적용 방지).</item>
/// <item>sandbox.AcquirePreviewLockAsync(잠금 충돌은 PreviewConflictException 그대로 409) → 잠금 안에서 store.GetAsync를 다시(동시 적용이 지운 건을 다시 적용하지 않게) → sandbox.GetContextAsync.</item>
/// <item>선행 조건(ApplyPrecondition): 상태 ok(D8 truncated 차단 포함), ContractFormulas.SqlHash(record.Sql) == result.SqlHash == request.SqlHash(있으면), CreatedAt + PreviewExpiry 안이고 미리보기의 기준선이 현재 기준선과 같음(아니면 PREVIEW_EXPIRED),
///   원본 지문(Core Fingerprint.ReadAsync(원본)) == baseline.Fingerprint == 미리보기 지문, 원본에 ExecuteAsUser 있음(Q30, 만들지 않는다). 어기면 precondition_failed(원본에 아무것도 안 함).</item>
/// <item>analyzer.Analyze(record.Sql, baseline) 재분석(문장 원문 얻기). 거부·문장 수·번호·종류·대상이 미리보기와 다르면 precondition_failed(SQL_HASH_MISMATCH와 같은 취급, 메시지로 구분).</item>
/// <item>검증 모드(2026-11-13): 선행 조건 단계에서 원본 CREATE DATABASE 권한과 FILESTREAM·메모리 최적화 유무로 정한다. 스냅샷을 만들 수 없으면 KEYSET_HASH이고,
///   요청에 allowKeysetVerification=true가 없으면 precondition_failed(WEAK_VERIFICATION_NOT_ACCEPTED). ForceKeyset은 설정 동의로 본다.</item>
/// <item>원본 스냅샷: Core BaselineSql.DropSnapshot·CreateSnapshot(SingleServer = OriginDb + 접미사, Split = 거기에 _PC_적용id, 스파스 파일은 원본 데이터 파일 폴더).
///   생성 실패는 원인과 상관없이 실행하지 않고 error(KEYSET_HASH로 조용히 낮추지 않는다).</item>
/// <item>전용 연결(풀링 끔, InitialCatalog = OriginDb) → runner.PrepareAsync → "BEGIN TRAN;" → CURRENT_TRANSACTION_ID().</item>
/// <item>드리프트: DriftChecker로 미리보기 NetChanges의 before 행(UPDATED·DELETED·MULTISET_REMOVED)을 UPDLOCK, HOLDLOCK으로 읽어 비교. 있으면 ROLLBACK, drift_detected.</item>
/// <item>KEYSET_HASH면 KeysetVerifier로 원본 카탈로그 사용자 테이블의 COUNT_BIG·CHECKSUM_AGG(실행 직전).</item>
/// <item>문장마다 runner.RunAsync. 실패(ok 아님, 트랜잭션이 끝남, 트랜잭션이 바뀜)면 ROLLBACK(남아 있으면) → 롤백 뒤 남은 변경(원본 대 원본 스냅샷, 전체 사용자 테이블) → status error.
///   검사 자체가 예외면 Verification.ResidualCheckFailed = true(인계 7과 같은 표시). KEYSET_HASH면 원본 스냅샷이 없어 검사할 수 없으므로 ResidualCheckFailed = true.
///   3609 보정: 남은 변경이 있고 실패 문장이 trigger_blocked인데 StatementRunOutcome.TriggerRollbackConfirmed = false면 그 문장을 failed로 바꾼다.</item>
/// <item>최종 차이: netDiffer.DiffAsync(같은 세션, OriginDb, 원본 스냅샷, 감사 저장소 뺀 전체 사용자 테이블) → ApplyMatcher.Match(D7). KEYSET_HASH면 KeysetVerifier(전후 상태 비교 + 키 집합). [적용] 원본 사전 경고(TRUSTWORTHY·복제·CDC·Service Broker)는 스냅샷 전에 limitations로.</item>
/// <item>위반 0이면 COMMIT(committed), 아니면 ROLLBACK(rolled_back_mismatch). COMMIT 단계 예외는 커밋 여부 불명으로 error(메시지·Limitations에 명시)이고, 닫힌 쪽으로 커밋과 같이 재적용 차단·미리보기 삭제·새로 고침을 한다.
///   커밋되면 잠금 안에서 store.RemoveAsync(previewId)(잠금을 푼 직후 같은 건이 다시 적용되지 않게). finally: 트랜잭션이 남아 있으면 ROLLBACK, 연결 닫기, 원본 스냅샷 삭제.</item>
/// <item>잠금을 푼 뒤(AD10): 원본이 바뀌었으면(committed, 남은 변경 있음, 검사 실패) sandbox.StartRefresh(), SandboxRefreshStarted = true.
///   예외로 끝났어도 문장을 하나라도 실행했으면 원본 상태를 알 수 없으므로 새로 고침을 시작하고 예외를 다시 던진다.</item>
/// </list>
/// </summary>
public sealed class ApplyService : IApplyService
{
    readonly IOriginDatabase origin;
    readonly ConcurrentDictionary<string, byte> committed = new(StringComparer.Ordinal);

    /// <summary>Host 조립용(§6.4 생성자 확정, week3-plan §4). 원본 연결 타입만 받는다(2026-11-13).</summary>
    public ApplyService(
        OriginConnection origin,
        ApplySettings settings,
        IPreviewStore store,
        ISandboxManager sandbox,
        IBatchAnalyzer analyzer,
        IStatementRunner runner,
        INetDiffer netDiffer,
        TimeProvider clock)
        : this(new SqlOriginDatabase((origin ?? throw new ArgumentNullException(nameof(origin))).ConnectionString, settings ?? throw new ArgumentNullException(nameof(settings))),
               settings, store, sandbox, analyzer, runner, netDiffer, clock)
    {
    }

    /// <summary>테스트용: 원본 서버 작업을 가짜로 바꾼다.</summary>
    internal ApplyService(
        IOriginDatabase origin,
        ApplySettings settings,
        IPreviewStore store,
        ISandboxManager sandbox,
        IBatchAnalyzer analyzer,
        IStatementRunner runner,
        INetDiffer netDiffer,
        TimeProvider clock)
    {
        this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        Analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        NetDiffer = netDiffer ?? throw new ArgumentNullException(nameof(netDiffer));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>설정.</summary>
    public ApplySettings Settings { get; }

    /// <summary>미리보기 저장소(C14).</summary>
    public IPreviewStore Store { get; }

    /// <summary>샌드박스 관리자(잠금, 문맥, 새로 고침).</summary>
    public ISandboxManager Sandbox { get; }

    /// <summary>배치 분석기(C6, 재분석).</summary>
    public IBatchAnalyzer Analyzer { get; }

    /// <summary>실행 봉투(C10과 공유, AD6).</summary>
    public IStatementRunner Runner { get; }

    /// <summary>최종 차이 계산기(C12).</summary>
    public INetDiffer NetDiffer { get; }

    /// <summary>시계.</summary>
    public TimeProvider Clock { get; }

    /// <summary>한 번의 적용 동안 바뀌는 상태.</summary>
    sealed class Run
    {
        public bool OriginTouched;
        public bool RefreshNeeded;
        public readonly List<string> Notes = [];
        public readonly Stopwatch Watch = new();
        public long Snapshot, Drift, Execute, NetDiff, Commit;

        public long Lap() { var ms = Watch.ElapsedMilliseconds; Watch.Restart(); return ms; }

        public ApplyTimingsMs Timings() => new() { Snapshot = Snapshot, Drift = Drift, Execute = Execute, NetDiff = NetDiff, Commit = Commit };
    }

    /// <inheritdoc />
    public async Task<ApplyResult?> ApplyAsync(string previewId, ApplyRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        ArgumentNullException.ThrowIfNull(request);
        if (committed.ContainsKey(previewId)) return null;
        if (await Store.GetAsync(previewId, ct) is null) return null;

        var run = new Run();
        ApplyResult? result;
        try
        {
            var gate = await Sandbox.AcquirePreviewLockAsync(ct);
            await using (gate)
            {
                var record = committed.ContainsKey(previewId) ? null : await Store.GetAsync(previewId, ct);
                if (record is null) return null;
                result = await RunLockedAsync(record, request, run, ct);
            }
        }
        catch when (run.OriginTouched)
        {
            // 문장을 실행한 뒤 예외로 끝났다: 트랜잭션은 finally에서 롤백했지만 트리거가 확정한 변경이 있을 수 있다(원본 상태 모름).
            TryStartRefresh(run);
            throw;
        }

        if (run.RefreshNeeded) result = result with { SandboxRefreshStarted = TryStartRefresh(run) };
        return run.Notes.Count == 0 ? result : result with { Message = Join(result.Message, run.Notes) };
    }

    /// <summary>Split [적용]의 실행 주체 한계(결과·문서에 표시).</summary>
    public const string PrincipalLimitation =
        "실행 주체 불일치: 미리보기는 샌드박스의 dbr_preview_user로, [적용]은 원본에서 본인 로그인으로 실행함(권한 차이, 트리거의 USER_NAME·SUSER_SNAME 값이 다를 수 있음)";

    /// <summary>Split 원본 로그인이 sysadmin일 때 결과 한계(막지 않음, N4).</summary>
    public const string SysadminLimitation =
        "원본 로그인이 sysadmin: 권한으로 막히는 문장이 없어 대상 서버·DB를 잘못 골라도 서버가 막지 않음(확인 상자에서 다시 확인)";

    bool TryStartRefresh(Run run)
    {
        if (Settings.Mode == ServerMode.Split)
        {
            // Split: 재구축은 원본 백업·복사를 동반하므로 자동으로 하지 않는다. 기준선 오래됨으로만 표시하고 사용자가 새로 고친다.
            Sandbox.MarkStale("[적용]으로 원본이 바뀜");
            run.Notes.Add("원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).");
            return false;
        }
        try
        {
            Sandbox.StartRefresh();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            run.Notes.Add($"샌드박스 새로 고침 시작 실패: {ex.Message}");
            return false;
        }
    }

    async Task<ApplyResult> RunLockedAsync(PreviewRecord record, ApplyRequest request, Run run, CancellationToken ct)
    {
        var context = await Sandbox.GetContextAsync(ct);
        var mode = Settings.ForceKeyset ? VerificationMode.KeysetHash : VerificationMode.SnapshotNetdiff;
        string? keysetReason = Settings.ForceKeyset ? "ForceKeyset 설정으로 원본 스냅샷 없이 KEYSET_HASH로 검증함" : null;

        // 3. 선행 조건(원본에 아무것도 하지 않는다)
        var (failures, reasons) = await CheckPreconditionsAsync(record, request, context, ct);
        if (!Settings.ForceKeyset && !failures.Contains(ApplyPrecondition.OriginUnreachable))
        {
            // 3b. 검증 모드 사전 결정(읽기만). 스냅샷을 못 만들면 KEYSET_HASH이고, 약한 검증은 요청의 명시 동의가 있어야 한다.
            var capability = await origin.CheckSnapshotCapabilityAsync(context.OriginDb, ct);
            if (!capability.CanSnapshot)
            {
                mode = VerificationMode.KeysetHash;
                keysetReason = $"원본 스냅샷을 만들 수 없어 KEYSET_HASH로 검증함(요청 동의): {capability.Reason}";
                if (!request.AllowKeysetVerification)
                {
                    failures.Add(ApplyPrecondition.WeakVerificationNotAccepted);
                    reasons.Add($"원본 스냅샷을 만들 수 없어({capability.Reason}) KEYSET_HASH(건수·해시 비교, 해시 충돌이나 미리보기 테이블의 키 밖 UPDATE를 놓칠 수 있음)로만 검증할 수 있음. 동의하면 allowKeysetVerification=true로 다시 요청할 것.");
                }
            }
        }
        AnalysisResult? analysis = null;
        if (failures.Count == 0)
        {
            // 4. 재분석: 문장 원문을 얻고, 분석 결과가 미리보기와 같은지 본다.
            analysis = Analyzer.Analyze(record.Sql, context);
            if (analysis.Statements.Any(s => s.Kind == ChangeOperation.Exec))
            {
                // 방어 심화: 저장된 미리보기 결과가 아니라 지금 재분석한 문장으로도 EXEC를 막는다(2026-11-13).
                failures.Add(ApplyPrecondition.ProcApplyNotSupported);
                reasons.Add("저장 프로시저 호출(EXEC)이 있는 미리보기는 아직 적용할 수 없음(미리보기 전용).");
            }
            var why = SameStatements(analysis, record.Result.Statements);
            if (why is not null)
            {
                failures.Add(ApplyPrecondition.SqlHashMismatch);
                reasons.Add(why);
            }
        }
        if (failures.Count > 0)
            return Make(record, ApplyStatus.PreconditionFailed, new ApplyVerification { Mode = mode }, [], string.Join(" ", reasons), run) with
            {
                PreconditionFailures = failures.Distinct().ToList(),
            };

        var tables = context.Catalog.ToCaptureInput().Where(t => !AuditTriggerGenerator.IsCaptureStore(t)).ToList();
        var snapshotDb = Settings.Mode == ServerMode.Split
            ? RunNames.OriginSnapshot(context.OriginDb, Settings.OriginSnapshotSuffix, Settings.MachineTag, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)))
            : context.OriginDb + Settings.OriginSnapshotSuffix;
        var limitations = new List<string>();
        if (Settings.Mode == ServerMode.Split) limitations.Add(PrincipalLimitation);
        if (Settings.Mode == ServerMode.Split && context.OriginIsSysadmin == true) limitations.Add(SysadminLimitation);
        limitations.AddRange(await origin.ReadOriginWarningsAsync(context.OriginDb, ct)); // 사전 경고(막지 않음)
        var snapshotCreated = false;
        try
        {
            // 5. 원본 스냅샷(실패하면 error, KEYSET_HASH로 낮추지 않는다)
            run.Watch.Restart();
            if (mode == VerificationMode.SnapshotNetdiff)
            {
                var (outcome, reason) = await origin.CreateSnapshotAsync(context.OriginDb, snapshotDb, ct);
                snapshotCreated = outcome == SnapshotOutcome.Created;
                if (!snapshotCreated)
                {
                    run.Snapshot = run.Lap();
                    return Make(record, ApplyStatus.Error, new ApplyVerification { Mode = mode, Limitations = limitations }, [],
                        $"원본 스냅샷을 준비하지 못해 실행하지 않음: {reason}", run);
                }
            }
            else
            {
                limitations.Add(keysetReason!);
                limitations.AddRange(KeysetVerifier.Limitations);
            }
            run.Snapshot = run.Lap();

            // 6. 전용 연결
            var conn = await origin.OpenAsync(context.OriginDb, ct);
            await using (conn)
            {
                try
                {
                    return await ExecuteAsync(conn, record, analysis!, context, tables, mode, snapshotDb, limitations, run, ct);
                }
                finally
                {
                    try { await conn.RollbackAsync(CancellationToken.None); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        run.Notes.Add($"정리 중 ROLLBACK 실패(연결을 닫아 서버 롤백에 맡김): {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            if (snapshotCreated)
            {
                try { await origin.DropSnapshotAsync(context.OriginDb, snapshotDb, CancellationToken.None); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    run.Notes.Add($"원본 스냅샷 {snapshotDb} 삭제 실패(다음 적용이 먼저 지운다): {ex.Message}");
                }
            }
        }
    }

    async Task<ApplyResult> ExecuteAsync(
        IOriginConnection conn, PreviewRecord record, AnalysisResult analysis, BaselineContext context, IReadOnlyList<TableInfo> tables,
        VerificationMode mode, string snapshotDb, List<string> limitations, Run run, CancellationToken ct)
    {
        await Runner.PrepareAsync(conn.Session, ct);
        var tranId = await conn.BeginTransactionAsync(ct);

        // 7. 드리프트(잠금은 COMMIT·ROLLBACK까지)
        run.Watch.Restart();
        var drift = await conn.CheckDriftAsync(record, tables, ct);
        run.Drift = run.Lap();
        if (drift.Count > 0)
        {
            await conn.RollbackAsync(CancellationToken.None);
            run.Commit = run.Lap();
            return Make(record, ApplyStatus.DriftDetected,
                new ApplyVerification { Mode = mode, DriftRows = drift, Limitations = limitations }, [],
                $"미리보기 뒤 원본의 before 행 {drift.Count}건이 바뀌어 실행하지 않고 롤백함. 샌드박스를 새로 고친 뒤 다시 미리보기 할 것", run);
        }

        // 8. KEYSET_HASH 실행 직전 상태(원본 카탈로그의 사용자 테이블, 같은 세션·LOCK_TIMEOUT)
        KeysetBaseline? keyset = null;
        if (mode == VerificationMode.KeysetHash)
        {
            try
            {
                var targets = await conn.ReadHashTargetsAsync(ct);
                var inPreview = KeysetVerifier.PreviewTables(record);
                keyset = new KeysetBaseline(targets, inPreview, await conn.ReadTableStatesAsync(targets, inPreview, ct));
                limitations.AddRange(KeysetVerifier.ExcludedColumnNotes(targets, inPreview));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SandboxUnavailableException)
            {
                // 문장 실행 전: 롤백하면 원본은 그대로다(잠금 시간 초과 1222 등).
                await conn.RollbackAsync(CancellationToken.None);
                run.Commit = run.Lap();
                return Make(record, ApplyStatus.Error, new ApplyVerification { Mode = mode, Limitations = limitations }, [],
                    $"KEYSET_HASH 실행 전 상태를 읽지 못해 실행하지 않고 롤백함: {ex.Message}", run);
            }
        }

        // 9. 문장 실행
        run.Watch.Restart();
        var runContext = new StatementRunContext
        {
            Baseline = context,
            ExecuteAsUser = Settings.Mode == ServerMode.Split ? null : Settings.ExecuteAsUser, // Split: 원본에서 가장하지 않는다(본인 로그인)
            CommandTimeoutSeconds = Settings.CommandTimeoutSeconds,
            TransactionId = tranId,
        };
        var statements = new List<StatementResult>();
        StatementRunOutcome? failed = null;
        foreach (var st in analysis.Statements.OrderBy(s => s.Index))
        {
            if (failed is not null)
            {
                statements.Add(NotRun(st));
                continue;
            }
            // 선행 조건이 이미 막는다. 여기 닿으면 코드 결함이므로 원본에서 실행하지 않고 예외로 롤백 경로에 넘긴다.
            if (st.Kind == ChangeOperation.Exec) throw new InvalidOperationException("EXEC 문장은 [적용]하지 않는다(PROC_APPLY_NOT_SUPPORTED).");
            run.OriginTouched = true;
            var outcome = await Runner.RunAsync(conn.Session, st, runContext, ct);
            statements.Add(outcome.Result with { CaptureSeqRange = null });
            if (outcome.Result.Status != StatementStatus.Ok || !outcome.TransactionOpen || outcome.TransactionReplaced) failed = outcome;
        }
        run.Execute = run.Lap();
        if (failed is not null)
            return await FailAsync(conn, record, context, tables, mode, snapshotDb, limitations, statements, failed, run, ct);

        // 10. 검증
        MatchOutcome match;
        IReadOnlyList<TableCountChange> tableCounts = [];
        try
        {
            if (mode == VerificationMode.SnapshotNetdiff)
            {
                var actualNet = await NetDiffer.DiffAsync(conn.Session, context.OriginDb, snapshotDb, tables, ct);
                match = ApplyMatcher.Match(new MatchInput { Preview = record, Actual = statements, ActualNet = actualNet, Baseline = context });
            }
            else
            {
                var (rows, statementMismatches) = ApplyMatcher.CompareStatements(record.Result.Statements, statements);
                var after = await conn.ReadTableStatesAsync(keyset!.Targets, keyset.PreviewTables, ct);
                var (counts, stateMismatches) = KeysetVerifier.CompareStates(record, keyset.States, after, keyset.PreviewTables);
                var keyRows = await conn.VerifyKeyRowsAsync(record, tables, context, ct);
                tableCounts = counts;
                match = new MatchOutcome { StatementRowCounts = rows, NetSummaryMatch = false, Mismatches = [.. statementMismatches, .. stateMismatches, .. keyRows] };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SandboxUnavailableException)
        {
            // 문장은 모두 성공했고 트랜잭션도 그대로였다(바뀌었으면 위 실패 경로): 롤백하면 원본은 실행 전 상태다.
            run.NetDiff = run.Lap();
            await conn.RollbackAsync(CancellationToken.None);
            run.Commit = run.Lap();
            return Make(record, ApplyStatus.Error, new ApplyVerification { Mode = mode, Limitations = limitations }, statements,
                $"커밋 전 검증 중 오류가 나서 롤백함: {ex.Message}", run);
        }
        run.NetDiff = run.Lap();

        var verification = new ApplyVerification
        {
            Mode = mode,
            StatementRowCounts = match.StatementRowCounts,
            NetSummaryMatch = mode == VerificationMode.SnapshotNetdiff ? match.NetSummaryMatch : null,
            ActualNetSummary = match.ActualNetSummary,
            Mismatches = match.Mismatches,
            VolatileIgnored = match.VolatileIgnored,
            IdentityPairs = match.IdentityPairs,
            TableCounts = tableCounts,
            Limitations = [.. limitations, .. match.Limitations],
        };

        // 11. COMMIT 또는 ROLLBACK
        if (match.Mismatches.Count > 0)
        {
            await conn.RollbackAsync(CancellationToken.None);
            run.Commit = run.Lap();
            return Make(record, ApplyStatus.RolledBackMismatch, verification, statements,
                $"원본 실행 결과가 미리보기와 {match.Mismatches.Count}건 달라 롤백함", run);
        }
        try
        {
            await conn.CommitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // COMMIT 단계 예외는 커밋 여부를 알 수 없다: 서버가 COMMIT을 확정한 뒤 응답 전에 연결이 끊겼을 수 있다.
            // 닫힌 쪽으로 커밋된 것처럼 다룬다(재적용 차단, 미리보기 삭제, 새로 고침). status는 error, 메시지·limitations에 "커밋 여부 불명".
            run.Commit = run.Lap();
            try { await conn.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackError) when (rollbackError is not OperationCanceledException)
            {
                run.Notes.Add($"COMMIT 예외 뒤 ROLLBACK도 실패(연결을 닫아 서버에 맡김): {rollbackError.Message}");
            }
            await MarkAppliedAsync(record, run);
            const string unknown = "COMMIT 중 예외가 나서 원본에 커밋됐는지 알 수 없음. 이 미리보기는 다시 적용하지 않으며 샌드박스를 새로 고친다. 원본을 직접 확인할 것";
            return Make(record, ApplyStatus.Error,
                verification with { Limitations = [.. verification.Limitations, unknown] }, statements,
                $"{unknown}: {ex.Message}", run);
        }
        run.Commit = run.Lap();
        await MarkAppliedAsync(record, run);
        return Make(record, ApplyStatus.Committed, verification, statements, null, run);
    }

    /// <summary>커밋했거나 커밋 여부를 모를 때: 이 서버에서 다시 적용하지 않고(잠금 안에서) 미리보기를 지우고, 잠금을 푼 뒤 새로 고침을 시작하게 한다.</summary>
    async Task MarkAppliedAsync(PreviewRecord record, Run run)
    {
        run.RefreshNeeded = true;
        committed.TryAdd(record.Result.PreviewId, 0);
        try { await Store.RemoveAsync(record.Result.PreviewId, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            run.Notes.Add($"적용한 미리보기를 저장소에서 지우지 못함(이 서버는 다시 적용하지 않는다): {ex.Message}");
        }
    }

    async Task<ApplyResult> FailAsync(
        IOriginConnection conn, PreviewRecord record, BaselineContext context, IReadOnlyList<TableInfo> tables, VerificationMode mode,
        string snapshotDb, List<string> limitations, List<StatementResult> statements, StatementRunOutcome failed, Run run, CancellationToken ct)
    {
        await conn.RollbackAsync(CancellationToken.None);
        run.Commit = run.Lap();
        var (residual, checkFailed, checkMessage) = await ResidualAsync(conn, context, tables, mode, snapshotDb, ct);
        run.NetDiff = run.Lap();

        // 3609 보정(2주차 인계 5): 롤백 뒤 남은 변경이 있는데 트리거 ROLLBACK으로 확인되지 않으면 거부가 아니라 실패다.
        var index = statements.FindIndex(s => s.Index == failed.Result.Index);
        if (index >= 0 && residual.Count > 0 && statements[index].Status == StatementStatus.TriggerBlocked && failed.TriggerRollbackConfirmed == false)
            statements[index] = statements[index] with { Status = StatementStatus.Failed };

        run.RefreshNeeded = residual.Count > 0 || checkFailed;
        var (rows, _) = ApplyMatcher.CompareStatements(record.Result.Statements, statements);
        var f = statements[index >= 0 ? index : ^1];
        var what = f.Error is { } e ? $"오류 {e.ErrorNumber}: {e.Message}" : "트랜잭션 상태가 예상과 다름";
        var message = $"문장 {f.Index}이(가) 원본에서 {Status(f.Status)}되어 롤백함({what})."
            + (residual.Count > 0 ? $" 롤백 뒤 원본에 남은 변경 {residual.Count}건이 있음." : "");
        return Make(record, ApplyStatus.Error,
            new ApplyVerification
            {
                Mode = mode, StatementRowCounts = rows, ResidualCommitted = residual, ResidualCheckFailed = checkFailed, Limitations = limitations,
            },
            statements, Join(message, Optional(checkMessage)), run);
    }

    /// <summary>롤백 뒤 남은 변경(원본 대 원본 스냅샷, 트랜잭션 없이). 검사가 예외면 실패 표시(Q31: 던지지 않는다). KEYSET_HASH는 스냅샷이 없어 검사할 수 없다.</summary>
    async Task<(IReadOnlyList<NetChange> Residual, bool Failed, string? Message)> ResidualAsync(
        IOriginConnection conn, BaselineContext context, IReadOnlyList<TableInfo> tables, VerificationMode mode, string snapshotDb, CancellationToken ct)
    {
        if (mode == VerificationMode.KeysetHash)
            return ([], true, "KEYSET_HASH 모드라 원본 스냅샷이 없어 롤백 뒤 남은 변경을 확인할 수 없음(원본 상태를 알 수 없어 샌드박스를 새로 고침)");
        try
        {
            return (await NetDiffer.DiffAsync(conn.Session, context.OriginDb, snapshotDb, tables, ct), false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], true, $"롤백 뒤 남은 변경 확인 실패: {ex.Message}");
        }
    }

    async Task<(List<ApplyPrecondition> Failures, List<string> Reasons)> CheckPreconditionsAsync(
        PreviewRecord record, ApplyRequest request, BaselineContext context, CancellationToken ct)
    {
        var failures = new List<ApplyPrecondition>();
        var reasons = new List<string>();
        var result = record.Result;

        if (result.Status != PreviewStatus.Ok || result.Capture.Truncated)
        {
            failures.Add(ApplyPrecondition.PreviewNotOk);
            reasons.Add(result.Status == PreviewStatus.Truncated || result.Capture.Truncated
                ? "미리보기가 이미지 상한을 넘어 잘렸으므로 적용할 수 없음(D8)."
                : "미리보기 상태가 ok가 아님.");
        }
        if (result.Statements.Any(s => s.Kind == ChangeOperation.Exec))
        {
            // EXEC [적용]은 2차(2026-11-13): 원본에서 프로시저를 다시 돌려 미리보기와 대조하는 경로가 아직 없다.
            failures.Add(ApplyPrecondition.ProcApplyNotSupported);
            reasons.Add("저장 프로시저 호출(EXEC)이 있는 미리보기는 아직 적용할 수 없음(미리보기 전용).");
        }
        if (!string.Equals(ContractFormulas.SqlHash(record.Sql), result.SqlHash, StringComparison.Ordinal))
        {
            failures.Add(ApplyPrecondition.SqlHashMismatch);
            reasons.Add("저장된 SQL의 해시가 미리보기 해시와 다름.");
        }
        if (request.SqlHash is { } requested && !string.Equals(requested, result.SqlHash, StringComparison.Ordinal))
        {
            failures.Add(ApplyPrecondition.SqlHashMismatch);
            reasons.Add("요청의 sqlHash가 미리보기 해시와 다름(편집기 내용이 바뀜).");
        }
        if (Clock.GetUtcNow() - result.CreatedAt > Settings.PreviewExpiry)
        {
            failures.Add(ApplyPrecondition.PreviewExpired);
            reasons.Add($"미리보기를 만든 지 {Settings.PreviewExpiry.TotalMinutes:0}분이 지남.");
        }
        var b = result.Baseline;
        if (b.BackupFinishedAt != context.BackupFinishedAt
            || !string.Equals(b.OriginDb, context.OriginDb, StringComparison.Ordinal)
            || !string.Equals(b.SandboxDb, context.SandboxDb, StringComparison.Ordinal)
            || !string.Equals(b.SnapshotDb, context.SnapshotDb, StringComparison.Ordinal))
        {
            failures.Add(ApplyPrecondition.PreviewExpired);
            reasons.Add("미리보기를 만든 뒤 기준선이 바뀜(새로 고침). 다시 미리보기 할 것.");
        }

        // SingleServer는 레코드 전체 일치(기존), Split은 차단 항목 일치(FingerprintComparison). 미리보기 지문은 샌드박스 지문과 같아야 한다.
        Fingerprint originFingerprint;
        try { originFingerprint = await origin.ReadFingerprintAsync(context.OriginDb, ct); }
        catch (SandboxUnavailableException ex)
        {
            // 원본에 연결할 수 없음(N4): 나머지 원본 조건은 읽지 않고 막는다. 메시지는 번호만 담는다(OriginConnections).
            failures.Add(ApplyPrecondition.OriginUnreachable);
            reasons.Add(ex.Message + ".");
            return (failures, reasons);
        }
        var comparison = FingerprintComparison.Compare(originFingerprint, context.Fingerprint, Settings.Mode);
        if (!context.FingerprintMatch || !comparison.Match || b.Fingerprint != context.Fingerprint)
        {
            failures.Add(ApplyPrecondition.FingerprintMismatch);
            reasons.Add("원본·샌드박스·미리보기의 환경 지문이 같지 않음." + (comparison.Mismatches.Count > 0 ? " " + string.Join("; ", comparison.Mismatches) + "." : ""));
        }
        // Split은 가장하지 않는다(Host가 ExecuteAsUser = null). 이 조건은 SingleServer에서만 의미가 있다.
        if (Settings.Mode == ServerMode.SingleServer && Settings.ExecuteAsUser is { } user && !await origin.UserExistsAsync(context.OriginDb, user, ct))
        {
            failures.Add(ApplyPrecondition.ExecuteAsUserMissing);
            reasons.Add($"원본에 실행 사용자 {user}가 없음(원본 준비 단계에서 만들어 둘 것, 적용 중에는 DDL을 하지 않음).");
        }
        return (failures, reasons);
    }

    /// <summary>재분석 결과가 미리보기 문장과 같은지. 다르면 이유.</summary>
    static string? SameStatements(AnalysisResult analysis, IReadOnlyList<StatementResult> preview)
    {
        if (!analysis.IsAccepted)
            return $"재분석에서 거부됨({string.Join(", ", analysis.Rejects.Select(r => r.RuleId).Distinct())}). 기준선이 바뀌었을 수 있음.";
        var a = analysis.Statements.OrderBy(s => s.Index).ToList();
        var p = preview.OrderBy(s => s.Index).ToList();
        if (a.Count != p.Count) return $"재분석 문장 수({a.Count})가 미리보기({p.Count})와 다름.";
        for (var i = 0; i < a.Count; i++)
            if (a[i].Index != p[i].Index || a[i].Kind != p[i].Kind || !string.Equals(a[i].Target, p[i].Target, StringComparison.Ordinal))
                return $"재분석 문장 {a[i].Index}이(가) 미리보기와 다름.";
        return null;
    }

    static StatementResult NotRun(AnalyzedStatement st) => new()
    {
        Index = st.Index,
        Batch = st.Batch,
        Position = st.Position,
        Kind = st.Kind,
        Target = st.Target,
        Span = st.Span,
        Status = StatementStatus.NotRun,
    };

    ApplyResult Make(PreviewRecord record, ApplyStatus status, ApplyVerification verification, IReadOnlyList<StatementResult> statements, string? message, Run run) => new()
    {
        PreviewId = record.Result.PreviewId,
        Status = status,
        FinishedAt = Clock.GetUtcNow(),
        Verification = verification,
        Statements = statements,
        Message = message,
        TimingsMs = run.Timings(),
    };

    static string Status(StatementStatus s) => s switch
    {
        StatementStatus.TriggerBlocked => "트리거에 거부",
        StatementStatus.Failed => "실패",
        _ => "중단",
    };

    static IEnumerable<string> Optional(string? s) => s is null ? Array.Empty<string>() : new[] { s };

    static string Join(string? first, IEnumerable<string> rest)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(first)) parts.Add(first);
        parts.AddRange(rest);
        return string.Join(" ", parts);
    }
}
