using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Core.Tests.Catalog;

/// <summary>DBRIPPLE_SQL_CONNECTION이 없으면 건너뛰는 DB 통합 테스트.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CatalogReader.ConnectionEnvVar)))
            Skip = $"{CatalogReader.ConnectionEnvVar} 없음";
    }
}

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
        Assert.Equal(["Id"], t.PrimaryKey!);
        Assert.Equal("Code", Assert.Single(t.UniqueNotNullKeys!)[0]);
        Assert.Equal([DmlOperation.Update], t.UserLast!);
        Assert.True(t.Columns[2].HasNondeterministicDefault);
        Assert.True(t.Columns[3].HasNondeterministicDefault);
        Assert.False(t.Columns[1].HasNondeterministicDefault);
        Assert.True(cat.Triggers[1].IsEncrypted);
    }

    [SqlFact]
    public async Task Reads_catalog_from_database()
    {
        await using var conn = new SqlConnection(Environment.GetEnvironmentVariable(CatalogReader.ConnectionEnvVar));
        var cat = await CatalogReader.ReadAsync(conn);
        Assert.NotEmpty(cat.Files);
    }
}
