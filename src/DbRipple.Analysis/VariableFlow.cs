using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

/// <summary>
/// 비결정 "원천 식" 판정과 같은 본문 안 단순 대입 추적(2026-11-17, PR #11 검토에서 미룬 것, PR #15 검토로 좁힘). 판정이 불확실하면 원천으로 보지 않는다(엄격 비교 쪽).
/// <para>
/// 원천 식은 순수해야 한다: 잎이 비결정 함수(반환 값 판정에서는 IDENTITY 함수도), 숫자 리터럴, 원천 변수뿐이고, 감싸는 것은 괄호·CAST·CONVERT·단항/이항 산술,
/// 인자가 모두 순수한 스칼라 내장 함수(DATEPART·DATEADD·DATEDIFF·CHECKSUM 등, <see cref="PureFunctions"/>)뿐이다. 원천 잎이 하나 이상 있어야 한다.
/// 하위 쿼리·열 참조·원천 아닌 변수·매개변수·@@ROWCOUNT 같은 전역 변수, CASE·COALESCE·ISNULL·IIF·NULLIF·CHOOSE, 사용자 함수가 하나라도 있으면 원천이 아니다.
/// </para>
/// <list type="bullet">
/// <item>추적하는 대입: 직선 위치(본문 최상위와 그 안의 BEGIN…END)의 <c>SET @v = 식</c>, <c>SELECT @v = 식</c>, <c>DECLARE @v … = 식</c>, <c>EXEC @v = 프로시저</c>(호출자가 안쪽 판정을 준다).</item>
/// <item>추적하지 않는 대입(그 변수는 원천이 아님): IF·WHILE·TRY/CATCH 안 대입, OUTPUT 매개변수, FETCH INTO, UPDATE·MERGE의 <c>SET @v = …</c>, 커서·UDT 대입, 매개변수 대입. GOTO가 있으면 아무 대입도 추적하지 않는다.</item>
/// </list>
/// 변수는 대입이 모두 추적한 대입이고 그 값이 모두 원천 식일 때만 원천 변수다(흐름 무관). 첫 대입 전 값은 NULL이라 미리보기·원본이 같다.
/// <c>EXEC @v = 안쪽</c>으로 받은 변수(와 그것을 그대로 옮긴 변수)는 안쪽 리터럴 반환 값이 변하지 않게 식 전체가 그 변수일 때만 원천이다.
/// </summary>
internal sealed class VariableFlow
{
    /// <summary>IDENTITY 함수(반환 값 판정에서만 원천).</summary>
    static readonly HashSet<string> IdentityFunctions = new(["SCOPE_IDENTITY", "IDENT_CURRENT"], StringComparer.OrdinalIgnoreCase);

    /// <summary>인자가 순수하면 결과도 순수한 스칼라 내장 함수.</summary>
    static readonly HashSet<string> PureFunctions = new(
        ["DATEPART", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATETRUNC", "YEAR", "MONTH", "DAY", "EOMONTH",
         "ABS", "FLOOR", "CEILING", "ROUND", "SIGN", "CHECKSUM", "SWITCHOFFSET", "TODATETIMEOFFSET"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>첫 인자가 날짜 부분 이름(ms, hour 등)인 함수.</summary>
    static readonly HashSet<string> DatePartFunctions = new(["DATEPART", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATETRUNC"], StringComparer.OrdinalIgnoreCase);

    /// <summary>변수 없이 식만 보는 판정기(사용자 문장의 직접 대입 등).</summary>
    public static readonly VariableFlow Empty = new(false, [], []);

    readonly bool identity;
    readonly HashSet<string> sources;
    readonly HashSet<string> directOnly;

    VariableFlow(bool identity, HashSet<string> sources, HashSet<string> directOnly)
    {
        this.identity = identity;
        this.sources = sources;
        this.directOnly = directOnly;
    }

    /// <summary>원천에서만 값을 받는 변수 이름(@ 포함, 대소문자 무시).</summary>
    public IReadOnlySet<string> SourceVariables => sources;

    /// <summary>
    /// 본문을 분석한다. identity면 IDENTITY 함수도 원천(반환 값 판정), parameters는 매개변수 이름(입력 값이 있어 원천으로 보지 않는다),
    /// execReturn은 <c>EXEC @v = 프로시저</c>의 반환 값이 원천인지(없거나 false면 아니다).
    /// </summary>
    public static VariableFlow Analyze(
        TSqlFragment body, bool identity, IEnumerable<string>? parameters = null, Func<ExecuteSpecification, bool>? execReturn = null)
    {
        var all = new AssignmentFinder();
        body.Accept(all);
        var tracked = new List<Assignment>();
        if (all.Gotos == 0) Straight(body, tracked, execReturn);
        var trackedSites = new HashSet<TSqlFragment>(tracked.Select(t => t.Site), ReferenceEqualityComparer.Instance);

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, site) in all.Sites)
            if (!trackedSites.Contains(site)) excluded.Add(name);
        excluded.UnionWith(parameters ?? []);

        // 최소 고정점: 대입이 모두 원천 식(자기 참조는 다른 원천 대입이 하나 있을 때만 인정)이면 원천 변수.
        // EXEC 반환 값이나 그것을 그대로 옮긴 값을 받는 변수는 directOnly(식 전체가 그 변수일 때만 원천).
        var byVar = tracked.Where(t => !excluded.Contains(t.Variable)).GroupBy(t => t.Variable, StringComparer.OrdinalIgnoreCase).ToList();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var g in byVar)
            {
                if (sources.Contains(g.Key)) continue;
                var flow = new VariableFlow(identity, sources, directOnly);
                var withSelf = new VariableFlow(identity, new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase) { g.Key }, directOnly);
                if (!g.All(a => withSelf.Value(a)) || !g.Any(a => flow.Value(a))) continue;
                sources.Add(g.Key);
                if (g.Any(a => a.Fixed == true || (a.Value is not null && DirectVariable(a.Value) is { } v && directOnly.Contains(v)))) directOnly.Add(g.Key);
                changed = true;
            }
        } while (changed);
        return new VariableFlow(identity, sources, directOnly);
    }

    /// <summary>식이 순수한 원천 식인가(클래스 설명의 규칙).</summary>
    public bool IsSource(ScalarExpression expression)
    {
        if (DirectVariable(expression) is { } v) return sources.Contains(v);
        var hasSource = false;
        return Pure(expression, ref hasSource) && hasSource;
    }

    bool Value(Assignment a) => a.Fixed ?? (a.Value is not null && (a.Compound
        ? (sources.Contains(a.Variable) && !directOnly.Contains(a.Variable) && PureOrLiteral(a.Value)) || (IsSource(a.Value) && !directOnly.Contains(a.Variable))
        : IsSource(a.Value)));

    bool PureOrLiteral(ScalarExpression e)
    {
        var hasSource = false;
        return Pure(e, ref hasSource);
    }

    /// <summary>괄호를 벗긴 식이 변수 하나면 그 이름.</summary>
    static string? DirectVariable(ScalarExpression e) => e switch
    {
        ParenthesisExpression p => DirectVariable(p.Expression),
        VariableReference v => v.Name,
        _ => null,
    };

    bool Pure(ScalarExpression e, ref bool hasSource, bool argument = false)
    {
        switch (e)
        {
            case ParenthesisExpression p:
                return Pure(p.Expression, ref hasSource);
            case IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral:
                return true;
            case StringLiteral:
                return argument;   // 함수 인자(형식 문자열, IDENT_CURRENT 테이블 이름 등)로만
            case VariableReference v:
                if (!sources.Contains(v.Name) || directOnly.Contains(v.Name)) return false;
                hasSource = true;
                return true;
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive or UnaryExpressionType.Negative, Expression: { } inner }:
                return Pure(inner, ref hasSource);
            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add or BinaryExpressionType.Subtract or BinaryExpressionType.Multiply
                    or BinaryExpressionType.Divide or BinaryExpressionType.Modulo } b:
                return Pure(b.FirstExpression, ref hasSource) && Pure(b.SecondExpression, ref hasSource);
            case CastCall c:
                return Pure(c.Parameter, ref hasSource);
            case ConvertCall c:
                return Pure(c.Parameter, ref hasSource) && (c.Style is null || Pure(c.Style, ref hasSource, argument: true));
            case ParameterlessCall pc:
                // CURRENT_TIMESTAMP, CURRENT_USER 등(SqlSyntax.FindNondeterminism과 같은 목록)
                if (SqlSyntax.FindNondeterminism(pc).Count == 0) return false;
                hasSource = true;
                return true;
            case GlobalVariableExpression g:
                if (!(SqlSyntax.EnvironmentValues.Contains(g.Name, StringComparer.OrdinalIgnoreCase) || (identity && g.Name.Equals("@@IDENTITY", StringComparison.OrdinalIgnoreCase)))) return false;
                hasSource = true;
                return true;
            case NextValueForExpression { OverClause: null }:
                hasSource = true;
                return true;
            case FunctionCall f when f.CallTarget is null && f.OverClause is null && f.WithinGroupClause is null && f.UniqueRowFilter == UniqueRowFilter.NotSpecified:
                var name = f.FunctionName.Value;
                var isSource = SqlSyntax.FindNondeterminism(f).Any(h => ReferenceEquals(h.Node, f)) || (identity && IdentityFunctions.Contains(name));
                if (!isSource && !PureFunctions.Contains(name)) return false;
                for (var i = 0; i < f.Parameters.Count; i++)
                {
                    if (i == 0 && DatePartFunctions.Contains(name) && f.Parameters[0] is IdentifierLiteral) continue;   // 날짜 부분 이름(ms, hour 등)
                    if (!Pure(f.Parameters[i], ref hasSource, argument: true)) return false;
                }
                hasSource |= isSource;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Site = 대입 구문 노드(추적 여부를 참조로 맞춘다). Fixed = 값이 식이 아니라 정해짐(EXEC @v =, 산술이 아닌 복합 대입). Compound = +=처럼 자기 값을 읽음.</summary>
    sealed record Assignment(string Variable, TSqlFragment Site, ScalarExpression? Value, bool Compound, bool? Fixed = null);

    /// <summary>산술 복합 대입(+= -= *= /= %=)만 자기 참조 식으로 본다. 비트 연산 복합 대입은 원천이 아니다.</summary>
    static Assignment Assign(string variable, TSqlFragment site, ScalarExpression value, AssignmentKind kind) => kind switch
    {
        AssignmentKind.Equals => new(variable, site, value, false),
        AssignmentKind.AddEquals or AssignmentKind.SubtractEquals or AssignmentKind.MultiplyEquals or AssignmentKind.DivideEquals or AssignmentKind.ModEquals
            => new(variable, site, value, true),
        _ => new(variable, site, value, false, Fixed: false),
    };

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
                    result.Add(Assign(set.Variable.Name, set, e, set.AssignmentKind));
                    break;
                case SelectStatement { QueryExpression: QuerySpecification q }:
                    foreach (var sv in q.SelectElements.OfType<SelectSetVariable>())
                        result.Add(Assign(sv.Variable.Name, sv, sv.Expression, sv.AssignmentKind));
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
}
