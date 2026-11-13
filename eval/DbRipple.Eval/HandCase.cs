using System.Globalization;
using System.Text.Json;

namespace DbRipple.Eval;

/// <summary>
/// 손 정답의 변경 행 하나(cascadelab/answer-format.md "changes 행", v0.2).
/// IDENTITY 행(v0.2 {"match", "identity"})은 키가 실행마다 달라 Key에 자리 표시(UnboundPrefix + match 정규형)를 두고, 비교 대상에 묶을 때(HandCase.Bind) 실제 키로 바꾼다.
/// 키 없는 테이블(v0.2 keyKind ROW)의 행은 Kind = MULTISET_REMOVED·MULTISET_ADDED, Key = 행 값 정규형, Multiplicity = 개수다.
/// </summary>
public sealed record HandRow(
    string Table,
    string Kind,
    string Key,
    IReadOnlyList<string> Causes,
    IReadOnlyList<string> ChangedColumns,
    IReadOnlyDictionary<string, string?> After)
{
    /// <summary>묶이지 않은 IDENTITY 행 키의 머리(어떤 정규 키와도 같지 않다).</summary>
    public const string UnboundPrefix = "?match:";

    /// <summary>보고용 "테이블(열=값)". 묶이지 않은 IDENTITY 행은 "테이블{match (열=값)}".</summary>
    public string Display => Table + (IsUnbound ? "{match " + RowKeys.Display(Key[UnboundPrefix.Length..]) + "}" : RowKeys.Display(Key));

    /// <summary>v0.2 IDENTITY 행의 match(비결정 아닌 열 값). key 표기 행이면 null.</summary>
    public IReadOnlyDictionary<string, string?>? Match { get; init; }
    /// <summary>v0.2 identity(그 행의 IDENTITY 열).</summary>
    public IReadOnlyList<string> Identity { get; init; } = [];
    /// <summary>v0.2 ref(사례 안 고유 라벨, 자식의 identityRefs가 가리킨다).</summary>
    public string? Ref { get; init; }
    /// <summary>v0.2 identityRefs(FK 열 → 부모 IDENTITY 행 라벨). 자식 FK 값 = 짝지은 부모 행의 IDENTITY 값이어야 한다(D7).</summary>
    public IReadOnlyDictionary<string, HandIdentityRef> IdentityRefs { get; init; } = new Dictionary<string, HandIdentityRef>();
    /// <summary>v0.2 volatile(비결정 셀): 값은 비교하지 않고 오라클 after에 NULL 아닌 값이 있는지만 본다.</summary>
    public IReadOnlyList<string> Volatile { get; init; } = [];
    /// <summary>다중집합 개수(ROW 행). 키 있는 행은 1.</summary>
    public long Multiplicity { get; init; } = 1;
    /// <summary>expected.residualCommitted에서 온 행(트랜잭션 밖에서 확정된 변경, v0.2).</summary>
    public bool ResidualCommitted { get; init; }
    /// <summary>아직 실제 키에 묶이지 않은 IDENTITY 행인지.</summary>
    public bool IsUnbound => Key.StartsWith(UnboundPrefix, StringComparison.Ordinal);
}

/// <summary>v0.2 identityRefs 값 {"table", "ref"}.</summary>
public sealed record HandIdentityRef(string Table, string Ref);

/// <summary>
/// v0.3 expected.rejects 항목 {ruleId, statementIndex}. ruleId가 "*"로 끝나면 접두 패턴이다(예 "REJECT_PROC_*").
/// 규칙 ID는 제품 쪽(AnalysisRules)에서 정하는 중이라 Pending이 true인 항목은 자리표시이고 통합 때 확정한다(문자열로만 다루고 제품 계약 enum에 기대지 않는다).
/// </summary>
public sealed record HandReject(string RuleId, int StatementIndex, bool Pending)
{
    /// <summary>실제 ruleId가 이 기대와 맞는지(정확히 같거나 접두 패턴).</summary>
    public bool Matches(string actualRuleId) =>
        RuleId.EndsWith('*') ? actualRuleId.StartsWith(RuleId[..^1], StringComparison.Ordinal) : string.Equals(RuleId, actualRuleId, StringComparison.Ordinal);

    /// <summary>보고용 "ruleId@문장 n".</summary>
    public string Display => $"{RuleId}@문장 {StatementIndex.ToString(CultureInfo.InvariantCulture)}{(Pending ? "(ruleId 자리표시, 통합 때 확정)" : "")}";
}

/// <summary>v0.2 scaling(CL20 M5 문장 틀). Statement·Generate의 {N}을 행 수로 바꾼다.</summary>
public sealed record HandScaling(string Statement, string? Generate)
{
    /// <summary>{N} 치환.</summary>
    public static string Fill(string template, long n) => template.Replace("{N}", n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

/// <summary>손 정답 간선 하나(answer-format.md "edges 항목"). Parent·Child는 NodeRefs.Normalize를 거친 노드 참조다. M3 간선 키 = (StatementIndex, Parent, Child, Kind, Name).</summary>
public sealed record HandEdge(int StatementIndex, string Parent, string Child, string Kind, string? Name, string Confidence)
{
    /// <summary>FIRES 간선의 observed(기본 true, false면 변경 없이 끝난 트리거 실행 = 점선).</summary>
    public bool Observed { get; init; } = true;

    /// <summary>M3 간선 키 문자열.</summary>
    public string Key => EdgeKeys.Compose(StatementIndex, Parent, Child, Kind, Name);
}

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

    /// <summary>"바탕#k" → (바탕, k). #k가 없으면 (참조, 0).</summary>
    public static (string Base, int K) Split(string r)
    {
        var i = r.LastIndexOf('#');
        return i > 0 && int.TryParse(r.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var k)
            ? (r[..i], k) : (r, 0);
    }
}

/// <summary>손 정답 사례(cascadelab/cases/*.json, 형식 v0.1·v0.2). M2 원인 버킷과 cases.csv 기대값, M3 간선의 입력이다.</summary>
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
    /// <summary>기대 간선(노드 참조 정규화 뒤, M3 입력).</summary>
    public IReadOnlyList<HandEdge> Edges { get; init; } = [];

    /// <summary>정규화로 바뀐 노드 참조("원래 → 정규화", 보고용).</summary>
    public IReadOnlyList<string> NormalizedRefs { get; init; } = [];

    /// <summary>v0.2 leavesResidue: 오라클 롤백 뒤에도 원본에 행이 남는 사례(시드 digest 불일치가 정상).</summary>
    public bool LeavesResidue { get; init; }

    /// <summary>v0.2 expected.warnings(ruleId, 채점하지 않음).</summary>
    public IReadOnlyList<string> ExpectedWarnings { get; init; } = [];

    /// <summary>v0.2 scaling(CL20 계열만).</summary>
    public HandScaling? Scaling { get; init; }

    /// <summary>v0.3 expected.rejects(거부 사례의 정답). 없으면 빈 목록.</summary>
    public IReadOnlyList<HandReject> ExpectedRejects { get; init; } = [];

    /// <summary>v0.3 statements[].returnValue: 문장 번호 → 기대 반환값(null이면 반환값이 없어야 함). 필드가 없는 문장은 키가 없다.</summary>
    public IReadOnlyDictionary<int, int?> ExpectedReturnValues { get; init; } = new Dictionary<int, int?>();

    /// <summary>v0.3 expected.statementStatus[].isToolFault: 문장 번호 → 기대 분류(필드가 없는 문장은 키가 없다).</summary>
    public IReadOnlyDictionary<int, bool> ExpectedToolFault { get; init; } = new Dictionary<int, bool>();

    /// <summary>v0.3 거부 사례(expected.status = rejected 또는 rejects가 있음). 오라클을 실행하지 않고 M1~M3·대조군에 넣지 않는다.</summary>
    public bool IsRejectCase => ExpectedStatus == "rejected" || ExpectedRejects.Count > 0;

    /// <summary>M2 원인 값(§10.4, answer-format.md v0.3). oneOf는 ONE_OF 하나로 센다. INSTEAD_OF는 v0.2(CL11), PROC_DIRECT는 v0.3(EXEC)에서 더했다.</summary>
    public static readonly IReadOnlyList<string> Causes =
        ["DIRECT", "FK_CASCADE", "FK_SET_NULL", "FK_SET_DEFAULT", "TRIGGER", "INSTEAD_OF", "SAME_TABLE_TRIGGER", "ONE_OF", "PROC_DIRECT"];

    /// <summary>사례 파일 하나를 읽는다(v0.1·v0.2).</summary>
    public static HandCase Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var exp = root.GetProperty("expected");
        var statements = root.GetProperty("statements").EnumerateArray()
            .OrderBy(s => s.GetProperty("index").GetInt32()).Select(s => s.GetProperty("sql").GetString()!).ToList();
        var stStatus = exp.GetProperty("statementStatus").EnumerateArray()
            .ToDictionary(s => s.GetProperty("index").GetInt32(), s => s.GetProperty("status").GetString()!);
        // v0.3: statements[].returnValue(키가 있으면 null도 기대값), statementStatus[].isToolFault, expected.rejects[]
        var returnValues = new Dictionary<int, int?>();
        foreach (var s in root.GetProperty("statements").EnumerateArray())
            if (s.TryGetProperty("returnValue", out var rv) && rv.ValueKind is JsonValueKind.Number or JsonValueKind.Null)
                returnValues[s.GetProperty("index").GetInt32()] = rv.ValueKind == JsonValueKind.Number ? rv.GetInt32() : null;
        var toolFault = new Dictionary<int, bool>();
        foreach (var s in exp.GetProperty("statementStatus").EnumerateArray())
            if (s.TryGetProperty("isToolFault", out var tf) && tf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                toolFault[s.GetProperty("index").GetInt32()] = tf.ValueKind == JsonValueKind.True;
        var rejects = new List<HandReject>();
        if (exp.TryGetProperty("rejects", out var rj) && rj.ValueKind == JsonValueKind.Array)
            foreach (var x in rj.EnumerateArray())
                rejects.Add(new HandReject(x.GetProperty("ruleId").GetString()!, x.GetProperty("statementIndex").GetInt32(), IsTrue(x, "ruleIdPending")));
        var keyCols = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<HandRow>();
        ReadChanges(exp.GetProperty("changes"), false, keyCols, rows);
        if (exp.TryGetProperty("residualCommitted", out var rc) && rc.ValueKind == JsonValueKind.Array) ReadChanges(rc, true, keyCols, rows);
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
                e.GetProperty("confidence").GetString()!)
            {
                Observed = !(e.TryGetProperty("observed", out var ob) && ob.ValueKind == JsonValueKind.False),
            }));
        }
        HandScaling? scaling = null;
        if (root.TryGetProperty("scaling", out var sc) && sc.ValueKind == JsonValueKind.Object
            && sc.TryGetProperty("statement", out var sst) && sst.ValueKind == JsonValueKind.String)
            scaling = new HandScaling(sst.GetString()!, sc.TryGetProperty("generate", out var sg) && sg.ValueKind == JsonValueKind.String ? sg.GetString() : null);
        return new HandCase(root.GetProperty("id").GetString()!, root.GetProperty("pattern").GetString()!, root.GetProperty("database").GetString()!,
            Path.GetFileName(path), statements, exp.GetProperty("status").GetString()!, err, stStatus, keyCols, rows)
        {
            Edges = edges,
            NormalizedRefs = normalized,
            LeavesResidue = IsTrue(root, "leavesResidue") || IsTrue(exp, "leavesResidue"),
            ExpectedWarnings = Strings(exp, "warnings"),
            Scaling = scaling,
            ExpectedRejects = rejects,
            ExpectedReturnValues = returnValues,
            ExpectedToolFault = toolFault,
        };
    }

    /// <summary>
    /// v0.3 거부 비교: 기대 (ruleId 또는 접두 패턴, 문장 번호) 집합 대 제품이 낸 거부. 같은 문장 번호에서 ruleId가 맞는 거부를 하나씩 짝짓는다.
    /// 반환: 차이 목록(비면 일치). "기대한 거부 없음"은 기대에만, "예상 밖 거부"는 실제에만 있는 항목이다.
    /// </summary>
    public IReadOnlyList<string> RejectDiff(IEnumerable<(string RuleId, int? StatementIndex)> actual)
    {
        var left = actual.ToList();
        var diffs = new List<string>();
        foreach (var e in ExpectedRejects)
        {
            var i = left.FindIndex(a => a.StatementIndex == e.StatementIndex && e.Matches(a.RuleId));
            if (i < 0) diffs.Add($"기대한 거부 없음: {e.Display}");
            else left.RemoveAt(i);
        }
        diffs.AddRange(left.Select(a => $"예상 밖 거부: {a.RuleId}@문장 {a.StatementIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
        return diffs;
    }

    /// <summary>폴더의 모든 사례(id 서수 순).</summary>
    public static IReadOnlyList<HandCase> LoadAll(string casesDir) =>
        Directory.GetFiles(casesDir, "CL*.json").Select(Load).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

    static void ReadChanges(JsonElement changes, bool residual, Dictionary<string, IReadOnlyList<string>> keyCols, List<HandRow> rows)
    {
        foreach (var ch in changes.EnumerateArray())
        {
            var table = ch.GetProperty("table").GetString()!;
            keyCols[table] = Strings(ch, "keyColumns");
            foreach (var (prop, kind) in new[] { ("inserted", ChangeKinds.Inserted), ("updated", ChangeKinds.Updated), ("deleted", ChangeKinds.Deleted) })
            {
                if (!ch.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in arr.EnumerateArray())
                {
                    var match = Values(r, "match");
                    var key = r.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.Object
                        ? RowKeys.FromJsonObject(k)
                        : HandRow.UnboundPrefix + RowKeys.Canonical(match ?? throw new InvalidDataException($"{table}: 행에 key도 match도 없음"));
                    var refs = new Dictionary<string, HandIdentityRef>(StringComparer.OrdinalIgnoreCase);
                    if (r.TryGetProperty("identityRefs", out var ir) && ir.ValueKind == JsonValueKind.Object)
                        foreach (var p in ir.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
                            refs[p.Name] = new HandIdentityRef(p.Value.GetProperty("table").GetString()!, p.Value.GetProperty("ref").GetString()!);
                    rows.Add(new HandRow(table, kind, key, CausesOf(r), Strings(r, "changedColumns"),
                        Values(r, "after") ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
                    {
                        Match = match,
                        Identity = Strings(r, "identity"),
                        Ref = r.TryGetProperty("ref", out var rf) && rf.ValueKind == JsonValueKind.String ? rf.GetString() : null,
                        IdentityRefs = refs,
                        Volatile = Strings(r, "volatile"),
                        ResidualCommitted = residual,
                    });
                }
            }
            foreach (var (prop, kind) in new[] { ("multisetRemoved", ChangeKinds.MultisetRemoved), ("multisetAdded", ChangeKinds.MultisetAdded) })
            {
                if (!ch.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in arr.EnumerateArray())
                {
                    var row = Values(r, "row") ?? throw new InvalidDataException($"{table}: {prop} 행에 row 없음");
                    rows.Add(new HandRow(table, kind, RowKeys.Canonical(row), CausesOf(r), [],
                        kind == ChangeKinds.MultisetAdded ? row : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
                    {
                        Multiplicity = r.TryGetProperty("multiplicity", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 1,
                        ResidualCommitted = residual,
                    });
                }
            }
        }
    }

    static bool IsTrue(JsonElement o, string prop) => o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    static IReadOnlyList<string> CausesOf(JsonElement r) =>
        r.TryGetProperty("causes", out var cs) && cs.ValueKind == JsonValueKind.Array
            ? cs.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()! : "ONE_OF").Distinct().ToList()
            : [];

    static IReadOnlyList<string> Strings(JsonElement o, string prop) =>
        o.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    static Dictionary<string, string?>? Values(JsonElement r, string prop)
    {
        if (!r.TryGetProperty(prop, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in o.EnumerateObject()) d[p.Name] = RowKeys.JsonValueText(p.Value);
        return d;
    }

    /// <summary>
    /// v0.2 IDENTITY 행을 비교 대상(오라클 또는 제품 순 변경)의 실제 키에 묶는다. 대상마다 IDENTITY 값이 다르므로 비교할 때마다 따로 묶는다.
    /// <list type="bullet">
    /// <item>후보: 같은 테이블·변경 종류이고 after가 match를 모두 만족하며(열 이름 대소문자 무시, 값 서수 비교), identityRefs가 가리키는 부모가 이미 묶였으면 그 FK 값이 부모 IDENTITY 값과 같은 행.</item>
    /// <item>후보가 여럿이면 IDENTITY 값 오름차순으로, 손 정답 배열 순서대로 하나씩 가져간다(answer-format v0.2). 부모(identityRefs 없는 행)를 먼저 묶는다.</item>
    /// <item>못 묶은 행은 자리 표시 키로 남아 비교에서 빠짐이 된다. key 표기 행의 identityRefs가 어긋나도 Issues에 남긴다.</item>
    /// </list>
    /// </summary>
    public (HandCase Bound, IReadOnlyList<string> Issues) Bind(IReadOnlyList<OracleRowChange> target)
    {
        var issues = new List<string>();
        if (Rows.All(r => r.Match is null && r.IdentityRefs.Count == 0)) return (this, issues);
        var refValue = new Dictionary<string, string?>(StringComparer.Ordinal);
        var used = new HashSet<OracleRowChange>(ReferenceEqualityComparer.Instance);
        var result = Rows.ToArray();
        var labels = Rows.Where(r => r.Ref is not null).Select(r => r.Ref!).ToHashSet(StringComparer.Ordinal);

        bool RefsOk(HandRow h, IReadOnlyDictionary<string, string?>? after) =>
            h.IdentityRefs.All(p => !refValue.TryGetValue(p.Value.Ref, out var v)
                                    || (after is not null && TryGet(after, p.Key, out var a) && string.Equals(a, v, StringComparison.Ordinal)));

        var pending = Enumerable.Range(0, result.Length).Where(i => result[i].Match is not null).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(i => result[i].IdentityRefs.Values.All(r => refValue.ContainsKey(r.Ref) || !labels.Contains(r.Ref)) ).ToList();
            if (ready.Count == 0) ready = [.. pending]; // 부모를 못 묶었거나 순환: 확인 없이 진행
            foreach (var i in ready)
            {
                var h = result[i];
                var cand = target
                    .Where(t => !used.Contains(t) && string.Equals(t.Table, h.Table, StringComparison.OrdinalIgnoreCase) && t.Change == h.Kind && t.After is not null
                                && h.Match!.All(m => TryGet(t.After, m.Key, out var v) && string.Equals(v, m.Value, StringComparison.Ordinal))
                                && RefsOk(h, t.After))
                    .OrderBy(t => IdentityOrder(t, h.Identity)).ThenBy(t => t.Key, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (cand is null) { issues.Add($"IDENTITY 짝 없음: {h.Kind} {h.Display}"); continue; }
                used.Add(cand);
                result[i] = h with { Key = cand.Key };
                if (h.Ref is not null && h.Identity.Count > 0 && TryGet(cand.After!, h.Identity[0], out var idv)) refValue[h.Ref] = idv;
            }
            pending = pending.Except(ready).ToList();
        }
        foreach (var h in result.Where(r => r.Match is null && r.IdentityRefs.Count > 0))
        {
            var t = target.FirstOrDefault(x => string.Equals(x.Table, h.Table, StringComparison.OrdinalIgnoreCase) && x.Change == h.Kind && x.Key == h.Key);
            if (t is not null && !RefsOk(h, t.After)) issues.Add($"identityRefs 불일치: {h.Kind} {h.Display}");
        }
        return (this with { Rows = result }, issues);
    }

    static (int, decimal) IdentityOrder(OracleRowChange t, IReadOnlyList<string> identity) =>
        identity.Count > 0 && t.After is not null && TryGet(t.After, identity[0], out var v)
        && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (0, d) : (1, 0m);

    static bool TryGet(IReadOnlyDictionary<string, string?> d, string column, out string? value)
    {
        if (d.TryGetValue(column, out value)) return true;
        foreach (var (k, v) in d)
            if (string.Equals(k, column, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = null;
        return false;
    }

    /// <summary>
    /// 오라클 순 변경이 손 정답과 맞는지(oracle_vs_hand_keys_match). 손 정답은 changes ∪ residualCommitted(v0.2)이고, 먼저 오라클에 묶는다(Bind).
    /// 비교: 테이블별 (변경 종류, 키) 집합, UPDATED의 changedColumns 집합, 손 정답이 after에 적은 열의 값(INSERTED·UPDATED),
    /// volatile 열은 오라클 after에 NULL 아닌 값이 있는지, 키 없는 테이블(MULTISET_*)은 개수까지.
    /// 손 정답에 없는 테이블의 변경도 불일치다(answer-format.md "changes에 없는 테이블은 변경 0"). 차이 목록이 비면 일치.
    /// </summary>
    public IReadOnlyList<string> DiffAgainst(IReadOnlyList<OracleRowChange> oracle)
    {
        var (bound, bindIssues) = Bind(oracle);
        var diffs = new List<string>(bindIssues);
        var hand = new Dictionary<(string, string, string), HandRow>();
        foreach (var r in bound.Rows.Where(r => !r.IsUnbound)) hand.TryAdd((r.Table.ToUpperInvariant(), r.Kind, r.Key), r);
        var seen = new HashSet<(string, string, string)>();
        foreach (var o in oracle)
        {
            var id = (o.Table.ToUpperInvariant(), o.Change, o.Key);
            if (!hand.TryGetValue(id, out var h)) { diffs.Add($"오라클에만 있음: {o.Change} {o.Display}"); continue; }
            seen.Add(id);
            if (o.Change is ChangeKinds.MultisetAdded or ChangeKinds.MultisetRemoved)
            {
                if (o.Multiplicity != h.Multiplicity) diffs.Add($"개수 다름: {o.Change} {o.Display} 오라클 {o.Multiplicity} 손 {h.Multiplicity}");
                continue;
            }
            foreach (var col in h.Volatile)
                if (o.After is null || !TryGet(o.After, col, out var vv) || vv is null) diffs.Add($"volatile 열 값 없음: {o.Display}.{col}");
            if (o.Change == ChangeKinds.Deleted) continue;
            if (o.Change == ChangeKinds.Updated)
            {
                var oc = o.ChangedColumns.Select(c => c.ToUpperInvariant()).ToHashSet();
                if (!oc.SetEquals(h.ChangedColumns.Select(c => c.ToUpperInvariant())))
                    diffs.Add($"changedColumns 다름: {o.Display} 오라클 [{string.Join(",", o.ChangedColumns)}] 손 [{string.Join(",", h.ChangedColumns)}]");
            }
            foreach (var (col, val) in h.After)
            {
                if (o.After is null || !TryGet(o.After, col, out var ov)) { diffs.Add($"after 열 없음: {o.Display}.{col}"); continue; }
                if (!string.Equals(ov, val, StringComparison.Ordinal))
                    diffs.Add($"after 값 다름: {o.Display}.{col} 오라클 {ov ?? "NULL"} 손 {val ?? "NULL"}");
            }
        }
        foreach (var h in bound.Rows.Where(r => !r.IsUnbound && !seen.Contains((r.Table.ToUpperInvariant(), r.Kind, r.Key))))
            diffs.Add($"손 정답에만 있음: {h.Kind} {h.Display}");
        return diffs;
    }
}
