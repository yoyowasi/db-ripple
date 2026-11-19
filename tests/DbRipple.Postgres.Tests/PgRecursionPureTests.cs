using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Execution;

namespace DbRipple.Postgres.Tests;

/// <summary>
/// 같은 트리거의 재귀 발동(DB 없이). PostgreSQL은 트리거가 자기 테이블을 다시 고치면 그 트리거가 다시 돈다(SQL Server RECURSIVE_TRIGGERS OFF 같은 설정이 없음).
/// 감사 헤더 모양은 통합 테스트 PgIntegrationTests.Recursive_trigger_*의 기대값과 같다: 후위 기록이라 깊이 3 → 2 → 1 순.
/// </summary>
public class PgRecursionPureTests
{
    const int Node = 300;

    [Fact]
    public void Self_recursive_trigger_is_attributed_at_every_depth_with_the_postgres_fingerprint()
    {
        var catalog = new DbCatalog(
            Tables: [new(Node, "s", "node", TemporalType.None, false, "app")],
            Columns:
            [
                new(Node, 1, "id", "integer", false, false, false, false, false, null),
                new(Node, 2, "n", "integer", false, false, false, false, false, null),
                new(Node, 3, "note", "text", false, true, false, false, false, null),
            ],
            KeyIndexes: [new(Node, 1, "node_pkey", true, true, 1, "id", false)],
            ForeignKeys: [],
            Triggers: [],
            Files: []);
        var last = new AuditOrderInfo(CaptureManifest.Last, CaptureManifest.Last, CaptureManifest.Last);
        var ctx = new BaselineContext
        {
            OriginDb = "shop", SandboxDb = "shop_dbr_sandbox", SnapshotDb = "shop_dbr_sandbox",
            BackupFinishedAt = DateTimeOffset.UnixEpoch,
            Fingerprint = PgSandboxManager.FingerprintOf("18.1", 18, "C.UTF-8"),
            FingerprintMatch = true,
            Catalog = catalog,
            Triggers =
            [
                new TriggerStaticInfo
                {
                    Trigger = "s.t_node_bump", ObjectId = 900, Table = "s.node", Events = [ChangeOperation.Update],
                    IsInsteadOf = false, IsDisabled = false, IsEncrypted = false, IsAuditTrigger = false,
                    WriteTargets = ["s.node"], WriteTargetsUnknown = false, HasDynamicSql = false, HasRollback = false, ExternalSideEffects = [],
                },
            ],
            Manifest = new CaptureManifest("pg-test", [new ManifestTable("s.node", true, last, [], [])], [], []),
        };
        var analysis = new PgBatchAnalyzer().Analyze("UPDATE s.node SET note = 'x' WHERE id = 1", ctx);
        var headers = new[] { H(1, 3), H(2, 2), H(3, 1) };
        var images = new[]
        {
            I(1, ImageKind.Deleted, """{"id":"1","n":"1","note":"x"}"""), I(1, ImageKind.Inserted, """{"id":"1","n":"2","note":"x"}"""),
            I(2, ImageKind.Deleted, """{"id":"1","n":"0","note":"x"}"""), I(2, ImageKind.Inserted, """{"id":"1","n":"1","note":"x"}"""),
            I(3, ImageKind.Deleted, """{"id":"1","n":"0","note":"a"}"""), I(3, ImageKind.Inserted, """{"id":"1","n":"0","note":"x"}"""),
        };
        var statement = new StatementResult
        {
            Index = 1, Batch = 1, Position = 1, Target = "s.node", Kind = ChangeOperation.Update,
            Span = Assert.Single(analysis.Statements).Span, Status = StatementStatus.Ok, RowsAffected = 1,
        };
        var net = ImageNetDiff.Compute(headers, images, catalog.ToCaptureInput().ToDictionary(t => t.ObjectId)).Changes;
        var raw = new RawCapture
        {
            Status = PreviewStatus.Ok,
            Executions = [new StatementExecution { Result = statement, Boundary = 3, ModeAfter = ImageMode.Full }],
            Boundary0 = 0, Headers = headers, Images = images, NetChanges = net, SandboxDirty = false,
            Capture = new CaptureInfo { ImageCount = images.Length, Cap = 100, Truncated = false, ManifestVersion = ctx.Manifest.Version },
            Timings = new ExecutionTimings(),
        };
        var firings = new CausalResolver().Resolve(new ResolveInput
        {
            Statements = [statement], Collected = new ChangeCollector().Collect(raw, analysis, ctx), Net = net, Baseline = ctx,
        }).Model.Firings.OrderBy(f => f.CaptureSeq).ToList();

        string Line(Firing f) => $"d{f.Depth} {f.Cause?.Kind} {f.Cause?.Confidence} parent={firings.FirstOrDefault(p => p.Id == f.Cause?.ParentFiringId)?.Depth.ToString() ?? "-"} [{string.Join(",", f.Cause?.TriggerCandidates ?? [])}]";
        PgAssert.Same(
            "d3 Trigger Inferred parent=2 [s.t_node_bump]\nd2 Trigger Inferred parent=1 [s.t_node_bump]\nd1 Direct Certain parent=- []",
            string.Join("\n", firings.Select(Line)), "재귀 트리거 묶음의 원인(깊이 3 → 1)");
    }

    static AuditHeaderRow H(long seq, int depth) => new()
    {
        CaptureSeq = seq, TableObjectId = Node, TableName = "[s].[node]", Operation = ChangeOperation.Update, InsertedCount = 1, DeletedCount = 1,
        NestLevel = depth, TranCount = 2, ImageMode = ImageMode.Full,
    };

    static AuditRowImageRow I(long seq, ImageKind kind, string row) =>
        new() { CaptureSeq = seq, ImageKind = kind, Ordinal = 1, KeyJson = """{"id":"1"}""", RowJson = row };
}
