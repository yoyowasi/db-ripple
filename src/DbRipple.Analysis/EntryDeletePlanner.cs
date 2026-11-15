using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

internal static class EntryDeletePlanner
{
    // This allowlist describes a proof, independently of which statements C6 permits executing.
    public static IndependentDelete? Find(CatalogModule module, ProcedureStatementBody body, DbCatalog catalog)
    {
        if (body.Options.Count != 0 || body.IsForReplication || body.MethodSpecifier is not null) return null;
        CatalogTable? first = null;
        bool returned = false;
        foreach (var statement in Flatten(body.StatementList))
        {
            if (returned) return null;
            switch (statement)
            {
                case PredicateSetStatement { Options: SetOptions.NoCount }:
                    break;
                case ReturnStatement { Expression: IntegerLiteral }:
                    returned = true;
                    break;
                case DeleteStatement delete when SimpleTarget(delete) is { } name:
                    var target = catalog.Tables.FirstOrDefault(t => t.Schema == name.SchemaIdentifier.Value && t.Name == name.BaseIdentifier.Value);
                    if (target is null) return null;
                    first ??= target;
                    break;
                default:
                    return null;
            }
        }
        if (first is null) return null;
        var auditName = AuditTriggerGenerator.TriggerName(first.Name, DmlOperation.Delete);
        if (catalog.Triggers.Any(t => t.ParentId == first.ObjectId && !t.IsDisabled &&
            string.Equals(t.Event, "DELETE", StringComparison.OrdinalIgnoreCase) && (t.IsInsteadOf || t.Name != auditName))) return null;
        if (catalog.ForeignKeys.Any(f => !f.IsDisabled && f.ReferencedObjectId == first.ObjectId &&
            (f.ParentObjectId == first.ObjectId || f.DeleteAction != "NO_ACTION"))) return null;
        return new IndependentDelete(module.ObjectId, first.ObjectId, TableResolver.Display(first), ChangeOperation.Delete);
    }

    static IEnumerable<TSqlStatement> Flatten(StatementList list)
    {
        foreach (var statement in list.Statements)
        {
            if (statement is BeginEndBlockStatement block)
                foreach (var inner in Flatten(block.StatementList)) yield return inner;
            else
                yield return statement;
        }
    }

    static SchemaObjectName? SimpleTarget(DeleteStatement statement)
    {
        var d = statement.DeleteSpecification;
        if (statement.WithCtesAndXmlNamespaces is not null || statement.OptimizerHints.Count != 0 ||
            d.FromClause is not null || d.TopRowFilter is not null || d.OutputClause is not null || d.OutputIntoClause is not null ||
            d.Target is not NamedTableReference { Alias: null, TableHints.Count: 0, TemporalClause: null, TableSampleClause: null,
                ForPath: false, SchemaObject.Identifiers.Count: 2 } table) return null;
        return d.WhereClause is null || (d.WhereClause.Cursor is null && SimpleCondition(d.WhereClause.SearchCondition))
            ? table.SchemaObject : null;
    }

    static bool SimpleCondition(BooleanExpression condition) => condition switch
    {
        BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } c => SimpleOperand(c.FirstExpression) && SimpleOperand(c.SecondExpression),
        BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } b => SimpleCondition(b.FirstExpression) && SimpleCondition(b.SecondExpression),
        BooleanParenthesisExpression p => SimpleCondition(p.Expression),
        _ => false,
    };

    static bool SimpleOperand(ScalarExpression value) => value is IntegerLiteral or VariableReference or
        ColumnReferenceExpression { ColumnType: ColumnType.Regular, MultiPartIdentifier.Identifiers.Count: 1, Collation: null };
}
