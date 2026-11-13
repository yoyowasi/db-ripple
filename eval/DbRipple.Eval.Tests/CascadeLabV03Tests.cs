using DbRipple.Contracts;
using DbRipple.Eval;
using DbRipple.TestSupport;

namespace DbRipple.Eval.Tests;

/// <summary>
/// 정답 형식 v0.3(EXEC·거부 사례, CL21~CL25) 하네스 쪽 검사. 파일만 읽고 DB를 쓰지 않는다.
/// P: 노드·PROC_DIRECT 간선과 원인·statements[].returnValue·expected.rejects를 문자열로 읽는지, 거부 사례를 오라클 없이 따로 채점하는지 본다.
/// </summary>
public class CascadeLabV03Tests
{
    static IReadOnlyDictionary<string, HandCase> Cases() =>
        HandCase.LoadAll(Path.Combine(RepoPaths.CascadeLab, "cases")).ToDictionary(c => c.Id);

    [Fact]
    public void Exec_cases_load_with_proc_nodes_edges_causes_and_return_values()
    {
        var cases = Cases();
        foreach (var id in new[] { "CL21", "CL22", "CL23", "CL24" })
            Assert.True(cases.ContainsKey(id), id);

        Assert.Contains("PROC_DIRECT", HandCase.Causes);
        Assert.All(cases.Values.SelectMany(c => c.Rows).SelectMany(r => r.Causes), c => Assert.Contains(c, HandCase.Causes));

        // CL21: 분기 없는 프로시저. 모든 쓰기가 PROC_DIRECT, P: 노드에서 묶음으로 가는 PROC_DIRECT 간선(name = EXEC에 쓴 프로시저), returnValue 0
        var cl21 = cases["CL21"];
        Assert.Equal(0, cl21.ExpectedReturnValues[1]);
        Assert.All(cl21.Rows, r => Assert.Equal(["PROC_DIRECT"], r.Causes));
        var procEdges = cl21.Edges.Where(e => e.Kind == "PROC_DIRECT").ToList();
        Assert.Equal(4, procEdges.Count);
        Assert.All(procEdges, e =>
        {
            Assert.Equal("P:cl21.usp_Outer", e.Parent);
            Assert.StartsWith("F:", e.Child);
            Assert.Equal("cl21.usp_Outer", e.Name);
            Assert.Equal("certain", e.Confidence);
        });
        // 2026-11-13 통합: EXEC 문장은 S 노드 대신 procedureCall(P:)이 뿌리다. S→P 간선은 없다
        Assert.DoesNotContain(cases.Values.Where(c => c.Id is "CL21" or "CL22" or "CL23" or "CL24").SelectMany(c => c.Edges), e => e.Parent == "S");
        Assert.Contains(cases["CL24"].Edges, e => e.Parent == "P:cl24.usp_Withdraw" && e.Child == "E:266" && e.Kind == "BLOCKED");

        // CL22: 프로시저가 지운 자식은 PROC_DIRECT, 건드리지 않은 자식만 FK_CASCADE
        var cl22 = cases["CL22"];
        Assert.Equal(["PROC_DIRECT"], cl22.Rows.Single(r => r.Table == "cl22.ChildA" && r.Key.Contains("10")).Causes);
        Assert.Equal(["FK_CASCADE"], cl22.Rows.Single(r => r.Table == "cl22.ChildB").Causes);
        Assert.DoesNotContain(cl22.Edges, e => e.Kind == "FK" && e.Child == "F:cl22.ChildA:DELETE");
        Assert.Contains(cl22.Edges, e => e.Kind == "FK" && e.Child == "F:cl22.ChildB:DELETE" && e.Confidence == "certain");

        // CL23: 트리거 쓰기는 한 단계 깊다
        Assert.Contains(cases["CL23"].Edges, e => e.Kind == "WRITES" && e.Parent == "T:cl23.trg_Acct_upd");

        // CL24: 실패 + 롤백 뒤 남은 변경 + 수집 장치 오류 아님
        var cl24 = cases["CL24"];
        Assert.Equal(("failed", 266), (cl24.ExpectedStatus, cl24.ExpectedErrorNumber!.Value));
        Assert.True(cl24.LeavesResidue);
        Assert.False(cl24.ExpectedToolFault[1]);
        Assert.True(cl24.ExpectedReturnValues.ContainsKey(1) && cl24.ExpectedReturnValues[1] is null); // 문장이 실패해 반환값 없음(null이 기대값)
        Assert.Contains(cl24.Rows, r => r.ResidualCommitted && r.IsUnbound && r.Causes.Contains("PROC_DIRECT"));
        Assert.False(cl24.IsRejectCase);
    }

    [Fact]
    public void Reject_cases_load_with_confirmed_rules_and_statement_indexes()
    {
        var cases = Cases();
        foreach (var id in new[] { "CL25a", "CL25b", "CL25c", "CL25d" })
        {
            var c = cases[id];
            Assert.True(c.IsRejectCase, id);
            Assert.Equal("rejected", c.ExpectedStatus);
            Assert.Empty(c.Rows); // 거부 사례는 변경 정답이 없다
            Assert.NotEmpty(c.ExpectedRejects);
        }
        // 2026-11-13 통합: be-exec-1의 AnalysisRules 이름으로 확정(자리표시 없음)
        Assert.Equal(("REJECT_PROC_DYNAMIC_SQL", 1, false), One(cases["CL25a"]));
        Assert.Equal(("REJECT_PROC_UNBALANCED_COMMIT", 1, false), One(cases["CL25b"]));
        Assert.Equal(("REJECT_PROC_EXTERNAL_EFFECT", 1, false), One(cases["CL25c"]));
        // CL25d: 문장 1은 직접 DELETE(N1 회귀), 문장 2는 EXEC의 쓰기 대상에서 닿는 트리거(같은 도달 폐포 규칙). 하네스가 만들지 않는 DB
        var d = cases["CL25d"];
        Assert.Equal("CascadeLab_Reach", d.Database);
        Assert.Equal(2, d.ExpectedRejects.Count);
        Assert.Equal(("REJECT_TRIGGER_EXTERNAL_EFFECT", 1, false), (d.ExpectedRejects[0].RuleId, d.ExpectedRejects[0].StatementIndex, d.ExpectedRejects[0].Pending));
        Assert.Equal(("REJECT_TRIGGER_EXTERNAL_EFFECT", 2, false), (d.ExpectedRejects[1].RuleId, d.ExpectedRejects[1].StatementIndex, d.ExpectedRejects[1].Pending));
        Assert.False(cases["CL21"].IsRejectCase);

        static (string, int, bool) One(HandCase c) { var r = Assert.Single(c.ExpectedRejects); return (r.RuleId, r.StatementIndex, r.Pending); }
    }

    [Fact]
    public void Reject_diff_matches_exact_and_prefix_and_reports_missing_and_extra()
    {
        // 접두 패턴·자리표시 표시는 하네스 기능이라 사례가 확정된 뒤에도 문장 2를 패턴으로 바꿔 시험한다.
        var hand = Cases()["CL25d"] with { ExpectedRejects = [new("REJECT_TRIGGER_EXTERNAL_EFFECT", 1, false), new("REJECT_TRIGGER_*", 2, true)] };
        Assert.Empty(hand.RejectDiff([("REJECT_TRIGGER_EXTERNAL_EFFECT", 1), ("REJECT_TRIGGER_EXTERNAL_EFFECT", 2)]));
        Assert.Empty(hand.RejectDiff([("REJECT_TRIGGER_OPAQUE", 2), ("REJECT_TRIGGER_EXTERNAL_EFFECT", 1)])); // 순서 무관, 접두 패턴

        var wrongStatement = hand.RejectDiff([("REJECT_TRIGGER_EXTERNAL_EFFECT", 1), ("REJECT_TRIGGER_EXTERNAL_EFFECT", 3)]);
        Assert.Equal(2, wrongStatement.Count);
        Assert.Contains(wrongStatement, x => x.StartsWith("기대한 거부 없음: REJECT_TRIGGER_*@문장 2", StringComparison.Ordinal) && x.Contains("자리표시"));
        Assert.Contains(wrongStatement, x => x == "예상 밖 거부: REJECT_TRIGGER_EXTERNAL_EFFECT@문장 3");

        var exactMismatch = hand.RejectDiff([("REJECT_EXEC", 1), ("REJECT_TRIGGER_X", 2)]); // 문장 1의 규칙 이름이 정확 일치가 아니다
        Assert.Contains(exactMismatch, x => x.StartsWith("기대한 거부 없음: REJECT_TRIGGER_EXTERNAL_EFFECT@문장 1", StringComparison.Ordinal));
        Assert.Contains(exactMismatch, x => x == "예상 밖 거부: REJECT_EXEC@문장 1");

        Assert.Equal(2, hand.RejectDiff([]).Count); // 제품이 거부하지 않으면 기대 둘 다 없음
        Assert.Empty(Cases()["CL25a"].RejectDiff([("REJECT_PROC_DYNAMIC_SQL", 1)]));
    }

    [Fact]
    public void Reject_case_scores_without_oracle_and_stays_out_of_m1_m2_m3()
    {
        var hand = Cases()["CL25a"];
        var suite = new SuiteAccumulator();
        var scorer = new CaseScorer(new MetricsCalculator());
        PreviewOutcome Rejected(string ruleId) => new()
        {
            Analysis = new AnalysisResult
            {
                Rejects = [new Diagnostic
                {
                    StatementIndex = 1, Severity = Severity.Error, RuleId = ruleId, Message = "거부",
                    Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 10 },
                }],
            },
            Record = null,
        };

        var ok = scorer.Score(new CaseRun { RunId = "r", Hand = hand, Oracle = OracleResult.Skipped("db"), Outcome = Rejected("REJECT_PROC_DYNAMIC_SQL") }, suite);
        Assert.Equal(MetricsCsv.CaseColumns.Count, ok.CaseRow.Count);
        string Col(IReadOnlyList<string?> row, string name) => row[MetricsCsv.CaseColumns.ToList().IndexOf(name)]!;
        Assert.Equal("rejected", Col(ok.CaseRow, "status_expected"));
        Assert.Equal("rejected", Col(ok.CaseRow, "status_actual"));
        Assert.Equal("true", Col(ok.CaseRow, "status_match"));
        Assert.Empty(ok.M1Rows);
        Assert.Empty(ok.M2Rows);
        Assert.Empty(ok.M3Rows);
        Assert.Empty(ok.RejectDifferences);
        var actual = Assert.Single(ok.ActualRejects);
        Assert.Equal(("REJECT_PROC_DYNAMIC_SQL", 1), (actual.RuleId, actual.StatementIndex));
        Assert.NotNull(ok.Signature);

        // 예전 제품(EXEC 일반 거부 REJECT_EXEC)이면 확정 이름과 달라 차이로 남는다
        var today = scorer.Score(new CaseRun { RunId = "r", Hand = hand, Oracle = OracleResult.Skipped("db"), Outcome = Rejected("REJECT_EXEC") }, suite);
        Assert.Equal("true", Col(today.CaseRow, "status_match")); // 상태는 rejected라 같다
        Assert.Equal(2, today.RejectDifferences.Count);

        // 제품이 거부하지 않고 실행하면 상태 불일치 + 기대한 거부 없음
        var ran = scorer.Score(new CaseRun { RunId = "r", Hand = hand, Oracle = OracleResult.Skipped("db"), Outcome = null, PreviewError = "x" }, suite);
        Assert.Equal("error", Col(ran.CaseRow, "status_actual"));
        Assert.Equal("false", Col(ran.CaseRow, "status_match"));
    }

    [Fact]
    public void Schema_scripts_and_case_files_exist_and_extra_script_is_outside_the_cl_glob()
    {
        var sqlDir = Path.Combine(RepoPaths.CascadeLab, "sql");
        foreach (var c in Cases().Values.Where(c => c.Id is "CL21" or "CL22" or "CL23" or "CL24" or "CL25a" or "CL25b" or "CL25c" or "CL25d"))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "cases", c.FileName)));
            var script = doc.RootElement.GetProperty("schemaScript").GetString()!;
            Assert.True(File.Exists(Path.Combine(RepoPaths.CascadeLab, script)), $"{c.Id}: {script}");
        }
        // 도달 폐포 사례의 외부 부작용 트리거는 CascadeLab에 두면 CL10(동적 SQL 트리거)이 거부로 바뀐다 → cl*.sql 글롭 밖(sql/extra)에 있어야 한다
        Assert.DoesNotContain(Directory.GetFiles(sqlDir, "cl*.sql"), f => Path.GetFileName(f).Contains("25d", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(sqlDir, "extra", "cl25d.sql")));
        // CascadeLab(cl*.sql)에는 외부 부작용 호출(xp_)이 있는 트리거를 두지 않는다
        foreach (var f in Directory.GetFiles(sqlDir, "cl*.sql"))
            Assert.DoesNotContain("xp_", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void B0_direct_targets_use_proc_direct_rows_for_exec_statements()
    {
        var cl21 = Cases()["CL21"];
        var targets = Baselines.DirectTargets(cl21);
        Assert.Contains("cl21.Parent", targets);
        Assert.Contains("cl21.Touch", targets); // 안쪽 프로시저가 쓴 테이블도 프로시저의 정적 쓰기 대상
        Assert.Contains("cl21.Log", targets);
        // CL22: 프로시저가 직접 지운 ChildA·Parent만. FK 연쇄로 지워진 ChildB는 정적 쓰기 대상이 아니다
        var cl22 = Baselines.DirectTargets(Cases()["CL22"]);
        Assert.Contains("cl22.ChildA", cl22);
        Assert.Contains("cl22.Parent", cl22);
        Assert.DoesNotContain("cl22.ChildB", cl22);
    }
}
