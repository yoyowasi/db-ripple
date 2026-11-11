using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Preview;

namespace DbRipple.Api.Tests;

/// <summary>/catalog/schema 공급자(AD8, Q23·Q37): DB 없이 가짜 카탈로그·매니페스트·형식 재료로 변환 규칙, 감사 저장소 제외, 캐시, 형식 이름만 두는 대체 경로.</summary>
public sealed class CatalogSchemaProviderTests
{
    static CatalogColumn Col(int obj, int id, string name, string type, bool nullable = false, bool identity = false, bool computed = false, bool rowversion = false, string? def = null) =>
        new(obj, id, name, type, false, nullable, computed, identity, rowversion, def);

    static CatalogIndexColumn Pk(int obj, string column, int ordinal = 1) => new(obj, 1, "PK_" + obj, true, true, ordinal, column, false);

    // sales.Order(1: PK OrderID IDENTITY, Amount decimal, Note nvarchar(50), Total 계산 열, Ver rowversion, Created 기본값 GETDATE, Doc text)
    // dbo.Log(2: 키 없음, 제외 테이블), audit.AuditHeader(3: 감사 저장소), dbo.Zed(4: 매니페스트에 없음)
    static BaselineContext Context(DateTimeOffset? at = null, string version = "m-1") => TestData.Baseline() with
    {
        BackupFinishedAt = at ?? TestData.Start,
        Catalog = new DbCatalog(
            [
                new CatalogTable(2, "dbo", "Log", TemporalType.None, false),
                new CatalogTable(1, "sales", "Order", TemporalType.None, false),
                new CatalogTable(3, "audit", "AuditHeader", TemporalType.None, false),
                new CatalogTable(4, "dbo", "Zed", TemporalType.None, false),
            ],
            [
                Col(1, 2, "Amount", "decimal", nullable: true), Col(1, 1, "OrderID", "int", identity: true), Col(1, 3, "Note", "nvarchar", nullable: true),
                Col(1, 4, "Total", "money", computed: true), Col(1, 5, "Ver", "timestamp", rowversion: true),
                Col(1, 6, "Created", "datetime2", def: "(getdate())"), Col(1, 7, "Doc", "text", nullable: true),
                Col(2, 1, "Msg", "varchar"),
                Col(3, 1, "captureSeq", "bigint"),
                Col(4, 1, "Id", "int"),
            ],
            [Pk(1, "OrderID")], [], [], []),
        Manifest = new CaptureManifest(version,
            [new ManifestTable("sales.Order", true, new AuditOrderInfo("LAST", "LAST", "LAST"), ["Doc"],
                [new VolatileColumn("OrderID", "IDENTITY"), new VolatileColumn("Total", "COMPUTED"), new VolatileColumn("Ver", "ROWVERSION"), new VolatileColumn("Created", "NONDETERMINISTIC_DEFAULT")])],
            [new ExcludedTable("dbo.Log", "MEMORY_OPTIMIZED")], []),
    };

    static readonly IReadOnlyDictionary<(int, int), ColumnTypeShape> Shapes = new Dictionary<(int, int), ColumnTypeShape>
    {
        [(1, 1)] = new(4, 10, 0),
        [(1, 2)] = new(9, 19, 4),
        [(1, 3)] = new(100, 0, 0),   // nvarchar 바이트 100 = 50자
        [(1, 4)] = new(8, 19, 4),
        [(1, 5)] = new(8, 0, 0),
        [(1, 6)] = new(8, 27, 7),
        [(1, 7)] = new(16, 0, 0),
        [(2, 1)] = new(-1, 0, 0),    // varchar(max)
        [(3, 1)] = new(8, 19, 0),
        [(4, 1)] = new(4, 10, 0),
    };

    sealed class FakeSandbox(Func<BaselineContext> context) : ISandboxManager
    {
        public int ContextCalls;
        public Task<SandboxStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BaselineContext> GetContextAsync(CancellationToken ct)
        {
            ContextCalls++;
            return Task.FromResult(context());
        }
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public void StartRefresh() { }
        public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct) => throw new NotSupportedException();
        public void MarkDirty(string reason) { }
    }

    sealed class FakeSource(Func<IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>> read) : IColumnTypeSource
    {
        public int Reads;
        public Task<IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>> ReadAsync(string sandboxDb, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(read());
        }
    }

    [Fact]
    public void Build_orders_tables_drops_audit_store_and_maps_manifest_fields()
    {
        var schema = CatalogSchemaProvider.Build(Context(), Shapes);

        Assert.Equal("Origin", schema.Database);
        Assert.Equal(TestData.Start, schema.BaselineAt);
        Assert.Equal("m-1", schema.ManifestVersion);
        Assert.Equal(["dbo.Log", "dbo.Zed", "sales.Order"], schema.Tables.Select(t => t.Table).ToArray()); // audit.* 없음, 스키마·이름 순

        var log = schema.Tables[0];
        Assert.False(log.Covered);
        Assert.Equal("MEMORY_OPTIMIZED", log.ExcludedReason);
        Assert.Equal(KeyKind.ROW, log.KeyKind);
        Assert.Empty(log.KeyColumns);

        var zed = schema.Tables[1]; // 매니페스트에 없는 테이블: 덮지 않았고 사유도 없다
        Assert.False(zed.Covered);
        Assert.Null(zed.ExcludedReason);

        var order = schema.Tables[2];
        Assert.Equal(("sales", "Order"), (order.Schema, order.Name));
        Assert.True(order.Covered);
        Assert.Null(order.ExcludedReason);
        Assert.Equal(KeyKind.PK, order.KeyKind);
        Assert.Equal("OrderID", Assert.Single(order.KeyColumns));
    }

    [Fact]
    public void Build_orders_columns_by_column_id_and_formats_sql_types()
    {
        var order = CatalogSchemaProvider.Build(Context(), Shapes).Tables.Single(t => t.Table == "sales.Order");
        Assert.Equal(["OrderID", "Amount", "Note", "Total", "Ver", "Created", "Doc"], order.Columns.Select(c => c.Name).ToArray());
        string Type(string name) => order.Columns.Single(c => c.Name == name).SqlType;
        Assert.Equal("int", Type("OrderID"));
        Assert.Equal("decimal(19,4)", Type("Amount"));
        Assert.Equal("nvarchar(50)", Type("Note"));
        Assert.Equal("money", Type("Total"));
        Assert.Equal("datetime2(7)", Type("Created"));
        Assert.Equal("text", Type("Doc"));
        Assert.Equal("varchar(max)", CatalogSchemaProvider.Build(Context(), Shapes).Tables.Single(t => t.Table == "dbo.Log").Columns[0].SqlType);
    }

    [Fact]
    public void Build_maps_flags_volatile_and_unobservable()
    {
        var order = CatalogSchemaProvider.Build(Context(), Shapes).Tables.Single(t => t.Table == "sales.Order");
        SchemaColumn C(string name) => order.Columns.Single(c => c.Name == name);

        Assert.True(C("OrderID").IsIdentity);
        Assert.Equal(VolatileReason.Identity, C("OrderID").Volatile);
        Assert.False(C("OrderID").IsNullable);
        Assert.True(C("Amount").IsNullable);
        Assert.Null(C("Amount").Volatile);

        Assert.True(C("Total").IsComputed);
        Assert.Null(C("Total").Volatile); // 매니페스트 "COMPUTED"는 volatile이 아니라 isComputed(Q7)

        Assert.True(C("Ver").IsRowVersion);
        Assert.Equal(VolatileReason.Rowversion, C("Ver").Volatile);
        Assert.Equal(VolatileReason.NondeterministicDefault, C("Created").Volatile);

        Assert.True(C("Doc").Unobservable);
        Assert.False(C("Amount").Unobservable);
    }

    [Fact]
    public void Build_without_shapes_keeps_only_the_type_name()
    {
        var order = CatalogSchemaProvider.Build(Context(), null).Tables.Single(t => t.Table == "sales.Order");
        Assert.Equal("decimal", order.Columns.Single(c => c.Name == "Amount").SqlType);
        Assert.Equal("nvarchar", order.Columns.Single(c => c.Name == "Note").SqlType);
    }

    [Fact]
    public async Task Provider_caches_per_baseline_and_rebuilds_when_the_baseline_changes()
    {
        var ctx = Context();
        var sandbox = new FakeSandbox(() => ctx);
        var source = new FakeSource(() => Shapes);
        var provider = new CatalogSchemaProvider(sandbox, source);

        var first = await provider.GetSchemaAsync(default);
        var second = await provider.GetSchemaAsync(default);
        Assert.Same(first, second);
        Assert.Equal(1, source.Reads);

        ctx = Context(TestData.Start.AddHours(1)); // 재구축 뒤 새 기준 시각
        var third = await provider.GetSchemaAsync(default);
        Assert.NotSame(first, third);
        Assert.Equal(TestData.Start.AddHours(1), third.BaselineAt);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task Provider_rebuilds_when_only_the_manifest_version_changes()
    {
        var ctx = Context();
        var source = new FakeSource(() => Shapes);
        var provider = new CatalogSchemaProvider(new FakeSandbox(() => ctx), source);
        await provider.GetSchemaAsync(default);
        ctx = Context(version: "m-2");
        Assert.Equal("m-2", (await provider.GetSchemaAsync(default)).ManifestVersion);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task Provider_falls_back_to_type_names_when_the_shape_query_fails_and_retries_next_time()
    {
        var fail = true;
        var source = new FakeSource(() => fail ? throw new IOException("연결 끊김 Password=SECRET") : Shapes);
        var provider = new CatalogSchemaProvider(new FakeSandbox(() => Context()), source);

        var degraded = await provider.GetSchemaAsync(default);
        Assert.Equal("decimal", degraded.Tables.Single(t => t.Table == "sales.Order").Columns.Single(c => c.Name == "Amount").SqlType);

        fail = false; // 실패 결과는 캐시하지 않는다
        var healthy = await provider.GetSchemaAsync(default);
        Assert.Equal("decimal(19,4)", healthy.Tables.Single(t => t.Table == "sales.Order").Columns.Single(c => c.Name == "Amount").SqlType);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task Provider_passes_through_the_conflict_when_there_is_no_baseline()
    {
        var provider = new CatalogSchemaProvider(
            new FakeSandbox(() => throw new PreviewConflictException(ConflictReason.NoBaseline, "기준선 없음")),
            new FakeSource(() => Shapes));
        var ex = await Assert.ThrowsAsync<PreviewConflictException>(() => provider.GetSchemaAsync(default));
        Assert.Equal(ConflictReason.NoBaseline, ex.Reason);
    }

    [Fact]
    public void Schema_serializes_with_contract_json_names()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(CatalogSchemaProvider.Build(Context(), Shapes), ContractJson.Options);
        Assert.Contains("\"keyKind\":\"PK\"", json);
        Assert.Contains("\"volatile\":\"IDENTITY\"", json);
        Assert.Contains("\"excludedReason\":\"MEMORY_OPTIMIZED\"", json);
        Assert.Contains("\"sqlType\":\"nvarchar(50)\"", json);
    }
}
