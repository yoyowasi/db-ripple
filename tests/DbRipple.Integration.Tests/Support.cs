using System.Text.Json;
using DbRipple.Analysis;
using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Diff;
using DbRipple.Execution;
using DbRipple.Preview;
using DbRipple.TestSupport;

namespace DbRipple.Integration.Tests;

/// <summary>DB 연결 환경 변수가 없으면 건너뛰는 Theory.</summary>
public sealed class SqlTheoryAttribute : TheoryAttribute
{
    /// <summary>환경 변수가 없으면 Skip을 채운다.</summary>
    public SqlTheoryAttribute()
    {
        if (!TestDb.IsConfigured) Skip = $"{TestDb.ConnectionEnvVar} 없음";
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationCollection : ICollectionFixture<LabFixture>
{
    public const string Name = "int-db";
}

/// <summary>CascadeLab 두 변형의 전용 원본(dbrt_int_)을 시드하고 SandboxManager.RefreshAsync로 기준선을 만든다(Eval.Cli와 같은 경로). 끝에 지운다.</summary>
public sealed class LabFixture : IAsyncLifetime
{
    Lab? @default, rt;

    public async Task InitializeAsync()
    {
        if (!TestDb.IsConfigured) return;
        @default = await Lab.CreateAsync(CascadeLabVariant.Default);
        rt = await Lab.CreateAsync(CascadeLabVariant.RecursiveTriggers);
    }

    public async Task DisposeAsync()
    {
        if (rt is not null) await rt.DisposeAsync();
        if (@default is not null) await @default.DisposeAsync();
    }

    public Lab For(HandCase c) => c.Database == "CascadeLab_RT" ? rt! : @default!;
}

/// <summary>기준선 한 벌과 Host와 같은 조립의 파이프라인.</summary>
public sealed class Lab : IAsyncDisposable
{
    readonly TestDatabases cleanup;
    readonly string dir;

    Lab(string origin, TestDatabases cleanup, string dir, SandboxManager manager, PreviewStore store, PreviewPipeline pipeline)
    {
        Origin = origin;
        this.cleanup = cleanup;
        this.dir = dir;
        Manager = manager;
        Store = store;
        Pipeline = pipeline;
    }

    public string Origin { get; }
    public SandboxManager Manager { get; }
    public PreviewStore Store { get; }
    public PreviewPipeline Pipeline { get; }

    public static Task<Lab> CreateAsync(CascadeLabVariant variant) =>
        CreateAsync(variant == CascadeLabVariant.Default ? "cl" : "clrt", origin => CascadeLabSeed.CreateAsync(origin, variant));

    /// <summary>batches로 만든 작은 원본(dbrt_int_{tag}_)의 기준선과 파이프라인.</summary>
    public static Task<Lab> CreateCustomAsync(string tag, params string[] batches) =>
        CreateAsync(tag, async origin =>
        {
            await TestDb.ExecAsync("master", $"CREATE DATABASE [{origin}];");
            await TestDb.ExecAsync(origin, batches);
        });

    static async Task<Lab> CreateAsync(string tag, Func<string, Task> seed)
    {
        var origin = TestDb.NewName("int", tag);
        var cleanup = new TestDatabases(origin + "_base", origin + "_sbx", origin);
        var backup = TestDb.BackupPath(origin);
        cleanup.ContainerFiles.Add(backup);
        var dir = Path.Combine(Path.GetTempPath(), "dbripple-int", origin);
        try
        {
            await seed(origin);
            var manager = new SandboxManager(new SandboxManagerOptions
            {
                ConnectionString = TestDb.ConnectionString(),
                OriginDb = origin,
                SandboxDb = origin + "_sbx",
                SnapshotDb = origin + "_base",
                BackupPath = backup,
                ManifestPath = Path.Combine(dir, "manifest.json"),
            }, new TriggerAnalyzer(), TimeProvider.System);
            await manager.RefreshAsync(default);
            var executor = new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName }, new NetDiffer());
            var store = new PreviewStore(Path.Combine(dir, "previews"), TimeProvider.System);
            var pipeline = new PreviewPipeline(manager, new BatchAnalyzer(), executor, new ChangeCollector(), new CausalResolver(), store, TimeProvider.System);
            return new Lab(origin, cleanup, dir, manager, store, pipeline);
        }
        catch
        {
            await cleanup.DisposeAsync();
            throw;
        }
    }

    /// <summary>샌드박스 대 기준 스냅샷의 사용자 테이블(감사 저장소 제외) 차이 건수(트랜잭션 없이).</summary>
    public async Task<int> SandboxDiffCountAsync()
    {
        var ctx = await Manager.GetContextAsync(default);
        var tables = ctx.Catalog.ToCaptureInput().Where(t => !AuditTriggerGenerator.IsCaptureStore(t)).ToList();
        await using var conn = await TestDb.OpenAsync(ctx.SandboxDb);
        return (await new NetDiffer().DiffAsync(new SqlSession { Connection = conn }, ctx.SandboxDb, ctx.SnapshotDb, tables, default)).Count;
    }

    public async ValueTask DisposeAsync()
    {
        await cleanup.DisposeAsync();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>cascadelab/cases 손 정답에서 이 테스트가 쓰는 부분.</summary>
public sealed record HandCase(
    string Id, string Database, IReadOnlyList<string> Statements, string Status, int? ErrorNumber,
    IReadOnlyList<(int Index, string Status)> StatementStatus,
    IReadOnlyList<(string Table, string Operation, long RowCount, int Depth)> Firings,
    IReadOnlyList<string> ChangeKeys)
{
    public static IReadOnlyList<HandCase> All { get; } = Load();

    public static HandCase Get(string id) => All.Single(c => c.Id == id);

    static List<HandCase> Load()
    {
        var list = new List<HandCase>();
        // 이 통합 테스트의 범위는 CL01~CL08b(answer-format v0.1 모양: changes 행마다 key)다. CL09 이후는 v0.2 모양(IDENTITY ref·키 없는 다중집합·residualCommitted)이라
        // Execution.Tests Week3 회귀와 평가 하네스가 맡는다.
        foreach (var f in Directory.GetFiles(Path.Combine(RepoPaths.CascadeLab, "cases"), "CL*.json").Order(StringComparer.Ordinal)
                     .Where(f => string.CompareOrdinal(Path.GetFileName(f), "CL09") < 0))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            var e = r.GetProperty("expected");
            var keys = new List<string>();
            foreach (var ch in e.GetProperty("changes").EnumerateArray())
            {
                var table = ch.GetProperty("table").GetString()!;
                var cols = ch.GetProperty("keyColumns").EnumerateArray().Select(c => c.GetString()!).ToList();
                foreach (var kind in new[] { "inserted", "updated", "deleted" })
                    foreach (var row in ch.GetProperty(kind).EnumerateArray())
                        keys.Add($"{table}|{kind.ToUpperInvariant()}|{string.Join(",", cols.Select(c => AuditJson.ToText(row.GetProperty("key").GetProperty(c))))}");
            }
            list.Add(new HandCase(
                r.GetProperty("id").GetString()!,
                r.GetProperty("database").GetString()!,
                [.. r.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("sql").GetString()!)],
                e.GetProperty("status").GetString()!,
                e.GetProperty("errorNumber").ValueKind == JsonValueKind.Null ? null : e.GetProperty("errorNumber").GetInt32(),
                [.. e.GetProperty("statementStatus").EnumerateArray().Select(s => (s.GetProperty("index").GetInt32(), s.GetProperty("status").GetString()!))],
                [.. e.GetProperty("firings").EnumerateArray().Select(x => (x.GetProperty("table").GetString()!, x.GetProperty("operation").GetString()!,
                    x.GetProperty("rowCount").GetInt64(), x.GetProperty("depth").GetInt32()))],
                keys));
        }
        return list;
    }
}
