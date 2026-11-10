using System.Diagnostics;
using System.Globalization;
using System.Text;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Core.Sql;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Diff.Tests;

/// <summary>DBRIPPLE_RUN_SP7=1일 때만 도는 SP7(AW 전용 기준선을 복원하므로 수십 초·디스크 수백 MB).</summary>
public sealed class Sp7FactAttribute : FactAttribute
{
    public Sp7FactAttribute()
    {
        if (!TestDb.IsConfigured) Skip = $"{TestDb.ConnectionEnvVar} 없음";
        else if (Environment.GetEnvironmentVariable("DBRIPPLE_RUN_SP7") != "1") Skip = "SP7은 DBRIPPLE_RUN_SP7=1일 때만";
    }
}

/// <summary>
/// SP7(설계 §13, 2주차 계획 §6): REVERT 뒤 같은 연결·트랜잭션에서 미커밋 변경을 스냅샷과 비교했을 때 정확한지와 AW 전 테이블 시간.
/// 정확성은 NetDiffer와 다른 방법(키 목록 질의, 테이블별 EXCEPT 행 수, COUNT_BIG 증감)으로 판정한다. 기록: logs/week2/sp7-aw-netdiff.log.
/// </summary>
public class Sp7Tests
{
    const string DmlUser = "sp7_dml";
    readonly StringBuilder log = new();

    void Log(string line) => log.AppendLine(line);

    [Sp7Fact]
    public async Task Sp7_open_transaction_netdiff_on_adventureworks()
    {
        var logPath = Path.Combine(RepoPaths.Root, "logs", "week2", "sp7-aw-netdiff.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        Log($"# SP7 열린 트랜잭션 안 최종 차이(C12 NetDiffer) — {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}");
        try
        {
            await RunAsync();
            Log("\n판정: 통과(모든 독립 확인 일치)");
        }
        catch (Exception ex)
        {
            Log($"\n판정: 실패 — {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(logPath, log.ToString(), new UTF8Encoding(false));
        }
    }

    async Task RunAsync()
    {
        var sw = Stopwatch.StartNew();
        await using var tb = await TestBaseline.BuildAdventureWorksAsync("dif");
        Log($"기준선: TestBaseline.BuildAdventureWorksAsync(\"dif\") {sw.ElapsedMilliseconds} ms (원본·샌드박스·스냅샷: dbrt_dif_aw_* 전용)");

        await using var conn = await TestDb.OpenAsync(tb.Sandbox);
        Log($"서버: {await Sql.ScalarAsync<string>(conn, null, "SELECT CONCAT(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), N' ', CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(128)), N' ', CAST(SERVERPROPERTY('Edition') AS nvarchar(128)));")}");
        var tables = (await CatalogReader.ReadAsync(conn)).ToCaptureInput()
            .Where(t => !string.Equals(t.Schema, AuditTriggerGenerator.StoreSchema, StringComparison.OrdinalIgnoreCase)).ToList();
        var totalRows = await Sql.ScalarAsync<long>(conn, null, $"""
            SELECT SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            WHERE t.is_ms_shipped = 0 AND SCHEMA_NAME(t.schema_id) <> {SqlText.NLiteral(AuditTriggerGenerator.StoreSchema)};
            """);
        Log($"비교 대상: 사용자 테이블 {tables.Count}개(감사 스키마 제외), 행 {totalRows:N0}개, 키 종류 {string.Join(", ", tables.GroupBy(t => TableKey.Choose(t).Kind).Select(g => $"{g.Key} {g.Count()}"))}");
        Log($"연결: 풀링 끔, SqlClient 기본 SET 옵션 + XACT_ABORT ON(트랜잭션 구간), NetDiffer 테이블당 질의 1개");
        var differ = new NetDiffer();
        var noTran = new SqlSession { Connection = conn };

        // 0. 시작 상태 = 스냅샷
        var start = await differ.DiffAsync(noTran, tb.Sandbox, tb.Snapshot, tables, default);
        Log($"\n## 0. 시작 상태 차이(트랜잭션 없음): {start.Count}건");
        Assert.Empty(start);

        // 1. DML 권한만 가진 사용자(트랜잭션 밖, 계획 §6·Q12)
        await Sql.ExecAsync(conn, null, $"""
            CREATE USER {DmlUser} WITHOUT LOGIN WITH DEFAULT_SCHEMA = dbo;
            GRANT SELECT, INSERT, UPDATE, DELETE TO {DmlUser};
            """);
        var afterUser = await differ.DiffAsync(noTran, tb.Sandbox, tb.Snapshot, tables, default);
        Log($"\n## 1. 사용자 {DmlUser}(WITHOUT LOGIN, DB 수준 SELECT·INSERT·UPDATE·DELETE만) 생성 뒤 차이(트랜잭션 없음): {afterUser.Count}건");
        foreach (var c in afterUser) Log($"  {Describe(c)} {(c.After is { } a && a.TryGetValue("Event", out var ev) ? $"Event={ev}" : "")}");
        Log("  → AW DB DDL 트리거 ddlDatabaseTriggerLog가 CREATE_USER·GRANT를 dbo.DatabaseLog에 자동 커밋으로 남긴다. 아래 '롤백 뒤 차이'의 기준은 이 상태다.");

        // 2. 대상 행 고르기와 독립 기대값(스냅샷에서 직접 질의)
        var snap = SqlText.Quote(tb.Snapshot);
        var live = SqlText.Quote(tb.Sandbox);
        var x = await Sql.ScalarAsync<int>(conn, null, $"""
            SELECT TOP (1) h.SalesOrderID FROM {snap}.Sales.SalesOrderHeader AS h
            WHERE EXISTS (SELECT 1 FROM {snap}.Sales.SalesOrderHeaderSalesReason AS r WHERE r.SalesOrderID = h.SalesOrderID)
              AND (SELECT COUNT(*) FROM {snap}.Sales.SalesOrderDetail AS d WHERE d.SalesOrderID = h.SalesOrderID) >= 3
            ORDER BY h.SalesOrderID;
            """);
        var sodOfX = (await TestDb.RowsAsync(conn, $"SELECT SalesOrderDetailID FROM {snap}.Sales.SalesOrderDetail WHERE SalesOrderID = {x} ORDER BY SalesOrderDetailID;"))
            .Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!).ToList();
        var sohsrOfX = (await TestDb.RowsAsync(conn, $"SELECT SalesReasonID FROM {snap}.Sales.SalesOrderHeaderSalesReason WHERE SalesOrderID = {x} ORDER BY SalesReasonID;"))
            .Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!).ToList();
        var ySel = await TestDb.RowsAsync(conn, $"""
            SELECT TOP (1) d.SalesOrderDetailID, d.SalesOrderID, d.OrderQty FROM {snap}.Sales.SalesOrderDetail AS d
            JOIN {snap}.Sales.SalesOrderHeader AS h ON h.SalesOrderID = d.SalesOrderID
            WHERE d.SalesOrderID <> {x} AND h.SalesPersonID IS NOT NULL ORDER BY d.SalesOrderDetailID;
            """);
        var (y, z, qty) = (Convert.ToInt32(ySel[0][0]), Convert.ToInt32(ySel[0][1]), Convert.ToInt32(ySel[0][2]));
        Log($"\n## 2. DM1·DM2 대상(스냅샷에서 고름)");
        Log($"  DM1: DELETE Sales.SalesOrderHeader WHERE SalesOrderID = {x} (스냅샷 기준 연쇄 대상 SOD {sodOfX.Count}행, SOHSR {sohsrOfX.Count}행)");
        Log($"  DM2: UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderDetailID = {y} (주문 {z}, OrderQty {qty} → {qty + 1})");

        // 3. BEGIN TRAN → EXECUTE AS → DM1·DM2 → REVERT (C10 방식: T-SQL 트랜잭션, XACT_ABORT ON)
        await Sql.ExecAsync(conn, null, "SET XACT_ABORT ON; BEGIN TRAN;");
        await Sql.ExecAsync(conn, null, $"EXECUTE AS USER = N'{DmlUser}';");
        var asUser = await Sql.ScalarAsync<string>(conn, null, "SELECT USER_NAME();");
        var dm1 = await Rows(conn, $"DELETE Sales.SalesOrderHeader WHERE SalesOrderID = {x};");
        var dm2 = await Rows(conn, $"UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderDetailID = {y};");
        await Sql.ExecAsync(conn, null, "REVERT;");
        var afterRevert = await Sql.ScalarAsync<string>(conn, null, "SELECT USER_NAME();");
        var tranCount = await Sql.ScalarAsync<int>(conn, null, "SELECT @@TRANCOUNT;");
        Log($"\n## 3. 실행: 가장 중 USER_NAME()={asUser}, DM1 영향 {dm1}행, DM2 영향 {dm2}행, REVERT 뒤 USER_NAME()={afterRevert}, @@TRANCOUNT={tranCount}");
        Assert.Equal(DmlUser, asUser);
        Assert.Equal("dbo", afterRevert);
        Assert.Equal(1, tranCount);

        // 4. 열린 트랜잭션 안 시간: 워밍업 1회 + 5회
        var (inTran, inTranMs) = await TimeAsync(() => differ.DiffAsync(noTran, tb.Sandbox, tb.Snapshot, tables, default));
        Log($"\n## 4. 열린 트랜잭션 안(REVERT 뒤 같은 연결) 전 테이블 NetDiffer");
        LogTimes(inTranMs);
        Assert.Equal(1, await Sql.ScalarAsync<int>(conn, null, "SELECT @@TRANCOUNT;"));

        // 5. 정확성(독립 확인)
        var dbLog = afterUser.Select(Describe).ToHashSet(StringComparer.Ordinal);
        var dmChanges = inTran.Where(c => !dbLog.Contains(Describe(c))).ToList();
        Log($"\n## 5. 결과 {inTran.Count}건(그중 사용자 생성 DDL 로그 {inTran.Count - dmChanges.Count}건 제외 → DM 결과 {dmChanges.Count}건)");
        foreach (var g in dmChanges.GroupBy(c => (c.Table, c.Change)))
            Log($"  {g.Key.Table} {Sql.Name(g.Key.Change)} {g.Count()}건" +
                (g.Key.Change == NetChangeKind.Updated ? $" 바뀐 열 [{string.Join(", ", g.SelectMany(c => c.ChangedColumns).Distinct())}]" : "") +
                $" 키 {string.Join(" ", g.Take(4).Select(c => "(" + string.Join(",", c.Key.Values) + ")"))}{(g.Count() > 4 ? " …" : "")}");

        var checks = new List<(string Item, string Expected, string Actual)>();
        void Check(string item, object expected, object actual) => checks.Add((item, $"{expected}", $"{actual}"));
        List<string> Keys(string table, NetChangeKind k, int col = 0) =>
            dmChanges.Where(c => c.Table == table && c.Change == k).Select(c => c.Key.Values[col]!).ToList();

        Check("SOH DELETED 키", x, string.Join(",", Keys("Sales.SalesOrderHeader", NetChangeKind.Deleted)));
        Check("SOD DELETED 행 수(연쇄)", sodOfX.Count, Keys("Sales.SalesOrderDetail", NetChangeKind.Deleted).Count);
        Check("SOD DELETED 키(SalesOrderDetailID)", string.Join(",", sodOfX), string.Join(",", Keys("Sales.SalesOrderDetail", NetChangeKind.Deleted, 1)));
        Check("SOHSR DELETED 행 수(연쇄)", sohsrOfX.Count, Keys("Sales.SalesOrderHeaderSalesReason", NetChangeKind.Deleted).Count);
        Check("SOHSR DELETED 키(SalesReasonID)", string.Join(",", sohsrOfX), string.Join(",", Keys("Sales.SalesOrderHeaderSalesReason", NetChangeKind.Deleted, 1)));
        var sodY = dmChanges.SingleOrDefault(c => c.Table == "Sales.SalesOrderDetail" && c.Change == NetChangeKind.Updated);
        Check("SOD UPDATED 키·OrderQty", $"({z},{y}) {qty}->{qty + 1}",
            sodY is null ? "없음" : $"({string.Join(",", sodY.Key.Values)}) {sodY.Before!["OrderQty"]}->{sodY.After!["OrderQty"]}");
        // 트리거 대상 행 키: TransactionHistory 새 행(iduSalesOrderDetail), SOH 주문 z(SubTotal 재계산)
        var thNew = (await TestDb.RowsAsync(conn, $"SELECT TransactionID FROM {live}.Production.TransactionHistory EXCEPT SELECT TransactionID FROM {snap}.Production.TransactionHistory ORDER BY 1;"))
            .Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!).ToList();
        Check("TransactionHistory INSERTED 키(트리거)", string.Join(",", thNew), string.Join(",", Keys("Production.TransactionHistory", NetChangeKind.Inserted)));
        var sohZ = dmChanges.SingleOrDefault(c => c.Table == "Sales.SalesOrderHeader" && c.Change == NetChangeKind.Updated);
        Check("SOH UPDATED 키(트리거 대상 주문)", z, sohZ?.Key.Values[0] ?? "없음");
        Check("SOH UPDATED에 SubTotal 포함", true, sohZ?.ChangedColumns.Contains("SubTotal") == true);

        // 전 테이블: COUNT_BIG 증감 = INSERTED − DELETED, EXCEPT 행 수 = INSERTED(또는 DELETED) + UPDATED
        var mismatches = new List<string>();
        foreach (var t in tables)
        {
            var mine = inTran.Where(c => c.Table == t.DisplayName).ToList();
            long ins = mine.Count(c => c.Change == NetChangeKind.Inserted), del = mine.Count(c => c.Change == NetChangeKind.Deleted), upd = mine.Count(c => c.Change == NetChangeKind.Updated);
            var row = (await TestDb.RowsAsync(conn, IndependentCounts(t, live, snap)))[0];
            var (dCount, liveMinus, snapMinus) = (Convert.ToInt64(row[0]), Convert.ToInt64(row[1]), Convert.ToInt64(row[2]));
            if (dCount != ins - del || liveMinus != ins + upd || snapMinus != del + upd)
                mismatches.Add($"{t.DisplayName}: 독립(증감 {dCount}, live−snap {liveMinus}, snap−live {snapMinus}) 대 NetDiffer(I {ins}, D {del}, U {upd})");
        }
        Check($"전 테이블 {tables.Count}개 독립 건수(COUNT_BIG 증감, EXCEPT 양방향) 불일치 테이블 수", 0, mismatches.Count);

        Log("\n## 6. 독립 확인");
        foreach (var (item, e, a) in checks) Log($"  [{(e == a ? "MATCH" : "MISMATCH")}] {item}: 기대 {e} / 실제 {a}");
        foreach (var m in mismatches) Log($"    {m}");

        // 7. ROLLBACK 뒤(트랜잭션 없음 = 실패 경로와 같은 전체 비교) 시간과 차이
        await Sql.ExecAsync(conn, null, "ROLLBACK;");
        var (afterRollback, rbMs) = await TimeAsync(() => differ.DiffAsync(noTran, tb.Sandbox, tb.Snapshot, tables, default));
        Log($"\n## 7. ROLLBACK 뒤(@@TRANCOUNT {await Sql.ScalarAsync<int>(conn, null, "SELECT @@TRANCOUNT;")}) 전 테이블 NetDiffer(실패 경로와 같은 호출: 트랜잭션 없음)");
        LogTimes(rbMs);
        var rollbackSame = Sql.Dump(afterRollback) == Sql.Dump(afterUser);
        Log($"  롤백 뒤 차이 {afterRollback.Count}건, 사용자 생성 직후 상태와 같음: {rollbackSame} (DM이 남긴 차이 0)");

        // 8. 테이블별 시간(트랜잭션 없음, 1회) 상위 8개
        var perTable = new List<(string Table, long Ms)>();
        foreach (var t in tables)
        {
            var s = Stopwatch.StartNew();
            await differ.DiffAsync(noTran, tb.Sandbox, tb.Snapshot, [t], default);
            perTable.Add((t.DisplayName, s.ElapsedMilliseconds));
        }
        Log($"\n## 8. 테이블별 1회 시간 합계 {perTable.Sum(p => p.Ms)} ms, 상위 8개:");
        foreach (var (t, ms) in perTable.OrderByDescending(p => p.Ms).Take(8)) Log($"  {t} {ms} ms");

        Assert.True(checks.All(c => c.Expected == c.Actual), "SP7 독립 확인 불일치(로그 참조)");
        Assert.True(rollbackSame, "롤백 뒤 차이가 사용자 생성 직후 상태와 다름");
    }

    static async Task<long> Rows(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>워밍업 1회 뒤 5회. 결과가 매번 같은지도 확인한다.</summary>
    static async Task<(IReadOnlyList<NetChange> Result, List<long> Ms)> TimeAsync(Func<Task<IReadOnlyList<NetChange>>> run)
    {
        var s = Stopwatch.StartNew();
        var first = await run();
        var ms = new List<long> { s.ElapsedMilliseconds }; // [0] = 워밍업
        var dump = Sql.Dump(first);
        for (var i = 0; i < 5; i++)
        {
            s.Restart();
            var r = await run();
            ms.Add(s.ElapsedMilliseconds);
            Assert.Equal(dump, Sql.Dump(r));
        }
        return (first, ms);
    }

    void LogTimes(List<long> ms)
    {
        var runs = ms.Skip(1).Order().ToList();
        // 최근접 순위: p50 = 3번째, p95 = ceil(0.95·5) = 5번째(최댓값)
        Log($"  워밍업 {ms[0]} ms, 5회 {string.Join(", ", ms.Skip(1))} ms → p50 {runs[2]} ms, p95 {runs[4]} ms (최근접 순위, 5회라 p95 = 최댓값)");
    }

    static string Describe(NetChange c) => $"{c.Table} {Sql.Name(c.Change)} ({string.Join(",", c.Key.Values)})";

    /// <summary>
    /// NetDiffer와 다른 방식의 테이블별 건수: COUNT_BIG 증감, live EXCEPT snap 행 수, snap EXCEPT live 행 수.
    /// EXCEPT로 비교할 수 없는 형식은 문자열·바이트로 바꾼다(xml → nvarchar(max), CLR 형식 → varbinary(max)).
    /// </summary>
    static string IndependentCounts(TableInfo t, string live, string snap)
    {
        var cols = string.Join(", ", t.Columns.Select(c => c.TypeName.ToLowerInvariant() switch
        {
            "xml" => $"CONVERT(nvarchar(max), {SqlText.Quote(c.Name)})",
            "geography" or "geometry" or "hierarchyid" => $"CAST({SqlText.Quote(c.Name)} AS varbinary(max))",
            _ when c.IsUserClrType => $"CAST({SqlText.Quote(c.Name)} AS varbinary(max))",
            _ => SqlText.Quote(c.Name),
        } + $" AS [c{t.Columns.ToList().IndexOf(c)}]"));
        var l = $"{live}.{t.QuotedName}";
        var s = $"{snap}.{t.QuotedName}";
        return $"""
            SELECT (SELECT COUNT_BIG(*) FROM {l}) - (SELECT COUNT_BIG(*) FROM {s}),
                   (SELECT COUNT_BIG(*) FROM (SELECT {cols} FROM {l} EXCEPT SELECT {cols} FROM {s}) AS a),
                   (SELECT COUNT_BIG(*) FROM (SELECT {cols} FROM {s} EXCEPT SELECT {cols} FROM {l}) AS b);
            """;
    }
}
