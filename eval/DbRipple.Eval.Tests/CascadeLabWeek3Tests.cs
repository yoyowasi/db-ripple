using DbRipple.Eval;
using DbRipple.TestSupport;

namespace DbRipple.Eval.Tests;

/// <summary>
/// 3주차 사례(CL09~CL20, answer-format v0.2) 하네스 쪽 검사. 앞의 둘은 파일만 읽고(DB 없음), [SqlFact]는 전용 dbrt_eval_ DB에서 오라클을 돌린다.
/// </summary>
public class CascadeLabWeek3Tests
{
    static IReadOnlyList<HandCase> Cases() => HandCase.LoadAll(Path.Combine(RepoPaths.CascadeLab, "cases"));

    [Fact]
    public void V02_cases_load_with_identity_multiset_residual_and_scaling()
    {
        var cases = Cases().ToDictionary(c => c.Id);
        Assert.All(cases.Values.SelectMany(c => c.Rows).SelectMany(r => r.Causes), c => Assert.Contains(c, HandCase.Causes));
        Assert.Equal(cases.Count, Directory.GetFiles(Path.Combine(RepoPaths.CascadeLab, "cases"), "CL*.json").Length);

        // IDENTITY 행(v0.2 match): CL16 Ord·OrdLine, CL12c, CL12a residualCommitted
        Assert.Contains(cases["CL16"].Rows, r => r.Match is not null && r.Identity.Count > 0 && r.Ref is not null);
        Assert.Contains(cases["CL16"].Rows, r => r.IdentityRefs.Count > 0);
        Assert.Contains(cases["CL16"].Rows, r => r.Volatile.Count > 0);
        Assert.Contains(cases["CL12c"].Rows, r => r.Match is null && r.IdentityRefs.Count > 0); // key + identityRefs(FK 아닌 열)
        Assert.Contains(cases["CL12a"].Rows, r => r.ResidualCommitted && r.IsUnbound);
        Assert.True(cases["CL12a"].LeavesResidue);
        // 키 없는 테이블(v0.2 ROW)
        Assert.Contains(cases["CL14"].Rows, r => r.Kind == ChangeKinds.MultisetRemoved && r.Multiplicity == 2);
        Assert.Contains(cases["CL14"].Rows, r => r.Kind == ChangeKinds.MultisetAdded);
        // INSTEAD_OF 원인
        Assert.Contains(cases["CL11"].Rows.Concat(cases["CL11b"].Rows), r => r.Causes.Contains("INSTEAD_OF"));
        // scaling(M5)
        foreach (var id in new[] { "CL20", "CL20b" })
        {
            var s = cases[id].Scaling;
            Assert.NotNull(s);
            Assert.Contains("{N}", s!.Statement);
            Assert.Contains("cl20.usp_Generate", s.Generate);
        }
        // 다중 문장 실패
        Assert.Equal(("failed", 547, 3), (cases["CL18"].ExpectedStatus, cases["CL18"].ExpectedErrorNumber!.Value, cases["CL18"].Statements.Count));
        // 간선 키는 사례 안에서 겹치지 않는다
        Assert.All(cases.Values, c => Assert.Equal(c.Edges.Count, c.Edges.Select(e => e.Key).Distinct().Count()));
    }

    [Fact]
    public void Identity_cases_bind_against_their_own_unbound_shape_without_exceptions()
    {
        // 묶을 대상이 없으면 모든 IDENTITY 행이 "짝 없음"으로 남고 예외는 없다(빈 오라클).
        foreach (var c in Cases().Where(c => c.Rows.Any(r => r.Match is not null)))
        {
            var (bound, issues) = c.Bind([]);
            Assert.Equal(c.Rows.Count(r => r.Match is not null), issues.Count(i => i.StartsWith("IDENTITY 짝 없음", StringComparison.Ordinal)));
            Assert.All(bound.Rows.Where(r => r.Match is not null), r => Assert.True(r.IsUnbound));
        }
    }

    /// <summary>
    /// CL09~CL20 오라클 대 손 정답(정정 기록 후보 찾기). 사례마다 새 전용 원본을 만들어 서로의 남은 행(leavesResidue)과 IDENTITY 소비가 섞이지 않게 한다.
    /// CL20 계열은 시드 기본(@parents = 3) 그대로다. 실패 사례는 오라클 after가 롤백 뒤 상태여야 한다(인계 4).
    /// </summary>
    [SqlFact]
    public async Task Oracle_matches_hand_answers_for_week3_cases()
    {
        var log = OracleLog.LoadCases(Path.Combine(RepoPaths.CascadeLab, "verification", "logs", "oracle-2026-11-10.json"));
        var cases = Cases().Where(c => !log.ContainsKey(c.Id)).ToList();
        Assert.NotEmpty(cases);
        var runner = new OracleRunner(new HashSnapshotComparer());
        var cs = TestDb.ConnectionString();
        var problems = new List<string>();
        foreach (var hc in cases)
        {
            var db = TestDb.NewName("eval", "w3");
            await using var cleanup = new TestDatabases(db);
            await CascadeLabSeed.CreateAsync(db, hc.Database == "CascadeLab_RT" ? CascadeLabVariant.RecursiveTriggers : CascadeLabVariant.Default);
            var per = await runner.RunAsync(cs, db, hc.Statements, rawBatch: false, default);
            var folding = KeyFolding.For(per.Tables);
            problems.AddRange(folding.Apply(hc).DiffAgainst(folding.Apply(per.Changes)).Select(x => $"{hc.Id} 손 정답: {x}"));
            problems.AddRange(per.ComparatorIssues.Select(x => $"{hc.Id} 비교기: {x}"));
            if (per.Status != hc.ExpectedStatus) problems.Add($"{hc.Id}: 상태 {per.Status}, 기대 {hc.ExpectedStatus}");
            if (hc.ExpectedErrorNumber is int e && !per.ErrorNumbers.Contains(e)) problems.Add($"{hc.Id}: 오류 {e} 없음");
            if (per.RestoredAfterRollback == hc.LeavesResidue) problems.Add($"{hc.Id}: 롤백 뒤 복원 {per.RestoredAfterRollback}, leavesResidue {hc.LeavesResidue}");
            if (!hc.LeavesResidue && hc.Statements.All(s => !s.Contains('@')))
            {
                var raw = await runner.RunAsync(cs, db, hc.Statements, rawBatch: true, default);
                problems.AddRange(CaseScorer.RawBatchDiff(per, raw).Select(x => $"{hc.Id} 원문 배치: {x}"));
            }
        }
        Assert.Empty(problems);
    }

    /// <summary>비교기 자체 검증(§10.3) 추가: geography·geometry·sql_variant 열(CL19, SP6 13604 회피 확인)과 정렬 규칙 키(인계 6).</summary>
    [SqlFact]
    public async Task Comparer_handles_spatial_variant_columns_and_case_only_key_update()
    {
        var db = TestDb.NewName("eval", "cmp3");
        await using var cleanup = new TestDatabases(db);
        await TestDb.ExecAsync("master", $"CREATE DATABASE [{db}] COLLATE SQL_Latin1_General_CP1_CI_AS;");
        await TestDb.ExecAsync(db,
            "CREATE TABLE dbo.S (id int NOT NULL PRIMARY KEY, g geography NULL, m geometry NULL, v sql_variant NULL);",
            "INSERT dbo.S VALUES (1, geography::Point(37.5, 127.0, 4326), geometry::STGeomFromText('POINT (1 2 3)', 0), CAST(1.5 AS float));",
            "CREATE TABLE dbo.K (code varchar(10) NOT NULL PRIMARY KEY, v int NOT NULL);",
            "INSERT dbo.K VALUES ('abc', 1);");
        var runner = new OracleRunner(new HashSnapshotComparer());
        var spatial = await runner.RunAsync(TestDb.ConnectionString(), db,
            ["UPDATE dbo.S SET g = geography::Point(37.6, 127.0, 4326), v = CAST(N'x' AS nvarchar(10)) WHERE id = 1;"], rawBatch: false, default);
        Assert.Empty(spatial.ComparatorIssues);
        var s = Assert.Single(spatial.Changes);
        Assert.Equal(ChangeKinds.Updated, s.Change);
        Assert.Contains("g", s.ChangedColumns);
        Assert.Contains("v@type", s.ChangedColumns);
        Assert.Equal("4326", s.After!["g@srid"]);

        var keyCase = await runner.RunAsync(TestDb.ConnectionString(), db, ["UPDATE dbo.K SET code = 'ABC' WHERE code = 'abc';"], rawBatch: false, default);
        var k = Assert.Single(keyCase.Changes); // DELETED + INSERTED가 아니라 같은 키의 UPDATED(정렬 규칙대로)
        Assert.Equal(ChangeKinds.Updated, k.Change);
        Assert.Equal(["code"], k.ChangedColumns);
        Assert.Single(keyCase.Notes);
        Assert.True(keyCase.RestoredAfterRollback);
    }

    /// <summary>인계 4: RAISERROR만 하는 트리거는 XACT_ABORT ON에서도 트랜잭션을 살려 두지만, 오라클 after는 롤백 뒤 상태(변경 0)여야 한다.</summary>
    [SqlFact]
    public async Task Oracle_after_is_post_rollback_state_when_trigger_only_raises_error()
    {
        var db = TestDb.NewName("eval", "rse");
        await using var cleanup = new TestDatabases(db);
        await TestDb.ExecAsync("master", $"CREATE DATABASE [{db}];");
        await TestDb.ExecAsync(db,
            "CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY, v int NOT NULL);",
            "INSERT dbo.T VALUES (1, 0), (2, 0);",
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM inserted WHERE id = 2) RAISERROR('no', 16, 1); END;");
        var r = await new OracleRunner(new HashSnapshotComparer()).RunAsync(TestDb.ConnectionString(), db,
            ["UPDATE dbo.T SET v = 1 WHERE id = 1;", "UPDATE dbo.T SET v = 1 WHERE id = 2;", "UPDATE dbo.T SET v = 2 WHERE id = 1;"], rawBatch: false, default);
        Assert.Equal("failed", r.Status);
        Assert.Equal(["ok", "failed", "not_run"], r.Statements.Select(s => s.Status));
        Assert.Empty(r.Changes); // 첫 문장의 변경도 롤백됨
        Assert.True(r.RestoredAfterRollback);
    }
}
