using DbRipple.Contracts;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

/// <summary>ScriptDom 공통 도우미(파서, 이름, 별칭, 비결정 함수, 대입 열). SP4의 TriggerAnalyzer를 바탕으로 했다.</summary>
internal static class SqlSyntax
{
    /// <summary>
    /// 파서. D1 대상은 SQL Server 2025(호환성 170)이고 허용 목록이 DML 세 종류라 160과 170 사이에 GO·위치 동작 차이는 없다(SP4).
    /// 새 구문(JSON·VECTOR 인덱스 등)은 어차피 DDL이라 거부된다. 바꾸려면 이 한 줄만 고친다.
    /// </summary>
    public static TSqlParser NewParser() => new TSql170Parser(initialQuotedIdentifiers: true);

    public static TSqlFragment? Parse(string sql, out IList<ParseError> errors)
    {
        using var reader = new StringReader(sql);
        return NewParser().Parse(reader, out errors);
    }

    /// <summary>쓰여 있는 그대로의 이름("서버.DB.스키마.이름" 중 있는 부분).</summary>
    public static string Name(SchemaObjectName n)
    {
        var parts = new List<string>(4);
        if (n.ServerIdentifier != null) parts.Add(n.ServerIdentifier.Value);
        if (n.DatabaseIdentifier != null) parts.Add(n.DatabaseIdentifier.Value);
        if (n.SchemaIdentifier != null) parts.Add(n.SchemaIdentifier.Value);
        parts.Add(n.BaseIdentifier.Value);
        return string.Join('.', parts);
    }

    /// <summary>한 부분 이름 대상이 FROM 안의 별칭이면 그 실제 테이블 참조로 푼다(SP4). 아니면 그대로.</summary>
    public static NamedTableReference ResolveAlias(NamedTableReference target, FromClause? from)
    {
        var so = target.SchemaObject;
        if (from is null || so.SchemaIdentifier != null || so.DatabaseIdentifier != null) return target;
        var finder = new AliasFinder(so.BaseIdentifier.Value);
        from.Accept(finder);
        return finder.Found ?? target;
    }

    sealed class AliasFinder(string alias) : TSqlFragmentVisitor
    {
        public NamedTableReference? Found;
        public override void Visit(NamedTableReference n)
        {
            if (Found == null && string.Equals(n.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase)) Found = n;
        }
    }

    // ---- 비결정 함수 ----

    /// <summary>
    /// 실행 환경(로그인·세션·서버·DB)에 따라 값이 달라지는 함수(2026-11-13 Split). 샌드박스(dbr_preview_user, 로컬 서버)와 원본(본인 로그인, 원본 서버)에서
    /// 값이 달라지므로 E1 비결정으로 보고 [적용] 값 대조에서 뺀다(존재·형식만 비교). 아래 NondeterministicFunctions보다 먼저 선언해야 한다(정적 초기화 순서).
    /// </summary>
    public static readonly IReadOnlyList<string> EnvironmentFunctions =
    [
        "SUSER_SNAME", "SUSER_NAME", "SUSER_ID", "SUSER_SID", "ORIGINAL_LOGIN", "HOST_NAME", "HOST_ID", "APP_NAME",
        "DB_NAME", "DB_ID", "USER_NAME", "USER_ID", "SESSION_CONTEXT", "CONTEXT_INFO",
    ];

    static readonly HashSet<string> NondeterministicFunctions = new(
        ["GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "NEWID", "NEWSEQUENTIALID", "RAND", "CRYPT_GEN_RANDOM", .. EnvironmentFunctions],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>인자 없는 호출(CURRENT_USER 등)과 전역 변수 중 환경 의존 값.</summary>
    public static readonly IReadOnlyList<string> EnvironmentValues =
        ["CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "USER", "@@SERVERNAME", "@@SERVICENAME", "@@SPID"];

    /// <summary>조각 안의 비결정 함수 호출(GETDATE 등, CURRENT_TIMESTAMP, NEXT VALUE FOR, 환경 의존 함수·값)을 찾는다.</summary>
    public static List<(string Name, TSqlFragment Node)> FindNondeterminism(TSqlFragment fragment)
    {
        var v = new NondeterminismFinder();
        fragment.Accept(v);
        return v.Hits;
    }

    sealed class NondeterminismFinder : TSqlFragmentVisitor
    {
        public readonly List<(string, TSqlFragment)> Hits = [];
        public override void Visit(FunctionCall n)
        {
            if (NondeterministicFunctions.Contains(n.FunctionName.Value)) Hits.Add((n.FunctionName.Value.ToUpperInvariant(), n));
        }
        public override void Visit(ParameterlessCall n)
        {
            switch (n.ParameterlessCallType)
            {
                case ParameterlessCallType.CurrentTimestamp: Hits.Add(("CURRENT_TIMESTAMP", n)); break;
                case ParameterlessCallType.CurrentUser: Hits.Add(("CURRENT_USER", n)); break;
                case ParameterlessCallType.SessionUser: Hits.Add(("SESSION_USER", n)); break;
                case ParameterlessCallType.SystemUser: Hits.Add(("SYSTEM_USER", n)); break;
                case ParameterlessCallType.User: Hits.Add(("USER", n)); break;
            }
        }
        public override void Visit(GlobalVariableExpression n)
        {
            if (EnvironmentValues.Contains(n.Name, StringComparer.OrdinalIgnoreCase)) Hits.Add((n.Name.ToUpperInvariant(), n));
        }
        public override void Visit(NextValueForExpression n) => Hits.Add(("NEXT VALUE FOR", n));
    }

    // ---- 대입 열(비결정 값으로 정해지는 열) ----

    /// <summary>INSERT/UPDATE/MERGE가 값을 대입하는 (열 이름, 값 식) 쌍. 열 목록이 없는 INSERT는 tableColumns(대입 가능한 열 순서)를 쓴다.</summary>
    public static IEnumerable<(string Column, ScalarExpression Value)> Assignments(TSqlFragment spec, Func<IReadOnlyList<string>?> tableColumns)
    {
        switch (spec)
        {
            case InsertSpecification ins:
                foreach (var a in InsertAssignments(ins.Columns, ins.InsertSource, tableColumns)) yield return a;
                break;
            case UpdateSpecification upd:
                foreach (var a in SetAssignments(upd.SetClauses)) yield return a;
                break;
            case MergeSpecification mrg:
                foreach (var clause in mrg.ActionClauses)
                {
                    switch (clause.Action)
                    {
                        case UpdateMergeAction u:
                            foreach (var a in SetAssignments(u.SetClauses)) yield return a;
                            break;
                        case InsertMergeAction i:
                            foreach (var a in InsertAssignments(i.Columns, i.Source, tableColumns)) yield return a;
                            break;
                    }
                }
                break;
        }
    }

    static IEnumerable<(string, ScalarExpression)> SetAssignments(IList<SetClause> clauses)
    {
        foreach (var c in clauses)
            if (c is AssignmentSetClause { Column: { } col, NewValue: { } val })
                yield return (col.MultiPartIdentifier.Identifiers[^1].Value, val);
    }

    static IEnumerable<(string, ScalarExpression)> InsertAssignments(IList<ColumnReferenceExpression> columns, InsertSource? source, Func<IReadOnlyList<string>?> tableColumns)
    {
        IReadOnlyList<string>? cols = columns.Count > 0
            ? columns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value).ToList()
            : tableColumns();
        if (cols is null) yield break;
        switch (source)
        {
            case ValuesInsertSource v:
                foreach (var row in v.RowValues)
                    for (var j = 0; j < row.ColumnValues.Count && j < cols.Count; j++)
                        yield return (cols[j], row.ColumnValues[j]);
                break;
            case SelectInsertSource s:
                foreach (var q in QuerySpecifications(s.Select))
                    for (var j = 0; j < q.SelectElements.Count && j < cols.Count; j++)
                    {
                        if (q.SelectElements[j] is not SelectScalarExpression se) break; // * 뒤로는 위치를 알 수 없다
                        yield return (cols[j], se.Expression);
                    }
                break;
        }
    }

    public static IEnumerable<QuerySpecification> QuerySpecifications(QueryExpression? q)
    {
        switch (q)
        {
            case QuerySpecification qs: yield return qs; break;
            case BinaryQueryExpression b:
                foreach (var x in QuerySpecifications(b.FirstQueryExpression)) yield return x;
                foreach (var x in QuerySpecifications(b.SecondQueryExpression)) yield return x;
                break;
            case QueryParenthesisExpression p:
                foreach (var x in QuerySpecifications(p.QueryExpression)) yield return x;
                break;
        }
    }

    /// <summary>값이 비결정 함수를 포함하는 대입의 열 이름(중복 없음, 등장 순서).</summary>
    public static List<string> NondeterministicColumns(TSqlFragment spec, Func<IReadOnlyList<string>?> tableColumns)
    {
        var result = new List<string>();
        foreach (var (column, value) in Assignments(spec, tableColumns))
            if (FindNondeterminism(value).Count > 0 && !result.Contains(column, StringComparer.OrdinalIgnoreCase))
                result.Add(column);
        return result;
    }

    /// <summary>열 목록이 없는 INSERT가 값을 받는 열 순서(계산·rowversion·IDENTITY 열은 값을 받지 않는다).</summary>
    public static IReadOnlyList<string>? InsertableColumns(DbCatalog catalog, CatalogTable? table) =>
        table is null ? null
            : catalog.Columns.Where(c => c.ObjectId == table.ObjectId && !c.IsComputed && !c.IsRowVersion && !c.IsIdentity)
                .OrderBy(c => c.ColumnId).Select(c => c.Name).ToList();
}

/// <summary>카탈로그 테이블 이름 해석(대소문자 무시, 정규 철자 반환).</summary>
internal sealed class TableResolver
{
    readonly Dictionary<string, CatalogTable> byName = new(StringComparer.OrdinalIgnoreCase);

    public TableResolver(DbCatalog catalog)
    {
        foreach (var t in catalog.Tables) byName.TryAdd(t.Schema + "." + t.Name, t);
    }

    public CatalogTable? Find(string schema, string name) => byName.GetValueOrDefault(schema + "." + name);

    public static string Display(CatalogTable t) => t.Schema + "." + t.Name;

    /// <summary>
    /// 이름을 "스키마.이름"으로 푼다. 3·4부분 이름은 쓴 그대로. 스키마를 생략했으면 preferredSchemas 순서로 카탈로그에서 찾고(Q4: 실행 사용자 기본 스키마 dbo),
    /// 없으면 마지막 후보 스키마를 붙인다.
    /// </summary>
    public (string Display, CatalogTable? Table) Resolve(SchemaObjectName n, params string[] preferredSchemas)
    {
        if (n.ServerIdentifier != null || n.DatabaseIdentifier != null) return (SqlSyntax.Name(n), null);
        var name = n.BaseIdentifier.Value;
        if (n.SchemaIdentifier != null)
        {
            var t = Find(n.SchemaIdentifier.Value, name);
            return (t is null ? n.SchemaIdentifier.Value + "." + name : Display(t), t);
        }
        foreach (var s in preferredSchemas)
        {
            var t = Find(s, name);
            if (t is not null) return (Display(t), t);
        }
        return (preferredSchemas[^1] + "." + name, null);
    }
}

/// <summary>스크립트 오프셋 ↔ (줄, 열). 줄바꿈은 \r\n, \n, \r. 열은 1부터 UTF-16 문자 수, 끝 위치는 마지막 문자를 포함한다.</summary>
internal sealed class LineIndex
{
    readonly List<int> starts = [0];
    readonly string text;

    public LineIndex(string text)
    {
        this.text = text;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
            else if (text[i] == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n')) starts.Add(i + 1);
        }
    }

    public (int Line, int Column) At(int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var i = starts.BinarySearch(offset);
        if (i < 0) i = ~i - 1;
        return (i + 1, offset - starts[i] + 1);
    }

    /// <summary>[offset, offset+length) 구간의 위치(길이 0이면 1자로 본다).</summary>
    public SourceSpan Span(int offset, int length)
    {
        var (sl, sc) = At(offset);
        var (el, ec) = At(offset + Math.Max(1, length) - 1);
        return new SourceSpan { StartLine = sl, StartColumn = sc, EndLine = el, EndColumn = ec };
    }

    public SourceSpan Span(TSqlFragment f) => Span(f.StartOffset, f.FragmentLength);

    /// <summary>오프셋이 있는 줄의 글자(줄바꿈 제외).</summary>
    public string LineText(int offset)
    {
        var (line, _) = At(offset);
        var start = starts[line - 1];
        var end = line < starts.Count ? starts[line] : text.Length;
        return text[start..end].TrimEnd('\r', '\n');
    }

    /// <summary>오프셋에서 시작하는 공백 아닌 글자 덩어리(줄 끝까지)의 위치. 오류 표시용.</summary>
    public SourceSpan TokenSpanAt(int offset)
    {
        if (text.Length == 0) return Span(0, 1);
        offset = Math.Clamp(offset, 0, text.Length - 1);
        var end = offset;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return Span(offset, Math.Max(1, end - offset));
    }
}
