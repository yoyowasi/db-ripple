using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Analysis.Tests;

/// <summary>배치 분석기 단위 테스트(DB 없이): 손으로 만든 카탈로그·트리거·매니페스트.</summary>
public class BatchAnalyzerTests
{
    /// <summary>dbo.T(id 식별, a, b), dbo.U(x, y), Sales.Orders(id, total), audit.AuditHeader. 트리거는 호출자가 준다.</summary>
    internal static BaselineContext Ctx(IEnumerable<TriggerStaticInfo>? triggers = null, IEnumerable<ManifestTable>? manifest = null, Action<CatalogBuilder>? more = null)
    {
        var b = new CatalogBuilder()
            .Table("dbo", "T", "!id", "a", "b")
            .Table("dbo", "U", "x", "y")
            .Table("Sales", "Orders", "id", "total", "modified")
            .Table("audit", "AuditHeader", "captureSeq");
        more?.Invoke(b);
        return new BaselineContext
        {
            OriginDb = "o", SandboxDb = "s", SnapshotDb = "b",
            BackupFinishedAt = DateTimeOffset.UnixEpoch,
            Fingerprint = new Fingerprint("17", "Dev", 170, false, 1, 0, 0, "x"),
            FingerprintMatch = true,
            Catalog = b.Build(),
            Triggers = triggers?.ToList() ?? [],
            Manifest = new CaptureManifest("m", manifest?.ToList() ?? [], [], []),
        };
    }

    static AnalysisResult Run(string sql, BaselineContext? ctx = null) => new BatchAnalyzer().Analyze(sql, ctx ?? Ctx());

    static void AssertRejectedWith(string sql, string ruleId, BaselineContext? ctx = null)
    {
        var r = Run(sql, ctx);
        Assert.False(r.IsAccepted);
        Assert.Contains(r.Rejects, d => d.RuleId == ruleId);
        Assert.All(r.Rejects, d => Assert.Equal(Severity.Error, d.Severity));
        Assert.All(r.Rejects, d => Assert.False(string.IsNullOrEmpty(d.DocRef)));
    }

    // ---- 수락 ----

    [Fact]
    public void Accepts_single_dml_and_fills_statement_fields()
    {
        var r = Run("DELETE FROM dbo.T WHERE id = 1;");
        Assert.True(r.IsAccepted);
        var s = Assert.Single(r.Statements);
        Assert.Equal((1, 1, 1), (s.Index, s.Batch, s.Position));
        Assert.Equal(ChangeOperation.Delete, s.Kind);
        Assert.Equal("dbo.T", s.Target);
        Assert.Equal("DELETE FROM dbo.T WHERE id = 1;", s.Sql);
        Assert.Equal(new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 31 }, s.Span);
        Assert.Empty(r.Rejects);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.T (a, b) VALUES (1, 2), (3, 4)", ChangeOperation.Insert)]
    [InlineData("INSERT INTO dbo.T (a, b) SELECT x, y FROM dbo.U", ChangeOperation.Insert)]
    [InlineData("INSERT INTO dbo.T DEFAULT VALUES", ChangeOperation.Insert)]
    [InlineData("UPDATE dbo.T SET a = a + 1 WHERE b IN (SELECT x FROM dbo.U)", ChangeOperation.Update)]
    [InlineData("UPDATE t SET t.a = u.y FROM dbo.T AS t JOIN dbo.U AS u ON t.b = u.x", ChangeOperation.Update)]
    [InlineData("DELETE t FROM dbo.T AS t JOIN dbo.U AS u ON t.b = u.x", ChangeOperation.Delete)]
    [InlineData("DELETE dbo.T WHERE a = 1", ChangeOperation.Delete)]
    [InlineData("WITH c AS (SELECT x FROM dbo.U) DELETE FROM dbo.T WHERE a IN (SELECT x FROM c)", ChangeOperation.Delete)]
    [InlineData("UPDATE [dbo].[T] SET [a] = 1", ChangeOperation.Update)]
    public void Accepts_allowed_forms(string sql, ChangeOperation kind)
    {
        var r = Run(sql);
        Assert.True(r.IsAccepted, string.Join("; ", r.Rejects.Select(d => d.RuleId + ":" + d.Message)));
        Assert.Equal(kind, Assert.Single(r.Statements).Kind);
        Assert.Equal("dbo.T", r.Statements[0].Target);
        Assert.Equal(sql, r.Statements[0].Sql); // 문장 원문(WITH 포함)을 정확히 자른다
    }

    [Fact]
    public void Unqualified_and_case_different_names_resolve_to_default_schema_and_catalog_spelling()
    {
        Assert.Equal("dbo.T", Run("DELETE FROM T").Statements[0].Target);          // Q4: 기본 스키마 dbo
        Assert.Equal("dbo.T", Run("DELETE FROM DBO.t").Statements[0].Target);      // 정규 철자
        Assert.Equal("Sales.Orders", Run("UPDATE sales.orders SET total = 1").Statements[0].Target);
        AssertRejectedWith("DELETE FROM Orders", AnalysisRules.RejectTargetNotUserTable); // 스키마 생략은 dbo로만 푼다
    }

    // ---- GO, 위치, 원문 ----

    [Fact]
    public void Go_splits_batches_and_numbers_statements_globally()
    {
        var r = Run("DELETE FROM dbo.T WHERE id = 1;\nDELETE FROM dbo.T WHERE id = 2;\nGO\nUPDATE dbo.U SET y = 1\ngo\nGO\nDELETE FROM dbo.U");
        Assert.True(r.IsAccepted);
        Assert.Equal([(1, 1, 1), (2, 1, 2), (3, 2, 1), (4, 3, 1)], r.Statements.Select(s => (s.Index, s.Batch, s.Position)));
        Assert.Equal([1, 2, 4, 7], r.Statements.Select(s => s.Span.StartLine));
    }

    [Theory]
    [InlineData("DELETE FROM dbo.T\n/* \nGO\n*/\nGO\nDELETE FROM dbo.U", 2)]       // 블록 주석 안 GO 무시
    [InlineData("DELETE FROM dbo.T -- GO\nGO\nDELETE FROM dbo.U", 2)]              // 줄 주석 안 GO 무시
    [InlineData("DELETE FROM dbo.T WHERE a = 'x\nGO\ny'\nGO\nDELETE FROM dbo.U", 2)] // 문자열 안 GO 무시
    [InlineData("DELETE FROM dbo.T\ngo\nDELETE FROM dbo.U", 2)]                    // 소문자
    [InlineData("DELETE FROM dbo.T\nGO -- c\nDELETE FROM dbo.U", 2)]               // 뒤 줄 주석
    [InlineData("DELETE FROM dbo.T\nGO /* x */\nDELETE FROM dbo.U", 2)]            // 뒤 블록 주석
    [InlineData("DELETE FROM dbo.T\n   GO  \nDELETE FROM dbo.U", 2)]               // 들여쓰기·공백
    [InlineData("DELETE FROM dbo.T\r\nGO\r\nDELETE FROM dbo.U\r\nGO", 2)]          // CRLF, 끝 GO
    public void Go_cases_from_sp4(string sql, int batches)
    {
        var r = Run(sql);
        Assert.True(r.IsAccepted, string.Join("; ", r.Rejects.Select(d => d.RuleId + ":" + d.Message)));
        Assert.Equal(batches, r.Statements.Max(s => s.Batch));
    }

    [Fact]
    public void Go_with_count_is_rejected_with_its_own_rule_and_position()
    {
        var r = Run("DELETE FROM dbo.T\nGO 2\nDELETE FROM dbo.U");
        Assert.False(r.IsAccepted);
        var d = Assert.Single(r.Rejects);
        Assert.Equal(AnalysisRules.RejectGoCount, d.RuleId);
        Assert.Equal(2, d.Span.StartLine);
        Assert.Null(d.StatementIndex);
    }

    [Fact]
    public void Go_in_middle_of_line_is_an_alias_not_a_separator()
    {
        // SP4: SELECT 1 GO 는 열 별칭. 허용 목록에서는 SELECT라 거부되지만 GO로 나뉘지는 않는다.
        var r = Run("SELECT 1 GO\nDELETE FROM dbo.T");
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectNotAllowed);
        Assert.Equal(1, r.Statements.Single().Batch);
    }

    [Fact]
    public void Statement_text_and_span_are_exact_with_crlf_tabs_and_leading_comments()
    {
        var sql = "-- 머리말\r\n/* 블록 */\r\n\tINSERT INTO dbo.T (a)\r\n\t  VALUES (1);\r\nGO\r\nUPDATE dbo.T SET a=2";
        var r = Run(sql);
        Assert.True(r.IsAccepted);
        Assert.Equal("INSERT INTO dbo.T (a)\r\n\t  VALUES (1);", r.Statements[0].Sql);
        Assert.Equal(new SourceSpan { StartLine = 3, StartColumn = 2, EndLine = 4, EndColumn = 14 }, r.Statements[0].Span);
        Assert.Equal("UPDATE dbo.T SET a=2", r.Statements[1].Sql);
        Assert.Equal(new SourceSpan { StartLine = 6, StartColumn = 1, EndLine = 6, EndColumn = 20 }, r.Statements[1].Span);
    }

    [Fact]
    public void Statement_text_without_semicolon_has_none_added()
    {
        Assert.Equal("DELETE FROM dbo.T", Run("DELETE FROM dbo.T   \n").Statements[0].Sql);
    }

    // ---- 빈 스크립트(Q5), 파싱 오류 ----

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    [InlineData("-- 주석뿐")]
    [InlineData("/* a */\n-- b\n")]
    [InlineData("GO\nGO")]
    public void Empty_or_comment_only_script_is_rejected_with_one_diagnostic(string sql)
    {
        var r = Run(sql);
        Assert.False(r.IsAccepted);
        Assert.Empty(r.Statements);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(AnalysisRules.RejectEmptyScript, d.RuleId);
    }

    [Fact]
    public void Null_sql_is_treated_as_empty()
    {
        Assert.Equal(AnalysisRules.RejectEmptyScript, Assert.Single(new BatchAnalyzer().Analyze(null!, Ctx()).Rejects).RuleId);
    }

    [Fact]
    public void Parse_error_position_is_script_wide_and_other_statements_still_analyzed()
    {
        var r = Run("DELETE FROM dbo.T\nGO\nDELETE FROM dbo.U\nSELEC 3\nFROM x\nGO\nTRUNCATE TABLE dbo.T");
        var err = Assert.Single(r.Rejects, d => d.RuleId == AnalysisRules.RejectParseError);
        Assert.Equal(4, err.Span.StartLine);
        Assert.Equal(1, err.Span.StartColumn);
        Assert.Contains(r.Rejects, d => d.RuleId == AnalysisRules.RejectTruncate && d.Span.StartLine == 7);
        Assert.False(r.IsAccepted);
    }

    // ---- D5 거부 범주 ----

    public static TheoryData<string, string> RejectCases => new()
    {
        { "CREATE TABLE dbo.X (a int)", AnalysisRules.RejectDdl },
        { "ALTER TABLE dbo.T ADD c int", AnalysisRules.RejectDdl },
        { "DROP TABLE dbo.T", AnalysisRules.RejectDdl },
        { "CREATE TRIGGER dbo.tr ON dbo.T AFTER INSERT AS SELECT 1", AnalysisRules.RejectDdl },
        { "SELECT a INTO dbo.Copy FROM dbo.T", AnalysisRules.RejectSelectInto },
        { "SELECT * FROM dbo.T", AnalysisRules.RejectNotAllowed },
        { "TRUNCATE TABLE dbo.T", AnalysisRules.RejectTruncate },
        { "BULK INSERT dbo.T FROM 'c:\\x.csv'", AnalysisRules.RejectBulkInsert },
        { "INSERT INTO dbo.T (a) SELECT a FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1 a')", AnalysisRules.RejectExternalRowset },
        { "INSERT INTO dbo.T (a) SELECT a FROM OPENQUERY(Lnk, 'SELECT 1 a')", AnalysisRules.RejectExternalRowset },
        { "INSERT INTO dbo.T (a) SELECT a FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x;').db.dbo.t", AnalysisRules.RejectExternalRowset },
        { "INSERT INTO dbo.T WITH (IGNORE_TRIGGERS) (a) VALUES (1)", AnalysisRules.RejectIgnoreTriggers },
        { "MERGE dbo.T AS t USING dbo.U AS u ON t.a = u.x WHEN MATCHED THEN DELETE;", AnalysisRules.RejectMerge },
        { "BEGIN TRANSACTION", AnalysisRules.RejectTransactionControl },
        { "COMMIT", AnalysisRules.RejectTransactionControl },
        { "ROLLBACK TRAN", AnalysisRules.RejectTransactionControl },
        { "SAVE TRANSACTION sp1", AnalysisRules.RejectTransactionControl },
        { "SET NOCOUNT ON", AnalysisRules.RejectSetOption },
        { "SET IDENTITY_INSERT dbo.T ON", AnalysisRules.RejectSetOption },
        { "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", AnalysisRules.RejectSetOption },
        { "USE master", AnalysisRules.RejectUse },
        { "EXEC dbo.uspX", AnalysisRules.RejectProcNotFound }, // 2026-11-13 EXEC 미리보기: 카탈로그에 없는 프로시저
        { "EXEC sp_executesql N'DELETE dbo.T'", AnalysisRules.RejectExec },
        { "EXEC('DELETE dbo.T')", AnalysisRules.RejectExec },
        { "INSERT INTO dbo.T (a) EXEC dbo.uspX", AnalysisRules.RejectExec },
        { "DECLARE @x int = 1", AnalysisRules.RejectVariableOrFlow },
        { "DECLARE @x int = 1; DELETE dbo.T WHERE a = @x", AnalysisRules.RejectVariableOrFlow },
        { "DELETE dbo.T WHERE a = @@ROWCOUNT", AnalysisRules.RejectVariableOrFlow },
        { "IF 1 = 1 DELETE dbo.T", AnalysisRules.RejectVariableOrFlow },
        { "WHILE 1 = 0 DELETE dbo.T", AnalysisRules.RejectVariableOrFlow },
        { "BEGIN DELETE dbo.T END", AnalysisRules.RejectVariableOrFlow },
        { "BEGIN TRY DELETE dbo.T END TRY BEGIN CATCH SELECT 1 END CATCH", AnalysisRules.RejectVariableOrFlow },
        { "DELETE #tmp WHERE a = 1", AnalysisRules.RejectTempOrTableVariable },
        { "DELETE dbo.T WHERE a IN (SELECT a FROM #tmp)", AnalysisRules.RejectTempOrTableVariable },
        { "DECLARE @t TABLE (a int)", AnalysisRules.RejectVariableOrFlow },
        { "DELETE FROM dbo.T OUTPUT deleted.a", AnalysisRules.RejectOutput },
        { "UPDATE dbo.T SET a = 1 OUTPUT inserted.a INTO dbo.U (x)", AnalysisRules.RejectOutput },
        { "DELETE FROM OtherDb.dbo.T", AnalysisRules.RejectCrossDatabase },
        { "DELETE FROM Srv.OtherDb.dbo.T", AnalysisRules.RejectCrossDatabase },
        { "DELETE dbo.T WHERE a IN (SELECT x FROM OtherDb.dbo.U)", AnalysisRules.RejectCrossDatabase },
        { "DBCC CHECKDB", AnalysisRules.RejectAdminCommand },
        { "WAITFOR DELAY '00:00:01'", AnalysisRules.RejectAdminCommand },
        { "BACKUP DATABASE x TO DISK = 'c:\\x.bak'", AnalysisRules.RejectAdminCommand },
        { "RESTORE DATABASE x FROM DISK = 'c:\\x.bak'", AnalysisRules.RejectAdminCommand },
        { "PRINT 'x'", AnalysisRules.RejectVariableOrFlow },
        { "GRANT SELECT ON dbo.T TO public", AnalysisRules.RejectNotAllowed },
        { "DELETE FROM dbo.Missing", AnalysisRules.RejectTargetNotUserTable },
        { "DELETE FROM sys.objects", AnalysisRules.RejectTargetNotUserTable },
        { "WITH c AS (SELECT * FROM dbo.T) DELETE FROM c", AnalysisRules.RejectTargetNotUserTable },
        { "DELETE FROM audit.AuditHeader", AnalysisRules.RejectCaptureStore },
        { "INSERT INTO audit.AuditHeader (captureSeq) VALUES (1)", AnalysisRules.RejectCaptureStore },
    };

    [Theory]
    [MemberData(nameof(RejectCases))]
    public void Rejects_each_d5_category_with_rule_position_and_docref(string sql, string rule) => AssertRejectedWith(sql, rule);

    [Fact]
    public void Reject_has_exact_span_statement_index_and_one_diagnostic_per_rule()
    {
        var r = Run("DELETE FROM dbo.T WHERE a = 1;\nTRUNCATE TABLE dbo.T;\nGO\nSET NOCOUNT ON");
        Assert.Equal(2, r.Rejects.Count);
        var trunc = r.Rejects[0];
        Assert.Equal(AnalysisRules.RejectTruncate, trunc.RuleId);
        Assert.Equal(2, trunc.StatementIndex);
        Assert.Equal(new SourceSpan { StartLine = 2, StartColumn = 1, EndLine = 2, EndColumn = 21 }, trunc.Span);
        Assert.Equal("A21", trunc.DocRef);
        Assert.Equal(AnalysisRules.RejectSetOption, r.Rejects[1].RuleId);
        Assert.Equal(3, r.Rejects[1].StatementIndex);
        Assert.Equal(4, r.Rejects[1].Span.StartLine);
        Assert.Single(r.Statements, s => s.Index == 1);
    }

    [Fact]
    public void One_rule_once_per_statement_even_if_it_occurs_twice()
    {
        var r = Run("DELETE dbo.T WHERE a IN (SELECT x FROM #a) AND b IN (SELECT y FROM #b)");
        Assert.Single(r.Rejects, d => d.RuleId == AnalysisRules.RejectTempOrTableVariable);
    }

    [Fact]
    public void Any_reject_makes_whole_script_not_accepted_but_parsed_dml_is_listed()
    {
        var r = Run("DELETE FROM dbo.T WHERE a = 1;\nTRUNCATE TABLE dbo.U");
        Assert.False(r.IsAccepted);
        Assert.Single(r.Statements);
        Assert.Equal(r.Rejects.Concat(r.Warnings).ToList(), r.Diagnostics.ToList());
    }

    // ---- 경고 4종 ----

    [Fact]
    public void Warns_top_without_order_for_update_and_delete()
    {
        var r = Run("DELETE TOP (5) FROM dbo.T;\nUPDATE TOP (1) dbo.T SET a = 1;\nUPDATE dbo.T SET a = 1 WHERE b IN (SELECT TOP 1 x FROM dbo.U);\nUPDATE dbo.T SET a = 1 WHERE b IN (SELECT TOP 1 x FROM dbo.U ORDER BY x)");
        Assert.True(r.IsAccepted);
        Assert.Equal([1, 2, 3], r.Warnings.Where(w => w.RuleId == AnalysisRules.WarnTopWithoutOrder).Select(w => w.StatementIndex!.Value));
        Assert.All(r.Warnings, w => Assert.Equal(Severity.Warning, w.Severity));
        Assert.Equal(r.Warnings.Count, r.Warnings.Count(w => !string.IsNullOrEmpty(w.DocRef)));
    }

    [Fact]
    public void Warns_nondeterministic_function_and_lists_columns()
    {
        var r = Run("UPDATE Sales.Orders SET modified = GETDATE(), total = 1 WHERE id = 1;\nINSERT INTO dbo.T (a, b) VALUES (1, DATEPART(year, SYSDATETIME()));\nINSERT INTO dbo.U SELECT 1, ABS(CHECKSUM(NEWID()));\nDELETE dbo.T WHERE a < DATEDIFF(day, 0, GETUTCDATE());");
        Assert.True(r.IsAccepted);
        Assert.Equal(["modified"], r.Statements[0].NondeterministicColumns);
        Assert.Equal(["b"], r.Statements[1].NondeterministicColumns);
        Assert.Equal(["y"], r.Statements[2].NondeterministicColumns);   // 열 목록 없는 INSERT: 카탈로그 열 순서(x, y)
        Assert.Empty(r.Statements[3].NondeterministicColumns);          // 비결정 함수는 있지만 대입 열은 없음
        Assert.Equal([1, 2, 3, 4], r.Warnings.Where(w => w.RuleId == AnalysisRules.WarnNondeterministicFunction).Select(w => w.StatementIndex!.Value));
    }

    [Fact]
    public void Nondeterministic_insert_skips_identity_column_when_no_column_list()
    {
        var r = Run("INSERT INTO dbo.T SELECT 1, GETDATE()"); // T(id 식별, a, b) → a, b
        Assert.Equal(["b"], r.Statements[0].NondeterministicColumns);
    }

    [Fact]
    public void Warns_when_audit_trigger_is_not_last_for_the_statement_operation()
    {
        var manifest = new[] { new ManifestTable("dbo.T", true, new AuditOrderInfo("LAST", "NONE", "LAST"), [], []) };
        var ctx = Ctx(manifest: manifest);
        Assert.Contains(Run("UPDATE dbo.T SET a = 1", ctx).Warnings, w => w.RuleId == AnalysisRules.WarnAuditNotLast);
        Assert.DoesNotContain(Run("DELETE dbo.T WHERE a = 1", ctx).Warnings, w => w.RuleId == AnalysisRules.WarnAuditNotLast);
        Assert.True(Run("UPDATE dbo.T SET a = 1", ctx).IsAccepted);
    }

    static TriggerStaticInfo Trig(string table, string name, string[]? external = null, bool dynamic = false, bool encrypted = false,
        bool disabled = false, bool audit = false, ChangeOperation[]? events = null, bool transactionControl = false,
        string[]? writes = null, bool rollback = false) => new()
    {
        HasTransactionControl = transactionControl,
        Trigger = name, ObjectId = 1, Table = table, Events = events ?? [ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete],
        IsInsteadOf = false, IsDisabled = disabled, IsEncrypted = encrypted, IsAuditTrigger = audit, HasDynamicSql = dynamic, HasRollback = rollback,
        ExternalSideEffects = external ?? [], WriteTargets = writes ?? [],
    };

    [Fact]
    public void Warns_when_a_trigger_on_the_target_uses_dynamic_sql()
    {
        var ctx = Ctx([Trig("dbo.T", "dbo.trDyn", dynamic: true, events: [ChangeOperation.Update])]);
        Assert.Contains(Run("UPDATE dbo.T SET a = 1", ctx).Warnings, w => w.RuleId == AnalysisRules.WarnTriggerDynamicSql && w.Message.Contains("dbo.trDyn"));
        Assert.DoesNotContain(Run("DELETE dbo.T", ctx).Warnings, w => w.RuleId == AnalysisRules.WarnTriggerDynamicSql); // 그 연산에 발동하지 않음
        Assert.DoesNotContain(Run("UPDATE dbo.U SET y = 1", ctx).Warnings, w => w.RuleId == AnalysisRules.WarnTriggerDynamicSql);
        Assert.True(Run("UPDATE dbo.T SET a = 1", ctx).IsAccepted);
    }

    [Fact]
    public void Rejects_dml_on_table_with_external_side_effect_trigger()
    {
        var ctx = Ctx([Trig("dbo.T", "dbo.trMail", external: ["msdb:msdb.dbo.sp_send_dbmail"])]);
        AssertRejectedWith("INSERT INTO dbo.T (a) VALUES (1)", AnalysisRules.RejectTriggerExternalEffect, ctx);
        Assert.True(Run("DELETE dbo.U", ctx).IsAccepted);
    }

    [Fact]
    public void Trigger_filters_ignore_disabled_audit_and_other_tables()
    {
        var ctx = Ctx([
            Trig("dbo.T", "dbo.trOff", external: ["OPENQUERY"], disabled: true),
            Trig("dbo.T", "dbo.dbr_audit_T_I", external: ["xp_:x"], audit: true),
            Trig("dbo.U", "dbo.trU", external: ["OPENQUERY"]),
        ]);
        Assert.True(Run("DELETE dbo.T", ctx).IsAccepted);
        AssertRejectedWith("DELETE dbo.U", AnalysisRules.RejectTriggerExternalEffect, ctx);
    }

    [Fact]
    public void Rejects_encrypted_trigger_and_unparsed_trigger_closed_side()
    {
        AssertRejectedWith("DELETE dbo.T", AnalysisRules.RejectTriggerOpaque, Ctx([Trig("dbo.T", "dbo.trEnc", encrypted: true, dynamic: true)]));
        var unparsed = Ctx([Trig("dbo.T", "dbo.trBad", external: [TriggerAnalyzer.UnparsedMarker], dynamic: true)]);
        AssertRejectedWith("DELETE dbo.T", AnalysisRules.RejectTriggerExternalEffect, unparsed);
        Assert.DoesNotContain(Run("DELETE dbo.T", unparsed).Warnings, w => w.RuleId == AnalysisRules.WarnTriggerDynamicSql);
    }

    [Fact]
    public void Diagnostics_are_rejects_then_warnings_and_every_diagnostic_has_a_span_inside_the_script()
    {
        var sql = "DELETE TOP (1) dbo.T;\nTRUNCATE TABLE dbo.U;";
        var r = Run(sql);
        Assert.Equal(r.Rejects.Count + r.Warnings.Count, r.Diagnostics.Count);
        Assert.Equal(AnalysisRules.RejectTruncate, r.Diagnostics[0].RuleId);
        Assert.All(r.Diagnostics, d => Assert.True(d.Span.StartLine >= 1 && d.Span.EndLine <= 2 && d.Span.StartColumn >= 1));
    }

    [Fact]
    public void Rule_id_lists_are_unique_and_prefixed()
    {
        Assert.Equal(AnalysisRules.AllRejects.Count, AnalysisRules.AllRejects.Distinct().Count());
        Assert.All(AnalysisRules.AllRejects, r => Assert.StartsWith("REJECT_", r));
        Assert.Equal(AnalysisRules.AllWarnings.Count, AnalysisRules.AllWarnings.Distinct().Count());
        Assert.Equal(13, AnalysisRules.AllWarnings.Count);
        Assert.All(AnalysisRules.AllWarnings, r => Assert.StartsWith("WARN_", r));
    }

    [Fact]
    public void Bad_baseline_context_does_not_throw_but_rejects_closed()
    {
        var r = new BatchAnalyzer().Analyze("DELETE dbo.T", null!);
        Assert.False(r.IsAccepted);
        Assert.Equal(AnalysisRules.RejectAnalysisError, Assert.Single(r.Rejects).RuleId);
    }

    [Fact]
    public void Rejects_dml_on_table_whose_trigger_commits_or_begins_a_transaction()
    {
        var ctx = Ctx([Trig("dbo.T", "dbo.trCommit", transactionControl: true, events: [ChangeOperation.Insert]),
                       Trig("dbo.U", "dbo.trOff", transactionControl: true, disabled: true)]);
        AssertRejectedWith("INSERT INTO dbo.T (a) VALUES (1)", AnalysisRules.RejectTriggerTransactionControl, ctx);
        Assert.True(Run("DELETE dbo.T", ctx).IsAccepted);  // 그 이벤트가 아니면 발동하지 않음
        Assert.True(Run("DELETE dbo.U", ctx).IsAccepted);  // 비활성 트리거
        Assert.Contains(AnalysisRules.RejectTriggerTransactionControl, AnalysisRules.AllRejects);
    }

    // ---- 도달 폐포(2026-11-13): 직접 대상 밖에서 발동하는 트리거 ----

    [Fact]
    public void Rejects_when_a_cascade_child_has_an_external_side_effect_trigger()
    {
        var ctx = Ctx([Trig("dbo.U", "dbo.trMail", external: ["msdb:msdb.dbo.sp_send_dbmail"], events: [ChangeOperation.Delete])],
            more: b => b.ForeignKey("FK_U_T", "dbo.U", "dbo.T", onDelete: "CASCADE"));
        AssertRejectedWith("DELETE dbo.T", AnalysisRules.RejectTriggerExternalEffect, ctx);
        var reject = Assert.Single(Run("DELETE dbo.T", ctx).Rejects);
        Assert.Contains("FK FK_U_T", reject.Message);       // 닿는 경로를 보인다
        Assert.Contains("dbo.trMail", reject.Message);
        Assert.True(Run("UPDATE dbo.T SET a = 1", ctx).IsAccepted); // ON UPDATE NO ACTION: 자식으로 번지지 않음
        Assert.True(Run("INSERT INTO dbo.T (a) VALUES (1)", ctx).IsAccepted);
    }

    [Fact]
    public void Set_null_reaches_the_child_as_an_update()
    {
        var onUpdate = Ctx([Trig("dbo.U", "dbo.trCommit", transactionControl: true, events: [ChangeOperation.Update])],
            more: b => b.ForeignKey("FK_U_T", "dbo.U", "dbo.T", onDelete: "SET_NULL"));
        AssertRejectedWith("DELETE dbo.T", AnalysisRules.RejectTriggerTransactionControl, onUpdate);

        var onDeleteOnly = Ctx([Trig("dbo.U", "dbo.trCommit", transactionControl: true, events: [ChangeOperation.Delete])],
            more: b => b.ForeignKey("FK_U_T", "dbo.U", "dbo.T", onDelete: "SET_NULL"));
        Assert.True(Run("DELETE dbo.T", onDeleteOnly).IsAccepted); // 자식은 UPDATE로만 바뀐다
    }

    [Fact]
    public void Follows_trigger_write_targets_and_then_their_cascades()
    {
        // T의 트리거가 Orders를 쓰고, Orders 삭제가 U로 CASCADE되고, U의 트리거가 외부 부작용을 낸다.
        var ctx = Ctx([
                Trig("dbo.T", "dbo.trT", writes: ["Sales.Orders"], events: [ChangeOperation.Insert]),
                Trig("dbo.U", "dbo.trU", external: ["OPENQUERY"], events: [ChangeOperation.Delete]),
            ],
            more: b => b.ForeignKey("FK_U_Orders", "dbo.U", "Sales.Orders", onDelete: "CASCADE"));
        var r = Run("INSERT INTO dbo.T (a) VALUES (1)", ctx);
        Assert.False(r.IsAccepted);
        var reject = Assert.Single(r.Rejects);
        Assert.Equal(AnalysisRules.RejectTriggerExternalEffect, reject.RuleId);
        Assert.Contains("트리거 dbo.trT", reject.Message);
        Assert.Contains("FK FK_U_Orders", reject.Message);
    }

    [Fact]
    public void Unknown_write_targets_reach_every_table_closed_side()
    {
        var bad = Ctx([
            Trig("dbo.T", "dbo.trDyn", dynamic: true, events: [ChangeOperation.Update]),
            Trig("Sales.Orders", "Sales.trMail", external: ["msdb:msdb.dbo.sp_send_dbmail"]),
        ]);
        AssertRejectedWith("UPDATE dbo.T SET a = 1", AnalysisRules.RejectTriggerExternalEffect, bad);
        Assert.Contains(Run("UPDATE dbo.T SET a = 1", bad).Rejects, d => d.Message.Contains("쓰기 대상 미상"));

        var clean = Ctx([Trig("dbo.T", "dbo.trDyn", dynamic: true, events: [ChangeOperation.Update]), Trig("dbo.U", "dbo.trU")]);
        var ok = Run("UPDATE dbo.T SET a = 1", clean);
        Assert.True(ok.IsAccepted);
        Assert.Contains(ok.Warnings, w => w.RuleId == AnalysisRules.WarnTriggerDynamicSql); // 직접 대상 경고는 그대로
    }

    [Fact]
    public void Reached_rollback_only_disabled_and_audit_triggers_are_not_rejected()
    {
        var ctx = Ctx([
                Trig("dbo.U", "dbo.trRollback", rollback: true, events: [ChangeOperation.Delete]),
                Trig("dbo.U", "dbo.trOff", external: ["OPENQUERY"], disabled: true),
                Trig("dbo.U", "dbo.dbr_audit_U_D", external: ["xp_:x"], audit: true),
            ],
            more: b => b.ForeignKey("FK_U_T", "dbo.U", "dbo.T", onDelete: "CASCADE")
                        .ForeignKey("FK_Orders_T", "Sales.Orders", "dbo.T", onDelete: "CASCADE", disabled: true));
        Assert.True(Run("DELETE dbo.T", ctx).IsAccepted);
    }

    [Fact]
    public void Direct_target_trigger_is_reported_once_and_cycles_terminate()
    {
        // 직접 대상 트리거는 CheckTargetTriggers 한 번만, 서로 쓰는 트리거 순환은 고정점에서 멈춘다.
        var ctx = Ctx([
            Trig("dbo.T", "dbo.trMail", external: ["OPENQUERY"], writes: ["dbo.U"]),
            Trig("dbo.U", "dbo.trU", writes: ["dbo.T"]),
        ]);
        var r = Run("DELETE dbo.T", ctx);
        Assert.Single(r.Rejects, d => d.RuleId == AnalysisRules.RejectTriggerExternalEffect);
    }
}
