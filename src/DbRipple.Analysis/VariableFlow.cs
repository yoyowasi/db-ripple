using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

/// <summary>
/// 같은 본문 안 단순 대입 추적(2026-11-17, PR #11 검토에서 미룬 것). 변수 값이 "원천"(비결정 함수, 반환 값 판정에서는 IDENTITY 함수도)에서만 오는지 본다.
/// <list type="bullet">
/// <item>추적하는 대입: 직선 위치(본문 최상위와 그 안의 BEGIN…END)의 <c>SET @v = 식</c>, <c>SELECT @v = 식</c>, <c>DECLARE @v … = 식</c>, <c>EXEC @v = 프로시저</c>(값은 호출자가 정한다).</item>
/// <item>추적하지 않는 대입(그 변수는 미상): IF·WHILE·TRY/CATCH 안 대입, OUTPUT 매개변수, FETCH INTO, UPDATE·MERGE의 <c>SET @v = …</c>, 커서·UDT 대입, 매개변수 대입. GOTO가 있으면 아무 대입도 추적하지 않는다.</item>
/// </list>
/// 변수는 대입이 모두 추적한 대입이고 그 값이 모두 원천에서 올 때만 원천 변수다(흐름 무관). 첫 대입 전 값은 NULL이라 미리보기·원본이 같다.
/// 판정이 불확실하면 원천으로 보지 않는다(엄격 비교 쪽).
/// </summary>
internal sealed class VariableFlow
{
    /// <summary>식 판정: 원천 값이 섞임, 원천과 무관함, 알 수 없음(추적하지 않은 대입이 있는 변수).</summary>
    public enum Kind { Clean, Source, Unknown }

    readonly Func<TSqlFragment, bool> isSource;
    readonly HashSet<string> sources;
    readonly HashSet<string> unknown;

    VariableFlow(Func<TSqlFragment, bool> isSource, HashSet<string> sources, HashSet<string> unknown)
    {
        this.isSource = isSource;
        this.sources = sources;
        this.unknown = unknown;
    }

    /// <summary>원천에서만 값을 받는 변수 이름(@ 포함, 대소문자 무시).</summary>
    public IReadOnlySet<string> SourceVariables => sources;

    /// <summary>
    /// 본문을 분석한다. isSource는 식 조각 안에 원천 함수가 있는지, parameters는 매개변수 이름(입력 값이 있어 원천으로 보지 않는다),
    /// execReturn은 <c>EXEC @v = 프로시저</c>의 반환 값이 원천인지(없으면 아니다).
    /// </summary>
    public static VariableFlow Analyze(
        TSqlFragment body, Func<TSqlFragment, bool> isSource, IEnumerable<string>? parameters = null, Func<ExecuteSpecification, bool>? execReturn = null)
    {
        var all = new AssignmentFinder();
        body.Accept(all);
        var tracked = new List<Assignment>();
        if (all.Gotos == 0) Straight(body, tracked, execReturn);
        var trackedSites = new HashSet<TSqlFragment>(tracked.Select(t => t.Site), ReferenceEqualityComparer.Instance);

        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, site) in all.Sites)
            if (!trackedSites.Contains(site)) unknown.Add(name);
        var paramSet = new HashSet<string>(parameters ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracked) if (paramSet.Contains(t.Variable)) unknown.Add(t.Variable);

        // 최소 고정점: 대입이 모두 원천(자기 참조는 다른 원천 대입이 하나 있을 때만 인정)이면 원천 변수.
        var byVar = tracked.Where(t => !unknown.Contains(t.Variable)).GroupBy(t => t.Variable, StringComparer.OrdinalIgnoreCase).ToList();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var g in byVar)
            {
                if (sources.Contains(g.Key)) continue;
                var withSelf = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase) { g.Key };
                if (g.All(a => Value(a, withSelf, isSource)) && g.Any(a => Value(a, sources, isSource)))
                {
                    sources.Add(g.Key);
                    changed = true;
                }
            }
        } while (changed);
        return new VariableFlow(isSource, sources, unknown);
    }

    /// <summary>식 판정. 원천 함수나 원천 변수를 쓰면 Source(미상 변수가 같이 있어도 값은 원천에 기댄다), 아니면 미상 변수를 쓰면 Unknown, 그 밖은 Clean.</summary>
    public Kind Classify(TSqlFragment expression)
    {
        if (isSource(expression)) return Kind.Source;
        var refs = Variables(expression);
        if (refs.Any(sources.Contains)) return Kind.Source;
        return refs.Any(unknown.Contains) ? Kind.Unknown : Kind.Clean;
    }

    /// <summary>조각이 원천 변수를 읽는가.</summary>
    public bool ReadsSource(TSqlFragment fragment) => sources.Count > 0 && Variables(fragment).Any(sources.Contains);

    static bool Value(Assignment a, HashSet<string> sources, Func<TSqlFragment, bool> isSource) =>
        a.Fixed ?? ((a.Value is not null && (isSource(a.Value) || Variables(a.Value).Any(sources.Contains))) || (a.Compound && sources.Contains(a.Variable)));

    static List<string> Variables(TSqlFragment f)
    {
        var v = new VariableCollector();
        f.Accept(v);
        return v.Names;
    }

    /// <summary>Site = 대입 구문 노드(추적 여부를 참조로 맞춘다). Fixed = 값이 식이 아니라 정해짐(EXEC @v =). Compound = +=처럼 자기 값을 읽음.</summary>
    sealed record Assignment(string Variable, TSqlFragment Site, ScalarExpression? Value, bool Compound, bool? Fixed = null);

    /// <summary>직선 위치의 대입만 모은다. 분기·반복·TRY/CATCH 안으로는 들어가지 않는다.</summary>
    static void Straight(TSqlFragment node, List<Assignment> result, Func<ExecuteSpecification, bool>? execReturn)
    {
        IEnumerable<TSqlStatement> statements = node switch
        {
            StatementList l => l.Statements,
            BeginEndBlockStatement b => b.StatementList?.Statements ?? [],
            _ => [],
        };
        foreach (var s in statements)
        {
            switch (s)
            {
                case BeginEndBlockStatement b:
                    Straight(b, result, execReturn);
                    break;
                case SetVariableStatement { Expression: { } e, CursorDefinition: null, Identifier: null, FunctionCallExists: false } set:
                    result.Add(new(set.Variable.Name, set, e, set.AssignmentKind != AssignmentKind.Equals));
                    break;
                case SelectStatement { QueryExpression: QuerySpecification q }:
                    foreach (var sv in q.SelectElements.OfType<SelectSetVariable>())
                        result.Add(new(sv.Variable.Name, sv, sv.Expression, sv.AssignmentKind != AssignmentKind.Equals));
                    break;
                case DeclareVariableStatement d:
                    foreach (var el in d.Declarations.Where(el => el.Value is not null))
                        result.Add(new(el.VariableName.Value, el, el.Value, false));
                    break;
                case ExecuteStatement { ExecuteSpecification: { Variable: { } rc } spec }:
                    result.Add(new(rc.Name, spec, null, false, execReturn?.Invoke(spec) == true));
                    break;
            }
        }
    }

    /// <summary>본문의 모든 스칼라 변수 대입 자리(구문 노드)와 GOTO 수.</summary>
    sealed class AssignmentFinder : TSqlFragmentVisitor
    {
        public readonly List<(string Name, TSqlFragment Site)> Sites = [];
        public int Gotos;

        public override void Visit(GoToStatement node) => Gotos++;
        public override void Visit(SetVariableStatement node) => Sites.Add((node.Variable.Name, node));
        public override void Visit(SelectSetVariable node) => Sites.Add((node.Variable.Name, node));
        public override void Visit(DeclareVariableElement node)
        {
            if (node.Value is not null) Sites.Add((node.VariableName.Value, node));
        }
        public override void Visit(ExecuteSpecification node)
        {
            if (node.Variable is { } rc) Sites.Add((rc.Name, node));
        }
        public override void Visit(ExecuteParameter node)
        {
            if (node.IsOutput && node.ParameterValue is VariableReference v) Sites.Add((v.Name, node));
        }
        public override void Visit(FetchCursorStatement node)
        {
            foreach (var v in node.IntoVariables) Sites.Add((v.Name, node));
        }
        public override void Visit(AssignmentSetClause node)
        {
            if (node.Variable is { } v) Sites.Add((v.Name, node));
        }
    }

    sealed class VariableCollector : TSqlFragmentVisitor
    {
        public readonly List<string> Names = [];
        public override void Visit(VariableReference node) => Names.Add(node.Name);
    }
}
