using DbRipple.Eval;
using DbRipple.TestSupport;

namespace DbRipple.Eval.Tests;

/// <summary>
/// 오라클: 전용 원본(감사 장치 없는 사본 dbrt_eval_orc*, cascadelab/sql 시드)에서 CL01~CL08을 실행한 결과가
/// 1주차 기록 cascadelab/verification/logs/oracle-2026-11-10.json과 같고, 원문 배치 경로도 같은 결과를 내며, 손 정답 키와 맞고, 롤백으로 시드 상태로 돌아간다.
/// </summary>
public class OracleTests
{
    /// <summary>음성 대조: 기록 대조·손 정답 대조가 빈 결과로 통과하지 않는다(DB 없음, CL04 기록에서 행을 빼거나 값을 바꾼다).</summary>
    [Fact]
    public void Log_and_hand_comparisons_detect_missing_and_wrong_rows()
    {
        var log = OracleLog.LoadCases(Path.Combine(RepoPaths.CascadeLab, "verification", "logs", "oracle-2026-11-10.json"));
        var hc = HandCase.Load(Path.Combine(RepoPaths.CascadeLab, "cases", "CL04-update-cascade-pk.json"));
        string K(string c, string v) => RowKeys.Canonical([new KeyValuePair<string, string?>(c, v)]);
        var tables = new List<OracleTable>
        {
            new("cl04", "Ch", OracleKeyKind.PK, ["id"], []),
            new("cl04", "P", OracleKeyKind.PK, ["id"], []),
        };
        OracleRowChange U(int id, string before, string after) =>
            new("cl04.Ch", K("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)), ChangeKinds.Updated, 1,
                new Dictionary<string, string?> { ["p_id"] = before }, new Dictionary<string, string?> { ["p_id"] = after }, ["p_id"]);
        var full = new List<OracleRowChange>
        {
            U(10, "1", "101"), U(11, "1", "101"), U(20, "2", "102"),
            new("cl04.P", K("id", "101"), ChangeKinds.Inserted, 1, null, new Dictionary<string, string?>(), []),
            new("cl04.P", K("id", "102"), ChangeKinds.Inserted, 1, null, new Dictionary<string, string?>(), []),
            new("cl04.P", K("id", "1"), ChangeKinds.Deleted, 1, new Dictionary<string, string?>(), null, []),
            new("cl04.P", K("id", "2"), ChangeKinds.Deleted, 1, new Dictionary<string, string?>(), null, []),
        };
        OracleResult R(List<OracleRowChange> ch) => new("db", tables, ch, [new OracleStatement(1, "ok", 2, [])], [], false, 0, "", true, []);

        Assert.Empty(OracleLog.Diff(log["CL04"], R(full)));
        Assert.Empty(hc.DiffAgainst(full));

        var missing = full.Skip(1).ToList();
        Assert.NotEmpty(OracleLog.Diff(log["CL04"], R(missing)));
        Assert.NotEmpty(hc.DiffAgainst(missing));
        var wrong = full.Select((c, i) => i == 0 ? U(10, "1", "999") : c).ToList();
        Assert.NotEmpty(OracleLog.Diff(log["CL04"], R(wrong)));
        Assert.NotEmpty(hc.DiffAgainst(wrong));
        Assert.NotEmpty(OracleLog.Diff(log["CL04"], R(full) with { Statements = [new OracleStatement(1, "failed", null, [547])] }));
        Assert.NotEmpty(CaseScorer.RawBatchDiff(R(full), R(missing)));
    }

    [SqlFact]
    public async Task Oracle_matches_week1_log_and_raw_batch_path_for_CL01_to_CL08()
    {
        var def = TestDb.NewName("eval", "orc");
        var rt = TestDb.NewName("eval", "orcrt");
        await using var cleanup = new TestDatabases(def, rt);
        await CascadeLabSeed.CreateAsync(def, CascadeLabVariant.Default);
        await CascadeLabSeed.CreateAsync(rt, CascadeLabVariant.RecursiveTriggers);

        var cases = HandCase.LoadAll(Path.Combine(RepoPaths.CascadeLab, "cases"));
        var log = OracleLog.LoadCases(Path.Combine(RepoPaths.CascadeLab, "verification", "logs", "oracle-2026-11-10.json"));
        Assert.Equal(11, cases.Count);
        Assert.Equal(cases.Select(c => c.Id).Order(StringComparer.Ordinal), log.Keys.Order(StringComparer.Ordinal));

        var runner = new OracleRunner(new HashSnapshotComparer());
        var cs = TestDb.ConnectionString();
        var problems = new List<string>();
        var digests = new Dictionary<string, HashSet<string>>();
        foreach (var hc in cases)
        {
            var db = hc.Database switch { "CascadeLab" => def, "CascadeLab_RT" => rt, _ => throw new InvalidOperationException(hc.Database) };
            var per = await runner.RunAsync(cs, db, hc.Statements, rawBatch: false, default);
            var raw = await runner.RunAsync(cs, db, hc.Statements, rawBatch: true, default);
            problems.AddRange(OracleLog.Diff(log[hc.Id], per).Select(x => $"{hc.Id} 기록 대조: {x}"));
            problems.AddRange(CaseScorer.RawBatchDiff(per, raw).Select(x => $"{hc.Id} 원문 배치: {x}"));
            problems.AddRange(hc.DiffAgainst(per.Changes).Select(x => $"{hc.Id} 손 정답: {x}"));
            problems.AddRange(per.ComparatorIssues.Concat(raw.ComparatorIssues).Select(x => $"{hc.Id} 비교기: {x}"));
            if (!per.RestoredAfterRollback || !raw.RestoredAfterRollback) problems.Add($"{hc.Id}: 롤백 뒤 시작 상태로 돌아가지 않음");
            if (per.Status != hc.ExpectedStatus) problems.Add($"{hc.Id}: 상태 {per.Status}, 기대 {hc.ExpectedStatus}");
            if (hc.ExpectedErrorNumber is int e && !per.ErrorNumbers.Contains(e)) problems.Add($"{hc.Id}: 오류 {e} 없음");
            (digests.TryGetValue(db, out var set) ? set : digests[db] = []).Add(per.BeforeDigest);
            digests[db].Add(raw.BeforeDigest);
        }
        Assert.Empty(problems);
        // 사례마다 같은 시드 상태에서 시작했다
        Assert.All(digests.Values, s => Assert.Single(s));
    }
}
