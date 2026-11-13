using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

/// <summary>
/// 트리거 본문 정적 분석(§4 C7 정적 분석 부분, §7.2 4단계, D5 외부 부작용). SP4 TriggerAnalyzer를 바탕으로 했다.
/// <list type="bullet">
/// <item>테이블 부모의 DML 트리거마다 1개를 낸다(DDL 트리거, 뷰 부모는 제외). 결과는 (테이블, 트리거 이름) 순이다.</item>
/// <item>감사 트리거(dbr_audit_)는 우리가 만든 코드라 본문을 분석하지 않고 IsAuditTrigger = true만 표시한다.</item>
/// <item>암호화·CLR(Definition 없음, Q6): IsEncrypted = true, HasDynamicSql = true, WriteTargets = [], WriteTargetsUnknown = true.</item>
/// <item>파싱 실패: HasDynamicSql = true, ExternalSideEffects = ["unparsed"], WriteTargets = [], WriteTargetsUnknown = true (외부 부작용도 배제 못 함 → 닫힌 쪽).</item>
/// <item>따라서 HasDynamicSql = false이고 WriteTargets가 비었으면 "아무 테이블에도 쓰지 않음"이 확실하다.</item>
/// <item>스키마를 생략한 쓰기 대상은 트리거 스키마(= 부모 테이블 스키마), dbo 순으로 카탈로그에서 찾고 없으면 dbo로 둔다.</item>
/// <item>CTE에 대한 쓰기(WITH c AS (SELECT ... FROM t) DELETE FROM c)는 CTE 정의 안의 기반 테이블 전부를 쓰기 대상으로 본다(SP4는 제외했다).</item>
/// <item>
/// 프로시저 펼치기(2주차 인계 1): 트리거가 부르는 프로시저를 카탈로그 프로시저 모듈(DbCatalog.ProcedureModules)에서 찾아 본문을 같은 방식으로 분석하고,
/// 쓰기 대상·비결정 쓰기·외부 부작용·ROLLBACK·트랜잭션 제어를 트리거 결과에 합친다. 펼친 호출은 ExpandedProcedureCalls, 못 펼친 호출은 UnresolvedProcedureCalls(둘이 ProcedureCalls를 이름 그대로 나눈다).
/// 못 펼치는 경우: 카탈로그에 없음(다른 DB·서버, 시스템 프로시저), 본문 없음(암호화·CLR·확장), 파싱 실패, 동적 SQL, 재귀, 깊이 상한 초과, 안에서 부르는 프로시저를 못 펼침.
/// 프로시저 안에서 부르는 프로시저도 같은 규칙으로 따라간다(깊이 상한 <see cref="MaxProcedureDepth"/>): AW uspLogError가 uspPrintError(PRINT만)를 부르므로,
/// 한 단계에서 멈추면 uspLogError가 늘 미상이 되어 인계 1의 목적(dbo.ErrorLog에만 씀)을 이루지 못한다. 못 펼친 프로시저라도 본문을 읽었으면 그 외부 부작용·트랜잭션 제어는 합친다(닫힌 쪽).
/// 이름 해석: 스키마가 있으면 그대로, 없으면 부르는 모듈의 스키마, dbo 순(TableResolver와 같은 규칙, Q4).
/// </item>
/// </list>
/// </summary>
public sealed class TriggerAnalyzer : ITriggerAnalyzer
{
    /// <summary>파싱 실패 표시(ExternalSideEffects 항목).</summary>
    public const string UnparsedMarker = "unparsed";

    /// <summary>프로시저 펼치기 깊이 상한(트리거가 직접 부르는 프로시저가 1).</summary>
    public const int MaxProcedureDepth = ProcedureAnalyzer.MaxProcedureDepth;

    /// <inheritdoc />
    public IReadOnlyList<TriggerStaticInfo> Analyze(DbCatalog catalog)
    {
        var resolver = new TableResolver(catalog);
        var tables = catalog.Tables.ToDictionary(t => t.ObjectId);
        var expander = new ProcedureExpander(catalog, resolver);
        var result = new List<TriggerStaticInfo>();

        foreach (var g in catalog.Triggers
                     .Where(t => string.Equals(t.ParentClass, "OBJECT_OR_COLUMN", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(t => t.ObjectId))
        {
            var first = g.First();
            if (!tables.TryGetValue(first.ParentId, out var table)) continue; // 뷰 부모 등
            var events = g.Select(e => ToOperation(e.Event)).OfType<ChangeOperation>().Distinct().OrderBy(o => o).ToList();
            var isAudit = first.Name.StartsWith(AuditTriggerGenerator.TriggerPrefix, StringComparison.OrdinalIgnoreCase);
            var display = TableResolver.Display(table);
            var trigger = table.Schema + "." + first.Name;

            Body body;
            if (isAudit) body = Body.Empty;
            else if (first.Definition is null) body = Body.Opaque;
            else body = AnalyzeBody(first.Definition, table.Schema, resolver, catalog, expander);

            result.Add(new TriggerStaticInfo
            {
                Trigger = trigger,
                ObjectId = first.ObjectId,
                Table = display,
                Events = events,
                IsInsteadOf = first.IsInsteadOf,
                IsDisabled = first.IsDisabled,
                IsEncrypted = !isAudit && first.Definition is null,
                IsAuditTrigger = isAudit,
                WriteTargets = body.WriteTargets,
                WriteTargetsUnknown = body.WriteTargetsUnknown,
                HasDynamicSql = body.HasDynamicSql,
                HasRollback = body.HasRollback,
                HasTransactionControl = body.TransactionControl,
                ExternalSideEffects = body.External,
                ProcedureCalls = body.ProcedureCalls,
                ExpandedProcedureCalls = body.ExpandedCalls,
                UnresolvedProcedureCalls = body.UnresolvedCalls,
                NondeterministicWrites = body.NondeterministicWrites,
            });
        }
        return result.OrderBy(t => t.Table, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Trigger, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static ChangeOperation? ToOperation(string ev) => ev.ToUpperInvariant() switch
    {
        "INSERT" => ChangeOperation.Insert,
        "UPDATE" => ChangeOperation.Update,
        "DELETE" => ChangeOperation.Delete,
        _ => null,
    };

    sealed record Body(
        IReadOnlyList<string> WriteTargets, bool HasDynamicSql, bool HasRollback, IReadOnlyList<string> External,
        IReadOnlyList<string> ProcedureCalls, IReadOnlyList<TableColumn> NondeterministicWrites, bool WriteTargetsUnknown = false, bool TransactionControl = false)
    {
        public IReadOnlyList<string> ExpandedCalls { get; init; } = [];
        public IReadOnlyList<string> UnresolvedCalls { get; init; } = [];

        public static readonly Body Empty = new([], false, false, [], [], []);
        public static readonly Body Opaque = new([], true, false, [], [], [], WriteTargetsUnknown: true);
        public static Body Unparsed => new([], true, false, [UnparsedMarker], [], [], WriteTargetsUnknown: true);
    }

    static Body AnalyzeBody(string definition, string triggerSchema, TableResolver resolver, DbCatalog catalog, ProcedureExpander expander)
    {
        TSqlFragment? fragment;
        IList<ParseError> errors;
        try { fragment = SqlSyntax.Parse(definition, out errors); }
        catch (Exception) { return Body.Unparsed; }
        if (fragment is null || errors.Count > 0) return Body.Unparsed;

        var finder = new TriggerFinder();
        fragment.Accept(finder);
        if (finder.Statements.Count == 0) return Body.Unparsed;

        var a = new Accumulator(triggerSchema, resolver, catalog, expander.Index);
        foreach (var stmt in finder.Statements)
        {
            stmt.StatementList.Accept(new CtePrepass(a.CteDefinitions));
            stmt.StatementList.Accept(new BodyVisitor(a));
        }

        // 인계 1: 직접 부르는 프로시저를 펼쳐 합친다. ProcedureCalls(이름 그대로)를 펼친 것·못 펼친 것으로 나눈다.
        var expanded = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in a.Calls.ToList())
        {
            var e = expander.Expand(node, triggerSchema, 1, []);
            if (e.Parsed is { } p) a.Merge(p);
            (e.Resolved ? expanded : unresolved).Add(name);
        }
        expanded.ExceptWith(unresolved); // 같은 이름은 같은 결과지만, 혹시 갈리면 미상 쪽(닫힌 쪽)

        return new Body(
            a.WriteTargets.ToList(), a.DynamicSql, a.Rollback, a.External.ToList(), a.ProcedureCalls.ToList(),
            a.NondeterministicWrites.OrderBy(w => w.Table, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Column, StringComparer.OrdinalIgnoreCase).ToList(),
            TransactionControl: a.TransactionControl)
        {
            ExpandedCalls = expanded.ToList(),
            UnresolvedCalls = unresolved.ToList(),
        };
    }

    sealed class TriggerFinder : TSqlFragmentVisitor
    {
        public readonly List<TriggerStatementBody> Statements = [];
        public override void Visit(CreateTriggerStatement node) => Statements.Add(node);
        public override void Visit(AlterTriggerStatement node) => Statements.Add(node);
        public override void Visit(CreateOrAlterTriggerStatement node) => Statements.Add(node);
    }
}
