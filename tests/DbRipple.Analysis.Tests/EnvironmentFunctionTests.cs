using DbRipple.Contracts;
using DbRipple.Core.Catalog;

namespace DbRipple.Analysis.Tests;

/// <summary>
/// 2026-11-13 Split: 로그인·서버·DB에 따라 값이 달라지는 함수(SUSER_SNAME 등)는 샌드박스와 원본에서 값이 다르므로 E1 비결정으로 본다
/// (문장 대입 열 → STATEMENT_FUNCTION, 열 기본값 → 매니페스트 비결정 열). [적용] 값 대조에서 존재·형식만 비교한다.
/// </summary>
public class EnvironmentFunctionTests
{
    [Theory]
    [InlineData("SUSER_SNAME()")]
    [InlineData("SUSER_NAME()")]
    [InlineData("ORIGINAL_LOGIN()")]
    [InlineData("HOST_NAME()")]
    [InlineData("APP_NAME()")]
    [InlineData("DB_NAME()")]
    [InlineData("USER_NAME()")]
    [InlineData("CURRENT_USER")]
    [InlineData("SYSTEM_USER")]
    [InlineData("SESSION_USER")]
    // @@SERVERNAME·@@SPID는 문장에서는 E9로 거부되므로 트리거 본문·열 기본값 경로에서만 의미가 있다(아래 기본값 테스트).
    public void Environment_dependent_value_makes_the_assigned_column_volatile(string expr)
    {
        var r = new BatchAnalyzer().Analyze($"UPDATE dbo.T SET b = {expr} WHERE a = 1;", BatchAnalyzerTests.Ctx());
        Assert.True(r.IsAccepted);
        Assert.Equal(["b"], r.Statements[0].NondeterministicColumns);
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnNondeterministicFunction);
    }

    [Fact]
    public void Ordinary_functions_stay_deterministic()
    {
        var r = new BatchAnalyzer().Analyze("UPDATE dbo.T SET b = UPPER(N'x') + OBJECT_NAME(1) WHERE a = 1;", BatchAnalyzerTests.Ctx());
        Assert.Empty(r.Statements[0].NondeterministicColumns);
        Assert.DoesNotContain(r.Warnings, w => w.RuleId == AnalysisRules.WarnNondeterministicFunction);
    }

    [Theory]
    [InlineData("(suser_sname())", true)]
    [InlineData("(user_name())", true)]
    [InlineData("(host_name())", true)]
    [InlineData("(original_login())", true)]
    [InlineData("(@@servername)", true)]
    [InlineData("(getdate())", true)]
    [InlineData("((0))", false)]
    [InlineData("(N'username')", false)]
    public void Column_defaults_with_environment_functions_are_nondeterministic(string definition, bool expected)
    {
        var c = new CatalogColumn(1, 1, "c", "nvarchar", false, true, false, false, false, definition);
        Assert.Equal(expected, c.HasNondeterministicDefault);
    }
}
