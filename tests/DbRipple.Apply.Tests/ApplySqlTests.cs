#if APPLY_INTEGRATION
// 실제 구현(Analysis·Execution·Diff·Attribution·Preview)이 필요한 DB 테스트. 병렬 구현 라운드에는 남의 프로젝트를 컴파일하지 않으려고
// 기본 빌드에서 빼 둔다. 테스트 라운드 실행: dotnet test tests\DbRipple.Apply.Tests -p:ApplyIntegration=true
// 기준선은 자기 dbrt_apl_ DB에서만 만든다(위험 5: [적용]은 원본에 COMMIT한다. 공유 AW_Origin·CascadeLab에는 쓰지 않는다).
using DbRipple.Analysis;
using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Diff;
using DbRipple.Execution;
using DbRipple.Preview;
using DbRipple.TestSupport;

namespace DbRipple.Apply.Tests;

/// <summary>dbrt_apl_ 기준선 한 벌 + 실제 미리보기 파이프라인 + 적용 서비스.</summary>
sealed class ApplyLab : IAsyncDisposable
{
    readonly TestDatabases cleanup;
    readonly string dir;

    ApplyLab(string origin, TestDatabases cleanup, string dir, SandboxManager manager, PreviewPipeline pipeline, ApplyService service)
    {
        Origin = origin; this.cleanup = cleanup; this.dir = dir; Manager = manager; Pipeline = pipeline; Service = service;
    }

    public string Origin { get; }
    public SandboxManager Manager { get; }
    public PreviewPipeline Pipeline { get; }
    public ApplyService Service { get; }

    public static Task<ApplyLab> CascadeLabAsync(ApplySettings? settings = null) =>
        CreateAsync("cl", origin => CascadeLabSeed.CreateAsync(origin, CascadeLabVariant.Default), settings);

    public static Task<ApplyLab> CustomAsync(string tag, string[] batches, ApplySettings? settings = null) =>
        CreateAsync(tag, async origin =>
        {
            await TestDb.ExecAsync("master", $"CREATE DATABASE [{origin}];");
            await TestDb.ExecAsync(origin, batches);
        }, settings);

    static async Task<ApplyLab> CreateAsync(string tag, Func<string, Task> seed, ApplySettings? settings)
    {
        var origin = TestDb.NewName("apl", tag);
        settings ??= new ApplySettings { ExecuteAsUser = PreviewUserSql.UserName };
        // 원본 스냅샷(origin_pre)은 서비스가 finally에서 지우지만, 실패로 남으면 여기서 지운다(스냅샷 먼저).
        var cleanup = new TestDatabases(origin + settings.OriginSnapshotSuffix, origin + "_base", origin + "_sbx", origin);
        var backup = TestDb.BackupPath(origin);
        cleanup.ContainerFiles.Add(backup);
        var dir = Path.Combine(Path.GetTempPath(), "dbripple-apl", origin);
        try
        {
            await seed(origin);
            // Q30: 원본 준비 단계에서 실행 사용자를 만들어 둔다(테스트 원본이므로 여기서. 적용 서비스는 만들지 않는다).
            await TestDb.ExecAsync(origin, PreviewUserSql.EnsureBatch(await SchemasAsync(origin)));
            var manager = new SandboxManager(new SandboxManagerOptions
            {
                ConnectionString = TestDb.ConnectionString(),
                OriginDb = origin,
                SandboxDb = origin + "_sbx",
                SnapshotDb = origin + "_base",
                BackupPath = backup,
                ManifestPath = Path.Combine(dir, "manifest.json"),
            }, new TriggerAnalyzer(), TimeProvider.System);
            await manager.RefreshAsync(default);
            var executor = new PreviewExecutor(TestDb.ConnectionString(), new ExecutionSettings { ExecuteAsUser = PreviewUserSql.UserName }, new NetDiffer());
            var store = new PreviewStore(Path.Combine(dir, "previews"), TimeProvider.System);
            var pipeline = new PreviewPipeline(manager, new BatchAnalyzer(), executor, new ChangeCollector(), new CausalResolver(), store, TimeProvider.System);
            var service = new ApplyService(TestDb.ConnectionString(), settings, store, manager, new BatchAnalyzer(), new StatementRunner(), new NetDiffer(), TimeProvider.System);
            return new ApplyLab(origin, cleanup, dir, manager, pipeline, service);
        }
        catch
        {
            await cleanup.DisposeAsync();
            throw;
        }
    }

    static async Task<string[]> SchemasAsync(string db)
    {
        await using var conn = await TestDb.OpenAsync(db);
        var rows = await TestDb.RowsAsync(conn, "SELECT DISTINCT s.name FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0;");
        return rows.Select(r => (string)r[0]!).ToArray();
    }

    public async Task<PreviewRecord> PreviewAsync(string sql)
    {
        var outcome = await Pipeline.RunAsync(sql, new PreviewOptions(), null, default);
        Assert.False(outcome.IsRejected, string.Join(", ", outcome.Analysis.Rejects.Select(r => r.RuleId)));
        return outcome.Record!;
    }

    public Task<ApplyResult?> ApplyAsync(PreviewRecord record) =>
        Service.ApplyAsync(record.Result.PreviewId, new ApplyRequest { SqlHash = record.Result.SqlHash }, default);

    /// <summary>원본에서 바로(자동 커밋) 실행: 미리보기 뒤 원본이 바뀐 상황을 만든다.</summary>
    public Task ExecOnOriginAsync(params string[] batches) => TestDb.ExecAsync(Origin, batches);

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = await TestDb.OpenAsync(Origin);
        return await TestDb.ScalarAsync<T>(conn, sql);
    }

    public async ValueTask DisposeAsync()
    {
        await cleanup.DisposeAsync();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>[적용] DB 테스트(dbrt_apl_ 기준선). 실행은 테스트 라운드에서.</summary>
public sealed class ApplySqlTests
{
    // 미리보기 뒤 원본을 바꾸는 시나리오용 작은 원본: T(키 있음), Gate(트리거 동작 스위치), Log(트리거의 롤백 뒤 기록).
    static readonly string[] Custom =
    [
        "CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY, v int NOT NULL);",
        "INSERT INTO dbo.T (id, v) VALUES (1, 1), (2, 5), (3, 20);",
        "CREATE TABLE dbo.Gate (id int NOT NULL PRIMARY KEY, deny bit NOT NULL);",
        "INSERT INTO dbo.Gate (id, deny) VALUES (1, 0);",
        "CREATE TABLE dbo.Log (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, msg nvarchar(100) NOT NULL);",
        // Gate.deny = 1이면 ROLLBACK한 뒤 로그를 남긴다(자동 커밋되어 롤백 뒤 남은 변경이 된다, A22·E13).
        """
        CREATE TRIGGER dbo.tr_T_gate ON dbo.T AFTER UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM dbo.Gate WHERE id = 1 AND deny = 1)
            BEGIN
                ROLLBACK;
                INSERT INTO dbo.Log (msg) VALUES (N'denied');
                RAISERROR (N'gate denied', 16, 1);
            END
        END;
        """,
    ];

    [SqlFact]
    public async Task Cl01_commits_to_origin()
    {
        await using var lab = await ApplyLab.CascadeLabAsync();
        var record = await lab.PreviewAsync("DELETE FROM cl01.A WHERE id = 1;");

        var result = await lab.ApplyAsync(record);

        Assert.Equal(ApplyStatus.Committed, result!.Status);
        Assert.Equal(VerificationMode.SnapshotNetdiff, result.Verification.Mode);
        Assert.True(result.Verification.NetSummaryMatch);
        Assert.Empty(result.Verification.Mismatches);
        Assert.True(result.SandboxRefreshStarted);
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM cl01.A WHERE id = 1;"));
        Assert.Equal(0, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.databases WHERE name = N'{lab.Origin}_pre';"));
    }

    [SqlFact]
    public async Task Origin_row_changed_after_preview_is_drift_detected()
    {
        await using var lab = await ApplyLab.CustomAsync("drift", Custom);
        var record = await lab.PreviewAsync("UPDATE dbo.T SET v = v + 1 WHERE id = 1;");
        await lab.ExecOnOriginAsync("UPDATE dbo.T SET v = 100 WHERE id = 1;");

        var result = await lab.ApplyAsync(record);

        Assert.Equal(ApplyStatus.DriftDetected, result!.Status);
        var drift = Assert.Single(result.Verification.DriftRows);
        Assert.Equal(DriftKind.Changed, drift.Kind);
        Assert.Equal(["v"], drift.Columns);
        Assert.Empty(result.Statements);
        Assert.Equal(100, await lab.ScalarAsync<int>("SELECT v FROM dbo.T WHERE id = 1;"));
    }

    [SqlFact]
    public async Task Row_inserted_into_origin_after_preview_is_rolled_back_mismatch()
    {
        await using var lab = await ApplyLab.CustomAsync("mismatch", Custom);
        var record = await lab.PreviewAsync("UPDATE dbo.T SET v = 0 WHERE v > 10;");
        await lab.ExecOnOriginAsync("INSERT INTO dbo.T (id, v) VALUES (4, 50);"); // 새로 WHERE를 만족하는 행: 드리프트 검사가 못 잡는다(§5.3)

        var result = await lab.ApplyAsync(record);

        Assert.Equal(ApplyStatus.RolledBackMismatch, result!.Status);
        Assert.Contains(result.Verification.Mismatches, m => m.Kind == MismatchKind.StatementRowCount);
        Assert.Contains(result.Verification.Mismatches, m => m.Kind == MismatchKind.RowUnexpected && m.Table == "dbo.T");
        Assert.False(result.SandboxRefreshStarted);
        Assert.Equal(20, await lab.ScalarAsync<int>("SELECT v FROM dbo.T WHERE id = 3;"));
        Assert.Equal(50, await lab.ScalarAsync<int>("SELECT v FROM dbo.T WHERE id = 4;"));
    }

    [SqlFact]
    public async Task Trigger_rollback_on_origin_is_error_with_residual_committed()
    {
        // CL12형(트리거 ROLLBACK + 롤백 뒤 기록). 미리보기에서는 통과하고 원본에서만 거부되도록 Gate를 미리보기 뒤에 켠다.
        await using var lab = await ApplyLab.CustomAsync("trig", Custom);
        var record = await lab.PreviewAsync("UPDATE dbo.T SET v = 2 WHERE id = 1;");
        await lab.ExecOnOriginAsync("UPDATE dbo.Gate SET deny = 1 WHERE id = 1;");

        var result = await lab.ApplyAsync(record);

        Assert.Equal(ApplyStatus.Error, result!.Status);
        Assert.NotEqual(StatementStatus.Ok, Assert.Single(result.Statements).Status);
        Assert.False(result.Verification.ResidualCheckFailed);
        Assert.Contains(result.Verification.ResidualCommitted, n => n.Table == "dbo.Log" && n.Change == NetChangeKind.Inserted);
        Assert.True(result.SandboxRefreshStarted);
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT v FROM dbo.T WHERE id = 1;"));
    }

    [SqlFact]
    public async Task Force_keyset_commits_with_keyset_verification()
    {
        await using var lab = await ApplyLab.CascadeLabAsync(new ApplySettings { ExecuteAsUser = PreviewUserSql.UserName, ForceKeyset = true });
        var record = await lab.PreviewAsync("DELETE FROM cl01.A WHERE id = 1;");

        var result = await lab.ApplyAsync(record);

        Assert.Equal(ApplyStatus.Committed, result!.Status);
        Assert.Equal(VerificationMode.Keyset, result.Verification.Mode);
        Assert.Null(result.Verification.NetSummaryMatch);
        Assert.Contains(result.Verification.TableCounts, c => c.Table == "cl01.A" && c.After - c.Before == -1 && c.ExpectedDelta == -1);
        Assert.Contains(KeysetVerifier.Limitation, result.Verification.Limitations);
        Assert.Equal(0, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.databases WHERE name = N'{lab.Origin}_pre';"));
    }
}
#endif
