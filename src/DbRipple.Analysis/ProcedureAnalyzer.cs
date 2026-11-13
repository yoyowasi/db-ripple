using DbRipple.Contracts;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

/// <summary>EXEC 판정에 쓰는 사실 하나(규칙 ID와 설명).</summary>
public sealed record ProcedureFinding(string RuleId, string Detail);

/// <summary>프로시저 정의의 매개변수 하나.</summary>
public sealed record ProcedureParameterInfo(string Name, bool HasDefault, bool IsOutput, bool IsReadOnly);

/// <summary>
/// 저장 프로시저 하나를 펼쳐 분석한 결과(2026-11-13, EXEC 미리보기). Opaque면(본문 없음·파싱 실패) 나머지는 비어 있다.
/// Resolved = 동적 SQL이 없고 안쪽 호출을 모두 펼쳤다(쓰기 대상을 다 안다).
/// </summary>
public sealed record ProcedureStaticInfo
{
    public required string Procedure { get; init; }
    public bool Opaque { get; init; }
    public bool Resolved { get; init; }
    public IReadOnlyList<ProcedureParameterInfo> Parameters { get; init; } = [];
    public IReadOnlyList<string> WriteTargets { get; init; } = [];
    public IReadOnlyList<string> ExpandedCalls { get; init; } = [];
    public IReadOnlyList<string> UnresolvedCalls { get; init; } = [];
    public IReadOnlyList<string> External { get; init; } = [];
    public IReadOnlyList<TableColumn> NondeterministicWrites { get; init; } = [];
    /// <summary>거부 사실(규칙 ID별로 묶는 것은 호출자).</summary>
    public IReadOnlyList<ProcedureFinding> Rejects { get; init; } = [];
    /// <summary>경고 사실.</summary>
    public IReadOnlyList<ProcedureFinding> Warnings { get; init; } = [];
}

/// <summary>
/// 저장 프로시저 본문 정적 분석(트리거 분석과 EXEC 미리보기가 함께 쓴다). 이름 해석: 스키마가 있으면 그대로, 없으면 부르는 모듈의 스키마, dbo 순(Q4).
/// 안쪽 호출은 깊이 <see cref="MaxProcedureDepth"/>까지 따라간다(대상 프로시저 = 깊이 1).
/// </summary>
public sealed class ProcedureAnalyzer
{
    /// <summary>펼치기 깊이 상한(트리거·EXEC 문장이 직접 부르는 프로시저가 1).</summary>
    public const int MaxProcedureDepth = 4;

    readonly ProcedureExpander expander;

    public ProcedureAnalyzer(DbCatalog catalog) : this(catalog, new TableResolver(catalog)) { }

    internal ProcedureAnalyzer(DbCatalog catalog, TableResolver resolver) => expander = new ProcedureExpander(catalog, resolver);

    /// <summary>카탈로그 사용자 프로시저 찾기(3·4부분 이름은 null).</summary>
    public CatalogModule? Find(SchemaObjectName name, string callerSchema) => expander.Index.Find(name, callerSchema);

    /// <summary>프로시저를 깊이 1부터 펼친다. 대상의 매개변수도 읽는다.</summary>
    public ProcedureStaticInfo Analyze(CatalogModule module)
    {
        var name = module.Schema + "." + module.Name;
        var body = ProcedureExpander.ParseBody(module);
        if (body is null) return new ProcedureStaticInfo { Procedure = name, Opaque = true };

        var parameters = body.Parameters.Select(p => new ProcedureParameterInfo(
            p.VariableName.Value, p.Value is not null, p.Modifier == ParameterModifier.Output, p.Modifier == ParameterModifier.ReadOnly)).ToList();
        var e = expander.ExpandModule(module, 1, []);
        var a = e.Parsed!;
        return new ProcedureStaticInfo
        {
            Procedure = name,
            Resolved = e.Resolved,
            Parameters = parameters,
            WriteTargets = a.WriteTargets.ToList(),
            ExpandedCalls = a.Modules.Where(m => !m.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList(),
            UnresolvedCalls = a.UnresolvedNames.ToList(),
            External = a.External.ToList(),
            NondeterministicWrites = a.NondeterministicWrites.OrderBy(w => w.Table, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Column, StringComparer.OrdinalIgnoreCase).ToList(),
            Rejects = a.ExecRejects.ToList(),
            Warnings = a.ExecWarnings.ToList(),
        };
    }
}

/// <summary>프로시저 본문 하나를 분석한 결과. Parsed는 본문을 읽은 경우의 누적값(못 펼쳤어도 외부 부작용·트랜잭션 제어는 합친다), Resolved는 쓰기 대상을 다 안다는 뜻.</summary>
/// PathDependent = 하위 어딘가에서 재귀(호출 경로 스택)에 걸려 결과가 경로에 따라 다르다(메모하지 않는다).
internal readonly record struct Expansion(bool Resolved, Accumulator? Parsed, bool PathDependent = false);

/// <summary>
/// 카탈로그 프로시저 이름 색인. 키는 (스키마, 이름) 쌍이다(문자열로 이으면 "a.b"+"c"와 "a"+"b.c"가 겹친다).
/// 대소문자만 다른 이름이 여럿이면(대소문자 구분 정렬 DB) 어느 것이 불릴지 정하지 않고 찾지 못한 것으로 본다(닫힌 쪽).
/// 스키마 없는 sp_ 이름은 SQL Server가 master(sys)를 먼저 찾으므로 카탈로그 사용자 프로시저로 해석하지 않는다.
/// </summary>
internal sealed class ProcedureIndex(DbCatalog catalog)
{
    sealed class KeyComparer : IEqualityComparer<(string Schema, string Name)>
    {
        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        public int GetHashCode((string Schema, string Name) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Schema), StringComparer.OrdinalIgnoreCase.GetHashCode(k.Name));
    }

    readonly Dictionary<(string Schema, string Name), List<CatalogModule>> byName = catalog.ProcedureModules
        .GroupBy(m => (m.Schema, m.Name), new KeyComparer())
        .ToDictionary(g => g.Key, g => g.ToList(), new KeyComparer());

    /// <summary>스키마 없는 sp_ 이름(시스템 프로시저가 먼저 해석됨).</summary>
    public static bool IsUnqualifiedSystemName(SchemaObjectName n) =>
        n.SchemaIdentifier == null && n.DatabaseIdentifier == null && n.ServerIdentifier == null
        && n.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase);

    public CatalogModule? Find(SchemaObjectName n, string callerSchema)
    {
        if (n.ServerIdentifier != null || n.DatabaseIdentifier != null) return null; // 다른 DB·서버(현재 DB 이름이어도 확인하지 않음, 닫힌 쪽)
        if (IsUnqualifiedSystemName(n)) return null;
        var name = n.BaseIdentifier.Value;
        if (n.SchemaIdentifier != null) return One(n.SchemaIdentifier.Value, name);
        if (byName.ContainsKey((callerSchema, name))) return One(callerSchema, name); // 부르는 스키마에 있으면(모호해도) dbo로 넘어가지 않는다
        return One("dbo", name);
    }

    CatalogModule? One(string schema, string name) => byName.TryGetValue((schema, name), out var l) && l.Count == 1 ? l[0] : null;
}

/// <summary>
/// 프로시저 펼치기(인계 1). 결과는 (모듈, 깊이)로 메모한다(검토 2026-11-13: 메모 없이 호출 수의 거듭제곱). 깊이 상한 판정은 깊이로만 정해지고,
/// 재귀(스택) 판정은 경로에 따라 다르므로 하위에서 재귀를 만난 결과는 메모하지 않는다. 메모한 Accumulator는 읽기만 한다(Merge는 받는 쪽만 바꾼다).
/// </summary>
internal sealed class ProcedureExpander(DbCatalog catalog, TableResolver resolver)
{
    public readonly ProcedureIndex Index = new(catalog);
    readonly Dictionary<(int ObjectId, int Depth), Expansion> memo = [];

    public static ProcedureStatementBody? ParseBody(CatalogModule module)
    {
        if (module.Definition is null) return null;
        TSqlFragment? fragment;
        IList<ParseError> errors;
        try { fragment = SqlSyntax.Parse(module.Definition, out errors); }
        catch (Exception) { return null; }
        if (fragment is null || errors.Count > 0) return null;
        var finder = new ProcedureFinder();
        fragment.Accept(finder);
        return finder.Statements.Count == 1 && finder.Statements[0].StatementList is not null ? finder.Statements[0] : null;
    }

    public Expansion Expand(SchemaObjectName call, string callerSchema, int depth, HashSet<int> stack)
    {
        var module = Index.Find(call, callerSchema);
        if (module is null) return new(false, null);
        return ExpandModule(module, depth, stack);
    }

    public Expansion ExpandModule(CatalogModule module, int depth, HashSet<int> stack)
    {
        if (depth > ProcedureAnalyzer.MaxProcedureDepth) return new(false, null);
        if (stack.Contains(module.ObjectId)) return new(false, null, PathDependent: true);
        if (memo.TryGetValue((module.ObjectId, depth), out var hit)) return hit;
        if (ParseBody(module) is not { } proc) return memo[(module.ObjectId, depth)] = new(false, null);

        var name = module.Schema + "." + module.Name;
        var a = new Accumulator(module.Schema, resolver, catalog, Index);
        a.Modules.Add(name);
        var body = proc.StatementList;
        body.Accept(new CtePrepass(a.CteDefinitions));
        body.Accept(new BodyVisitor(a));
        var rules = new ExecRulesVisitor(a, name);
        body.Accept(rules);
        rules.Finish(body);

        // (d) WITH EXECUTE AS OWNER·SELF·사용자: 실행 사용자 권한 밖에서 돈다(CALLER만 허용).
        foreach (var o in proc.Options.OfType<ExecuteAsProcedureOption>())
            if (o.ExecuteAs.ExecuteAsOption != ExecuteAsOption.Caller)
                a.ExecRejects.Add(new(AnalysisRules.RejectProcExecuteAs, $"{name}: WITH EXECUTE AS {o.ExecuteAs.ExecuteAsOption.ToString().ToUpperInvariant()}"));
        if (proc.ProcedureReference?.Number is not null)
            a.ExecRejects.Add(new(AnalysisRules.RejectProcCallForm, $"{name}: 번호 프로시저(;n)"));
        if (a.DynamicSql)
            a.ExecRejects.Add(new(AnalysisRules.RejectProcDynamicSql, name));

        var resolved = !a.DynamicSql;
        var pathDependent = false;
        stack.Add(module.ObjectId);
        foreach (var (callName, node) in a.Calls.DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var inner = Expand(node, module.Schema, depth + 1, stack);
            pathDependent |= inner.PathDependent;
            if (inner.Parsed is { } p) a.Merge(p);
            if (!inner.Resolved)
            {
                var found = Index.Find(node, module.Schema);
                a.UnresolvedNames.Add(found is null ? callName : found.Schema + "." + found.Name);
            }
            resolved &= inner.Resolved;
        }
        stack.Remove(module.ObjectId);
        var result = new Expansion(resolved, a, pathDependent);
        if (!pathDependent) memo[(module.ObjectId, depth)] = result;
        return result;
    }
}

internal sealed class ProcedureFinder : TSqlFragmentVisitor
{
    public readonly List<ProcedureStatementBody> Statements = [];
    public override void Visit(CreateProcedureStatement node) => Statements.Add(node);
    public override void Visit(AlterProcedureStatement node) => Statements.Add(node);
    public override void Visit(CreateOrAlterProcedureStatement node) => Statements.Add(node);
}

internal sealed class Accumulator(string moduleSchema, TableResolver resolver, DbCatalog catalog, ProcedureIndex procedures)
{
    public readonly string TriggerSchema = moduleSchema;
    public readonly TableResolver Resolver = resolver;
    public readonly DbCatalog Catalog = catalog;
    public readonly ProcedureIndex Procedures = procedures;
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

    // ---- EXEC 미리보기 전용(트리거 분석은 쓰지 않는다) ----
    /// <summary>본문을 읽은 프로시저 "스키마.이름"(자신 포함).</summary>
    public readonly SortedSet<string> Modules = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>펼치지 못한 안쪽 호출.</summary>
    public readonly SortedSet<string> UnresolvedNames = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<ProcedureFinding> ExecRejects = [];
    public readonly List<ProcedureFinding> ExecWarnings = [];

    /// <summary>
    /// 펼친(또는 본문만 읽은) 프로시저 결과를 합친다: 쓰기 대상, 비결정 쓰기, 외부 부작용, ROLLBACK, 트랜잭션 제어(+ EXEC 전용 사실).
    /// 동적 SQL·프로시저 호출 이름은 합치지 않는다(트리거 본문 속성이고, 프로시저 쪽 미상은 UnresolvedProcedureCalls로 나타낸다).
    /// </summary>
    public void Merge(Accumulator p)
    {
        WriteTargets.UnionWith(p.WriteTargets);
        NondeterministicWrites.UnionWith(p.NondeterministicWrites);
        External.UnionWith(p.External);
        Rollback |= p.Rollback;
        TransactionControl |= p.TransactionControl;
        Modules.UnionWith(p.Modules);
        UnresolvedNames.UnionWith(p.UnresolvedNames);
        foreach (var f in p.ExecRejects) if (!ExecRejects.Contains(f)) ExecRejects.Add(f);
        foreach (var f in p.ExecWarnings) if (!ExecWarnings.Contains(f)) ExecWarnings.Add(f);
    }
}

internal sealed class CtePrepass(Dictionary<string, List<QueryExpression>> defs) : TSqlFragmentVisitor
{
    public override void Visit(CommonTableExpression node)
    {
        if (!defs.TryGetValue(node.ExpressionName.Value, out var list)) defs[node.ExpressionName.Value] = list = [];
        list.Add(node.QueryExpression);
    }
}

internal sealed class NamedReferenceCollector : TSqlFragmentVisitor
{
    public readonly List<NamedTableReference> Refs = [];
    public override void Visit(NamedTableReference n) => Refs.Add(n);
}

/// <summary>쓰기 대상·외부 부작용·트랜잭션 제어·프로시저 호출을 모은다(트리거 본문과 프로시저 본문 공통).</summary>
internal sealed class BodyVisitor(Accumulator a) : TSqlFragmentVisitor
{
    /// <summary>동적 SQL 진입점(이름 그대로 실행할 문자열·핸들을 받는 시스템 프로시저).</summary>
    static readonly HashSet<string> DynamicEntryPoints = new(
        ["sp_executesql", "sp_prepare", "sp_prepexec", "sp_prepexecrpc", "sp_execute", "sp_unprepare",
         "sp_cursoropen", "sp_cursorprepare", "sp_cursorprepexec", "sp_cursorexecute"],
        StringComparer.OrdinalIgnoreCase);

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

    // (b) Service Broker: 큐·대화는 트랜잭션 밖 상태와 엮이고 원본 서비스로 메시지를 보낼 수 있다.
    public override void Visit(BeginDialogStatement n) => a.External.Add("service-broker:BEGIN_DIALOG");
    public override void Visit(BeginConversationTimerStatement n) => a.External.Add("service-broker:BEGIN_CONVERSATION_TIMER");
    public override void Visit(SendStatement n) => a.External.Add("service-broker:SEND");
    public override void Visit(ReceiveStatement n) => a.External.Add("service-broker:RECEIVE");
    public override void Visit(EndConversationStatement n) => a.External.Add("service-broker:END_CONVERSATION");
    public override void Visit(GetConversationGroupStatement n) => a.External.Add("service-broker:GET_CONVERSATION_GROUP");
    public override void Visit(MoveConversationStatement n) => a.External.Add("service-broker:MOVE_CONVERSATION");

    public override void Visit(NamedTableReference n) => ExternalName(n.SchemaObject);
    public override void Visit(SchemaObjectFunctionTableReference n) => ExternalName(n.SchemaObject);

    /// <summary>4부분(링크드 서버), msdb, (a) 그 밖의 다른 DB 3부분 이름(tempdb 제외)을 외부 부작용으로 표시한다.</summary>
    void ExternalName(SchemaObjectName s)
    {
        if (s.ServerIdentifier != null) { a.External.Add("4-part:" + SqlSyntax.Name(s)); return; }
        var db = s.DatabaseIdentifier?.Value;
        if (db is null || db.Equals("tempdb", StringComparison.OrdinalIgnoreCase)) return;
        a.External.Add((db.Equals("msdb", StringComparison.OrdinalIgnoreCase) ? "msdb:" : "3-part:") + SqlSyntax.Name(s));
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
                if (DynamicEntryPoints.Contains(last)) { a.DynamicSql = true; break; }
                var name = SqlSyntax.Name(pn);
                a.ProcedureCalls.Add(name);
                a.Calls.Add((name, pn));
                ExternalName(pn);
                if (last.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)) a.External.Add("xp_:" + name);
                else if (last.StartsWith("sp_OA", StringComparison.OrdinalIgnoreCase)) a.External.Add("sp_oa:" + name);
                else if (IsSystemLike(pn) && a.Procedures.Find(pn, a.TriggerSchema) is null)
                    a.External.Add("system-proc:" + name); // (c) 허용 목록 방식: 카탈로그 사용자 프로시저가 아닌 시스템 프로시저는 부작용을 모른다
                break;
        }
    }

    static bool IsSystemLike(SchemaObjectName n) =>
        n.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.DatabaseIdentifier?.Value, "master", StringComparison.OrdinalIgnoreCase);

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

/// <summary>
/// 트랜잭션 구조 검사(검토 2026-11-13). COMMIT은 그 지점에서 "확실히 열린" BEGIN TRAN이 있어야 한다:
/// 같은 블록이나 감싼 블록에서 앞서 실행된 BEGIN만 센다. IF/ELSE는 두 갈래 중 작은 쪽, WHILE 본문 안 BEGIN은 밖에서 세지 않고
/// 본문이 열린 수를 줄이면(반복하면 도구 트랜잭션에 닿음) 거부, CATCH는 TRY 앞과 TRY 안 모든 지점 중 가장 작은 값에서 시작, ROLLBACK은 0, RETURN·THROW 뒤는 도달 불가.
/// CATCH 안 "IF XACT_STATE() = 1 COMMIT"처럼 TRY 안에서 연 트랜잭션을 CATCH에서 확정하는 모양도 거부된다(닫힌 쪽, SP16 뒤 재검토).
/// </summary>
internal static class TranFlow
{
    /// <summary>End = 끝에서 확실히 열린 수(null = 도달 불가), Min = 지나온 모든 지점의 가장 작은 값.</summary>
    readonly record struct Flow(int? End, int Min);

    public static List<string> Check(StatementList body)
    {
        var problems = new List<string>();
        Seq(body.Statements, 0, problems);
        return problems;
    }

    static int? MinOrNull(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    static Flow Seq(IEnumerable<TSqlStatement> statements, int? d, List<string> p)
    {
        var min = d ?? int.MaxValue;
        foreach (var s in statements)
        {
            if (d is not int cur) break;
            var f = One(s, cur, p);
            min = Math.Min(min, f.Min);
            d = f.End;
        }
        return new(d, min);
    }

    static Flow One(TSqlStatement s, int d, List<string> p)
    {
        switch (s)
        {
            case BeginTransactionStatement:
                return new(d + 1, d);
            case CommitTransactionStatement:
                if (d > 0) return new(d - 1, d - 1);
                p.Add("COMMIT 앞에 확실히 열린 BEGIN TRAN이 없음(분기·반복·TRY 안에서만 열렸거나 이미 닫힘)");
                return new(0, 0);
            case RollbackTransactionStatement { Name: null }:
                return new(0, 0);
            case ReturnStatement or ThrowStatement:
                return new(null, d);
            case BeginEndBlockStatement b:
                return Seq(b.StatementList.Statements, d, p);
            case IfStatement i:
                var then = One(i.ThenStatement, d, p);
                var other = i.ElseStatement is null ? new Flow(d, d) : One(i.ElseStatement, d, p);
                return new(MinOrNull(then.End, other.End), Math.Min(then.Min, other.Min));
            case WhileStatement w:
                var body = One(w.Statement, d, p);
                if (body.End is int end && end < d) p.Add("반복문 본문의 COMMIT이 열린 트랜잭션 수를 줄임(반복하면 도구 트랜잭션에 닿음)");
                var low = Math.Min(d, body.Min); // BREAK 지점 포함
                return new(low, low);
            case TryCatchStatement tc:
                var tr = Seq(tc.TryStatements.Statements, d, p);
                var ca = Seq(tc.CatchStatements.Statements, Math.Min(d, tr.Min), p);
                return new(MinOrNull(tr.End, ca.End), Math.Min(tr.Min, ca.Min));
            default:
                return new(d, d);
        }
    }
}

/// <summary>
/// EXEC 미리보기 본문 규칙(프로시저 모듈 하나). 허용 목록 방식: 아는 문장만 통과시키고 나머지는 거부 사실로 남긴다.
/// "성공 경로"는 CATCH 블록 밖 전부로 본다(SP16 전까지 닫힌 쪽: TRY 밖의 @@TRANCOUNT 분기도 도구 트랜잭션 때문에 원본과 갈린다).
/// </summary>
internal sealed class ExecRulesVisitor(Accumulator a, string module) : TSqlFragmentVisitor
{
    int catchDepth;
    int begins;
    int commits;
    int gotos;

    static readonly HashSet<string> Allowed =
    [
        "InsertStatement", "UpdateStatement", "DeleteStatement", "MergeStatement", "SelectStatement",
        "DeclareVariableStatement", "DeclareTableVariableStatement", "SetVariableStatement", "IfStatement", "WhileStatement",
        "BeginEndBlockStatement", "TryCatchStatement", "ReturnStatement", "GoToStatement", "LabelStatement", "BreakStatement",
        "ContinueStatement", "ThrowStatement", "RaiseErrorStatement", "PrintStatement", "ExecuteStatement",
        "BeginTransactionStatement", "CommitTransactionStatement", "RollbackTransactionStatement", "SaveTransactionStatement",
        "PredicateSetStatement", "SetTransactionIsolationLevelStatement", "SetIdentityInsertStatement", "SetRowCountStatement",
        "DeclareCursorStatement", "OpenCursorStatement", "FetchCursorStatement", "CloseCursorStatement", "DeallocateCursorStatement",
        // Service Broker는 BodyVisitor가 외부 부작용으로 표시한다(여기서 또 거부하지 않는다).
        "BeginDialogStatement", "BeginConversationTimerStatement", "SendStatement", "ReceiveStatement", "EndConversationStatement",
        "GetConversationGroupStatement", "MoveConversationStatement",
    ];

    void Reject(string rule, string detail) => a.ExecRejects.Add(new(rule, $"{module}: {detail}"));
    void Warn(string rule, string detail) => a.ExecWarnings.Add(new(rule, $"{module}: {detail}"));

    /// <summary>모듈 하나를 다 본 뒤 트랜잭션 구조를 판정한다(<see cref="TranFlow"/>, 검토 2026-11-13: 개수 비교는 분기·반복·GOTO로 우회됐다).</summary>
    public void Finish(StatementList body)
    {
        var problems = TranFlow.Check(body);
        if (gotos > 0 && commits > 0) problems.Add("GOTO가 있는 모듈의 COMMIT(흐름을 정적으로 따라갈 수 없음)");
        foreach (var p in problems.Distinct()) Reject(AnalysisRules.RejectProcUnbalancedCommit, p);
        if (problems.Count == 0 && begins > 0) Warn(AnalysisRules.WarnProcNestedTransaction, $"BEGIN TRAN {begins}개·COMMIT {commits}개(도구 트랜잭션 안의 중첩 트랜잭션)");
    }

    public override void Visit(GoToStatement node) => gotos++;

    // E11: 샌드박스의 감사 트리거 때문에 원본과 달리 실패한다(트리거가 있는 테이블의 INTO 없는 OUTPUT, 트리거가 있는 테이블로의 OUTPUT INTO).
    public override void Visit(OutputClause node) =>
        Reject(AnalysisRules.RejectProcStatementNotAllowed, "OUTPUT 절(INTO 없이 결과를 냄): 샌드박스 감사 트리거 때문에 원본과 달리 실패할 수 있음(E11)");

    public override void Visit(OutputIntoClause node)
    {
        if (node.IntoTable is NamedTableReference nt && !nt.SchemaObject.BaseIdentifier.Value.StartsWith('#'))
            Reject(AnalysisRules.RejectProcStatementNotAllowed, $"OUTPUT ... INTO 실제 테이블 {SqlSyntax.Name(nt.SchemaObject)}: 샌드박스 감사 트리거 때문에 원본과 달리 실패할 수 있음(E11)");
    }

    // 사용자 정의 함수는 카탈로그에 본문·EXECUTE AS·CLR 여부가 없어 펼치지 못한다. 프로시저 소유권 체인으로 실행되므로 닫힌 쪽으로 거부한다.
    public override void Visit(SchemaObjectFunctionTableReference node)
    {
        if (!string.Equals(node.SchemaObject.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase))
            Reject(AnalysisRules.RejectProcUnresolvedCall, $"사용자 정의 함수 {SqlSyntax.Name(node.SchemaObject)}(본문을 펼치지 않음: CLR·EXECUTE AS 함수일 수 있음)");
    }

    public override void ExplicitVisit(TryCatchStatement node)
    {
        node.TryStatements?.Accept(this);
        catchDepth++;
        node.CatchStatements?.Accept(this);
        catchDepth--;
        var raises = new RaiseFinder();
        node.CatchStatements?.Accept(raises);
        if (!raises.Found) Warn(AnalysisRules.WarnProcCatchSwallows, "CATCH가 THROW·RAISERROR 없이 오류를 삼킴");
    }

    public override void Visit(TSqlStatement node)
    {
        var type = node.GetType().Name;
        if (Allowed.Contains(type)) return;
        switch (node)
        {
            case CreateTableStatement c when c.SchemaObjectName.BaseIdentifier.Value.StartsWith('#'):
                Warn(AnalysisRules.WarnProcTempTable, "#임시 테이블 " + c.SchemaObjectName.BaseIdentifier.Value);
                return;
            case DropTableStatement d when d.Objects.All(o => o.BaseIdentifier.Value.StartsWith('#')):
                return;
            case CreateIndexStatement ci when ci.OnName.BaseIdentifier.Value.StartsWith('#'):
                return;
            case TruncateTableStatement:
                Reject(AnalysisRules.RejectProcStatementNotAllowed, "TRUNCATE(DELETE 트리거가 발동하지 않음)");
                return;
        }
        if (type.StartsWith("Create", StringComparison.Ordinal) || type.StartsWith("Alter", StringComparison.Ordinal) || type.StartsWith("Drop", StringComparison.Ordinal))
            Reject(AnalysisRules.RejectProcDdl, type.Replace("Statement", ""));
        else
            Reject(AnalysisRules.RejectProcStatementNotAllowed, type.Replace("Statement", ""));
    }

    public override void Visit(BeginTransactionStatement node)
    {
        begins++;
        if (node.Distributed) Reject(AnalysisRules.RejectProcStatementNotAllowed, "BEGIN DISTRIBUTED TRANSACTION");
    }

    public override void Visit(CommitTransactionStatement node) => commits++;
    public override void Visit(SaveTransactionStatement node) => Reject(AnalysisRules.RejectProcSavepoint, "SAVE TRANSACTION");

    public override void Visit(RollbackTransactionStatement node)
    {
        if (node.Name is not null) Reject(AnalysisRules.RejectProcSavepoint, "저장점으로 ROLLBACK");
        else Warn(AnalysisRules.WarnProcRollback, catchDepth > 0 ? "CATCH 안 ROLLBACK" : "ROLLBACK");
    }

    public override void Visit(GlobalVariableExpression node)
    {
        if (node.Name.Equals("@@TRANCOUNT", StringComparison.OrdinalIgnoreCase)) TranState("@@TRANCOUNT");
    }

    public override void Visit(FunctionCall node)
    {
        if (node.CallTarget is null && node.FunctionName.Value.Equals("XACT_STATE", StringComparison.OrdinalIgnoreCase)) TranState("XACT_STATE()");
        // 스키마로 한정한 호출 = 사용자 정의 스칼라 함수·집계(열의 xml·CLR 형식 메서드도 여기 걸린다, 닫힌 쪽)
        if (node.CallTarget is MultiPartIdentifierCallTarget t)
            Reject(AnalysisRules.RejectProcUnresolvedCall,
                $"사용자 정의 함수 {string.Join('.', t.MultiPartIdentifier.Identifiers.Select(i => i.Value))}.{node.FunctionName.Value}(본문을 펼치지 않음: CLR·EXECUTE AS 함수일 수 있음)");
    }

    // 도구 트랜잭션 안이라 성공 경로의 @@TRANCOUNT·XACT_STATE() 값은 원본과 다르다(SP16 뒤 재검토).
    void TranState(string what)
    {
        if (catchDepth > 0) Warn(AnalysisRules.WarnProcXactStateCheck, $"CATCH 안 {what} 검사");
        else Reject(AnalysisRules.RejectProcTrancountBranch, $"CATCH 밖에서 {what}를 읽음");
    }

    public override void Visit(RaiseErrorStatement node)
    {
        if ((node.RaiseErrorOptions & RaiseErrorOptions.Log) != 0) Reject(AnalysisRules.RejectProcExternalEffect, "RAISERROR ... WITH LOG(오류 로그에 씀)");
    }

    public override void Visit(CursorDefinition node) => Warn(AnalysisRules.WarnProcCursor, "커서");
    public override void Visit(DeclareTableVariableStatement node) => Warn(AnalysisRules.WarnProcTableVariable, "테이블 변수 " + node.Body.VariableName.Value);

    public override void Visit(SelectStatement node)
    {
        if (node.Into is { } into)
        {
            if (into.BaseIdentifier.Value.StartsWith('#')) Warn(AnalysisRules.WarnProcTempTable, "SELECT ... INTO " + into.BaseIdentifier.Value);
            else Reject(AnalysisRules.RejectProcDdl, "SELECT ... INTO " + SqlSyntax.Name(into));
            return;
        }
        var assignsOnly = SqlSyntax.QuerySpecifications(node.QueryExpression).All(q => q.SelectElements.All(e => e is SelectSetVariable));
        if (!assignsOnly) Warn(AnalysisRules.WarnProcResultSet, "결과 집합을 내는 SELECT");
    }

    sealed class RaiseFinder : TSqlFragmentVisitor
    {
        public bool Found;
        public override void Visit(ThrowStatement node) => Found = true;
        public override void Visit(RaiseErrorStatement node) => Found = true;
    }
}
