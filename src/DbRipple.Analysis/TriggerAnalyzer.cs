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
    public const int MaxProcedureDepth = 4;

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

        var a = new Accumulator(triggerSchema, resolver, catalog);
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

    /// <summary>프로시저 본문 하나를 분석한 결과. Parsed는 본문을 읽은 경우의 누적값(못 펼쳤어도 외부 부작용·트랜잭션 제어는 합친다), Resolved는 쓰기 대상을 다 안다는 뜻.</summary>
    readonly record struct Expansion(bool Resolved, Accumulator? Parsed);

    /// <summary>프로시저 펼치기(인계 1). 깊이 상한·재귀 판정이 호출 경로마다 달라 캐시하지 않는다(트리거 수 × 호출 수가 작다).</summary>
    sealed class ProcedureExpander(DbCatalog catalog, TableResolver resolver)
    {
        readonly Dictionary<string, CatalogModule> byName = catalog.ProcedureModules
            .GroupBy(m => m.Schema + "." + m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public CatalogModule? Find(SchemaObjectName n, string callerSchema)
        {
            if (n.ServerIdentifier != null || n.DatabaseIdentifier != null) return null; // 다른 DB·서버(현재 DB 이름이어도 확인하지 않음, 닫힌 쪽)
            var name = n.BaseIdentifier.Value;
            if (n.SchemaIdentifier != null) return byName.GetValueOrDefault(n.SchemaIdentifier.Value + "." + name);
            return byName.GetValueOrDefault(callerSchema + "." + name) ?? byName.GetValueOrDefault("dbo." + name);
        }

        public Expansion Expand(SchemaObjectName call, string callerSchema, int depth, HashSet<int> stack)
        {
            var module = Find(call, callerSchema);
            if (module?.Definition is null || depth > MaxProcedureDepth || stack.Contains(module.ObjectId)) return new(false, null);

            TSqlFragment? fragment;
            IList<ParseError> errors;
            try { fragment = SqlSyntax.Parse(module.Definition, out errors); }
            catch (Exception) { return new(false, null); }
            if (fragment is null || errors.Count > 0) return new(false, null);
            var finder = new ProcedureFinder();
            fragment.Accept(finder);
            if (finder.Statements.Count != 1 || finder.Statements[0].StatementList is not { } body) return new(false, null);

            var a = new Accumulator(module.Schema, resolver, catalog);
            body.Accept(new CtePrepass(a.CteDefinitions));
            body.Accept(new BodyVisitor(a));

            var resolved = !a.DynamicSql;
            stack.Add(module.ObjectId);
            foreach (var (_, node) in a.Calls.ToList())
            {
                var inner = Expand(node, module.Schema, depth + 1, stack);
                if (inner.Parsed is { } p) a.Merge(p);
                resolved &= inner.Resolved;
            }
            stack.Remove(module.ObjectId);
            return new(resolved, a);
        }
    }

    sealed class ProcedureFinder : TSqlFragmentVisitor
    {
        public readonly List<ProcedureStatementBody> Statements = [];
        public override void Visit(CreateProcedureStatement node) => Statements.Add(node);
        public override void Visit(AlterProcedureStatement node) => Statements.Add(node);
        public override void Visit(CreateOrAlterProcedureStatement node) => Statements.Add(node);
    }

    sealed class Accumulator(string triggerSchema, TableResolver resolver, DbCatalog catalog)
    {
        public readonly string TriggerSchema = triggerSchema;
        public readonly TableResolver Resolver = resolver;
        public readonly DbCatalog Catalog = catalog;
        public readonly SortedSet<string> WriteTargets = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> ProcedureCalls = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> External = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<TableColumn> NondeterministicWrites = [];
        public readonly Dictionary<string, List<QueryExpression>> CteDefinitions = new(StringComparer.OrdinalIgnoreCase);
        public bool DynamicSql;
        public bool Rollback;
        public bool TransactionControl;
        /// <summary>이름 해석 전 프로시저 호출(이름 그대로, 구문 노드). ProcedureCalls와 같은 이름들이다.</summary>
        public readonly List<(string Name, SchemaObjectName Node)> Calls = [];

        /// <summary>
        /// 펼친(또는 본문만 읽은) 프로시저 결과를 합친다: 쓰기 대상, 비결정 쓰기, 외부 부작용, ROLLBACK, 트랜잭션 제어.
        /// 동적 SQL·프로시저 호출 이름은 합치지 않는다(트리거 본문 속성이고, 프로시저 쪽 미상은 UnresolvedProcedureCalls로 나타낸다).
        /// 못 펼친 프로시저의 쓰기 대상을 합쳐도 해롭지 않다: 그 호출이 UnresolvedProcedureCalls에 있어 C13이 미상으로 본다.
        /// </summary>
        public void Merge(Accumulator p)
        {
            WriteTargets.UnionWith(p.WriteTargets);
            NondeterministicWrites.UnionWith(p.NondeterministicWrites);
            External.UnionWith(p.External);
            Rollback |= p.Rollback;
            TransactionControl |= p.TransactionControl;
        }
    }

    sealed class TriggerFinder : TSqlFragmentVisitor
    {
        public readonly List<TriggerStatementBody> Statements = [];
        public override void Visit(CreateTriggerStatement node) => Statements.Add(node);
        public override void Visit(AlterTriggerStatement node) => Statements.Add(node);
        public override void Visit(CreateOrAlterTriggerStatement node) => Statements.Add(node);
    }

    sealed class CtePrepass(Dictionary<string, List<QueryExpression>> defs) : TSqlFragmentVisitor
    {
        public override void Visit(CommonTableExpression node)
        {
            if (!defs.TryGetValue(node.ExpressionName.Value, out var list)) defs[node.ExpressionName.Value] = list = [];
            list.Add(node.QueryExpression);
        }
    }

    sealed class BodyVisitor(Accumulator a) : TSqlFragmentVisitor
    {
        public override void Visit(InsertSpecification n) => Write(n, n.Target, null);
        public override void Visit(UpdateSpecification n) => Write(n, n.Target, n.FromClause);
        public override void Visit(DeleteSpecification n) => Write(n, n.Target, n.FromClause);
        public override void Visit(MergeSpecification n) => Write(n, n.Target, null);
        public override void Visit(RollbackTransactionStatement n) => a.Rollback = true;
        public override void Visit(CommitTransactionStatement n) => a.TransactionControl = true;
        public override void Visit(BeginTransactionStatement n) => a.TransactionControl = true;
        public override void Visit(SaveTransactionStatement n) => a.TransactionControl = true;
        public override void Visit(OpenQueryTableReference n) => a.External.Add("OPENQUERY");
        public override void Visit(OpenRowsetTableReference n) => a.External.Add("OPENROWSET");
        public override void Visit(AdHocTableReference n) => a.External.Add("OPENDATASOURCE");
        public override void Visit(BulkOpenRowset n) => a.External.Add("OPENROWSET");
        public override void Visit(BulkInsertStatement n) => a.External.Add("BULK_INSERT");

        public override void Visit(NamedTableReference n)
        {
            var s = n.SchemaObject;
            if (s.ServerIdentifier != null) a.External.Add("4-part:" + SqlSyntax.Name(s));
            if (string.Equals(s.DatabaseIdentifier?.Value, "msdb", StringComparison.OrdinalIgnoreCase)) a.External.Add("msdb:" + SqlSyntax.Name(s));
        }

        public override void Visit(ExecuteSpecification n)
        {
            if (n.LinkedServer != null) a.External.Add("linked-server:" + n.LinkedServer.Value);
            switch (n.ExecutableEntity)
            {
                case ExecutableStringList:
                    a.DynamicSql = true;
                    break;
                case ExecutableProcedureReference pr:
                    var pn = pr.ProcedureReference.ProcedureReference?.Name;
                    if (pn == null) { a.DynamicSql = true; break; } // EXEC @procvar
                    var last = pn.BaseIdentifier.Value;
                    if (last.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase)) { a.DynamicSql = true; break; }
                    a.ProcedureCalls.Add(SqlSyntax.Name(pn));
                    a.Calls.Add((SqlSyntax.Name(pn), pn));
                    if (last.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)) a.External.Add("xp_:" + SqlSyntax.Name(pn));
                    if (last.StartsWith("sp_OA", StringComparison.OrdinalIgnoreCase)) a.External.Add("sp_oa:" + SqlSyntax.Name(pn));
                    if (pn.ServerIdentifier != null) a.External.Add("4-part:" + SqlSyntax.Name(pn));
                    if (string.Equals(pn.DatabaseIdentifier?.Value, "msdb", StringComparison.OrdinalIgnoreCase)) a.External.Add("msdb:" + SqlSyntax.Name(pn));
                    break;
            }
        }

        void Write(TSqlFragment spec, TableReference? target, FromClause? from)
        {
            if (target is not NamedTableReference nt) return; // 변수(@t)·기타
            var resolved = SqlSyntax.ResolveAlias(nt, from);
            var tables = AddTarget(resolved, [], out var catalogTable);
            if (spec is InsertSpecification or UpdateSpecification or MergeSpecification && tables.Count > 0)
            {
                // 비결정 값이 정해지는 (테이블, 열): 대상이 하나로 정해질 때만(CTE로 여러 개 후보면 열 귀속을 알 수 없다)
                if (tables.Count == 1)
                {
                    var display = tables[0];
                    var ct = catalogTable ?? FindTable(display);
                    var known = ct is null ? null : a.Catalog.Columns.Where(c => c.ObjectId == ct.ObjectId).Select(c => c.Name).ToList();
                    foreach (var col in SqlSyntax.NondeterministicColumns(spec, () => SqlSyntax.InsertableColumns(a.Catalog, ct)))
                        a.NondeterministicWrites.Add(new TableColumn { Table = display, Column = known?.FirstOrDefault(k => k.Equals(col, StringComparison.OrdinalIgnoreCase)) ?? col });
                }
            }
        }

        CatalogTable? FindTable(string display)
        {
            var i = display.IndexOf('.');
            return i < 0 ? null : a.Resolver.Find(display[..i], display[(i + 1)..]);
        }

        /// <summary>쓰기 대상 하나를 해석해 WriteTargets에 넣고, 넣은 이름들을 돌려준다(CTE면 정의 안의 기반 테이블들).</summary>
        List<string> AddTarget(NamedTableReference nt, HashSet<string> visitedCtes, out CatalogTable? catalogTable)
        {
            catalogTable = null;
            var so = nt.SchemaObject;
            var baseName = so.BaseIdentifier.Value;
            if (baseName.StartsWith('#') || baseName.StartsWith('@')) return [];
            if (so.SchemaIdentifier == null && so.DatabaseIdentifier == null)
            {
                if (baseName.Equals("inserted", StringComparison.OrdinalIgnoreCase) || baseName.Equals("deleted", StringComparison.OrdinalIgnoreCase)) return [];
                if (a.CteDefinitions.TryGetValue(baseName, out var defs))
                {
                    var added = new List<string>();
                    if (!visitedCtes.Add(baseName)) return added;
                    foreach (var def in defs)
                    {
                        var refs = new NamedReferenceCollector();
                        def.Accept(refs);
                        foreach (var r in refs.Refs)
                            foreach (var name in AddTarget(r, visitedCtes, out _)) if (!added.Contains(name)) added.Add(name);
                    }
                    return added;
                }
            }
            var (display, table) = a.Resolver.Resolve(so, a.TriggerSchema, "dbo");
            catalogTable = table;
            a.WriteTargets.Add(display);
            return [display];
        }
    }

    sealed class NamedReferenceCollector : TSqlFragmentVisitor
    {
        public readonly List<NamedTableReference> Refs = [];
        public override void Visit(NamedTableReference n) => Refs.Add(n);
    }
}
