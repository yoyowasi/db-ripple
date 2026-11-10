using System.Text.Json;
using DbRipple.Analysis;
using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Diff;
using DbRipple.Execution;
using DbRipple.Preview;
using DbRipple.TestSupport;

namespace DbRipple.Integration.Tests;

/// <summary>
/// 2주차 D3 통합: Host와 같은 조립(진짜 C6·C10·C11·C12·C13·C14·SandboxManager, 실행 사용자 dbr_preview_user, 평가 모드 아님)으로
/// 자기 전용 CascadeLab 기준선(dbrt_int_)에서 CL01을 끝까지 돌린 뒤 CL01~CL08b 전부로 넓힌다.
/// 사례마다: 상태·문장 상태·오류 번호, 묶음(테이블·연산·행 수·깊이), 순 변경 키가 손 정답과 같고,
/// 행 변경이 가리키는 묶음이 결과에 모두 있고, FULL 이미지가 전체 행이며, 같은 행의 C11 최종 after·처음 before와 C12 순 변경 after·before가 §6.1 문자열로 같다(위험 8-2).
/// 끝나면 샌드박스 = 스냅샷이고 오염 표시가 없다.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class PipelineIntegrationTests(LabFixture labs)
{
    public static TheoryData<string> CaseIds { get; } = new(HandCase.All.Select(c => c.Id));

    [SqlFact]
    public async Task Cl01_runs_end_to_end_through_real_components()
    {
        var hc = HandCase.Get("CL01");
        var lab = labs.For(hc);
        var ctx = await lab.Manager.GetContextAsync(default);
        // 조립 확인: BaselineContext.Triggers는 C6 TriggerAnalyzer 결과(비면 C13 확신도가 일괄로 낮아진다)
        Assert.Contains(ctx.Triggers, t => t.IsAuditTrigger);
        Assert.Contains(ctx.Triggers, t => !t.IsAuditTrigger && t.WriteTargets.Count > 0);
        Assert.True(ctx.FingerprintMatch);

        var outcome = await lab.Pipeline.RunAsync(string.Join("\r\n", hc.Statements), new PreviewOptions(), null, default);
        var rec = Assert.IsType<PreviewRecord>(outcome.Record);
        Assert.Equal(PreviewStatus.Ok, rec.Result.Status);
        Assert.Equal(["DELETE FROM cl01.A WHERE id = 1"], rec.Result.Graph.Nodes.OfType<StatementNode>().Select(n => n.Label)); // §6.3 문장 앞부분(ResolveInput.StatementSql)
        Assert.Equal(4, rec.Result.Firings.Count);
        Assert.All(rec.Result.Firings, f => Assert.Equal(Confidence.Certain, f.Cause!.Confidence)); // FK 연쇄 + DIRECT 모두 certain(CL01 손 정답)
        Assert.Equal(9, rec.RowChanges.Count);
        Assert.Equal(9, rec.NetChanges.Count);
        Assert.Equal(9, rec.Result.NetSummary.Sum(s => s.Deleted));
        Assert.Equal(PreviewPipeline.SqlHash(rec.Sql), rec.Result.SqlHash);

        // C14: 저장·페이지
        var stored = await lab.Store.GetAsync(rec.Result.PreviewId, default);
        Assert.NotNull(stored);
        var page = await lab.Store.PageEventsAsync(rec.Result.PreviewId, new RowPageQuery { Offset = 0, Limit = 100 }, default);
        Assert.Equal(9, page!.Total);

        var status = await lab.Manager.GetStatusAsync(default);
        Assert.Equal((SandboxState.Ready, false), (status.State, status.Locked));
        Assert.Equal(0, await lab.SandboxDiffCountAsync());
    }

    [SqlTheory]
    [MemberData(nameof(CaseIds))]
    public async Task Case_matches_hand_answer_and_c11_final_after_equals_c12_net(string id)
    {
        var hc = HandCase.Get(id);
        var lab = labs.For(hc);
        var ctx = await lab.Manager.GetContextAsync(default);
        var outcome = await lab.Pipeline.RunAsync(string.Join("\r\n", hc.Statements), new PreviewOptions(), null, default);
        Assert.True(outcome.Analysis.IsAccepted, string.Join("; ", outcome.Analysis.Rejects.Select(r => r.RuleId)));
        var rec = Assert.IsType<PreviewRecord>(outcome.Record);
        var result = rec.Result;

        // 상태·문장 상태·오류 번호
        Assert.Equal(hc.Status, Text(result.Status));
        Assert.Equal(hc.StatementStatus, result.Statements.Select(s => (s.Index, Text(s.Status))));
        Assert.Equal(hc.ErrorNumber, result.Statements.Select(s => s.Error?.ErrorNumber).FirstOrDefault(n => n is not null));

        // 묶음(테이블·연산·행 수·깊이)
        Assert.Equal(hc.Firings.Order(), result.Firings.Select(f => (f.Table, f.Operation.ToString().ToUpperInvariant(), f.RowCount, f.Depth)).Order());

        // 순 변경 키(테이블, 종류, 키 값)
        Assert.Equal(hc.ChangeKeys.Order(StringComparer.Ordinal),
            rec.NetChanges.Select(n => $"{n.Table}|{Text(n.Change)}|{string.Join(",", n.Key.Values)}").Order(StringComparer.Ordinal));
        Assert.Empty(result.Residuals);

        // 행 변경이 가리키는 묶음이 결과에 모두 있다
        var firings = result.Firings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        Assert.All(rec.RowChanges, rc => Assert.True(firings.ContainsKey(rc.FiringId), rc.Id));

        // FULL 묶음의 이미지는 전체 행(관찰 가능한 열의 rowJson 속성 전부)
        var props = ctx.Catalog.ToCaptureInput().ToDictionary(t => t.DisplayName, t => t.Columns
            .Where(c => !AuditValueExpressions.IsUnobservable(c)).SelectMany(c => AuditValueExpressions.Items(c, "x").Select(i => i.Property))
            .Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        foreach (var rc in rec.RowChanges.Where(r => firings[r.FiringId].ImageMode == ImageMode.Full))
            foreach (var image in new[] { rc.Before, rc.After }.OfType<IReadOnlyDictionary<string, string?>>())
                Assert.Equal(props[firings[rc.FiringId].Table], image.Keys.Order(StringComparer.Ordinal));

        // 위험 8-2: 같은 행의 C11 최종 after(전위 순서 마지막 묶음) = C12 after, C11 처음 before = C12 before
        var order = Preorder(result.Firings);
        var events = rec.RowChanges.Select((rc, i) => (rc, i)).OrderBy(x => order[x.rc.FiringId]).ThenBy(x => x.i).Select(x => x.rc).ToList();
        foreach (var n in rec.NetChanges.Where(n => n.Key.Kind != KeyKind.ROW))
        {
            var same = events.Where(rc => firings[rc.FiringId].Table == n.Table).ToList();
            if (n.After is { } after)
            {
                var last = same.LastOrDefault(rc => HasKey(rc.After, n.Key));
                Assert.True(last is not null, $"{n.Table} {string.Join(",", n.Key.Values)}: after가 있는 행 변경 없음");
                Assert.Equal(Sorted(after), Sorted(last!.After!));
            }
            if (n.Before is { } before)
            {
                var first = same.FirstOrDefault(rc => HasKey(rc.Before, n.Key));
                Assert.True(first is not null, $"{n.Table} {string.Join(",", n.Key.Values)}: before가 있는 행 변경 없음");
                Assert.Equal(Sorted(before), Sorted(first!.Before!));
            }
        }

        var status = await lab.Manager.GetStatusAsync(default);
        Assert.Equal(SandboxState.Ready, status.State);
        Assert.Equal(0, await lab.SandboxDiffCountAsync());
    }

    static string Text<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options).Trim('"');

    static List<KeyValuePair<string, string?>> Sorted(IReadOnlyDictionary<string, string?> d) => [.. d.OrderBy(kv => kv.Key, StringComparer.Ordinal)];

    static bool HasKey(IReadOnlyDictionary<string, string?>? image, RowKey key) =>
        image is not null && key.Columns.Select((c, i) => (c, i)).All(x => image.TryGetValue(x.c, out var v) && string.Equals(v, key.Values[x.i], StringComparison.Ordinal));

    /// <summary>묶음 id → (문장, 전위 순번). 트리는 captureSeq 순(후위)과 depth로 세운다(§7.2 6단계, 하네스 Reconstruction과 같은 규칙의 테스트 쪽 구현).</summary>
    static Dictionary<string, (int, int)> Preorder(IReadOnlyList<Firing> firings)
    {
        var index = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var st in firings.GroupBy(f => f.StatementIndex))
        {
            var stack = new List<(Firing F, List<object> Kids)>();
            foreach (var f in st.OrderBy(f => f.CaptureSeq))
            {
                var i = stack.Count;
                while (i > 0 && stack[i - 1].F.Depth > f.Depth) i--;
                var kids = stack.Skip(i).Cast<object>().ToList();
                stack.RemoveRange(i, stack.Count - i);
                stack.Add((f, kids));
            }
            var n = 0;
            void Visit((Firing F, List<object> Kids) x)
            {
                index[x.F.Id] = (st.Key, n++);
                foreach (var k in x.Kids) Visit(((Firing, List<object>))k);
            }
            foreach (var root in stack) Visit(root);
        }
        return index;
    }

    /// <summary>
    /// 검토 재현(2026-11-10): 트리거의 COMMIT. 대상 테이블에 붙어 있으면 분석기가 거부하고(REJECT_TRIGGER_TRANSACTION_CONTROL),
    /// 다른 트리거를 거쳐 닿으면(분석기가 못 봄) 실행기가 트랜잭션 ID 변화로 잡아 failed + 오염으로 표시하고, 다음 미리보기 전에 기준선을 다시 만든다.
    /// </summary>
    [SqlFact]
    public async Task Trigger_commit_is_rejected_when_direct_and_marks_sandbox_dirty_when_reached_indirectly()
    {
        await using var lab = await Lab.CreateCustomAsync("commit",
            """
            CREATE TABLE dbo.Plain (id int NOT NULL CONSTRAINT PK_Plain PRIMARY KEY);
            CREATE TABLE dbo.Cb (id int NOT NULL CONSTRAINT PK_Cb PRIMARY KEY);
            CREATE TABLE dbo.Co (id int NOT NULL CONSTRAINT PK_Co PRIMARY KEY);
            CREATE TABLE dbo.Src (id int NOT NULL CONSTRAINT PK_Src PRIMARY KEY);
            """,
            "CREATE TRIGGER dbo.trg_Cb ON dbo.Cb AFTER INSERT AS BEGIN SET NOCOUNT ON; COMMIT TRAN; BEGIN TRAN; END;",
            "CREATE TRIGGER dbo.trg_Co ON dbo.Co AFTER INSERT AS BEGIN SET NOCOUNT ON; COMMIT TRAN; END;",
            "CREATE TRIGGER dbo.trg_Src ON dbo.Src AFTER INSERT AS BEGIN SET NOCOUNT ON; INSERT INTO dbo.Cb (id) SELECT id FROM inserted; END;");
        var ctx = await lab.Manager.GetContextAsync(default);
        Assert.Contains(ctx.Triggers, t => t.Trigger == "dbo.trg_Cb" && t.HasTransactionControl);
        Assert.Contains(ctx.Triggers, t => t.Trigger == "dbo.trg_Co" && t.HasTransactionControl);

        foreach (var sql in new[] { "INSERT INTO dbo.Cb (id) VALUES (1);", "INSERT INTO dbo.Co (id) VALUES (1);" })
        {
            var rejected = await lab.Pipeline.RunAsync(sql, new PreviewOptions(), null, default);
            Assert.Null(rejected.Record);
            Assert.Contains(rejected.Analysis.Rejects, r => r.RuleId == AnalysisRules.RejectTriggerTransactionControl);
        }
        Assert.Equal(0, await lab.SandboxDiffCountAsync());

        // 간접: Src 트리거 → Cb INSERT → trg_Cb의 COMMIT; BEGIN TRAN;
        var indirect = await lab.Pipeline.RunAsync("INSERT INTO dbo.Src (id) VALUES (1);", new PreviewOptions(), null, default);
        var rec = Assert.IsType<PreviewRecord>(indirect.Record);
        Assert.Equal(PreviewStatus.Failed, rec.Result.Status);
        Assert.Contains("트랜잭션 ID", rec.Result.Statements.Single().Error!.Message);
        Assert.Contains(rec.Result.ResidualCommitted, n => n.Table == "dbo.Cb");
        Assert.Contains(rec.Result.ResidualCommitted, n => n.Table == "dbo.Src");
        Assert.Equal(SandboxState.Dirty, (await lab.Manager.GetStatusAsync(default)).State);
        Assert.NotEqual(0, await lab.SandboxDiffCountAsync());

        // 다음 미리보기는 먼저 기준선을 다시 만든다(§5.2 2)
        var next = await lab.Pipeline.RunAsync("INSERT INTO dbo.Plain (id) VALUES (1);", new PreviewOptions(), null, default);
        Assert.Equal(PreviewStatus.Ok, next.Record!.Result.Status);
        Assert.Equal(SandboxState.Ready, (await lab.Manager.GetStatusAsync(default)).State);
        Assert.Equal(0, await lab.SandboxDiffCountAsync());
    }
}
