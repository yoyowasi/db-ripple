using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Api.Tests;

/// <summary>손으로 정할 수 있는 시계(만료 시험용).</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;

    /// <summary>지금 시각.</summary>
    public override DateTimeOffset GetUtcNow() => now;
    /// <summary>시각을 앞으로.</summary>
    public void Advance(TimeSpan by) => now += by;
    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>계약 타입 샘플 만들기.</summary>
public static class TestData
{
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-10T09:00:00+00:00");

    public static Fingerprint Fingerprint() => new("17.0.1000.7", "Developer Edition", 170, false, 1, 0, 5496, "SQL_Latin1_General_CP1_CI_AS");

    public static BaselineContext Baseline(bool fingerprintMatch = true) => new()
    {
        OriginDb = "Origin", SandboxDb = "Sandbox", SnapshotDb = "Snap",
        BackupFinishedAt = Start.AddMinutes(-5),
        Fingerprint = Fingerprint(), FingerprintMatch = fingerprintMatch,
        Catalog = new DbCatalog([], [], [], [], [], []), Triggers = [],
        Manifest = new CaptureManifest("m-test", [], [], []),
    };

    public static SourceSpan Span(int line = 1) => new() { StartLine = line, StartColumn = 1, EndLine = line, EndColumn = 10 };

    public static AnalyzedStatement Statement(int index, string sql = "DELETE FROM dbo.T;") => new()
    {
        Index = index, Batch = 1, Position = index, Kind = ChangeOperation.Delete, Target = "dbo.T", Span = Span(index), Sql = sql,
    };

    public static AnalysisResult Accepted(params string[] warnings) => new()
    {
        Statements = [Statement(1)],
        Warnings = [.. warnings.Select(w => new Diagnostic { Span = Span(), Severity = Severity.Warning, RuleId = w, Message = w })],
    };

    public static AnalysisResult Rejected() => new()
    {
        Statements = [Statement(1)],
        Rejects = [new Diagnostic { StatementIndex = 1, Span = Span(), Severity = Severity.Error, RuleId = "REJECT_TRUNCATE", Message = "TRUNCATE", DocRef = "A21" }],
    };

    public static StatementResult StatementResult(int index = 1, StatementStatus status = StatementStatus.Ok) => new()
    {
        Index = index, Batch = 1, Position = index, Kind = ChangeOperation.Delete, Target = "dbo.T", Span = Span(index), Status = status, RowsAffected = 1,
    };

    public static RowKey Key(string id) => new() { Kind = KeyKind.PK, Columns = ["Id"], Values = [id] };

    public static NetChange Net(string table, NetChangeKind kind, string id = "1", long multiplicity = 1, params string[] firingIds) => new()
    {
        Table = table, Key = Key(id), Change = kind, Multiplicity = multiplicity,
        Before = kind is NetChangeKind.Inserted or NetChangeKind.MultisetAdded ? null : new Dictionary<string, string?> { ["Id"] = id },
        After = kind is NetChangeKind.Deleted or NetChangeKind.MultisetRemoved ? null : new Dictionary<string, string?> { ["Id"] = id },
        ExplainedBy = new ExplainedBy { FiringIds = firingIds },
    };

    public static RowChange Row(string firingId, int n) => new()
    {
        Id = $"rc-{firingId[2..]}-{n}", FiringId = firingId, Operation = ChangeOperation.Delete, Key = Key(n.ToString()),
        Before = new Dictionary<string, string?> { ["Id"] = n.ToString() }, Pairing = Pairing.SingleRow, FinalAfterSource = FinalAfterSource.Audit,
    };

    public static Firing Firing(long seq, int statement = 1) => new()
    {
        Id = ContractIds.Firing(seq), CaptureSeq = seq, StatementIndex = statement, Depth = 1, Table = "dbo.T", Operation = ChangeOperation.Delete,
        RowCount = 1, ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
        Cause = new Cause { Kind = CauseKind.Direct, Confidence = Confidence.Certain, Evidence = [Evidence.Depth] },
        OrderGuarantee = OrderGuarantee.Guaranteed,
    };

    public static CaptureInfo Capture() => new() { ImageCount = 3, Cap = 100_000, Truncated = false, ManifestVersion = "m-test" };

    public static RawCapture Raw(PreviewStatus status = PreviewStatus.Ok, bool dirty = false, IReadOnlyList<NetChange>? net = null, IReadOnlyList<NetChange>? residual = null) => new()
    {
        Status = status,
        Executions = [new StatementExecution { Result = StatementResult(1, status == PreviewStatus.Failed ? StatementStatus.Failed : StatementStatus.Ok), Boundary = 7 }],
        Boundary0 = 0, SandboxDirty = dirty, Capture = Capture(),
        NetChanges = net ?? [], ResidualCommitted = residual ?? [],
        Timings = new ExecutionTimings { Execute = 11, ReadLog = 12, Reread = 13, NetDiff = 14 },
    };

    public static PreviewRecord Record(string id, DateTimeOffset createdAt, int rowChanges = 5, int nets = 5) => new()
    {
        Sql = "DELETE FROM dbo.T;",
        RowChanges = [.. Enumerable.Range(1, rowChanges).Select(i => Row(i % 2 == 0 ? "f-1002" : "f-1001", i))],
        NetChanges = [.. Enumerable.Range(1, nets).Select(i => Net("dbo.T", NetChangeKind.Deleted, i.ToString(), 1, i % 2 == 0 ? "f-1002" : "f-1001"))],
        Result = new PreviewResult
        {
            PreviewId = id, Status = PreviewStatus.Ok, CreatedAt = createdAt, SqlHash = PreviewHash("DELETE FROM dbo.T;"),
            Baseline = new BaselineInfo
            {
                OriginDb = "Origin", SandboxDb = "Sandbox", SnapshotDb = "Snap", BackupFinishedAt = createdAt.AddMinutes(-5), Fingerprint = Fingerprint(),
            },
            Statements = [StatementResult()],
            Firings = [Firing(1001), Firing(1002)],
            Graph = new GraphDto
            {
                Nodes = [new StatementNode { Id = "st-1", StatementIndex = 1, Label = "DELETE", RowsAffected = 1, Status = StatementStatus.Ok }, new FiringNode { Id = "f-1001", FiringId = "f-1001", Table = "dbo.T", Operation = ChangeOperation.Delete, RowCount = 1, Depth = 1, CaptureSeq = 1001 }],
                Edges = [new GraphEdge { Id = "e1", Source = "st-1", Target = "f-1001", Kind = EdgeKind.Direct, Confidence = Confidence.Certain, OrderGuarantee = OrderGuarantee.Guaranteed, Evidence = [Evidence.Depth] }],
            },
            Capture = Capture(),
            TimingsMs = new TimingsMs { Analyze = 1 },
        },
    };

    static string PreviewHash(string sql) => DbRipple.Preview.PreviewPipeline.SqlHash(sql);
}
