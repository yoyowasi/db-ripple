using DbRipple.Contracts;
using DbRipple.Eval;
using DbRipple.TestSupport;

namespace DbRipple.Eval.Tests;

/// <summary>
/// 평가 훅: 전용 CascadeLab 기준선(dbrt_eval_cl*, 감사 장치 설치 샌드박스 + 기준 스냅샷)에서 실행기처럼 BEGIN TRAN → 문장 → 훅 → ROLLBACK을 한다.
/// 훅의 샌드박스 순 변경이 1주차 오라클 기록과 같고, 감사 스키마(같은 트랜잭션에서 실제로 쓰임)만 빠지고 notes에 나온 테이블은 빠지지 않는지 본다.
/// </summary>
public class HookTests
{
    [SqlFact]
    public async Task Hook_records_sandbox_net_change_without_audit_schema()
    {
        await using var tb = await TestBaseline.BuildCascadeLabAsync("eval");
        var hc = HandCase.Load(Path.Combine(RepoPaths.CascadeLab, "cases", "CL07-trigger-chain-3.json"));
        var log = OracleLog.LoadCases(Path.Combine(RepoPaths.CascadeLab, "verification", "logs", "oracle-2026-11-10.json"));
        var cmp = new HashSnapshotComparer();

        await using (var conn = await TestDb.OpenAsync(tb.Sandbox))
        {
            var s = new SqlSession { Connection = conn };
            await Exec(s, "BEGIN TRANSACTION;");
            try
            {
                foreach (var sql in hc.Statements) await Exec(s, sql);
                Assert.True(await Scalar(s, "SELECT COUNT_BIG(*) FROM audit.AuditHeader;") > 0); // 감사 기록이 실제로 같은 트랜잭션에 있음

                var hook = new EvaluationHook(cmp);
                var ctx = new EvaluationHookContext { Session = s, SandboxDb = tb.Sandbox, SnapshotDb = tb.Snapshot, Statements = [] };
                await hook.OnBeforeRollbackAsync(ctx, default);
                Assert.Equal(1, hook.Calls);
                Assert.Empty(hook.LastIssues);
                Assert.DoesNotContain(hook.LastSandboxChanges!, c => c.Table.StartsWith("audit.", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(hook.LastExcluded, e => e.StartsWith("audit.", StringComparison.OrdinalIgnoreCase));
                var asOracle = new OracleResult(tb.Sandbox, hook.LastTables, hook.LastSandboxChanges!, [new OracleStatement(1, "ok", 1, [])], [], false, 0, "", true, []);
                Assert.Empty(OracleLog.Diff(log[hc.Id], asOracle));
                Assert.Empty(hc.DiffAgainst(hook.LastSandboxChanges!));

                // 감사 스키마를 빼지 않으면 감사 테이블 변경이 보인다(제외가 실제로 효과가 있음)
                var (withAudit, _) = await cmp.LoadTablesAsync(s, tb.Sandbox, [], [], default);
                var raw = cmp.Compare(await cmp.TakeAsync(s, tb.Snapshot, withAudit, default), await cmp.TakeAsync(s, tb.Sandbox, withAudit, default));
                Assert.Contains(raw.Changes, c => c.Table.StartsWith("audit.", StringComparison.OrdinalIgnoreCase));

                // 매니페스트 notes에 나온 테이블도 빼지 않는다(감사 스키마만 뺀다, 2026-11-10 검토)
                var noted = new EvaluationHook(cmp);
                await noted.OnBeforeRollbackAsync(ctx with { ManifestNotes = ["설치 DDL을 cl07.D에 기록함(시험용 문장)"] }, default);
                Assert.Contains(noted.LastSandboxChanges!, c => c.Table == "cl07.D");
                Assert.Equal(hook.LastSandboxChanges!.Count, noted.LastSandboxChanges!.Count);
            }
            finally
            {
                await Exec(s, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            }

            // 롤백 뒤 샌드박스 = 스냅샷(감사 스키마 제외)
            var (tables, _) = await cmp.LoadTablesAsync(s, tb.Sandbox, ["audit"], [], default);
            Assert.Empty(cmp.Compare(await cmp.TakeAsync(s, tb.Snapshot, tables, default), await cmp.TakeAsync(s, tb.Sandbox, tables, default)).Changes);
        }
    }

    static async Task Exec(SqlSession s, string sql)
    {
        await using var cmd = s.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<long> Scalar(SqlSession s, string sql)
    {
        await using var cmd = s.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
