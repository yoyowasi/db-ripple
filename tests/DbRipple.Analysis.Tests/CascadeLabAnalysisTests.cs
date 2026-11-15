using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.TestSupport;

namespace DbRipple.Analysis.Tests;

/// <summary>자기 전용 CascadeLab 기준선(dbrt_ana_)을 한 번 만들어 쓰고 지운다. 연결 환경 변수가 없으면 만들지 않는다.</summary>
public sealed class CascadeLabFixture : IAsyncLifetime
{
    public TestBaseline? Baseline { get; private set; }
    public BaselineContext Context { get; private set; } = null!;
    public DbCatalog Catalog { get; private set; } = null!;
    public List<string> DatabaseNames { get; } = [];

    public async Task InitializeAsync()
    {
        if (!TestDb.IsConfigured) return;
        Baseline = await TestBaseline.BuildCascadeLabAsync("ana");
        DatabaseNames.AddRange([Baseline.Origin, Baseline.Sandbox, Baseline.Snapshot]);
        await using (var conn = await TestDb.OpenAsync(Baseline.Sandbox))
            Catalog = await CatalogReader.ReadAsync(conn);
        Context = new BaselineContext
        {
            OriginDb = Baseline.Origin,
            SandboxDb = Baseline.Sandbox,
            SnapshotDb = Baseline.Snapshot,
            BackupFinishedAt = Baseline.Result.BackupCompletedAt,
            Fingerprint = await ReadFingerprint(Baseline.Sandbox),
            FingerprintMatch = Baseline.Result.FingerprintMatch,
            Catalog = Catalog,
            Triggers = new TriggerAnalyzer().Analyze(Catalog),
            Manifest = Baseline.Result.Install.Manifest,
        };
    }

    static async Task<Fingerprint> ReadFingerprint(string db)
    {
        await using var conn = await TestDb.OpenAsync(db);
        return await Fingerprint.ReadAsync(conn, db);
    }

    public async Task DisposeAsync()
    {
        if (Baseline is null) return;
        await Baseline.DisposeAsync();
        // 테스트 뒤 dbrt_ana_ DB가 남지 않는다(이 픽스처가 만든 것만 본다).
        await using var conn = await TestDb.OpenAsync("master");
        var left = await TestDb.RowsAsync(conn, "SELECT name FROM sys.databases WHERE name IN (" + string.Join(", ", DatabaseNames.Select(n => "N'" + n + "'")) + ")");
        if (left.Count > 0) throw new InvalidOperationException("정리되지 않은 테스트 DB: " + string.Join(", ", left.Select(r => (string)r[0]!)));
    }
}

/// <summary>C6 통합: CascadeLab 카탈로그의 트리거 정적 분석과 CL01~CL08 문장 수락(SqlFact).</summary>
public class CascadeLabAnalysisTests(CascadeLabFixture fx) : IClassFixture<CascadeLabFixture>
{
    [SqlFact]
    public void Cascadelab_trigger_write_targets_match_the_seed_scripts()
    {
        // 3주차: CL09~CL20 시드(cl09.sql~)도 같은 기준선에 실리므로 CL01~CL08 스키마의 트리거만 본다.
        var t = fx.Context.Triggers.Where(x => !x.IsAuditTrigger && IsWeek1Schema(x.Table)).ToDictionary(x => x.Trigger, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["cl06.trg_Line_del", "cl07.trg_A_upd", "cl07.trg_B_upd", "cl07.trg_C_ins", "cl08.trg_Acct_upd"], t.Keys.Order());

        void Check(string trigger, string table, ChangeOperation op, params string[] targets)
        {
            var info = t[trigger];
            Assert.Equal(table, info.Table);
            Assert.Equal([op], info.Events);
            Assert.False(info.IsInsteadOf);
            Assert.False(info.IsDisabled);
            Assert.False(info.IsEncrypted);
            Assert.False(info.HasDynamicSql);
            Assert.False(info.HasRollback);
            Assert.Empty(info.ExternalSideEffects);
            Assert.Equal(targets, info.WriteTargets);
        }
        Check("cl06.trg_Line_del", "cl06.Line", ChangeOperation.Delete, "cl06.LineLog");
        Check("cl07.trg_A_upd", "cl07.A", ChangeOperation.Update, "cl07.B");
        Check("cl07.trg_B_upd", "cl07.B", ChangeOperation.Update, "cl07.C");
        Check("cl07.trg_C_ins", "cl07.C", ChangeOperation.Insert, "cl07.D");
        Check("cl08.trg_Acct_upd", "cl08.Acct", ChangeOperation.Update, "cl08.Acct");   // 자기 테이블 갱신(별칭 a → cl08.Acct)
    }

    [SqlFact]
    public void Audit_triggers_are_present_marked_and_not_analyzed()
    {
        var audit = fx.Context.Triggers.Where(x => x.IsAuditTrigger).ToList();
        Assert.NotEmpty(audit);
        Assert.All(audit, a => { Assert.StartsWith("dbr_audit_", a.Trigger[(a.Trigger.IndexOf('.') + 1)..]); Assert.Empty(a.WriteTargets); Assert.False(a.HasDynamicSql); });
        // 테이블당 3개(I, U, D)
        Assert.Equal(0, audit.Count % 3);
    }

    static bool IsWeek1Schema(string table) => string.CompareOrdinal(table, "cl09") < 0;

    /// <summary>CL01~CL08(진단 0개 기대). CL09 이후는 기대 경고가 있어 CascadeLabWeek3Tests가 본다.</summary>
    public static TheoryData<string> CaseFiles() => Files(id => string.CompareOrdinal(id, "CL09") < 0);

    /// <summary>CL09~CL20(lab-1, answer-format v0.2).</summary>
    public static TheoryData<string> Week3CaseFiles() => Files(id => string.CompareOrdinal(id, "CL09") >= 0 && string.CompareOrdinal(id, "CL21") < 0);

    public static TheoryData<string> ExecCaseFiles() => Files(id => string.CompareOrdinal(id, "CL21") >= 0 && string.CompareOrdinal(id, "CL25") < 0);

    public static TheoryData<string> RejectCaseFiles() => Files(id => string.CompareOrdinal(id, "CL25a") >= 0 && string.CompareOrdinal(id, "CL25d") < 0);

    static TheoryData<string> Files(Func<string, bool> include)
    {
        var d = new TheoryData<string>();
        if (Directory.Exists(RepoPaths.CascadeLab))
            foreach (var f in Directory.GetFiles(Path.Combine(RepoPaths.CascadeLab, "cases"), "CL*.json").Order(StringComparer.Ordinal))
                if (include(Path.GetFileName(f))) d.Add(Path.GetFileName(f));
        return d;
    }

    /// <summary>
    /// 3주차 회귀(CL09~CL20, lab-1 목록 2026-11-11): 거부 사례는 없고, 문장마다 수락되며, 한 스크립트로 이어 붙인 분석의 경고 ruleId 집합이 expected.warnings와 같다
    /// (CL10 WARN_TRIGGER_DYNAMIC_SQL, CL13 WARN_AUDIT_NOT_LAST, CL14b WARN_TOP_WITHOUT_ORDER, CL16b WARN_NONDETERMINISTIC_FUNCTION, 나머지 없음).
    /// </summary>
    [SqlTheory]
    [MemberData(nameof(Week3CaseFiles))]
    public void Week3_cascadelab_cases_are_accepted_with_expected_warnings(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "cases", file)));
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var sqls = root.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("sql").GetString()!).ToList();
        Assert.NotEmpty(sqls);
        var expected = root.GetProperty("expected");
        var warnings = expected.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString()!).Distinct().Order(StringComparer.Ordinal).ToList()
            : [];

        foreach (var sql in sqls)
        {
            var one = new BatchAnalyzer().Analyze(sql, fx.Context);
            Assert.True(one.IsAccepted, id + ": " + string.Join("; ", one.Diagnostics.Select(d => d.RuleId + " " + d.Message)));
            Assert.Empty(one.Rejects);
            Assert.Equal(sql, Assert.Single(one.Statements).Sql);
        }
        var all = new BatchAnalyzer().Analyze(string.Join("\nGO\n", sqls), fx.Context);
        Assert.True(all.IsAccepted, id);
        Assert.Equal(sqls.Count, all.Statements.Count);
        Assert.Equal(warnings, all.Warnings.Select(d => d.RuleId).Distinct().Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>CL21~CL24는 실제 DB에서 읽은 프로시저·트리거 카탈로그로 분석한다. 분석만 하며 호출은 실행하지 않는다.</summary>
    [SqlTheory]
    [MemberData(nameof(ExecCaseFiles))]
    public void Exec_cascadelab_cases_are_accepted_with_exact_warnings(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "cases", file)));
        var root = doc.RootElement;
        Assert.Equal("CascadeLab", root.GetProperty("database").GetString());
        var sqls = root.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("sql").GetString()!).ToList();
        Assert.NotEmpty(sqls);
        var expectedNode = root.GetProperty("expected");
        var expected = expectedNode.TryGetProperty("warnings", out var warnings)
            ? warnings.EnumerateArray().Select(w => w.GetString()!).Order(StringComparer.Ordinal).ToArray()
            : [];

        foreach (var sql in sqls)
        {
            var result = new BatchAnalyzer().Analyze(sql, fx.Context);
            Assert.True(result.IsAccepted, string.Join("; ", result.Diagnostics.Select(d => d.RuleId + " " + d.Message)));
            Assert.Empty(result.Rejects);
            var statement = Assert.Single(result.Statements);
            Assert.Equal(ChangeOperation.Exec, statement.Kind);
            Assert.Equal(sql, statement.Sql);
            Assert.NotNull(statement.Plan);
            Assert.False(string.IsNullOrWhiteSpace(statement.Plan.ExecSql));
            Assert.Equal(expected, result.Warnings.Select(d => d.RuleId).Order(StringComparer.Ordinal).ToArray());
        }

        var all = new BatchAnalyzer().Analyze(string.Join("\nGO\n", sqls), fx.Context);
        Assert.True(all.IsAccepted);
        Assert.Equal(sqls.Count, all.Statements.Count);
        Assert.Equal(expected, all.Warnings.Select(d => d.RuleId).Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>CL25a~c는 실행하지 않고 실제 카탈로그 분석의 거부 규칙과 문장 번호를 정확히 검증한다.</summary>
    [SqlTheory]
    [MemberData(nameof(RejectCaseFiles))]
    public void Rejected_exec_cases_report_exact_rules_without_execution(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "cases", file)));
        var root = doc.RootElement;
        var expectedNode = root.GetProperty("expected");
        Assert.Equal("rejected", expectedNode.GetProperty("status").GetString());
        var expected = expectedNode.GetProperty("rejects").EnumerateArray()
            .Select(d => (RuleId: d.GetProperty("ruleId").GetString()!, StatementIndex: (int?)d.GetProperty("statementIndex").GetInt32()))
            .OrderBy(d => d.RuleId, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal("CascadeLab", root.GetProperty("database").GetString());
        var statement = Assert.Single(root.GetProperty("statements").EnumerateArray());
        var result = new BatchAnalyzer().Analyze(statement.GetProperty("sql").GetString()!, fx.Context);
        Assert.False(result.IsAccepted);
        Assert.Equal(expected,
            result.Rejects.OrderBy(d => d.RuleId, StringComparer.Ordinal).Select(d => (d.RuleId, d.StatementIndex)).ToArray());
        Assert.All(result.Rejects, d => Assert.Equal(Severity.Error, d.Severity));
    }

    /// <summary>CL25d는 공유 CascadeLab에 외부 부작용 트리거를 섞지 않고 전용 dbrt_ana_ DB의 카탈로그로 두 문장을 분석한다.</summary>
    [SqlFact]
    public async Task Reach_case_rejects_both_delete_and_exec_in_private_database_without_execution()
    {
        var db = TestDb.NewName("ana", "reach");
        await using var cleanup = new TestDatabases(db);
        await TestDb.ExecAsync("master", $"CREATE DATABASE [{db}];");
        // USE master 구간의 DROP/CREATE DATABASE는 제외하고 스키마·시드만 자기 테스트 DB에 설치한다.
        var script = await File.ReadAllTextAsync(Path.Combine(RepoPaths.CascadeLab, "sql", "extra", "cl25d.sql"));
        var batches = CascadeLabSeed.BatchesFor(script, "CascadeLab_Reach").ToArray();
        Assert.NotEmpty(batches);
        await TestDb.ExecAsync(db, batches);
        await using var conn = await TestDb.OpenAsync(db);
        var catalog = await CatalogReader.ReadAsync(conn);
        var context = fx.Context with
        {
            OriginDb = db, SandboxDb = db, SnapshotDb = db,
            Catalog = catalog,
            Triggers = new TriggerAnalyzer().Analyze(catalog),
            Manifest = new CaptureManifest("analysis-only", [], [], []),
        };
        Assert.Contains(catalog.Tables, t => t.Schema == "cl25d" && t.Name == "Parent");
        Assert.Contains(context.Triggers, t => t.Table == "cl25d.Child" && t.ExternalSideEffects.Count > 0);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "cases", "CL25d-reject-reach-external.json")));
        var sqls = doc.RootElement.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("sql").GetString()!).ToList();
        Assert.Equal(2, sqls.Count);
        foreach (var sql in sqls)
        {
            var one = new BatchAnalyzer().Analyze(sql, context);
            Assert.False(one.IsAccepted);
            var rejection = Assert.Single(one.Rejects);
            Assert.Equal((AnalysisRules.RejectTriggerExternalEffect, (int?)1), (rejection.RuleId, rejection.StatementIndex));
        }
        var all = new BatchAnalyzer().Analyze(string.Join("\nGO\n", sqls), context);
        Assert.False(all.IsAccepted);
        Assert.Equal(new[] { (AnalysisRules.RejectTriggerExternalEffect, (int?)1), (AnalysisRules.RejectTriggerExternalEffect, (int?)2) },
            all.Rejects.OrderBy(d => d.StatementIndex).Select(d => (d.RuleId, d.StatementIndex)).ToArray());
        // 테이블에 쓰는 DELETE/EXEC는 한 번도 실행하지 않았으며 시드가 그대로 남아 있어야 한다.
        Assert.Equal(2, await TestDb.ScalarAsync<int>(conn, "SELECT COUNT(*) FROM cl25d.Parent;"));
        Assert.Equal(2, await TestDb.ScalarAsync<int>(conn, "SELECT COUNT(*) FROM cl25d.Child;"));
    }

    /// <summary>
    /// C13 회귀 입력(tests/fixtures/capture/triggers-cascadelab.json): 실제 CascadeLab 카탈로그의 C6 트리거 정적 분석(감사 트리거 제외).
    /// Execution 픽스처의 기준선은 Triggers가 빈 배열이라(C6 미참조) Attribution.Tests CL09~CL20 회귀가 이 파일로 채운다. DBR_WRITE_FIXTURES=1일 때만 쓴다.
    /// </summary>
    [SqlFact]
    public async Task Write_trigger_static_info_fixture()
    {
        var user = fx.Context.Triggers.Where(t => !t.IsAuditTrigger).ToList();
        Assert.NotEmpty(user);
        if (Environment.GetEnvironmentVariable("DBR_WRITE_FIXTURES") != "1") return;
        var path = Path.Combine(RepoPaths.Root, "tests", "fixtures", "capture", "triggers-cascadelab.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new JsonSerializerOptions(ContractJson.Create()) { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(user, options) + "\n", new System.Text.UTF8Encoding(false));
    }

    /// <summary>CL10: 동적 SQL 트리거는 HasDynamicSql이고 쓰기 대상 미상은 아니다(Q6 규약). CL12a: ROLLBACK 트리거. 프로시저를 부르는 CascadeLab 트리거가 없으면 두 목록은 빈다.</summary>
    [SqlFact]
    public void Week3_cascadelab_trigger_flags()
    {
        var user = fx.Context.Triggers.Where(x => !x.IsAuditTrigger).ToList();
        if (!user.Any(t => t.Table.StartsWith("cl10.", StringComparison.OrdinalIgnoreCase))) return; // 시드 전(lab-1 작성 중)
        Assert.Contains(user, t => t.Table.StartsWith("cl10.", StringComparison.OrdinalIgnoreCase) && t.HasDynamicSql && !t.WriteTargetsUnknown);
        Assert.Contains(user, t => t.Table.StartsWith("cl12.", StringComparison.OrdinalIgnoreCase) && t.HasRollback);
        Assert.All(user, t => Assert.Equal(t.ProcedureCalls.Order(StringComparer.OrdinalIgnoreCase),
            t.ExpandedProcedureCalls.Concat(t.UnresolvedProcedureCalls).Order(StringComparer.OrdinalIgnoreCase)));
    }

    static readonly Dictionary<string, (ChangeOperation Op, string Target)> ExpectedTargets = new()
    {
        ["CL01"] = (ChangeOperation.Delete, "cl01.A"),
        ["CL02"] = (ChangeOperation.Delete, "cl02.Dept"),
        ["CL03"] = (ChangeOperation.Delete, "cl03.Category"),
        ["CL03b"] = (ChangeOperation.Delete, "cl03.Grp"),
        ["CL04"] = (ChangeOperation.Update, "cl04.P"),
        ["CL04b"] = (ChangeOperation.Update, "cl04.P"),
        ["CL05"] = (ChangeOperation.Delete, "cl05.P"),
        ["CL06"] = (ChangeOperation.Delete, "cl06.Ord"),
        ["CL07"] = (ChangeOperation.Update, "cl07.A"),
        ["CL08a"] = (ChangeOperation.Update, "cl08.Acct"),
        ["CL08b"] = (ChangeOperation.Update, "cl08.Acct"),
    };

    [SqlTheory]
    [MemberData(nameof(CaseFiles))]
    public void Cascadelab_case_statements_are_all_accepted_without_diagnostics(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "cases", file)));
        var id = doc.RootElement.GetProperty("id").GetString()!;
        var sqls = doc.RootElement.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("sql").GetString()!).ToList();
        Assert.NotEmpty(sqls);

        // 문장 하나씩, 그리고 GO 없이 한 스크립트로 이어 붙여서
        foreach (var sql in sqls)
        {
            var one = new BatchAnalyzer().Analyze(sql, fx.Context);
            Assert.True(one.IsAccepted, id + ": " + string.Join("; ", one.Diagnostics.Select(d => d.RuleId + " " + d.Message)));
            Assert.Empty(one.Diagnostics);
            Assert.Equal(sql, Assert.Single(one.Statements).Sql);
        }
        var all = new BatchAnalyzer().Analyze(string.Join("\nGO\n", sqls), fx.Context);
        Assert.True(all.IsAccepted);
        Assert.Equal(sqls.Count, all.Statements.Count);

        var (op, target) = ExpectedTargets[id];
        Assert.Equal(op, all.Statements[0].Kind);
        Assert.Equal(target, all.Statements[0].Target);
    }

    [SqlFact]
    public void Real_catalog_baseline_rules()
    {
        var a = new BatchAnalyzer();
        Assert.Contains(a.Analyze("DELETE FROM audit.AuditHeader", fx.Context).Rejects, d => d.RuleId == AnalysisRules.RejectCaptureStore);
        Assert.Contains(a.Analyze("DELETE FROM cl01.NoSuch", fx.Context).Rejects, d => d.RuleId == AnalysisRules.RejectTargetNotUserTable);
        Assert.Contains(a.Analyze("DELETE FROM A", fx.Context).Rejects, d => d.RuleId == AnalysisRules.RejectTargetNotUserTable); // dbo.A 없음(Q4)
        var r = a.Analyze("UPDATE cl08.Acct SET bal = GETDATE() WHERE id = 1", fx.Context);
        Assert.True(r.IsAccepted);
        Assert.Equal(["bal"], r.Statements[0].NondeterministicColumns);
        Assert.Equal([AnalysisRules.WarnNondeterministicFunction], r.Warnings.Select(w => w.RuleId));
    }
}

/// <summary>DBRIPPLE_SQL_CONNECTION이 없으면 건너뛰는 Theory.</summary>
public sealed class SqlTheoryAttribute : TheoryAttribute
{
    /// <summary>환경 변수가 없으면 Skip을 채운다.</summary>
    public SqlTheoryAttribute()
    {
        if (!TestDb.IsConfigured) Skip = $"{TestDb.ConnectionEnvVar} 없음";
    }
}
