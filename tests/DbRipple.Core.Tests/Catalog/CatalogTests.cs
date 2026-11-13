using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Core.Tests.Catalog;

public class CatalogTests
{
    [Fact]
    public void All_catalog_queries_parse()
    {
        foreach (var sql in CatalogQueries.All)
        {
            new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
            Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Message)) + "\n" + sql);
        }
    }

    [Fact]
    public void Catalog_maps_to_capture_input()
    {
        var cat = new DbCatalog(
            [new(10, "Sales", "Orders", TemporalType.None, false)],
            [
                new(10, 1, "Id", "int", false, false, false, true, false, null),
                new(10, 2, "Code", "nvarchar", false, false, false, false, false, null),
                new(10, 3, "Guid", "uniqueidentifier", false, false, false, false, false, "(newid())"),
                new(10, 4, "Seq", "bigint", false, false, false, false, false, "(NEXT VALUE FOR [dbo].[S])"),
            ],
            [
                new(10, 2, "UQ_Code", false, true, 1, "Code", false),
                new(10, 1, "PK_Orders", true, true, 1, "Id", false),
            ],
            [],
            [
                new(50, "tU", 10, "OBJECT_OR_COLUMN", false, false, "UPDATE", false, true, "CREATE TRIGGER ..."),
                new(51, "tI", 10, "OBJECT_OR_COLUMN", true, false, "INSERT", false, false, null),
            ],
            []);
        var t = Assert.Single(cat.ToCaptureInput());
        Assert.Equal("dbo", t.Owner);
        Assert.Equal("app", Assert.Single((cat with { Tables = [cat.Tables[0] with { Owner = "app" }] }).ToCaptureInput()).Owner);
        Assert.Equal(["Id"], t.PrimaryKey!);
        Assert.Equal("Code", Assert.Single(t.UniqueNotNullKeys!)[0]);
        Assert.Equal([DmlOperation.Update], t.UserLast!);
        Assert.True(t.Columns[2].HasNondeterministicDefault);
        Assert.True(t.Columns[3].HasNondeterministicDefault);
        Assert.False(t.Columns[1].HasNondeterministicDefault);
        Assert.True(cat.Triggers[1].IsEncrypted);
    }

    [Fact]
    public void Summary_counts_objects_not_event_rows()
    {
        var cat = new DbCatalog(
            [new(10, "Sales", "H", TemporalType.None, false), new(11, "Sales", "D", TemporalType.None, false), new(12, "dbo", "Log", TemporalType.None, false)],
            [],
            [new(10, 1, "PK_H", true, true, 1, "Id", false), new(11, 1, "PK_D", true, true, 1, "HId", false), new(11, 1, "PK_D", true, true, 2, "Line", false)],
            [
                new(20, "FK_D_H", 11, 10, "CASCADE", "NO_ACTION", false, false, 1, "HId", "Id"),
                new(21, "FK_Log_H", 12, 10, "NO_ACTION", "NO_ACTION", true, true, 1, "HId", "Id"),
            ],
            [
                new(50, "tD", 11, "OBJECT_OR_COLUMN", false, false, "INSERT", false, false, "x"),
                new(50, "tD", 11, "OBJECT_OR_COLUMN", false, false, "UPDATE", false, true, "x"),
                new(51, "dH", 10, "OBJECT_OR_COLUMN", true, false, "DELETE", false, false, "x"),
                new(90, "ddl", 0, "DATABASE", false, false, "CREATE_TABLE", false, false, "x"),
                new(90, "ddl", 0, "DATABASE", false, false, "ALTER_TABLE", false, false, "x"),
            ],
            [new(1, "D", "ROWS", "/d.mdf"), new(2, "L", "LOG", "/l.ldf")]);
        var s = CatalogSummary.From(cat);
        Assert.Equal(3, s.Tables);
        Assert.Equal(2, s.ForeignKeys);
        Assert.Equal(1, s.DisabledForeignKeys);
        Assert.Equal(1, s.OnDelete["CASCADE"]);
        Assert.Equal(2, s.OnUpdate["NO_ACTION"]);
        Assert.Equal("FK_D_H: Sales.D → Sales.H ON DELETE CASCADE, ON UPDATE NO_ACTION", Assert.Single(s.ReferentialActions));
        Assert.Equal((1, 1), (s.AfterTriggers, s.InsteadOfTriggers));
        Assert.Contains("Sales.D.tD: AFTER INSERT, UPDATE Last(UPDATE)", s.DmlTriggers);
        Assert.Equal(["ddl"], s.DdlTriggers);
        Assert.Equal(["dbo.Log"], s.TablesWithoutPrimaryKey);
        Assert.Equal(1, s.DataFiles);
    }

    // 설계 A24를 실제 AdventureWorks(AW_Origin, 읽기만)에서 C7로 다시 확인한다. 값이 바뀌면 A24와 기준선 가정을 다시 본다.
    [SqlFact]
    public async Task Reads_adventureworks_catalog_matching_A24()
    {
        await using var conn = await TestSql.OpenAsync("AW_Origin");
        var cat = await CatalogReader.ReadAsync(conn);
        var s = CatalogSummary.From(cat);
        Assert.Equal(71, s.Tables);
        Assert.Equal(90, s.ForeignKeys);
        Assert.Equal(new Dictionary<string, int> { ["CASCADE"] = 2, ["NO_ACTION"] = 88 }, s.OnDelete);
        Assert.Equal(new Dictionary<string, int> { ["NO_ACTION"] = 90 }, s.OnUpdate);
        Assert.All(s.ReferentialActions, r => Assert.Contains("→ Sales.SalesOrderHeader ON DELETE CASCADE", r));
        Assert.Equal((8, 2), (s.AfterTriggers, s.InsteadOfTriggers));
        Assert.Equal(["ddlDatabaseTriggerLog"], s.DdlTriggers);
        Assert.Empty(s.TablesWithoutPrimaryKey); // A24의 "dbo.DatabaseLog는 PK 없는 힙"과 다름: 힙이지만 비클러스터형 PK가 있다

        // 형식 매핑: 별칭 형식은 기저 형식, 시스템 CLR 형식은 이름 그대로(사용자 UDT 아님), rowversion·계산 열·IDENTITY
        var cols = cat.ToCaptureInput().ToDictionary(t => t.DisplayName);
        ColumnInfo Col(string table, string column) => cols[table].Columns.Single(c => c.Name == column);
        Assert.Equal("nvarchar", Col("Person.Person", "FirstName").TypeName);        // dbo.Name
        Assert.Equal("bit", Col("Person.Person", "NameStyle").TypeName);            // dbo.NameStyle
        Assert.Equal("tinyint", Col("Sales.SalesOrderHeader", "RevisionNumber").TypeName);
        Assert.Equal("smallint", Col("Sales.SalesOrderDetail", "OrderQty").TypeName);
        Assert.Equal(new ColumnInfo("OrganizationNode", "hierarchyid"), Col("HumanResources.Employee", "OrganizationNode"));
        Assert.Equal(new ColumnInfo("SpatialLocation", "geography"), Col("Person.Address", "SpatialLocation"));
        Assert.Equal("xml", Col("Person.Person", "Demographics").TypeName);
        Assert.True(Col("Sales.SalesOrderHeader", "SalesOrderID").IsIdentity);
        Assert.True(Col("Sales.SalesOrderHeader", "TotalDue").IsComputed);
        Assert.True(Col("Sales.SalesOrderHeader", "rowguid").HasNondeterministicDefault);
        Assert.Equal(["SalesOrderID", "SalesOrderDetailID"], cols["Sales.SalesOrderDetail"].PrimaryKey!);
        Assert.Equal(["DatabaseLogID"], cols["dbo.DatabaseLog"].PrimaryKey!);
        Assert.All(cols.Values, t => Assert.Equal("dbo", t.Owner)); // 모든 스키마가 dbo 소유 → OWNER_NOT_DBO 제외 없음
        Assert.All(cols.Values, t => Assert.Empty(t.UserFirst!));
        Assert.All(cols.Values, t => Assert.Empty(t.UserLast!));
        Assert.Equal("AdventureWorks", Assert.Single(cat.Files, f => f.TypeDesc == "ROWS").LogicalName);
    }

    // 2주차 인계 1(3주차): 프로시저 모듈은 DbCatalog 끝의 기본값 null 매개변수다. 생략한 호출자(3주차 이전)는 빈 목록을 본다.
    [Fact]
    public void Procedure_modules_are_optional_and_default_to_empty()
    {
        var old = new DbCatalog([], [], [], [], [], []);
        Assert.Null(old.Procedures);
        Assert.Empty(old.ProcedureModules);
        var cat = old with { Procedures = [new(7, "dbo", "uspLogError", "CREATE PROCEDURE dbo.uspLogError AS INSERT dbo.ErrorLog DEFAULT VALUES;"), new(8, "dbo", "uspSecret", null)] };
        Assert.Equal(2, cat.ProcedureModules.Count);
        Assert.False(cat.ProcedureModules[0].IsEncrypted);
        Assert.True(cat.ProcedureModules[1].IsEncrypted);
        Assert.Contains(CatalogQueries.Procedures, CatalogQueries.All);
        // ContractJson으로 기준선 픽스처에 실릴 때 계산 속성은 빼고 Procedures만 싣는다(역직렬화 생성자 매개변수와 이름이 같아야 한다)
        var json = System.Text.Json.JsonSerializer.Serialize(cat);
        Assert.DoesNotContain("ProcedureModules", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<DbCatalog>(json)!;
        Assert.Equal(cat.ProcedureModules, back.ProcedureModules);
    }

    // 2026-11-13: 함수 목록도 끝의 기본값 null 매개변수다. 예전 기준선(null)은 "함수 정보 없음"이라 EXEC 본문의 함수 호출을 거부한다.
    [Fact]
    public void Function_modules_are_optional_and_null_means_unknown()
    {
        var old = new DbCatalog([], [], [], [], [], []);
        Assert.False(old.FunctionsKnown);
        Assert.Empty(old.FunctionModules);
        var cat = old with { Functions = [new(9, "dbo", "ufnX", "FN", "CREATE FUNCTION dbo.ufnX() RETURNS int AS BEGIN RETURN 1; END;", true), new(10, "dbo", "clrY", "FS", null, true)] };
        Assert.True(cat.FunctionsKnown);
        Assert.True(cat.FunctionModules[0].IsTSql);
        Assert.False(cat.FunctionModules[1].IsTSql);
        Assert.True((old with { Functions = [] }).FunctionsKnown); // 빈 목록 = 읽었는데 없음
        Assert.Contains(CatalogQueries.Functions, CatalogQueries.All);
        Assert.Contains(CatalogQueries.Synonyms, CatalogQueries.All);
        Assert.Empty(old.SynonymModules);
        Assert.True((old with { Synonyms = [new("dbo", "F", "x")] }).IsSynonymName("DBO", "f")); // 대소문자 무시
        var json = System.Text.Json.JsonSerializer.Serialize(cat);
        Assert.DoesNotContain("FunctionModules", json);
        Assert.DoesNotContain("FunctionsKnown", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<DbCatalog>(json)!;
        Assert.Equal(cat.FunctionModules, back.FunctionModules);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<DbCatalog>(System.Text.Json.JsonSerializer.Serialize(old))!.FunctionsKnown);
    }

    // AW: 사용자 정의 함수 목록(2026-11-13). 스칼라 dbo.ufnLeadingZeros(FN), 다중 문 테이블 값 dbo.ufnGetContactInformation(TF). 읽기만.
    [SqlFact]
    public async Task Reads_adventureworks_function_modules()
    {
        await using var conn = await TestSql.OpenAsync("AW_Origin");
        var cat = await CatalogReader.ReadAsync(conn);
        Assert.True(cat.FunctionsKnown);
        var lz = Assert.Single(cat.FunctionModules, f => f.Schema == "dbo" && f.Name == "ufnLeadingZeros");
        Assert.Equal("FN", lz.Type);
        Assert.True(lz.IsTSql && lz.ExecuteAsCaller && lz.Definition is not null);
        Assert.Equal("TF", Assert.Single(cat.FunctionModules, f => f.Schema == "dbo" && f.Name == "ufnGetContactInformation").Type);
    }

    // AW: 트리거들이 CATCH에서 부르는 dbo.uspLogError와 그것이 부르는 dbo.uspPrintError의 본문을 읽는다(읽기만).
    [SqlFact]
    public async Task Reads_adventureworks_procedure_modules()
    {
        await using var conn = await TestSql.OpenAsync("AW_Origin");
        var cat = await CatalogReader.ReadAsync(conn);
        var log = Assert.Single(cat.ProcedureModules, m => m.Schema == "dbo" && m.Name == "uspLogError");
        Assert.Contains("ErrorLog", log.Definition!);
        Assert.Contains("uspPrintError", log.Definition!);
        Assert.Single(cat.ProcedureModules, m => m.Schema == "dbo" && m.Name == "uspPrintError" && !m.IsEncrypted);
    }
}
