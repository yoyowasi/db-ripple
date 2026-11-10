using System.Text;
using System.Text.Json;

namespace DbRipple.Eval;

// 하네스 자체 모델. 제품 NetChange와 일부러 다른 형식을 쓴다(§10.3 독립성: 비교 결과를 하네스 쪽 형식으로 정규화한 뒤 지표를 잰다).

/// <summary>하네스 키 종류(PK → 필터 없는 NOT NULL 유일 인덱스 → ROW). 제품 규칙(Core TableKey.Choose)과 같은 규칙을 하네스가 sys.* 카탈로그로 따로 계산한다.</summary>
public enum OracleKeyKind
{
    /// <summary>기본 키.</summary>
    PK,
    /// <summary>필터 없는 활성 유일 인덱스이고 키 열이 모두 NOT NULL.</summary>
    UNIQUE,
    /// <summary>키 없음(다중집합).</summary>
    ROW,
}

/// <summary>하네스가 sys.columns로 읽은 열 정보.</summary>
public sealed record OracleColumn(
    string Name,
    int ColumnId,
    string TypeName,
    bool IsIdentity,
    bool IsRowVersion,
    bool IsComputed,
    bool IsUserClrType,
    bool HasNondeterministicDefault);

/// <summary>비교 대상 테이블(하네스가 sys.* 카탈로그를 직접 읽어 만든다. 제품 C7을 쓰지 않는다).</summary>
public sealed record OracleTable(string Schema, string Name, OracleKeyKind KeyKind, IReadOnlyList<string> KeyColumns, IReadOnlyList<OracleColumn> Columns)
{
    /// <summary>"스키마.테이블".</summary>
    public string Display => Schema + "." + Name;

    /// <summary>비결정 열(E1·E2·E4: IDENTITY, rowversion, 비결정 기본값). M1에서 존재·형식만 센다.</summary>
    public IEnumerable<string> VolatileColumns =>
        Columns.Where(c => c.IsIdentity || c.IsRowVersion || c.HasNondeterministicDefault).Select(c => c.Name);
}

/// <summary>순 변경 종류(하네스 문자열). 값은 설계 §6.2 NetChange.change와 같은 철자다.</summary>
public static class ChangeKinds
{
    /// <summary>새 키.</summary>
    public const string Inserted = "INSERTED";
    /// <summary>사라진 키.</summary>
    public const string Deleted = "DELETED";
    /// <summary>같은 키, 바뀐 열 있음.</summary>
    public const string Updated = "UPDATED";
    /// <summary>키 없는 테이블에서 줄어든 행.</summary>
    public const string MultisetRemoved = "MULTISET_REMOVED";
    /// <summary>키 없는 테이블에서 늘어난 행.</summary>
    public const string MultisetAdded = "MULTISET_ADDED";
}

/// <summary>
/// 셀 사실 하나(M1 단위 = (테이블, 키, 열), §10.4). Value는 하네스 정규화 문자열.
/// INSERTED는 모든 열, UPDATED는 바뀐 열, DELETED는 열 이름 "*"와 값 "DELETED" 한 개로 만든다(docs/week2-plan.md §5 M1 셀 정의).
/// </summary>
public sealed record CellFact(string Table, string Key, string Column, string? Value, bool Volatile);

/// <summary>
/// 행 단위 순 변경(하네스 형식). Key는 RowKeys.Canonical(키 열, 값). 키 없는 테이블은 행 전체 값으로 만든 키(같은 값 행은 같은 키, Multiplicity로 개수).
/// </summary>
public sealed record OracleRowChange(string Table, string Key, string Change, long Multiplicity,
    IReadOnlyDictionary<string, string?>? Before, IReadOnlyDictionary<string, string?>? After, IReadOnlyList<string> ChangedColumns)
{
    /// <summary>보고용 키 표시 "테이블(열=값, …)".</summary>
    public string Display => Table + RowKeys.Display(Key);
}

/// <summary>오라클 문장 1개(또는 원문 배치 1개, Index = 0) 실행 결과.</summary>
public sealed record OracleStatement(int Index, string Status, long? RowsAffected, IReadOnlyList<int> ErrorNumbers);

/// <summary>오라클 실행 결과(§10.3 2차 정답).</summary>
public sealed record OracleResult(
    string Database,
    IReadOnlyList<OracleTable> Tables,
    IReadOnlyList<OracleRowChange> Changes,
    IReadOnlyList<OracleStatement> Statements,
    IReadOnlyList<int> ErrorNumbers,
    bool RawBatchPath,
    long ExecuteMs,
    string BeforeDigest,
    bool RestoredAfterRollback,
    IReadOnlyList<string> ComparatorIssues)
{
    /// <summary>ok, 또는 처음 실패한 문장의 상태(failed, trigger_blocked).</summary>
    public string Status => Statements.FirstOrDefault(s => s.Status is not ("ok" or "not_run"))?.Status ?? "ok";
}

/// <summary>
/// 하네스 행 키 형식. 열 이름은 대소문자 무시(SQL Server 식별자 규칙), 값은 §6.1 문자열 그대로 비교한다.
/// 정규형: 열 이름(대문자)으로 정렬한 [열, 값] 쌍의 JSON 배열. 손 정답·오라클 JSON·제품 RowKey가 모두 이 형식으로 바뀌어 비교된다.
/// </summary>
public static class RowKeys
{
    /// <summary>정규 키 문자열.</summary>
    public static string Canonical(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var sorted = pairs.Select(p => new[] { p.Key.ToUpperInvariant(), p.Value })
            .OrderBy(p => p[0], StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(sorted);
    }

    /// <summary>키 열 이름과 값 목록(같은 순서)으로 정규 키.</summary>
    public static string Canonical(IReadOnlyList<string> columns, IReadOnlyList<string?> values)
    {
        if (columns.Count != values.Count) throw new ArgumentException("키 열과 값의 개수가 다르다");
        return Canonical(columns.Select((c, i) => new KeyValuePair<string, string?>(c, values[i])));
    }

    /// <summary>JSON 객체 키({"id": 10})를 정규 키로. 숫자는 원문, 문자열은 그 값, null은 null.</summary>
    public static string FromJsonObject(JsonElement obj) =>
        Canonical(obj.EnumerateObject().Select(p => new KeyValuePair<string, string?>(p.Name, JsonValueText(p.Value))));

    /// <summary>JSON 값을 §6.1 문자열로(숫자는 원문 그대로, true/false는 소문자 문자열).</summary>
    public static string? JsonValueText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };

    /// <summary>정규 키를 "(열=값, …)"로 표시한다.</summary>
    public static string Display(string canonical)
    {
        try
        {
            var pairs = JsonSerializer.Deserialize<List<List<string?>>>(canonical) ?? [];
            var sb = new StringBuilder("(");
            for (var i = 0; i < pairs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pairs[i][0]).Append('=').Append(pairs[i][1] ?? "NULL");
            }
            return sb.Append(')').ToString();
        }
        catch (JsonException)
        {
            return "(" + canonical + ")";
        }
    }
}
