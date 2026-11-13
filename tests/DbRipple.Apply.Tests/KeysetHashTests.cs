using DbRipple.Contracts;
using static DbRipple.Apply.Tests.TestData;

namespace DbRipple.Apply.Tests;

/// <summary>KEYSET_HASH 쿼리 생성·전후 비교, [적용] 원본 사전 경고 문구(DB 없음).</summary>
public sealed class KeysetHashTests
{
    static HashColumn Col(string name, string type = "int", bool clr = false, bool computed = false) => new(name, type, clr, computed);

    static readonly IReadOnlySet<string> NoPreview = new HashSet<string>();

    [Theory]
    [InlineData("int", false, false, true)]
    [InlineData("nvarchar", false, false, true)]
    [InlineData("varbinary", false, false, true)]
    [InlineData("datetime2", false, false, true)]
    [InlineData("xml", false, false, false)]
    [InlineData("text", false, false, false)]
    [InlineData("ntext", false, false, false)]
    [InlineData("image", false, false, false)]
    [InlineData("sql_variant", false, false, false)]
    [InlineData("geography", true, false, false)]
    [InlineData("geometry", true, false, false)]
    [InlineData("hierarchyid", true, false, false)]
    [InlineData("MyClrType", true, false, false)]
    [InlineData("timestamp", false, false, false)]
    [InlineData("int", false, true, false)]          // 계산 열
    public void Comparable_columns_exclude_types_binary_checksum_ignores_or_rejects(string type, bool clr, bool computed, bool comparable) =>
        Assert.Equal(comparable, KeysetVerifier.IsComparable(Col("c", type, clr, computed)));

    [Fact]
    public void States_sql_hashes_only_comparable_columns_and_quotes_names()
    {
        var t = new HashTarget("s]x", "Ta'b", false, [Col("id"), Col("a]b", "nvarchar"), Col("doc", "xml"), Col("g", "geography", clr: true), Col("rv", "timestamp"), Col("calc", computed: true)]);

        var sql = KeysetVerifier.StatesSql([t], NoPreview);

        Assert.Equal("SELECT N's]x.Ta''b' AS t, COUNT_BIG(*) AS n, CHECKSUM_AGG(BINARY_CHECKSUM([id], [a]]b])) AS h FROM [s]]x].[Ta'b];", sql);
    }

    [Fact]
    public void States_sql_counts_only_for_preview_tables_and_tables_without_comparable_columns()
    {
        var preview = new HashSet<string> { "dbo.T" };
        var sql = KeysetVerifier.StatesSql(
            [new HashTarget("dbo", "T", false, [Col("id")]), new HashTarget("dbo", "Docs", false, [Col("x", "xml")]), new HashTarget("dbo", "Mem", true, [Col("id")])],
            preview);

        var parts = sql.TrimEnd(';').Split("\nUNION ALL\n");
        Assert.Equal(3, parts.Length);
        Assert.Equal("SELECT N'dbo.T' AS t, COUNT_BIG(*) AS n, CAST(NULL AS int) AS h FROM [dbo].[T]", parts[0]);
        Assert.Equal("SELECT N'dbo.Docs' AS t, COUNT_BIG(*) AS n, CAST(NULL AS int) AS h FROM [dbo].[Docs]", parts[1]);
        Assert.EndsWith("FROM [dbo].[Mem] WITH (SNAPSHOT)", parts[2]);         // 메모리 최적화 테이블은 명시 트랜잭션에서 SNAPSHOT
    }

    [Fact]
    public void Targets_sql_reads_user_tables_only_and_writes_nothing()
    {
        var sql = KeysetVerifier.TargetsSql;
        Assert.Contains("is_ms_shipped = 0", sql);
        Assert.Contains("is_external = 0", sql);
        foreach (var w in new[] { "INSERT", "UPDATE ", "DELETE", "CREATE", "ALTER", "DROP", "EXEC" })
            Assert.DoesNotContain(w, sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Excluded_column_notes_list_uncomparable_columns_of_outside_tables()
    {
        var targets = new[]
        {
            new HashTarget("dbo", "T", false, [Col("doc", "xml")]),                  // 미리보기 테이블: 적지 않음
            new HashTarget("dbo", "Other", false, [Col("id"), Col("doc", "xml"), Col("calc", computed: true)]),
            new HashTarget("dbo", "OnlyXml", false, [Col("x", "xml")]),
        };
        var notes = KeysetVerifier.ExcludedColumnNotes(targets, new HashSet<string> { "dbo.T" });

        Assert.Equal(2, notes.Count);
        Assert.Contains("dbo.Other.doc(xml)", notes[0]);
        Assert.Contains("dbo.OnlyXml.x(xml)", notes[0]);
        Assert.DoesNotContain("dbo.T.", notes[0]);
        Assert.DoesNotContain("calc", notes[0]);
        Assert.Contains("dbo.OnlyXml", notes[1]);
        Assert.Empty(KeysetVerifier.ExcludedColumnNotes([new HashTarget("dbo", "A", false, [Col("id")])], NoPreview));
    }

    [Fact]
    public void Preview_tables_are_net_summary_and_net_change_tables()
    {
        var record = Record();
        Assert.Equal(["dbo.T"], KeysetVerifier.PreviewTables(record));
    }

    static Dictionary<string, TableState> S(params (string Table, long Count, int? Hash)[] rows) =>
        rows.ToDictionary(r => r.Table, r => new TableState(r.Count, r.Hash), StringComparer.Ordinal);

    [Fact]
    public void Same_states_pass()
    {
        var before = S(("dbo.T", 3, null), ("dbo.Other", 5, 77), ("dbo.Empty", 0, null));
        var (counts, mismatches) = KeysetVerifier.CompareStates(Record(), before, S(("dbo.T", 3, null), ("dbo.Other", 5, 77), ("dbo.Empty", 0, null)), new HashSet<string> { "dbo.T" });
        Assert.Empty(mismatches);
        Assert.Empty(counts);
    }

    [Fact]
    public void Outside_table_checksum_or_count_change_is_outside_table_changed()
    {
        var preview = new HashSet<string> { "dbo.T" };
        var before = S(("dbo.T", 3, null), ("dbo.A", 5, 77), ("dbo.B", 2, 10), ("dbo.C", 0, null));
        var after = S(("dbo.T", 3, null), ("dbo.A", 5, 78), ("dbo.B", 3, 10), ("dbo.C", 1, 42));

        var (counts, mismatches) = KeysetVerifier.CompareStates(Record(), before, after, preview);

        Assert.Equal(["dbo.A", "dbo.B", "dbo.C"], mismatches.Select(m => m.Table));
        Assert.All(mismatches, m => Assert.Equal(MismatchKind.OutsideTableChanged, m.Kind));
        Assert.Contains("행 수가 그대로인 UPDATE", mismatches[0].Message);
        Assert.Equal("count=0,checksum=NULL", mismatches[2].Expected);
        Assert.Equal(["dbo.B", "dbo.C"], counts.Select(c => c.Table));       // 건수 변화가 있는 테이블만
    }

    [Fact]
    public void Preview_table_count_delta_is_compared_with_net_summary()
    {
        var record = Record([Deleted("dbo.T", "1", Row(("id", "1"), ("v", "1")))]);
        var preview = KeysetVerifier.PreviewTables(record);

        var ok = KeysetVerifier.CompareStates(record, S(("dbo.T", 3, null)), S(("dbo.T", 2, null)), preview);
        Assert.Empty(ok.Mismatches);
        Assert.Equal(-1, Assert.Single(ok.Counts).ExpectedDelta);

        var bad = KeysetVerifier.CompareStates(record, S(("dbo.T", 3, null)), S(("dbo.T", 3, null)), preview);
        var m = Assert.Single(bad.Mismatches);
        Assert.Equal(MismatchKind.KeysetTableCount, m.Kind);
        Assert.Equal("-1", m.Expected);
        Assert.Equal("0", m.Actual);
    }

    [Fact]
    public void Origin_warnings_describe_each_flag_and_unknown()
    {
        Assert.Equal([OriginWarnings.Unknown], OriginWarnings.Describe(null));
        Assert.Empty(OriginWarnings.Describe(new OriginDbFlags(false, false, false, false, false, false)));
        Assert.Equal([OriginWarnings.Trustworthy, OriginWarnings.Replication, OriginWarnings.Cdc, OriginWarnings.Broker],
            OriginWarnings.Describe(new OriginDbFlags(true, true, false, false, true, true)));
        Assert.Equal([OriginWarnings.Replication], OriginWarnings.Describe(new OriginDbFlags(false, false, true, false, false, false)));
        Assert.Equal([OriginWarnings.Replication], OriginWarnings.Describe(new OriginDbFlags(false, false, false, true, false, false)));
        Assert.Contains("sys.databases", OriginWarnings.Sql);
        Assert.Contains("DB_ID()", OriginWarnings.Sql);
    }
}
