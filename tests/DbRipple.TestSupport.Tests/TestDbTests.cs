using DbRipple.Core.Capture;
using DbRipple.TestSupport;

namespace DbRipple.TestSupport.Tests;

/// <summary>DB 격리 규칙(이름·가드)과 CascadeLab 시드 분리 검사.</summary>
public class TestDbTests
{
    [Fact]
    public async Task Names_follow_component_prefix_rule()
    {
        var n = TestDb.NewName("exe", "cl");
        Assert.Matches("^dbrt_exe_cl_[0-9a-f]{8}$", n);
        Assert.True(TestDb.IsTestDatabase(n));
        Assert.True(TestDb.IsTestDatabase(n + "_sbx"));
        Assert.Throws<ArgumentException>(() => TestDb.NewName("core", "x"));
        Assert.Throws<ArgumentException>(() => TestDb.NewName("exe", "Bad-Tag"));
        foreach (var shared in new[] { "AW_Origin", "AW_Sandbox", "AW_Sandbox_base", "CascadeLab", "CascadeLab_Sandbox", "dbr_it_x", "dbrt_zzz_x", "dbrt_exe_x " })
            Assert.False(TestDb.IsTestDatabase(shared));
        await Assert.ThrowsAsync<ArgumentException>(() => TestDb.DropAsync("AW_Sandbox"));
        Assert.Throws<ArgumentException>(() => TestDb.DeleteContainerFile("/var/opt/mssql/backup/AdventureWorks2025.bak"));
    }

    [Fact]
    public void CascadeLab_seed_picks_batches_by_use_target()
    {
        var cl08 = File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "sql", "cl08.sql"));
        var off = CascadeLabSeed.BatchesFor(cl08, "CascadeLab").ToList();
        var rt = CascadeLabSeed.BatchesFor(cl08, "CascadeLab_RT").ToList();
        Assert.NotEmpty(off);
        Assert.NotEmpty(rt);
        Assert.DoesNotContain(off.Concat(rt), b => b.TrimStart().StartsWith("USE", StringComparison.OrdinalIgnoreCase));
        var cl01 = File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "sql", "cl01.sql"));
        Assert.Empty(CascadeLabSeed.BatchesFor(cl01, "CascadeLab_RT"));
    }

    /// <summary>자기 전용 CascadeLab 기준선을 만들고(감사 장치·스냅샷 포함) 지운다. 공유 DB는 건드리지 않는다.</summary>
    [SqlFact]
    public async Task Private_cascadelab_baseline_is_built_and_dropped()
    {
        string origin, sandbox, snapshot;
        await using (var tb = await TestBaseline.BuildCascadeLabAsync("sup"))
        {
            (origin, sandbox, snapshot) = (tb.Origin, tb.Sandbox, tb.Snapshot);
            Assert.True(tb.Result.FingerprintMatch);
            Assert.Contains(tb.Result.Install.Manifest.Tables, t => t.Table == "cl07.A");
            // CL13(cl13.T)은 사용자 Last DELETE 트리거가 있어 감사 DELETE 순서가 NONE인 것이 사례의 의도다(A6, E15)
            Assert.All(tb.Result.Install.Manifest.Tables.Where(t => t.Table != "cl13.T"), t => Assert.Equal(new AuditOrderInfo("LAST", "LAST", "LAST"), t.AuditOrder));
            Assert.Equal(new AuditOrderInfo("LAST", "LAST", "NONE"), Assert.Single(tb.Result.Install.Manifest.Tables, t => t.Table == "cl13.T").AuditOrder);
            await using var conn = await TestDb.OpenAsync(sandbox);
            Assert.Equal(0, await TestDb.ScalarAsync<int>(conn, "SELECT COUNT(*) FROM audit.AuditHeader;"));
            Assert.Equal(1, await TestDb.ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{snapshot}' AND source_database_id = DB_ID();"));
            Assert.Equal(0, await TestDb.ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM [{origin}].sys.schemas WHERE name = N'audit';"));
        }
        await using var master = await TestDb.OpenAsync("master");
        Assert.Equal(0, await TestDb.ScalarAsync<int>(master, $"SELECT COUNT(*) FROM sys.databases WHERE name IN (N'{origin}', N'{sandbox}', N'{snapshot}');"));
    }
}
