using DbRipple.Contracts;

namespace DbRipple.Analysis.Tests;

/// <summary>EXEC [적용]용 분석 재료(2026-11-17): 프로시저 본문 비결정 쓰기를 실행 계획에 싣기, 반환 값 비결정 판정과 경고.</summary>
public sealed class ExecApplyAnalysisTests
{
    static BaselineContext Ctx(params (string Name, string Definition)[] procedures)
    {
        var ctx = BatchAnalyzerTests.Ctx(more: b =>
        {
            b.Table("dbo", "C", "id", "t_id");
            foreach (var (name, definition) in procedures) b.Procedure("dbo", name, definition);
        });
        return ctx with { Triggers = new TriggerAnalyzer().Analyze(ctx.Catalog) };
    }

    static AnalyzedStatement Accepted(string sql, BaselineContext ctx, out AnalysisResult result)
    {
        result = new BatchAnalyzer().Analyze(sql, ctx);
        Assert.True(result.IsAccepted, string.Join(" | ", result.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        return Assert.Single(result.Statements);
    }

    [Fact]
    public void Plan_carries_nondeterministic_writes_of_expanded_body()
    {
        var ctx = Ctx(
            ("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; UPDATE dbo.U SET y = DATEPART(ms, SYSDATETIME()) WHERE x = 1; EXEC dbo.inner1; RETURN 0; END;"),
            ("inner1", "CREATE PROCEDURE dbo.inner1 AS INSERT INTO dbo.C (id, t_id) VALUES (CHECKSUM(NEWID()), 1);"));

        var st = Accepted("EXEC dbo.q;", ctx, out var r);

        Assert.Equal(["dbo.C.id", "dbo.U.y"], st.Plan!.NondeterministicWrites.Select(w => w.Table + "." + w.Column).Order(StringComparer.Ordinal));
        Assert.False(st.Plan.ReturnValueNondeterministic);   // RETURN 0(리터럴)
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicWrite);
        Assert.DoesNotContain(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.U (x, y) VALUES (1, 2); DECLARE @id int = SCOPE_IDENTITY(); RETURN @id;", true)]
    [InlineData("INSERT INTO dbo.U (x, y) VALUES (1, 2); RETURN @@IDENTITY;", true)]
    [InlineData("DECLARE @n int = DATEPART(second, GETDATE()); UPDATE dbo.U SET y = 1 WHERE x = @n; RETURN @n;", true)]
    [InlineData("UPDATE dbo.U SET y = 1; RETURN @@ROWCOUNT;", false)]                       // 비결정 함수 없음
    [InlineData("UPDATE dbo.U SET y = DATEPART(ms, GETDATE()); RETURN (0);", false)]       // 반환은 리터럴
    [InlineData("UPDATE dbo.U SET y = DATEPART(ms, GETDATE()); RETURN -1;", false)]
    [InlineData("UPDATE dbo.U SET y = DATEPART(ms, GETDATE()); RETURN;", false)]
    public void Return_value_nondeterminism_is_judged_conservatively(string body, bool expected)
    {
        var st = Accepted("EXEC dbo.q;", Ctx(("q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} END;")), out var r);

        Assert.Equal(expected, st.Plan!.ReturnValueNondeterministic);
        Assert.Equal(expected, r.Warnings.Any(w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn));
    }

    // PR #11 2차 검토 1: 반환 값은 RETURN 식이 IDENTITY·비결정 원천에서 (직접 또는 같은 본문의 단순 대입으로) 왔을 때만 느슨하게 본다.
    [Theory]
    // 본문 어딘가의 비결정 쓰기만으로는 켜지지 않는다: 상태 코드는 데이터로 정해진다
    [InlineData("INSERT INTO dbo.U (x, y) VALUES (1, DATEPART(ms, GETDATE())); DECLARE @status int; SELECT @status = y FROM dbo.U WHERE x = 2; RETURN @status;", false)]
    [InlineData("DECLARE @t datetime2; SET @t = SYSDATETIME(); DECLARE @n int; SET @n = DATEPART(ms, @t); RETURN @n;", true)]   // 대입 사슬
    [InlineData("DECLARE @id int; SELECT @id = CAST(SCOPE_IDENTITY() AS int); RETURN @id;", true)]                              // SELECT @v =
    [InlineData("DECLARE @t datetime2 = SYSDATETIME(); SET @t = DATEADD(day, 1, @t); RETURN DATEPART(ms, @t);", true)]          // 자기 참조 대입
    [InlineData("BEGIN DECLARE @id int = SCOPE_IDENTITY(); END; RETURN @id;", true)]                                            // BEGIN…END는 직선
    [InlineData("DECLARE @id int; IF 1 = 1 SET @id = SCOPE_IDENTITY(); RETURN @id;", false)]                                   // 분기 안 대입
    [InlineData("DECLARE @id int = 0; WHILE @id < 1 SET @id = SCOPE_IDENTITY(); RETURN @id;", false)]                          // 반복 안 대입
    [InlineData("DECLARE @id int; BEGIN TRY SET @id = SCOPE_IDENTITY(); END TRY BEGIN CATCH THROW; END CATCH; RETURN @id;", false)] // TRY 안 대입
    [InlineData("DECLARE @id int = SCOPE_IDENTITY(); SET @id = 5; RETURN @id;", false)]                                         // 결정적 대입이 섞임
    [InlineData("DECLARE @id int = SCOPE_IDENTITY(); GOTO done; done: RETURN @id;", false)]                                     // GOTO가 있으면 추적하지 않음
    [InlineData("IF EXISTS (SELECT 1 FROM dbo.U WHERE x = 1) RETURN @@ROWCOUNT; RETURN CAST(SCOPE_IDENTITY() AS int);", false)] // 데이터로 정해지는 RETURN이 섞임
    [InlineData("IF EXISTS (SELECT 1 FROM dbo.U WHERE x = 1) RETURN 1.5; RETURN CAST(SCOPE_IDENTITY() AS int);", false)]        // 정수가 아닌 리터럴
    public void Return_value_is_nondeterministic_only_when_it_comes_from_identity_or_nondeterministic_source(string body, bool expected)
    {
        var st = Accepted("EXEC dbo.q;", Ctx(("q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} END;")), out var r);

        Assert.Equal(expected, st.Plan!.ReturnValueNondeterministic);
        Assert.Equal(expected, r.Warnings.Any(w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn));
    }

    [Fact]
    public void Parameter_assigned_from_identity_is_not_a_source()
    {
        var ctx = Ctx(("q", "CREATE PROCEDURE dbo.q @id int AS BEGIN SET NOCOUNT ON; SET @id = SCOPE_IDENTITY(); RETURN @id; END;"));

        var st = Accepted("EXEC dbo.q @id = 1;", ctx, out _);

        Assert.False(st.Plan!.ReturnValueNondeterministic);   // 매개변수는 입력 값이 있어 원천으로 보지 않는다
    }

    [Fact]
    public void Literal_status_codes_next_to_identity_return_are_kept_for_exact_comparison()
    {
        var ctx = Ctx(("q", """
            CREATE PROCEDURE dbo.q AS BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM dbo.U WHERE x = 1) RETURN 5;
                IF EXISTS (SELECT 1 FROM dbo.U WHERE x = 2) RETURN (-1);
                INSERT INTO dbo.U (x, y) VALUES (3, 4);
                RETURN CAST(SCOPE_IDENTITY() AS int);
            END;
            """));

        var st = Accepted("EXEC dbo.q;", ctx, out var r);

        Assert.True(st.Plan!.ReturnValueNondeterministic);
        Assert.Equal([-1, 0, 5], st.Plan.LiteralReturnValues);   // 0은 값 없는 RETURN·본문 끝
        var w = Assert.Single(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn);
        Assert.Contains("-1, 0, 5", w.Message);
    }

    // PR #11 2차 검토 3: 변수를 거친 비결정 쓰기도 실행 계획에 싣는다(같은 본문의 단순 대입만).
    [Fact]
    public void Plan_carries_nondeterministic_writes_through_simple_variable_assignment()
    {
        var ctx = Ctx(("q", """
            CREATE PROCEDURE dbo.q AS BEGIN
                SET NOCOUNT ON;
                DECLARE @t int; SET @t = DATEPART(ms, SYSDATETIME());
                UPDATE dbo.U SET y = @t WHERE x = 1;
                DECLARE @g int; SELECT @g = CHECKSUM(NEWID());
                IF @g > 0 INSERT INTO dbo.C (id, t_id) VALUES (@g, 1);
                RETURN 0;
            END;
            """));

        var st = Accepted("EXEC dbo.q;", ctx, out var r);

        Assert.Equal(["dbo.C.id", "dbo.U.y"], st.Plan!.NondeterministicWrites.Select(w => w.Table + "." + w.Column).Order(StringComparer.Ordinal));
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicWrite);
    }

    [Theory]
    [InlineData("DECLARE @t int; IF 1 = 1 SET @t = DATEPART(ms, SYSDATETIME()); UPDATE dbo.U SET y = @t WHERE x = 1;")]          // 분기 안 대입
    [InlineData("DECLARE @t int = DATEPART(ms, SYSDATETIME()); SET @t = 7; UPDATE dbo.U SET y = @t WHERE x = 1;")]               // 결정적 대입이 섞임
    [InlineData("DECLARE @id int = SCOPE_IDENTITY(); UPDATE dbo.U SET y = @id WHERE x = 1;")]                                   // IDENTITY는 쓰기 쪽 원천이 아님(FK 짝 매핑이 맡음)
    [InlineData("DECLARE @t int; SELECT @t = y FROM dbo.U WHERE x = 2; UPDATE dbo.U SET y = @t WHERE x = 1;")]                 // 데이터 값
    public void Variable_writes_that_are_not_surely_nondeterministic_stay_strict(string body)
    {
        var st = Accepted("EXEC dbo.q;", Ctx(("q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} RETURN 0; END;")), out _);

        Assert.Empty(st.Plan!.NondeterministicWrites);
    }

    [Fact]
    public void Exec_and_dml_in_one_script_are_both_accepted()
    {
        var r = new BatchAnalyzer().Analyze("EXEC dbo.q; UPDATE dbo.U SET y = 2 WHERE x = 3;", Ctx(("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; UPDATE dbo.U SET y = 1 WHERE x = 1; RETURN 0; END;")));

        Assert.True(r.IsAccepted, string.Join(" | ", r.Rejects.Select(d => d.RuleId + ": " + d.Message)));
        Assert.Equal([ChangeOperation.Exec, ChangeOperation.Update], r.Statements.Select(s => s.Kind));
    }

    [Fact]
    public void Identity_function_in_inner_procedure_makes_non_literal_return_nondeterministic()
    {
        var ctx = Ctx(
            ("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; DECLARE @rc int; EXEC @rc = dbo.inner1; RETURN @rc; END;"),
            ("inner1", "CREATE PROCEDURE dbo.inner1 AS BEGIN INSERT INTO dbo.U (x, y) VALUES (1, 2); RETURN CAST(SCOPE_IDENTITY() AS int); END;"));

        var st = Accepted("EXEC dbo.q;", ctx, out var r);

        Assert.True(st.Plan!.ReturnValueNondeterministic);
        var w = Assert.Single(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn);
        Assert.Equal(Severity.Warning, w.Severity);
        Assert.Contains("NULL 여부만", w.Message);
    }
}
