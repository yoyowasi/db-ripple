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

    [Fact]
    public void Exec_same_return_value_and_result_set_count_has_no_mismatch()
    {
        var (rows, mismatches) = ApplyMatcher.CompareStatements([ExecStatement(returnValue: 3, resultSets: 2)], [ExecStatement(returnValue: 3, resultSets: 2)]);

        Assert.Empty(mismatches);
        var row = Assert.Single(rows);
        Assert.Null(row.Preview);   // EXEC 행 수는 양쪽 다 null(A9)
        Assert.Null(row.Actual);
    }

    [Fact]
    public void Exec_return_value_and_result_set_count_differences_are_separate_mismatches()
    {
        var (_, mismatches) = ApplyMatcher.CompareStatements([ExecStatement(returnValue: 0, resultSets: 1)], [ExecStatement(returnValue: -1, resultSets: 0)]);

        Assert.Equal(2, mismatches.Count);
        Assert.Contains(mismatches, m => m is { Kind: MismatchKind.ProcReturnValue, StatementIndex: 1, Expected: "0", Actual: "-1", Table: null });
        Assert.Contains(mismatches, m => m is { Kind: MismatchKind.ProcResultSetCount, StatementIndex: 1, Expected: "1", Actual: "0" });
    }

    [Fact]
    public void Exec_failed_on_origin_has_no_actual_return_value()
    {
        var (_, mismatches) = ApplyMatcher.CompareStatements([ExecStatement()], [ExecStatement(returnValue: 0, resultSets: 0) with { Status = StatementStatus.Failed }]);

        Assert.Contains(mismatches, m => m is { Kind: MismatchKind.ProcReturnValue, Expected: "0", Actual: null });
        Assert.Contains(mismatches, m => m is { Kind: MismatchKind.ProcResultSetCount, Expected: "0", Actual: null });
    }

    [Fact]
    public void Exec_nondeterministic_return_value_compares_only_null_ness()
    {
        // 반환 값이 SCOPE_IDENTITY 등에 기대면(분석기 판정) 샌드박스와 원본 값이 달라도 일치, NULL 여부가 다르면 불일치(2026-11-17).
        var plan = ExecAnalyzed(returnNondeterministic: true).Plan;
        var preview = ExecStatement(returnValue: 7);   // 저장된 미리보기에는 Plan이 없다(JSON에 저장 안 됨) → 실제 쪽 Plan으로 판정

        var (_, same) = ApplyMatcher.CompareStatements([preview], [ExecStatement(returnValue: 1042) with { Plan = plan }]);
        var (_, nullDiff) = ApplyMatcher.CompareStatements([preview], [ExecStatement(returnValue: null) with { Plan = plan }]);
        var (_, strict) = ApplyMatcher.CompareStatements([preview], [ExecStatement(returnValue: 1042) with { Plan = ExecAnalyzed().Plan }]);

        Assert.Empty(same);
        var m = Assert.Single(nullDiff);
        Assert.Equal((MismatchKind.ProcReturnValue, "7", (string?)null), (m.Kind, m.Expected, m.Actual));
        Assert.Contains("NULL 여부", m.Message);
        Assert.Equal(MismatchKind.ProcReturnValue, Assert.Single(strict).Kind);
    }

    [Fact]
    public void Exec_literal_return_values_are_compared_exactly_even_when_nondeterministic()
    {
        // PR #11 2차 검토 1: IF … RETURN 5; … RETURN SCOPE_IDENTITY()처럼 리터럴 상태 코드가 섞이면 리터럴 값은 그대로 비교한다.
        var plan = ExecAnalyzed(returnNondeterministic: true).Plan! with { LiteralReturnValues = [0, 5] };
        IReadOnlyList<ApplyMismatch> Run(int? preview, int? actual) =>
            ApplyMatcher.CompareStatements([ExecStatement(returnValue: preview)], [ExecStatement(returnValue: actual) with { Plan = plan }]).Mismatches;

        Assert.Empty(Run(1042, 7));     // 둘 다 IDENTITY 경로: NULL 여부만
        Assert.Empty(Run(5, 5));
        Assert.Empty(Run(0, 0));
        var m = Assert.Single(Run(1042, 5));   // 원본은 상태 코드 경로
        Assert.Equal((MismatchKind.ProcReturnValue, "1042", "5"), (m.Kind, m.Expected, m.Actual));
        Assert.DoesNotContain("NULL 여부", m.Message);
        Assert.Single(Run(0, 1042));
        Assert.Contains("NULL 여부", Assert.Single(Run(1042, null)).Message);
    }

    // PR #11 2차 검토 2: RowChange.volatile은 그 행을 만든 행 변경 것만 쓴다. 같은 배치의 다른 문장이 같은 열에 쓴 결정적 값은 값으로 비교한다.
    static Firing UpdateFiring(string id, int statement, string table = "dbo.T", ChangeOperation op = ChangeOperation.Update) => new()
    {
        Id = id, CaptureSeq = long.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture), StatementIndex = statement, Depth = 1, Table = table,
        Operation = op, RowCount = 1, ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
    };

    static RowChange Rc(string firing, string table, string id, IReadOnlyDictionary<string, string?>? after, ChangeOperation op, params (string Column, VolatileReason Reason)[] volatileCells) => new()
    {
        Id = "rc-" + firing[2..] + "-1", FiringId = firing, Operation = op, Key = Key(id), After = after,
        Volatile = volatileCells.Select(v => new VolatileCell { Column = v.Column, Reason = v.Reason }).ToList(),
        Pairing = Pairing.ByPk, FinalAfterSource = FinalAfterSource.Audit,
    };

    static NetChange Explained(NetChange n, params string[] firings) => n with { ExplainedBy = new ExplainedBy { FiringIds = firings } };

    static PreviewRecord TwoStatementPreview(bool explained)
    {
        // 문장 1(f-1)은 id=1의 v를 비결정 값으로, 문장 2(f-2)는 id=2의 v를 결정적 값으로 바꿨다.
        var net = new List<NetChange>
        {
            Updated("dbo.T", "1", Row(("id", "1"), ("v", "1")), Row(("id", "1"), ("v", "10")), "v"),
            Updated("dbo.T", "2", Row(("id", "2"), ("v", "5")), Row(("id", "2"), ("v", "6")), "v"),
        };
        if (explained) net = [Explained(net[0], "f-1"), Explained(net[1], "f-2")];
        return Record(net, [Statement(1, 1), Statement(2, 1)],
            firings: [UpdateFiring("f-1", 1), UpdateFiring("f-2", 2)],
            rowChanges:
            [
                Rc("f-1", "dbo.T", "1", Row(("id", "1"), ("v", "10")), ChangeOperation.Update, ("v", VolatileReason.StatementFunction)),
                Rc("f-2", "dbo.T", "2", Row(("id", "2"), ("v", "6")), ChangeOperation.Update),
            ]);
    }

    static readonly List<NetChange> TwoStatementActual =
    [
        Updated("dbo.T", "1", Row(("id", "1"), ("v", "1")), Row(("id", "1"), ("v", "11")), "v"),
        Updated("dbo.T", "2", Row(("id", "2"), ("v", "5")), Row(("id", "2"), ("v", "7")), "v"),
    ];

    [Fact]
    public void Volatile_mark_applies_only_to_rows_of_the_marked_row_change()
    {
        var preview = TwoStatementPreview(explained: true);

        var outcome = ApplyMatcher.Match(new MatchInput { Preview = preview, Actual = preview.Result.Statements, ActualNet = TwoStatementActual, Baseline = Context() });

        var m = Assert.Single(outcome.Mismatches);   // id=1은 비결정(존재·형식만), id=2는 값 비교
        Assert.Equal((MismatchKind.RowValue, "2", "v", "6", "7"), (m.Kind, m.Key!.Values[0], m.Column, m.Expected, m.Actual));
        Assert.Contains("dbo.T.v", outcome.VolatileIgnored);
    }

    [Fact]
    public void Net_change_without_explanation_falls_back_to_table_level_volatile()
    {
        // 설명 연결(ExplainedBy)이 없는 기록(C12 출력·예전 픽스처)은 예전처럼 테이블 단위로 본다.
        var preview = TwoStatementPreview(explained: false);

        var outcome = ApplyMatcher.Match(new MatchInput { Preview = preview, Actual = preview.Result.Statements, ActualNet = TwoStatementActual, Baseline = Context() });

        Assert.Empty(outcome.Mismatches);
    }

    [Fact]
    public void Row_volatile_uses_the_firing_rows_when_no_row_change_key_matches()
    {
        // 키 표기가 달라 행 변경과 맞출 수 없으면 그 행을 만든 묶음의 같은 테이블 행 변경 표시를 쓴다(다른 묶음 표시는 쓰지 않는다).
        var preview = TwoStatementPreview(explained: true);
        preview = preview with
        {
            RowChanges =
            [
                Rc("f-1", "dbo.T", "x1", null, ChangeOperation.Update, ("v", VolatileReason.StatementFunction)),
                Rc("f-2", "dbo.T", "x2", null, ChangeOperation.Update),
            ],
        };

        var outcome = ApplyMatcher.Match(new MatchInput { Preview = preview, Actual = preview.Result.Statements, ActualNet = TwoStatementActual, Baseline = Context() });

        Assert.Equal("2", Assert.Single(outcome.Mismatches, m => m.Kind == MismatchKind.RowValue).Key!.Values[0]);
    }

    [Theory]
    [InlineData("7", "007")]   // 숫자 표기만 다름
    [InlineData("a", "A")]     // 대소문자만 다름
    public void Row_change_key_is_matched_ordinally(string markedKey, string rowKey)
    {
        // PR #15 검토 4: 같은 묶음 안에서 표기만 비슷한 다른 행의 표시를 붙이지 않는다.
        var net = Explained(Updated("dbo.T", rowKey, Row(("id", rowKey), ("v", "5")), Row(("id", rowKey), ("v", "6")), "v"), "f-1");
        var preview = Record([net], [Statement(1, 2)], firings: [UpdateFiring("f-1", 1)],
            rowChanges:
            [
                Rc("f-1", "dbo.T", markedKey, Row(("id", markedKey), ("v", "10")), ChangeOperation.Update, ("v", VolatileReason.StatementFunction)),
                Rc("f-1", "dbo.T", rowKey, Row(("id", rowKey), ("v", "6")), ChangeOperation.Update),
            ]);
        var actual = new List<NetChange> { Updated("dbo.T", rowKey, Row(("id", rowKey), ("v", "5")), Row(("id", rowKey), ("v", "7")), "v") };

        var outcome = ApplyMatcher.Match(new MatchInput { Preview = preview, Actual = [Statement(1, 2)], ActualNet = actual, Baseline = Context() });

        var m = Assert.Single(outcome.Mismatches, m => m.Kind == MismatchKind.RowValue);
        Assert.Equal(("v", "6", "7"), (m.Column, m.Expected, m.Actual));
    }

    [Fact]
    public void Paired_identity_rows_compare_values_with_their_own_volatile_marks()
    {
        // dbo.C 새 IDENTITY 행 두 개: f-1 행의 qty는 비결정, f-2 행의 qty는 결정적. 짝짓기 서명은 테이블 단위(qty 제외), 값 비교는 행 단위.
        var preview = Record(
            [Explained(Inserted("dbo.C", "500", C("500", "1", "3")), "f-1"), Explained(Inserted("dbo.C", "501", C("501", "2", "4")), "f-2")],
            [Statement(1, 1, "dbo.C", ChangeOperation.Insert), Statement(2, 1, "dbo.C", ChangeOperation.Insert)],
            firings: [UpdateFiring("f-1", 1, "dbo.C", ChangeOperation.Insert), UpdateFiring("f-2", 2, "dbo.C", ChangeOperation.Insert)],
            rowChanges:
            [
                Rc("f-1", "dbo.C", "500", C("500", "1", "3"), ChangeOperation.Insert, ("id", VolatileReason.Identity), ("qty", VolatileReason.StatementFunction)),
                Rc("f-2", "dbo.C", "501", C("501", "2", "4"), ChangeOperation.Insert, ("id", VolatileReason.Identity)),
            ]);
        var actual = new List<NetChange> { Inserted("dbo.C", "900", C("900", "1", "8")), Inserted("dbo.C", "901", C("901", "2", "9")) };

        var outcome = ApplyMatcher.Match(new MatchInput { Preview = preview, Actual = preview.Result.Statements, ActualNet = actual, Baseline = Context() });

        Assert.Equal(2, outcome.IdentityPairs.Count);
        var m = Assert.Single(outcome.Mismatches);
        Assert.Equal((MismatchKind.RowValue, "501", "qty", "4", "9"), (m.Kind, m.Key!.Values[0], m.Column, m.Expected, m.Actual));
    }

    [Fact]
    public void Dml_statement_does_not_compare_return_value()
    {
        var (_, mismatches) = ApplyMatcher.CompareStatements([Statement(1, 1)], [Statement(1, 1) with { ReturnValue = 9, ResultSetCount = 4 }]);

        Assert.Empty(mismatches);
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
