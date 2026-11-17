using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Execution.Tests.Support;

namespace DbRipple.Execution.Tests;

/// <summary>
/// 재조회 생략(2026-11-18, RereadSql.Plan) 동일성 검사 도구. "전체"는 바꾸기 전 RereadAsync의 키 고르기를 그대로 옮긴 기준 구현이다.
/// DB 상태는 (테이블, 키) → 현재 행 JSON 함수로 흉내 낸다. 어떤 DB 상태에서도 전체 재조회와 생략 재조회의 C11 출력이 같아야 한다.
/// </summary>
static class RereadEquivalence
{
    /// <summary>바꾸기 전 키 고르기: FULL 묶음 이미지 keyJson(키 있는 테이블), 테이블 이름 순, 테이블마다 서수 중복 제거.</summary>
    public static List<(string Table, string Key)> AllKeys(RawCapture raw, IReadOnlyList<TableInfo> tables)
    {
        var byId = tables.ToDictionary(t => t.ObjectId);
        var full = raw.Headers.Where(h => h.ImageMode == ImageMode.Full).ToDictionary(h => h.CaptureSeq, h => h.TableObjectId);
        return raw.Images.Where(i => i.KeyJson is not null && full.ContainsKey(i.CaptureSeq))
            .GroupBy(i => full[i.CaptureSeq])
            .Where(g => byId.ContainsKey(g.Key) && RereadSql.For(byId[g.Key]) is not null)
            .OrderBy(g => byId[g.Key].DisplayName, StringComparer.Ordinal)
            .SelectMany(g => g.Select(i => i.KeyJson!).Distinct(StringComparer.Ordinal).Select(k => (byId[g.Key].DisplayName, k)))
            .ToList();
    }

    public static List<(string Table, string Key)> PlanKeys(RereadPlan plan) =>
        plan.Tables.SelectMany(t => t.Keys.Select(k => (t.Table.DisplayName, k))).ToList();

    public static IReadOnlyList<TableInfo> Tables(BaselineContext ctx) =>
        ctx.Catalog.ToCaptureInput().Where(t => !AuditTriggerGenerator.IsCaptureStore(t)).ToList();

    public static List<RereadRow> Rows(IEnumerable<(string Table, string Key)> keys, Func<string, string, string?> db) =>
        keys.Select(k => new RereadRow { Table = k.Table, KeyJson = k.Key, RowJson = db(k.Table, k.Key) }).ToList();

    /// <summary>DB 상태 세 가지: 행 없음, 모든 키에 감사 after와 다른 행(감사 밖 재삽입까지 가정), 마지막 INSERTED 행(정직한 상태).</summary>
    public static IEnumerable<(string Name, Func<string, string, string?> Db)> DbStates(RawCapture raw, IReadOnlyList<TableInfo> tables)
    {
        var display = tables.ToDictionary(t => t.ObjectId, t => t.DisplayName);
        var tableOf = raw.Headers.ToDictionary(h => h.CaptureSeq, h => display.GetValueOrDefault(h.TableObjectId, h.TableName));
        var last = raw.Images.Where(i => i.ImageKind == ImageKind.Inserted && i.KeyJson is not null && i.RowJson is not null && tableOf.ContainsKey(i.CaptureSeq))
            .GroupBy(i => (tableOf[i.CaptureSeq], i.KeyJson!))
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.CaptureSeq).ThenBy(i => i.Ordinal).Last().RowJson);
        yield return ("행 없음", (_, _) => null);
        yield return ("다른 행", (_, k) => k.TrimEnd().TrimEnd('}') + ",\"dbr_other\":\"x\"}");
        yield return ("마지막 after", (t, k) => last.GetValueOrDefault((t, k)));
    }

    public static string Json(CollectResult r) => JsonSerializer.Serialize(r, FixtureFiles.Options);

    /// <summary>모든 DB 상태에서 전체 재조회와 생략 재조회의 Collect 결과(JSON)가 같은지 확인하고 계획을 돌려준다. 생략된 키는 계획 키를 뺀 나머지와 같아야 한다.</summary>
    public static RereadPlan AssertSame(RawCapture raw, AnalysisResult analysis, BaselineContext ctx, bool expectSomeDifference = false)
    {
        var tables = Tables(ctx);
        var all = AllKeys(raw, tables);
        var plan = RereadSql.Plan(raw.Headers, raw.Images, tables);
        var kept = PlanKeys(plan);
        var keptSet = kept.ToHashSet();
        Assert.Equal(all.Where(keptSet.Contains), kept); // 계획 키 = 전체 키의 부분 수열(순서 유지)
        Assert.Equal(all.Count, plan.KeyCount + plan.Skipped);
        var sawDifference = false;
        foreach (var (name, db) in DbStates(raw, tables))
        {
            var full = new ChangeCollector().Collect(raw with { Rereads = Rows(all, db) }, analysis, ctx);
            var skipped = new ChangeCollector().Collect(raw with { Rereads = Rows(kept, db), RereadSkippedKeys = plan.Skipped }, analysis, ctx);
            Assert.True(Json(full) == Json(skipped), $"DB 상태 '{name}'에서 Collect 결과가 다름");
            sawDifference |= full.RereadDifferences.Count > 0;
        }
        if (expectSomeDifference) Assert.True(sawDifference, "재조회가 결과를 바꾸는 경로를 한 번도 타지 않음(검사가 헛돎)");
        return plan;
    }
}

/// <summary>재조회 생략 판정(RereadSql.Plan)의 C11 출력 불변(DB 없음, 합성 입력).</summary>
public partial class CollectorTests
{
    const string T2 = "{\"id\":2,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}";

    static RereadPlan Same(RawCapture raw, BaselineContext? ctx = null, bool expectSomeDifference = true) =>
        RereadEquivalence.AssertSame(raw, new AnalysisResult(), ctx ?? Ctx(), expectSomeDifference);

    static string[] Kept(RereadPlan plan) => RereadEquivalence.PlanKeys(plan).Select(k => k.Table + " " + k.Key).ToArray();

    [Fact]
    public void Reread_skips_keys_seen_only_in_deleted_images()
    {
        var plan = Same(new RawBuilder().Firing(101, 1, ChangeOperation.Delete, 1, deleted: [T1, T2]).EndStatement(101).Build(), expectSomeDifference: false);
        Assert.Empty(Kept(plan));
        Assert.Equal(2, plan.Skipped);
    }

    [Fact]
    public void Reread_keeps_key_deleted_then_inserted_again()
    {
        // 다음 문장에서 다시 INSERT
        var plan = Same(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Delete, 1, deleted: [T1, T2]).EndStatement(101)
            .Firing(102, 1, ChangeOperation.Insert, 1, inserted: ["{\"id\":1,\"code\":\"a2\",\"v\":9,\"amt\":3,\"c\":90}"]).EndStatement(102).Build());
        Assert.Equal(["dbo.T {\"id\":1}"], Kept(plan));
        Assert.Equal(1, plan.Skipped);

        // 같은 문장 안 트리거(깊이 2)가 지운 키를 다시 INSERT(후위 기록: 안쪽 묶음이 먼저)
        plan = Same(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Insert, 2, inserted: ["{\"id\":1,\"code\":\"a2\",\"v\":9,\"amt\":3,\"c\":90}"])
            .Firing(102, 1, ChangeOperation.Delete, 1, deleted: [T1, T2]).EndStatement(102).Build());
        Assert.Equal(["dbo.T {\"id\":1}"], Kept(plan));
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Reread_skip_in_trigger_cascade_delete_keeps_only_inserted_log_rows()
    {
        // CL20 모양: A 삭제 → T 연쇄 삭제(깊이 1) → T 트리거가 I에 기록(깊이 2)
        var plan = Same(new RawBuilder()
            .Firing(101, 3, ChangeOperation.Insert, 2, inserted: ["{\"id\":7,\"at\":\"2026-11-10T10:00:00.123\",\"note\":\"1\"}", "{\"id\":8,\"at\":\"2026-11-10T10:00:00.123\",\"note\":\"2\"}"])
            .Firing(102, 1, ChangeOperation.Delete, 1, deleted: [T1, T2])
            .Firing(103, 4, ChangeOperation.Delete, 1, deleted: ["{\"id\":1}"])
            .EndStatement(103).Build());
        Assert.Equal(["dbo.I {\"id\":7}", "dbo.I {\"id\":8}"], Kept(plan));
        Assert.Equal(3, plan.Skipped);
    }

    [Fact]
    public void Reread_after_pk_change_reads_new_keys_and_skips_old_ones()
    {
        // 한 행 PK 변경(SINGLE_ROW)
        var plan = Same(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: ["{\"id\":300,\"code\":\"z\",\"v\":1,\"amt\":12.50,\"c\":10}"])
            .EndStatement(101).Build());
        Assert.Equal(["dbo.T {\"id\":300}"], Kept(plan));
        Assert.Equal(1, plan.Skipped);

        // 다중 행 PK 변경, 유일 키로 짝(BY_UNIQUE)
        plan = Same(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1, T2],
                inserted: ["{\"id\":101,\"code\":\"a\",\"v\":1,\"amt\":12.50,\"c\":10}", "{\"id\":102,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}"])
            .EndStatement(101).Build());
        Assert.Equal(["dbo.T {\"id\":101}", "dbo.T {\"id\":102}"], Kept(plan));
        Assert.Equal(2, plan.Skipped);

        // 짝 없음(UNPAIRED): 옛 키 before 행은 after가 없다
        plan = Same(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1, T2],
                inserted: ["{\"id\":101,\"code\":\"x\",\"v\":1,\"amt\":12.50,\"c\":10}", "{\"id\":102,\"code\":\"y\",\"v\":5,\"amt\":1.00,\"c\":50}"])
            .EndStatement(101).Build());
        Assert.Equal(2, plan.Skipped);

        // 키 맞바꾸기(1↔2): 두 키 모두 INSERTED에 있다
        plan = Same(new RawBuilder().Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1, T2],
                inserted: ["{\"id\":2,\"code\":\"a\",\"v\":1,\"amt\":12.50,\"c\":10}", "{\"id\":1,\"code\":\"b\",\"v\":5,\"amt\":1.00,\"c\":50}"])
            .EndStatement(101).Build());
        Assert.Equal(["dbo.T {\"id\":1}", "dbo.T {\"id\":2}"], Kept(plan));
        Assert.Equal(0, plan.Skipped);
    }

    [Fact]
    public void Reread_keeps_deleted_key_that_differs_only_by_case_or_trailing_space()
    {
        // 서수로는 다른 키지만 정렬 규칙상 같을 수 있다 → 넓게 재조회. "ABD"는 어디에도 INSERTED가 없어 생략.
        var plan = Same(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Delete, 1, deleted: ["{\"id\":\"ABC\",\"code\":\"a\",\"v\":1,\"amt\":1,\"c\":10}", "{\"id\":\"ABD\",\"code\":\"d\",\"v\":1,\"amt\":1,\"c\":10}"])
            .EndStatement(101)
            .Firing(102, 1, ChangeOperation.Insert, 1, inserted: ["{\"id\":\"abc  \",\"code\":\"a\",\"v\":2,\"amt\":1,\"c\":20}"]).EndStatement(102).Build());
        Assert.Equal(["dbo.T {\"id\":\"ABC\"}", "dbo.T {\"id\":\"abc  \"}"], Kept(plan));
        Assert.Equal(1, plan.Skipped);

        Assert.Equal(RereadSql.FoldKey("{\"id\":\"abc  \"}"), RereadSql.FoldKey("{\"id\":\"ABC\"}"));
        Assert.Equal(RereadSql.FoldKey("{\"id\":1.0}"), RereadSql.FoldKey("{\"id\":1}"));
        Assert.Equal(RereadSql.FoldKey("{\"b\":1,\"a\":\"x\"}"), RereadSql.FoldKey("{\"A\":\"X\",\"b\":1}"));
        Assert.NotEqual(RereadSql.FoldKey("{\"id\":\"ABD\"}"), RereadSql.FoldKey("{\"id\":\"ABC\"}"));
        Assert.NotEqual(RereadSql.FoldKey("{\"id\":null}"), RereadSql.FoldKey("{\"id\":\"\"}"));
        Assert.Equal("not json", RereadSql.FoldKey("not json")); // 읽지 못하면 원문(서수 일치는 그대로 남는다)
    }

    [Fact]
    public void Reread_skip_is_per_table()
    {
        // A의 {"id":1}은 UPDATE(INSERTED 있음), T의 {"id":1}은 DELETE만 → T 쪽만 생략
        var plan = Same(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Delete, 2, deleted: [T1])
            .Firing(102, 4, ChangeOperation.Update, 1, deleted: ["{\"id\":1}"], inserted: ["{\"id\":1}"])
            .EndStatement(102).Build());
        Assert.Equal(["dbo.A {\"id\":1}"], Kept(plan));
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Reread_skip_with_keys_mode_counts_keys_mode_inserts_and_never_reads_keys_headers()
    {
        // 문장 1 FULL DELETE, 문장 2 KEYS INSERT(같은 키 1), 문장 3 KEYS UPDATE(키 3). KEYS 묶음 키는 전처럼 읽지 않는다.
        var plan = Same(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Delete, 1, deleted: [T1, T2]).EndStatement(101)
            .Firing(102, 1, ChangeOperation.Insert, 1, inserted: [T1], keys: true).EndStatement(102)
            .Firing(103, 1, ChangeOperation.Update, 1, deleted: ["{\"id\":3}"], inserted: ["{\"id\":3}"], keys: true).EndStatement(103).Build(),
            expectSomeDifference: false);
        Assert.Equal(["dbo.T {\"id\":1}"], Kept(plan)); // KEYS INSERT가 있어 넓게 남김
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Reread_skip_keeps_order_undetermined_rows_with_a_delete_sibling()
    {
        // Q14 모양: A UPDATE(103) 아래 형제 101(T 1 UPDATE)·102(T 1 DELETE). 같은 키에 after가 있으므로 남긴다.
        var plan = Same(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Update, 2, deleted: ["{\"id\":1,\"code\":\"a\",\"v\":0,\"amt\":1,\"c\":0}"], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":1,\"amt\":1,\"c\":10}"])
            .Firing(102, 1, ChangeOperation.Delete, 2, deleted: ["{\"id\":1,\"code\":\"a\",\"v\":1,\"amt\":1,\"c\":10}", T2])
            .Firing(103, 4, ChangeOperation.Update, 1, deleted: ["{\"id\":1}"], inserted: ["{\"id\":1}"])
            .EndStatement(103).Build(), Ctx(updateOrder: CaptureManifest.None));
        Assert.Equal(["dbo.A {\"id\":1}", "dbo.T {\"id\":1}"], Kept(plan));
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Reread_skips_nothing_on_keyless_tables_because_they_are_never_read()
    {
        var plan = Same(new RawBuilder().Firing(101, 2, ChangeOperation.Delete, 1, deleted: ["{\"a\":1,\"b\":1}"]).EndStatement(101).Build(), expectSomeDifference: false);
        Assert.Empty(plan.Tables);
        Assert.Equal(0, plan.Skipped);
    }
}

/// <summary>
/// 실제 DB로 만든 CascadeLab 픽스처(바꾸기 전 실행기 출력)로 본 재조회 생략 동일성과 키 수 감소.
/// 픽스처의 rereads가 기준 구현(전체)과 같다는 것도 확인해, 기준 구현이 바꾸기 전 실행기를 그대로 옮겼음을 보인다.
/// </summary>
public class RereadPlanFixtureTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(FixtureFiles.Root).Order(StringComparer.Ordinal))
            if (File.Exists(Path.Combine(dir, "raw.json"))) data.Add(Path.GetFileName(dir));
        return data;
    }

    static (RawCapture Raw, AnalysisResult Analysis, BaselineContext Ctx) Load(string id)
    {
        var dir = Path.Combine(FixtureFiles.Root, id);
        return (FixtureFiles.Read<RawCapture>(Path.Combine(dir, "raw.json")), FixtureFiles.Read<AnalysisResult>(Path.Combine(dir, "analysis.json")),
            FixtureFiles.Read<BaselineContext>(FixtureFiles.BaselineFile(LabCase.Get(id).Database)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture_collect_is_same_when_deleted_only_keys_are_not_reread(string id)
    {
        var (raw, analysis, ctx) = Load(id);
        var tables = RereadEquivalence.Tables(ctx);
        Assert.Equal(RereadEquivalence.AllKeys(raw, tables), raw.Rereads.Select(r => (r.Table, r.KeyJson)).ToList());
        RereadEquivalence.AssertSame(raw, analysis, ctx);

        // 픽스처에 기록된 실제 재조회 값으로도 같다(생략한 키는 행에서 뺀다)
        var plan = RereadSql.Plan(raw.Headers, raw.Images, tables);
        var kept = RereadEquivalence.PlanKeys(plan).ToHashSet();
        var skipped = raw with { Rereads = raw.Rereads.Where(r => kept.Contains((r.Table, r.KeyJson))).ToList(), RereadSkippedKeys = plan.Skipped };
        Assert.Equal(File.ReadAllText(Path.Combine(FixtureFiles.Root, id, "collect.json")).TrimEnd(), RereadEquivalence.Json(new ChangeCollector().Collect(skipped, analysis, ctx)));
    }

    [Fact]
    public void Cl20_fixture_rereads_drop_from_5n_to_2n()
    {
        var (raw, _, ctx) = Load("CL20");
        var plan = RereadSql.Plan(raw.Headers, raw.Images, RereadEquivalence.Tables(ctx));
        Assert.Equal(10, raw.Rereads.Count); // N = 2: Parent 2 + Child 4 + ChildLog 4
        Assert.Equal((4L, 6L), (plan.KeyCount, plan.Skipped));
        Assert.All(plan.Tables, t => Assert.Equal("cl20.ChildLog", t.Table.DisplayName));
    }

    [Fact]
    public void Cl20_shape_at_n_1000_rereads_2n_keys_and_collect_is_same()
    {
        // CL20 픽스처의 헤더 3개(ChildLog INSERT 깊이 2, Child DELETE, Parent DELETE)를 N = 1000으로 키운다.
        const int n = 1000;
        var (raw0, analysis, ctx) = Load("CL20");
        var h = raw0.Headers.ToDictionary(x => x.TableName);
        AuditHeaderRow Header(string name, long count) => h[name] with
        {
            InsertedCount = h[name].Operation == ChangeOperation.Insert ? count : 0, DeletedCount = h[name].Operation == ChangeOperation.Delete ? count : 0,
        };
        var log = Header("[cl20].[ChildLog]", 2 * n);
        var child = Header("[cl20].[Child]", 2 * n);
        var parent = Header("[cl20].[Parent]", n);
        var images = new List<AuditRowImageRow>();
        for (var i = 1; i <= 2 * n; i++)
            images.Add(new() { CaptureSeq = log.CaptureSeq, ImageKind = ImageKind.Inserted, Ordinal = i, KeyJson = $"{{\"child_id\":{i}}}", RowJson = $"{{\"child_id\":{i},\"parent_id\":{(i + 1) / 2},\"qty\":1}}" });
        for (var i = 1; i <= 2 * n; i++)
            images.Add(new() { CaptureSeq = child.CaptureSeq, ImageKind = ImageKind.Deleted, Ordinal = i, KeyJson = $"{{\"id\":{i}}}", RowJson = $"{{\"id\":{i},\"parent_id\":{(i + 1) / 2},\"qty\":1}}" });
        for (var i = 1; i <= n; i++)
            images.Add(new() { CaptureSeq = parent.CaptureSeq, ImageKind = ImageKind.Deleted, Ordinal = i, KeyJson = $"{{\"id\":{i}}}", RowJson = $"{{\"id\":{i},\"total\":2}}" });
        var raw = raw0 with { Headers = [log, child, parent], Images = images, Rereads = [], NetChanges = [] };

        var tables = RereadEquivalence.Tables(ctx);
        Assert.Equal(5 * n, RereadEquivalence.AllKeys(raw, tables).Count);
        var plan = RereadEquivalence.AssertSame(raw, analysis, ctx, expectSomeDifference: true);
        Assert.Equal((2L * n, 3L * n), (plan.KeyCount, plan.Skipped));
    }

    [Fact]
    public void Skipped_count_is_left_out_of_json_when_zero_and_round_trips_otherwise()
    {
        var (raw, _, _) = Load("CL20");
        Assert.DoesNotContain("rereadSkippedKeys", JsonSerializer.Serialize(raw, FixtureFiles.Options), StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(raw with { RereadSkippedKeys = 6 }, FixtureFiles.Options);
        Assert.Contains("\"rereadSkippedKeys\": 6", json, StringComparison.Ordinal);
        Assert.Equal(6, JsonSerializer.Deserialize<RawCapture>(json, FixtureFiles.Options)!.RereadSkippedKeys);
    }
}
