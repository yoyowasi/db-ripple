using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbRipple.Eval;

/// <summary>
/// 인계 6(오라클 쪽, Q35): 키 동등 비교를 DB 정렬 규칙에 맞춘다. 제품은 대소문자만 바꾸는 키 UPDATE를 정렬 규칙대로 UPDATED로 본다.
/// 하네스 규칙(근사, 제품 코드와 공유하지 않음):
/// <list type="bullet">
/// <item>정렬 규칙이 있는 열(문자 열)의 키 값은 오른쪽 공백을 뗀다(SQL Server의 = 비교는 정렬 규칙과 무관하게 뒤 공백을 무시한다).</item>
/// <item>정렬 규칙 이름에 "_CI"가 있으면 ToUpperInvariant, "_AI"가 있으면 결합 문자(악센트)를 뗀다. 폭·가나 구분(_WS·_KS)은 다루지 않는다.</item>
/// <item>접힌 값은 키 문자열에만 쓴다. 셀 값(before·after)은 원래 값 그대로 비교한다.</item>
/// </list>
/// 정규 키가 접히므로 같은 규칙을 오라클·훅(비교기 안)과 제품 결과·재구성·손 정답(CaseScorer 안)에 똑같이 적용해야 한다. 적용은 멱등이다.
/// </summary>
public sealed class KeyFolding
{
    readonly Dictionary<string, Dictionary<string, OracleColumn>> foldable;

    KeyFolding(Dictionary<string, Dictionary<string, OracleColumn>> foldable) => this.foldable = foldable;

    /// <summary>접을 일이 없는 규칙(테이블 정보 없음).</summary>
    public static KeyFolding None { get; } = new([]);

    /// <summary>테이블 목록에서 만든다. 키 있는 테이블의 문자 키 열만 접는다.</summary>
    public static KeyFolding For(IEnumerable<OracleTable> tables)
    {
        var d = new Dictionary<string, Dictionary<string, OracleColumn>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables.Where(t => t.KeyKind != OracleKeyKind.ROW))
        {
            var cols = t.KeyColumns
                .Select(k => t.Columns.FirstOrDefault(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase)))
                .Where(c => c?.Collation is not null)
                .ToDictionary(c => c!.Name.ToUpperInvariant(), c => c!, StringComparer.Ordinal);
            if (cols.Count > 0) d[t.Display] = cols;
        }
        return new KeyFolding(d);
    }

    /// <summary>접을 키 열이 하나라도 있는지.</summary>
    public bool IsEmpty => foldable.Count == 0;

    /// <summary>정렬 규칙에 맞춘 값(키 동등 비교용).</summary>
    public static string? Fold(string? collation, string? value)
    {
        if (collation is null || value is null) return value;
        var v = value.TrimEnd(' ');
        if (collation.Contains("_CI", StringComparison.OrdinalIgnoreCase)) v = v.ToUpperInvariant();
        if (collation.Contains("_AI", StringComparison.OrdinalIgnoreCase)) v = StripMarks(v);
        return v;
    }

    /// <summary>정규 키 하나를 접는다(그 테이블에 접을 열이 없으면 그대로).</summary>
    public string Key(string table, string canonicalKey)
    {
        if (!foldable.TryGetValue(table, out var cols)) return canonicalKey;
        List<List<string?>>? pairs;
        try { pairs = JsonSerializer.Deserialize<List<List<string?>>>(canonicalKey); }
        catch (JsonException) { return canonicalKey; }
        if (pairs is null) return canonicalKey;
        return RowKeys.Canonical(pairs.Where(p => p.Count == 2 && p[0] is not null).Select(p =>
            new KeyValuePair<string, string?>(p[0]!, cols.TryGetValue(p[0]!, out var c) ? Fold(c.Collation, p[1]) : p[1])));
    }

    /// <summary>순 변경의 키를 접는다.</summary>
    public OracleRowChange Apply(OracleRowChange r) => IsEmpty ? r : r with { Key = Key(r.Table, r.Key) };

    /// <summary>순 변경 목록의 키를 접는다.</summary>
    public IReadOnlyList<OracleRowChange> Apply(IReadOnlyList<OracleRowChange> rows) => IsEmpty ? rows : rows.Select(Apply).ToList();

    /// <summary>예측 순 변경의 키를 접는다.</summary>
    public IReadOnlyList<PredictedChange> Apply(IReadOnlyList<PredictedChange> rows) =>
        IsEmpty ? rows : rows.Select(p => p with { Row = Apply(p.Row) }).ToList();

    /// <summary>손 정답의 키를 접는다.</summary>
    public HandCase Apply(HandCase hand) =>
        IsEmpty ? hand : hand with { Rows = hand.Rows.Select(r => r with { Key = Key(r.Table, r.Key) }).ToList() };

    static string StripMarks(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>
/// 인계 2: 키 없는 테이블(ROW)의 다중집합 키에 쓸 열 규칙을 평가 훅과 E_cap 재구성에서 같게 한다.
/// 하네스 비교기는 모든 열(계산 열 제외)을 읽지만, 제품 행 이미지에는 관측 불가 열(§6.1: text·ntext·image, 이름에 '.'이 있는 열,
/// 다른 열의 생성 속성 "열@srid"·"열@type"과 이름이 같은 열)이 없다. 그래서 cap 비교 전에 양쪽을 "관측 가능 열"만으로 다시 키를 만들고 개수를 합친다.
/// 계산 열은 비교기(Items)와 같이 뺀다. 생성 속성(열@srid·열@type)은 원래 열이 관측 가능하면 남긴다.
/// </summary>
public static class HeapKeys
{
    static readonly HashSet<string> UnobservableTypes = new(["text", "ntext", "image"], StringComparer.OrdinalIgnoreCase);

    /// <summary>ROW 키에 쓰는 열(대소문자 무시 집합).</summary>
    public static IReadOnlySet<string> ObservableColumns(OracleTable t)
    {
        var generated = t.Columns
            .SelectMany(c => c.TypeName is "geography" or "geometry" ? [c.Name + "@srid"] : c.TypeName == "sql_variant" ? [c.Name + "@type"] : Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return t.Columns
            .Where(c => !c.IsComputed && !UnobservableTypes.Contains(c.TypeName) && !c.Name.Contains('.') && !generated.Contains(c.Name))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ROW 테이블의 MULTISET_* 변경을 관측 가능 열만으로 다시 묶는다(늘어난 개수는 +, 줄어든 개수는 −로 합쳐 0이면 버린다). 다른 테이블은 그대로.
    /// 테이블 정보가 없는 변경도 그대로 둔다.
    /// </summary>
    public static IReadOnlyList<OracleRowChange> Rekey(IReadOnlyList<OracleRowChange> rows, IEnumerable<OracleTable> tables)
    {
        var heap = tables.Where(t => t.KeyKind == OracleKeyKind.ROW).ToDictionary(t => t.Display, ObservableColumns, StringComparer.OrdinalIgnoreCase);
        if (heap.Count == 0) return rows;
        var result = new List<OracleRowChange>();
        var sums = new Dictionary<(string Table, string Key), (long Delta, IReadOnlyDictionary<string, string?> Values)>();
        foreach (var r in rows)
        {
            if (!heap.TryGetValue(r.Table, out var cols) || r.Change is not (ChangeKinds.MultisetAdded or ChangeKinds.MultisetRemoved))
            {
                result.Add(r);
                continue;
            }
            var src = r.After ?? r.Before ?? new Dictionary<string, string?>();
            var values = src.Where(kv => cols.Contains(BaseColumn(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var key = (r.Table, RowKeys.Canonical(values));
            var delta = r.Change == ChangeKinds.MultisetAdded ? r.Multiplicity : -r.Multiplicity;
            sums[key] = sums.TryGetValue(key, out var cur) ? (cur.Delta + delta, cur.Values) : (delta, values);
        }
        foreach (var ((table, key), (delta, values)) in sums)
        {
            if (delta > 0) result.Add(new OracleRowChange(table, key, ChangeKinds.MultisetAdded, delta, null, values, []));
            else if (delta < 0) result.Add(new OracleRowChange(table, key, ChangeKinds.MultisetRemoved, -delta, values, null, []));
        }
        return result.OrderBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Change, StringComparer.Ordinal).ToList();
    }

    static string BaseColumn(string c) => c.IndexOf('@') is var i and > 0 ? c[..i] : c;
}
