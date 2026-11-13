using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Preview;

namespace DbRipple.Api.Tests;

/// <summary>C14: 활성 1건, 30분 만료(TimeProvider), JSON 파일 보관, 페이지·firingId 필터, 내보내기, 삭제.</summary>
public sealed class PreviewStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dbrt_api_store_" + Guid.NewGuid().ToString("N")[..8]);
    readonly FakeClock clock = new(TestData.Start);

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    PreviewStore NewStore() => new(dir, clock);

    PreviewRecord Rec(string id, int rows = 5, int nets = 5) => TestData.Record(id, clock.GetUtcNow(), rows, nets);

    [Fact]
    public async Task Save_then_get_returns_same_record_and_keeps_only_one_active()
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a"), default);
        Assert.Equal("pv_a", (await store.GetAsync("pv_a", default))!.Result.PreviewId);

        await store.SaveAsync(Rec("pv_b"), default);
        Assert.Null(await store.GetAsync("pv_a", default));
        Assert.NotNull(await store.GetAsync("pv_b", default));
        Assert.Equal("pv_b.preview.json", Path.GetFileName(Assert.Single(Directory.GetFiles(dir))));
    }

    [Fact]
    public async Task Expires_after_30_minutes_and_removes_the_file()
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a"), default);
        clock.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        Assert.NotNull(await store.GetAsync("pv_a", default));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await store.GetAsync("pv_a", default));
        Assert.Empty(Directory.GetFiles(dir));
        Assert.False(await store.ExportAsync("pv_a", new MemoryStream(), default));
        Assert.False(await store.RemoveAsync("pv_a", default));
        Assert.Null(await store.PageEventsAsync("pv_a", new RowPageQuery(), default));
    }

    [Fact]
    public async Task A_new_store_instance_reads_the_file_and_applies_expiry()
    {
        await NewStore().SaveAsync(Rec("pv_a"), default);
        var restarted = NewStore();
        var loaded = await restarted.GetAsync("pv_a", default);
        Assert.NotNull(loaded);
        Assert.Equal(5, loaded.RowChanges.Count);
        Assert.IsType<FiringNode>(loaded.Result.Graph.Nodes[1]); // 다형 그래프 노드 왕복
        Assert.Equal(PreviewStatus.Ok, loaded.Result.Status);

        clock.Advance(IPreviewStore.DefaultExpiry);
        Assert.Null(await NewStore().GetAsync("pv_a", default));
    }

    [Fact]
    public async Task Event_page_filters_by_firing_and_pages_with_offset_and_limit()
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a", rows: 5), default); // f-1001: 1,3,5 / f-1002: 2,4

        var all = await store.PageEventsAsync("pv_a", new RowPageQuery { Limit = 2 }, default);
        Assert.Equal(5, all!.Total);
        Assert.Equal(RowView.Event, all.View);
        Assert.Equal(2, all.Rows.Count);

        var second = await store.PageEventsAsync("pv_a", new RowPageQuery { Offset = 4, Limit = 2 }, default);
        Assert.Equal(5, second!.Total);
        Assert.Single(second.Rows);

        var f2 = await store.PageEventsAsync("pv_a", new RowPageQuery { FiringId = "f-1002" }, default);
        Assert.Equal(2, f2!.Total);
        Assert.All(f2.Rows, r => Assert.Equal("f-1002", r.FiringId));

        Assert.Equal(0, (await store.PageEventsAsync("pv_a", new RowPageQuery { FiringId = "f-9999" }, default))!.Total);
    }

    [Fact]
    public async Task Net_page_filters_by_explainedBy_firing_ids()
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a", nets: 5), default);
        var page = await store.PageNetAsync("pv_a", new RowPageQuery { View = RowView.Net, FiringId = "f-1001" }, default);
        Assert.Equal(3, page!.Total);
        Assert.Equal(RowView.Net, page.View);
        Assert.All(page.Rows, r => Assert.Contains("f-1001", r.ExplainedBy!.FiringIds));
    }

    static PreviewRecord TwoTables(PreviewRecord r) => r with
    {
        // dbo.T 1~3(f-1001,f-1002,f-1001), dbo.U 4~5(f-1002,f-1001)
        NetChanges = [.. r.NetChanges.Select((n, i) => i < 3 ? n : n with { Table = "dbo.U" })],
    };

    [Fact]
    public async Task Net_page_filters_by_table_before_offset_and_limit()
    {
        var store = NewStore();
        await store.SaveAsync(TwoTables(Rec("pv_a", nets: 5)), default);

        var t = await store.PageNetAsync("pv_a", new RowPageQuery { View = RowView.Net, Offset = 1, Limit = 1 }, "dbo.T", default);
        Assert.Equal(3, t!.Total);                                 // total은 거른 수
        Assert.Equal("2", Assert.Single(t.Rows).Key.Values[0]);     // 거른 뒤 offset 1

        var u = await store.PageNetAsync("pv_a", new RowPageQuery { View = RowView.Net, FiringId = "f-1001" }, "dbo.U", default);
        Assert.Equal(1, u!.Total);                                  // table과 firingId를 함께 거름
        Assert.All(u.Rows, r => Assert.Equal("dbo.U", r.Table));

        var none = await store.PageNetAsync("pv_a", new RowPageQuery { View = RowView.Net }, "dbo.Missing", default);
        Assert.Equal(0, none!.Total);
        Assert.Empty(none.Rows);

        Assert.Equal(5, (await store.PageNetAsync("pv_a", new RowPageQuery { View = RowView.Net }, null, default))!.Total);
        Assert.Null(await store.PageNetAsync("pv_none", new RowPageQuery { View = RowView.Net }, "dbo.T", default));
    }

    [Theory]
    [InlineData("dbo.T", true)]
    [InlineData("Sales.Sales Order.Header", true)]   // 테이블 이름의 '.'는 받는다(스키마는 첫 '.' 앞)
    [InlineData("T", false)]
    [InlineData(".T", false)]
    [InlineData("dbo.", false)]
    [InlineData("dbo.T\n", false)]
    [InlineData("", false)]
    public void Table_filter_format_is_schema_dot_table(string table, bool ok) => Assert.Equal(ok, RowPages.IsValidTable(table));

    [Fact]
    public async Task Table_filter_rejects_bad_format_and_too_long()
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a"), default);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PageNetAsync("pv_a", new RowPageQuery { View = RowView.Net }, "T", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PageNetAsync("pv_a", new RowPageQuery { View = RowView.Net }, "dbo." + new string('x', 129), default));
        Assert.False(RowPages.IsValidTable(new string('s', 128) + "." + new string('t', 129)));
        Assert.True(RowPages.IsValidTable(new string('s', 128) + "." + new string('t', 128)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PageNetAsync("pv_a", new RowPageQuery { Limit = 1001 }, "dbo.T", default));
    }

    [Fact]
    public async Task Page_accepts_limit_up_to_1000_and_rejects_out_of_range()
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a"), default);
        Assert.NotNull(await store.PageEventsAsync("pv_a", new RowPageQuery { Limit = 1000 }, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PageEventsAsync("pv_a", new RowPageQuery { Limit = 1001 }, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PageNetAsync("pv_a", new RowPageQuery { Limit = 0 }, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PageEventsAsync("pv_a", new RowPageQuery { Offset = -1 }, default));
    }

    [Fact]
    public async Task Export_writes_the_preview_record_json_including_sql()
    {
        var store = NewStore();
        var record = Rec("pv_a");
        await store.SaveAsync(record, default);
        using var ms = new MemoryStream();
        Assert.True(await store.ExportAsync("pv_a", ms, default));
        var back = JsonSerializer.Deserialize<PreviewRecord>(ms.ToArray(), ContractJson.Options)!;
        Assert.Equal(record.Sql, back.Sql);
        Assert.Equal(record.Result.SqlHash, back.Result.SqlHash);
        Assert.Equal(record.RowChanges.Count, back.RowChanges.Count);
        using var doc = JsonDocument.Parse(ms.ToArray());
        Assert.Equal("pv_a", doc.RootElement.GetProperty("result").GetProperty("previewId").GetString()); // camelCase
        Assert.False(await store.ExportAsync("pv_none", new MemoryStream(), default));
    }

    [Fact]
    public async Task Remove_deletes_memory_and_file()
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a"), default);
        Assert.True(await store.RemoveAsync("pv_a", default));
        Assert.Null(await store.GetAsync("pv_a", default));
        Assert.Empty(Directory.GetFiles(dir));
        Assert.False(await store.RemoveAsync("pv_a", default));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("pv a")]
    public async Task Ids_that_could_escape_the_directory_are_treated_as_missing(string id)
    {
        var store = NewStore();
        await store.SaveAsync(Rec("pv_a"), default);
        Assert.Null(await store.GetAsync(id, default));
        Assert.False(await store.RemoveAsync(id, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Rec(id), default));
    }

    [Fact]
    public async Task Save_leaves_unrelated_files_in_a_shared_directory_alone_and_a_corrupt_file_reads_as_missing()
    {
        Directory.CreateDirectory(dir);
        var other = Path.Combine(dir, "notes.json");
        await File.WriteAllTextAsync(other, "{}");
        await File.WriteAllTextAsync(Path.Combine(dir, "pv_broken.preview.json"), "{ not json");
        var store = NewStore();
        Assert.Null(await store.GetAsync("pv_broken", default));
        await store.SaveAsync(Rec("pv_a"), default);
        Assert.True(File.Exists(other));
    }
}
