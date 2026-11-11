using DbRipple.Contracts;
using DbRipple.Eval;
using DbRipple.TestSupport;

namespace DbRipple.Eval.Tests;

/// <summary>§10.3 독립성(하네스는 제품 C12·Core 값 식·Core 카탈로그와 코드를 공유하지 않는다)과 골격 스모크.</summary>
public class IndependenceTests
{
    static readonly string[] ProductAssemblies = ["DbRipple.Diff", "DbRipple.Execution", "DbRipple.Attribution", "DbRipple.Analysis", "DbRipple.Preview", "DbRipple.Api", "DbRipple.Host"];

    /// <summary>하네스 어셈블리는 제품 컴포넌트(특히 C12 DbRipple.Diff)를 참조하지 않는다. Contracts(계약)와 Core(계약의 전이)만 허용.</summary>
    [Fact]
    public void Harness_does_not_reference_product_components()
    {
        var refs = typeof(HashSnapshotComparer).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(refs, r => ProductAssemblies.Contains(r));
        Assert.Contains("DbRipple.Contracts", refs);
    }

    /// <summary>하네스 테스트도 제품 컴포넌트를 참조하지 않는다(지표는 가짜 제품 결과로 검사).</summary>
    [Fact]
    public void Harness_tests_do_not_reference_product_components()
    {
        var refs = typeof(IndependenceTests).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(refs, r => ProductAssemblies.Contains(r));
    }

    /// <summary>
    /// 하네스 소스는 Core의 값 식·키 선택·카탈로그·C12를 쓰지 않는다(어셈블리 참조 검사로는 Contracts를 거친 Core 사용을 못 잡으므로 소스로 본다).
    /// </summary>
    [Fact]
    public void Harness_sources_do_not_use_core_value_rules_or_product_differ()
    {
        string[] forbidden = ["AuditValueExpressions", "TableKey", "CatalogReader", "CatalogQueries", "DbCatalog", "NetDiffer", "DbRipple.Diff", "DbRipple.Core", "INetDiffer"];
        var dir = Path.Combine(RepoPaths.Root, "eval", "DbRipple.Eval");
        var hits = Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: Path.GetFileName(f), Line: i + 1, Text: l)))
            .Where(x => !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !x.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(x => forbidden.Any(w => x.Text.Contains(w, StringComparison.Ordinal)))
            .Select(x => $"{x.File}:{x.Line}: {x.Text.Trim()}")
            .ToList();
        Assert.Empty(hits);
        var csproj = File.ReadAllText(Path.Combine(dir, "DbRipple.Eval.csproj"));
        Assert.DoesNotContain(ProductAssemblies, a => csproj.Contains(a + ".csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Hook_implements_contract_and_csv_columns_are_unique()
    {
        Assert.IsAssignableFrom<IEvaluationHook>(new EvaluationHook(new HashSnapshotComparer()));
        foreach (var cols in new[] { MetricsCsv.CaseColumns, MetricsCsv.M1Columns, MetricsCsv.M2Columns, MetricsCsv.M3Columns, MetricsCsv.M4Columns, MetricsCsv.M5Columns, MetricsCsv.SuiteColumns })
            Assert.Equal(cols.Count, cols.Distinct().Count());
        Assert.Equal("a,\"b,c\",\"d\"\"e\"", MetricsCsv.Line(["a", "b,c", "d\"e"]));
        Assert.Equal("", MetricsCsv.Rate(1, 0));
        Assert.Equal("0.5000", MetricsCsv.Rate(1, 2));
    }
}
