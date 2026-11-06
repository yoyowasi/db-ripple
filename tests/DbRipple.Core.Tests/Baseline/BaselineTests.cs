using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Core.Tests.Baseline;

public class BaselineTests
{
    static readonly BackupFile[] Files = [new("AW_Data", 'D', 1), new("AW_Extra", 'D', 3), new("AW_Log", 'L', 2)];

    static void Parse(string sql)
    {
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Message)) + "\n" + sql);
    }

    [Fact]
    public void All_statements_parse()
    {
        Parse(BaselineSql.DropSnapshot("AW_Sandbox_base"));
        Parse(BaselineSql.Backup("AW_Origin", "/var/opt/mssql/backup/AW_Origin.bak"));
        Parse(BaselineSql.FileListOnly("/var/opt/mssql/backup/AW_Origin.bak"));
        Parse(BaselineSql.Restore("AW_Sandbox", "/var/opt/mssql/backup/AW_Origin.bak", Files));
        Parse(BaselineSql.CreateSnapshot("AW_Sandbox_base", "AW_Sandbox", ["AW_Data", "AW_Extra"]));
    }

    [Fact]
    public void Backup_is_copy_only_and_restore_moves_every_file()
    {
        Assert.Contains("WITH COPY_ONLY, INIT", BaselineSql.Backup("AW_Origin", "/b.bak"));
        var r = BaselineSql.Restore("AW_Sandbox", "/b.bak", Files);
        Assert.Contains("MOVE N'AW_Data' TO N'/var/opt/mssql/data/AW_Sandbox_AW_Data.mdf'", r);
        Assert.Contains("MOVE N'AW_Extra' TO N'/var/opt/mssql/data/AW_Sandbox_AW_Extra.ndf'", r);
        Assert.Contains("MOVE N'AW_Log' TO N'/var/opt/mssql/data/AW_Sandbox_AW_Log.ldf'", r);
        Assert.EndsWith(", REPLACE;", r);
    }

    [Fact]
    public void Snapshot_lists_data_files_as_sparse_files()
    {
        var s = BaselineSql.CreateSnapshot("AW_Sandbox_base", "AW_Sandbox", ["AW_Data"]);
        Assert.Equal("CREATE DATABASE [AW_Sandbox_base] ON (NAME = [AW_Data], FILENAME = N'/var/opt/mssql/data/AW_Sandbox_base_AW_Data.ss') AS SNAPSHOT OF [AW_Sandbox];", s);
    }

    [Fact]
    public void Names_and_paths_are_escaped()
    {
        var b = BaselineSql.Backup("we]ird", "/x/it's.bak");
        Assert.Contains("[we]]ird]", b);
        Assert.Contains("N'/x/it''s.bak'", b);
        Parse(b);
        var r = BaselineSql.Restore("sb", "/b.bak", [new("a/../b'c", 'D', 1)]);
        Assert.Contains("MOVE N'a/../b''c' TO N'/var/opt/mssql/data/sb_a____b_c.mdf'", r);
        Parse(r);
    }

    [Fact]
    public async Task Build_runs_steps_in_design_order()
    {
        var steps = new FakeSteps();
        var times = new Queue<DateTimeOffset>([new(2026, 11, 6, 9, 58, 0, TimeSpan.Zero), new(2026, 11, 6, 9, 59, 0, TimeSpan.Zero)]);
        var result = await BaselineBuilder.BuildAsync(new("AW_Origin", "AW_Sandbox", "AW_Sandbox_base", "/b.bak"), steps, times.Dequeue);

        var log = steps.Log;
        Assert.StartsWith("master: IF DB_ID(N'AW_Sandbox_base')", log[0]);
        Assert.StartsWith("master: BACKUP DATABASE [AW_Origin]", log[1]);
        Assert.StartsWith("filelist: RESTORE FILELISTONLY", log[2]);
        Assert.StartsWith("master: RESTORE DATABASE [AW_Sandbox]", log[3]);
        Assert.Equal("catalog: AW_Sandbox", log[4]);
        Assert.StartsWith("AW_Sandbox: CREATE SCHEMA audit", log[5]);
        var fp = log.IndexOf("fingerprint");
        Assert.True(fp > 5);
        Assert.StartsWith("master: CREATE DATABASE [AW_Sandbox_base] ON (NAME = [AW_Data]", log[fp + 1]);
        Assert.DoesNotContain("AW_Log", log[fp + 1]);
        Assert.Equal(fp + 2, log.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 9, 58, 0, TimeSpan.Zero), result.BackupCompletedAt);
        Assert.Contains(result.Install.Manifest.Notes, n => n.Contains("ddlDatabaseTriggerLog"));
    }

    sealed class FakeSteps : IBaselineSteps
    {
        public List<string> Log { get; } = [];

        public Task ExecuteAsync(string database, string sql, CancellationToken ct) { Log.Add($"{database}: {sql}"); return Task.CompletedTask; }

        public Task<IReadOnlyList<BackupFile>> ReadFileListAsync(string sql, CancellationToken ct)
        { Log.Add("filelist: " + sql); return Task.FromResult<IReadOnlyList<BackupFile>>([new("AW_Data", 'D', 1), new("AW_Log", 'L', 2)]); }

        public Task<DbCatalog> ReadCatalogAsync(string database, CancellationToken ct)
        {
            Log.Add("catalog: " + database);
            return Task.FromResult(new DbCatalog(
                [new(10, "dbo", "T", TemporalType.None, false)],
                [new(10, 1, "Id", "int", false, false, false, false, false, null)],
                [new(10, 1, "PK_T", true, true, 1, "Id", false)],
                [],
                [new(90, "ddlDatabaseTriggerLog", 0, "DATABASE", false, false, "CREATE_TABLE", false, false, "CREATE TRIGGER ...")],
                [new(1, "AW_Data", "ROWS", "/d.mdf"), new(2, "AW_Log", "LOG", "/l.ldf")]));
        }

        public Task<bool> FingerprintMatchesAsync(string source, string sandbox, CancellationToken ct) { Log.Add("fingerprint"); return Task.FromResult(true); }
    }
}
