using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Sp4.ScriptDom.Tests;

/// <summary>C7 후보: CREATE TRIGGER 본문의 쓰기 대상·위험 플래그 정적 분석.</summary>
public sealed class TriggerAnalysis
{
    public string? TriggerName { get; set; }
    public bool InsteadOf { get; set; }
    public SortedSet<string> WriteTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DynamicSql { get; set; }
    public SortedSet<string> ProcedureCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Rollback { get; set; }
    public bool Raiserror { get; set; }
    public SortedSet<string> External { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class TriggerAnalyzer
{
    public static IList<TriggerAnalysis> Analyze(string sql)
    {
        var parser = new TSql160Parser(true);
        using var rd = new StringReader(sql);
        var frag = parser.Parse(rd, out var errors);
        if (errors.Count > 0)
            throw new InvalidOperationException($"parse error: {errors[0].Line}:{errors[0].Column} {errors[0].Message}");
        var result = new List<TriggerAnalysis>();
        var v = new TriggerVisitor(result);
        frag.Accept(v);
        return result;
    }

    private sealed class TriggerVisitor(List<TriggerAnalysis> output) : TSqlFragmentVisitor
    {
        private TriggerAnalysis? _cur;
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase) { "inserted", "deleted" };

        public override void Visit(CreateTriggerStatement node)
        {
            _cur = new TriggerAnalysis
            {
                TriggerName = Name(node.Name),
                InsteadOf = node.TriggerType == TriggerType.InsteadOf,
            };
            output.Add(_cur);
            // 이름 제외 집합: CTE·테이블 변수(본문 전체에서 선수집)
            var pre = new ExcludePrepass(_excluded);
            node.StatementList.Accept(pre);
            node.StatementList.Accept(new BodyVisitor(_cur, _excluded));
        }
    }

    private sealed class ExcludePrepass(HashSet<string> excluded) : TSqlFragmentVisitor
    {
        public override void Visit(CommonTableExpression node) => excluded.Add(node.ExpressionName.Value);
        public override void Visit(DeclareTableVariableBody node) => excluded.Add(node.VariableName.Value);
    }

    private sealed class BodyVisitor(TriggerAnalysis a, HashSet<string> excluded) : TSqlFragmentVisitor
    {
        public override void Visit(InsertSpecification n) => Target(n.Target, null);
        public override void Visit(UpdateSpecification n) => Target(n.Target, n.FromClause);
        public override void Visit(DeleteSpecification n) => Target(n.Target, n.FromClause);
        public override void Visit(MergeSpecification n) => Target(n.Target, null);
        public override void Visit(RollbackTransactionStatement n) => a.Rollback = true;
        public override void Visit(RaiseErrorStatement n) => a.Raiserror = true;
        public override void Visit(OpenQueryTableReference n) => a.External.Add("OPENQUERY");
        public override void Visit(OpenRowsetTableReference n) => a.External.Add("OPENROWSET");

        public override void Visit(NamedTableReference n)
        {
            var s = n.SchemaObject;
            if (s.ServerIdentifier != null) a.External.Add("4-part:" + Name(s));
            if (string.Equals(s.DatabaseIdentifier?.Value, "msdb", StringComparison.OrdinalIgnoreCase))
                a.External.Add("msdb:" + Name(s));
        }

        public override void Visit(ExecuteStatement n)
        {
            var spec = n.ExecuteSpecification.ExecutableEntity;
            if (spec is ExecutableStringList) { a.DynamicSql = true; return; }
            if (spec is ExecutableProcedureReference pr)
            {
                var pn = pr.ProcedureReference.ProcedureReference?.Name;
                if (pn == null) { a.DynamicSql = true; return; } // EXEC @procvar
                var last = pn.BaseIdentifier.Value;
                if (last.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase)) { a.DynamicSql = true; return; }
                a.ProcedureCalls.Add(Name(pn));
                if (last.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)) a.External.Add("xp_:" + Name(pn));
                if (pn.ServerIdentifier != null) a.External.Add("4-part:" + Name(pn));
                if (string.Equals(pn.DatabaseIdentifier?.Value, "msdb", StringComparison.OrdinalIgnoreCase))
                    a.External.Add("msdb:" + Name(pn));
            }
        }

        private void Target(TableReference? target, FromClause? from)
        {
            if (target is not NamedTableReference nt) return; // 변수(@t)·기타는 제외
            var so = nt.SchemaObject;
            var baseName = so.BaseIdentifier.Value;
            // 별칭 풀기: 대상이 한 부분 이름이고 FROM 안에 같은 별칭이 있으면 실제 테이블로
            if (from != null && so.SchemaIdentifier == null)
            {
                var finder = new AliasFinder(baseName);
                from.Accept(finder);
                if (finder.Found != null) { nt = finder.Found; so = nt.SchemaObject; baseName = so.BaseIdentifier.Value; }
            }
            if (baseName.StartsWith('#') || baseName.StartsWith('@')) return;
            if (so.SchemaIdentifier == null && excluded.Contains(baseName)) return;
            a.WriteTargets.Add(Name(so));
        }
    }

    private sealed class AliasFinder(string alias) : TSqlFragmentVisitor
    {
        public NamedTableReference? Found;
        public override void Visit(NamedTableReference n)
        {
            if (Found == null && string.Equals(n.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase)) Found = n;
        }
    }

    public static string Name(SchemaObjectName n)
    {
        var parts = new List<string>();
        if (n.ServerIdentifier != null) parts.Add(n.ServerIdentifier.Value);
        if (n.DatabaseIdentifier != null) parts.Add(n.DatabaseIdentifier.Value);
        if (n.SchemaIdentifier != null) parts.Add(n.SchemaIdentifier.Value);
        parts.Add(n.BaseIdentifier.Value);
        return string.Join('.', parts);
    }
}
