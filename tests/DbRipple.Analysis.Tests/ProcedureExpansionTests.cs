using DbRipple.Contracts;
using DbRipple.Core.Catalog;
using DbRipple.TestSupport;

namespace DbRipple.Analysis.Tests;

/// <summary>
/// 2주차 인계 1(3주차): 트리거가 부르는 프로시저 본문 펼치기. 카탈로그는 손으로 만들고(DB 없이), AW 실측은 SqlFact 하나로 본다.
/// 규칙: ExpandedProcedureCalls ∪ UnresolvedProcedureCalls = ProcedureCalls(이름 그대로, 서로소), 펼친 프로시저의 쓰기 대상은 WriteTargets에 합친다,
/// 못 펼친 것만 미상이고 WriteTargetsUnknown은 트리거 본문 기준 그대로다.
/// </summary>
public class ProcedureExpansionTests
{
    const string LogError = """
        CREATE PROCEDURE dbo.uspLogError @ErrorLogID int = 0 OUTPUT AS
        BEGIN
            SET NOCOUNT ON;
            BEGIN TRY
                IF ERROR_NUMBER() IS NULL RETURN;
                INSERT dbo.ErrorLog (UserName, ErrorTime) VALUES (CONVERT(sysname, CURRENT_USER), GETDATE());
                SET @ErrorLogID = @@IDENTITY;
            END TRY
            BEGIN CATCH
                EXECUTE dbo.uspPrintError;
                RETURN -1;
            END CATCH
        END;
        """;

    const string PrintError = "CREATE PROCEDURE dbo.uspPrintError AS BEGIN SET NOCOUNT ON; PRINT 'Error ' + CONVERT(varchar(50), ERROR_NUMBER()); END;";

    static string Trigger(string body, string schema = "dbo", string table = "T") =>
        $"CREATE TRIGGER {schema}.trX ON {schema}.{table} AFTER INSERT AS BEGIN SET NOCOUNT ON; {body} END";

    static TriggerStaticInfo Analyze(string definition, Action<CatalogBuilder> procs, string schema = "dbo")
    {
        var b = new CatalogBuilder().Table(schema, "T", "a").Table("dbo", "ErrorLog", "!ErrorLogID", "UserName", "ErrorTime").Table("dbo", "L", "a").Table("app", "L", "a");
        procs(b);
        b.Trigger(schema, "T", "trX", ["INSERT"], definition);
        return Assert.Single(new TriggerAnalyzer().Analyze(b.Build()), i => i.Trigger == schema + ".trX");
    }

    static void AssertPartition(TriggerStaticInfo t)
    {
        Assert.Empty(t.ExpandedProcedureCalls.Intersect(t.UnresolvedProcedureCalls, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(t.ProcedureCalls.Order(StringComparer.OrdinalIgnoreCase),
            t.ExpandedProcedureCalls.Concat(t.UnresolvedProcedureCalls).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aw_style_log_error_expands_through_print_error_to_error_log()
    {
        var t = Analyze(Trigger("BEGIN TRY UPDATE dbo.L SET a = 1; END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; EXECUTE dbo.uspLogError; END CATCH;"),
            b => b.Procedure("dbo", "uspLogError", LogError).Procedure("dbo", "uspPrintError", PrintError));
        Assert.Equal(["dbo.uspLogError"], t.ProcedureCalls);
        Assert.Equal(["dbo.uspLogError"], t.ExpandedProcedureCalls);
        Assert.Empty(t.UnresolvedProcedureCalls);
        Assert.Equal(["dbo.ErrorLog", "dbo.L"], t.WriteTargets);
        Assert.False(t.WriteTargetsUnknown);
        Assert.False(t.HasDynamicSql);
        Assert.True(t.HasRollback);                 // 트리거 본문
        Assert.False(t.HasTransactionControl);      // 프로시저에도 COMMIT·BEGIN TRAN 없음
        Assert.Empty(t.ExternalSideEffects);
        Assert.Contains(t.NondeterministicWrites, w => w is { Table: "dbo.ErrorLog", Column: "ErrorTime" });
        AssertPartition(t);
    }

    [Fact]
    public void Missing_procedure_and_old_catalog_without_modules_are_unresolved()
    {
        var t = Analyze(Trigger("EXEC dbo.uspLogError;"), _ => { });
        Assert.Equal(["dbo.uspLogError"], t.UnresolvedProcedureCalls);
        Assert.Empty(t.ExpandedProcedureCalls);
        Assert.Empty(t.WriteTargets);
        Assert.False(t.WriteTargetsUnknown); // 트리거 본문은 읽었다. 미상은 UnresolvedProcedureCalls로만 나타낸다
        AssertPartition(t);
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("dynamic")]
    [InlineData("unparsed")]
    [InlineData("recursive")]
    [InlineData("nested-unresolved")]
    [InlineData("other-db")]
    [InlineData("too-deep")]
    public void Unresolvable_procedures(string kind)
    {
        var call = "EXEC dbo.p1;";
        var t = Analyze(Trigger(kind == "other-db" ? "EXEC OtherDb.dbo.p1;" : call), b =>
        {
            switch (kind)
            {
                case "encrypted": b.Procedure("dbo", "p1", null); break;
                case "dynamic": b.Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS BEGIN UPDATE dbo.L SET a = 1; EXEC (N'DELETE dbo.ErrorLog'); END;"); break;
                case "unparsed": b.Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS BEGIN UPDATE dbo.L SET a = ; END;"); break;
                case "recursive": b.Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS BEGIN UPDATE dbo.L SET a = 1; EXEC dbo.p1; END;"); break;
                case "nested-unresolved": b.Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS BEGIN UPDATE dbo.L SET a = 1; EXEC dbo.nope; END;"); break;
                case "other-db": b.Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS UPDATE dbo.L SET a = 1;"); break;
                case "too-deep":
                    for (var i = 1; i <= TriggerAnalyzer.MaxProcedureDepth; i++)
                        b.Procedure("dbo", "p" + i, $"CREATE PROCEDURE dbo.p{i} AS BEGIN UPDATE dbo.L SET a = {i}; EXEC dbo.p{i + 1}; END;");
                    b.Procedure("dbo", "p" + (TriggerAnalyzer.MaxProcedureDepth + 1), $"CREATE PROCEDURE dbo.p{TriggerAnalyzer.MaxProcedureDepth + 1} AS UPDATE dbo.L SET a = 0;");
                    break;
            }
        });
        Assert.Single(t.UnresolvedProcedureCalls);
        Assert.Empty(t.ExpandedProcedureCalls);
        Assert.False(t.WriteTargetsUnknown);
        Assert.False(t.HasDynamicSql); // 프로시저의 동적 SQL은 트리거 본문 속성이 아니다(미상은 UnresolvedProcedureCalls)
        AssertPartition(t);
        // 본문을 읽은 경우 정적으로 보인 쓰기 대상은 합친다(그 테이블은 확실히 쓰므로 C13이 inferred를 유지할 수 있다)
        if (kind is "dynamic" or "recursive" or "nested-unresolved" or "too-deep") Assert.Contains("dbo.L", t.WriteTargets);
        if (kind is "encrypted" or "unparsed" or "other-db") Assert.Empty(t.WriteTargets);
    }

    [Fact]
    public void Depth_at_limit_is_expanded()
    {
        var t = Analyze(Trigger("EXEC dbo.p1;"), b =>
        {
            for (var i = 1; i < TriggerAnalyzer.MaxProcedureDepth; i++)
                b.Procedure("dbo", "p" + i, $"CREATE PROCEDURE dbo.p{i} AS BEGIN UPDATE dbo.L SET a = {i}; EXEC dbo.p{i + 1}; END;");
            b.Procedure("dbo", "p" + TriggerAnalyzer.MaxProcedureDepth, $"CREATE PROCEDURE dbo.p{TriggerAnalyzer.MaxProcedureDepth} AS DELETE dbo.ErrorLog;");
        });
        Assert.Equal(["dbo.p1"], t.ExpandedProcedureCalls);
        Assert.Equal(["dbo.ErrorLog", "dbo.L"], t.WriteTargets);
    }

    [Fact]
    public void Unqualified_names_resolve_by_caller_schema_then_dbo()
    {
        // 트리거 스키마 app의 프로시저가 먼저, 그 안의 한 부분 이름 테이블은 프로시저 스키마(app)부터
        var t = Analyze(Trigger("EXEC p1; EXEC p2;", schema: "app"), b => b
            .Procedure("app", "p1", "CREATE PROCEDURE app.p1 AS UPDATE L SET a = 1;")
            .Procedure("dbo", "p1", "CREATE PROCEDURE dbo.p1 AS DELETE dbo.ErrorLog;")
            .Procedure("dbo", "p2", "CREATE PROCEDURE dbo.p2 AS UPDATE L SET a = 2;"), schema: "app");
        Assert.Equal(["p1", "p2"], t.ExpandedProcedureCalls);
        Assert.Equal(["app.L", "dbo.L"], t.WriteTargets); // app.p1 → app.L, dbo.p2 → dbo.L(dbo.ErrorLog는 쓰지 않음)
        AssertPartition(t);
    }

    [Theory]
    [InlineData("COMMIT TRAN;", true, false)]
    [InlineData("BEGIN TRAN; UPDATE dbo.L SET a = 1; COMMIT;", true, false)]
    [InlineData("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", false, true)]
    public void Transaction_control_in_expanded_procedure_is_merged(string procBody, bool control, bool rollback)
    {
        var t = Analyze(Trigger("EXEC dbo.p1;"), b => b.Procedure("dbo", "p1", $"CREATE PROCEDURE dbo.p1 AS BEGIN {procBody} END;"));
        Assert.Equal((control, rollback), (t.HasTransactionControl, t.HasRollback));
        Assert.Equal(["dbo.p1"], t.ExpandedProcedureCalls);
    }

    [Fact]
    public void External_side_effects_in_procedure_are_merged_even_when_unresolved()
    {
        var t = Analyze(Trigger("EXEC dbo.p1;"), b => b.Procedure("dbo", "p1",
            "CREATE PROCEDURE dbo.p1 AS BEGIN EXEC master.dbo.xp_cmdshell 'dir'; INSERT dbo.L (a) SELECT a FROM OPENQUERY(S, 'select 1'); END;"));
        Assert.Contains(t.ExternalSideEffects, x => x.StartsWith("xp_:", StringComparison.Ordinal));
        Assert.Contains("OPENQUERY", t.ExternalSideEffects);
        Assert.Equal(["dbo.p1"], t.UnresolvedProcedureCalls); // xp_cmdshell은 이 DB 카탈로그에 없다
    }

    /// <summary>AW 실측(읽기 전용): CATCH에서 uspLogError를 부르는 트리거는 모두 펼쳐지고 dbo.ErrorLog가 쓰기 대상에 들어간다.</summary>
    [SqlFact]
    public async Task Adventureworks_triggers_expand_usp_log_error()
    {
        await using var conn = await TestDb.OpenAsync("AW_Origin");
        var catalog = await CatalogReader.ReadAsync(conn);
        var infos = new TriggerAnalyzer().Analyze(catalog).Where(t => !t.IsAuditTrigger).ToList();
        var calling = infos.Where(t => t.ProcedureCalls.Any(c => c.EndsWith("uspLogError", StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.NotEmpty(calling);
        Assert.Contains(calling, t => t.Trigger == "Sales.iduSalesOrderDetail");
        Assert.All(calling, t =>
        {
            Assert.Empty(t.UnresolvedProcedureCalls);
            Assert.Contains("dbo.ErrorLog", t.WriteTargets);
            Assert.False(t.HasTransactionControl);
            Assert.Empty(t.ExternalSideEffects);
        });
        Assert.All(infos, AssertPartition);
    }
}
