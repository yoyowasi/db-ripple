using DbRipple.Core.Baseline;
using DbRipple.Contracts;
using DbRipple.Execution.Tests.Support;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Execution.Tests;

/// <summary>C10 + C11 CascadeLab 통합(dbrt_exe_cl_·dbrt_exe_clrt_ 전용 기준선, dbr_preview_user 가장, 가짜 EXCEPT differ).</summary>
[Collection(DbCollection.Name)]
public class CascadeLabTests(LabFixture lab)
{
    public static readonly string[] CaseIds = ["CL01", "CL02", "CL03", "CL03b", "CL04", "CL04b", "CL05", "CL06", "CL07", "CL08a", "CL08b"];

    /// <summary>
    /// 손 정답 pairing과 다르게 내는 묶음(C11 결정: 키가 그대로인 UPDATE는 행 수와 상관없이 BY_PK, SINGLE_ROW는 키가 바뀐 한 행 묶음).
    /// 손 정답 안에서도 같은 모양(한 행, PK 그대로)이 CL07 A·CL04b Ch는 BY_PK, 아래 묶음은 SINGLE_ROW로 엇갈린다. 정정 기록은 오케스트레이터에 요청했다.
    /// </summary>
    static readonly Dictionary<(string Case, string Node), string> PairingOverrides = new()
    {
        [("CL07", "F:cl07.D:UPDATE")] = "BY_PK",
        [("CL08a", "F:cl08.Acct:UPDATE#1")] = "BY_PK",
        [("CL08b", "F:cl08.Acct:UPDATE#1")] = "BY_PK",
        [("CL08b", "F:cl08.Acct:UPDATE#2")] = "BY_PK",
        [("CL08b", "F:cl08.Acct:UPDATE#3")] = "BY_PK",
        [("CL08b", "F:cl08.Acct:UPDATE#4")] = "BY_PK",
    };

    static ExecutionSettings Settings(bool eval = false) => new() { ExecuteAsUser = PreviewUserSql.UserName, EvaluationMode = eval };

    async Task<(RawCapture Raw, CollectResult Collected, AnalysisResult Analysis, BaselineContext Ctx, ExceptNetDiffer Differ)> RunAsync(LabCase c, PreviewOptions? options = null)
    {
        var (b, ctx) = lab.For(c);
        var analysis = Stmts.Sql([.. c.Statements]);
        var differ = ExceptNetDiffer.Create();
        var raw = await new PreviewExecutor(TestDb.ConnectionString(), Settings(), differ).ExecuteAsync(analysis, ctx, options ?? new PreviewOptions(), null, default);
        Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b)); // 실행 뒤 샌드박스 = 스냅샷
        return (raw, new ChangeCollector().Collect(raw, analysis, ctx), analysis, ctx, differ);
    }

    [SqlFact]
    public async Task Headers_match_week1_audit_log_and_firings_match_hand_answers()
    {
        foreach (var id in CaseIds)
        {
            var c = LabCase.Get(id);
            var (raw, collected, analysis, ctx, differ) = await RunAsync(c);

            var (status, expectedHeaders) = AuditLog.Get(id);
            Assert.Equal(status, Status(raw.Status));
            Assert.Equal(c.Status, Status(raw.Status));
            Assert.Equal(expectedHeaders, AuditLog.From(raw, ctx));
            Assert.False(raw.SandboxDirty);
            Assert.Empty(raw.ResidualCommitted);

            if (c.ErrorNumber is int n)
            {
                var failed = Assert.Single(raw.Executions, e => e.Result.Status != StatementStatus.Ok);
                Assert.Equal(n, failed.Result.Error!.ErrorNumber);
                Assert.False(failed.Result.Error.IsToolFault);
                Assert.Empty(raw.Headers);
                Assert.Empty(raw.NetChanges);
                Assert.Equal(0, Assert.Single(differ.Calls).TranCount); // 롤백 뒤 전체 테이블 비교
            }
            else
            {
                Assert.All(raw.Executions, e => Assert.Equal(StatementStatus.Ok, e.Result.Status));
                Assert.Equal(1, Assert.Single(differ.Calls).TranCount); // 열린 트랜잭션 안
                Assert.Equal(raw.Headers.Select(h => h.TableObjectId).Distinct().Count(), differ.Calls[0].Tables.Count); // 감사에 나온 테이블(제외 테이블 0)
            }

            // C11: 손 정답 firings(테이블·연산·rowCount·depth·pairing)
            var actual = collected.Firings.Select(f => (f.Table, Op: f.Operation.ToString().ToUpperInvariant(), f.RowCount, f.Depth)).Order().ToList();
            var expected = c.Firings.Select(f => (f.Table, Op: f.Operation, f.RowCount, f.Depth)).Order().ToList();
            Assert.Equal(expected, actual);
            foreach (var ef in c.Firings.Where(f => f.Pairing is not null))
            {
                var want = PairingOverrides.GetValueOrDefault((id, ef.Node), ef.Pairing!);
                var firing = collected.Firings.Single(f => f.Table == ef.Table && f.Depth == ef.Depth && f.Operation.ToString().ToUpperInvariant() == ef.Operation);
                var pairings = collected.RowChanges.Where(r => r.FiringId == firing.Id).Select(r => Pair(r.Pairing)).Distinct().ToList();
                Assert.True(pairings.SequenceEqual([want]), $"{id} {ef.Node}: 기대 {want}, 실제 {string.Join(",", pairings)}");
            }
            Assert.All(collected.Firings, f => Assert.Equal(1, f.StatementIndex));
            Assert.Equal(collected.Firings.Sum(f => f.Operation == ChangeOperation.Update && collected.RowChanges.Any(r => r.FiringId == f.Id && r.Pairing == Pairing.Unpaired)
                ? f.RowCount * 2 : f.RowCount), collected.RowChanges.Count);
            // 재조회 차이 없음(CL08a·b도 전위 순서 마지막 묶음 = 최종 after)
            Assert.Empty(collected.RereadDifferences);
            Assert.All(collected.RowChanges, r => Assert.Equal(FinalAfterSource.Audit, r.FinalAfterSource));

            await FixtureFiles.WriteAsync(id, raw, collected, analysis, ctx, c.Database);
        }
    }

    [SqlFact]
    public async Task CL08_final_after_is_last_in_preorder_not_max_capture_seq()
    {
        foreach (var id in new[] { "CL08a", "CL08b" })
        {
            var (raw, collected, _, _, _) = await RunAsync(LabCase.Get(id));
            var maxSeq = collected.Firings.MaxBy(f => f.CaptureSeq)!;
            Assert.Equal(1, maxSeq.Depth); // 바깥 문장 묶음이 가장 늦게 기록됨(후위 기록)
            Assert.Equal("0", collected.RowChanges.Single(r => r.FiringId == maxSeq.Id).After!["touched"]);
            var deepest = collected.Firings.MaxBy(f => f.Depth)!;
            var reread = Assert.Single(raw.Rereads, r => r.Table == "cl08.Acct" && r.KeyJson == "{\"id\":1}");
            Assert.Equal(ChangeCollector.ParseRow(reread.RowJson!)["touched"], collected.RowChanges.Single(r => r.FiringId == deepest.Id).After!["touched"]);
            Assert.Empty(collected.RereadDifferences);
            // 괄호: 깊이 d+1 묶음마다 부모 1개(깊이 d), 상한 inferred
            foreach (var f in collected.Firings.Where(f => f.Depth >= 2))
            {
                var link = Assert.Single(collected.ContextLinks, l => l.ChildFiringId == f.Id);
                var parent = collected.Firings.Single(p => p.Id == Assert.Single(link.CandidateParentFiringIds));
                Assert.Equal(f.Depth - 1, parent.Depth);
                Assert.True(parent.CaptureSeq > f.CaptureSeq);
                Assert.Equal(Confidence.Inferred, link.ConfidenceCap);
            }
        }
    }

    [SqlFact]
    public async Task Cap_one_switches_to_keys_and_truncates()
    {
        var (b, ctx) = lab.For(LabCase.Get("CL01"));
        var analysis = Stmts.Sql("DELETE FROM cl01.A WHERE id = 1;", "DELETE FROM cl02.Dept WHERE id = 1;");
        var raw = await new PreviewExecutor(TestDb.ConnectionString(), Settings(), new ExceptNetDiffer())
            .ExecuteAsync(analysis, ctx, new PreviewOptions { ImageCap = 1 }, null, default);
        Assert.Equal(PreviewStatus.Truncated, raw.Status);
        Assert.True(raw.Capture.Truncated);
        Assert.Equal(1, raw.Capture.Cap);
        Assert.Equal(ImageMode.Keys, raw.Executions[0].ModeAfter);
        Assert.Equal(9, raw.Executions[0].ImageCountAfter); // 첫 문장 이미지 9개가 모두 수집된 뒤에 안다(D8)
        var second = raw.Headers.Where(h => h.CaptureSeq > raw.Executions[0].Boundary).ToList();
        Assert.NotEmpty(second);
        Assert.All(second, h => Assert.Equal(ImageMode.Keys, h.ImageMode));
        Assert.All(raw.Images.Where(i => second.Any(h => h.CaptureSeq == i.CaptureSeq)), i => { Assert.Null(i.RowJson); Assert.NotNull(i.KeyJson); });
        Assert.Equal(9, raw.Capture.ImageCount);

        var collected = new ChangeCollector().Collect(raw, analysis, ctx);
        var keysRows = collected.RowChanges.Where(r => second.Any(h => ContractIds.Firing(h.CaptureSeq) == r.FiringId)).ToList();
        Assert.Equal(3, keysRows.Count); // Dept 1 + Emp 2
        Assert.All(keysRows, r => { Assert.Null(r.Before); Assert.Null(r.After); Assert.NotEmpty(r.Key.Values); });
        Assert.All(keysRows.Where(r => r.Operation == ChangeOperation.Update), r => Assert.Equal(Pairing.ByPk, r.Pairing));
        Assert.Equal(2, keysRows.Count(r => r.Operation == ChangeOperation.Update));
        Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));
    }

    [SqlFact]
    public async Task No_action_violation_fails_and_later_statements_are_not_run()
    {
        var (b, ctx) = lab.For(LabCase.Get("CL01"));
        var analysis = Stmts.Sql("DELETE FROM cl01.A WHERE id = 1;", "DELETE FROM cl04.P WHERE id = 1;", "DELETE FROM cl02.Dept WHERE id = 1;");
        var differ = new ExceptNetDiffer();
        var raw = await new PreviewExecutor(TestDb.ConnectionString(), Settings(), differ).ExecuteAsync(analysis, ctx, new PreviewOptions(), null, default);
        Assert.Equal(PreviewStatus.Failed, raw.Status);
        Assert.Equal([StatementStatus.Ok, StatementStatus.Failed, StatementStatus.NotRun], raw.Executions.Select(e => e.Result.Status));
        var err = raw.Executions[1].Result.Error!;
        Assert.Equal(547, err.ErrorNumber);
        Assert.Null(err.Procedure);
        Assert.False(err.IsToolFault);
        Assert.Contains("FK_cl04_Ch_P", err.Message);
        Assert.Null(raw.Executions[2].Boundary);
        Assert.Null(raw.Executions[2].Result.RowsAffected);
        Assert.Empty(raw.Headers);
        Assert.Empty(raw.NetChanges);
        Assert.Empty(raw.ResidualCommitted);
        Assert.False(raw.SandboxDirty);
        var call = Assert.Single(differ.Calls);
        Assert.Equal(0, call.TranCount);
        Assert.Equal(ctx.Catalog.Tables.Count(t => t.Schema != "audit"), call.Tables.Count); // 전체 사용자 테이블
        Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));
    }

    [SqlFact]
    public async Task Evaluation_mode_calls_hook_before_rollback_with_open_transaction()
    {
        var c = LabCase.Get("CL06");
        var (b, ctx) = lab.For(c);
        var hook = new RecordingHook();
        var differ = new ExceptNetDiffer();
        var raw = await new PreviewExecutor(TestDb.ConnectionString(), Settings(eval: true), differ).ExecuteAsync(Stmts.Sql([.. c.Statements]), ctx, new PreviewOptions(), hook, default);
        Assert.Equal(PreviewStatus.Ok, raw.Status);
        var seen = Assert.Single(hook.Seen);
        Assert.Equal((1, "dbo", 21816), (seen.TranCount, seen.User, seen.Options)); // 트랜잭션 안, REVERT 뒤 도구 계정, 실행 봉투 옵션
        Assert.Equal(2, seen.LineLogRows); // 미커밋 트리거 INSERT 2행이 보인다(시드 0행)
        Assert.Equal(ctx.SnapshotDb, seen.Context.SnapshotDb);
        Assert.Equal(ctx.Manifest.Notes, seen.Context.ManifestNotes);
        Assert.Single(seen.Context.Statements);
        Assert.Equal(ctx.Catalog.Tables.Count(t => t.Schema != "audit"), Assert.Single(differ.Calls).Tables.Count); // 평가 모드: 전체 테이블
        Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));

        // 실패하면 부르지 않는다(Q13)
        hook.Seen.Clear();
        var failed = await new PreviewExecutor(TestDb.ConnectionString(), Settings(eval: true), new ExceptNetDiffer())
            .ExecuteAsync(Stmts.Sql("DELETE FROM cl04.P WHERE id = 1;"), ctx, new PreviewOptions(), hook, default);
        Assert.Equal(PreviewStatus.Failed, failed.Status);
        Assert.Empty(hook.Seen);

        // 훅 예외는 그대로 올라오고 ROLLBACK은 된다
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PreviewExecutor(TestDb.ConnectionString(), Settings(eval: true), new ExceptNetDiffer())
            .ExecuteAsync(Stmts.Sql([.. c.Statements]), ctx, new PreviewOptions(), new ThrowingHook(), default));
        Assert.Equal(0, await Stmts.SandboxDiffRowsAsync(b));
    }

    [SqlFact]
    public async Task Preview_user_is_dml_only_and_ensure_is_idempotent()
    {
        var (b, ctx) = lab.For(LabCase.Get("CL01"));
        await new PreviewExecutor(TestDb.ConnectionString(), Settings(), new ExceptNetDiffer()).ExecuteAsync(Stmts.Sql("DELETE FROM cl01.A WHERE id = 1;"), ctx, new PreviewOptions(), null, default);
        await using var conn = await TestDb.OpenAsync(b.Sandbox);
        var rows = await TestDb.RowsAsync(conn, $"""
            SELECT dp.type, dp.default_schema_name, dp.authentication_type_desc,
                   (SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id = dp.principal_id),
                   (SELECT COUNT(*) FROM sys.database_permissions p WHERE p.grantee_principal_id = dp.principal_id AND p.class = 3 AND p.major_id = SCHEMA_ID(N'audit')),
                   (SELECT COUNT(*) FROM sys.database_permissions p WHERE p.grantee_principal_id = dp.principal_id AND p.class = 3 AND p.permission_name NOT IN (N'SELECT', N'INSERT', N'UPDATE', N'DELETE')),
                   (SELECT COUNT(*) FROM sys.database_permissions p WHERE p.grantee_principal_id = dp.principal_id AND p.class <> 3 AND p.permission_name <> N'CONNECT')
            FROM sys.database_principals AS dp WHERE dp.name = N'{PreviewUserSql.UserName}';
            """);
        var r = Assert.Single(rows);
        Assert.Equal("S", ((string)r[0]!).Trim());
        Assert.Equal("dbo", r[1]);
        Assert.Equal("NONE", r[2]); // WITHOUT LOGIN
        Assert.Equal([0, 0, 0, 0], r[3..].Select(x => Convert.ToInt32(x)));
        await using var cmd = new SqlCommand(PreviewUserSql.EnsureBatch(ctx.Catalog.Tables.Select(t => t.Schema)), conn);
        Assert.False((bool)(await cmd.ExecuteScalarAsync())!); // 두 번째부터 DDL 없음
    }

    static string Status(PreviewStatus s) => s switch
    {
        PreviewStatus.Ok => "ok",
        PreviewStatus.Failed => "failed",
        PreviewStatus.TriggerBlocked => "trigger_blocked",
        _ => "truncated",
    };

    static string Pair(Pairing p) => p switch
    {
        Pairing.ByPk => "BY_PK",
        Pairing.ByUnique => "BY_UNIQUE",
        Pairing.SingleRow => "SINGLE_ROW",
        _ => "UNPAIRED",
    };

    sealed class RecordingHook : IEvaluationHook
    {
        public List<(EvaluationHookContext Context, int TranCount, string User, int Options, int LineLogRows)> Seen { get; } = [];

        public async Task OnBeforeRollbackAsync(EvaluationHookContext context, CancellationToken ct)
        {
            await using var cmd = context.Session.CreateCommand(
                $"SELECT @@TRANCOUNT, USER_NAME(), @@OPTIONS, (SELECT COUNT(*) FROM [{context.SandboxDb}].cl06.LineLog);");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            Seen.Add((context, r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        }
    }

    sealed class ThrowingHook : IEvaluationHook
    {
        public Task OnBeforeRollbackAsync(EvaluationHookContext context, CancellationToken ct) => throw new InvalidOperationException("훅 실패");
    }
}
