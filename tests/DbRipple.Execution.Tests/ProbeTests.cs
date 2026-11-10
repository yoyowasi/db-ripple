using DbRipple.Core.Baseline;
using DbRipple.Execution.Tests.Support;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace DbRipple.Execution.Tests;

/// <summary>
/// 위험 6·7과 Q27의 드라이버 수준 실측(2026-11-10, SQL Server 2025 RTM-CU10, Microsoft.Data.SqlClient 7.1.1). 실행 봉투와 같은 SET 옵션(XACT_ABORT ON)과
/// dbr_preview_user 가장으로 문장 하나를 BEGIN TRAN 안에서 실행하고, 오류 필드와 트랜잭션 상태, REVERT 결과를 확인한다. TRY/CATCH가 없는 ad hoc 명령이다.
/// <para>실측 결과(이 테스트가 단언한다)</para>
/// <list type="bullet">
/// <item>위험 7: SqlError.Procedure는 스키마 없는 트리거 이름이다(예 "dbr_audit_Calc_U", "trg_Blk_rb"). 문장 수준 오류(547)와 3609·3621은 빈 문자열이다. 트리거 안 오류의 LineNumber는 트리거 본문 기준이다.</item>
/// <item>위험 6: XACT_ABORT ON에서는 문장 오류, 트리거 안 엔진 오류(R18 8115 포함), THROW, 트리거 ROLLBACK 모두 트랜잭션을 이미 롤백해 @@TRANCOUNT = 0이다. doomed(−1)로 남지 않았다.
/// 그 직후 XACT_STATE()는 1로 읽혔다(@@TRANCOUNT 0과 모순). 그래서 실행기는 @@TRANCOUNT를 먼저 본다. REVERT는 성공해 가장이 풀렸다(SP10의 3930은 TRY/CATCH 안 doomed 경로).</item>
/// <item>RAISERROR만 하는 트리거(AFTER·INSTEAD OF)는 XACT_ABORT와 무관하게 트랜잭션이 살아 있다(@@TRANCOUNT 1, XACT_STATE 1).</item>
/// <item>트리거 ROLLBACK 뒤 트리거 본문의 INSERT(ErrLog)는 자동 커밋으로 남는다(롤백 뒤 남은 변경, E13).</item>
/// </list>
/// </summary>
[Collection(DbCollection.Name)]
public class ProbeTests(ITestOutputHelper output)
{
    sealed record Expect(string Sql, int[] Numbers, string?[] Procedures, int TranCountAfter, int ErrLogAfter);

    static readonly Expect[] Cases =
    [
        new("DELETE FROM dbo.Parent WHERE id = 1;", [547], [null], 0, 0),
        new("UPDATE dbo.Calc SET a = a WHERE id = 2;", [8115, 3621], ["dbr_audit_Calc_U", null], 0, 0),
        new("UPDATE dbo.Blk SET v = 1 WHERE id = 1;", [50000, 3609], ["trg_Blk_rb", null], 0, 0),
        new("UPDATE dbo.Blk SET v = 2 WHERE id = 1;", [50001], ["trg_Blk_throw"], 0, 0),
        new("UPDATE dbo.Blk SET v = 3 WHERE id = 1;", [50000], ["trg_Blk_raise"], 1, 0),
        new("UPDATE dbo.Blk SET v = 5 WHERE id = 1;", [547, 3621], ["trg_Blk_engine", null], 0, 0),
        new("DELETE FROM dbo.Ins WHERE id = 1;", [50000], ["trg_Ins_iod"], 1, 0),
        new("UPDATE dbo.Blk SET v = 4 WHERE id = 1;", [3609], [null], 0, 1), // 마지막: ErrLog에 확정 행을 남긴다
    ];

    [SqlFact]
    public async Task Xact_abort_errors_roll_back_and_revert_succeeds()
    {
        await using var b = await ExeBaseline.CustomAsync("probe", Q27Schema.Batches);
        await using var conn = await TestDb.OpenAsync(b.Sandbox);
        Assert.Equal(21816, await Scalar(conn, PreviewExecutor.EnvelopeSql)); // SqlClient 기본 5432 + XACT_ABORT 16384
        await Exec(conn, PreviewUserSql.EnsureBatch(["dbo"]));
        foreach (var c in Cases)
        {
            await Exec(conn, "BEGIN TRAN;");
            await Exec(conn, $"EXECUTE AS USER = N'{PreviewUserSql.UserName}';");
            var ex = await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var cmd = new SqlCommand(c.Sql, conn);
                await cmd.ExecuteNonQueryAsync();
            });
            var errors = ex.Errors.Cast<SqlError>().ToList();
            foreach (var e in errors) output.WriteLine($"{c.Sql} → {e.Number} class={e.Class} line={e.LineNumber} procedure='{e.Procedure}' {e.Message}");
            Assert.Equal(c.Numbers, errors.Select(e => e.Number));
            Assert.Equal(c.Procedures, errors.Select(e => string.IsNullOrEmpty(e.Procedure) ? null : e.Procedure));

            var (xact, tran, user) = await State(conn);
            output.WriteLine($"   after: XACT_STATE={xact} @@TRANCOUNT={tran} USER={user}");
            Assert.Equal(c.TranCountAfter, tran);
            Assert.Equal(1, xact); // 실측: @@TRANCOUNT 0이어도 1로 읽힌다(위 설명)
            Assert.Equal(PreviewUserSql.UserName, user);
            await Exec(conn, "REVERT;");
            Assert.Equal("dbo", (await State(conn)).User);
            await Exec(conn, "IF @@TRANCOUNT > 0 ROLLBACK;");
            Assert.Equal(c.ErrLogAfter, await Scalar(conn, "SELECT COUNT(*) FROM dbo.ErrLog;"));
        }
    }

    static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<int> Scalar(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    static async Task<(int Xact, int Tran, string User)> State(SqlConnection c)
    {
        await using var cmd = new SqlCommand("SELECT XACT_STATE(), @@TRANCOUNT, USER_NAME();", c);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (Convert.ToInt32(r.GetValue(0)), r.GetInt32(1), r.GetString(2));
    }
}
