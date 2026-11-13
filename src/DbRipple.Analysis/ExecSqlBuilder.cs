using System.Text.RegularExpressions;
using DbRipple.Core.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

/// <summary>
/// EXEC 미리보기 실행 SQL(2026-11-13). 사용자 원문을 잇지 않는다: 이름은 대괄호로 인용하고, 매개변수 이름은 정의의 철자를 검사해 쓰고,
/// 값은 파싱한 리터럴 노드에서 다시 만든다(문자열은 작은따옴표를 두 번). 만든 SQL은 다시 파싱해 모양을 확인한다.
/// </summary>
public static class ExecSqlBuilder
{
    /// <summary>반환 값을 받는 변수·열 이름.</summary>
    public const string ReturnColumn = Contracts.ProcedurePlan.ReturnColumn;

    /// <summary>트랜잭션 수 변화 열 이름(실행 전 대비, 감싼 BEGIN TRAN 포함).</summary>
    public const string TranDeltaColumn = Contracts.ProcedurePlan.TranDeltaColumn;

    static readonly Regex ParamName = new(@"^@[\p{L}\p{N}_@#$]{1,127}$", RegexOptions.CultureInvariant);
    static readonly Regex Digits = new(@"^\d+$", RegexOptions.CultureInvariant);
    static readonly Regex Decimal = new(@"^(\d+\.?\d*|\.\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex Real = new(@"^(\d+\.?\d*|\.\d+)[eE][+-]?\d+$", RegexOptions.CultureInvariant);
    static readonly Regex Money = new(@"^\p{Sc}?(\d+\.?\d*|\.\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex Binary = new(@"^0[xX][0-9A-Fa-f]*$", RegexOptions.CultureInvariant);

    /// <summary>리터럴·음수 리터럴·NULL·DEFAULT만 SQL 조각으로 다시 만든다. 그 밖(변수·식·이름 그대로 쓴 값)은 null.</summary>
    public static string? RenderLiteral(ScalarExpression? e) => e switch
    {
        NullLiteral => "NULL",
        DefaultLiteral => "DEFAULT",
        StringLiteral s => (s.IsNational ? "N'" : "'") + s.Value.Replace("'", "''") + "'",
        IntegerLiteral i when Digits.IsMatch(i.Value) => i.Value,
        NumericLiteral n when Decimal.IsMatch(n.Value) => n.Value,
        RealLiteral r when Real.IsMatch(r.Value) => r.Value,
        MoneyLiteral m when Money.IsMatch(m.Value) => m.Value,
        BinaryLiteral b when Binary.IsMatch(b.Value) => b.Value,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral } u
            => RenderLiteral(u.Expression) is { } v ? "-" + v : null,
        _ => null,
    };

    /// <summary>
    /// 실행 SQL(검토 2026-11-13: BEGIN TRAN 한 겹을 더 씌운다). 프로시저가 남긴 짝 없는 COMMIT 하나는 이 겹을 닫을 뿐 바깥 도구 트랜잭션을 확정하지 못한다.
    /// <code>
    /// DECLARE @dbr_rc int, @dbr_tc0 int = @@TRANCOUNT;
    /// BEGIN TRAN;
    /// EXEC @dbr_rc = [스키마].[이름] @p = 값, ...;
    /// DECLARE @dbr_dtc int = @@TRANCOUNT - @dbr_tc0;
    /// IF @dbr_dtc = 1 COMMIT TRAN;
    /// SELECT @dbr_rc AS dbr_rc, @dbr_dtc AS dbr_dtc;
    /// </code>
    /// dbr_dtc: 1 = 정상(감싼 겹을 닫음, 바깥은 그대로), 0 = 짝 없는 COMMIT, 2 이상 = 짝 없는 BEGIN, 음수 = 트랜잭션이 없어짐. 해석은 실행기(StatementRunner.ExecTransactionAnomaly).
    /// args의 Value는 <see cref="RenderLiteral"/> 결과여야 한다. 모양이 어긋나면 InvalidOperationException(분석기가 REJECT_ANALYSIS_ERROR로 돌린다).
    /// </summary>
    public static string Build(string schema, string name, IReadOnlyList<(string Parameter, string Value)> args)
    {
        foreach (var (p, _) in args)
            if (!ParamName.IsMatch(p)) throw new InvalidOperationException($"매개변수 이름 형식이 아님: {p}");
        var call = SqlText.Quote(schema, name) + (args.Count == 0 ? "" : " " + string.Join(", ", args.Select(a => a.Parameter + " = " + a.Value)));
        var sql = $"DECLARE @{ReturnColumn} int, @dbr_tc0 int = @@TRANCOUNT;\nBEGIN TRAN;\nEXEC @{ReturnColumn} = {call};\n"
            + $"DECLARE @{TranDeltaColumn} int = @@TRANCOUNT - @dbr_tc0;\nIF @{TranDeltaColumn} = 1 COMMIT TRAN;\n"
            + $"SELECT @{ReturnColumn} AS {ReturnColumn}, @{TranDeltaColumn} AS {TranDeltaColumn};";
        Verify(sql, schema, name, args.Count);
        return sql;
    }

    /// <summary>만든 SQL이 정확히 여섯 문장(DECLARE, BEGIN TRAN, 그 프로시저 EXEC, DECLARE, IF ... COMMIT, SELECT)인지 다시 파싱해 확인한다(주입 방어의 마지막 줄).</summary>
    static void Verify(string sql, string schema, string name, int argCount)
    {
        var fragment = SqlSyntax.Parse(sql, out var errors) as TSqlScript;
        var stmts = fragment?.Batches.SelectMany(b => b.Statements).ToList();
        var ok = errors.Count == 0 && stmts is [DeclareVariableStatement, BeginTransactionStatement, ExecuteStatement exec, DeclareVariableStatement, IfStatement { ThenStatement: CommitTransactionStatement, ElseStatement: null }, SelectStatement]
            && exec.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference pr
            && pr.ProcedureReference.ProcedureReference?.Name is { ServerIdentifier: null, DatabaseIdentifier: null } n
            && n.SchemaIdentifier?.Value == schema && n.BaseIdentifier.Value == name
            && pr.Parameters.Count == argCount && pr.Parameters.All(p => p.Variable is not null && !p.IsOutput);
        if (!ok) throw new InvalidOperationException("EXEC 실행 SQL 모양 확인 실패");
    }
}
