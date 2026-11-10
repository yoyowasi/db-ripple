using System.Text.RegularExpressions;
using DbRipple.Contracts;
using DbRipple.Core.Catalog;

namespace DbRipple.Analysis.Tests;

/// <summary>트리거 정적 분석(DB 없이). AW 트리거 10개는 AW_Origin에서 읽기 전용으로 뽑은 정의(Fixtures/aw-triggers.txt)다.</summary>
public class TriggerAnalyzerTests
{
    static readonly string[] UspCalls = ["dbo.uspLogError", "dbo.uspPrintError"];

    static DbCatalog AwCatalog()
    {
        var b = new CatalogBuilder();
        foreach (var (s, t) in new[]
                 {
                     ("HumanResources", "Employee"), ("Person", "Person"), ("Production", "WorkOrder"), ("Production", "TransactionHistory"),
                     ("Purchasing", "Vendor"), ("Purchasing", "PurchaseOrderDetail"), ("Purchasing", "PurchaseOrderHeader"),
                     ("Sales", "SalesOrderDetail"), ("Sales", "SalesOrderHeader"), ("Sales", "SalesPerson"), ("Sales", "SalesTerritory"), ("Sales", "Customer"),
                 })
            b.Table(s, t);
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "aw-triggers.txt"));
        var events = new Dictionary<string, string[]>
        {
            ["HumanResources.dEmployee"] = ["DELETE"],
            ["Person.iuPerson"] = ["INSERT", "UPDATE"],
            ["Production.iWorkOrder"] = ["INSERT"],
            ["Production.uWorkOrder"] = ["UPDATE"],
            ["Purchasing.dVendor"] = ["DELETE"],
            ["Purchasing.iPurchaseOrderDetail"] = ["INSERT"],
            ["Purchasing.uPurchaseOrderDetail"] = ["UPDATE"],
            ["Purchasing.uPurchaseOrderHeader"] = ["UPDATE"],
            ["Sales.iduSalesOrderDetail"] = ["INSERT", "DELETE", "UPDATE"],
            ["Sales.uSalesOrderHeader"] = ["UPDATE"],
        };
        foreach (var part in text.Split("==== ", StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = part.IndexOf('\n');
            var header = part[..nl].TrimEnd('\r');
            var def = part[(nl + 1)..].Trim();
            var m = Regex.Match(header, @"^(\S+) ON (\S+) instead=(True|False) disabled=(True|False)$");
            Assert.True(m.Success, header);
            var parent = m.Groups[2].Value.Split('.');
            b.Trigger(parent[0], parent[1], m.Groups[1].Value.Split('.')[1], events[m.Groups[1].Value], def,
                insteadOf: m.Groups[3].Value == "True", disabled: m.Groups[4].Value == "True");
        }
        return b.Build();
    }

    // 트리거, 쓰기 대상, ROLLBACK, 호출 프로시저, INSTEAD OF (손으로 본문을 읽어 정한 기대값)
    public static TheoryData<string, string[], bool, string[], bool> AwExpectations => new()
    {
        { "HumanResources.dEmployee", [], true, [], true },
        { "Person.iuPerson", ["Person.Person"], false, [], false },
        { "Production.iWorkOrder", ["Production.TransactionHistory"], true, UspCalls, false },
        { "Production.uWorkOrder", ["Production.TransactionHistory"], true, UspCalls, false },
        { "Purchasing.dVendor", [], true, UspCalls, true },
        { "Purchasing.iPurchaseOrderDetail", ["Production.TransactionHistory", "Purchasing.PurchaseOrderHeader"], true, UspCalls, false },
        { "Purchasing.uPurchaseOrderDetail", ["Production.TransactionHistory", "Purchasing.PurchaseOrderDetail", "Purchasing.PurchaseOrderHeader"], true, UspCalls, false },
        { "Purchasing.uPurchaseOrderHeader", ["Purchasing.PurchaseOrderHeader"], true, UspCalls, false },
        { "Sales.iduSalesOrderDetail", ["Person.Person", "Production.TransactionHistory", "Sales.SalesOrderHeader"], true, UspCalls, false },
        { "Sales.uSalesOrderHeader", ["Sales.SalesOrderHeader", "Sales.SalesPerson", "Sales.SalesTerritory"], true, UspCalls, false },
    };

    [Theory]
    [MemberData(nameof(AwExpectations))]
    public void Aw_trigger_write_targets_match_hand_derivation(string trigger, string[] targets, bool rollback, string[] calls, bool insteadOf)
    {
        var infos = new TriggerAnalyzer().Analyze(AwCatalog());
        var info = Assert.Single(infos, i => i.Trigger == trigger);
        Assert.Equal(targets, info.WriteTargets);
        Assert.Equal(rollback, info.HasRollback);
        Assert.Equal(calls, info.ProcedureCalls);
        Assert.Equal(insteadOf, info.IsInsteadOf);
        Assert.False(info.HasDynamicSql);
        Assert.False(info.WriteTargetsUnknown);
        Assert.False(info.IsEncrypted);
        Assert.False(info.IsAuditTrigger);
        Assert.False(info.IsDisabled);
        Assert.Empty(info.ExternalSideEffects);
    }

    [Fact]
    public void Aw_has_ten_triggers_with_events_and_nondeterministic_writes()
    {
        var infos = new TriggerAnalyzer().Analyze(AwCatalog());
        Assert.Equal(10, infos.Count);
        Assert.Equal([ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete],
            infos.Single(i => i.Trigger == "Sales.iduSalesOrderDetail").Events);
        static string[] Nd(TriggerStaticInfo i) => i.NondeterministicWrites.Select(w => $"{w.Table}.{w.Column}").ToArray();
        Assert.Equal(["Production.TransactionHistory.TransactionDate"], Nd(infos.Single(i => i.Trigger == "Production.iWorkOrder")));
        Assert.Equal(["Production.TransactionHistory.TransactionDate"], Nd(infos.Single(i => i.Trigger == "Production.uWorkOrder")));
        Assert.Equal(["Production.TransactionHistory.TransactionDate"], Nd(infos.Single(i => i.Trigger == "Purchasing.iPurchaseOrderDetail")));
        Assert.Equal(["Production.TransactionHistory.TransactionDate", "Purchasing.PurchaseOrderDetail.ModifiedDate"],
            Nd(infos.Single(i => i.Trigger == "Purchasing.uPurchaseOrderDetail")));
        Assert.Equal(["Production.TransactionHistory.TransactionDate"], Nd(infos.Single(i => i.Trigger == "Sales.iduSalesOrderDetail")));
        Assert.Empty(Nd(infos.Single(i => i.Trigger == "Sales.uSalesOrderHeader")));
        Assert.Empty(Nd(infos.Single(i => i.Trigger == "Person.iuPerson")));
    }

    // ---- 손으로 쓴 표본(SP4 TriggerAnalyzerTests와 같은 기대) ----

    const string Idu = @"
CREATE TRIGGER Sales.iduSalesOrderDetail ON Sales.SalesOrderDetail
AFTER INSERT, DELETE, UPDATE AS
BEGIN
    DECLARE @Count int;
    SET @Count = @@ROWCOUNT;
    IF @Count = 0 RETURN;
    SET NOCOUNT ON;
    BEGIN TRY
        IF EXISTS (SELECT * FROM inserted)
        BEGIN
            INSERT INTO Production.TransactionHistory (ProductID, ReferenceOrderID)
            SELECT inserted.ProductID, inserted.SalesOrderID FROM inserted;
            UPDATE Person.Person SET ModifiedDate = GETDATE() FROM inserted
            INNER JOIN Sales.SalesOrderHeader AS soh ON inserted.SalesOrderID = soh.SalesOrderID
            WHERE Person.Person.BusinessEntityID = soh.CustomerID;
        END;
        UPDATE h SET h.SubTotal = h.SubTotal + i.LineTotal
        FROM Sales.SalesOrderHeader h INNER JOIN inserted i ON h.SalesOrderID = i.SalesOrderID;
        DECLARE @tv TABLE (id int);
        INSERT INTO @tv SELECT SalesOrderID FROM inserted;
        UPDATE #tmp SET x = 1;
        WITH c AS (SELECT * FROM deleted) DELETE FROM c;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        EXEC dbo.uspLogError;
    END CATCH;
END;";

    static TriggerStaticInfo AnalyzeOne(string? definition, string schema = "dbo", string table = "T", string name = "trX",
        string[]? events = null, Action<CatalogBuilder>? more = null)
    {
        var b = new CatalogBuilder().Table(schema, table, "a", "b", "!id");
        more?.Invoke(b);
        b.Trigger(schema, table, name, events ?? ["INSERT"], definition);
        return Assert.Single(new TriggerAnalyzer().Analyze(b.Build()), i => i.Trigger == schema + "." + name);
    }

    [Fact]
    public void Excludes_inserted_deleted_cte_tablevariable_temp_and_resolves_alias()
    {
        var a = AnalyzeOne(Idu, schema: "Sales", table: "SalesOrderDetail", name: "iduSalesOrderDetail");
        Assert.Equal(["Person.Person", "Production.TransactionHistory", "Sales.SalesOrderHeader"], a.WriteTargets);
        Assert.True(a.HasRollback);
        Assert.False(a.HasDynamicSql);
        Assert.Equal(["dbo.uspLogError"], a.ProcedureCalls);
        Assert.Contains(a.NondeterministicWrites, w => w is { Table: "Person.Person", Column: "ModifiedDate" });
    }

    [Fact]
    public void Dynamic_sql_and_external_flags()
    {
        var a = AnalyzeOne(@"
CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS
BEGIN
    DECLARE @sql nvarchar(max) = N'UPDATE dbo.Z SET a=1';
    EXEC(@sql);
    EXEC sp_executesql @sql;
    EXEC master.dbo.xp_cmdshell 'dir';
    EXEC msdb.dbo.sp_send_dbmail @recipients='a@b.c';
    INSERT INTO LinkedSrv.RemoteDb.dbo.Audit(a) SELECT 1;
    SELECT * FROM OPENQUERY(LinkedSrv, 'select 1');
    MERGE dbo.Target AS t USING inserted AS i ON t.id = i.id WHEN MATCHED THEN UPDATE SET a = 1;
    DELETE FROM dbo.Old WHERE id IN (SELECT id FROM msdb.dbo.sysjobs);
END;");
        Assert.True(a.HasDynamicSql);
        Assert.Contains("dbo.Target", a.WriteTargets);
        Assert.Contains("dbo.Old", a.WriteTargets);
        Assert.DoesNotContain("dbo.Z", a.WriteTargets); // 동적 SQL 안은 정적으로 못 본다. HasDynamicSql이 대신 알린다.
        Assert.Contains("OPENQUERY", a.ExternalSideEffects);
        Assert.Contains(a.ExternalSideEffects, x => x.StartsWith("xp_:"));
        Assert.Contains(a.ExternalSideEffects, x => x.StartsWith("msdb:"));
        Assert.Contains(a.ExternalSideEffects, x => x.StartsWith("4-part:"));
        Assert.Contains("master.dbo.xp_cmdshell", a.ProcedureCalls);
    }

    [Fact]
    public void Exec_procedure_variable_is_dynamic_and_insert_exec_is_seen()
    {
        var a = AnalyzeOne("CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS BEGIN DECLARE @p sysname = N'x'; EXEC @p; END;");
        Assert.True(a.HasDynamicSql);
        var b = AnalyzeOne("CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS BEGIN INSERT INTO dbo.Log EXEC msdb.dbo.sp_x; END;");
        Assert.Contains("dbo.Log", b.WriteTargets);
        Assert.Contains(b.ExternalSideEffects, x => x.StartsWith("msdb:"));
    }

    [Fact]
    public void Write_through_cte_counts_base_tables_inside_the_cte()
    {
        var a = AnalyzeOne("CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS BEGIN WITH c AS (SELECT * FROM dbo.Real WHERE a = 1) DELETE FROM c; END;");
        Assert.Equal(["dbo.Real"], a.WriteTargets);
    }

    [Fact]
    public void Unqualified_write_targets_resolve_to_trigger_schema_then_dbo()
    {
        var a = AnalyzeOne("CREATE TRIGGER s1.trX ON s1.T AFTER INSERT AS BEGIN UPDATE Other SET a = 1; UPDATE OnlyDbo SET a = 1; UPDATE ghost SET a = 1; END;",
            schema: "s1", table: "T", more: b => b.Table("s1", "Other").Table("dbo", "OnlyDbo"));
        Assert.Equal(["dbo.ghost", "dbo.OnlyDbo", "s1.Other"], a.WriteTargets);
    }

    [Fact]
    public void Nondeterministic_writes_from_update_and_column_less_insert()
    {
        var a = AnalyzeOne(@"CREATE TRIGGER dbo.trX ON dbo.T AFTER UPDATE AS BEGIN
            UPDATE dbo.T SET b = SYSDATETIME() FROM inserted;
            INSERT INTO dbo.L SELECT 1, NEWID();
            INSERT INTO dbo.L (a, b) VALUES (GETDATE(), 5); END;",
            events: ["UPDATE"], more: b => b.Table("dbo", "L", "!id", "x", "y"));
        var cols = a.NondeterministicWrites.Select(w => $"{w.Table}.{w.Column}").ToList();
        Assert.Contains("dbo.T.b", cols);
        Assert.Contains("dbo.L.y", cols);   // 열 목록 없음: IDENTITY(!id)를 건너뛰고 x, y 순 → SELECT 1, NEWID() → y
        Assert.DoesNotContain("dbo.L.x", cols);
        Assert.Contains("dbo.L.a", cols);   // 대상 L에 열 a가 없어도 쓰인 이름 그대로
    }

    [Fact]
    public void Encrypted_trigger_has_unknown_targets()
    {
        var a = AnalyzeOne(null, name: "trEnc");
        Assert.True(a.IsEncrypted);
        Assert.True(a.HasDynamicSql);
        Assert.True(a.WriteTargetsUnknown);
        Assert.Empty(a.WriteTargets);
        Assert.Empty(a.ExternalSideEffects);
    }

    [Fact]
    public void Parse_failure_is_conservative()
    {
        var a = AnalyzeOne("CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS BEGIN UPDATE SET ;; END");
        Assert.False(a.IsEncrypted);
        Assert.True(a.HasDynamicSql);
        Assert.True(a.WriteTargetsUnknown);
        Assert.Empty(a.WriteTargets);
        Assert.Equal([TriggerAnalyzer.UnparsedMarker], a.ExternalSideEffects);
    }

    [Fact]
    public void Audit_triggers_are_marked_and_not_parsed_and_ddl_or_view_triggers_are_skipped()
    {
        var b = new CatalogBuilder().Table("dbo", "T");
        b.Trigger("dbo", "T", "dbr_audit_T_I", ["INSERT"],
            "CREATE TRIGGER [dbo].[dbr_audit_T_I] ON [dbo].[T] AFTER INSERT AS BEGIN INSERT INTO audit.AuditHeader(a) SELECT 1; END", isLast: true);
        var cat = b.Build();
        cat = cat with
        {
            Triggers =
            [
                .. cat.Triggers,
                new CatalogTriggerEvent(9, "ddl", 0, "DATABASE", false, false, "CREATE_TABLE", false, false, "CREATE TRIGGER ddl ON DATABASE FOR CREATE_TABLE AS SELECT 1"),
                new CatalogTriggerEvent(10, "vtr", 424242, "OBJECT_OR_COLUMN", true, false, "INSERT", false, false, "CREATE TRIGGER vtr ON v INSTEAD OF INSERT AS SELECT 1"),
            ],
        };
        var info = Assert.Single(new TriggerAnalyzer().Analyze(cat));
        Assert.True(info.IsAuditTrigger);
        Assert.False(info.IsEncrypted);
        Assert.Empty(info.WriteTargets);
        Assert.False(info.HasDynamicSql);
    }

    [Fact]
    public void Disabled_and_instead_of_flags_come_from_catalog()
    {
        var b = new CatalogBuilder().Table("dbo", "T");
        b.Trigger("dbo", "T", "trOff", ["DELETE"], "CREATE TRIGGER dbo.trOff ON dbo.T INSTEAD OF DELETE AS BEGIN DELETE dbo.T WHERE 1=0; END",
            insteadOf: true, disabled: true);
        var info = Assert.Single(new TriggerAnalyzer().Analyze(b.Build()));
        Assert.True(info.IsDisabled);
        Assert.True(info.IsInsteadOf);
        Assert.Equal([ChangeOperation.Delete], info.Events);
        Assert.Equal(["dbo.T"], info.WriteTargets);
    }

    [Theory]
    [InlineData("COMMIT TRAN; BEGIN TRAN;", true, false)]
    [InlineData("COMMIT TRANSACTION;", true, false)]
    [InlineData("SAVE TRANSACTION s1;", true, false)]
    [InlineData("BEGIN TRAN; COMMIT;", true, false)]
    [InlineData("IF @@ROWCOUNT > 1 ROLLBACK TRANSACTION;", false, true)]
    [InlineData("UPDATE dbo.L SET a = 1;", false, false)]
    public void Transaction_control_other_than_rollback_is_detected(string body, bool control, bool rollback)
    {
        var a = AnalyzeOne($"CREATE TRIGGER dbo.trX ON dbo.T AFTER INSERT AS BEGIN SET NOCOUNT ON; {body} END");
        Assert.Equal((control, rollback), (a.HasTransactionControl, a.HasRollback));
    }
}
