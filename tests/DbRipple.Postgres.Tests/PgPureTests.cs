using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Execution;

namespace DbRipple.Postgres.Tests;

/// <summary>PostgreSQL 최소판 순수 테스트(DB 없이). 카탈로그·감사 기록은 손으로 만든다.</summary>
public class PgPureTests
{
    // s.parent(id) ← s.child(id, parent_id ON DELETE CASCADE) ← s.grandchild(id, child_id ON DELETE CASCADE), s.note(id, parent_id ON DELETE SET NULL), public.loose(a, b: 키 없음)
    const int Parent = 100, Child = 101, Grand = 102, Note = 103, Loose = 104;

    static DbCatalog Catalog() => new(
        Tables:
        [
            new(Parent, "s", "parent", TemporalType.None, false, "app"),
            new(Child, "s", "child", TemporalType.None, false, "app"),
            new(Grand, "s", "grandchild", TemporalType.None, false, "app"),
            new(Note, "s", "note", TemporalType.None, false, "app"),
            new(Loose, "public", "loose", TemporalType.None, false, "app"),
        ],
        Columns:
        [
            Col(Parent, 1, "id"), Col(Parent, 2, "name"),
            Col(Child, 1, "id"), Col(Child, 2, "parent_id"),
            Col(Grand, 1, "id"), Col(Grand, 2, "child_id"),
            Col(Note, 1, "id"), Col(Note, 2, "parent_id", nullable: true),
            Col(Loose, 1, "a", nullable: true), Col(Loose, 2, "b", nullable: true),
        ],
        KeyIndexes:
        [
            new(Parent, 1, "parent_pkey", true, true, 1, "id", false),
            new(Child, 1, "child_pkey", true, true, 1, "id", false),
            new(Grand, 1, "grandchild_pkey", true, true, 1, "id", false),
            new(Note, 1, "note_pkey", true, true, 1, "id", false),
        ],
        ForeignKeys:
        [
            new(200, "child_parent_fk", Child, Parent, "CASCADE", "NO_ACTION", false, false, 1, "parent_id", "id"),
            new(201, "grand_child_fk", Grand, Child, "CASCADE", "NO_ACTION", false, false, 1, "child_id", "id"),
            new(202, "note_parent_fk", Note, Parent, "SET_NULL", "NO_ACTION", false, false, 1, "parent_id", "id"),
        ],
        Triggers: [],
        Files: []);

    static CatalogColumn Col(int table, int id, string name, bool nullable = false) =>
        new(table, id, name, "integer", false, nullable, false, false, false, null);

    static BaselineContext Ctx(IReadOnlyList<TriggerStaticInfo>? triggers = null, IReadOnlyList<ExcludedTable>? excluded = null)
    {
        var cat = Catalog();
        var last = new AuditOrderInfo(CaptureManifest.Last, CaptureManifest.Last, CaptureManifest.Last);
        return new BaselineContext
        {
            OriginDb = "shop", SandboxDb = "shop_dbr_sandbox", SnapshotDb = "shop_dbr_sandbox",
            BackupFinishedAt = DateTimeOffset.UnixEpoch,
            Fingerprint = new Fingerprint("17.2", "PostgreSQL", 17, false, 1, 0, 0, "C"),
            FingerprintMatch = true,
            Catalog = cat,
            Triggers = triggers ?? [],
            Manifest = new CaptureManifest("pg-test",
                cat.Tables.Where(t => excluded?.Any(e => e.Table == t.Schema + "." + t.Name) != true)
                    .Select(t => new ManifestTable(t.Schema + "." + t.Name, true, last, [], [])).ToList(),
                excluded ?? [], []),
        };
    }

    static TriggerStaticInfo Trig(string table, string name, ChangeOperation[] events, string[]? writes = null, bool unknown = false, string[]? external = null) => new()
    {
        Trigger = table.Split('.')[0] + "." + name, ObjectId = name.GetHashCode() & 0xffff, Table = table, Events = events,
        IsInsteadOf = false, IsDisabled = false, IsEncrypted = false, IsAuditTrigger = false,
        WriteTargets = writes ?? [], WriteTargetsUnknown = unknown, HasDynamicSql = unknown, HasRollback = false, ExternalSideEffects = external ?? [],
    };

    static AnalysisResult Analyze(string sql, BaselineContext? ctx = null) => new PgBatchAnalyzer().Analyze(sql, ctx ?? Ctx());

    // ---------------- 문장 나누기 ----------------

    [Fact]
    public void Splits_on_semicolons_outside_quotes_comments_and_dollar_strings()
    {
        var sql = "UPDATE s.parent SET name = 'a;b' WHERE id = 1; -- x;y\nDELETE FROM s.note WHERE id = 2 /* ; */;\nINSERT INTO s.parent VALUES (3, $q$x;y$q$)";
        var (parts, error, _) = PgSqlLexer.Split(sql);
        Assert.Null(error);
        Assert.Equal(3, parts.Count);
        Assert.StartsWith("UPDATE", parts[0].Sql);
        Assert.StartsWith("DELETE", parts[1].Sql);
        Assert.EndsWith("$q$x;y$q$)", parts[2].Sql);
        Assert.Equal(parts[0].Sql.Length, parts[0].Cleaned.Length);
    }

    [Fact]
    public void Positional_parameter_is_not_a_dollar_quote_and_empty_pieces_are_dropped()
    {
        var (parts, error, _) = PgSqlLexer.Split(";; DELETE FROM s.note WHERE id = $1;  -- only comment\n");
        Assert.Null(error);
        Assert.Single(parts);
    }

    [Theory]
    [InlineData("UPDATE s.parent SET name = 'abc")]
    [InlineData("DELETE FROM s.note /* never closed")]
    [InlineData("INSERT INTO s.parent VALUES ($$x)")]
    [InlineData("UPDATE \"s.parent SET name = 1")]
    public void Unterminated_quote_or_comment_is_an_error(string sql)
    {
        var (_, error, at) = PgSqlLexer.Split(sql);
        Assert.NotNull(error);
        Assert.True(at >= 0);
    }

    [Fact]
    public void Escape_string_with_backslash_quote_stays_inside_the_string()
    {
        var (parts, error, _) = PgSqlLexer.Split("UPDATE s.parent SET name = E'it\\'s;fine' WHERE id = 1; DELETE FROM s.note");
        Assert.Null(error);
        Assert.Equal(2, parts.Count);
    }

    // ---------------- 판별 ----------------

    [Theory]
    [InlineData("INSERT INTO s.parent (id, name) VALUES (1, 'x')", ChangeOperation.Insert, "s.parent")]
    [InlineData("update ONLY s.parent set name = 'y'", ChangeOperation.Update, "s.parent")]
    [InlineData("DELETE FROM \"s\".\"note\" WHERE id = 1", ChangeOperation.Delete, "s.note")]
    [InlineData("DELETE FROM loose", ChangeOperation.Delete, "public.loose")]
    public void Accepts_plain_dml_on_covered_user_tables(string sql, ChangeOperation kind, string target)
    {
        var r = Analyze(sql);
        Assert.True(r.IsAccepted, string.Join(" / ", r.Rejects.Select(d => d.RuleId + " " + d.Message)));
        var st = Assert.Single(r.Statements);
        Assert.Equal(kind, st.Kind);
        Assert.Equal(target, st.Target);
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnPgMinimalScope);
    }

    [Theory]
    [InlineData("TRUNCATE s.parent", "REJECT_TRUNCATE")]
    [InlineData("MERGE INTO s.parent p USING s.child c ON true WHEN MATCHED THEN DELETE", "REJECT_MERGE")]
    [InlineData("CALL s.do_it()", "REJECT_EXEC")]
    [InlineData("DO $$ BEGIN END $$", "REJECT_EXEC")]
    [InlineData("COMMIT", "REJECT_TRANSACTION_CONTROL")]
    [InlineData("SET search_path = s", "REJECT_SET_OPTION")]
    [InlineData("DROP TABLE s.parent", "REJECT_DDL")]
    [InlineData("COPY s.parent FROM '/tmp/x'", "REJECT_BULK_INSERT")]
    [InlineData("SELECT * INTO s.copy FROM s.parent", "REJECT_SELECT_INTO")]
    [InlineData("WITH d AS (DELETE FROM s.child RETURNING *) DELETE FROM s.parent", "REJECT_NOT_ALLOWED")]
    [InlineData("VACUUM s.parent", "REJECT_ADMIN_COMMAND")]
    [InlineData("DELETE FROM dbr_audit.header", "REJECT_CAPTURE_STORE")]
    [InlineData("DELETE FROM parent", "REJECT_TARGET_NOT_USER_TABLE")]
    [InlineData("DELETE FROM pg_catalog.pg_class", "REJECT_TARGET_NOT_USER_TABLE")]
    [InlineData("UPDATE s.parent SET name = set_config('role', 'postgres', false)", "REJECT_ADMIN_COMMAND")]
    [InlineData("UPDATE s.parent SET name = pg_catalog.pg_read_file ('/etc/passwd')", "REJECT_EXTERNAL_ROWSET")]
    [InlineData("INSERT INTO s.parent SELECT * FROM dblink('x', 'select 1') AS t(id int, name text)", "REJECT_EXTERNAL_ROWSET")]
    public void Rejects_out_of_scope_statements(string sql, string rule)
    {
        var r = Analyze(sql);
        Assert.False(r.IsAccepted);
        Assert.Contains(r.Rejects, d => d.RuleId == rule);
    }

    [Fact]
    public void Function_name_inside_a_string_is_not_a_call()
    {
        Assert.True(Analyze("UPDATE s.parent SET name = 'set_config(x)' WHERE id = 1").IsAccepted);
    }

    [Fact]
    public void One_reject_stops_the_whole_script_but_parsed_statements_are_kept()
    {
        var r = Analyze("DELETE FROM s.note; TRUNCATE s.parent");
        Assert.False(r.IsAccepted);
        Assert.Single(r.Statements);
        Assert.Equal(2, Assert.Single(r.Rejects).StatementIndex);
    }

    [Fact]
    public void Excluded_partitioned_table_is_rejected()
    {
        var r = Analyze("DELETE FROM s.note", Ctx(excluded: [new ExcludedTable("s.note", "PARTITIONED_TABLE")]));
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectTargetNotUserTable && d.Message.Contains("PARTITIONED_TABLE"));
    }

    [Fact]
    public void Trigger_with_external_effect_reached_through_cascade_is_rejected()
    {
        var ctx = Ctx([Trig("s.grandchild", "t_mail", [ChangeOperation.Delete], external: ["pg_notify"])]);
        Assert.Contains(Analyze("DELETE FROM s.parent WHERE id = 1", ctx).Rejects, d => d.RuleId == AnalysisRules.RejectTriggerExternalEffect);
        Assert.True(Analyze("UPDATE s.parent SET name = 'x'", ctx).IsAccepted); // UPDATE는 연쇄가 없다(NO_ACTION)
    }

    [Fact]
    public void Dynamic_trigger_is_a_warning_not_a_reject()
    {
        var r = Analyze("UPDATE s.parent SET name = 'x'", Ctx([Trig("s.parent", "t_dyn", [ChangeOperation.Update], unknown: true)]));
        Assert.True(r.IsAccepted);
        Assert.Contains(r.Warnings, w => w.RuleId == AnalysisRules.WarnTriggerDynamicSql);
    }

    [Fact]
    public void Analyzer_never_throws()
    {
        var r = new PgBatchAnalyzer().Analyze("DELETE FROM s.note", null!);
        Assert.Equal(AnalysisRules.RejectAnalysisError, Assert.Single(r.Rejects).RuleId);
    }

    // ---------------- 이미지 최종 차이 ----------------

    static AuditHeaderRow H(long seq, int table, ChangeOperation op, int depth = 1, ImageMode mode = ImageMode.Full) => new()
    {
        CaptureSeq = seq, TableObjectId = table, TableName = "[x].[y]", Operation = op, InsertedCount = 0, DeletedCount = 0,
        NestLevel = depth, TranCount = 2, ImageMode = mode,
    };

    static AuditRowImageRow I(long seq, ImageKind kind, string? key, string? row) => new() { CaptureSeq = seq, ImageKind = kind, Ordinal = 1, KeyJson = key, RowJson = row };

    static Dictionary<int, TableInfo> Tables() => Catalog().ToCaptureInput().ToDictionary(t => t.ObjectId);

    [Fact]
    public void Net_diff_is_order_independent_for_nested_rewrites_of_the_same_row()
    {
        // 바깥 UPDATE(a→1)가 트리거로 같은 행을 다시 고침(name→z). 후위 기록이라 안쪽이 먼저 기록된다.
        var headers = new[] { H(1, Parent, ChangeOperation.Update, 2), H(2, Parent, ChangeOperation.Update, 1) };
        var images = new[]
        {
            I(1, ImageKind.Deleted, """{"id":"1"}""", """{"id":"1","name":"b"}"""),
            I(1, ImageKind.Inserted, """{"id":"1"}""", """{"id":"1","name":"z"}"""),
            I(2, ImageKind.Deleted, """{"id":"1"}""", """{"id":"1","name":"a"}"""),
            I(2, ImageKind.Inserted, """{"id":"1"}""", """{"id":"1","name":"b"}"""),
        };
        var r = ImageNetDiff.Compute(headers, images, Tables());
        var c = Assert.Single(r.Changes);
        Assert.Equal(NetChangeKind.Updated, c.Change);
        Assert.Equal("a", c.Before!["name"]);
        Assert.Equal("z", c.After!["name"]);
        Assert.Equal(new[] { "name" }, c.ChangedColumns);
        Assert.Equal(new string?[] { "1" }, c.Key.Values);
    }

    [Fact]
    public void Insert_then_delete_of_the_same_key_cancels_out()
    {
        var headers = new[] { H(1, Note, ChangeOperation.Insert), H(2, Note, ChangeOperation.Delete) };
        var images = new[]
        {
            I(1, ImageKind.Inserted, """{"id":"9"}""", """{"id":"9","parent_id":null}"""),
            I(2, ImageKind.Deleted, """{"id":"9"}""", """{"id":"9","parent_id":null}"""),
        };
        Assert.Empty(ImageNetDiff.Compute(headers, images, Tables()).Changes);
    }

    [Fact]
    public void Keyless_table_is_a_multiset()
    {
        var headers = new[] { H(1, Loose, ChangeOperation.Delete) };
        var images = new[]
        {
            I(1, ImageKind.Deleted, null, """{"a":"1","b":"x"}"""),
            I(1, ImageKind.Deleted, null, """{"a":"1","b":"x"}"""),
        };
        var c = Assert.Single(ImageNetDiff.Compute(headers, images, Tables()).Changes);
        Assert.Equal(NetChangeKind.MultisetRemoved, c.Change);
        Assert.Equal(2, c.Multiplicity);
        Assert.Equal(KeyKind.ROW, c.Key.Kind);
    }

    [Fact]
    public void Keys_mode_table_is_left_for_reread()
    {
        var headers = new[] { H(1, Note, ChangeOperation.Delete, mode: ImageMode.Keys) };
        var r = ImageNetDiff.Compute(headers, [I(1, ImageKind.Deleted, """{"id":"1"}""", null)], Tables());
        Assert.Empty(r.Changes);
        Assert.Equal(new[] { Note }, r.Unresolved);
    }

    // ---------------- 감사 묶음 귀속 ----------------

    [Fact]
    public void Cascade_inside_a_user_trigger_keeps_its_depth_and_parent()
    {
        // SP22 추가 실측: parent(3)의 AFTER DELETE 트리거가 parent(2)를 지운다.
        // 안쪽 FK 연쇄는 안쪽 parent 뒤 같은 깊이로 기록되고 바깥 parent가 그 다음에 기록된다.
        var ctx = Ctx([Trig("s.parent", "t_nested", [ChangeOperation.Delete], writes: ["s.parent"])]);
        var analysis = Analyze("DELETE FROM s.parent WHERE id = 3", ctx);
        var headers = new[]
        {
            H(1, Parent, ChangeOperation.Delete, 2) with { DeletedCount = 1 },
            H(2, Child, ChangeOperation.Delete, 2) with { DeletedCount = 1 },
            H(3, Note, ChangeOperation.Update, 2) with { DeletedCount = 1, InsertedCount = 1 },
            H(4, Grand, ChangeOperation.Delete, 2),
            H(5, Parent, ChangeOperation.Delete) with { DeletedCount = 1 },
            H(6, Child, ChangeOperation.Delete),
            H(7, Note, ChangeOperation.Update),
        };
        var images = new[]
        {
            I(1, ImageKind.Deleted, """{"id":"2"}""", """{"id":"2","name":"p2"}"""),
            I(2, ImageKind.Deleted, """{"id":"20"}""", """{"id":"20","parent_id":"2"}"""),
            I(3, ImageKind.Deleted, """{"id":"1002"}""", """{"id":"1002","parent_id":"2"}"""),
            I(3, ImageKind.Inserted, """{"id":"1002"}""", """{"id":"1002","parent_id":null}"""),
            I(5, ImageKind.Deleted, """{"id":"3"}""", """{"id":"3","name":"p3-no-children"}"""),
        };
        var statement = new StatementResult
        {
            Index = 1, Batch = 1, Position = 1, Target = "s.parent", Kind = ChangeOperation.Delete,
            Span = Assert.Single(analysis.Statements).Span, Status = StatementStatus.Ok, RowsAffected = 1,
        };
        var net = ImageNetDiff.Compute(headers, images, Tables()).Changes;
        var raw = new RawCapture
        {
            Status = PreviewStatus.Ok,
            Executions = [new StatementExecution { Result = statement, Boundary = 7, ModeAfter = ImageMode.Full }],
            Boundary0 = 0, Headers = headers, Images = images,
            NetChanges = net, SandboxDirty = false,
            Capture = new CaptureInfo { ImageCount = images.Length, Cap = 100, Truncated = false, ManifestVersion = ctx.Manifest.Version },
            Timings = new ExecutionTimings(),
        };
        var result = new CausalResolver().Resolve(new ResolveInput
        {
            Statements = [statement], Collected = new ChangeCollector().Collect(raw, analysis, ctx), Net = net, Baseline = ctx,
        }).Model;

        var direct = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.Direct);
        var nested = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.Trigger);
        Assert.Equal(direct.Id, nested.Cause?.ParentFiringId);
        var cascade = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.FkCascade);
        var setNull = Assert.Single(result.Firings, f => f.Cause?.Kind == CauseKind.FkSetNull);
        Assert.All(new[] { cascade, setNull }, f =>
        {
            Assert.Equal(2, f.Depth);
            Assert.Equal(nested.Id, f.Cause?.ParentFiringId);
        });
        var emptyCascade = Assert.Single(result.Firings, f => f.Table == "s.grandchild");
        Assert.Equal(2, emptyCascade.Depth);
        Assert.Equal(0, emptyCascade.RowCount);
        Assert.DoesNotContain(result.NetChanges, n => n.Table == "s.grandchild");
    }

    // ---------------- 오류 분류·트리거 분석 ----------------

    [Theory]
    [InlineData("PL/pgSQL function s.f_block() line 3 at RAISE", "s.f_block")]
    [InlineData("SQL statement \"UPDATE x\"\nPL/pgSQL function dbr_audit.capture_upd() line 7 at SQL statement", "dbr_audit.capture_upd")]
    [InlineData("SQL function \"f\" statement 1", null)]
    [InlineData(null, null)]
    public void Trigger_function_is_read_from_the_error_context(string? where, string? expected)
    {
        Assert.Equal(expected, PgPreviewExecutor.TriggerFunction(where));
    }

    [Fact]
    public void Trigger_body_cleaning_removes_comments_and_string_contents()
    {
        var cleaned = PgTriggerAnalyzer.Clean("-- DELETE FROM a\nINSERT INTO s.log VALUES ('DELETE FROM b'); /* UPDATE c SET x = 1 */");
        Assert.DoesNotContain("DELETE FROM a", cleaned);
        Assert.DoesNotContain("DELETE FROM b", cleaned);
        Assert.DoesNotContain("UPDATE c", cleaned);
        Assert.Contains("INSERT INTO s.log", cleaned);
    }

    [Fact]
    public void Oid_beyond_int_range_fails_the_baseline()
    {
        Assert.Equal(16384, PgNames.ObjectId(16384u, "t"));
        Assert.Throws<InvalidOperationException>(() => PgNames.ObjectId(3_000_000_000u, "t"));
    }
}
