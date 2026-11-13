using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DbRipple.Eval;

/// <summary>FK 메타데이터 한 줄(sys.foreign_keys). 테이블 이름은 "스키마.테이블", 동작은 *_referential_action_desc 값(NO_ACTION, CASCADE, SET_NULL, SET_DEFAULT).</summary>
public sealed record ForeignKeyMeta(string Name, string ChildTable, string ParentTable, string DeleteAction, string UpdateAction, bool IsDisabled);

/// <summary>B1 정적 예측의 시작점: 문장 번호(1부터)와 직접 대상 테이블·연산(INSERT, UPDATE, DELETE).</summary>
public sealed record B1Seed(int StatementIndex, string Table, string Operation);

/// <summary>B1이 닿은 변경 묶음 하나(문장, 테이블, 연산). 시작점도 포함한다.</summary>
public sealed record B1Node(int StatementIndex, string Table, string Operation)
{
    /// <summary>간선 노드 표기 "F:테이블:연산"(#k 없음).</summary>
    public string Label => "F:" + Table + ":" + Operation;
}

/// <summary>B1 예측: 닿은 묶음과 FK 간선(DIRECT 간선은 포함하지 않는다. B0Edges가 낸다).</summary>
public sealed record B1Prediction(IReadOnlyList<B1Node> Nodes, IReadOnlyList<PredictedEdge> Edges)
{
    /// <summary>닿은 테이블(대소문자 무시).</summary>
    public IReadOnlySet<string> Tables => Nodes.Select(n => n.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 대조군 B1(FK 메타데이터 정적 예측, 설계 §10.5). 제품 분석기가 아니라 DB의 FK 목록(sys.foreign_keys)만 쓴다. 열 정보와 트리거는 보지 않는다.
/// <list type="bullet">
/// <item>시작: 문장의 직접 대상과 연산. 연산은 문장 첫 단어(INSERT·UPDATE·DELETE, MERGE는 WHEN 절의 THEN 뒤 연산)로 정한다.
/// 별칭 UPDATE·DELETE TOP처럼 정규식이 대상을 못 잡는 문장은 손 정답의 그 문장 DIRECT 간선 자식 노드(F:테이블:연산)를 쓴다(B0이 손 정답 DIRECT를 쓰는 것과 같은 근사).
/// EXEC 문장은 손 정답 PROC_DIRECT 행의 테이블을 직접 대상으로 보고, 연산은 그 행의 변경 종류(INSERTED·MULTISET_ADDED는 INSERT, UPDATED는 UPDATE, DELETED·MULTISET_REMOVED는 DELETE)로 정한다.</item>
/// <item>확장(고정점): 활성(is_disabled = 0) FK의 참조 동작만 따른다. 부모 DELETE + CASCADE → 자식 DELETE, 부모 DELETE + SET NULL·SET DEFAULT → 자식 UPDATE,
/// 부모 UPDATE + ON UPDATE CASCADE·SET NULL·SET DEFAULT → 자식 UPDATE. NO ACTION은 넓히지 않고 INSERT도 넓히지 않는다. (문장, 테이블, 연산) 상태를 한 번만 처리하므로 순환 FK도 끝난다.</item>
/// <item>예측 간선: FK마다 부모 묶음 → 자식 묶음, kind FK, name = FK 이름, confidence certain. 같은 (테이블, 연산)은 한 노드이고 #k를 붙이지 않는다(행·인과 순서를 모르는 정적 예측이다).
/// 그래서 손 정답이 같은 묶음을 #1·#2로 나눈 사례(CL09 등)는 키가 어긋난다.</item>
/// <item>기대 약점: 트리거가 만든 변경과 그 뒤의 FK 연쇄는 놓친다. 열을 안 보므로 키 열이 아닌 UPDATE에도 ON UPDATE CASCADE를 따라가고, 프로시저가 FK 자식 행을 먼저 직접 지웠어도 FK 간선을 낸다(CL22).</item>
/// </list>
/// </summary>
public static class FkStaticBaseline
{
    static readonly Regex Verb = new(@"^\s*(?<v>INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Exec = new(@"^\s*EXEC(?:UTE)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex MergeAction = new(@"\bTHEN\s+(?<a>INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>손 정답에서 문장별 시작점을 만든다(문장 번호 = 목록 위치 + 1, HandCase.Load가 index 순으로 정렬한다).</summary>
    public static IReadOnlyList<B1Seed> Seeds(HandCase hand)
    {
        var seeds = new List<B1Seed>();
        var procOps = hand.Rows.Where(r => r.Causes.Contains("PROC_DIRECT"))
            .Select(r => (r.Table, Op: OperationOf(r.Kind))).Where(x => x.Op is not null).Distinct().ToList();
        for (var i = 0; i < hand.Statements.Count; i++)
        {
            var index = i + 1;
            var sql = hand.Statements[i];
            if (Exec.IsMatch(sql))
            {
                seeds.AddRange(procOps.Select(x => new B1Seed(index, x.Table, x.Op!)));
                continue;
            }
            var target = Baselines.Target.Match(sql);
            var verb = Verb.Match(sql);
            if (target.Success && verb.Success)
            {
                var parts = target.Groups["t"].Value.Split('.').Select(p => p.Trim().Trim('[', ']')).ToList();
                var table = parts[^2] + "." + parts[^1];
                var v = verb.Groups["v"].Value.ToUpperInvariant();
                var ops = v == "MERGE" ? MergeAction.Matches(sql).Select(m => m.Groups["a"].Value.ToUpperInvariant()).Distinct().ToList() : [v];
                seeds.AddRange(ops.Select(op => new B1Seed(index, table, op)));
                continue;
            }
            foreach (var e in hand.Edges.Where(e => e.StatementIndex == index && e.Kind == "DIRECT"))
                if (ParseNode(e.Child) is { } n) seeds.Add(new B1Seed(index, n.Table, n.Op));
        }
        return seeds.Distinct().ToList();
    }

    /// <summary>"F:스키마.테이블:연산#k" → (테이블, 연산). 묶음 노드가 아니면 null.</summary>
    static (string Table, string Op)? ParseNode(string label)
    {
        var b = NodeRefs.Split(label).Base;
        if (!b.StartsWith("F:", StringComparison.Ordinal)) return null;
        var c = b.LastIndexOf(':');
        return c > 2 ? (b[2..c], b[(c + 1)..].ToUpperInvariant()) : null;
    }

    static string? OperationOf(string kind) => kind switch
    {
        ChangeKinds.Inserted or ChangeKinds.MultisetAdded => "INSERT",
        ChangeKinds.Updated => "UPDATE",
        ChangeKinds.Deleted or ChangeKinds.MultisetRemoved => "DELETE",
        _ => null,
    };

    static string Action(string? desc) => (desc ?? "").Trim().ToUpperInvariant().Replace(' ', '_');

    /// <summary>부모 묶음의 연산과 FK 동작 → 자식 묶음의 연산(넓히지 않으면 null).</summary>
    public static string? ChildOperation(string parentOperation, ForeignKeyMeta fk) => parentOperation switch
    {
        "DELETE" => Action(fk.DeleteAction) switch { "CASCADE" => "DELETE", "SET_NULL" or "SET_DEFAULT" => "UPDATE", _ => null },
        "UPDATE" => Action(fk.UpdateAction) is "CASCADE" or "SET_NULL" or "SET_DEFAULT" ? "UPDATE" : null,
        _ => null,
    };

    /// <summary>순수 함수: 시작점과 FK 목록에서 닿는 묶음과 FK 간선을 고정점까지 구한다.</summary>
    public static B1Prediction Predict(IEnumerable<B1Seed> seeds, IReadOnlyList<ForeignKeyMeta> foreignKeys)
    {
        var byParent = foreignKeys.Where(f => !f.IsDisabled).ToLookup(f => f.ParentTable, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(int, string, string)>();
        var nodes = new List<B1Node>();
        var edges = new List<PredictedEdge>();
        var queue = new Queue<B1Node>();
        bool Add(B1Node n)
        {
            if (!seen.Add((n.StatementIndex, n.Table.ToUpperInvariant(), n.Operation))) return false;
            nodes.Add(n);
            queue.Enqueue(n);
            return true;
        }
        foreach (var s in seeds) Add(new B1Node(s.StatementIndex, s.Table, s.Operation.ToUpperInvariant()));
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var fk in byParent[parent.Table])
            {
                if (ChildOperation(parent.Operation, fk) is not { } op) continue;
                var child = new B1Node(parent.StatementIndex, fk.ChildTable, op);
                edges.Add(new PredictedEdge(parent.StatementIndex, parent.Label, child.Label, "FK", fk.Name, "certain", "guaranteed", null));
                Add(child);
            }
        }
        return new B1Prediction(nodes, edges);
    }

    /// <summary>B1 예측 변경: 오라클 순 변경을 (직접 대상 ∪ FK로 닿은 테이블)로 거른 것.</summary>
    public static IReadOnlyList<OracleRowChange> Changes(HandCase hand, IReadOnlyList<OracleRowChange> oracle, B1Prediction prediction)
    {
        var scope = Scope(hand, prediction);
        return oracle.Where(o => scope.Contains(o.Table)).ToList();
    }

    /// <summary>B1 예측 간선: B0의 DIRECT 간선 + FK 간선.</summary>
    public static IReadOnlyList<PredictedEdge> Edges(HandCase hand, B1Prediction prediction) =>
        [.. Baselines.B0Edges(hand), .. prediction.Edges];

    /// <summary>작업량: 예측 범위(직접 대상 ∪ FK로 닿은 테이블) 밖인데 오라클에서 바뀐 테이블 수. B0·B2의 "직접 대상 밖"과 달리 B1이 알려 주는 FK 연쇄 테이블은 뺀다.</summary>
    public static long ManualSelectTables(HandCase hand, IReadOnlyList<OracleRowChange> oracle, B1Prediction prediction)
    {
        var scope = Scope(hand, prediction);
        return oracle.Select(o => o.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count(t => !scope.Contains(t));
    }

    static HashSet<string> Scope(HandCase hand, B1Prediction prediction)
    {
        var scope = new HashSet<string>(Baselines.DirectTargets(hand), StringComparer.OrdinalIgnoreCase);
        scope.UnionWith(prediction.Tables);
        return scope;
    }

    const string ForeignKeySql = """
        SELECT fk.name,
               SCHEMA_NAME(c.schema_id) + N'.' + c.name AS child_table,
               SCHEMA_NAME(p.schema_id) + N'.' + p.name AS parent_table,
               fk.delete_referential_action_desc,
               fk.update_referential_action_desc,
               fk.is_disabled
        FROM sys.foreign_keys AS fk
        JOIN sys.objects AS c ON c.object_id = fk.parent_object_id
        JOIN sys.objects AS p ON p.object_id = fk.referenced_object_id
        ORDER BY fk.name;
        """;

    /// <summary>
    /// DB 하나의 FK 목록을 읽는다(읽기 전용 SELECT 하나). connectionString은 서버 연결 문자열(값을 로그·예외에 넣지 않는다). 연결은 풀링을 끄고 database로 연다.
    /// 호출하는 쪽이 DB별로 한 번만 읽어 캐시한다(사례 사이에 FK는 바뀌지 않는다).
    /// </summary>
    public static async Task<IReadOnlyList<ForeignKeyMeta>> ReadAsync(string connectionString, string database, CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(connectionString) { Pooling = false, InitialCatalog = database };
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ForeignKeySql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ForeignKeyMeta>();
        while (await reader.ReadAsync(ct))
            list.Add(new ForeignKeyMeta(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
        return list;
    }
}
