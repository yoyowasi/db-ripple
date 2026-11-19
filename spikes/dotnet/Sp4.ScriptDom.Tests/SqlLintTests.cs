using Microsoft.SqlServer.TransactSql.ScriptDom;
namespace Sp4.ScriptDom.Tests;

/// <summary>정적 문법 점검: spikes/sql 와 cascadelab 의 .sql 을 TSql170Parser 로 파싱한다. 파일이 없으면 통과.</summary>
public class SqlLintTests
{
    static string? RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DbRipple.slnx"))) d = d.Parent;
        return d?.FullName;
    }

    [Fact]
    public void Repo_sql_files_parse_with_TSql160()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var errors = new List<string>();
        foreach (var dir in new[] { "spikes/sql", "cascadelab" })
        {
            var full = Path.Combine(root!, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.sql", SearchOption.AllDirectories))
            {
                // PostgreSQL 스크립트(파일 이름이 pg_로 시작하거나 _pg_를 포함, 예 sp22_10_pg_capture.sql)는 psql에서 검증한다.
                var relative = Path.GetRelativePath(root!, f);
                if (IsPostgresScript(f)) continue;
                using var r = new StringReader(SqlcmdToTsql(File.ReadAllText(f)));
                new TSql170Parser(true).Parse(r, out var e);
                foreach (var x in e)
                    errors.Add($"{relative} | {x.Line}:{x.Column} {x.Message} | 정적 문법 오류");
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    static bool IsPostgresScript(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase) || name.Contains("_pg_", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Postgres_scripts_are_recognized_by_name()
    {
        Assert.True(IsPostgresScript("spikes/sql/sp22_10_pg_capture.sql"));
        Assert.True(IsPostgresScript("spikes/sql/pg_cascade_measure.sql"));
        Assert.False(IsPostgresScript("spikes/sql/sp19_00_run.sql"));
        Assert.False(IsPostgresScript("cascadelab/sql/cl21.sql"));
    }

    [Fact]
    public void Sqlcmd_mode_files_are_parsed_not_skipped()
    {
        static int Errors(string sql) { new TSql170Parser(true).Parse(new StringReader(SqlcmdToTsql(sql)), out var e); return e.Count; }
        Assert.Equal(0, Errors(":on error exit\nUSE [$(DbName)];\nDECLARE @n int = CAST(20000 * $(Scale) AS int);\nIF N'$(DbName)' NOT LIKE N'SP19[_]%' THROW 50001, N'x', 1;"));
        Assert.NotEqual(0, Errors(":on error exit\nSELEC 1 FROM [$(DbName)].dbo.t;"));
        Assert.NotEqual(0, Errors("SELECT 1 FROM t WHERE x = $(Scale);")); // sqlcmd 명령 줄이 없으면 바꾸지 않는다
    }

    /// <summary>
    /// sqlcmd 모드 파일(줄머리 `:` 명령이 있는 파일, 예: SP19의 `:on error exit` + `-v` 변수)은 명령 줄을 주석으로 바꾸고
    /// `$(이름)`을 `1`로 바꿔 파싱한다. `1`은 숫자·`[1]` 식별자·문자열 안 어디서나 T-SQL로 읽혀 구조 검사는 그대로 남는다.
    /// </summary>
    internal static string SqlcmdToTsql(string text)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^[ \t]*:[A-Za-z]")) return text;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^([ \t]*):([A-Za-z])", "$1-- :$2");
        return System.Text.RegularExpressions.Regex.Replace(text, @"\$\([A-Za-z_][A-Za-z0-9_]*\)", "1");
    }
}
