using System.Text.RegularExpressions;
using DbRipple.Core.Baseline;
using DbRipple.Core.Sql;

namespace DbRipple.TestSupport;

/// <summary>저장소 루트 찾기(DbRipple.slnx가 있는 폴더).</summary>
public static class RepoPaths
{
    /// <summary>저장소 루트. 현재 실행 폴더에서 위로 올라가며 DbRipple.slnx를 찾는다.</summary>
    public static string Root { get; } = Find();

    /// <summary>cascadelab 폴더.</summary>
    public static string CascadeLab => Path.Combine(Root, "cascadelab");

    static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DbRipple.slnx"))) return d.FullName;
        throw new InvalidOperationException("DbRipple.slnx를 찾지 못함(저장소 밖에서 실행?)");
    }
}

/// <summary>CascadeLab 변형(cascadelab/sql/00-create-databases.sql).</summary>
public enum CascadeLabVariant
{
    /// <summary>CascadeLab: RECURSIVE_TRIGGERS OFF, 스키마 cl01~cl08.</summary>
    Default,
    /// <summary>CascadeLab_RT: RECURSIVE_TRIGGERS ON, 스키마 cl08만(CL08b).</summary>
    RecursiveTriggers,
}

/// <summary>
/// 공유 CascadeLab DB를 건드리지 않고, 저장소의 cascadelab/sql/cl*.sql을 자기 테스트 DB에 실행해 같은 시드를 만든다.
/// 스크립트의 "USE CascadeLab;"·"USE CascadeLab_RT;"는 대상 변형과 같은 쪽 배치만 골라 자기 DB에서 실행하는 표시로 쓴다.
/// GO 분리는 "GO만 있는 줄" 규칙이다(우리가 쓴 스크립트 전용. 사용자 SQL 분리는 C6의 ScriptDom이 한다).
/// </summary>
public static class CascadeLabSeed
{
    static readonly Regex Go = new(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex Use = new(@"^\s*USE\s+\[?(?<db>[A-Za-z0-9_]+)\]?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>원본 DB 이름(스크립트 USE 대상).</summary>
    public static string SourceName(CascadeLabVariant v) => v == CascadeLabVariant.Default ? "CascadeLab" : "CascadeLab_RT";

    /// <summary>
    /// database(테스트 DB, 없어야 함)를 만들고 변형 설정을 맞춘 뒤 cl01~cl08 스크립트(scripts가 있으면 그 파일 이름만)를 실행한다.
    /// </summary>
    public static async Task CreateAsync(string database, CascadeLabVariant variant, IEnumerable<string>? scripts = null)
    {
        if (!TestDb.IsTestDatabase(database)) throw new ArgumentException($"테스트 DB 이름이 아님: {database}");
        var q = SqlText.Quote(database);
        await TestDb.ExecAsync("master",
            $"CREATE DATABASE {q};",
            $"ALTER DATABASE {q} SET RECURSIVE_TRIGGERS {(variant == CascadeLabVariant.RecursiveTriggers ? "ON" : "OFF")};");
        var files = (scripts ?? Directory.GetFiles(Path.Combine(RepoPaths.CascadeLab, "sql"), "cl*.sql").Select(Path.GetFileName).OfType<string>())
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        var batches = new List<string>();
        foreach (var f in files)
            batches.AddRange(BatchesFor(await File.ReadAllTextAsync(Path.Combine(RepoPaths.CascadeLab, "sql", f)), SourceName(variant)));
        await TestDb.ExecAsync(database, [.. batches]);
    }

    /// <summary>스크립트를 GO로 나누고, 마지막 USE가 source인 배치만(USE 배치 자체와 주석뿐인 배치는 빼고) 돌려준다.</summary>
    public static IEnumerable<string> BatchesFor(string script, string source)
    {
        string? current = null;
        foreach (var raw in Go.Split(script))
        {
            var body = string.Join('\n', raw.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))).Trim();
            if (body.Length == 0) continue;
            var use = Use.Match(body);
            if (use.Success) { current = use.Groups["db"].Value; continue; }
            if (string.Equals(current, source, StringComparison.OrdinalIgnoreCase)) yield return raw;
        }
    }
}

/// <summary>
/// 자기 전용 기준선 한 벌(원본 역할 DB + 감사 장치를 깐 샌드박스 + 기준 스냅샷)을 Core BaselineBuilder(§5.1)로 만든다.
/// 원본 역할 DB는 수집 장치가 없으므로 평가의 오라클 사본(§10.3 AW_Actual 역할)으로도 쓸 수 있다(트랜잭션 + ROLLBACK으로만).
/// Dispose에서 스냅샷 → 샌드박스 → 원본 → 백업 파일 순으로 지운다.
/// </summary>
public sealed class TestBaseline : IAsyncDisposable
{
    /// <summary>AdventureWorks .bak 컨테이너 경로(scripts/restore-aw-origin.ps1). AW_Origin을 거치지 않고 이 파일에서 복원한다.</summary>
    public const string AdventureWorksBackup = "/var/opt/mssql/backup/AdventureWorks2025.bak";

    readonly TestDatabases cleanup;

    TestBaseline(string origin, string sandbox, string snapshot, string backup)
    {
        Origin = origin; Sandbox = sandbox; Snapshot = snapshot; BackupPath = backup;
        cleanup = new TestDatabases(snapshot, sandbox, origin);
        cleanup.ContainerFiles.Add(backup);
    }

    /// <summary>원본 역할 DB(감사 장치 없음).</summary>
    public string Origin { get; }
    /// <summary>샌드박스(감사 장치 설치).</summary>
    public string Sandbox { get; }
    /// <summary>기준 스냅샷.</summary>
    public string Snapshot { get; }
    /// <summary>기준선 백업 파일(컨테이너 경로).</summary>
    public string BackupPath { get; }
    /// <summary>기준선 결과(백업 시각, 설치 계획·매니페스트, 지문 일치). 성공한 뒤에만 채워진다.</summary>
    public BaselineResult Result { get; private set; } = null!;

    /// <summary>CascadeLab 시드로 기준선을 만든다(수 초). component는 TestDb.Components 코드.</summary>
    public static async Task<TestBaseline> BuildCascadeLabAsync(string component, CascadeLabVariant variant = CascadeLabVariant.Default, CancellationToken ct = default)
    {
        var tb = Create(component, variant == CascadeLabVariant.Default ? "cl" : "clrt");
        try
        {
            await CascadeLabSeed.CreateAsync(tb.Origin, variant);
            await tb.BuildAsync(ct);
            return tb;
        }
        catch
        {
            await tb.DisposeAsync();
            throw;
        }
    }

    /// <summary>AdventureWorks2025.bak에서 원본 역할 DB를 복원하고 기준선을 만든다(수십 초, 디스크 수백 MB). SP7·AW 통합 테스트용.</summary>
    public static async Task<TestBaseline> BuildAdventureWorksAsync(string component, CancellationToken ct = default)
    {
        var tb = Create(component, "aw");
        try
        {
            await using (var steps = new SqlBaselineSteps(TestDb.ConnectionString()))
                await BaselineBuilder.RestoreAsync(steps, tb.Origin, AdventureWorksBackup, null, ct);
            await tb.BuildAsync(ct);
            return tb;
        }
        catch
        {
            await tb.DisposeAsync();
            throw;
        }
    }

    static TestBaseline Create(string component, string tag)
    {
        var origin = TestDb.NewName(component, tag);
        return new TestBaseline(origin, origin + "_sbx", origin + "_base", TestDb.BackupPath(origin));
    }

    async Task BuildAsync(CancellationToken ct)
    {
        await using var steps = new SqlBaselineSteps(TestDb.ConnectionString());
        Result = await BaselineBuilder.BuildAsync(new BaselineOptions(Origin, Sandbox, Snapshot, BackupPath), steps, () => DateTimeOffset.Now, ct);
    }

    /// <summary>정리(실패는 표준 오류에만).</summary>
    public ValueTask DisposeAsync() => cleanup.DisposeAsync();
}
