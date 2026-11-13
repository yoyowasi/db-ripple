using DbRipple.Contracts;
using DbRipple.Core.Catalog;

namespace DbRipple.Analysis.Tests;

/// <summary>
/// EXEC 본문의 사용자 정의 함수 판정(2026-11-13): 카탈로그 함수 정보(종류·EXECUTE AS·본문)로 안전한 T-SQL 함수만 허용한다.
/// </summary>
public class FunctionGateTests
{
    static BaselineContext Ctx(string procBody, Action<CatalogBuilder> functions, bool functionsKnown = true)
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b =>
        {
            b.Procedure("dbo", "q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {procBody} END;");
            functions(b);
        });
        ctx = ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) };
        return functionsKnown ? ctx : ctx with { Catalog = ctx.Catalog with { Functions = null } };
    }

    static AnalysisResult Run(BaselineContext ctx) => new BatchAnalyzer().Analyze("EXEC dbo.q;", ctx);
    static string Show(AnalysisResult r) => string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message));

    const string Scalar = "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN @a + 1; END;";
    const string Inline = "CREATE FUNCTION dbo.tvf(@a int) RETURNS TABLE AS RETURN SELECT y FROM dbo.U WHERE y > @a;";

    [Fact]
    public void Clean_tsql_scalar_and_inline_functions_are_allowed()
    {
        var r = Run(Ctx("UPDATE dbo.U SET y = dbo.f(y); DELETE dbo.T WHERE a IN (SELECT y FROM dbo.tvf(1));",
            b => b.Function("dbo", "f", Scalar).Function("dbo", "tvf", Inline, "IF")));
        Assert.True(r.IsAccepted, Show(r));
    }

    [Fact]
    public void Multi_statement_table_function_writing_its_table_variable_is_allowed()
    {
        const string tf = "CREATE FUNCTION dbo.mtf() RETURNS @t TABLE (a int) AS BEGIN INSERT @t (a) SELECT y FROM dbo.U; RETURN; END;";
        var r = Run(Ctx("SELECT a FROM dbo.mtf();", b => b.Function("dbo", "mtf", tf, "TF")));
        Assert.DoesNotContain(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall);
    }

    [Theory]
    [InlineData("FS", null, true, "CLR 함수")]
    [InlineData("FN", null, true, "암호화")]
    [InlineData("FN", Scalar, false, "EXECUTE AS")]
    [InlineData("FN", "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN EXEC master.dbo.xp_logevent 60000, N'x'; RETURN @a; END;", true, "외부 부작용")]
    [InlineData("FN", "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM OtherDb.dbo.T); END;", true, "외부 부작용")]
    [InlineData("FN", "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM OPENQUERY(Lnk, 'select 1')); END;", true, "외부 부작용")]
    [InlineData("FN", "CREATE FUNCTION dbo.f(@a int) RETURNS int WITH ENCRYPTION AS BEGIN RETURN @a; END; garbage(", true, "파싱")]
    public void Unsafe_or_unreadable_functions_are_rejected(string type, string? definition, bool caller, string reason)
    {
        var r = Run(Ctx("UPDATE dbo.U SET y = dbo.f(y);", b => b.Function("dbo", "f", definition, type, caller)));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("dbo.f") && d.Message.Contains(reason));
    }

    [Fact]
    public void Nested_functions_are_checked_recursively()
    {
        const string outer = "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN dbo.g(@a) * 2; END;";
        const string clean = "CREATE FUNCTION dbo.g(@a int) RETURNS int AS BEGIN RETURN @a; END;";
        Assert.True(Run(Ctx("SELECT dbo.f(1);", b => b.Function("dbo", "f", outer).Function("dbo", "g", clean))).IsAccepted);

        var clr = Run(Ctx("SELECT dbo.f(1);", b => b.Function("dbo", "f", outer).Function("dbo", "g", null, "FS")));
        Assert.Contains(clr.Rejects, d => d.Message.Contains("안쪽 함수 dbo.g") && d.Message.Contains("CLR"));

        var missing = Run(Ctx("SELECT dbo.f(1);", b => b.Function("dbo", "f", outer)));
        Assert.Contains(missing.Rejects, d => d.Message.Contains("안쪽 함수 dbo.g"));
    }

    [Fact]
    public void Recursive_function_chain_is_rejected_and_terminates()
    {
        const string f = "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN CASE WHEN @a > 0 THEN dbo.g(@a - 1) ELSE 0 END; END;";
        const string g = "CREATE FUNCTION dbo.g(@a int) RETURNS int AS BEGIN RETURN dbo.f(@a); END;";
        var r = Run(Ctx("SELECT dbo.f(3);", b => b.Function("dbo", "f", f).Function("dbo", "g", g)));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("재귀"));
    }

    [Fact]
    public void Unknown_function_catalog_keeps_the_closed_side()
    {
        var r = Run(Ctx("SELECT dbo.f(1);", b => b.Function("dbo", "f", Scalar), functionsKnown: false));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("함수 정보가 없음"));
    }

    [Theory]
    [InlineData("SELECT OtherDb.dbo.f(1);")] // 다른 DB 함수
    [InlineData("SELECT x.col.value('(/a)[1]', 'int') FROM dbo.U AS x;")] // 열 메서드(판정할 수 없어 닫힌 쪽)
    public void Multi_part_call_targets_stay_rejected(string body)
    {
        var r = Run(Ctx(body, b => b.Function("dbo", "f", Scalar)));
        Assert.False(r.IsAccepted, Show(r));
    }

    // ---- 보안 검토(review-fn-1, 2026-11-13) 재현 ----

    [Theory]
    [InlineData("SELECT * FROM tempdb.dbo.evil();", "다른 DB")] // BodyVisitor는 tempdb를 외부로 보지 않는다
    [InlineData("SELECT * FROM tempdb..evil();", "다른 DB")]
    [InlineData("SELECT dbo.ClrT::Ping();", "사용자 정의 형식 메서드")]
    [InlineData("DECLARE @x dbo.ClrT; SELECT @x.Ping();", "식의 메서드")]
    [InlineData("DECLARE @x dbo.ClrT; SELECT @x.Prop;", "속성")]
    public void Unjudgeable_call_forms_in_procedure_body_are_rejected(string body, string reason)
    {
        var r = Run(Ctx(body, _ => { }));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains(reason));
    }

    [Fact]
    public void Xml_variable_methods_stay_allowed()
    {
        var r = Run(Ctx("DECLARE @x xml = N'<a>1</a>'; UPDATE dbo.U SET y = @x.value('(/a)[1]', 'int');", _ => { }));
        Assert.True(r.IsAccepted, Show(r));
    }

    [Theory]
    [InlineData("CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN dbo.ClrT::Ping(); END;", "사용자 정의 형식 메서드")]
    [InlineData("CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN DECLARE @x dbo.ClrT; RETURN @x.Ping(); END;", "식의 메서드")]
    [InlineData("CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN @@TRANCOUNT; END;", "@@TRANCOUNT")]
    [InlineData("CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN XACT_STATE(); END;", "XACT_STATE")]
    public void Unjudgeable_forms_inside_function_body_are_rejected(string definition, string reason)
    {
        var r = Run(Ctx("UPDATE dbo.U SET y = dbo.f(y);", b => b.Function("dbo", "f", definition)));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("dbo.f") && d.Message.Contains(reason));
    }

    [Fact]
    public void One_part_table_function_with_two_candidate_schemas_is_ambiguous()
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b => b
            .Procedure("s", "q", "CREATE PROCEDURE s.q AS BEGIN SET NOCOUNT ON; DELETE dbo.T WHERE a IN (SELECT y FROM tvf(1)); END;")
            .Function("s", "tvf", "CREATE FUNCTION s.tvf(@a int) RETURNS TABLE AS RETURN SELECT y FROM dbo.U;", "IF")
            .Function("dbo", "tvf", null, "FT"));
        var r = new BatchAnalyzer().Analyze("EXEC s.q;", ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) });
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("모호"));
    }

    // 대소문자 구분 정렬 DB에서 dbo.F 동의어가 다른 DB·CLR 함수를 가리킬 수 있다(카탈로그 dbo.f로 판정하면 어긋남).
    [Fact]
    public void Function_name_clashing_with_a_synonym_is_rejected()
    {
        var r = Run(Ctx("SELECT dbo.F(1);", b => b.Function("dbo", "f", Scalar).Synonym("dbo", "F", "[OtherDb].[dbo].[evil]")));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("동의어"));
        const string outer = "CREATE FUNCTION dbo.g(@a int) RETURNS int AS BEGIN RETURN dbo.F(@a); END;";
        var inner = Run(Ctx("SELECT dbo.g(1);", b => b.Function("dbo", "g", outer).Function("dbo", "f", Scalar).Synonym("dbo", "F", "x")));
        Assert.Contains(inner.Rejects, d => d.Message.Contains("안쪽 함수 dbo.F") && d.Message.Contains("동의어"));
    }

    [Fact]
    public void Procedure_name_clashing_with_a_synonym_is_not_resolved()
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b => b
            .Procedure("dbo", "p", "CREATE PROCEDURE dbo.p AS UPDATE dbo.U SET y = 1;")
            .Procedure("dbo", "q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; EXEC dbo.P; END;")
            .Synonym("dbo", "P", "[OtherDb].[dbo].[evil]"));
        ctx = ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) };
        Assert.Contains(new BatchAnalyzer().Analyze("EXEC dbo.P;", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectProcNotFound);
        Assert.Contains(new BatchAnalyzer().Analyze("EXEC dbo.q;", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall);
    }

    // ---- 뷰 안에 숨은 함수·외부 참조(2026-11-13) ----

    const string ClrView = "CREATE VIEW dbo.vClr AS SELECT y, dbo.clr(y) AS z FROM dbo.U;";

    [Fact]
    public void Clean_view_read_is_allowed()
    {
        var r = Run(Ctx("UPDATE dbo.U SET y = 1 WHERE y IN (SELECT y FROM dbo.vClean);", b => b.View("dbo", "vClean", "CREATE VIEW dbo.vClean AS SELECT y, dbo.f(y) AS z FROM dbo.U;").Function("dbo", "f", Scalar)));
        Assert.True(r.IsAccepted, Show(r));
    }

    [Theory]
    [InlineData("UPDATE dbo.U SET y = 1 WHERE y IN (SELECT y FROM dbo.vClr);")]
    [InlineData("UPDATE dbo.U SET y = 1 WHERE y IN (SELECT y FROM vClr);")] // 한 부분 이름
    [InlineData("UPDATE dbo.U SET y = 1 WHERE y IN (SELECT y FROM dbo.vOuter);")] // 뷰 → 뷰 → CLR 함수
    public void View_hiding_a_clr_function_is_rejected(string body)
    {
        var r = Run(Ctx(body, b => b.View("dbo", "vClr", ClrView).View("dbo", "vOuter", "CREATE VIEW dbo.vOuter AS SELECT y FROM dbo.vClr;").Function("dbo", "clr", null, "FS")));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("뷰") && d.Message.Contains("CLR"));
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.vX AS SELECT * FROM OPENQUERY(Lnk, 'select 1');", "외부 부작용")]
    [InlineData("CREATE VIEW dbo.vX AS SELECT a FROM OtherDb.dbo.T;", "외부 부작용")]
    [InlineData(null, "암호화")]
    public void Unsafe_or_unreadable_views_are_rejected(string? definition, string reason)
    {
        var r = Run(Ctx("SELECT * FROM dbo.vX;", b => b.View("dbo", "vX", definition)));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("dbo.vX") && d.Message.Contains(reason));
    }

    [Fact]
    public void Function_reading_a_view_with_a_clr_function_is_rejected()
    {
        const string f = "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM dbo.vClr); END;";
        var r = Run(Ctx("UPDATE dbo.U SET y = dbo.f(y);", b => b.Function("dbo", "f", f).View("dbo", "vClr", ClrView).Function("dbo", "clr", null, "FS")));
        Assert.Contains(r.Rejects, d => d.Message.Contains("dbo.f") && d.Message.Contains("읽는 뷰 dbo.vClr") && d.Message.Contains("CLR"));
    }

    [Fact]
    public void Old_baseline_without_views_keeps_previous_behavior()
    {
        var ctx = Ctx("UPDATE dbo.U SET y = 1 WHERE y IN (SELECT y FROM dbo.vClr);", b => b.View("dbo", "vClr", ClrView).Function("dbo", "clr", null, "FS"));
        var r = Run(ctx with { Catalog = ctx.Catalog with { Views = null } });
        Assert.True(r.IsAccepted, Show(r)); // 뷰 목록이 없으면 판정하지 않는다(오늘 전과 같음). 기준선을 다시 만들면 판정한다
    }

    // ---- 테이블 계산 열·DEFAULT·CHECK 안 함수(2026-11-13) ----

    [Theory]
    [InlineData("UPDATE dbo.U SET y = 1;")] // 쓰기: CHECK·DEFAULT·계산 열
    [InlineData("DELETE dbo.T WHERE a IN (SELECT y FROM dbo.U);")] // 읽기: 계산 열
    [InlineData("UPDATE U SET y = 1;")] // 스키마 없는 이름
    public void Table_whose_constraint_calls_a_clr_function_is_rejected(string body)
    {
        var r = Run(Ctx(body, b => b.Function("dbo", "clr", null, "FS").TableFunction("dbo.U", "dbo.clr")));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("dbo.U") && d.Message.Contains("계산 열·제약") && d.Message.Contains("CLR"));
    }

    [Fact]
    public void Table_whose_constraint_calls_a_clean_function_is_allowed()
    {
        var r = Run(Ctx("UPDATE dbo.U SET y = 1;", b => b.Function("dbo", "f", Scalar).TableFunction("dbo.U", "dbo.f")));
        Assert.True(r.IsAccepted, Show(r));
    }

    [Fact]
    public void Table_whose_constraint_calls_another_database_function_is_rejected()
    {
        var r = Run(Ctx("UPDATE dbo.U SET y = 1;", b => b.TableFunction("dbo.U", "dbo.g", database: "OtherDb")));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectProcUnresolvedCall && d.Message.Contains("다른 DB 함수"));
    }

    [Fact]
    public void Function_reading_a_table_with_clr_computed_column_is_rejected()
    {
        const string f = "CREATE FUNCTION dbo.f(@a int) RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM dbo.U); END;";
        var r = Run(Ctx("DELETE dbo.T WHERE a = dbo.f(1);", b => b.Function("dbo", "f", f).Function("dbo", "clr", null, "FS").TableFunction("dbo.U", "dbo.clr")));
        Assert.Contains(r.Rejects, d => d.Message.Contains("dbo.f") && d.Message.Contains("읽는 테이블 dbo.U") && d.Message.Contains("CLR"));
    }

    [Fact]
    public void Old_baseline_without_table_function_dependencies_keeps_previous_behavior()
    {
        var ctx = Ctx("UPDATE dbo.U SET y = 1;", b => b.Function("dbo", "clr", null, "FS").TableFunction("dbo.U", "dbo.clr"));
        Assert.True(Run(ctx with { Catalog = ctx.Catalog with { TableFunctions = null } }).IsAccepted);
    }

    [Fact]
    public void Ambiguous_function_names_are_rejected()
    {
        var r = Run(Ctx("SELECT dbo.f(1);", b => b.Function("dbo", "f", Scalar).Function("dbo", "F", Scalar)));
        Assert.Contains(r.Rejects, d => d.Message.Contains("모호"));
    }
}
