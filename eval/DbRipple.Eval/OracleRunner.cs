using System.Diagnostics;
using DbRipple.Contracts;
using Microsoft.Data.SqlClient;

namespace DbRipple.Eval;

/// <summary>
/// 오라클 실행(§10.3): 수집 장치 없는 사본(원본 역할 DB)에서 같은 문장을 트랜잭션 안에서 실행하고, 해시 비교로 순 변경을 구한 뒤 ROLLBACK한다.
/// 변수 없는 단순 배치는 제품 분석기(C6)를 거치지 않은 원문 배치 경로(rawBatch = true)로도 대조한다(문장 분리 결함이 정답과 결과에 함께 들어가는 것을 막는다).
/// 세션 옵션은 SqlClient 기본값에 XACT_ABORT ON을 더한 제품 실행 봉투(C10)와 같은 조건이다(2026-11-10 검토: 1주차 oracle-2026-11-10.json은 XACT_ABORT OFF).
/// 그래서 오류가 나면 트랜잭션 전체가 롤백되고, after는 롤백 뒤 상태(자동 커밋으로 남은 변경만)다. 첫 실패 문장 뒤 문장은 실행하지 않는다(not_run).
/// 인계 4(2026-11-11): RAISERROR만 하는 트리거와 컴파일 오류는 XACT_ABORT ON에서도 트랜잭션을 살려 둔다. 그래서 첫 실패에서 바로
/// IF @@TRANCOUNT > 0 ROLLBACK을 하고 after를 읽는다. 실패 사례의 오라클 after는 언제나 롤백 뒤 상태다(제품 미리보기의 residualCommitted와 같은 뜻).
/// 롤백 뒤에도 남는 변경(트리거가 트랜잭션 밖에서 확정한 행, CL12a)은 원본에 남으므로 RestoredAfterRollback이 false가 된다(손 정답 leavesResidue).
/// </summary>
public sealed class OracleRunner(HashSnapshotComparer comparer)
{
    /// <summary>비교기.</summary>
    public HashSnapshotComparer Comparer { get; } = comparer;

    /// <summary>문장별 CommandTimeout(초).</summary>
    public int CommandTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// 문장을 하나씩 실행(rawBatch = false) 또는 줄바꿈으로 이은 원문 배치 하나로 실행(rawBatch = true)한다.
    /// connectionString은 서버 연결 문자열(값을 로그·예외에 넣지 않는다). 연결은 풀링을 끄고 database로 연다.
    /// </summary>
    public async Task<OracleResult> RunAsync(string connectionString, string database, IReadOnlyList<string> statements, bool rawBatch, CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(connectionString) { Pooling = false, InitialCatalog = database };
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        var session = new SqlSession { Connection = conn };

        await ExecAsync(session, "SET XACT_ABORT ON;", ct); // 제품 실행 봉투와 같게(E20)
        await ExecAsync(session, "BEGIN TRANSACTION;", ct);
        var results = new List<OracleStatement>();
        var errors = new List<int>();
        IReadOnlyList<OracleTable> tables;
        RowHashSnapshot before, after;
        long executeMs;
        try
        {
            // 원본 역할 DB에는 감사 스키마가 없지만 같은 규칙으로 뺀다.
            (tables, _) = await Comparer.LoadTablesAsync(session, database, ["audit"], [], ct);
            before = await Comparer.TakeAsync(session, database, tables, ct);

            var batches = rawBatch
                ? new List<(int Index, string Sql)> { (0, string.Join("\r\n", statements)) }
                : statements.Select((s, i) => (i + 1, s)).ToList();
            var stop = false;
            var sw = Stopwatch.StartNew();
            foreach (var (index, sql) in batches)
            {
                if (stop) { results.Add(new OracleStatement(index, "not_run", null, [])); continue; }
                try
                {
                    await using var cmd = session.CreateCommand(sql, CommandTimeoutSeconds);
                    var n = await cmd.ExecuteNonQueryAsync(ct);
                    results.Add(new OracleStatement(index, "ok", n, []));
                }
                catch (SqlException ex)
                {
                    var nums = ex.Errors.Cast<SqlError>().Select(e => e.Number).ToList();
                    errors.AddRange(nums);
                    // 3609 = 트리거 안에서 트랜잭션이 끝남(A22)
                    results.Add(new OracleStatement(index, nums.Contains(3609) ? "trigger_blocked" : "failed", null, nums));
                    stop = true;
                    // 인계 4: 살아 있는 트랜잭션도 여기서 끝낸다(after = 롤백 뒤 상태).
                    await ExecAsync(session, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", CancellationToken.None);
                }
            }
            executeMs = sw.ElapsedMilliseconds;
            after = await Comparer.TakeAsync(session, database, tables, ct);
        }
        finally
        {
            await ExecAsync(session, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", CancellationToken.None);
        }

        var restored = await Comparer.TakeAsync(session, database, tables, ct);
        var cmp = Comparer.Compare(before, after);
        var beforeDigest = HashSnapshotComparer.Digest(before);
        return new OracleResult(database, tables, cmp.Changes, results, errors, rawBatch, executeMs,
            beforeDigest, HashSnapshotComparer.Digest(restored) == beforeDigest, cmp.Issues) { Notes = cmp.Notes };
    }

    /// <summary>
    /// M5 오라클 execute 시간만 잰다(해시 스냅샷 없음): XACT_ABORT ON, BEGIN TRAN, 문장 실행(스톱워치), ROLLBACK. 실패해도 시간은 돌려주고 롤백한다.
    /// 큰 N에서 RunAsync의 세 번 스냅샷을 피하려는 것이다. 수집 오버헤드 = 샌드박스 execute ÷ 이 값(§10.4).
    /// </summary>
    public async Task<(long ExecuteMs, IReadOnlyList<int> ErrorNumbers)> TimeAsync(string connectionString, string database, IReadOnlyList<string> statements, CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(connectionString) { Pooling = false, InitialCatalog = database };
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        var session = new SqlSession { Connection = conn };
        await ExecAsync(session, "SET XACT_ABORT ON;", ct);
        await ExecAsync(session, "BEGIN TRANSACTION;", ct);
        var errors = new List<int>();
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var sql in statements)
            {
                await using var cmd = session.CreateCommand(sql, CommandTimeoutSeconds);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (SqlException ex)
        {
            errors.AddRange(ex.Errors.Cast<SqlError>().Select(e => e.Number));
        }
        finally
        {
            sw.Stop();
            await ExecAsync(session, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", CancellationToken.None);
        }
        return (sw.ElapsedMilliseconds, errors);
    }

    static async Task ExecAsync(SqlSession s, string sql, CancellationToken ct)
    {
        await using var cmd = s.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
