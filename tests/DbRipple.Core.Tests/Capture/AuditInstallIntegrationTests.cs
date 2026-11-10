using System.Globalization;
using System.Text.Json;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Tests.Capture;

/// <summary>
/// C7 → C9 → 설치를 실제 SQL Server의 임시 DB에서 돌리고, 트랜잭션 안 DML로 감사 계약(§6.2, D3)을 확인한 뒤 ROLLBACK한다.
/// </summary>
public class AuditInstallIntegrationTests
{
    static readonly string[] Setup =
    [
        "CREATE SCHEMA z;",
        """
        CREATE TABLE z.Zoo (
            Id int NOT NULL PRIMARY KEY,
            Ti tinyint NULL, Si smallint NULL, Bi bigint NULL, De decimal(38, 10) NULL,
            F float NULL, R real NULL, M money NULL, D2 datetime2(7) NULL, Dto datetimeoffset(7) NULL,
            Bin varbinary(max) NULL, Gd uniqueidentifier NOT NULL CONSTRAINT DF_Zoo_Gd DEFAULT NEWID(),
            H hierarchyid NULL, G geography NULL, Gm geometry NULL, X xml NULL, V sql_variant NULL,
            Calc AS (Bi * 2), Rv rowversion, Nt ntext NULL);
        """,
        "CREATE TABLE z.Child (Id int NOT NULL PRIMARY KEY, ZooId int NOT NULL CONSTRAINT FK_Child_Zoo REFERENCES z.Zoo (Id) ON DELETE CASCADE);",
        "CREATE TABLE z.Heap (a int NULL, b nvarchar(10) NULL);",
        "CREATE TABLE z.Uq (k int NOT NULL CONSTRAINT UQ_Uq_k UNIQUE, v int NULL);",
        "CREATE TABLE z.Odd (id int NOT NULL PRIMARY KEY, [b.c] int NULL, g geography NULL, [g@srid] int NULL);",
        "CREATE TABLE z.DotKey ([k.x] int NOT NULL PRIMARY KEY, v int NULL);",
        "CREATE TRIGGER z.uq_user_last ON z.Uq AFTER UPDATE AS SET NOCOUNT ON;",
        "EXEC sys.sp_settriggerorder @triggername = N'z.uq_user_last', @order = N'Last', @stmttype = N'UPDATE';",
        "CREATE USER dbr_it_owner WITHOUT LOGIN;",
        "CREATE SCHEMA o AUTHORIZATION dbr_it_owner;",
        "CREATE TABLE o.Owned (id int NOT NULL PRIMARY KEY);",
    ];

    [SqlFact]
    public async Task Installed_triggers_capture_dml_per_contract()
    {
        var db = TestSql.NewName("c9");
        await TestSql.ExecAsync("master", $"CREATE DATABASE [{db}];");
        await using var cleanup = new TempDatabases(db);
        await TestSql.ExecAsync(db, Setup);

        DbCatalog catalog;
        await using (var conn = await TestSql.OpenAsync(db)) catalog = await CatalogReader.ReadAsync(conn);
        var plan = AuditTriggerGenerator.Generate(catalog.ToCaptureInput(), DateTimeOffset.Now);
        await using (var steps = new SqlBaselineSteps(TestSql.ConnectionString()))
            foreach (var b in plan.Batches) await steps.ExecuteAsync(db, b.Sql, default); // 13601 등 생성 시점 오류가 없어야 함

        var m = plan.Manifest;
        Assert.Equal([new ExcludedTable("o.Owned", "OWNER_NOT_DBO"), new ExcludedTable("z.DotKey", "JSON_KEY_CONFLICT")], m.ExcludedTables);
        Assert.Equal("dbr_it_owner", catalog.Tables.Single(t => t.Name == "Owned").Owner);
        Assert.Equal("dbo", catalog.Tables.Single(t => t.Name == "Zoo").Owner);
        Assert.Equal(new AuditOrderInfo("LAST", "NONE", "LAST"), m.Tables.Single(t => t.Table == "z.Uq").AuditOrder);
        Assert.Equal(["Nt"], m.Tables.Single(t => t.Table == "z.Zoo").UnobservableColumns);
        Assert.Equal(["b.c", "g@srid"], m.Tables.Single(t => t.Table == "z.Odd").UnobservableColumns);

        await using var c = await TestSql.OpenAsync(db);
        // Last 지정(사용자 Last가 있는 z.Uq UPDATE만 아님), First 없음, 정의에 저장된 SET 옵션 ON
        var triggers = await TestSql.RowsAsync(c, """
            SELECT OBJECT_SCHEMA_NAME(tr.parent_id) + N'.' + OBJECT_NAME(tr.parent_id), te.type_desc,
                   CAST(te.is_first AS int), CAST(te.is_last AS int), CAST(sm.uses_quoted_identifier AS int), CAST(sm.uses_ansi_nulls AS int)
            FROM sys.triggers AS tr
            JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
            JOIN sys.sql_modules AS sm ON sm.object_id = tr.object_id
            WHERE tr.name LIKE N'dbr[_]audit[_]%';
            """);
        Assert.Equal(5 * 3, triggers.Count);
        foreach (var r in triggers)
        {
            Assert.Equal(0, r[2]);
            Assert.Equal((string)r[0]! == "z.Uq" && (string)r[1]! == "UPDATE" ? 0 : 1, r[3]);
            Assert.Equal((1, 1), ((int)r[4]!, (int)r[5]!));
        }
        Assert.Equal(0L, await TestSql.ScalarAsync<long>(c, "SELECT COUNT_BIG(*) FROM audit.AuditHeader;"));

        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var b0 = await TestSql.ScalarAsync<long>(c, "SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader;", tx);

            // 마지막 문장 행 수 = 발동 행 수: DML 직후 @@ROWCOUNT가 그대로다.
            // V는 sql_variant로 CAST한다(여러 행 VALUES는 열 형식을 우선순위로 맞춰 decimal이 float가 됨)
            Assert.Equal(2, await RowCountAsync(c, tx, """
                INSERT INTO z.Zoo (Id, Ti, Si, Bi, De, F, R, M, D2, Dto, Bin, H, G, Gm, X, V, Nt) VALUES
                (1, 255, -32768, 4611686018427387903, 1234567890123456789012345678.0123456789, 0.1E0 + 0.2E0,
                 CAST(1000 AS real) + CAST(POWER(2E0, -14) AS real), 922337203685477.5807, '2026-11-10T09:00:00.1234567',
                 '2026-11-10T09:00:00.1234567+09:00', 0x00FF, hierarchyid::Parse('/1/2/'),
                 geography::STGeomFromText('POINT(127.1 37.5 10.5 2.25)', 4326), geometry::Point(1, 2, 0), N'<a b="1"/>',
                 CAST(CAST(1.25 AS decimal(5, 2)) AS sql_variant), N'note'),
                (2, 0, 0, 1, 0, 2.2250738585072014E-308, 0, 0, '0001-01-01', '0001-01-01T00:00:00+00:00', NULL, NULL, NULL, NULL, NULL,
                 CAST(CAST(0.1E0 AS float) AS sql_variant), NULL);
                """));
            var images = await ImagesAsync(c, tx, b0, "[z].[Zoo]", "I");
            Assert.Equal(2, images.Count);
            Assert.All(images, i => Assert.Equal("INSERTED", i.Kind));
            var one = JsonDocument.Parse(images.Single(i => i.Key == """{"Id":1}""").Row!).RootElement;
            var two = JsonDocument.Parse(images.Single(i => i.Key == """{"Id":2}""").Row!).RootElement;

            // SP6 변환: 서버의 같은 식과 문자 그대로 같고, float/real은 되읽어도 같은 값(무손실)
            var expected = (await TestSql.RowsAsync(c, """
                SELECT CONVERT(varchar(30), F, 3), CONVERT(varchar(30), CONVERT(float, R), 3), G.ToString(), Gm.ToString(), H.ToString(),
                       CONVERT(nvarchar(max), X), F, R
                FROM z.Zoo WHERE Id = 1;
                """, tx)).Single();
            Assert.Equal(expected[0], one.GetProperty("F").GetString());
            Assert.Equal((double)expected[6]!, double.Parse(one.GetProperty("F").GetString()!, CultureInfo.InvariantCulture));
            Assert.Equal(expected[1], one.GetProperty("R").GetString());
            Assert.Equal((float)expected[7]!, (float)double.Parse(one.GetProperty("R").GetString()!, CultureInfo.InvariantCulture));
            Assert.Equal(expected[2], one.GetProperty("G").GetString());
            Assert.Contains("10.5 2.25", one.GetProperty("G").GetString()); // Z/M 유지(STAsText는 버림)
            Assert.Equal(4326, one.GetProperty("G@srid").GetInt32());
            Assert.Equal(expected[3], one.GetProperty("Gm").GetString());
            Assert.Equal(0, one.GetProperty("Gm@srid").GetInt32());
            Assert.Equal("/1/2/", one.GetProperty("H").GetString());
            Assert.Equal(expected[5], one.GetProperty("X").GetString());
            Assert.Equal("1.25", one.GetProperty("V").GetRawText());
            Assert.Equal("decimal", one.GetProperty("V@type").GetString());
            Assert.Equal("4611686018427387903", one.GetProperty("Bi").GetRawText()); // 숫자 원문
            Assert.Equal("1234567890123456789012345678.0123456789", one.GetProperty("De").GetRawText());
            Assert.Equal(JsonValueKind.String, one.GetProperty("Rv").ValueKind);       // rowversion base64
            Assert.Equal(JsonValueKind.String, one.GetProperty("Gd").ValueKind);
            Assert.Equal("9223372036854775806", one.GetProperty("Calc").GetRawText());  // 계산 열 포함
            Assert.False(one.TryGetProperty("Nt", out _));                              // ntext는 수집 불가 열
            Assert.Equal(JsonValueKind.Null, two.GetProperty("G").ValueKind);           // INCLUDE_NULL_VALUES
            Assert.Equal(JsonValueKind.Null, two.GetProperty("G@srid").ValueKind);
            Assert.Equal("float", two.GetProperty("V@type").GetString());
            Assert.Equal(0.1, double.Parse(two.GetProperty("V").GetString()!, CultureInfo.InvariantCulture));
            Assert.Equal(2.2250738585072014E-308, double.Parse(two.GetProperty("F").GetString()!, CultureInfo.InvariantCulture));

            // UPDATE 2행: 헤더 건수 2/2, 이미지 DELETED 2 + INSERTED 2
            var b1 = await MaxSeqAsync(c, tx);
            Assert.Equal(2, await RowCountAsync(c, tx, "UPDATE z.Zoo SET Bi = Bi - 1;"));
            var u = Assert.Single(await HeadersAsync(c, tx, b1));
            Assert.Equal(("[z].[Zoo]", "U", 2L, 2L, "FULL"), (u.Table, u.Op, u.Inserted, u.Deleted, u.Mode));
            var uImages = await ImagesAsync(c, tx, b1, "[z].[Zoo]", "U");
            Assert.Equal(2, uImages.Count(i => i.Kind == "DELETED"));
            Assert.Equal(2, uImages.Count(i => i.Kind == "INSERTED"));

            // 직접 대상 0행 발동: 헤더만 남고(건수 0) 이미지는 없음
            var b2 = await MaxSeqAsync(c, tx);
            Assert.Equal(0, await RowCountAsync(c, tx, "UPDATE z.Heap SET a = a WHERE 1 = 0;"));
            var zero = Assert.Single(await HeadersAsync(c, tx, b2));
            Assert.Equal(("[z].[Heap]", "U", 0L, 0L), (zero.Table, zero.Op, zero.Inserted, zero.Deleted));
            Assert.Empty(await ImagesAsync(c, tx, b2, "[z].[Heap]", "U"));

            // FK CASCADE: 자식 D 묶음과 부모 D 묶음이 모두 남는다
            await RowCountAsync(c, tx, "INSERT INTO z.Child (Id, ZooId) VALUES (1, 1), (2, 1);");
            var b3 = await MaxSeqAsync(c, tx);
            Assert.Equal(1, await RowCountAsync(c, tx, "DELETE FROM z.Zoo WHERE Id = 1;"));
            var d = await HeadersAsync(c, tx, b3);
            Assert.Contains(d, h => h is { Table: "[z].[Child]", Op: "D", Deleted: 2L });
            Assert.Contains(d, h => h is { Table: "[z].[Zoo]", Op: "D", Deleted: 1L });
            Assert.Equal(2, (await ImagesAsync(c, tx, b3, "[z].[Child]", "D")).Count(i => i.Kind == "DELETED"));

            // KEYS 모드: 키만, rowJson NULL, 행 수는 같다
            await RowCountAsync(c, tx, "UPDATE audit.AuditControl SET mode = 'KEYS' WHERE id = 1;");
            var b4 = await MaxSeqAsync(c, tx);
            Assert.Equal(1, await RowCountAsync(c, tx, "UPDATE z.Zoo SET Bi = 0 WHERE Id = 2;"));
            Assert.Equal("KEYS", Assert.Single(await HeadersAsync(c, tx, b4)).Mode);
            var keys = await ImagesAsync(c, tx, b4, "[z].[Zoo]", "U");
            Assert.Equal(2, keys.Count);
            Assert.All(keys, i => { Assert.Null(i.Row); Assert.Equal("""{"Id":2}""", i.Key); });
            await RowCountAsync(c, tx, "UPDATE audit.AuditControl SET mode = 'FULL' WHERE id = 1;");

            // 키 없는 테이블: keyJson NULL, 같은 행 2개도 2개 이미지
            var b5 = await MaxSeqAsync(c, tx);
            Assert.Equal(2, await RowCountAsync(c, tx, "INSERT INTO z.Heap (a, b) VALUES (1, N'x'), (1, N'x');"));
            var heap = await ImagesAsync(c, tx, b5, "[z].[Heap]", "I");
            Assert.Equal(2, heap.Count);
            Assert.All(heap, i => { Assert.Null(i.Key); Assert.Equal("""{"a":1,"b":"x"}""", i.Row); });

            // JSON 키 충돌 열은 rowJson에서 빠지고, g@srid는 생성 속성(SRID)이다
            var b6 = await MaxSeqAsync(c, tx);
            Assert.Equal(1, await RowCountAsync(c, tx, "INSERT INTO z.Odd (id, [b.c], g, [g@srid]) VALUES (1, 2, geography::Point(37.5, 127.1, 4326), 99);"));
            var odd = JsonDocument.Parse(Assert.Single(await ImagesAsync(c, tx, b6, "[z].[Odd]", "I")).Row!).RootElement;
            Assert.Equal(["id", "g", "g@srid"], odd.EnumerateObject().Select(p => p.Name));
            Assert.Equal(4326, odd.GetProperty("g@srid").GetInt32());

            // 사용자 Last가 있는 연산도 감사 트리거는 발동한다(순서만 보장 안 됨)
            var b7 = await MaxSeqAsync(c, tx);
            await RowCountAsync(c, tx, "INSERT INTO z.Uq (k, v) VALUES (1, 1); UPDATE z.Uq SET v = 2;");
            Assert.Equal(["I", "U"], (await HeadersAsync(c, tx, b7)).Select(h => h.Op));

            await tx.RollbackAsync();
        }
        Assert.Equal(0L, await TestSql.ScalarAsync<long>(c, "SELECT COUNT_BIG(*) FROM audit.AuditHeader;"));

        // 이미 설치된 DB에서 다시 계획을 만들어도 audit 테이블은 대상이 아니다(재귀 방지)
        DbCatalog installed;
        await using (var conn = await TestSql.OpenAsync(db)) installed = await CatalogReader.ReadAsync(conn);
        var again = AuditTriggerGenerator.Generate(installed.ToCaptureInput(), DateTimeOffset.Now);
        Assert.DoesNotContain(again.Manifest.Tables, t => t.Table.StartsWith("audit.", StringComparison.Ordinal));
        Assert.Contains(again.Manifest.Notes, n => n.Contains("audit 스키마") && n.Contains("3개"));
    }

    sealed record Header(long Seq, string Table, string Op, long Inserted, long Deleted, string Mode);

    sealed record Image(string Kind, string? Key, string? Row);

    static async Task<int> RowCountAsync(SqlConnection c, SqlTransaction tx, string dml) =>
        await TestSql.ScalarAsync<int>(c, dml + "\nSELECT @@ROWCOUNT;", tx);

    static Task<long> MaxSeqAsync(SqlConnection c, SqlTransaction tx) =>
        TestSql.ScalarAsync<long>(c, "SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader;", tx);

    static async Task<List<Header>> HeadersAsync(SqlConnection c, SqlTransaction tx, long after) =>
        (await TestSql.RowsAsync(c, $"""
            SELECT captureSeq, tableName, operation, insertedCount, deletedCount, imageMode
            FROM audit.AuditHeader WHERE captureSeq > {after} ORDER BY captureSeq;
            """, tx))
        .Select(r => new Header((long)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[3]!, (long)r[4]!, (string)r[5]!)).ToList();

    static async Task<List<Image>> ImagesAsync(SqlConnection c, SqlTransaction tx, long after, string table, string op) =>
        (await TestSql.RowsAsync(c, $"""
            SELECT i.imageKind, i.keyJson, i.rowJson
            FROM audit.AuditRowImage AS i JOIN audit.AuditHeader AS h ON h.captureSeq = i.captureSeq
            WHERE h.captureSeq > {after} AND h.tableName = N'{table}' AND h.operation = '{op}'
            ORDER BY i.captureSeq, i.imageKind, i.ordinal;
            """, tx))
        .Select(r => new Image((string)r[0]!, (string?)r[1], (string?)r[2])).ToList();
}
