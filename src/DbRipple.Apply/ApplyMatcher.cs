using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.Apply;

/// <summary>D7 일치 규칙 입력(DB 없이 판정하는 순수 단계).</summary>
public sealed record MatchInput
{
    /// <summary>미리보기 한 건(Result.Statements, Result.NetSummary, NetChanges, RowChanges의 volatile·derived).</summary>
    public required PreviewRecord Preview { get; init; }
    /// <summary>원본 실행 문장 결과(StatementRunOutcome.Result).</summary>
    public required IReadOnlyList<StatementResult> Actual { get; init; }
    /// <summary>원본 최종 차이(원본 대 원본 스냅샷, 전체 사용자 테이블, ExplainedBy = null).</summary>
    public required IReadOnlyList<NetChange> ActualNet { get; init; }
    /// <summary>기준선 문맥(FK 메타데이터: IDENTITY 짝 매핑의 자식 FK 치환, 매니페스트 volatileColumns·unobservableColumns).</summary>
    public required BaselineContext Baseline { get; init; }
}

/// <summary>D7 일치 규칙 결과(ApplyVerification의 일부).</summary>
public sealed record MatchOutcome
{
    /// <summary>문장별 행 수 대조(D7 1).</summary>
    public IReadOnlyList<StatementRowCount> StatementRowCounts { get; init; } = [];
    /// <summary>netSummary 일치(D7 2).</summary>
    public required bool NetSummaryMatch { get; init; }
    /// <summary>원본 실제 netSummary(ContractFormulas.Summarize).</summary>
    public IReadOnlyList<NetSummaryRow> ActualNetSummary { get; init; } = [];
    /// <summary>위반.</summary>
    public IReadOnlyList<ApplyMismatch> Mismatches { get; init; } = [];
    /// <summary>값 비교에서 뺀 비결정 열 "스키마.테이블.열".</summary>
    public IReadOnlyList<string> VolatileIgnored { get; init; } = [];
    /// <summary>새 IDENTITY 행 짝.</summary>
    public IReadOnlyList<IdentityPair> IdentityPairs { get; init; } = [];
    /// <summary>판정이 기댄 가정(verification.limitations에 더한다). 예: 같은 값 IDENTITY 행을 IDENTITY 순서로 짝지음.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>
/// D7 일치 규칙(§8 D7, E1·E2·E4). 규칙(D7 목록 순서):
/// <list type="number">
/// <item>문장별 직접 대상 행 수(StatementResult.RowsAffected)가 같다.</item>
/// <item>테이블별 netSummary 다섯 건수가 같다(발동 단위가 아니라 최종 차이로 비교, 원본에는 감사 트리거가 없다).</item>
/// <item>PK·UNIQUE 키 행은 비결정 열(RowChange.volatile + 매니페스트 volatileColumns)을 뺀 값이 모두 같다. 계산 열(derived)은 식이 결정적이므로 비교에 넣는다(E4).
///   다만 새 IDENTITY 값이나 그 짝 매핑으로 치환한 FK 값에 기대는 계산 열(예: AW SalesOrderNumber = 'SO' + SalesOrderID)은 그 값만 짝 값으로 바꿔 비교한다.</item>
/// <item>새 IDENTITY 행은 비결정 열을 뺀 값으로 짝을 짓고, 그 IDENTITY를 참조하는 자식 FK 값은 짝 매핑으로 치환해 비교한다.
///   같은 서명 행이 미리보기·원본에 같은 개수로 있으면 각 쪽을 IDENTITY 오름차순으로 정렬해 순서대로 짝짓는다(같은 문장이 같은 순서로 IDENTITY를 매긴다는 가정, Limitations에 남김).
///   개수가 다르거나 IDENTITY 값으로 순서를 정할 수 없으면 IDENTITY_UNPAIRED. 순서가 어긋나 자식이 다른 부모를 가리키면 자식 쪽 값 비교(ROW_VALUE)나 짝짓기(IDENTITY_UNPAIRED)에서 잡힌다.
///   부모 테이블을 먼저 짝짓고(FK 위상 순서), 아직 짝짓지 않은 테이블(자기 참조 포함)을 가리키는 FK 열은 짝짓기 서명에서 빼고 값 비교에서 확인한다.</item>
/// <item>미리보기에 없는 테이블에 변경이 있으면 UNEXPECTED_TABLE.</item>
/// <item>비결정 열은 존재(NULL 여부)와 형식만 비교한다(VOLATILE_FORMAT).</item>
/// </list>
/// 행 단위: 미리보기에만 있는 변경은 ROW_MISSING, 실제에만 있는 변경(미리보기에 있는 테이블)은 ROW_UNEXPECTED. 키 없는 테이블은 비결정 열을 뺀 이미지의 다중집합 개수로 비교한다.
/// 이 코드는 평가 하네스(Eval)의 IDENTITY 짝 매핑과 공유하지 않는다(§10.3 독립성).
/// </summary>
public static class ApplyMatcher
{
    /// <summary>같은 값 IDENTITY 행을 순서로 짝지었을 때 verification.limitations에 남기는 가정.</summary>
    public const string IdentityOrderLimitation =
        "비결정 열을 뺀 값이 같은 새 IDENTITY 행은 IDENTITY 순서로 짝지음(같은 문장이 같은 순서로 IDENTITY를 매긴다는 가정)";

    /// <summary>D7 판정.</summary>
    public static MatchOutcome Match(MatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var preview = input.Preview;
        var (rowCounts, mismatches) = CompareStatements(preview.Result.Statements, input.Actual);
        var list = new List<ApplyMismatch>(mismatches);

        // D7 2, 5: netSummary와 미리보기에 없는 테이블
        var actualSummary = ContractFormulas.Summarize(input.ActualNet);
        var expected = preview.Result.NetSummary.GroupBy(r => r.Table, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var actual = actualSummary.ToDictionary(r => r.Table, StringComparer.Ordinal);
        var previewTables = preview.Result.NetSummary.Where(r => !IsZero(r)).Select(r => r.Table)
            .Concat(preview.NetChanges.Select(n => n.Table)).ToHashSet(StringComparer.Ordinal);
        var summaryMatch = true;
        foreach (var table in expected.Keys.Union(actual.Keys, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            var e = expected.GetValueOrDefault(table);
            var a = actual.GetValueOrDefault(table);
            if (SummaryText(e) == SummaryText(a)) continue;
            summaryMatch = false;
            if (previewTables.Contains(table))
                list.Add(new ApplyMismatch
                {
                    Kind = MismatchKind.NetSummary, Table = table, Expected = SummaryText(e), Actual = SummaryText(a),
                    Message = $"{table} 순 변경 건수(inserted,updated,deleted,multisetRemoved,multisetAdded)가 미리보기와 다름",
                });
        }
        foreach (var table in input.ActualNet.Select(n => n.Table).Distinct(StringComparer.Ordinal).Where(t => !previewTables.Contains(t)).OrderBy(t => t, StringComparer.Ordinal))
            list.Add(new ApplyMismatch
            {
                Kind = MismatchKind.UnexpectedTable, Table = table, Actual = SummaryText(actual.GetValueOrDefault(table)),
                Message = $"미리보기에 없는 테이블 {table}이(가) 원본에서 바뀜",
            });

        // D7 3, 4, 6: 행 단위
        var rules = MatchRules.Create(preview, input.Baseline);
        var rows = rules.CompareRows(preview.NetChanges.Where(n => previewTables.Contains(n.Table)).ToList(),
            input.ActualNet.Where(n => previewTables.Contains(n.Table)).ToList(), list);

        return new MatchOutcome
        {
            StatementRowCounts = rowCounts,
            NetSummaryMatch = summaryMatch,
            ActualNetSummary = actualSummary,
            Mismatches = list,
            VolatileIgnored = rows.VolatileIgnored,
            IdentityPairs = rows.IdentityPairs,
            Limitations = rows.Limitations,
        };
    }

    /// <summary>D7 1: 문장별 직접 대상 행 수. 실제가 ok가 아니면 실제 행 수는 null이다.</summary>
    public static (IReadOnlyList<StatementRowCount> Rows, IReadOnlyList<ApplyMismatch> Mismatches) CompareStatements(
        IReadOnlyList<StatementResult> preview, IReadOnlyList<StatementResult> actual)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(actual);
        var rows = new List<StatementRowCount>();
        var mismatches = new List<ApplyMismatch>();
        foreach (var p in preview.OrderBy(s => s.Index))
        {
            var a = actual.FirstOrDefault(s => s.Index == p.Index);
            long? actualRows = a is { Status: StatementStatus.Ok } ? a.RowsAffected : null;
            rows.Add(new StatementRowCount { Index = p.Index, Preview = p.RowsAffected, Actual = actualRows });
            if (p.RowsAffected != actualRows)
                mismatches.Add(new ApplyMismatch
                {
                    Kind = MismatchKind.StatementRowCount, StatementIndex = p.Index,
                    Expected = p.RowsAffected?.ToString(CultureInfo.InvariantCulture), Actual = actualRows?.ToString(CultureInfo.InvariantCulture),
                    Message = $"문장 {p.Index}의 영향 행 수가 미리보기와 다름",
                });
        }
        return (rows, mismatches);
    }

    static bool IsZero(NetSummaryRow r) => r.Inserted == 0 && r.Updated == 0 && r.Deleted == 0 && r.MultisetRemoved == 0 && r.MultisetAdded == 0;

    static string SummaryText(NetSummaryRow? r) => r is null ? "0,0,0,0,0"
        : string.Create(CultureInfo.InvariantCulture, $"{r.Inserted},{r.Updated},{r.Deleted},{r.MultisetRemoved},{r.MultisetAdded}");
}

/// <summary>행 단위 대조 결과.</summary>
internal sealed record RowMatchResult(IReadOnlyList<string> VolatileIgnored, IReadOnlyList<IdentityPair> IdentityPairs, IReadOnlyList<string> Limitations);

/// <summary>
/// 한 미리보기·기준선에 대한 D7 3·4·6 규칙 재료(테이블 정보, 비결정 열, IDENTITY를 가리키는 FK)와 행 비교. KEYSET 대조도 값 비교에 쓴다.
/// </summary>
internal sealed class MatchRules
{
    static readonly Regex IntPattern = new(@"^-?\d+\z", RegexOptions.CultureInvariant);
    static readonly Regex NumberPattern = new(@"^-?\d+(\.\d+)?([eE][-+]?\d+)?\z", RegexOptions.CultureInvariant);
    static readonly Regex GuidPattern = new(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.CultureInvariant);
    static readonly Regex DateTimePattern = new(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d+)?)?)?(Z|[+-]\d{2}:\d{2})?\z", RegexOptions.CultureInvariant);
    static readonly Regex TimePattern = new(@"^\d{2}:\d{2}:\d{2}(\.\d+)?\z", RegexOptions.CultureInvariant);

    readonly Dictionary<string, TableInfo> tables;
    readonly Dictionary<string, HashSet<string>> insertVolatile;
    readonly Dictionary<string, HashSet<string>> updateVolatile;
    /// <summary>자식 테이블 → 자식 열 → (부모 테이블, 부모 IDENTITY 열).</summary>
    readonly Dictionary<string, Dictionary<string, (string Table, string Column)>> identityRefs;
    readonly HashSet<string> previewInsertTables;

    MatchRules(
        Dictionary<string, TableInfo> tables, Dictionary<string, HashSet<string>> insertVolatile, Dictionary<string, HashSet<string>> updateVolatile,
        Dictionary<string, Dictionary<string, (string, string)>> identityRefs, HashSet<string> previewInsertTables)
    {
        this.tables = tables;
        this.insertVolatile = insertVolatile;
        this.updateVolatile = updateVolatile;
        this.identityRefs = identityRefs;
        this.previewInsertTables = previewInsertTables;
    }

    /// <summary>
    /// 비결정 열: INSERT 쪽은 매니페스트 volatileColumns(IDENTITY·ROWVERSION·NONDETERMINISTIC_DEFAULT) + 카탈로그 열 속성 + 그 테이블 RowChange.volatile 전부,
    /// UPDATE 쪽은 ROWVERSION + RowChange.volatile 중 STATEMENT_FUNCTION·TRIGGER_GETDATE·ROWVERSION. DELETE(before)는 비결정 열이 없다(기준선 값).
    /// </summary>
    public static MatchRules Create(PreviewRecord preview, BaselineContext baseline)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(baseline);
        var tables = baseline.Catalog.ToCaptureInput().GroupBy(t => t.DisplayName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ins = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var upd = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> Set(Dictionary<string, HashSet<string>> d, string table) =>
            d.TryGetValue(table, out var s) ? s : d[table] = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in tables.Values)
            foreach (var c in t.Columns)
            {
                if (c.IsIdentity || c.HasNondeterministicDefault) Set(ins, t.DisplayName).Add(c.Name);
                if (c.IsRowVersion) { Set(ins, t.DisplayName).Add(c.Name); Set(upd, t.DisplayName).Add(c.Name); }
            }
        foreach (var mt in baseline.Manifest.Tables)
            foreach (var v in mt.VolatileColumns)
            {
                Set(ins, mt.Table).Add(v.Column);
                if (v.Reason == "ROWVERSION") Set(upd, mt.Table).Add(v.Column);
            }
        var firingTable = preview.Result.Firings.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Table, StringComparer.Ordinal);
        foreach (var rc in preview.RowChanges)
        {
            if (!firingTable.TryGetValue(rc.FiringId, out var table)) continue;
            foreach (var v in rc.Volatile)
            {
                Set(ins, table).Add(v.Column);
                if (v.Reason is VolatileReason.StatementFunction or VolatileReason.TriggerGetdate or VolatileReason.Rowversion) Set(upd, table).Add(v.Column);
            }
        }

        var names = baseline.Catalog.Tables.ToDictionary(t => t.ObjectId, t => t.Schema + "." + t.Name);
        var refs = new Dictionary<string, Dictionary<string, (string, string)>>(StringComparer.Ordinal);
        foreach (var fk in baseline.Catalog.ForeignKeys)
        {
            if (!names.TryGetValue(fk.ParentObjectId, out var child) || !names.TryGetValue(fk.ReferencedObjectId, out var parent)) continue;
            if (!tables.TryGetValue(parent, out var p) || !p.Columns.Any(c => c.IsIdentity && string.Equals(c.Name, fk.ReferencedColumn, StringComparison.Ordinal))) continue;
            if (!refs.TryGetValue(child, out var m)) refs[child] = m = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            m.TryAdd(fk.ParentColumn, (parent, fk.ReferencedColumn));
        }

        var insertTables = preview.NetChanges.Where(n => n.Change == NetChangeKind.Inserted).Select(n => n.Table).ToHashSet(StringComparer.Ordinal);
        return new MatchRules(tables, ins, upd, refs, insertTables);
    }

    /// <summary>KEYSET에서 INSERTED를 건수로만 확인할 테이블(IDENTITY가 있거나, 미리보기에서 새 행이 생긴 IDENTITY 테이블을 FK로 가리킴).</summary>
    public bool InsertCountsOnly(string table) =>
        HasIdentity(table)
        || (identityRefs.TryGetValue(table, out var m) && m.Values.Any(r => previewInsertTables.Contains(r.Table)));

    bool HasIdentity(string table) => tables.TryGetValue(table, out var t) && t.Columns.Any(c => c.IsIdentity);

    IReadOnlySet<string> VolatileFor(string table, NetChangeKind change) => change switch
    {
        NetChangeKind.Inserted or NetChangeKind.MultisetAdded => insertVolatile.TryGetValue(table, out var i) ? i : NoColumns,
        NetChangeKind.Updated => updateVolatile.TryGetValue(table, out var u) ? u : NoColumns,
        _ => NoColumns,
    };

    static readonly HashSet<string> NoColumns = new(StringComparer.Ordinal);

    string BaseColumn(string table, string property)
    {
        if (tables.TryGetValue(table, out var t))
        {
            if (t.Columns.Any(c => string.Equals(c.Name, property, StringComparison.Ordinal))) return property;
            var at = property.LastIndexOf('@');
            if (at > 0 && t.Columns.Any(c => string.Equals(c.Name, property[..at], StringComparison.Ordinal))) return property[..at];
        }
        return property;
    }

    bool IsComputed(string table, string column) =>
        tables.TryGetValue(table, out var t) && t.Columns.Any(c => c.IsComputed && string.Equals(c.Name, column, StringComparison.Ordinal));

    /// <summary>
    /// 값 비교(D7 3·6). expected는 짝 매핑 치환을 마친 미리보기 값. 비결정 열은 존재·형식만 보고 ignored에 "테이블.열"을 넣는다.
    /// replacements(미리보기 값 → 실제 값)는 계산 열에만, 숫자 경계를 지켜 적용한다.
    /// </summary>
    public void CompareValues(
        string table, NetChangeKind change, RowKey key, IReadOnlyDictionary<string, string?>? expected, IReadOnlyDictionary<string, string?>? actual,
        IReadOnlyDictionary<string, string>? replacements, List<ApplyMismatch> mismatches, ISet<string> ignored)
    {
        if (expected is null && actual is null) return;
        var volatileColumns = VolatileFor(table, change);
        var props = (expected?.Keys ?? []).Concat(actual?.Keys ?? []).Distinct(StringComparer.Ordinal).ToList();
        foreach (var prop in props)
        {
            var column = BaseColumn(table, prop);
            var ev = expected?.GetValueOrDefault(prop);
            var av = actual?.GetValueOrDefault(prop);
            if (volatileColumns.Contains(column))
            {
                ignored.Add(table + "." + column);
                var ef = FormatClass(ev);
                var af = FormatClass(av);
                if (ef != af)
                    mismatches.Add(new ApplyMismatch
                    {
                        Kind = MismatchKind.VolatileFormat, Table = table, Key = key, Column = prop, Expected = ev, Actual = av,
                        Message = $"{table}.{prop} 비결정 값의 존재·형식이 다름({ef} → {af})",
                    });
                continue;
            }
            if (string.Equals(ev, av, StringComparison.Ordinal)) continue;
            if (ev is not null && replacements is { Count: > 0 } && IsComputed(table, column) && string.Equals(Replace(ev, replacements), av, StringComparison.Ordinal)) continue;
            mismatches.Add(new ApplyMismatch
            {
                Kind = MismatchKind.RowValue, Table = table, Key = key, Column = prop, Expected = ev, Actual = av,
                Message = $"{table}.{prop} 값이 미리보기와 다름",
            });
        }
    }

    /// <summary>존재·형식 분류(D7 6). 같은 열끼리 비교하므로 NULL 여부와 큰 형식 묶음만 본다.</summary>
    internal static string FormatClass(string? v) =>
        v is null ? "NULL"
        : IntPattern.IsMatch(v) ? "INTEGER"
        : NumberPattern.IsMatch(v) ? "NUMBER"
        : GuidPattern.IsMatch(v) ? "GUID"
        : DateTimePattern.IsMatch(v) ? "DATETIME"
        : TimePattern.IsMatch(v) ? "TIME"
        : v is "true" or "false" ? "BOOLEAN"
        : "TEXT";

    static string Replace(string value, IReadOnlyDictionary<string, string> replacements)
    {
        var pattern = string.Join("|", replacements.Keys.OrderByDescending(k => k.Length).Select(k => @"(?<!\d)" + Regex.Escape(k) + @"(?!\d)"));
        return Regex.Replace(value, pattern, m => replacements.TryGetValue(m.Value, out var r) ? r : m.Value, RegexOptions.CultureInvariant);
    }

    /// <summary>D7 3·4·6 행 단위 대조. 둘 다 미리보기에 있는 테이블만 넘긴다.</summary>
    public RowMatchResult CompareRows(IReadOnlyList<NetChange> preview, IReadOnlyList<NetChange> actual, List<ApplyMismatch> mismatches)
    {
        var ignored = new SortedSet<string>(StringComparer.Ordinal);
        var pairs = new List<IdentityPair>();
        // (부모 테이블, 부모 IDENTITY 열, 미리보기 값) → 실제 값
        var idMap = new Dictionary<(string Table, string Column, string Value), string>();
        var consumedPreview = new HashSet<NetChange>(ReferenceEqualityComparer.Instance);
        var consumedActual = new HashSet<NetChange>(ReferenceEqualityComparer.Instance);
        var pairedRows = new List<(string Table, NetChange Preview, NetChange Actual, Dictionary<string, string> Replacements)>();
        var orderedPairing = false;

        // D7 4: 새 IDENTITY 행 짝짓기(부모 먼저)
        var identityTables = preview.Concat(actual)
            .Where(n => n.Change == NetChangeKind.Inserted && n.Key.Kind != KeyKind.ROW && HasIdentity(n.Table))
            .Select(n => n.Table).ToHashSet(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in TopologicalOrder(identityTables))
        {
            var p = preview.Where(n => n.Table == table && n.Change == NetChangeKind.Inserted && n.Key.Kind != KeyKind.ROW).ToList();
            var a = actual.Where(n => n.Table == table && n.Change == NetChangeKind.Inserted && n.Key.Kind != KeyKind.ROW).ToList();
            var info = tables[table];
            var identityColumns = info.Columns.Where(c => c.IsIdentity).Select(c => c.Name).ToList();
            HashSet<string> deferred = identityRefs.TryGetValue(table, out var refs)
                ? refs.Where(r => identityTables.Contains(r.Value.Table) && !done.Contains(r.Value.Table)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var volatileColumns = VolatileFor(table, NetChangeKind.Inserted);
            var signature = p.Concat(a).SelectMany(n => n.After?.Keys ?? [])
                .Distinct(StringComparer.Ordinal)
                .Where(prop =>
                {
                    var c = BaseColumn(table, prop);
                    return !volatileColumns.Contains(c) && !identityColumns.Contains(c) && !IsComputed(table, c) && !deferred.Contains(c);
                })
                .OrderBy(x => x, StringComparer.Ordinal).ToList();

            var previewGroups = p.GroupBy(n => RowImage.Canonical(Substitute(table, n.After, idMap, null), signature), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var actualGroups = a.GroupBy(n => RowImage.Canonical(n.After, signature), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var (sig, ps) in previewGroups)
            {
                var candidates = actualGroups.GetValueOrDefault(sig) ?? [];
                // 같은 서명 행이 양쪽에 같은 개수면 IDENTITY 오름차순으로 순서대로 짝짓는다(같은 문장이 같은 순서로 IDENTITY를 매긴다는 가정).
                // IDENTITY 값으로 순서를 정할 수 없으면(값 없음·정수 아님·중복) 짝짓지 않는다.
                if (ps.Count == candidates.Count && OrderByIdentity(ps, identityColumns[0]) is { } orderedPreview
                    && OrderByIdentity(candidates, identityColumns[0]) is { } orderedActual)
                {
                    if (ps.Count > 1) orderedPairing = true;
                    for (var i = 0; i < orderedPreview.Count; i++)
                    {
                        var (pv, av) = (orderedPreview[i], orderedActual[i]);
                        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var col in identityColumns)
                            if (pv.After?.GetValueOrDefault(col) is { } from && av.After?.GetValueOrDefault(col) is { } to)
                            {
                                idMap[(table, col, from)] = to;
                                if (from != to) replacements.TryAdd(from, to);
                            }
                        pairs.Add(new IdentityPair { Table = table, PreviewKey = pv.Key, ActualKey = av.Key });
                        pairedRows.Add((table, pv, av, replacements));
                    }
                    continue;
                }
                foreach (var n in ps)
                    mismatches.Add(new ApplyMismatch
                    {
                        Kind = MismatchKind.IdentityUnpaired, Table = table, Key = n.Key,
                        Expected = ps.Count.ToString(CultureInfo.InvariantCulture), Actual = candidates.Count.ToString(CultureInfo.InvariantCulture),
                        Message = candidates.Count == 0
                            ? $"{table}의 새 IDENTITY 행과 비결정 열을 뺀 값이 같은 원본 행이 없음"
                            : ps.Count != candidates.Count
                                ? $"{table}의 비결정 열을 뺀 값이 같은 새 IDENTITY 행 개수가 다름(미리보기 {ps.Count}행, 원본 {candidates.Count}행)"
                                : $"{table}의 같은 값 새 IDENTITY 행을 IDENTITY 순서로 짝지을 수 없음(IDENTITY 값이 없거나 정수가 아니거나 겹침)",
                    });
            }
            foreach (var (sig, cs) in actualGroups)
                if (!previewGroups.ContainsKey(sig))
                    foreach (var n in cs)
                        mismatches.Add(new ApplyMismatch { Kind = MismatchKind.RowUnexpected, Table = table, Key = n.Key, Actual = "INSERTED", Message = $"{table}에 미리보기에 없는 새 행이 생김" });
            consumedPreview.UnionWith(p);
            consumedActual.UnionWith(a);
            done.Add(table);
        }

        // 짝지은 행의 값(D7 3·6, FK 치환 포함)
        foreach (var (table, pv, av, replacements) in pairedRows)
        {
            var expected = Substitute(table, pv.After, idMap, replacements);
            CompareValues(table, NetChangeKind.Inserted, pv.Key, expected, av.After, replacements, mismatches, ignored);
        }

        // D7 3: 키 있는 행(키의 FK 값도 짝 매핑으로 치환)
        var actualByKey = new Dictionary<(string Table, string Key), NetChange>();
        foreach (var n in actual.Where(n => n.Key.Kind != KeyKind.ROW && !consumedActual.Contains(n)))
            actualByKey.TryAdd((n.Table, RowImage.KeyString(n.Key.Columns, n.Key.Values)), n);
        foreach (var n in preview.Where(n => n.Key.Kind != KeyKind.ROW && !consumedPreview.Contains(n)))
        {
            var mappedValues = n.Key.Columns.Select((c, i) => Map(n.Table, c, i < n.Key.Values.Count ? n.Key.Values[i] : null, idMap)).ToList();
            var key = (n.Table, RowImage.KeyString(n.Key.Columns, mappedValues));
            if (!actualByKey.Remove(key, out var a))
            {
                mismatches.Add(new ApplyMismatch { Kind = MismatchKind.RowMissing, Table = n.Table, Key = n.Key, Expected = Name(n.Change), Message = $"{n.Table}의 미리보기 변경({Name(n.Change)})이 원본에서 일어나지 않음" });
                continue;
            }
            if (a.Change != n.Change)
            {
                mismatches.Add(new ApplyMismatch { Kind = MismatchKind.RowMissing, Table = n.Table, Key = n.Key, Expected = Name(n.Change), Actual = Name(a.Change), Message = $"{n.Table} 행의 변경 종류가 미리보기와 다름" });
                continue;
            }
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            var source = n.Change == NetChangeKind.Deleted ? n.Before : n.After;
            var expected = Substitute(n.Table, source, idMap, replacements);
            CompareValues(n.Table, n.Change, n.Key, expected, n.Change == NetChangeKind.Deleted ? a.Before : a.After, replacements, mismatches, ignored);
        }
        foreach (var a in actualByKey.Values)
            mismatches.Add(new ApplyMismatch { Kind = MismatchKind.RowUnexpected, Table = a.Table, Key = a.Key, Actual = Name(a.Change), Message = $"{a.Table}에 미리보기에 없는 변경({Name(a.Change)})이 생김" });

        // 키 없는 테이블: 비결정 열을 뺀 이미지의 다중집합 개수
        var rowTables = preview.Concat(actual).Where(n => n.Key.Kind == KeyKind.ROW).Select(n => n.Table).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal);
        foreach (var table in rowTables)
        {
            var expectedCounts = MultisetCounts(table, preview.Where(n => n.Table == table && n.Key.Kind == KeyKind.ROW), idMap, ignored);
            var actualCounts = MultisetCounts(table, actual.Where(n => n.Table == table && n.Key.Kind == KeyKind.ROW), null, ignored);
            foreach (var k in expectedCounts.Keys.Union(actualCounts.Keys).OrderBy(k => k.Kind).ThenBy(k => k.Image, StringComparer.Ordinal))
            {
                var e = expectedCounts.GetValueOrDefault(k);
                var a = actualCounts.GetValueOrDefault(k);
                if (e == a) continue;
                mismatches.Add(new ApplyMismatch
                {
                    Kind = e > a ? MismatchKind.RowMissing : MismatchKind.RowUnexpected, Table = table, Key = new RowKey { Kind = KeyKind.ROW },
                    Expected = e.ToString(CultureInfo.InvariantCulture), Actual = a.ToString(CultureInfo.InvariantCulture),
                    Message = $"{table}(키 없음)의 같은 행 {Name(k.Kind)} 개수가 미리보기와 다름(미리보기 {e}, 원본 {a})",
                });
            }
        }

        return new RowMatchResult(ignored.ToList(), pairs, orderedPairing ? [ApplyMatcher.IdentityOrderLimitation] : []);
    }

    /// <summary>
    /// 새 IDENTITY 행을 IDENTITY 값(정수) 오름차순으로 정렬한 목록. 한 행이면 그대로. 값이 없거나 정수가 아니거나 겹치면 null(순서를 정할 수 없음).
    /// </summary>
    static List<NetChange>? OrderByIdentity(List<NetChange> rows, string identityColumn)
    {
        if (rows.Count == 1) return rows;
        var keyed = new List<(BigInteger Value, NetChange Row)>(rows.Count);
        foreach (var n in rows)
        {
            if (n.After?.GetValueOrDefault(identityColumn) is not { } text
                || !BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return null;
            keyed.Add((value, n));
        }
        keyed.Sort((x, y) => x.Value.CompareTo(y.Value));
        for (var i = 1; i < keyed.Count; i++)
            if (keyed[i].Value == keyed[i - 1].Value) return null;
        return keyed.Select(k => k.Row).ToList();
    }

    Dictionary<(NetChangeKind Kind, string Image), long> MultisetCounts(
        string table, IEnumerable<NetChange> changes, Dictionary<(string, string, string), string>? idMap, ISet<string> ignored)
    {
        var counts = new Dictionary<(NetChangeKind, string), long>();
        foreach (var n in changes)
        {
            var values = n.Change == NetChangeKind.MultisetRemoved ? n.Before : n.After;
            if (idMap is not null) values = Substitute(table, values, idMap, null);
            var volatileColumns = VolatileFor(table, n.Change);
            var props = (values?.Keys ?? []).Where(p =>
            {
                var c = BaseColumn(table, p);
                if (!volatileColumns.Contains(c)) return true;
                ignored.Add(table + "." + c);
                return false;
            }).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var key = (n.Change, RowImage.Canonical(values, props));
            counts[key] = counts.GetValueOrDefault(key) + n.Multiplicity;
        }
        return counts;
    }

    string? Map(string table, string column, string? value, Dictionary<(string, string, string), string> idMap) =>
        value is not null && identityRefs.TryGetValue(table, out var refs) && refs.TryGetValue(column, out var r) && idMap.TryGetValue((r.Table, r.Column, value), out var mapped)
            ? mapped : value;

    /// <summary>IDENTITY를 가리키는 FK 열 값을 짝 매핑으로 바꾼 사본. replacements가 있으면 바꾼 값(미리보기 → 실제)을 더한다.</summary>
    Dictionary<string, string?>? Substitute(string table, IReadOnlyDictionary<string, string?>? values, Dictionary<(string, string, string), string> idMap, Dictionary<string, string>? replacements)
    {
        if (values is null) return null;
        var copy = new Dictionary<string, string?>(values, StringComparer.Ordinal);
        if (!identityRefs.TryGetValue(table, out var refs)) return copy;
        foreach (var column in refs.Keys)
        {
            if (!copy.TryGetValue(column, out var v) || v is null) continue;
            var mapped = Map(table, column, v, idMap);
            if (mapped is null || mapped == v) continue;
            copy[column] = mapped;
            replacements?.TryAdd(v, mapped);
        }
        return copy;
    }

    /// <summary>FK 의존(자식 → 부모) 위상 순서, 같은 단계는 이름 순. 순환(자기 참조 포함)은 남은 것을 이름 순으로 붙인다.</summary>
    List<string> TopologicalOrder(HashSet<string> set)
    {
        var deps = set.ToDictionary(t => t, t => identityRefs.TryGetValue(t, out var refs)
            ? refs.Values.Select(r => r.Table).Where(p => p != t && set.Contains(p)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var order = new List<string>();
        var remaining = new SortedSet<string>(set, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(t => deps[t].All(order.Contains));
            var next = ready ?? remaining.Min!;
            order.Add(next);
            remaining.Remove(next);
        }
        return order;
    }

    static string Name(NetChangeKind k) => k switch
    {
        NetChangeKind.Inserted => "INSERTED",
        NetChangeKind.Updated => "UPDATED",
        NetChangeKind.Deleted => "DELETED",
        NetChangeKind.MultisetRemoved => "MULTISET_REMOVED",
        _ => "MULTISET_ADDED",
    };
}
