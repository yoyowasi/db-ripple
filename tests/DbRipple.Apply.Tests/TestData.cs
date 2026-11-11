using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Apply.Tests;

/// <summary>
/// DB 없는 단위 테스트용 기준선·미리보기. 테이블:
/// <list type="bullet">
/// <item>dbo.P(id int IDENTITY PK, name nvarchar, created datetime2 DEFAULT GETDATE(), label = 'P' + id 계산 열)</item>
/// <item>dbo.C(id int IDENTITY PK, p_id int FK → dbo.P.id, qty int) — CL16형 새 IDENTITY를 참조하는 자식</item>
/// <item>dbo.T(id int PK, v int) — 일반 UPDATE 대상</item>
/// <item>dbo.H(a int, b int) — 키 없는 테이블</item>
/// <item>dbo.X(id int PK) — 미리보기에 없는 테이블</item>
/// </list>
/// </summary>
internal static class TestData
{
    public static readonly DateTimeOffset Start = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset BackupAt = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    public const string Sql = "UPDATE dbo.T SET v = 2 WHERE id = 1;";

    public static Fingerprint Fingerprint(string version = "17.0.1000.7") =>
        new(version, "Developer Edition (64-bit)", 170, false, 1, 0, 5496, "SQL_Latin1_General_CP1_CI_AS");

    public static DbCatalog Catalog()
    {
        var tables = new List<CatalogTable>
        {
            new(1, "dbo", "P", TemporalType.None, false),
            new(2, "dbo", "C", TemporalType.None, false),
            new(3, "dbo", "T", TemporalType.None, false),
            new(4, "dbo", "H", TemporalType.None, false),
            new(5, "dbo", "X", TemporalType.None, false),
        };
        var columns = new List<CatalogColumn>
        {
            new(1, 1, "id", "int", false, false, false, true, false, null),
            new(1, 2, "name", "nvarchar", false, false, false, false, false, null),
            new(1, 3, "created", "datetime2", false, true, false, false, false, "(getdate())"),
            new(1, 4, "label", "nvarchar", false, true, true, false, false, null),
            new(2, 1, "id", "int", false, false, false, true, false, null),
            new(2, 2, "p_id", "int", false, false, false, false, false, null),
            new(2, 3, "qty", "int", false, false, false, false, false, null),
            new(3, 1, "id", "int", false, false, false, false, false, null),
            new(3, 2, "v", "int", false, true, false, false, false, null),
            new(4, 1, "a", "int", false, true, false, false, false, null),
            new(4, 2, "b", "int", false, true, false, false, false, null),
            new(5, 1, "id", "int", false, false, false, false, false, null),
        };
        var keys = new List<CatalogIndexColumn>
        {
            new(1, 1, "PK_P", true, true, 1, "id", false),
            new(2, 1, "PK_C", true, true, 1, "id", false),
            new(3, 1, "PK_T", true, true, 1, "id", false),
            new(5, 1, "PK_X", true, true, 1, "id", false),
        };
        var fks = new List<CatalogForeignKeyColumn>
        {
            new(100, "FK_C_P", 2, 1, "NO_ACTION", "NO_ACTION", false, false, 1, "p_id", "id"),
        };
        return new DbCatalog(tables, columns, keys, fks, [], []);
    }

    public static CaptureManifest Manifest() => new("1.0",
        [
            new ManifestTable("dbo.P", true, new AuditOrderInfo("LAST", "LAST", "LAST"), [],
                [new VolatileColumn("id", "IDENTITY"), new VolatileColumn("created", "NONDETERMINISTIC_DEFAULT")]),
            new ManifestTable("dbo.C", true, new AuditOrderInfo("LAST", "LAST", "LAST"), [], [new VolatileColumn("id", "IDENTITY")]),
            new ManifestTable("dbo.T", true, new AuditOrderInfo("LAST", "LAST", "LAST"), [], []),
            new ManifestTable("dbo.H", true, new AuditOrderInfo("LAST", "LAST", "LAST"), [], []),
            new ManifestTable("dbo.X", true, new AuditOrderInfo("LAST", "LAST", "LAST"), [], []),
        ], [], []);

    public static BaselineContext Context(bool fingerprintMatch = true) => new()
    {
        OriginDb = "Origin",
        SandboxDb = "Sandbox",
        SnapshotDb = "Sandbox_base",
        BackupFinishedAt = BackupAt,
        Fingerprint = Fingerprint(),
        FingerprintMatch = fingerprintMatch,
        Catalog = Catalog(),
        Triggers = [],
        Manifest = Manifest(),
    };

    public static SourceSpan Span(int line = 1) => new() { StartLine = line, StartColumn = 1, EndLine = line, EndColumn = 30 };

    public static StatementResult Statement(int index, long? rows, string target = "dbo.T", ChangeOperation kind = ChangeOperation.Update, StatementStatus status = StatementStatus.Ok) => new()
    {
        Index = index, Batch = 1, Position = index, Kind = kind, Target = target, Span = Span(index), Status = status, RowsAffected = rows,
    };

    public static AnalyzedStatement Analyzed(StatementResult s, string sql = Sql) => new()
    {
        Index = s.Index, Batch = s.Batch, Position = s.Position, Kind = s.Kind, Target = s.Target, Span = s.Span, Sql = sql,
    };

    public static Dictionary<string, string?> Row(params (string Column, string? Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);

    public static RowKey Key(string value) => new() { Kind = KeyKind.PK, Columns = ["id"], Values = [value] };

    public static NetChange Inserted(string table, string id, Dictionary<string, string?> after) =>
        new() { Table = table, Key = Key(id), Change = NetChangeKind.Inserted, Multiplicity = 1, After = after };

    public static NetChange Updated(string table, string id, Dictionary<string, string?> before, Dictionary<string, string?> after, params string[] changed) =>
        new() { Table = table, Key = Key(id), Change = NetChangeKind.Updated, Multiplicity = 1, Before = before, After = after, ChangedColumns = changed };

    public static NetChange Deleted(string table, string id, Dictionary<string, string?> before) =>
        new() { Table = table, Key = Key(id), Change = NetChangeKind.Deleted, Multiplicity = 1, Before = before };

    public static NetChange Multiset(string table, NetChangeKind kind, long n, Dictionary<string, string?> image) =>
        new()
        {
            Table = table, Key = new RowKey { Kind = KeyKind.ROW }, Change = kind, Multiplicity = n,
            Before = kind == NetChangeKind.MultisetRemoved ? image : null, After = kind == NetChangeKind.MultisetAdded ? image : null,
        };

    /// <summary>기본 미리보기: dbo.T id=1의 v를 1 → 2로 바꾸는 문장 하나.</summary>
    public static PreviewRecord Record(
        IReadOnlyList<NetChange>? net = null, IReadOnlyList<StatementResult>? statements = null, PreviewStatus status = PreviewStatus.Ok,
        string sql = Sql, string? sqlHash = null, DateTimeOffset? createdAt = null, bool truncated = false, IReadOnlyList<RowChange>? rowChanges = null,
        IReadOnlyList<Firing>? firings = null)
    {
        net ??= [Updated("dbo.T", "1", Row(("id", "1"), ("v", "1")), Row(("id", "1"), ("v", "2")), "v")];
        return new PreviewRecord
        {
            Sql = sql,
            NetChanges = net,
            RowChanges = rowChanges ?? [],
            Result = new PreviewResult
            {
                PreviewId = "pv_20261111_0001",
                Status = status,
                CreatedAt = createdAt ?? Start,
                SqlHash = sqlHash ?? ContractFormulas.SqlHash(sql),
                Baseline = new BaselineInfo { OriginDb = "Origin", SandboxDb = "Sandbox", SnapshotDb = "Sandbox_base", BackupFinishedAt = BackupAt, Fingerprint = Fingerprint() },
                Statements = statements ?? [Statement(1, 1)],
                Firings = firings ?? [],
                NetSummary = ContractFormulas.Summarize(net),
                Graph = new GraphDto(),
                Capture = new CaptureInfo { ImageCount = 2, Cap = 100_000, Truncated = truncated, ManifestVersion = "1.0" },
                TimingsMs = new TimingsMs(),
            },
        };
    }
}
