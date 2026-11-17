using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using Xunit.Abstractions;

namespace DbRipple.Attribution.Tests;

/// <summary>
/// 성능 변경(2026-11-17 큰 DELETE) 전후 결과 동일성 회귀. 입력마다 Resolve 결과 전체(모델·그래프, ContractJson)의 SHA-256을
/// 변경 전 코드로 만든 지문(tests/fixtures/attribution/resolve-golden.txt)과 비교한다. 귀속·확신도·순서·행 단위 부모가 하나라도 바뀌면 실패한다.
/// 입력: CL20 키운 것 몇 개 + FK 판정 경로(부모 키 중복, UNPAIRED, 이미지 없음, 불완전 묶음, CASCADE·SET NULL·SET DEFAULT, 깊이 다른 후보,
/// 쓰기 경합, 트리거 연결)를 섞은 무작위 입력(고정 시드). 의도한 동작 변경이면 DBR_WRITE_FIXTURES=1로 다시 쓴다.
/// </summary>
public class ResolveEquivalenceTests(ITestOutputHelper output)
{
    const int RandomCases = 400;
    static string GoldenPath => Repo.Path_("tests", "fixtures", "attribution", "resolve-golden.txt");

    [Fact]
    public void Resolution_matches_pre_change_fingerprints()
    {
        var actual = Cases().Select(c => $"{c.Name} {Fingerprint(c.Input)}").ToList();
        if (Environment.GetEnvironmentVariable("DBR_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllLines(GoldenPath, actual);
            output.WriteLine($"지문 {actual.Count}개를 썼다: {GoldenPath}");
            return;
        }
        var expected = File.ReadAllLines(GoldenPath).Where(l => l.Length > 0).ToList();
        Assert.Equal(expected.Count, actual.Count);
        var diff = expected.Zip(actual).Where(p => p.First != p.Second).Select(p => $"기대 {p.First} / 실제 {p.Second}").ToList();
        Assert.True(diff.Count == 0, $"{diff.Count}건 다름:\n" + string.Join("\n", diff.Take(20)));
    }

    /// <summary>같은 입력을 두 번 풀어도 같다(결정적).</summary>
    [Fact]
    public void Resolve_is_deterministic_on_large_input()
    {
        Assert.Equal(Fingerprint(() => LargeDeleteData.Cl20(2_000)), Fingerprint(() => LargeDeleteData.Cl20(2_000)));
    }

    /// <summary>CL20 키운 입력의 결과 모양(행 단위 부모는 자식 p의 부모 행 하나, 확신도 certain).</summary>
    [Fact]
    public void Cl20_scaled_parent_row_ids_are_exact()
    {
        const int n = 1_000;
        var r = new CausalResolver().Resolve(LargeDeleteData.Cl20(n));
        GraphInvariants.Check(r);
        var child = r.Model.Firings.Single(f => f.Table == "cl20.Child");
        Assert.Equal(CauseKind.FkCascade, child.Cause!.Kind);
        Assert.Equal(Confidence.Certain, child.Cause.Confidence);
        Assert.Equal(2 * n, child.Cause.ForeignKey!.MatchedRows);
        var childRows = r.Model.RowChanges.Where(x => x.FiringId == child.Id).ToList();
        for (var i = 1; i <= 2 * n; i++)
            Assert.Equal(new[] { ContractIds.RowChange(21, (i + 1) / 2) }, childRows[i - 1].ParentRowIds);
    }

    /// <summary>
    /// 행 단위 부모(UPDATE 이벤트): before 키가 같은 부모 행을 부모 묶음 순서대로, UNPAIRED·이미지 없는 행은 빼고, CASCADE는 after까지 같은 행만.
    /// SET NULL은 after를 보지 않는다. 부모 k=1 행이 넷(그중 하나 UNPAIRED, 하나 after 다름)이다.
    /// </summary>
    [Fact]
    public void Update_event_parent_row_ids_keep_order_and_filters()
    {
        var cat = new CatalogBuilder()
            .Table("t.P", ["id"], "id", "k").Table("t.C", ["id"], "id", "pk").Table("t.D", ["id"], "id", "pk?")
            .Fk("FK_C_P", "t.C", "pk", "t.P", "k", onUpdate: "CASCADE")
            .Fk("FK_D_P", "t.D", "pk", "t.P", "k", onUpdate: "SET_NULL");
        Dictionary<string, string?> R(params (string K, string? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);
        RowChange Row(long seq, int n, string key, Dictionary<string, string?>? b, Dictionary<string, string?>? a, Pairing p = Pairing.ByPk) => new()
        {
            Id = ContractIds.RowChange(seq, n), FiringId = ContractIds.Firing(seq), Operation = ChangeOperation.Update,
            Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = [key] }, Before = b, After = a, Pairing = p, FinalAfterSource = FinalAfterSource.Audit,
        };
        Firing F(long seq, string table, int rows) => new()
        {
            Id = ContractIds.Firing(seq), CaptureSeq = seq, StatementIndex = 1, Depth = 1, Table = table, Operation = ChangeOperation.Update,
            RowCount = rows, ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
        };
        var rows = new List<RowChange>
        {
            Row(1, 1, "1", R(("id", "1"), ("k", "1")), R(("id", "1"), ("k", "7"))),
            Row(1, 2, "2", R(("id", "2"), ("k", "1")), null, Pairing.Unpaired),
            Row(1, 3, "3", R(("id", "3"), ("k", "1")), R(("id", "3"), ("k", "8"))),
            Row(1, 4, "4", R(("id", "4"), ("k", "1")), R(("id", "4"), ("k", "7"))),
            Row(1, 5, "5", R(("id", "5"), ("k", "2")), R(("id", "5"), ("k", "7"))),
            Row(2, 1, "10", R(("id", "10"), ("pk", "1")), R(("id", "10"), ("pk", "7"))),
            Row(2, 2, "11", R(("id", "11"), ("pk", "1")), R(("id", "11"), ("pk", "8"))),
            Row(2, 3, "12", R(("id", "12"), ("pk", "2")), R(("id", "12"), ("pk", "7"))),
            Row(3, 1, "20", R(("id", "20"), ("pk", "1")), R(("id", "20"), ("pk", null))),
        };
        var r = new CausalResolver().Resolve(new ResolveInput
        {
            Statements =
            [
                new StatementResult
                {
                    Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Update, Target = "t.P",
                    Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 10 }, Status = StatementStatus.Ok, RowsAffected = 5,
                },
            ],
            Collected = new CollectResult { Firings = [F(1, "t.P", 5), F(2, "t.C", 3), F(3, "t.D", 1)], RowChanges = rows, ContextLinks = [], RereadDifferences = [] },
            Net = [],
            Baseline = cat.Build(),
        });
        GraphInvariants.Check(r);
        var parents = r.Model.RowChanges.ToDictionary(x => x.Id, x => x.ParentRowIds);
        Assert.Equal(new[] { "rc-1-1", "rc-1-4" }, parents["rc-2-1"]);
        Assert.Equal(new[] { "rc-1-3" }, parents["rc-2-2"]);
        Assert.Equal(new[] { "rc-1-5" }, parents["rc-2-3"]);
        Assert.Equal(new[] { "rc-1-1", "rc-1-3", "rc-1-4" }, parents["rc-3-1"]);
        Assert.Equal(CauseKind.FkCascade, r.Model.Firings.Single(f => f.Id == "f-2").Cause!.Kind);
        Assert.Equal(CauseKind.FkSetNull, r.Model.Firings.Single(f => f.Id == "f-3").Cause!.Kind);
    }

    /// <summary>무작위 입력이 바꾼 경로(행 단위 부모 찾기)를 실제로 밟는지: FK 종류 셋, 부모 행 여럿, 부분 일치, certain·inferred가 모두 나온다.</summary>
    [Fact]
    public void Fuzz_covers_fk_paths()
    {
        var res = Enumerable.Range(1, RandomCases).Select(s => new CausalResolver().Resolve(FkFuzz.Build(s))).ToList();
        var causes = res.SelectMany(r => r.Model.Firings).Select(f => f.Cause!).ToList();
        var rowsWithParents = res.SelectMany(r => r.Model.RowChanges).Select(r => r.ParentRowIds.Count).Where(c => c > 0).ToList();
        output.WriteLine($"FK 판정 {causes.Count(c => c.ForeignKey is not null)}, 부모 행 1개 {rowsWithParents.Count(c => c == 1)}, 2개 이상 {rowsWithParents.Count(c => c > 1)}");
        foreach (var k in new[] { CauseKind.FkCascade, CauseKind.FkSetNull, CauseKind.FkSetDefault })
            Assert.Contains(causes, c => c.Kind == k);
        Assert.Contains(causes, c => c.ForeignKey is not null && c.Confidence == Confidence.Certain);
        Assert.Contains(causes, c => c.ForeignKey is not null && c.Confidence == Confidence.Inferred);
        Assert.Contains(causes, c => c.ForeignKey is { UnmatchedRows: > 0 });
        Assert.True(rowsWithParents.Count(c => c > 1) >= 10, "부모 행이 여럿인 자식 행이 적다");
    }

    internal static string Fingerprint(Func<ResolveInput> input)
    {
        try
        {
            var r = new CausalResolver().Resolve(input());
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(r, ContractJson.Options)));
        }
        catch (Exception ex)
        {
            return "EXCEPTION:" + ex.GetType().Name;
        }
    }

    static IEnumerable<(string Name, Func<ResolveInput> Input)> Cases()
    {
        foreach (var n in new[] { 1, 2, 3, 257 }) yield return ($"cl20-{n}", () => LargeDeleteData.Cl20(n));
        for (var seed = 1; seed <= RandomCases; seed++)
        {
            var s = seed;
            yield return ($"fuzz-{s}", () => FkFuzz.Build(s));
        }
    }
}

/// <summary>FK 판정 경로를 고르게 밟는 작은 무작위 입력(고정 시드, System.Random 시드 구현은 버전과 무관하게 같다).</summary>
internal static class FkFuzz
{
    static readonly string[] Actions = ["NO_ACTION", "CASCADE", "SET_NULL", "SET_DEFAULT"];

    public static ResolveInput Build(int seed)
    {
        var rnd = new Random(seed);
        var cat = new CatalogBuilder();
        string[] id = ["id"];
        var pkDefault = rnd.Next(3) switch { 0 => "pk=((0))", 1 => "pk=(getdate())", _ => "pk?" };
        cat.Table("t.P", id, "id", "k", "v").Table("t.C", id, "id", pkDefault, "pk2?", "w").Table("t.G", id, "id", "c_id").Table("t.Log", id, "id");
        cat.Fk("FK_C_P", "t.C", "pk", "t.P", "k", Actions[rnd.Next(4)], Actions[rnd.Next(4)]);
        if (rnd.Next(3) == 0) cat.Fk("FK_C_P2", "t.C", "pk2", "t.P", "id", Actions[rnd.Next(4)], Actions[rnd.Next(4)]);
        cat.Fk("FK_G_C", "t.G", "c_id", "t.C", "id", "CASCADE");
        var pEvents = new[] { ChangeOperation.Delete, ChangeOperation.Update };
        if (rnd.Next(3) == 0) cat.Trigger("t.trg_P", "t.P", pEvents, [rnd.Next(2) == 0 ? "t.C" : "t.Log"], dynamicSql: rnd.Next(5) == 0);
        if (rnd.Next(2) == 0) cat.Trigger("t.trg_C", "t.C", pEvents, ["t.Log"], unparsed: rnd.Next(6) == 0);

        var stmtKind = rnd.Next(2) == 0 ? ChangeOperation.Delete : ChangeOperation.Update;
        var statements = new List<StatementResult>
        {
            new()
            {
                Index = 1, Batch = 1, Position = 1, Kind = stmtKind, Target = "t.P",
                Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 10 }, Status = StatementStatus.Ok, RowsAffected = 1,
            },
        };

        var firings = new List<Firing>();
        var rows = new List<RowChange>();
        var links = new List<TriggerContextLink>();
        var net = new List<NetChange>();
        long seq = 100;
        var nextKey = 1;
        string V(int x) => x.ToString(CultureInfo.InvariantCulture);
        string? Pick(params string?[] xs) => xs[rnd.Next(xs.Length)];

        Firing AddFiring(int depth, string table, ChangeOperation op, List<(Dictionary<string, string?>? B, Dictionary<string, string?>? A, Pairing Pairing)> rs)
        {
            var s = ++seq;
            var f = new Firing
            {
                Id = ContractIds.Firing(s), CaptureSeq = s, StatementIndex = 1, Depth = depth, Table = table, Operation = op,
                RowCount = rs.Count + (rnd.Next(6) == 0 ? 1 : 0), ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
            };
            firings.Add(f);
            var n = 0;
            foreach (var (b, a, pairing) in rs)
            {
                var keyVal = (a ?? b)?.GetValueOrDefault("id") ?? V(nextKey++);
                var keysOnly = rnd.Next(7) == 0; // KEYS 모드 흉내: 이미지 없음
                var rc = new RowChange
                {
                    Id = ContractIds.RowChange(s, ++n), FiringId = f.Id, Operation = op,
                    Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = [keyVal] },
                    Before = keysOnly ? null : b, After = keysOnly ? null : a,
                    ChangedColumns = b is not null && a is not null ? a.Keys.Where(k => b.GetValueOrDefault(k) != a[k]).ToList() : [],
                    Pairing = pairing, FinalAfterSource = FinalAfterSource.Audit,
                };
                rows.Add(rc);
                if (rnd.Next(2) == 0)
                    net.Add(new NetChange
                    {
                        Table = table, Key = rc.Key, Multiplicity = 1, Before = b, After = a,
                        Change = op switch { ChangeOperation.Insert => NetChangeKind.Inserted, ChangeOperation.Delete => NetChangeKind.Deleted, _ => NetChangeKind.Updated },
                        ChangedColumns = rc.ChangedColumns,
                    });
            }
            return f;
        }

        // 부모 묶음 1~2개. 키 k는 작은 범위에서 뽑아 중복이 생기게 한다.
        var parentKeys = new List<(string? Before, string? After)>();
        var parents = new List<Firing>();
        for (var pf = 0; pf < 1 + rnd.Next(2); pf++)
        {
            var op = rnd.Next(5) == 0 ? (stmtKind == ChangeOperation.Delete ? ChangeOperation.Update : ChangeOperation.Delete) : stmtKind;
            var rs = new List<(Dictionary<string, string?>?, Dictionary<string, string?>?, Pairing)>();
            for (var i = 0; i < rnd.Next(9); i++)
            {
                var pid = V(nextKey++);
                var kb = Pick(V(rnd.Next(1, 4)), V(rnd.Next(1, 4)), V(rnd.Next(1, 6)), null);
                var ka = Pick(V(rnd.Next(1, 6)), V(rnd.Next(6, 9)), kb);
                var b = new Dictionary<string, string?> { ["id"] = pid, ["k"] = kb, ["v"] = "x" };
                if (op == ChangeOperation.Delete) { rs.Add((b, null, Pairing.ByPk)); parentKeys.Add((kb, null)); continue; }
                var a = new Dictionary<string, string?> { ["id"] = pid, ["k"] = ka, ["v"] = "y" };
                switch (rnd.Next(5))
                {
                    case 0: rs.Add((b, null, Pairing.Unpaired)); break;
                    case 1: rs.Add((null, a, Pairing.Unpaired)); break;
                    default: rs.Add((b, a, Pairing.ByPk)); break;
                }
                parentKeys.Add((kb, ka));
            }
            parents.Add(AddFiring(rnd.Next(4) == 0 ? 2 : 1, "t.P", op, rs));
        }

        // 자식 묶음 1~2개: 부모 키(일치), 없는 키, NULL을 섞는다. UPDATE면 after는 NULL·기본값·부모 after·그대로·무작위.
        var childFirings = new List<Firing>();
        for (var cf = 0; cf < 1 + rnd.Next(2); cf++)
        {
            var op = rnd.Next(2) == 0 ? ChangeOperation.Delete : ChangeOperation.Update;
            var rs = new List<(Dictionary<string, string?>?, Dictionary<string, string?>?, Pairing)>();
            for (var i = 0; i < rnd.Next(11); i++)
            {
                var cid = V(nextKey++);
                var pk = parentKeys.Count > 0 && rnd.Next(4) != 0 ? parentKeys[rnd.Next(parentKeys.Count)] : (Pick(V(99), null, V(rnd.Next(1, 6))), null);
                var pk2 = Pick(null, V(rnd.Next(1, 30)));
                var b = new Dictionary<string, string?> { ["id"] = cid, ["pk"] = pk.Before, ["pk2"] = pk2, ["w"] = "1" };
                if (op == ChangeOperation.Delete) { rs.Add((b, null, Pairing.ByPk)); continue; }
                var after = Pick(null, "0", pk.After, pk.Before, V(rnd.Next(1, 9)));
                var a = new Dictionary<string, string?> { ["id"] = cid, ["pk"] = after, ["pk2"] = rnd.Next(3) == 0 ? null : pk2, ["w"] = "2" };
                rs.Add(rnd.Next(8) == 0 ? (b, null, Pairing.Unpaired) : (b, a, Pairing.ByPk));
            }
            childFirings.Add(AddFiring(rnd.Next(3) == 0 ? 2 : 1, "t.C", op, rs));
        }

        // 손자(FK_G_C CASCADE)와 트리거가 쓴 Log 묶음.
        if (rnd.Next(2) == 0)
        {
            var rs = new List<(Dictionary<string, string?>?, Dictionary<string, string?>?, Pairing)>();
            var childIds = rows.Where(r => childFirings.Any(f => f.Id == r.FiringId)).Select(r => r.Key.Values[0]).ToList();
            for (var i = 0; i < rnd.Next(5); i++)
                rs.Add((new Dictionary<string, string?> { ["id"] = V(nextKey++), ["c_id"] = childIds.Count > 0 && rnd.Next(3) != 0 ? childIds[rnd.Next(childIds.Count)] : V(500) }, null, Pairing.ByPk));
            AddFiring(1, "t.G", ChangeOperation.Delete, rs);
        }
        if (rnd.Next(2) == 0)
        {
            var rs = new List<(Dictionary<string, string?>?, Dictionary<string, string?>?, Pairing)>();
            for (var i = 0; i < 1 + rnd.Next(3); i++) rs.Add((null, new Dictionary<string, string?> { ["id"] = V(nextKey++) }, Pairing.ByPk));
            var log = AddFiring(2, "t.Log", ChangeOperation.Insert, rs);
            var cands = firings.Where(f => f.Depth == 1 && f.Id != log.Id).Select(f => f.Id).ToList();
            if (cands.Count > 0 && rnd.Next(4) != 0)
                links.Add(new TriggerContextLink
                {
                    ChildFiringId = log.Id, CandidateParentFiringIds = cands.Take(1 + rnd.Next(cands.Count)).ToList(),
                    ConfidenceCap = rnd.Next(3) == 0 ? Confidence.Ambiguous : Confidence.Inferred,
                });
        }

        // 묶음 없는 최종 차이 하나(잔차 경로).
        if (rnd.Next(3) == 0)
            net.Add(new NetChange
            {
                Table = "t.Log", Key = new RowKey { Kind = KeyKind.PK, Columns = ["id"], Values = [V(900)] }, Change = NetChangeKind.Inserted, Multiplicity = 1,
                After = new Dictionary<string, string?> { ["id"] = V(900) },
            });

        return new ResolveInput
        {
            Statements = statements,
            Collected = new CollectResult { Firings = firings, RowChanges = rows, ContextLinks = links, RereadDifferences = [] },
            Net = net,
            Baseline = cat.Build(recursiveTriggers: rnd.Next(4) == 0),
        };
    }
}
