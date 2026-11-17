using System.Globalization;
using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;

namespace DbRipple.Attribution.Tests;

/// <summary>
/// CL20 픽스처(tests/fixtures/capture/CL20, "DELETE FROM cl20.Parent WHERE id &lt;= N")를 부모 N행으로 키운 C13 입력(DB 없음, 2026-11-17 성능).
/// 모양은 픽스처 collect.json과 같다: ChildLog INSERT(깊이 2, 2N행) → Child DELETE(깊이 1, 2N행, FK 연쇄) → Parent DELETE(깊이 1, N행), 모두 FULL.
/// 부모 p의 자식 id는 2p-1, 2p(cl20.sql). 기준선은 실제 카탈로그 픽스처 + C6 정적 분석이다.
/// </summary>
internal static class LargeDeleteData
{
    static readonly Lazy<BaselineContext> Baseline = new(() =>
    {
        var b = JsonSerializer.Deserialize<BaselineContext>(File.ReadAllText(Repo.Path_("tests", "fixtures", "capture", "baseline-cascadelab.json")), ContractJson.Options)!;
        var t = JsonSerializer.Deserialize<List<TriggerStaticInfo>>(File.ReadAllText(Repo.Path_("tests", "fixtures", "capture", "triggers-cascadelab.json")), ContractJson.Options)!;
        return b with { Triggers = t };
    });

    public static ResolveInput Cl20(int parents)
    {
        const long logSeq = 19, childSeq = 20, parentSeq = 21;
        var children = 2 * parents;
        var s = new string[children + 1];
        for (var i = 1; i <= children; i++) s[i] = i.ToString(CultureInfo.InvariantCulture);
        string P(int child) => s[(child + 1) / 2];

        var firings = new List<Firing>
        {
            Firing(logSeq, 2, "cl20.ChildLog", ChangeOperation.Insert, children),
            Firing(childSeq, 1, "cl20.Child", ChangeOperation.Delete, children),
            Firing(parentSeq, 1, "cl20.Parent", ChangeOperation.Delete, parents),
        };
        var pairing = children == 1 ? Pairing.SingleRow : Pairing.ByPk;
        var rows = new List<RowChange>(2 * children + parents);
        for (var i = 1; i <= children; i++)
            rows.Add(Row(logSeq, i, ChangeOperation.Insert, "child_id", s[i], null,
                new Dictionary<string, string?> { ["child_id"] = s[i], ["parent_id"] = P(i), ["qty"] = "1" }, pairing));
        for (var i = 1; i <= children; i++)
            rows.Add(Row(childSeq, i, ChangeOperation.Delete, "id", s[i],
                new Dictionary<string, string?> { ["id"] = s[i], ["parent_id"] = P(i), ["qty"] = "1" }, null, pairing));
        for (var i = 1; i <= parents; i++)
            rows.Add(Row(parentSeq, i, ChangeOperation.Delete, "id", s[i],
                new Dictionary<string, string?> { ["id"] = s[i], ["total"] = "2" }, null, parents == 1 ? Pairing.SingleRow : Pairing.ByPk));

        // 최종 차이: 테이블 이름순, 키 오름차순(픽스처 raw.json과 같은 순서).
        var net = new List<NetChange>(2 * children + parents);
        for (var i = 1; i <= children; i++)
            net.Add(Net("cl20.Child", "id", s[i], NetChangeKind.Deleted, new Dictionary<string, string?> { ["id"] = s[i], ["parent_id"] = P(i), ["qty"] = "1" }, null));
        for (var i = 1; i <= children; i++)
            net.Add(Net("cl20.ChildLog", "child_id", s[i], NetChangeKind.Inserted, null, new Dictionary<string, string?> { ["child_id"] = s[i], ["parent_id"] = P(i), ["qty"] = "1" }));
        for (var i = 1; i <= parents; i++)
            net.Add(Net("cl20.Parent", "id", s[i], NetChangeKind.Deleted, new Dictionary<string, string?> { ["id"] = s[i], ["total"] = "2" }, null));

        var sql = $"DELETE FROM cl20.Parent WHERE id <= {parents};";
        return new ResolveInput
        {
            Statements =
            [
                new StatementResult
                {
                    Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Delete, Target = "cl20.Parent",
                    Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = sql.Length },
                    Status = StatementStatus.Ok, RowsAffected = parents, CaptureSeqRange = [logSeq, parentSeq],
                },
            ],
            Collected = new CollectResult
            {
                Firings = firings,
                RowChanges = rows,
                ContextLinks = [new TriggerContextLink { ChildFiringId = ContractIds.Firing(logSeq), CandidateParentFiringIds = [ContractIds.Firing(childSeq)], ConfidenceCap = Confidence.Inferred }],
                RereadDifferences = [],
            },
            Net = net,
            Baseline = Baseline.Value,
            StatementSql = new Dictionary<int, string> { [1] = sql },
        };
    }

    static Firing Firing(long seq, int depth, string table, ChangeOperation op, long rows) => new()
    {
        Id = ContractIds.Firing(seq), CaptureSeq = seq, StatementIndex = 1, Depth = depth, Table = table, Operation = op,
        RowCount = rows, ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
    };

    static RowChange Row(long seq, int n, ChangeOperation op, string keyColumn, string key,
        IReadOnlyDictionary<string, string?>? before, IReadOnlyDictionary<string, string?>? after, Pairing pairing) => new()
    {
        Id = ContractIds.RowChange(seq, n), FiringId = ContractIds.Firing(seq), Operation = op,
        Key = new RowKey { Kind = KeyKind.PK, Columns = [keyColumn], Values = [key] },
        Before = before, After = after, Pairing = pairing, FinalAfterSource = FinalAfterSource.Audit,
    };

    static NetChange Net(string table, string keyColumn, string key, NetChangeKind change,
        IReadOnlyDictionary<string, string?>? before, IReadOnlyDictionary<string, string?>? after) => new()
    {
        Table = table, Key = new RowKey { Kind = KeyKind.PK, Columns = [keyColumn], Values = [key] },
        Change = change, Multiplicity = 1, Before = before, After = after,
    };
}
