using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Catalog;
using DbRipple.TestSupport;
using static DbRipple.Apply.Tests.TestData;

namespace DbRipple.Apply.Tests;

/// <summary>EXEC [적용] 선행 조건(2026-11-17): 원본 읽기 SQL 모양, 확인 대상, 판정. 마지막 테스트만 실제 DB(SqlFact)다.</summary>
public sealed class OriginProceduresTests
{
    static readonly IReadOnlyDictionary<string, CatalogModule> Baseline =
        OriginProcedures.Baseline(ExecContext().Catalog, ["dbo.usp_T", "dbo.usp_Inner"]);

    [Fact]
    public void Sql_reads_definitions_before_impersonating_and_reverts_after_permission_check()
    {
        var sql = OriginProcedures.Sql(2, PreviewUserSql.UserName);

        var definitions = sql.IndexOf("m.definition", StringComparison.Ordinal);
        var executeAs = sql.IndexOf("EXECUTE AS USER = N'dbr_preview_user';", StringComparison.Ordinal);
        var perms = sql.IndexOf("HAS_PERMS_BY_NAME", StringComparison.Ordinal);
        var revert = sql.IndexOf("REVERT;", StringComparison.Ordinal);
        Assert.True(definitions >= 0 && definitions < executeAs && executeAs < perms && perms < revert, sql);
        Assert.Contains("(0, @s0, @n0), (1, @s1, @n1)", sql);
        Assert.DoesNotContain("GRANT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_for_own_login_does_not_impersonate()
    {
        var sql = OriginProcedures.Sql(1, null);

        Assert.DoesNotContain("EXECUTE AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REVERT", sql, StringComparison.Ordinal);
        Assert.Contains("HAS_PERMS_BY_NAME", sql);
    }

    [Fact]
    public void Sql_quotes_user_name_and_rejects_empty_list()
    {
        Assert.Contains("EXECUTE AS USER = N'o''brien';", OriginProcedures.Sql(1, "o'brien"));
        Assert.Throws<ArgumentOutOfRangeException>(() => OriginProcedures.Sql(0, null));
    }

    [Fact]
    public void Targets_are_top_level_procedures_and_expanded_calls_without_duplicates()
    {
        var other = ExecAnalyzed(2, "dbo.usp_Inner") with { Plan = ExecAnalyzed().Plan! with { Procedure = "dbo.usp_Inner", ExpandedCalls = ["dbo.usp_X"] } };
        var dml = Analyzed(Statement(3, 1));

        var (top, all) = OriginProcedures.Targets([other, ExecAnalyzed(1), dml]);

        Assert.Equal(["dbo.usp_T", "dbo.usp_Inner"], top);
        Assert.Equal(["dbo.usp_T", "dbo.usp_Inner", "dbo.usp_X"], all);
        var (dmlTop, dmlAll) = OriginProcedures.Targets([dml]);   // DML만이면 원본을 읽지 않는다
        Assert.Empty(dmlTop);
        Assert.Empty(dmlAll);
    }

    [Fact]
    public void Baseline_skips_names_that_are_ambiguous_in_catalog()
    {
        var catalog = Catalog() with { Procedures = [new(1, "dbo", "p", "A"), new(2, "dbo", "p", "B"), new(3, "dbo", "q", "C")] };

        var found = OriginProcedures.Baseline(catalog, ["dbo.p", "dbo.q", "dbo.r"]);

        Assert.Equal(["dbo.q"], found.Keys);
    }

    [Fact]
    public void Judge_passes_when_definitions_match_and_top_level_is_executable()
    {
        var origin = new[] { new OriginProcedure("dbo", "usp_T", ProcDefinition, true), new OriginProcedure("dbo", "usp_Inner", InnerDefinition, false) };

        Assert.Empty(OriginProcedures.Judge(["dbo.usp_T"], ["dbo.usp_T", "dbo.usp_Inner"], Baseline, origin, "u"));
    }

    [Fact]
    public void Judge_compares_definitions_exactly()
    {
        // 공백 하나만 달라도 바뀐 것으로 본다(같은 백업에서 온 정의는 글자까지 같다).
        var origin = new[] { new OriginProcedure("dbo", "usp_T", ProcDefinition + " ", true), new OriginProcedure("dbo", "usp_Inner", InnerDefinition, true) };

        var (code, reason) = Assert.Single(OriginProcedures.Judge(["dbo.usp_T"], ["dbo.usp_T", "dbo.usp_Inner"], Baseline, origin, "u"));

        Assert.Equal(ApplyPrecondition.ProcDefinitionChanged, code);
        Assert.Contains("dbo.usp_T", reason);
    }

    [Fact]
    public void Judge_reports_every_failure_kind()
    {
        var baseline = new Dictionary<string, CatalogModule>(Baseline) { ["dbo.enc"] = new(12, "dbo", "enc", null) };
        var origin = new[]
        {
            new OriginProcedure("dbo", "usp_T", ProcDefinition, false),        // 권한 없음
            new OriginProcedure("dbo", "usp_Inner", null, true),               // 정의 못 읽음
            new OriginProcedure("dbo", "enc", "x", true),                      // 기준선 정의가 없음(암호화) → 같다고 볼 수 없음
        };

        var list = OriginProcedures.Judge(["dbo.usp_T", "dbo.gone"], ["dbo.usp_T", "dbo.gone", "dbo.usp_Inner", "dbo.enc"], baseline, origin, "원본 실행 사용자 u");

        Assert.Equal(
            [ApplyPrecondition.ProcDefinitionChanged, ApplyPrecondition.ProcDefinitionChanged, ApplyPrecondition.ProcDefinitionChanged, ApplyPrecondition.ProcExecutePermissionMissing],
            list.Select(x => x.Code));
        Assert.Contains("dbo.enc", list[0].Reason);
        Assert.Contains("dbo.usp_Inner", list[1].Reason);
        Assert.Contains("dbo.gone", list[2].Reason);
        Assert.Contains("원본 실행 사용자 u에게", list[3].Reason);
        Assert.Contains("dbo.usp_T", list[3].Reason);
        Assert.DoesNotContain("dbo.gone", list[3].Reason);   // 기준선에 없는 최상위는 정의 실패로만 보인다
    }

    [SqlFact]
    public async Task Reads_definitions_and_execute_permission_of_impersonated_user_or_own_login()
    {
        var db = TestDb.NewName("apl", "prc");
        await using var cleanup = new TestDatabases(db);
        await TestDb.ExecAsync("master", $"CREATE DATABASE [{db}];");
        await TestDb.ExecAsync(db,
            "CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY, v int NOT NULL);",
            "CREATE PROCEDURE dbo.p_ok AS UPDATE dbo.T SET v = v + 1 WHERE id = 1;",
            "CREATE PROCEDURE dbo.p_denied AS UPDATE dbo.T SET v = v - 1 WHERE id = 1;",
            "CREATE USER [dbr_preview_user] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo];",
            "GRANT EXECUTE ON OBJECT::[dbo].[p_ok] TO [dbr_preview_user];");
        var origin = new SqlOriginDatabase(TestDb.ConnectionString(), new ApplySettings());
        (string, string)[] names = [("dbo", "p_ok"), ("dbo", "p_denied"), ("dbo", "p_missing")];

        var asUser = await origin.ReadProceduresAsync(db, names, PreviewUserSql.UserName, default);
        var asLogin = await origin.ReadProceduresAsync(db, names, null, default);

        Assert.Equal([true, false, false], asUser.Select(p => p.CanExecute));
        Assert.Contains("p_ok", asUser[0].Definition);                       // 정의는 가장 전에 도구 로그인으로 읽는다
        Assert.Contains("p_denied", asUser[1].Definition);
        Assert.Null(asUser[2].Definition);
        Assert.Equal([true, true, false], asLogin.Select(p => p.CanExecute)); // 테스트 로그인(sysadmin)은 모두 실행할 수 있고, 없는 프로시저는 false
        Assert.Equal(asUser.Select(p => p.Definition), asLogin.Select(p => p.Definition));
    }
}
