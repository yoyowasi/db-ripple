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
    [InlineData("DECLARE @id int; SET @id = CAST(SCOPE_IDENTITY() AS int); RETURN @id;", true)]                                 // 순수 원천
    [InlineData("RETURN DATEDIFF(second, '20000101', SYSDATETIME()) % 1000 + 1;", true)]                                      // 날짜 함수·산술
    // PR #15 검토 재현(순수하지 않은 식은 엄격): COALESCE·CASE·ISNULL·하위 쿼리
    [InlineData("DECLARE @s int; SELECT @s = y FROM dbo.U WHERE x = 1; RETURN COALESCE(@s, CAST(SCOPE_IDENTITY() AS int));", false)]
    [InlineData("RETURN CASE WHEN EXISTS (SELECT 1 FROM dbo.U WHERE x = 1) THEN 5 ELSE CAST(SCOPE_IDENTITY() AS int) END;", false)]
    [InlineData("RETURN ISNULL(CAST(SCOPE_IDENTITY() AS int), 0);", false)]
    [InlineData("DECLARE @c int; SET @c = (SELECT COUNT(*) FROM dbo.U WHERE x < DATEPART(hour, GETDATE())); RETURN @c;", false)]
    [InlineData("RETURN (SELECT MAX(y) FROM dbo.U WHERE x < DATEPART(hour, GETDATE()));", false)]
    [InlineData("DECLARE @n int; SET @n = DATEPART(ms, GETDATE()) + (SELECT MAX(y) FROM dbo.U); RETURN @n;", false)]           // 원천 + 하위 쿼리
    [InlineData("DECLARE @n int = 3; RETURN DATEPART(ms, GETDATE()) + @n;", false)]                                           // 원천 아닌 변수가 섞임
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
    // PR #15 검토 재현: 하위 쿼리 필터에만 비결정 함수(값은 데이터), CASE, 직접 대입도 같은 규칙
    [InlineData("DECLARE @r int = (SELECT TOP (1) y FROM dbo.U WHERE x <= DATEPART(hour, GETDATE()) ORDER BY x DESC); UPDATE dbo.U SET y = @r WHERE x = 1;")]
    [InlineData("DECLARE @r int; SELECT TOP (1) @r = y FROM dbo.U WHERE x <= DATEPART(hour, GETDATE()) ORDER BY x DESC; UPDATE dbo.U SET y = @r WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = (SELECT TOP (1) y FROM dbo.U WHERE x <= DATEPART(hour, GETDATE()) ORDER BY x DESC) WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = CASE WHEN x > 0 THEN DATEPART(ms, GETDATE()) ELSE 0 END WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = DATEPART(ms, GETDATE()) + x WHERE x = 1;")]                                              // 열 참조가 섞임
    // PR #15 2차 검토 B: 문자열 감싸기에 데이터 인자가 섞이면 엄격
    [InlineData("UPDATE dbo.U SET y = LEFT(y, 2) + CONVERT(char(8), GETDATE(), 112) WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = REPLACE(CONVERT(varchar(36), NEWID()), '-', (SELECT MAX(y) FROM dbo.U)) WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = FORMAT(GETDATE(), 'yyyyMMdd') + (SELECT MAX(y) FROM dbo.U) WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = DATEADD(day, x, GETDATE()) WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = RAND(42) WHERE x = 1;")]                                                                 // 상수 시드(C)
    public void Variable_writes_that_are_not_surely_nondeterministic_stay_strict(string body)
    {
        var st = Accepted("EXEC dbo.q;", Ctx(("q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} RETURN 0; END;")), out _);

        Assert.Empty(st.Plan!.NondeterministicWrites);
    }

    [Theory]
    [InlineData("DECLARE @t datetime2; SET @t = GETDATE(); UPDATE dbo.U SET y = @t WHERE x = 1;")]
    [InlineData("DECLARE @t int = CAST(DATEPART(ms, SYSDATETIME()) AS int) * 10; UPDATE dbo.U SET y = @t WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = CONVERT(varchar(30), SYSDATETIME(), 126) WHERE x = 1;")]
    // PR #15 2차 검토 B: 문자열 감싸기
    [InlineData("UPDATE dbo.U SET y = FORMAT(GETDATE(), 'yyyyMMdd') WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = 'PO' + CONVERT(char(8), GETDATE(), 112) WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = REPLACE(CONVERT(varchar(36), NEWID()), '-', '') WHERE x = 1;")]
    [InlineData("UPDATE dbo.U SET y = UPPER(LEFT(CONCAT('n', CONVERT(varchar(36), NEWID())), 8)) WHERE x = 1;")]
    [InlineData("DECLARE @n varchar(20) = 'PO' + CONVERT(char(8), GETDATE(), 112); UPDATE dbo.U SET y = @n WHERE x = 1;")]
    public void Pure_nondeterministic_writes_stay_marked(string body)
    {
        var st = Accepted("EXEC dbo.q;", Ctx(("q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} RETURN 0; END;")), out _);

        Assert.Equal(["dbo.U.y"], st.Plan!.NondeterministicWrites.Select(w => w.Table + "." + w.Column));
    }

    // PR #15 검토 2: EXEC @rc = 안쪽은 안쪽 반환 값 판정을 쓴다.
    [Fact]
    public void Inner_procedure_status_codes_are_not_a_source_even_if_inner_writes_nondeterministic_values()
    {
        var ctx = Ctx(
            ("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; DECLARE @rc int; EXEC @rc = dbo.inner1; RETURN @rc; END;"),
            ("inner1", "CREATE PROCEDURE dbo.inner1 AS BEGIN UPDATE dbo.U SET y = DATEPART(ms, GETDATE()) WHERE x = 1; IF EXISTS (SELECT 1 FROM dbo.U WHERE x = 2) RETURN 2; RETURN 1; END;"));

        var st = Accepted("EXEC dbo.q;", ctx, out var r);

        Assert.False(st.Plan!.ReturnValueNondeterministic);
        Assert.DoesNotContain(r.Warnings, w => w.RuleId == AnalysisRules.WarnProcNondeterministicReturn);
    }

    [Fact]
    public void Inner_procedure_identity_return_is_a_source_and_its_literals_are_merged()
    {
        var ctx = Ctx(
            ("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; DECLARE @rc int; EXEC @rc = dbo.inner1; IF @rc IS NULL RETURN -1; RETURN @rc; END;"),
            ("inner1", "CREATE PROCEDURE dbo.inner1 AS BEGIN IF EXISTS (SELECT 1 FROM dbo.U WHERE x = 2) RETURN 7; INSERT INTO dbo.U (x, y) VALUES (1, 2); RETURN CAST(SCOPE_IDENTITY() AS int); END;"));

        var st = Accepted("EXEC dbo.q;", ctx, out _);

        Assert.True(st.Plan!.ReturnValueNondeterministic);
        Assert.Equal([-1, 0, 7], st.Plan.LiteralReturnValues);   // 안쪽 RETURN 7도 그대로 비교
    }

    // PR #15 2차 검토 A: 자기 참조 산술로 EXEC 반환 값을 바꾸면 엄격(안쪽 RETURN 7이 8이 되어 리터럴 집합으로 잡을 수 없다).
    [Theory]
    [InlineData("DECLARE @rc int; EXEC @rc = dbo.innerid; SET @rc = @rc + 1; RETURN @rc;")]
    [InlineData("DECLARE @rc int; EXEC @rc = dbo.innerid; SET @rc += 1; RETURN @rc;")]
    [InlineData("DECLARE @rc int; EXEC @rc = dbo.innerid; DECLARE @x int = @rc; SET @x = @x * 2; RETURN @x;")]
    public void Inner_procedure_return_changed_by_self_referencing_arithmetic_is_strict(string body)
    {
        var ctx = Ctx(
            ("q", $"CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; {body} END;"),
            ("innerid", "CREATE PROCEDURE dbo.innerid AS BEGIN IF EXISTS (SELECT 1 FROM dbo.U WHERE x = 2) RETURN 7; INSERT INTO dbo.U (x, y) VALUES (1, 2); RETURN CAST(SCOPE_IDENTITY() AS int); END;"));

        var st = Accepted("EXEC dbo.q;", ctx, out _);

        Assert.False(st.Plan!.ReturnValueNondeterministic);
    }

    // PR #15 2차 검토 B: 사용자 문장. 영향 열로 표시하지 않은 비결정 사용이 있으면 경고 문구에 롤백 가능성을 덧붙인다.
    [Theory]
    [InlineData("UPDATE dbo.U SET y = 'PO' + CONVERT(char(8), GETDATE(), 112) WHERE x = 1;", true, false)]
    [InlineData("UPDATE dbo.U SET y = GETDATE() WHERE x = 1;", true, false)]
    [InlineData("UPDATE dbo.U SET y = DATEADD(day, x, GETDATE()) WHERE x = 1;", false, true)]
    [InlineData("UPDATE dbo.U SET y = GETDATE() WHERE x < DATEPART(hour, GETDATE());", true, true)]
    [InlineData("DELETE dbo.U WHERE x < DATEPART(hour, GETDATE());", false, true)]
    public void User_statement_warning_says_when_nondeterministic_use_is_compared_by_value(string sql, bool marked, bool rollbackNote)
    {
        var r = new BatchAnalyzer().Analyze(sql, Ctx());

        Assert.True(r.IsAccepted);
        Assert.Equal(marked, r.Statements[0].NondeterministicColumns.Contains("y"));
        var w = Assert.Single(r.Warnings, w => w.RuleId == AnalysisRules.WarnNondeterministicFunction);
        Assert.Equal(rollbackNote, w.Message.Contains("롤백될 수 있음", StringComparison.Ordinal));
    }

    [Fact]
    public void Inner_procedure_return_used_in_an_expression_is_strict()
    {
        // 안쪽 리터럴 반환 값이 식을 거치면 바깥 리터럴 집합으로 잡을 수 없다.
        var ctx = Ctx(
            ("q", "CREATE PROCEDURE dbo.q AS BEGIN SET NOCOUNT ON; DECLARE @rc int; EXEC @rc = dbo.inner1; DECLARE @x int = @rc; RETURN @x + 1; END;"),
            ("inner1", "CREATE PROCEDURE dbo.inner1 AS BEGIN INSERT INTO dbo.U (x, y) VALUES (1, 2); RETURN CAST(SCOPE_IDENTITY() AS int); END;"));

        var st = Accepted("EXEC dbo.q;", ctx, out _);

        Assert.False(st.Plan!.ReturnValueNondeterministic);
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
