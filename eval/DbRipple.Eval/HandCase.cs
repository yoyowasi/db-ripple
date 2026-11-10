using System.Text.Json;

namespace DbRipple.Eval;

/// <summary>손 정답의 변경 행 하나(cascadelab/answer-format.md "changes 행").</summary>
public sealed record HandRow(
    string Table,
    string Kind,
    string Key,
    IReadOnlyList<string> Causes,
    IReadOnlyList<string> ChangedColumns,
    IReadOnlyDictionary<string, string?> After)
{
    /// <summary>보고용 "테이블(열=값)".</summary>
    public string Display => Table + RowKeys.Display(Key);
}

/// <summary>손 정답 간선 하나(answer-format.md "edges 항목"). Parent·Child는 NodeRefs.Normalize를 거친 노드 참조다. M3 간선 키 = (StatementIndex, Parent, Child, Kind, Name).</summary>
public sealed record HandEdge(int StatementIndex, string Parent, string Child, string Kind, string? Name, string Confidence);

/// <summary>
/// 손 정답 노드 참조(S, F:…, T:…, R:…, E:…)의 정규화. answer-format.md는 "한 문장에서 같은 노드가 여러 번 나오면 #k"인데,
/// 실행이 한 번뿐인데 "#1"을 붙인 표기가 있다(CL08a의 T:cl08.trg_Acct_upd#1, 2026-11-10 attrib-1 발견).
/// C13 비교기와 같은 규칙: 같은 문장 안에 같은 이름의 #2가 없으면 "#1"을 뗀다. #2 이상이 있으면 그대로 둔다. 손 정답 파일은 고치지 않는다.
/// </summary>
public static class NodeRefs
{
    /// <summary>한 문장의 참조 목록 → (원래 참조 → 정규화 참조).</summary>
    public static IReadOnlyDictionary<string, string> Normalize(IEnumerable<string> refsInStatement)
    {
        var refs = refsInStatement.Distinct(StringComparer.Ordinal).ToList();
        var multi = refs.Select(Split).Where(x => x.K >= 2).Select(x => x.Base).ToHashSet(StringComparer.Ordinal);
        return refs.ToDictionary(r => r, r => Split(r) is var (b, k) && k == 1 && !multi.Contains(b) ? b : r, StringComparer.Ordinal);
    }

    static (string Base, int K) Split(string r)
    {
        var i = r.LastIndexOf('#');
        return i > 0 && int.TryParse(r.AsSpan(i + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var k)
            ? (r[..i], k) : (r, 0);
    }
}

/// <summary>손 정답 사례(cascadelab/cases/*.json, 형식 v0.1). M2 원인 버킷과 cases.csv 기대값의 입력이다.</summary>
public sealed record HandCase(
    string Id,
    string Pattern,
    string Database,
    string FileName,
    IReadOnlyList<string> Statements,
    string ExpectedStatus,
    int? ExpectedErrorNumber,
    IReadOnlyDictionary<int, string> ExpectedStatementStatus,
    IReadOnlyDictionary<string, IReadOnlyList<string>> KeyColumns,
    IReadOnlyList<HandRow> Rows)
{
    /// <summary>기대 간선(노드 참조 정규화 뒤, M3 입력). 3주차 M3 계산 전까지는 읽기만 한다.</summary>
    public IReadOnlyList<HandEdge> Edges { get; init; } = [];

    /// <summary>정규화로 바뀐 노드 참조("원래 → 정규화", 보고용).</summary>
    public IReadOnlyList<string> NormalizedRefs { get; init; } = [];

    /// <summary>M2 원인 값(§10.4, answer-format.md). oneOf는 ONE_OF 하나로 센다.</summary>
    public static readonly IReadOnlyList<string> Causes =
        ["DIRECT", "FK_CASCADE", "FK_SET_NULL", "FK_SET_DEFAULT", "TRIGGER", "SAME_TABLE_TRIGGER", "ONE_OF"];

    /// <summary>사례 파일 하나를 읽는다.</summary>
    public static HandCase Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var exp = root.GetProperty("expected");
        var statements = root.GetProperty("statements").EnumerateArray()
            .OrderBy(s => s.GetProperty("index").GetInt32()).Select(s => s.GetProperty("sql").GetString()!).ToList();
        var stStatus = exp.GetProperty("statementStatus").EnumerateArray()
            .ToDictionary(s => s.GetProperty("index").GetInt32(), s => s.GetProperty("status").GetString()!);
        var keyCols = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<HandRow>();
        foreach (var ch in exp.GetProperty("changes").EnumerateArray())
        {
            var table = ch.GetProperty("table").GetString()!;
            keyCols[table] = ch.GetProperty("keyColumns").EnumerateArray().Select(x => x.GetString()!).ToList();
            foreach (var (prop, kind) in new[] { ("inserted", ChangeKinds.Inserted), ("updated", ChangeKinds.Updated), ("deleted", ChangeKinds.Deleted) })
                foreach (var r in ch.GetProperty(prop).EnumerateArray())
                {
                    var causes = r.GetProperty("causes").EnumerateArray()
                        .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()! : "ONE_OF").Distinct().ToList();
                    var changed = r.TryGetProperty("changedColumns", out var cc) ? cc.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
                    var after = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    if (r.TryGetProperty("after", out var af))
                        foreach (var p in af.EnumerateObject()) after[p.Name] = RowKeys.JsonValueText(p.Value);
                    rows.Add(new HandRow(table, kind, RowKeys.FromJsonObject(r.GetProperty("key")), causes, changed, after));
                }
        }
        int? err = exp.GetProperty("errorNumber").ValueKind == JsonValueKind.Number ? exp.GetProperty("errorNumber").GetInt32() : null;
        var rawEdges = exp.TryGetProperty("edges", out var es) ? es.EnumerateArray().ToList() : [];
        var edges = new List<HandEdge>();
        var normalized = new List<string>();
        foreach (var g in rawEdges.GroupBy(e => e.GetProperty("statementIndex").GetInt32()))
        {
            var map = NodeRefs.Normalize(g.SelectMany(e => new[] { e.GetProperty("parent").GetString()!, e.GetProperty("child").GetString()! }));
            normalized.AddRange(map.Where(p => p.Key != p.Value).Select(p => $"문장 {g.Key}: {p.Key} → {p.Value}"));
            edges.AddRange(g.Select(e => new HandEdge(g.Key, map[e.GetProperty("parent").GetString()!], map[e.GetProperty("child").GetString()!],
                e.GetProperty("kind").GetString()!, e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                e.GetProperty("confidence").GetString()!)));
        }
        return new HandCase(root.GetProperty("id").GetString()!, root.GetProperty("pattern").GetString()!, root.GetProperty("database").GetString()!,
            Path.GetFileName(path), statements, exp.GetProperty("status").GetString()!, err, stStatus, keyCols, rows)
        { Edges = edges, NormalizedRefs = normalized };
    }

    /// <summary>폴더의 모든 사례(id 서수 순).</summary>
    public static IReadOnlyList<HandCase> LoadAll(string casesDir) =>
        Directory.GetFiles(casesDir, "CL*.json").Select(Load).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 오라클 순 변경이 손 정답과 맞는지(oracle_vs_hand_keys_match). 비교: 테이블별 (변경 종류, 키) 집합, UPDATED의 changedColumns 집합,
    /// 손 정답이 after에 적은 열의 값. 손 정답에 없는 테이블의 변경도 불일치다(answer-format.md "changes에 없는 테이블은 변경 0").
    /// 키 없는 테이블의 MULTISET_*는 손 정답 형식에 없어 불일치로 남긴다. 차이 목록이 비면 일치.
    /// </summary>
    public IReadOnlyList<string> DiffAgainst(IReadOnlyList<OracleRowChange> oracle)
    {
        var diffs = new List<string>();
        var hand = Rows.ToDictionary(r => (r.Table.ToUpperInvariant(), r.Kind, r.Key));
        var seen = new HashSet<(string, string, string)>();
        foreach (var o in oracle)
        {
            var id = (o.Table.ToUpperInvariant(), o.Change, o.Key);
            if (!hand.TryGetValue(id, out var h)) { diffs.Add($"오라클에만 있음: {o.Change} {o.Display}"); continue; }
            seen.Add(id);
            if (o.Change != ChangeKinds.Updated) continue;
            var oc = o.ChangedColumns.Select(c => c.ToUpperInvariant()).ToHashSet();
            if (!oc.SetEquals(h.ChangedColumns.Select(c => c.ToUpperInvariant())))
                diffs.Add($"changedColumns 다름: {o.Display} 오라클 [{string.Join(",", o.ChangedColumns)}] 손 [{string.Join(",", h.ChangedColumns)}]");
            foreach (var (col, val) in h.After)
            {
                var ov = o.After?.FirstOrDefault(p => string.Equals(p.Key, col, StringComparison.OrdinalIgnoreCase));
                if (ov is null || ov.Value.Key is null) { diffs.Add($"after 열 없음: {o.Display}.{col}"); continue; }
                if (!string.Equals(ov.Value.Value, val, StringComparison.Ordinal))
                    diffs.Add($"after 값 다름: {o.Display}.{col} 오라클 {ov.Value.Value ?? "NULL"} 손 {val ?? "NULL"}");
            }
        }
        foreach (var h in Rows.Where(r => !seen.Contains((r.Table.ToUpperInvariant(), r.Kind, r.Key))))
            diffs.Add($"손 정답에만 있음: {h.Kind} {h.Display}");
        return diffs;
    }
}
