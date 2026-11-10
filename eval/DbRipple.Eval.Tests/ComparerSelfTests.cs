using DbRipple.Contracts;
using DbRipple.Eval;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Eval.Tests;

/// <summary>
/// 비교기 자체 검증(§10.3): NULL↔NULL, NULL↔빈 문자열, 힙 중복 행(다중집합), xml, hierarchyid, NOT NULL 유일 키, 이름에 '.'이 있는 열,
/// 감사 스키마 제외, 롤백 뒤 복원. 전용 DB dbrt_eval_cmp_*에서만 하고 끝에 지운다.
/// </summary>
public class ComparerSelfTests
{
    const string Schema = """
        CREATE TABLE dbo.t_null (id int NOT NULL PRIMARY KEY, a int NULL, b nvarchar(10) NULL);
        INSERT dbo.t_null VALUES (1, NULL, NULL), (2, 5, N'x'), (4, NULL, NULL);
        CREATE TABLE dbo.heap (a int NULL, b int NULL);
        INSERT dbo.heap VALUES (1, 1), (1, 1), (2, 2);
        CREATE TABLE dbo.t_xml (id int NOT NULL PRIMARY KEY, x xml NULL);
        INSERT dbo.t_xml VALUES (1, N'<a>1</a>'), (2, N'<b/>');
        CREATE TABLE dbo.t_hid (id int NOT NULL PRIMARY KEY, h hierarchyid NULL);
        INSERT dbo.t_hid VALUES (1, '/1/'), (2, NULL);
        CREATE TABLE dbo.t_uq (code varchar(10) NOT NULL, v int NULL);
        CREATE UNIQUE INDEX ux_t_uq ON dbo.t_uq (code);
        INSERT dbo.t_uq VALUES ('a', 1);
        CREATE TABLE dbo.t_dot (id int NOT NULL PRIMARY KEY, [a.b] int NULL, [a] int NULL);
        INSERT dbo.t_dot VALUES (1, 1, 1);
        """;

    const string Mutations = """
        UPDATE dbo.t_null SET a = NULL WHERE id = 1;
        UPDATE dbo.t_null SET a = NULL WHERE id = 2;
        UPDATE dbo.t_null SET b = N'' WHERE id = 4;
        INSERT dbo.t_null VALUES (3, NULL, NULL);
        DELETE TOP (1) FROM dbo.heap WHERE a = 1;
        INSERT dbo.heap VALUES (2, 2), (3, NULL), (3, NULL);
        UPDATE dbo.t_xml SET x = N'<a>2</a>' WHERE id = 1;
        UPDATE dbo.t_xml SET x = N'<b/>' WHERE id = 2;
        UPDATE dbo.t_hid SET h = '/1/2/' WHERE id = 1;
        UPDATE dbo.t_hid SET h = NULL WHERE id = 2;
        UPDATE dbo.t_uq SET v = 2 WHERE code = 'a';
        UPDATE dbo.t_dot SET [a.b] = 2 WHERE id = 1;
        INSERT audit.x VALUES (1);
        """;

    static string K(string col, string? val) => RowKeys.Canonical([new KeyValuePair<string, string?>(col, val)]);

    [SqlFact]
    public async Task Comparer_detects_exact_changes_for_special_values_and_heaps()
    {
        var db = TestDb.NewName("eval", "cmp");
        await using var cleanup = new TestDatabases(db);
        await TestDb.ExecAsync("master", $"CREATE DATABASE {HashSnapshotComparer.Quote(db)};");
        await TestDb.ExecAsync(db, "CREATE SCHEMA audit;", "CREATE TABLE audit.x (id int NOT NULL PRIMARY KEY);", Schema);

        var cmp = new HashSnapshotComparer();
        await using var conn = await TestDb.OpenAsync(db);
        var s = new SqlSession { Connection = conn };
        await Exec(s, "BEGIN TRANSACTION;");
        try
        {
            var (tables, excluded) = await cmp.LoadTablesAsync(s, db, ["audit"], [], default);
            Assert.Contains(excluded, e => e.StartsWith("audit.x", StringComparison.Ordinal));
            Assert.Equal(OracleKeyKind.UNIQUE, tables.Single(t => t.Name == "t_uq").KeyKind);
            Assert.Equal(OracleKeyKind.ROW, tables.Single(t => t.Name == "heap").KeyKind);

            var before = await cmp.TakeAsync(s, db, tables, default);
            Assert.Equal(2, before.Tables["dbo.heap"][RowKeys.Canonical(new Dictionary<string, string?> { ["a"] = "1", ["b"] = "1" })].Multiplicity);
            await Exec(s, Mutations);
            var after = await cmp.TakeAsync(s, db, tables, default);
            var result = cmp.Compare(before, after);
            Assert.Empty(result.Issues);
            var ch = result.Changes.ToDictionary(c => (c.Table, c.Key, c.Change));

            // NULL → NULL은 변경 아님, 값 → NULL과 NULL → ''는 변경
            Assert.DoesNotContain(result.Changes, c => c.Table == "dbo.t_null" && c.Key == K("id", "1"));
            var r2 = ch[("dbo.t_null", K("id", "2"), ChangeKinds.Updated)];
            Assert.Equal(["a"], r2.ChangedColumns);
            Assert.Equal("5", r2.Before!["a"]);
            Assert.Null(r2.After!["a"]);
            var r4 = ch[("dbo.t_null", K("id", "4"), ChangeKinds.Updated)];
            Assert.Equal(["b"], r4.ChangedColumns);
            Assert.Null(r4.Before!["b"]);
            Assert.Equal("", r4.After!["b"]);
            var r3 = ch[("dbo.t_null", K("id", "3"), ChangeKinds.Inserted)];
            Assert.Null(r3.After!["a"]);
            Assert.Equal("3", r3.After!["id"]);

            // 힙: 같은 값 행은 개수로
            string Row(string a, string? b) => RowKeys.Canonical(new Dictionary<string, string?> { ["a"] = a, ["b"] = b });
            Assert.Equal(1, ch[("dbo.heap", Row("1", "1"), ChangeKinds.MultisetRemoved)].Multiplicity);
            Assert.Equal(1, ch[("dbo.heap", Row("2", "2"), ChangeKinds.MultisetAdded)].Multiplicity);
            Assert.Equal(2, ch[("dbo.heap", Row("3", null), ChangeKinds.MultisetAdded)].Multiplicity);

            // xml: 같은 값 재대입은 변경 아님(SQL에서 xml은 = 비교 불가, 하네스는 문자열 해시로 본다)
            Assert.Equal("<a>2</a>", ch[("dbo.t_xml", K("id", "1"), ChangeKinds.Updated)].After!["x"]);
            Assert.DoesNotContain(result.Changes, c => c.Table == "dbo.t_xml" && c.Key == K("id", "2"));

            // hierarchyid: ToString() 값, NULL → NULL은 변경 아님
            var h = ch[("dbo.t_hid", K("id", "1"), ChangeKinds.Updated)];
            Assert.Equal("/1/", h.Before!["h"]);
            Assert.Equal("/1/2/", h.After!["h"]);
            Assert.DoesNotContain(result.Changes, c => c.Table == "dbo.t_hid" && c.Key == K("id", "2"));

            // NOT NULL 유일 키, '.' 이름 열(FOR JSON PATH 중첩·13601을 위치 별칭으로 피함)
            Assert.Equal(["v"], ch[("dbo.t_uq", K("code", "a"), ChangeKinds.Updated)].ChangedColumns);
            Assert.Equal(["a.b"], ch[("dbo.t_dot", K("id", "1"), ChangeKinds.Updated)].ChangedColumns);

            // 감사 스키마는 결과에 없음, 그 밖의 변경도 없음
            Assert.DoesNotContain(result.Changes, c => c.Table.StartsWith("audit.", StringComparison.Ordinal));
            Assert.Equal(10, result.Changes.Count);

            // 감사 스키마를 빼지 않으면 보인다(제외가 실제로 효과가 있음)
            var (all, _) = await cmp.LoadTablesAsync(s, db, [], [], default);
            Assert.Contains(all, t => t.Display == "audit.x");

            await Exec(s, "ROLLBACK TRANSACTION;");
            var restored = await cmp.TakeAsync(s, db, tables, default);
            Assert.Empty(cmp.Compare(before, restored).Changes);
            Assert.Equal(HashSnapshotComparer.Digest(before), HashSnapshotComparer.Digest(restored));
        }
        finally
        {
            await Exec(s, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            SqlConnection.ClearPool(conn);
        }
    }

    [Fact]
    public void Compare_flags_hash_and_cell_disagreement()
    {
        var t = new OracleTable("dbo", "t", OracleKeyKind.PK, ["id"], [new OracleColumn("id", 1, "int", false, false, false, false, false)]);
        var key = K("id", "1");
        RowHashSnapshot Snap(string hash, string? v) => new("db", [t], new Dictionary<string, IReadOnlyDictionary<string, RowHash>>
        {
            ["dbo.t"] = new Dictionary<string, RowHash> { [key] = new(hash, new Dictionary<string, string?> { ["id"] = "1", ["v"] = v }, 1) },
        });
        var cmp = new HashSnapshotComparer();
        Assert.Single(cmp.Compare(Snap("AA", "1"), Snap("BB", "1")).Issues);      // 해시만 다름
        var r = cmp.Compare(Snap("AA", "1"), Snap("AA", "2"));                       // 셀만 다름
        Assert.Single(r.Issues);
        Assert.Single(r.Changes);
    }

    static async Task Exec(SqlSession s, string sql)
    {
        await using var cmd = s.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    [SqlFact]
    public async Task Heap_row_key_and_images_leave_out_computed_columns()
    {
        var db = TestDb.NewName("eval", "cmpc");
        await using var cleanup = new TestDatabases(db);
        await TestDb.ExecAsync("master", $"CREATE DATABASE {HashSnapshotComparer.Quote(db)};");
        // c는 비지속 계산 열, o는 a = 3000000이면 읽기만 해도 오버플로(8115)하는 계산 열
        await TestDb.ExecAsync(db, """
            CREATE TABLE dbo.hc (a int NULL, b int NULL, c AS (a * 2), o AS (a * 1000));
            INSERT dbo.hc (a, b) VALUES (1, 1), (1, 1), (3000000, 5);
            CREATE TABLE dbo.kc (id int NOT NULL PRIMARY KEY, a int NOT NULL, c AS (a * 2));
            INSERT dbo.kc (id, a) VALUES (1, 1);
            """);
        var cmp = new HashSnapshotComparer();
        await using var conn = await TestDb.OpenAsync(db);
        var s = new SqlSession { Connection = conn };
        var (tables, _) = await cmp.LoadTablesAsync(s, db, [], [], default);
        var heap = tables.Single(t => t.Display == "dbo.hc");
        Assert.Equal(["a", "b"], HashSnapshotComparer.Items(heap).Select(i => i.Property));                         // ROW: 계산 열 제외
        Assert.Equal(["id", "a", "c"], HashSnapshotComparer.Items(tables.Single(t => t.Display == "dbo.kc")).Select(i => i.Property)); // 키 있는 테이블은 그대로

        await Exec(s, "BEGIN TRANSACTION;");
        try
        {
            var before = await cmp.TakeAsync(s, db, tables, default); // 오버플로 행이 있어도 8115 없이 읽힌다
            await Exec(s, "UPDATE TOP (1) dbo.hc SET a = 2 WHERE a = 1; UPDATE dbo.kc SET a = 2 WHERE id = 1;");
            var after = await cmp.TakeAsync(s, db, tables, default);
            var r = cmp.Compare(before, after);
            Assert.Empty(r.Issues);
            var hc = r.Changes.Where(c => c.Table == "dbo.hc").OrderBy(c => c.Change, StringComparer.Ordinal).ToList();
            Assert.Equal([(ChangeKinds.MultisetAdded, 1L), (ChangeKinds.MultisetRemoved, 1L)], hc.Select(c => (c.Change, c.Multiplicity)));
            Assert.All(hc, c => Assert.DoesNotContain("c", (c.After ?? c.Before)!.Keys));
            Assert.Equal(RowKeys.Canonical([new("a", "2"), new("b", "1")]), hc[0].Key);
            var kc = Assert.Single(r.Changes, c => c.Table == "dbo.kc");
            Assert.Equal(["a", "c"], kc.ChangedColumns); // 키 있는 테이블은 계산 열도 바뀐 열로 본다
        }
        finally { await Exec(s, "IF @@TRANCOUNT > 0 ROLLBACK;"); }
    }
}
