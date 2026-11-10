using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Diff.Tests;

/// <summary>C12 실DB 검사(dbrt_dif_): 형식별 비교·정규화, 키 없는 테이블 다중집합, UNIQUE 키, PK 변경, 정렬, 트랜잭션 유무.</summary>
public class NetDifferDbTests
{
    const string TypesSetup = """
        CREATE TABLE dbo.T (
            id int NOT NULL PRIMARY KEY,
            vc varchar(20) NULL, nv nvarchar(20) NULL, vb varbinary(10) NULL, fl float NULL, rl real NULL,
            dto datetimeoffset NULL, x xml NULL, g geography NULL, h hierarchyid NULL, sv sql_variant NULL,
            tx text NULL, nt ntext NULL, im image NULL, m money NULL, d decimal(9,2) NULL, dt2 datetime2 NULL,
            b bit NULL, u uniqueidentifier NULL, [a.b] int NULL, calc AS (d * 2));
        """;

    const string TypesSeed = """
        INSERT dbo.T (id, vc, nv, vb, fl, rl, dto, x, g, h, sv, tx, nt, im, m, d, dt2, b, u, [a.b])
        SELECT n, 'abc', N'가a', 0x01, 0e0, 0.1, '2020-01-01T10:00:00+09:00', N'<a/>', geography::Point(1, 2, 4326),
               hierarchyid::Parse('/1/'), CAST(1 AS int), 'abc', N'abc', 0x01, 1.00, 1.50, '2020-01-01', 0,
               '00000000-0000-0000-0000-000000000001', 7
        FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14),(15),(16),(17),(18),(19),(20),(50)) AS v(n);
        UPDATE dbo.T SET vc = NULL, nv = NULL, vb = NULL, fl = NULL, rl = NULL, dto = NULL, x = NULL, g = NULL, h = NULL, sv = NULL,
            tx = NULL, nt = NULL, im = NULL, m = NULL, d = NULL, dt2 = NULL, b = NULL, u = NULL, [a.b] = NULL WHERE id IN (18, 19);
        """;

    // 행마다 한 가지를 바꾼다. 기대 바뀐 열은 열 이름(생성 속성 @srid·@type은 기본 열로 접힘).
    static readonly (int Id, string Sql, string[] Changed)[] TypeEdits =
    [
        (1, "vc = 'ABC'", ["vc"]),                                   // 대소문자만(CI 데이터 정렬에서 =)
        (2, "vc = 'abc '", ["vc"]),                                  // 끝 공백만(= 무시)
        (3, "vb = 0x0100", ["vb"]),                                  // 끝 0x00(varbinary = 무시)
        (4, "fl = -fl", ["fl"]),                                     // -0 (= 같음)
        (5, "dto = '2020-01-01T01:00:00+00:00'", ["dto"]),           // 같은 순간, 다른 오프셋
        (6, "x = N'<b/>'", ["x"]),
        (7, "g = geography::Point(1, 2, 4269)", ["g"]),              // SRID만(g@srid → g)
        (8, "h = hierarchyid::Parse('/2/')", ["h"]),
        (9, "sv = CAST(1 AS bigint)", ["sv"]),                       // 기저 형식만(sv@type → sv)
        (10, "tx = 'abd'", ["tx"]),
        (11, "nt = N'abd'", ["nt"]),
        (12, "im = 0x0100", ["im"]),
        (13, "d = 2.50", ["d", "calc"]),                             // 계산 열 포함
        (14, "m = 1.01, b = 1, u = '00000000-0000-0000-0000-000000000002'", ["m", "b", "u"]),
        (15, "x = CONVERT(xml, CONVERT(nvarchar(max), x)), vc = vc, g = g, tx = tx, rl = rl", []), // 값 그대로(결과 없음)
        (16, "dt2 = DATEADD(nanosecond, 100, dt2), rl = 0.2", ["rl", "dt2"]),
        (17, "vc = NULL, sv = NULL", ["vc", "sv"]),
        (18, "vc = 'x', nv = N'y'", ["vc", "nv"]),                   // NULL → 값
        (20, "[a.b] = 8", ["a.b"]),                                  // '.'이 든 열 이름
    ];

    [SqlFact]
    public async Task Types_are_compared_exactly_and_normalized()
    {
        await using var dbs = await DiffTestDbs.CreateAsync("types", TypesSetup, TypesSeed);
        await using var conn = await TestDb.OpenAsync(dbs.Live);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        foreach (var (id, set, _) in TypeEdits) await Sql.ExecAsync(conn, tx, $"UPDATE dbo.T SET {set} WHERE id = {id};");
        await Sql.ExecAsync(conn, tx, "DELETE dbo.T WHERE id = 50; INSERT dbo.T (id, vc) VALUES (100, 'new');");

        var changes = await new NetDiffer().DiffAsync(new SqlSession { Connection = conn, Transaction = tx }, dbs.Live, dbs.Snap, dbs.Tables, default);

        var updated = changes.Where(c => c.Change == NetChangeKind.Updated).ToDictionary(c => int.Parse(c.Key.Values[0]!));
        foreach (var (id, _, cols) in TypeEdits)
        {
            if (cols.Length == 0) { Assert.False(updated.ContainsKey(id), $"id {id}: 값이 그대로인데 결과에 나옴"); continue; }
            Assert.True(updated.ContainsKey(id), $"id {id}: 바뀐 행을 놓침");
            Assert.Equal(cols, updated[id].ChangedColumns);
        }
        Assert.Equal(TypeEdits.Count(e => e.Changed.Length > 0), updated.Count);

        // §6.1 표현(C11과 같은 Core 식): base64, 숫자 원문, 생성 속성, float 스타일 3, text류는 문자열
        Assert.Equal("AQ==", updated[3].Before!["vb"]);
        Assert.Equal("AQA=", updated[3].After!["vb"]);
        Assert.StartsWith("-0.0000000000000000e+000", updated[4].After!["fl"]);
        Assert.Equal("4326", updated[7].Before!["g@srid"]);
        Assert.Equal("4269", updated[7].After!["g@srid"]);
        Assert.Equal("/2/", updated[8].After!["h"]);
        Assert.Equal("int", updated[9].Before!["sv@type"]);
        Assert.Equal("bigint", updated[9].After!["sv@type"]);
        Assert.Equal("abd", updated[10].After!["tx"]);
        Assert.Equal("abd", updated[11].After!["nt"]);
        Assert.Equal("AQA=", updated[12].After!["im"]);
        Assert.Equal("2.50", updated[13].After!["d"]);
        Assert.Equal("5.00", updated[13].After!["calc"]);
        Assert.Equal("1.0100", updated[14].After!["m"]);
        Assert.Equal("true", updated[14].After!["b"]);
        Assert.Null(updated[17].After!["vc"]);
        Assert.Equal("8", updated[20].After!["a.b"]);
        Assert.Equal("<b/>", updated[6].After!["x"]);

        var del = Assert.Single(changes, c => c.Change == NetChangeKind.Deleted);
        Assert.Equal(["50"], del.Key.Values);
        Assert.Null(del.After);
        var ins = Assert.Single(changes, c => c.Change == NetChangeKind.Inserted);
        Assert.Equal(["100"], ins.Key.Values);
        Assert.Equal(KeyKind.PK, ins.Key.Kind);
        Assert.Equal("new", ins.After!["vc"]);
        Assert.Null(ins.Before);
        Assert.All(changes, c => Assert.Null(c.ExplainedBy));

        // 결과 순서는 키의 SQL 순서(문자열 순서가 아님: 2 < 10)
        var ids = changes.Select(c => int.Parse(c.Key.Values[0]!)).ToList();
        Assert.Equal(ids.Order().ToList(), ids);
        await tx.RollbackAsync();
    }

    const string KeysSetup = """
        CREATE TABLE dbo.H (a int NULL, s nvarchar(10) NULL);
        INSERT dbo.H VALUES (1, N'a'), (1, N'a'), (1, N'a'), (2, N'b'), (NULL, NULL), (NULL, NULL);
        CREATE TABLE dbo.U (code nvarchar(10) NOT NULL, v int NULL, CONSTRAINT UQ_U UNIQUE (code));
        INSERT dbo.U VALUES (N'k1', 1), (N'k2', 2);
        CREATE TABLE dbo.P2 (a int NOT NULL, b int NOT NULL, v int NULL, CONSTRAINT PK_P2 PRIMARY KEY (a, b));
        INSERT dbo.P2 VALUES (1, 1, 0), (1, 2, 0), (2, 1, 0);
        CREATE TABLE dbo.Same (id int NOT NULL PRIMARY KEY, v int NULL);
        INSERT dbo.Same VALUES (1, 1);
        """;

    const string KeysEdits = """
        DELETE dbo.H WHERE a = 1;
        INSERT dbo.H VALUES (1, N'a'), (2, N'b'), (3, N'c');
        UPDATE TOP (1) dbo.H SET s = N'z' WHERE a IS NULL;
        UPDATE dbo.U SET v = 20 WHERE code = N'k2';
        UPDATE dbo.P2 SET b = b + 10 WHERE a = 1;
        UPDATE dbo.Same SET v = v;
        """;

    static readonly string[] KeysExpected =
    [
        // 키 없는 테이블은 행 이미지 JSON 바이트 순서(UTF-16): {"p0":1… < {"p0":2… < {"p0":3… < {"p0":null,"p1":"z"} < {"p0":null,"p1":null}
        "dbo.H MULTISET_REMOVED x2 key[]=() cols[]",     // (1, a) 3 → 1
        "dbo.H MULTISET_ADDED x1 key[]=() cols[]",       // (2, b) 1 → 2
        "dbo.H MULTISET_ADDED x1 key[]=() cols[]",       // (3, c) 0 → 1
        "dbo.H MULTISET_ADDED x1 key[]=() cols[]",       // (NULL, z) 0 → 1
        "dbo.H MULTISET_REMOVED x1 key[]=() cols[]",     // (NULL, NULL) 2 → 1
        "dbo.P2 DELETED x1 key[a,b]=(1,1) cols[]",
        "dbo.P2 DELETED x1 key[a,b]=(1,2) cols[]",
        "dbo.P2 INSERTED x1 key[a,b]=(1,11) cols[]",
        "dbo.P2 INSERTED x1 key[a,b]=(1,12) cols[]",
        "dbo.U UPDATED x1 key[code]=(k2) cols[v]",
    ];

    [SqlFact]
    public async Task Heap_unique_and_pk_change_with_sqlclient_transaction()
    {
        await using var dbs = await DiffTestDbs.CreateAsync("keys", KeysSetup);
        await using var conn = await TestDb.OpenAsync(dbs.Live);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        await Sql.ExecAsync(conn, tx, KeysEdits);
        var session = new SqlSession { Connection = conn, Transaction = tx };

        var first = await new NetDiffer().DiffAsync(session, dbs.Live, dbs.Snap, dbs.Tables, default);
        var second = await new NetDiffer().DiffAsync(session, dbs.Live, dbs.Snap, dbs.Tables.Reverse().ToList(), default);

        Assert.Equal(string.Join("\n", KeysExpected), Sql.Dump(first));
        Assert.Equal(Sql.Dump(first), Sql.Dump(second)); // 입력 순서와 무관하게 결정적
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first, ContractJson.Options),
            System.Text.Json.JsonSerializer.Serialize(second, ContractJson.Options));

        var heap = first.Where(c => c.Table == "dbo.H").ToList();
        Assert.All(heap, c => Assert.Equal(KeyKind.ROW, c.Key.Kind));
        Assert.Equal(("1", "a"), Img(heap.Single(c => c.Multiplicity == 2).Before!));
        Assert.Equal(((string?)null, (string?)null), Img(heap.Single(c => c.Change == NetChangeKind.MultisetRemoved && c.Multiplicity == 1).Before!));
        Assert.Equal([("2", "b"), ("3", "c"), (null, "z")],
            heap.Where(c => c.Change == NetChangeKind.MultisetAdded).Select(c => Img(c.After!)).ToList());
        Assert.All(heap.Where(c => c.Change == NetChangeKind.MultisetAdded), c => Assert.Null(c.Before));

        var u = first.Single(c => c.Table == "dbo.U");
        Assert.Equal(KeyKind.UNIQUE, u.Key.Kind);
        Assert.Equal(("2", "20"), (u.Before!["v"], u.After!["v"]));
        await tx.RollbackAsync();

        // 롤백 뒤(트랜잭션 없음) 차이 0
        Assert.Empty(await new NetDiffer().DiffAsync(new SqlSession { Connection = conn }, dbs.Live, dbs.Snap, dbs.Tables, default));
    }

    static (string?, string?) Img(IReadOnlyDictionary<string, string?> d) => (d["a"], d["s"]);

    [SqlFact]
    public async Task Computed_column_that_overflows_on_unchanged_row_does_not_break_diff()
    {
        // R18 모양: 비지속 계산 열이 한 행에서 8115를 낸다. 그 행이 바뀌지 않았으면 비교가 성공해야 한다(exec-1 재현).
        await using var dbs = await DiffTestDbs.CreateAsync("calc", """
            CREATE TABLE dbo.Calc (id int NOT NULL PRIMARY KEY, a int NULL, c AS (a * 1000));
            INSERT dbo.Calc (id, a) VALUES (1, 1), (2, 3000000);
            CREATE TABLE dbo.CalcHeap (a int NULL, c AS (a * 1000));
            INSERT dbo.CalcHeap (a) VALUES (1), (3000000);
            """);
        await using var conn = await TestDb.OpenAsync(dbs.Live);
        await Assert.ThrowsAsync<SqlException>(() => Sql.ScalarAsync<int>(conn, null, "SELECT COUNT(*) FROM (SELECT c FROM dbo.Calc) AS q WHERE c IS NOT NULL;"));
        await Sql.ExecAsync(conn, null, "SET XACT_ABORT ON; BEGIN TRAN;");
        await Sql.ExecAsync(conn, null, "UPDATE dbo.Calc SET a = 5 WHERE id = 1; INSERT dbo.CalcHeap (a) VALUES (7);");

        var changes = await new NetDiffer().DiffAsync(new SqlSession { Connection = conn }, dbs.Live, dbs.Snap, dbs.Tables, default);

        Assert.Equal("dbo.Calc UPDATED x1 key[id]=(1) cols[a,c]\ndbo.CalcHeap MULTISET_ADDED x1 key[]=() cols[]", Sql.Dump(changes));
        Assert.Equal(("1000", "5000"), (changes[0].Before!["c"], changes[0].After!["c"]));  // 바뀐 행 이미지에는 계산 열이 있다
        Assert.False(changes[1].After!.ContainsKey("c"));                                  // 키 없는 테이블 이미지는 계산 열을 뺀다
        Assert.Equal(1, await Sql.ScalarAsync<int>(conn, null, "SELECT @@TRANCOUNT;"));
        await Sql.ExecAsync(conn, null, "ROLLBACK;");
    }

    [SqlFact]
    public async Task Works_with_tsql_transaction_and_without_any_transaction()
    {
        await using var dbs = await DiffTestDbs.CreateAsync("tran", KeysSetup);
        await using var conn = await TestDb.OpenAsync(dbs.Live);
        var session = new SqlSession { Connection = conn };

        // C10 방식: XACT_ABORT ON + T-SQL BEGIN TRAN, SqlSession.Transaction = null
        await Sql.ExecAsync(conn, null, "SET XACT_ABORT ON; BEGIN TRAN;");
        await Sql.ExecAsync(conn, null, "UPDATE dbo.U SET v = 99 WHERE code = N'k1';");
        var inTran = await new NetDiffer().DiffAsync(session, dbs.Live, dbs.Snap, dbs.Tables, default);
        Assert.Equal("dbo.U UPDATED x1 key[code]=(k1) cols[v]", Sql.Dump(inTran));
        Assert.Equal(1, await Sql.ScalarAsync<int>(conn, null, "SELECT @@TRANCOUNT;")); // 트랜잭션을 끝내지 않음

        // 실패로 트랜잭션이 끝난 뒤(XACT_ABORT ON, PK 위반) 검사: 차이 0
        var ex = await Assert.ThrowsAsync<SqlException>(() => Sql.ExecAsync(conn, null, "INSERT dbo.Same VALUES (1, 1);"));
        Assert.Equal(2627, ex.Number);
        Assert.Equal(0, await Sql.ScalarAsync<int>(conn, null, "SELECT @@TRANCOUNT;"));
        Assert.Empty(await new NetDiffer().DiffAsync(session, dbs.Live, dbs.Snap, dbs.Tables, default));

        // SqlClient 트랜잭션이 실패로 끝나 좀비가 된 세션도 받는다
        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync())
        {
            await Sql.ExecAsync(conn, tx, "UPDATE dbo.U SET v = 98 WHERE code = N'k1';");
            await Assert.ThrowsAsync<SqlException>(() => Sql.ExecAsync(conn, tx, "INSERT dbo.Same VALUES (1, 1);"));
            Assert.Null(tx.Connection);
            Assert.Empty(await new NetDiffer().DiffAsync(new SqlSession { Connection = conn, Transaction = tx }, dbs.Live, dbs.Snap, dbs.Tables, default));
        }

        // 자동 커밋으로 확정된 변경도 트랜잭션 없이 보인다(롤백 뒤 남은 변경 경로)
        await Sql.ExecAsync(conn, null, "SET XACT_ABORT OFF; DELETE dbo.Same WHERE id = 1;");
        Assert.Equal("dbo.Same DELETED x1 key[id]=(1) cols[]",
            Sql.Dump(await new NetDiffer().DiffAsync(session, dbs.Live, dbs.Snap, dbs.Tables, default)));
    }
}
