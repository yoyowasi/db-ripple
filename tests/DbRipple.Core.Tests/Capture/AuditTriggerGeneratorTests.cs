using DbRipple.Core.Capture;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Core.Tests.Capture;

public class AuditTriggerGeneratorTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 58, 0, TimeSpan.Zero);

    static TableInfo Orders(params DmlOperation[] userLast) => new(
        "Sales", "SalesOrderHeader", 1234,
        [
            new("SalesOrderID", "int", IsIdentity: true),
            new("RevisionNumber", "tinyint"),
            new("rowguid", "uniqueidentifier", HasNondeterministicDefault: true),
            new("TotalDue", "money", IsComputed: true),
            new("Notes", "ntext"),
            new("Photo", "image"),
            new("Legacy", "text"),
            new("Org", "hierarchyid"),
            new("Spot", "geography"),
            new("Shape", "geometry"),
            new("Doc", "xml"),
            new("Ver", "timestamp", IsRowVersion: true),
        ],
        PrimaryKey: ["SalesOrderID"],
        UserLast: userLast);

    static TableInfo Heap() => new("dbo", "Heap", 99, [new("a", "int"), new("b", "nvarchar")]);

    static TSqlScript Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Line}:{e.Column} {e.Message}")) + "\n" + sql);
        return (TSqlScript)fragment;
    }

    static string Trigger(InstallPlan plan, string table, string op) =>
        plan.Batches.Single(b => b.Label == $"trigger {table} {op}").Sql;

    static IList<TSqlStatement> TriggerBody(string sql)
    {
        var script = Parse(sql);
        var create = Assert.IsType<CreateTriggerStatement>(script.Batches.Single().Statements[0]);
        var block = Assert.IsType<BeginEndBlockStatement>(create.StatementList.Statements.Single());
        return block.StatementList.Statements;
    }

    static string Text(string sql, TSqlFragment f) => sql.Substring(f.StartOffset, f.FragmentLength);

    [Fact]
    public void All_batches_parse_and_have_no_identity_token()
    {
        var plan = AuditTriggerGenerator.Generate([Orders(), Heap()], Now);
        foreach (var b in plan.Batches)
        {
            var script = Parse(b.Sql);
            Assert.Single(script.Batches);
            Assert.DoesNotContain(script.ScriptTokenStream, t => t.TokenType == TSqlTokenType.Identity);
        }
    }

    [Fact]
    public void Create_trigger_is_first_statement_of_its_batch()
    {
        var plan = AuditTriggerGenerator.Generate([Orders()], Now);
        var triggers = plan.Batches.Where(b => b.Label.StartsWith("trigger ")).ToList();
        Assert.Equal(3, triggers.Count);
        foreach (var b in triggers)
            Assert.IsType<CreateTriggerStatement>(Parse(b.Sql).Batches.Single().Statements[0]);
    }

    [Fact]
    public void Update_trigger_writes_deleted_then_inserted_last()
    {
        var plan = AuditTriggerGenerator.Generate([Orders()], Now);
        var sql = Trigger(plan, "Sales.SalesOrderHeader", "U");
        var body = TriggerBody(sql);
        var images = body.OfType<InsertStatement>().Select(s => Text(sql, s)).Where(s => s.Contains("audit.AuditRowImage")).ToList();
        Assert.Equal(2, images.Count);
        Assert.Contains("'DELETED'", images[0]);
        Assert.Contains("FROM deleted AS d", images[0]);
        var last = Text(sql, body[^1]);
        Assert.IsType<InsertStatement>(body[^1]);
        Assert.Contains("'INSERTED'", last);
        Assert.Contains("FROM inserted AS i", last);
        Assert.Contains("AFTER UPDATE", sql);
    }

    [Fact]
    public void Delete_trigger_writes_only_deleted_and_insert_only_inserted()
    {
        var plan = AuditTriggerGenerator.Generate([Orders()], Now);
        var d = Trigger(plan, "Sales.SalesOrderHeader", "D");
        var dLast = Text(d, TriggerBody(d)[^1]);
        Assert.Contains("'DELETED'", dLast);
        Assert.DoesNotContain("'INSERTED'", d);

        var i = Trigger(plan, "Sales.SalesOrderHeader", "I");
        var iLast = Text(i, TriggerBody(i)[^1]);
        Assert.Contains("'INSERTED'", iLast);
        Assert.DoesNotContain("'DELETED'", i);
    }

    [Fact]
    public void Header_and_sequence_come_first()
    {
        var plan = AuditTriggerGenerator.Generate([Orders()], Now);
        var sql = Trigger(plan, "Sales.SalesOrderHeader", "U");
        var body = TriggerBody(sql);
        Assert.IsType<PredicateSetStatement>(body[0]); // SET NOCOUNT ON
        Assert.Contains("NEXT VALUE FOR audit.CaptureSeq", sql);
        Assert.Contains("audit.AuditHeader", Text(sql, body.OfType<InsertStatement>().First()));
        Assert.Contains("TRIGGER_NESTLEVEL()", sql);
        Assert.Contains("@@TRANCOUNT", sql);
        Assert.Contains("N'[Sales].[SalesOrderHeader]'", sql);
        Assert.Contains("1234", sql);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_last_occupied_gives_none_and_no_settriggerorder()
    {
        var plan = AuditTriggerGenerator.Generate([Orders(DmlOperation.Update)], Now);
        var t = plan.Manifest.Tables.Single();
        Assert.Equal(new AuditOrderInfo("LAST", "NONE", "LAST"), t.AuditOrder);
        var order = plan.Batches.Single(b => b.Label == "order Sales.SalesOrderHeader").Sql;
        Assert.Contains("@stmttype = N'INSERT'", order);
        Assert.Contains("@stmttype = N'DELETE'", order);
        Assert.DoesNotContain("N'UPDATE'", order);
        Assert.DoesNotContain("First", string.Concat(plan.Batches.Select(b => b.Sql)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void All_last_occupied_emits_no_order_batch()
    {
        var plan = AuditTriggerGenerator.Generate([Orders(DmlOperation.Insert, DmlOperation.Update, DmlOperation.Delete)], Now);
        Assert.DoesNotContain(plan.Batches, b => b.Sql.Contains("sp_settriggerorder"));
        Assert.Equal(new AuditOrderInfo("NONE", "NONE", "NONE"), plan.Manifest.Tables.Single().AuditOrder);
    }

    [Fact]
    public void Text_ntext_image_are_excluded_and_reported()
    {
        var plan = AuditTriggerGenerator.Generate([Orders()], Now);
        var sql = Trigger(plan, "Sales.SalesOrderHeader", "U");
        Assert.DoesNotContain("[Notes]", sql);
        Assert.DoesNotContain("[Photo]", sql);
        Assert.DoesNotContain("[Legacy]", sql);
        Assert.Equal(["Notes", "Photo", "Legacy"], plan.Manifest.Tables.Single().UnobservableColumns);
    }

    [Fact]
    public void Special_types_are_converted()
    {
        var plan = AuditTriggerGenerator.Generate([Orders()], Now);
        var sql = Trigger(plan, "Sales.SalesOrderHeader", "I");
        Assert.Contains("i.[Org].ToString() AS [Org]", sql);
        // SP6: STAsText()는 Z/M을 버리므로 ToString()
        Assert.Contains("i.[Spot].ToString() AS [Spot]", sql);
        Assert.Contains("i.[Spot].STSrid AS [Spot@srid]", sql);
        Assert.Contains("i.[Shape].ToString() AS [Shape]", sql);
        Assert.DoesNotContain("STAsText", sql);
        Assert.Contains("CONVERT(nvarchar(max), i.[Doc]) AS [Doc]", sql);
        Assert.Contains("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES", sql);
        Assert.Contains("CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE", sql);
    }

    // SP6: FOR JSON 기본 출력은 float 16자리·real 8자리라 값이 바뀜 → 스타일 3 문자열
    [Fact]
    public void Float_real_and_sql_variant_are_lossless()
    {
        var t = Heap() with { Columns = [new("a", "int"), new("f", "float"), new("r", "real"), new("v", "sql_variant")] };
        var sql = Trigger(AuditTriggerGenerator.Generate([t], Now), "dbo.Heap", "I");
        Assert.Contains("CONVERT(varchar(30), i.[f], 3) AS [f]", sql);
        Assert.Contains("CONVERT(varchar(30), CONVERT(float, i.[r]), 3) AS [r]", sql);
        Assert.Contains("CASE WHEN SQL_VARIANT_PROPERTY(i.[v], 'BaseType') IN ('float', 'real') THEN CONVERT(sql_variant, CONVERT(varchar(30), CONVERT(float, i.[v]), 3)) ELSE i.[v] END AS [v]", sql);
        Assert.Contains("CONVERT(nvarchar(128), SQL_VARIANT_PROPERTY(i.[v], 'BaseType')) AS [v@type]", sql);
        Assert.Contains("i.[a] AS [a]", sql);
    }

    // SP6: '.'이 든 열은 중첩 객체가 되고, 생성 속성(@srid/@type)과 같은 이름의 열은 CREATE TRIGGER 13601
    [Fact]
    public void Json_key_conflicts_are_dropped_from_row_json_and_reported()
    {
        var t = new TableInfo("dbo", "Odd", 5,
            [new("id", "int"), new("b.c", "int"), new("g", "geography"), new("g@srid", "int"), new("v", "sql_variant"), new("V@TYPE", "int")],
            PrimaryKey: ["id"]);
        var plan = AuditTriggerGenerator.Generate([t], Now);
        var sql = Trigger(plan, "dbo.Odd", "U");
        Assert.DoesNotContain("[b.c]", sql);
        Assert.DoesNotContain("i.[g@srid]", sql);
        Assert.DoesNotContain("i.[V@TYPE]", sql);
        Assert.Contains("i.[g].STSrid AS [g@srid]", sql);
        Assert.Contains("AS [v@type]", sql);
        var m = plan.Manifest.Tables.Single();
        Assert.Equal(["b.c", "g@srid", "V@TYPE"], m.UnobservableColumns);
        Assert.Contains(plan.Manifest.Notes, n => n.Contains("dbo.Odd.b.c") && n.Contains("JSON_KEY_DOT"));
        Assert.Contains(plan.Manifest.Notes, n => n.Contains("dbo.Odd.g@srid") && n.Contains("JSON_KEY_CONFLICT"));
        Assert.Empty(plan.Manifest.ExcludedTables);
    }

    [Fact]
    public void Json_key_conflict_in_key_excludes_table()
    {
        var t = new TableInfo("dbo", "DotKey", 6, [new("k.x", "int"), new("v", "int")], PrimaryKey: ["k.x"]);
        var plan = AuditTriggerGenerator.Generate([t, Heap()], Now);
        Assert.Equal(new ExcludedTable("dbo.DotKey", "JSON_KEY_CONFLICT"), Assert.Single(plan.Manifest.ExcludedTables));
        Assert.DoesNotContain(plan.Batches, b => b.Sql.Contains("[DotKey]"));
    }

    // 이미 설치된 DB에서 계획을 만들면 audit 테이블에 감사 트리거가 붙어 감사 INSERT가 재귀한다 → 저장소(audit + 알려진 3개 이름)는 조용히 뺀다.
    // 그 밖의 audit/Audit 스키마 테이블은 저장소가 아니므로 사유와 함께 제외 목록에 남긴다(대소문자 무시 DB에서 CREATE SCHEMA audit와 겹침)
    [Fact]
    public void Capture_store_tables_are_never_targets_and_other_audit_schema_tables_are_excluded_with_reason()
    {
        var store = new TableInfo("audit", "AuditHeader", 8, [new("captureSeq", "bigint")], PrimaryKey: ["captureSeq"]);
        var control = store with { Name = "AuditControl", ObjectId = 10 };
        var mixedCase = store with { Schema = "Audit", Name = "AuditRowImage", ObjectId = 9 };
        var userAudit = store with { Name = "Orders", ObjectId = 11 };
        var plan = AuditTriggerGenerator.Generate([store, control, mixedCase, userAudit, Heap()], Now);
        Assert.Equal("dbo.Heap", Assert.Single(plan.Manifest.Tables).Table);
        Assert.Equal([new ExcludedTable("Audit.AuditRowImage", "AUDIT_SCHEMA_CONFLICT"), new ExcludedTable("audit.Orders", "AUDIT_SCHEMA_CONFLICT")], plan.Manifest.ExcludedTables);
        Assert.DoesNotContain(plan.Batches, b => b.Label.StartsWith("trigger audit.", StringComparison.OrdinalIgnoreCase) || b.Sql.Contains(" ON [audit].", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Manifest.Notes, n => n.Contains("audit 스키마") && n.Contains("2개"));
        Assert.True(AuditTriggerGenerator.IsCaptureStore(store));
        Assert.False(AuditTriggerGenerator.IsCaptureStore(mixedCase));
        Assert.False(AuditTriggerGenerator.IsCaptureStore(userAudit));
    }

    // SP10: 트리거 소유자 = 테이블 소유자 ≠ dbo면 dbo 소유 audit 쓰기에서 소유권 체인이 끊겨 사용자 문장이 229로 실패
    [Fact]
    public void Tables_not_owned_by_dbo_are_excluded()
    {
        var owned = Heap() with { Schema = "o", Name = "Owned", Owner = "app_owner" };
        var dboUpper = Heap() with { Name = "Upper", Owner = "DBO" };
        var plan = AuditTriggerGenerator.Generate([owned, dboUpper], Now);
        Assert.Equal(new ExcludedTable("o.Owned", "OWNER_NOT_DBO"), Assert.Single(plan.Manifest.ExcludedTables));
        Assert.Equal("dbo.Upper", Assert.Single(plan.Manifest.Tables).Table);
        Assert.DoesNotContain(plan.Batches, b => b.Sql.Contains("[o].[Owned]"));
    }

    [Fact]
    public void Plan_fixes_session_options_first_and_empties_store_last()
    {
        var plan = AuditTriggerGenerator.Generate([Orders(), Heap()], Now);
        Assert.Equal("session", plan.Batches[0].Label);
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", plan.Batches[0].Sql);
        Assert.Contains("SET ANSI_NULLS ON;", plan.Batches[0].Sql);
        Assert.Equal("schema", plan.Batches[1].Label);
        var reset = plan.Batches[^1];
        Assert.Equal("reset", reset.Label);
        Assert.Equal("DELETE FROM audit.AuditRowImage;\nDELETE FROM audit.AuditHeader;", reset.Sql);
        Assert.DoesNotContain("SEQUENCE", reset.Sql, StringComparison.OrdinalIgnoreCase); // 순번은 재설정하지 않는다
        Assert.True(plan.Batches.ToList().FindLastIndex(b => b.Label.StartsWith("order ") || b.Label.StartsWith("trigger ")) < plan.Batches.Count - 1);
    }

    [Fact]
    public void Keyless_table_uses_null_key_but_same_row_count()
    {
        var plan = AuditTriggerGenerator.Generate([Heap()], Now);
        var sql = Trigger(plan, "dbo.Heap", "U");
        var last = Text(sql, TriggerBody(sql)[^1]);
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY (SELECT NULL))", last);
        Assert.Contains("NULL,\n", last); // keyJson NULL
        Assert.Contains("FROM inserted AS i", last);
    }

    [Fact]
    public void Unique_not_null_index_is_key_when_no_pk()
    {
        var t = Heap() with { UniqueNotNullKeys = [["b"]] };
        Assert.Equal(new[] { "b" }, TableKey.Choose(t).Columns);
        Assert.Equal(KeyKind.UNIQUE, TableKey.Choose(t).Kind);
        Assert.Equal(KeyKind.ROW, TableKey.Choose(Heap()).Kind);
    }

    [Fact]
    public void Long_names_are_truncated_with_hash()
    {
        var longName = new string('x', 128); // 테이블 이름 최대 길이
        var t = new TableInfo("dbo", longName, 1, [new("a", "int")], PrimaryKey: ["a"]);
        var plan = AuditTriggerGenerator.Generate([t], Now);
        foreach (var op in new[] { DmlOperation.Insert, DmlOperation.Update, DmlOperation.Delete })
        {
            var name = AuditTriggerGenerator.TriggerName(longName, op);
            Assert.Equal(128, name.Length);
            Assert.StartsWith("dbr_audit_", name);
            Assert.EndsWith("_" + AuditTriggerGenerator.Code(op), name);
        }
        Assert.NotEqual(
            AuditTriggerGenerator.TriggerName(longName, DmlOperation.Update),
            AuditTriggerGenerator.TriggerName(longName[..127] + "y", DmlOperation.Update));
        foreach (var b in plan.Batches) Parse(b.Sql);
    }

    [Fact]
    public void Bracket_and_quote_in_names_are_escaped()
    {
        var t = new TableInfo("dbo", "we]ird'name", 7, [new("c]ol", "int")], PrimaryKey: ["c]ol"]);
        var plan = AuditTriggerGenerator.Generate([t], Now);
        foreach (var b in plan.Batches) Parse(b.Sql);
        Assert.Contains("[we]]ird'name]", Trigger(plan, "dbo.we]ird'name", "I"));
        Assert.Contains("N'[dbo].[we]]ird''name]'", Trigger(plan, "dbo.we]ird'name", "I"));
    }

    [Fact]
    public void Temporal_history_and_memory_optimized_are_excluded()
    {
        var hist = Heap() with { Name = "HeapHistory", Temporal = TemporalType.History };
        var mem = Heap() with { Name = "Mem", IsMemoryOptimized = true };
        var plan = AuditTriggerGenerator.Generate([hist, mem, Orders()], Now);
        Assert.Equal(2, plan.Manifest.ExcludedTables.Count);
        Assert.Contains(new ExcludedTable("dbo.HeapHistory", "TEMPORAL_HISTORY"), plan.Manifest.ExcludedTables);
        Assert.Contains(new ExcludedTable("dbo.Mem", "MEMORY_OPTIMIZED"), plan.Manifest.ExcludedTables);
        Assert.DoesNotContain(plan.Batches, b => b.Sql.Contains("HeapHistory") || b.Sql.Contains("[Mem]"));
    }

    [Fact]
    public void Manifest_json_has_design_shape()
    {
        var plan = AuditTriggerGenerator.Generate([Orders(DmlOperation.Delete)], Now);
        var json = plan.Manifest.ToJson();
        Assert.Contains("\"version\": \"m-20261106-0958\"", json);
        Assert.Contains("\"auditOrder\": {", json);
        Assert.Contains("\"delete\": \"NONE\"", json);
        Assert.Contains("\"unobservableColumns\": [", json);
        Assert.Contains("\"excludedTables\": []", json);
        var vol = plan.Manifest.Tables.Single().VolatileColumns;
        Assert.Contains(new VolatileColumn("SalesOrderID", "IDENTITY"), vol);
        Assert.Contains(new VolatileColumn("rowguid", "NONDETERMINISTIC_DEFAULT"), vol);
        Assert.Contains(new VolatileColumn("Ver", "ROWVERSION"), vol);
        Assert.Contains(new VolatileColumn("TotalDue", "COMPUTED"), vol);
    }

    [Fact]
    public void Clr_udt_is_converted_and_noted()
    {
        var t = Heap() with { Columns = [new("a", "int"), new("u", "MyPoint", IsUserClrType: true)] };
        var plan = AuditTriggerGenerator.Generate([t], Now);
        Assert.Contains("CONVERT(nvarchar(max), i.[u]) AS [u]", Trigger(plan, "dbo.Heap", "I"));
        Assert.Contains(plan.Manifest.Notes, n => n.StartsWith("TODO(SP6)"));
    }
}
