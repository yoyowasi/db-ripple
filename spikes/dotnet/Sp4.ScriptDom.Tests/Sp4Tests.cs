using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
namespace Sp4.ScriptDom.Tests;

public class ParserClassTests
{
    static readonly Assembly Asm = typeof(TSqlParser).Assembly;

    [Fact]
    public void Package_version_is_180_140()
        => Assert.StartsWith("18.0.140", Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

    [Fact]
    public void Parser_classes_include_170_and_180()
    {
        var names = Asm.GetTypes().Where(t => t.IsPublic && t.Name.StartsWith("TSql") && t.Name.EndsWith("Parser")).Select(t => t.Name).ToHashSet();
        Assert.Contains("TSql160Parser", names);
        Assert.Contains("TSql170Parser", names);
        Assert.Contains("TSql180Parser", names);
        var max = names.Where(n => n.Length == 13).Max(); // TSqlNNNParser
        Assert.Equal("TSql180Parser", max);
        Assert.Contains(nameof(SqlVersion.Sql170), Enum.GetNames<SqlVersion>());
        Assert.Contains(nameof(SqlVersion.Sql180), Enum.GetNames<SqlVersion>());
    }

    static int Errors(TSqlParser p, string sql) { using var r = new StringReader(sql); p.Parse(r, out var e); return e.Count; }

    [Theory]
    [InlineData("CREATE JSON INDEX ix ON dbo.t(j) FOR ('$.a')")]
    [InlineData("CREATE VECTOR INDEX ix ON dbo.t(v) WITH (METRIC='cosine', TYPE='diskann')")]
    [InlineData("CREATE EXTERNAL MODEL m WITH (LOCATION='https://x', API_FORMAT='OpenAI', MODEL_TYPE=EMBEDDINGS, MODEL='x')")]
    [InlineData("SELECT AI_GENERATE_EMBEDDINGS(N'a' USE MODEL m)")]
    public void New_syntax_fails_on_160_passes_on_170_and_180(string sql)
    {
        Assert.True(Errors(new TSql160Parser(true), sql) > 0);
        Assert.Equal(0, Errors(new TSql170Parser(true), sql));
        Assert.Equal(0, Errors(new TSql180Parser(true), sql));
    }

    [Theory]
    [InlineData("CREATE TABLE t(j JSON)")]
    [InlineData("SELECT 1 WHERE 'a' IS DISTINCT FROM 'b'")]
    [InlineData("MERGE t USING s ON 1=1 WHEN MATCHED THEN UPDATE SET a=1;")]
    [InlineData("UPDATE dbo.t SET a=1 OUTPUT inserted.a")]
    [InlineData("CREATE TRIGGER dbo.tr ON dbo.t AFTER INSERT AS BEGIN SELECT 1 END")]
    public void Common_syntax_same_result_on_160_and_180(string sql)
    {
        Assert.Equal(0, Errors(new TSql160Parser(true), sql));
        Assert.Equal(0, Errors(new TSql180Parser(true), sql));
    }
}

public class BatchAndPositionTests
{
    public static (TSqlScript s, IList<ParseError> e) Parse(string sql, TSqlParser? p = null)
    {
        using var r = new StringReader(sql);
        var f = (p ?? new TSql160Parser(true)).Parse(r, out var e);
        return ((TSqlScript)f, e);
    }

    [Theory]
    [InlineData("SELECT 1\n/* \nGO\n*/\nGO\nSELECT 2", 2, new[] { 1, 6 })]   // 블록 주석 안 GO 무시
    [InlineData("SELECT 1 -- GO\nGO\nSELECT 2", 2, new[] { 1, 3 })]          // 줄 주석 안 GO 무시
    [InlineData("SELECT 'a\nGO\nb'\nGO\nSELECT 2", 2, new[] { 1, 5 })]       // 문자열 안 GO 무시
    [InlineData("SELECT 1\ngo\nSELECT 2", 2, new[] { 1, 3 })]               // 소문자
    [InlineData("SELECT 1\nGO -- c\nSELECT 2", 2, new[] { 1, 3 })]          // 뒤 줄 주석
    [InlineData("SELECT 1\nGO /* x */\nSELECT 2", 2, new[] { 1, 3 })]       // 뒤 블록 주석
    [InlineData("SELECT 1\n   GO  \nSELECT 2", 2, new[] { 1, 3 })]          // 들여쓰기·공백
    public void Go_splits_batches(string sql, int count, int[] startLines)
    {
        var (s, e) = Parse(sql);
        Assert.Empty(e);
        Assert.Equal(count, s.Batches.Count);
        Assert.Equal(startLines, s.Batches.Select(b => b.StartLine).ToArray());
        Assert.All(s.Batches, b => Assert.Single(b.Statements));
    }

    [Fact]
    public void Last_batch_without_trailing_go_is_kept()
    {
        var (s, e) = Parse("SELECT 1\nGO\nSELECT 2");
        Assert.Empty(e);
        Assert.Equal(2, s.Batches.Count);
    }

    [Fact]
    public void Go_with_count_is_a_parse_error_so_caller_must_reject_or_preprocess()
    {
        var (_, e) = Parse("SELECT 1\nGO 2\nSELECT 2");
        Assert.Single(e);
        Assert.Equal(2, e[0].Line);
    }

    [Fact]
    public void Go_in_middle_of_line_is_not_a_separator_it_is_an_alias()
    {
        var (s, e) = Parse("SELECT 1 GO\nSELECT 2");
        Assert.Empty(e);
        Assert.Single(s.Batches);
        Assert.Equal(2, s.Batches[0].Statements.Count);
    }

    [Fact]
    public void Error_position_is_script_wide_in_second_batch()
    {
        var sql = "SELECT 1\nGO\nSELECT 2\nSELEC 3\nFROM x\nGO\nSELECT 4";
        var (s, e) = Parse(sql);
        Assert.Single(e);
        Assert.Equal(46010, e[0].Number);
        Assert.Equal(4, e[0].Line);        // 배치 내 상대 줄(2)이 아니라 전체 기준 4
        Assert.Equal(1, e[0].Column);
        Assert.Equal(21, e[0].Offset);     // 전체 스크립트 문자 오프셋
        Assert.False(string.IsNullOrEmpty(e[0].Message));
        Assert.Equal(3, s.Batches.Count);  // 오류가 있어도 배치는 나뉜다
    }

    [Fact]
    public void Statement_span_matches_source_text_crlf()
    {
        var sql = "SELECT 1;\r\n  INSERT INTO dbo.T (a)\r\n  VALUES (1);\r\nGO\r\nUPDATE dbo.T SET a=2";
        var (s, e) = Parse(sql);
        Assert.Empty(e);
        var st = s.Batches.SelectMany(b => b.Statements).ToList();
        Assert.Equal(3, st.Count);

        var ins = st[1];
        Assert.Equal((2, 3), (ins.StartLine, ins.StartColumn));
        Assert.Equal("INSERT INTO dbo.T (a)\r\n  VALUES (1);", sql.Substring(ins.StartOffset, ins.FragmentLength));
        var last = ins.ScriptTokenStream[ins.LastTokenIndex];
        Assert.Equal(3, last.Line);              // 끝 줄
        Assert.Equal(13, last.Column);           // 마지막 토큰(;) 열. 끝 열 = Column + 길이 - 1

        var upd = st[2];                         // 두 번째 배치: 줄 번호는 전체 기준
        Assert.Equal((5, 1), (upd.StartLine, upd.StartColumn));
        Assert.Equal("UPDATE dbo.T SET a=2", sql.Substring(upd.StartOffset, upd.FragmentLength));
    }
}

public class TriggerAnalyzerTests
{
    const string IduSalesOrderDetail = @"
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

            UPDATE Person.Person
            SET ModifiedDate = GETDATE()
            FROM inserted
            INNER JOIN Sales.SalesOrderHeader AS soh ON inserted.SalesOrderID = soh.SalesOrderID
            WHERE Person.Person.BusinessEntityID = soh.CustomerID;
        END;

        UPDATE h
        SET h.SubTotal = h.SubTotal + i.LineTotal
        FROM Sales.SalesOrderHeader h
        INNER JOIN inserted i ON h.SalesOrderID = i.SalesOrderID;

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

    const string USalesOrderHeader = @"
CREATE TRIGGER Sales.uSalesOrderHeader ON Sales.SalesOrderHeader AFTER UPDATE AS
BEGIN
    UPDATE Sales.SalesOrderHeader
    SET RevisionNumber = RevisionNumber + 1
    WHERE SalesOrderID IN (SELECT inserted.SalesOrderID FROM inserted);
    UPDATE sp SET SalesYTD = SalesYTD + 1
    FROM Sales.SalesPerson AS sp INNER JOIN inserted AS i ON sp.BusinessEntityID = i.SalesPersonID;
    UPDATE Sales.SalesTerritory SET SalesYTD = 0 WHERE TerritoryID = 1;
END;";

    const string DEmployee = @"
CREATE TRIGGER HumanResources.dEmployee ON HumanResources.Employee INSTEAD OF DELETE AS
BEGIN
    DECLARE @Count int;
    SET @Count = @@ROWCOUNT;
    IF @Count = 0 RETURN;
    BEGIN
        RAISERROR(N'Employees cannot be deleted.', 10, 1);
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    END;
END;";

    [Fact]
    public void IduSalesOrderDetail_like()
    {
        var a = Assert.Single(TriggerAnalyzer.Analyze(IduSalesOrderDetail));
        Assert.Equal("Sales.iduSalesOrderDetail", a.TriggerName);
        Assert.False(a.InsteadOf);
        Assert.Equal(new[] { "Person.Person", "Production.TransactionHistory", "Sales.SalesOrderHeader" }, a.WriteTargets.ToArray());
        Assert.True(a.Rollback);
        Assert.False(a.DynamicSql);
        Assert.Equal(new[] { "dbo.uspLogError" }, a.ProcedureCalls.ToArray());
        Assert.Empty(a.External);
    }

    [Fact]
    public void USalesOrderHeader_like()
    {
        var a = Assert.Single(TriggerAnalyzer.Analyze(USalesOrderHeader));
        Assert.Equal(new[] { "Sales.SalesOrderHeader", "Sales.SalesPerson", "Sales.SalesTerritory" }, a.WriteTargets.ToArray());
        Assert.False(a.Rollback);
        Assert.Empty(a.ProcedureCalls);
    }

    [Fact]
    public void DEmployee_like()
    {
        var a = Assert.Single(TriggerAnalyzer.Analyze(DEmployee));
        Assert.True(a.InsteadOf);
        Assert.True(a.Rollback);
        Assert.True(a.Raiserror);
        Assert.Empty(a.WriteTargets); // 본문에 쓰기 문이 없다. ON 테이블은 호출자가 별도로 안다.
    }

    [Fact]
    public void Dynamic_sql_and_external_flags()
    {
        var sql = @"
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
END;";
        var a = Assert.Single(TriggerAnalyzer.Analyze(sql));
        Assert.True(a.DynamicSql);
        Assert.Contains("dbo.Target", a.WriteTargets);
        Assert.Contains("dbo.Old", a.WriteTargets);
        Assert.Contains("LinkedSrv.RemoteDb.dbo.Audit", a.WriteTargets);
        Assert.DoesNotContain("dbo.Z", a.WriteTargets); // 동적 SQL 내부는 정적으로 못 본다. DynamicSql 플래그가 대신 알린다.
        Assert.Contains("OPENQUERY", a.External);
        Assert.Contains(a.External, x => x.StartsWith("xp_:"));
        Assert.Contains(a.External, x => x.StartsWith("msdb:"));
        Assert.Contains(a.External, x => x.StartsWith("4-part:"));
        Assert.Contains("master.dbo.xp_cmdshell", a.ProcedureCalls);
    }
}
