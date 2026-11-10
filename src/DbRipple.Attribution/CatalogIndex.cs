using System.Globalization;
using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.Attribution;

/// <summary>FK 한 개(다중 열은 Ordinal 순). Child = 참조하는 테이블, Parent = 참조되는 테이블.</summary>
internal sealed record FkInfo(
    string Name, string Child, string Parent,
    IReadOnlyList<string> ChildColumns, IReadOnlyList<string> ParentColumns,
    FkAction? OnDelete, FkAction? OnUpdate, bool IsDisabled);

/// <summary>
/// 원인 후보로 쓰는 트리거 한 개. Unknown = 쓰기 대상을 알 수 없음(암호화·CLR·파싱 실패·정적 분석 누락, 열린 질문 Q6).
/// Dynamic = 동적 SQL(모든 테이블에 쓸 수 있다고 본다, §7.2 4단계). C6 규약: 쓰기 대상 미상이면 WriteTargetsUnknown = true이고 HasDynamicSql = true다.
/// 저장 프로시저를 부르는 트리거(ProcedureCalls)도 Unknown으로 둔다: 프로시저 본문의 쓰기 대상을 펼치지 않으므로(3주차) 어떤 테이블에든 쓸 수 있다고 보고
/// FK 쓰기 경합으로 치며, 그 트리거의 WRITES 후보는 ambiguous 이하다(2026-11-10 통합 검토).
/// </summary>
internal sealed record TriggerInfo(
    string Name, string Table, IReadOnlySet<ChangeOperation> Events, bool IsInsteadOf,
    IReadOnlySet<string> WriteTargets, bool Dynamic, bool Unknown)
{
    public bool CanWrite(string table) => Dynamic || Unknown || WriteTargets.Contains(table);
}

/// <summary>카탈로그·트리거 분석·매니페스트·지문을 C13이 쓰는 모양으로 색인한 것. 감사·비활성 트리거와 비활성 FK는 뺀다(E22).</summary>
internal sealed class CatalogIndex
{
    public static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    readonly Dictionary<string, List<FkInfo>> _fksByChild = new(Names);
    readonly Dictionary<string, List<FkInfo>> _fksByParent = new(Names);
    readonly List<TriggerInfo> _triggers = [];
    readonly Dictionary<(string Table, string Column), Core.Catalog.CatalogColumn> _columns = new();
    readonly HashSet<string> _excluded = new(Names);
    readonly Dictionary<string, HashSet<string>> _unobservable = new(Names);

    public bool RecursiveTriggers { get; }
    public bool NestedTriggers { get; }
    public ValueComparer Values { get; }

    public CatalogIndex(BaselineContext b)
    {
        var cat = b.Catalog;
        var tableName = cat.Tables.ToDictionary(t => t.ObjectId, t => t.Schema + "." + t.Name);
        string? NameOf(int id) => tableName.TryGetValue(id, out var n) ? n : null;

        foreach (var c in cat.Columns)
            if (NameOf(c.ObjectId) is { } tn) _columns[(tn.ToUpperInvariant(), c.Name.ToUpperInvariant())] = c;

        foreach (var g in cat.ForeignKeys.GroupBy(f => f.ObjectId))
        {
            var cols = g.OrderBy(f => f.Ordinal).ToList();
            var first = cols[0];
            if (NameOf(first.ParentObjectId) is not { } child || NameOf(first.ReferencedObjectId) is not { } parent) continue;
            var fk = new FkInfo(first.Name, child, parent,
                cols.Select(c => c.ParentColumn).ToList(), cols.Select(c => c.ReferencedColumn).ToList(),
                ParseAction(first.DeleteAction), ParseAction(first.UpdateAction), first.IsDisabled);
            if (fk.IsDisabled) continue;
            Add(_fksByChild, child, fk);
            Add(_fksByParent, parent, fk);
        }

        // 트리거: 정적 분석(C6)이 기본, 카탈로그에만 있는 트리거는 쓰기 대상 미상으로 둔다(Q6).
        var seen = new HashSet<string>(Names);
        foreach (var t in b.Triggers)
        {
            seen.Add(t.Trigger);
            if (t.IsAuditTrigger || IsAuditName(t.Trigger) || t.IsDisabled) continue;
            _triggers.Add(new TriggerInfo(t.Trigger, t.Table, t.Events.ToHashSet(), t.IsInsteadOf,
                new HashSet<string>(t.WriteTargets, Names), t.HasDynamicSql, IsWriteTargetsUnknown(t) || t.ProcedureCalls.Count > 0));
        }
        foreach (var g in cat.Triggers.GroupBy(t => t.ObjectId))
        {
            var first = g.First();
            if (NameOf(first.ParentId) is not { } table) continue;
            var name = table[..table.IndexOf('.')] + "." + first.Name;
            if (seen.Contains(name) || IsAuditName(name) || first.IsDisabled) continue;
            var events = g.Select(e => ParseOp(e.Event)).OfType<ChangeOperation>().ToHashSet();
            _triggers.Add(new TriggerInfo(name, table, events, first.IsInsteadOf, new HashSet<string>(Names), true, true));
        }

        foreach (var e in b.Manifest.ExcludedTables) _excluded.Add(e.Table);
        foreach (var t in b.Manifest.Tables)
        {
            if (!t.Covered) _excluded.Add(t.Table);
            if (t.UnobservableColumns.Count > 0) _unobservable[t.Table] = new HashSet<string>(t.UnobservableColumns, Names);
        }

        RecursiveTriggers = b.Fingerprint.IsRecursiveTriggersOn;
        NestedTriggers = b.Fingerprint.NestedTriggers != 0;
        var cs = b.Fingerprint.Collation?.Contains("_CS", StringComparison.OrdinalIgnoreCase) == true
              || b.Fingerprint.Collation?.EndsWith("_BIN", StringComparison.OrdinalIgnoreCase) == true
              || b.Fingerprint.Collation?.EndsWith("_BIN2", StringComparison.OrdinalIgnoreCase) == true;
        Values = new ValueComparer(caseSensitive: cs);
    }

    public IReadOnlyList<FkInfo> FksByChild(string table) => _fksByChild.TryGetValue(table, out var l) ? l : [];
    public IReadOnlyList<FkInfo> FksByParent(string table) => _fksByParent.TryGetValue(table, out var l) ? l : [];

    /// <summary>테이블·연산에서 발동하는 활성 사용자 AFTER 트리거(감사 제외), 이름순.</summary>
    public IReadOnlyList<TriggerInfo> AfterTriggers(string table, ChangeOperation op) =>
        _triggers.Where(t => !t.IsInsteadOf && Names.Equals(t.Table, table) && t.Events.Contains(op))
                 .OrderBy(t => t.Name, Names).ToList();

    /// <summary>테이블·연산의 활성 INSTEAD OF 트리거(테이블·연산마다 최대 1개).</summary>
    public TriggerInfo? InsteadOf(string table, ChangeOperation op) =>
        _triggers.FirstOrDefault(t => t.IsInsteadOf && Names.Equals(t.Table, table) && t.Events.Contains(op));

    public TriggerInfo? Trigger(string name) => _triggers.FirstOrDefault(t => Names.Equals(t.Name, name));

    public Core.Catalog.CatalogColumn? Column(string table, string column) =>
        _columns.TryGetValue((table.ToUpperInvariant(), column.ToUpperInvariant()), out var c) ? c : null;

    public bool IsExcluded(string table) => _excluded.Contains(table);

    public bool IsUnobservable(string table, string column) => _unobservable.TryGetValue(table, out var s) && s.Contains(column);

    /// <summary>
    /// 쓰기 대상을 알 수 없는 트리거(Q6: 그 트리거가 낸 후보는 ambiguous 이하). 판정은 C6이 채운 계약 속성 TriggerStaticInfo.WriteTargetsUnknown이다
    /// (암호화·CLR 또는 파싱 실패, 2026-11-10 통합).
    /// </summary>
    public static bool IsWriteTargetsUnknown(TriggerStaticInfo t) => t.WriteTargetsUnknown;

    public static bool IsAuditName(string name)
    {
        var last = name[(name.LastIndexOf('.') + 1)..].Trim('[', ']');
        return last.StartsWith(AuditTriggerGenerator.TriggerPrefix, StringComparison.OrdinalIgnoreCase);
    }

    static FkAction? ParseAction(string? s) => s?.ToUpperInvariant() switch
    {
        "CASCADE" => FkAction.Cascade,
        "SET_NULL" => FkAction.SetNull,
        "SET_DEFAULT" => FkAction.SetDefault,
        _ => null,
    };

    static ChangeOperation? ParseOp(string ev) => ev.ToUpperInvariant() switch
    {
        "INSERT" => ChangeOperation.Insert,
        "UPDATE" => ChangeOperation.Update,
        "DELETE" => ChangeOperation.Delete,
        _ => null,
    };

    static void Add(Dictionary<string, List<FkInfo>> d, string k, FkInfo v)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = [];
        l.Add(v);
    }

    /// <summary>
    /// 열 기본값 정의를 §6.1 비교용 값으로 바꾼다. "((0))" → "0", "('x')"·"(N'x')" → "x", "(NULL)" 또는 기본값 없음 → null.
    /// 함수 호출 등 상수가 아니면 Parsed = false.
    /// </summary>
    public static (bool Parsed, string? Value) ParseDefault(string? definition)
    {
        if (definition is null) return (true, null);
        var s = definition.Trim();
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')') s = s[1..^1].Trim();
        if (s.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return (true, null);
        if (s.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return (true, s[1..^1].Replace("''", "'"));
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return (true, s);
        return (false, null);
    }
}

/// <summary>§6.1 문자열 값 비교. 정렬 규칙이 대소문자 무시면 대소문자를 무시하고, 끝 공백은 무시한다(SQL Server 문자열 비교). 숫자는 값으로 비교한다.</summary>
internal sealed class ValueComparer(bool caseSensitive)
{
    const string NullToken = "\u0000NULL";

    public string Normalize(string? v)
    {
        if (v is null) return NullToken;
        if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return "#" + d.ToString("G29", CultureInfo.InvariantCulture);
        var t = v.TrimEnd(' ');
        return caseSensitive ? t : t.ToUpperInvariant();
    }

    public bool AreEqual(string? a, string? b) => Normalize(a) == Normalize(b);

    /// <summary>여러 열 값을 한 비교 키로 묶는다. 하나라도 null이면 null(FK가 걸리지 않는 값).</summary>
    public string? Tuple(IReadOnlyList<string?> values)
    {
        if (values.Any(v => v is null)) return null;
        return string.Join('\u001F', values.Select(Normalize));
    }

    /// <summary>NULL을 포함해도 되는 키(행 식별용).</summary>
    public string KeyTuple(IReadOnlyList<string?> values) => string.Join('\u001F', values.Select(Normalize));
}
