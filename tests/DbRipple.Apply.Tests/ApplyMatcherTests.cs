using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using static DbRipple.Apply.Tests.TestData;

namespace DbRipple.Apply.Tests;

/// <summary>D7 일치 규칙 단위 테스트(DB 없음).</summary>
public sealed class ApplyMatcherTests
{
    static MatchOutcome Match(PreviewRecord preview, IReadOnlyList<NetChange> actualNet, IReadOnlyList<StatementResult>? actual = null) =>
        ApplyMatcher.Match(new MatchInput { Preview = preview, Actual = actual ?? preview.Result.Statements, ActualNet = actualNet, Baseline = Context() });

    static Dictionary<string, string?> P(string id, string name, string? created) => Row(("id", id), ("name", name), ("created", created), ("label", "P" + id));
    static Dictionary<string, string?> C(string id, string pId, string qty) => Row(("id", id), ("p_id", pId), ("qty", qty));

    static PreviewRecord InsertPreview(IReadOnlyList<NetChange> net) =>
        Record(net, [Statement(1, 1, "dbo.P", ChangeOperation.Insert), Statement(2, 1, "dbo.C", ChangeOperation.Insert)]);

    [Fact]
    public void Same_update_matches_without_mismatch()
    {
        var preview = Record();
        var outcome = Match(preview, preview.NetChanges);

        Assert.Empty(outcome.Mismatches);
        Assert.True(outcome.NetSummaryMatch);
        Assert.Equal(1, Assert.Single(outcome.StatementRowCounts).Actual);
    }

    [Fact]
    public void Cl16_identity_rows_pair_and_child_fk_is_mapped()
    {
        // 미리보기 샌드박스: P.id 100, C.id 500(p_id 100). 원본: P.id 200, C.id 900(p_id 200). created(GETDATE)·label(id 기반 계산 열)도 다르다.
        var preview = InsertPreview([Inserted("dbo.P", "100", P("100", "a", "2026-11-11T10:00:00")), Inserted("dbo.C", "500", C("500", "100", "3"))]);
        var actual = new List<NetChange> { Inserted("dbo.C", "900", C("900", "200", "3")), Inserted("dbo.P", "200", P("200", "a", "2026-11-11T10:05:00.1234567")) };

        var outcome = Match(preview, actual);

        Assert.Empty(outcome.Mismatches);
        Assert.Equal(2, outcome.IdentityPairs.Count);
        var p = Assert.Single(outcome.IdentityPairs, x => x.Table == "dbo.P");
        Assert.Equal("100", p.PreviewKey.Values[0]);
        Assert.Equal("200", p.ActualKey.Values[0]);
        var c = Assert.Single(outcome.IdentityPairs, x => x.Table == "dbo.C");
        Assert.Equal("900", c.ActualKey.Values[0]);
        Assert.Contains("dbo.P.id", outcome.VolatileIgnored);
        Assert.Contains("dbo.P.created", outcome.VolatileIgnored);
        Assert.Contains("dbo.C.id", outcome.VolatileIgnored);
    }

    [Fact]
    public void Child_pointing_to_other_new_parent_is_row_value_mismatch()
    {
        var preview = Record(
            [Inserted("dbo.P", "100", P("100", "a", "x1")), Inserted("dbo.P", "101", P("101", "b", "x1")), Inserted("dbo.C", "500", C("500", "100", "3"))],
            [Statement(1, 2, "dbo.P", ChangeOperation.Insert), Statement(2, 1, "dbo.C", ChangeOperation.Insert)]);
        var actual = new List<NetChange>
        {
            Inserted("dbo.P", "200", P("200", "a", "x2")), Inserted("dbo.P", "201", P("201", "b", "x2")),
            Inserted("dbo.C", "900", C("900", "201", "3")), // 원본에서는 b(201)를 가리킴: 짝 매핑으로는 200이어야 함
        };

        var outcome = Match(preview, actual);

        // p_id는 부모 짝 매핑(100 → 200)으로 치환해 짝짓기 서명에 쓰이므로(부모 먼저) 자식이 짝을 못 찾고, 원본 자식은 예상 밖 행이 된다
        Assert.Contains(outcome.Mismatches, x => x.Table == "dbo.C" && x.Kind == MismatchKind.IdentityUnpaired && x.Key!.Values[0] == "500");
        Assert.Contains(outcome.Mismatches, x => x.Table == "dbo.C" && x.Kind == MismatchKind.RowUnexpected && x.Key!.Values[0] == "900");
        Assert.Equal(2, outcome.IdentityPairs.Count(x => x.Table == "dbo.P"));
    }

    [Fact]
    public void Same_value_identity_rows_pair_in_identity_order_and_children_follow()
    {
        // INSERT ... SELECT로 IDENTITY만 다른 같은 값 P 3행, 각 P를 가리키는 C 3행. 원본 목록 순서는 섞여 있어도 IDENTITY 오름차순으로 짝짓는다.
        var preview = Record(
            [
                Inserted("dbo.P", "100", P("100", "a", "x1")), Inserted("dbo.P", "101", P("101", "a", "x1")), Inserted("dbo.P", "102", P("102", "a", "x1")),
                Inserted("dbo.C", "500", C("500", "100", "1")), Inserted("dbo.C", "501", C("501", "101", "2")), Inserted("dbo.C", "502", C("502", "102", "3")),
            ],
            [Statement(1, 3, "dbo.P", ChangeOperation.Insert), Statement(2, 3, "dbo.C", ChangeOperation.Insert)]);
        var actual = new List<NetChange>
        {
            Inserted("dbo.P", "202", P("202", "a", "x2")), Inserted("dbo.P", "200", P("200", "a", "x2")), Inserted("dbo.P", "201", P("201", "a", "x2")),
            Inserted("dbo.C", "902", C("902", "202", "3")), Inserted("dbo.C", "900", C("900", "200", "1")), Inserted("dbo.C", "901", C("901", "201", "2")),
        };

        var outcome = Match(preview, actual);

        Assert.Empty(outcome.Mismatches);
        var parents = outcome.IdentityPairs.Where(x => x.Table == "dbo.P").Select(x => (x.PreviewKey.Values[0], x.ActualKey.Values[0])).OrderBy(x => x.Item1).ToList();
        Assert.Equal([("100", "200"), ("101", "201"), ("102", "202")], parents);
        // 자식은 p_id를 각자 짝 매핑(100→200, 101→201, 102→202)으로 치환한 서명으로 짝지어진다
        var children = outcome.IdentityPairs.Where(x => x.Table == "dbo.C").Select(x => (x.PreviewKey.Values[0], x.ActualKey.Values[0])).OrderBy(x => x.Item1).ToList();
        Assert.Equal([("500", "900"), ("501", "901"), ("502", "902")], children);
        Assert.Contains(ApplyMatcher.IdentityOrderLimitation, outcome.Limitations);
    }

    [Fact]
    public void Same_value_identity_rows_with_different_counts_are_unpaired()
    {
        var preview = Record([Inserted("dbo.P", "100", P("100", "a", "x")), Inserted("dbo.P", "101", P("101", "a", "x"))], [Statement(1, 2, "dbo.P", ChangeOperation.Insert)]);
        var actual = new List<NetChange> { Inserted("dbo.P", "200", P("200", "a", "y")), Inserted("dbo.P", "201", P("201", "a", "y")), Inserted("dbo.P", "202", P("202", "a", "y")) };

        var outcome = Match(preview, actual, [Statement(1, 3, "dbo.P", ChangeOperation.Insert)]);

        Assert.Equal(2, outcome.Mismatches.Count(m => m.Kind == MismatchKind.IdentityUnpaired));
        Assert.Empty(outcome.IdentityPairs);
        Assert.Empty(outcome.Limitations);
    }

    [Fact]
    public void Same_value_identity_rows_out_of_order_children_are_row_value_mismatch()
    {
        // dbo.D(id int PK, p_id int FK → dbo.P.id): 키가 있는 비 IDENTITY 자식. 원본에서는 같은 값 P의 IDENTITY 순서와 자식 연결이 어긋났다.
        var preview = Record(
            [
                Inserted("dbo.P", "100", P("100", "a", "x1")), Inserted("dbo.P", "101", P("101", "a", "x1")),
                Inserted("dbo.D", "1", Row(("id", "1"), ("p_id", "100"))), Inserted("dbo.D", "2", Row(("id", "2"), ("p_id", "101"))),
            ],
            [Statement(1, 2, "dbo.P", ChangeOperation.Insert), Statement(2, 2, "dbo.D", ChangeOperation.Insert)]);
        var actual = new List<NetChange>
        {
            Inserted("dbo.P", "200", P("200", "a", "x2")), Inserted("dbo.P", "201", P("201", "a", "x2")),
            Inserted("dbo.D", "1", Row(("id", "1"), ("p_id", "201"))), Inserted("dbo.D", "2", Row(("id", "2"), ("p_id", "200"))),
        };

        var outcome = ApplyMatcher.Match(new MatchInput { Preview = preview, Actual = preview.Result.Statements, ActualNet = actual, Baseline = ContextWithD() });

        Assert.Equal(2, outcome.IdentityPairs.Count(x => x.Table == "dbo.P"));
        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.RowValue && m.Table == "dbo.D" && m.Key!.Values[0] == "1" && m.Column == "p_id" && m.Expected == "200" && m.Actual == "201");
        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.RowValue && m.Table == "dbo.D" && m.Key!.Values[0] == "2" && m.Column == "p_id" && m.Expected == "201" && m.Actual == "200");
        Assert.All(outcome.Mismatches, m => Assert.Equal(MismatchKind.RowValue, m.Kind));
    }

    /// <summary>기본 기준선 + dbo.D(id int PK, p_id int FK → dbo.P.id).</summary>
    static BaselineContext ContextWithD()
    {
        var context = Context();
        var c = context.Catalog;
        var catalog = c with
        {
            Tables = [.. c.Tables, new CatalogTable(6, "dbo", "D", TemporalType.None, false)],
            Columns = [.. c.Columns, new CatalogColumn(6, 1, "id", "int", false, false, false, false, false, null), new CatalogColumn(6, 2, "p_id", "int", false, false, false, false, false, null)],
            KeyIndexes = [.. c.KeyIndexes, new CatalogIndexColumn(6, 1, "PK_D", true, true, 1, "id", false)],
            ForeignKeys = [.. c.ForeignKeys, new CatalogForeignKeyColumn(101, "FK_D_P", 6, 1, "NO_ACTION", "NO_ACTION", false, false, 1, "p_id", "id")],
        };
        var manifest = context.Manifest with
        {
            Tables = [.. context.Manifest.Tables, new ManifestTable("dbo.D", true, new AuditOrderInfo("LAST", "LAST", "LAST"), [], [])],
        };
        return context with { Catalog = catalog, Manifest = manifest };
    }

    [Fact]
    public void Volatile_column_compares_presence_and_format_only()
    {
        var preview = Record([Inserted("dbo.P", "100", P("100", "a", "2026-11-11T10:00:00"))], [Statement(1, 1, "dbo.P", ChangeOperation.Insert)]);
        var actual = new List<NetChange> { Inserted("dbo.P", "200", P("200", "a", null)) };

        var outcome = Match(preview, actual);

        var m = Assert.Single(outcome.Mismatches);
        Assert.Equal(MismatchKind.VolatileFormat, m.Kind);
        Assert.Equal("created", m.Column);
    }

    [Fact]
    public void Change_in_table_missing_from_preview_is_unexpected_table()
    {
        var preview = Record();
        var actual = preview.NetChanges.Append(Inserted("dbo.X", "7", Row(("id", "7")))).ToList();

        var outcome = Match(preview, actual);

        var m = Assert.Single(outcome.Mismatches);
        Assert.Equal(MismatchKind.UnexpectedTable, m.Kind);
        Assert.Equal("dbo.X", m.Table);
        Assert.False(outcome.NetSummaryMatch);
    }

    [Fact]
    public void Extra_row_changes_net_summary_and_row_unexpected()
    {
        var preview = Record();
        var actual = preview.NetChanges.Append(Updated("dbo.T", "2", Row(("id", "2"), ("v", "5")), Row(("id", "2"), ("v", "2")), "v")).ToList();

        var outcome = Match(preview, actual, [Statement(1, 2)]);

        Assert.False(outcome.NetSummaryMatch);
        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.StatementRowCount && m.StatementIndex == 1 && m.Expected == "1" && m.Actual == "2");
        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.NetSummary && m.Table == "dbo.T" && m.Expected == "0,1,0,0,0" && m.Actual == "0,2,0,0,0");
        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.RowUnexpected && m.Key!.Values[0] == "2");
    }

    [Fact]
    public void Different_after_value_is_row_value()
    {
        var preview = Record();
        var actual = new List<NetChange> { Updated("dbo.T", "1", Row(("id", "1"), ("v", "1")), Row(("id", "1"), ("v", "3")), "v") };

        var outcome = Match(preview, actual);

        var m = Assert.Single(outcome.Mismatches);
        Assert.Equal(MismatchKind.RowValue, m.Kind);
        Assert.Equal(("v", "2", "3"), (m.Column, m.Expected, m.Actual));
    }

    [Fact]
    public void Missing_row_change_is_row_missing()
    {
        var preview = Record();
        var outcome = Match(preview, [], [Statement(1, 0)]);

        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.RowMissing && m.Table == "dbo.T");
        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.StatementRowCount);
    }

    [Fact]
    public void Heap_rows_compare_as_multiset_counts()
    {
        var image = Row(("a", "1"), ("b", "2"));
        var preview = Record([Multiset("dbo.H", NetChangeKind.MultisetAdded, 2, image)], [Statement(1, 2, "dbo.H", ChangeOperation.Insert)]);

        Assert.Empty(Match(preview, [Multiset("dbo.H", NetChangeKind.MultisetAdded, 2, image)]).Mismatches);

        var outcome = Match(preview, [Multiset("dbo.H", NetChangeKind.MultisetAdded, 1, image)], [Statement(1, 1, "dbo.H", ChangeOperation.Insert)]);
        Assert.Contains(outcome.Mismatches, m => m.Kind == MismatchKind.RowMissing && m.Table == "dbo.H" && m.Expected == "2" && m.Actual == "1");
    }

    [Fact]
    public void Statement_failure_counts_actual_as_null()
    {
        var (rows, mismatches) = ApplyMatcher.CompareStatements([Statement(1, 1)], [Statement(1, null, status: StatementStatus.Failed)]);

        Assert.Null(Assert.Single(rows).Actual);
        Assert.Equal(MismatchKind.StatementRowCount, Assert.Single(mismatches).Kind);
    }

    [Theory]
    [InlineData(null, "NULL")]
    [InlineData("42", "INTEGER")]
    [InlineData("1.50", "NUMBER")]
    [InlineData("1.0000000000000000e+000", "NUMBER")]
    [InlineData("2026-11-11T10:00:00", "DATETIME")]
    [InlineData("2026-11-11T10:00:00.123+09:00", "DATETIME")]
    [InlineData("0E984725-C51C-4BF4-9960-E1C80E27ABA0", "GUID")]
    [InlineData("10:00:00.1234567", "TIME")]
    [InlineData("true", "BOOLEAN")]
    [InlineData("AAAAAAAAB9E=", "TEXT")]
    public void Format_class(string? value, string expected) => Assert.Equal(expected, MatchRules.FormatClass(value));
}
