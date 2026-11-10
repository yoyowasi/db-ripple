using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbRipple.Contracts;

namespace DbRipple.Preview;

/// <summary>
/// 미리보기 파이프라인(§5.2). 컴포넌트는 인터페이스로만 받는다(구현체 조립은 Host·Eval.Cli).
/// 순서: (오염이면 RefreshAsync) → 미리보기 1건 잠금 → 지문 확인 → C6 Analyze(거부면 저장 없이 반환) → C10 Execute
/// → SandboxDirty면 MarkDirty → C11 Collect → C13 Resolve → PreviewResult 조립(netSummary, timingsMs, sqlHash) → C14 Save.
/// <list type="bullet">
/// <item>RefreshAsync는 잠금이 풀리길 기다리므로 잠금을 쥔 채 부르면 교착한다. 그래서 "오염 확인 → (재구축) → 잠금 → 다시 오염 확인"을 되풀이한다(최대 <see cref="MaxRefreshAttempts"/>번,
/// 그래도 오염이면 InvalidOperationException).</item>
/// <item>잠금 충돌, 구축 중, 기준선 없음, 환경 지문 불일치는 InvalidOperationException(API 409).</item>
/// <item>실행기가 예외를 던지면(연결 끊김 등) 샌드박스 상태를 알 수 없으므로 오염으로 표시하고 예외를 그대로 다시 던진다.</item>
/// <item>sqlHash = "sha256:" + SHA-256(UTF-8 바이트, 정규화 없음) 소문자 16진(Q17).</item>
/// <item>netSummary는 순 변경(NetChanges)을 테이블별로 센다. 건수는 Multiplicity의 합(Q16).</item>
/// <item>timingsMs.serialize는 저장할 기록(PreviewRecord)을 ContractJson으로 직렬화하는 시간이다(파일 쓰기 제외: 결과에 담긴 시간이라 저장 시간은 잴 수 없다).</item>
/// </list>
/// </summary>
public sealed class PreviewPipeline(
    ISandboxManager sandbox,
    IBatchAnalyzer analyzer,
    IPreviewExecutor executor,
    IChangeCollector collector,
    ICausalResolver resolver,
    IPreviewStore store,
    TimeProvider clock) : IPreviewPipeline
{
    /// <summary>오염 확인 → 재구축 → 잠금 시도 횟수 상한.</summary>
    public const int MaxRefreshAttempts = 3;

    /// <summary>조립한 구성 요소(검사·진단용).</summary>
    public IReadOnlyList<object> Components { get; } = [sandbox, analyzer, executor, collector, resolver, store, clock];

    /// <summary>"sha256:" + 소문자 16진(Q17: UTF-8, 정규화 없음).</summary>
    public static string SqlHash(string sql) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    /// <summary>순 변경을 테이블별 건수로(Q16: Multiplicity 합). 테이블 이름 순.</summary>
    public static IReadOnlyList<NetSummaryRow> Summarize(IEnumerable<NetChange> net) => net
        .GroupBy(n => n.Table, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new NetSummaryRow
        {
            Table = g.Key,
            Inserted = g.Where(n => n.Change == NetChangeKind.Inserted).Sum(n => n.Multiplicity),
            Updated = g.Where(n => n.Change == NetChangeKind.Updated).Sum(n => n.Multiplicity),
            Deleted = g.Where(n => n.Change == NetChangeKind.Deleted).Sum(n => n.Multiplicity),
            MultisetRemoved = g.Where(n => n.Change == NetChangeKind.MultisetRemoved).Sum(n => n.Multiplicity),
            MultisetAdded = g.Where(n => n.Change == NetChangeKind.MultisetAdded).Sum(n => n.Multiplicity),
        })
        .ToList();

    /// <inheritdoc />
    public async Task<PreviewOutcome> RunAsync(string sql, PreviewOptions options, IEvaluationHook? hook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(options);

        await using var preview = await AcquireReadyAsync(ct);
        var baseline = await sandbox.GetContextAsync(ct);
        if (!baseline.FingerprintMatch)
            throw new InvalidOperationException("원본과 샌드박스의 환경 지문이 달라 미리보기를 막습니다(§5.1 6).");

        var sw = Stopwatch.StartNew();
        var analysis = analyzer.Analyze(sql, baseline);
        var analyzeMs = sw.ElapsedMilliseconds;
        if (!analysis.IsAccepted) return new PreviewOutcome { Analysis = analysis };

        RawCapture raw;
        try
        {
            raw = await executor.ExecuteAsync(analysis, baseline, options, hook, ct);
        }
        catch
        {
            sandbox.MarkDirty("실행기 예외로 샌드박스 상태를 알 수 없음");
            throw;
        }
        if (raw.SandboxDirty) sandbox.MarkDirty($"롤백 뒤 남은 변경 {raw.ResidualCommitted.Count}건(감사 트리거가 자동 커밋으로 기록)");

        sw.Restart();
        var collected = collector.Collect(raw, analysis, baseline);
        var resolution = resolver.Resolve(new ResolveInput
        {
            Statements = raw.Executions.Select(e => e.Result).ToList(),
            Collected = collected,
            Net = raw.NetChanges,
            Baseline = baseline,
            StatementSql = analysis.Statements.ToDictionary(s => s.Index, s => s.Sql),
        });
        var resolveMs = sw.ElapsedMilliseconds;

        var model = resolution.Model;
        var result = new PreviewResult
        {
            PreviewId = NewId(),
            Status = raw.Status,
            CreatedAt = clock.GetUtcNow(),
            SqlHash = SqlHash(sql),
            Baseline = new BaselineInfo
            {
                OriginDb = baseline.OriginDb,
                SandboxDb = baseline.SandboxDb,
                SnapshotDb = baseline.SnapshotDb,
                BackupFinishedAt = baseline.BackupFinishedAt,
                Fingerprint = baseline.Fingerprint,
            },
            Statements = raw.Executions.Select(e => e.Result).ToList(),
            Firings = model.Firings,
            Residuals = model.Residuals,
            ResidualCommitted = raw.ResidualCommitted,
            NetSummary = Summarize(model.NetChanges),
            Graph = resolution.Graph,
            Warnings = analysis.Warnings,
            Capture = raw.Capture,
            TimingsMs = new TimingsMs
            {
                Analyze = analyzeMs,
                Execute = raw.Timings.Execute,
                ReadLog = raw.Timings.ReadLog,
                Reread = raw.Timings.Reread,
                NetDiff = raw.Timings.NetDiff,
                Resolve = resolveMs,
            },
        };
        var record = new PreviewRecord { Result = result, RowChanges = model.RowChanges, NetChanges = model.NetChanges, Sql = sql };

        sw.Restart();
        JsonSerializer.SerializeToUtf8Bytes(record, ContractJson.Options);
        record = record with { Result = result with { TimingsMs = result.TimingsMs with { Serialize = sw.ElapsedMilliseconds } } };
        await store.SaveAsync(record, ct);
        return new PreviewOutcome { Analysis = analysis, Record = record };
    }

    // 오염이면 (잠금 없이) 재구축하고, 잠금을 얻은 뒤 다시 오염이 아닌지 확인한다.
    async Task<IAsyncDisposable> AcquireReadyAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxRefreshAttempts; attempt++)
        {
            if ((await sandbox.GetStatusAsync(ct)).State == SandboxState.Dirty) await sandbox.RefreshAsync(ct);
            var held = await sandbox.AcquirePreviewLockAsync(ct);
            try
            {
                if ((await sandbox.GetStatusAsync(ct)).State != SandboxState.Dirty) return held;
            }
            catch
            {
                await held.DisposeAsync();
                throw;
            }
            await held.DisposeAsync(); // 그 사이 다른 미리보기가 오염시켰다
        }
        throw new InvalidOperationException("샌드박스가 계속 오염 상태라 미리보기를 시작할 수 없습니다.");
    }

    string NewId() => $"pv_{clock.GetLocalNow():yyyyMMdd}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
}
