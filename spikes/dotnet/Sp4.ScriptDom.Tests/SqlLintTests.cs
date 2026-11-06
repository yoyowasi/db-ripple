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
                using var r = new StreamReader(f);
                new TSql170Parser(true).Parse(r, out var e);
                foreach (var x in e)
                    errors.Add($"{Path.GetRelativePath(root!, f)} | {x.Line}:{x.Column} {x.Message} | 정적 문법 오류");
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
