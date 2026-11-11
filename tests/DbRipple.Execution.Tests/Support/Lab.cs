using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.TestSupport;

namespace DbRipple.Execution.Tests.Support;

/// <summary>CascadeLab 두 변형의 전용 기준선(dbrt_exe_cl_, dbrt_exe_clrt_). 컬렉션 안에서 한 번만 만들고 끝에 지운다.</summary>
public sealed class LabFixture : IAsyncLifetime
{
    public ExeBaseline? Default { get; private set; }
    public ExeBaseline? Rt { get; private set; }
    public BaselineContext? DefaultContext { get; private set; }
    public BaselineContext? RtContext { get; private set; }

    public async Task InitializeAsync()
    {
        if (!TestDb.IsConfigured) return;
        Default = await ExeBaseline.CascadeLabAsync();
        DefaultContext = await Default.ContextAsync();
        Rt = await ExeBaseline.CascadeLabAsync(CascadeLabVariant.RecursiveTriggers);
        RtContext = await Rt.ContextAsync();
    }

    public async Task DisposeAsync()
    {
        if (Rt is not null) await Rt.DisposeAsync();
        if (Default is not null) await Default.DisposeAsync();
    }

    public (ExeBaseline Baseline, BaselineContext Context) For(LabCase c) =>
        c.Database == "CascadeLab_RT" ? (Rt!, RtContext!) : (Default!, DefaultContext!);
}

[CollectionDefinition(DbCollection.Name, DisableParallelization = true)]
public sealed class DbCollection : ICollectionFixture<LabFixture>
{
    public const string Name = "exe-db";
}

/// <summary>cascadelab/cases 사례 하나(필요한 필드만).</summary>
public sealed record LabCase(string Id, string Database, IReadOnlyList<string> Statements, string Status, int? ErrorNumber, IReadOnlyList<ExpectedFiring> Firings)
{
    public static IReadOnlyList<LabCase> All { get; } = Load();

    public static LabCase Get(string id) => All.Single(c => c.Id == id);

    static IReadOnlyList<LabCase> Load()
    {
        var list = new List<LabCase>();
        foreach (var f in Directory.GetFiles(Path.Combine(RepoPaths.CascadeLab, "cases"), "CL*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            var e = r.GetProperty("expected");
            list.Add(new LabCase(
                r.GetProperty("id").GetString()!,
                r.GetProperty("database").GetString()!,
                r.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("sql").GetString()!).ToList(),
                e.GetProperty("status").GetString()!,
                e.GetProperty("errorNumber").ValueKind == JsonValueKind.Null ? null : e.GetProperty("errorNumber").GetInt32(),
                e.GetProperty("firings").EnumerateArray().Select(x => new ExpectedFiring(
                    x.GetProperty("node").GetString()!,
                    x.GetProperty("table").GetString()!,
                    x.GetProperty("operation").GetString()!,
                    x.GetProperty("rowCount").GetInt64(),
                    x.GetProperty("depth").GetInt32(),
                    x.TryGetProperty("pairing", out var p) ? p.GetString() : null,
                    x.TryGetProperty("statementIndex", out var si) ? si.GetInt32() : 1)).ToList()));
        }
        return list;
    }
}

/// <summary>손 정답 묶음. StatementIndex는 answer-format v0.2(없으면 1, CL12b·CL17·CL18처럼 문장이 여럿일 때 씀).</summary>
public sealed record ExpectedFiring(string Node, string Table, string Operation, long RowCount, int Depth, string? Pairing, int StatementIndex = 1);

/// <summary>cascadelab/verification/logs/audit-2026-11-10.json의 사례별 감사 헤더(1주차 대조 도구 출력).</summary>
public static class AuditLog
{
    public sealed record Header(string Table, string Operation, long InsertedCount, long DeletedCount, int NestLevel, int TranCount, string ImageMode, string Images);

    static readonly Lazy<Dictionary<string, (string Status, List<Header> Firings)>> Cases = new(Load);

    public static (string Status, List<Header> Firings) Get(string id) => Cases.Value[id];

    static Dictionary<string, (string, List<Header>)> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.CascadeLab, "verification", "logs", "audit-2026-11-10.json")));
        var d = new Dictionary<string, (string, List<Header>)>();
        foreach (var c in doc.RootElement.GetProperty("Cases").EnumerateArray())
        {
            var firings = c.GetProperty("Firings").EnumerateArray().OrderBy(f => f.GetProperty("Seq").GetInt64()).Select(f => new Header(
                f.GetProperty("Table").GetString()!, f.GetProperty("Operation").GetString()!,
                f.GetProperty("InsertedCount").GetInt64(), f.GetProperty("DeletedCount").GetInt64(),
                f.GetProperty("NestLevel").GetInt32(), f.GetProperty("TranCount").GetInt32(), f.GetProperty("ImageMode").GetString()!,
                string.Join(" | ", f.GetProperty("Images").EnumerateArray().Select(i => i.GetString()!).Order(StringComparer.Ordinal)))).ToList();
            d[c.GetProperty("Id").GetString()!] = (c.GetProperty("Status").GetString()!, firings);
        }
        return d;
    }

    /// <summary>제품 RawCapture를 같은 모양으로.</summary>
    public static List<Header> From(RawCapture raw, BaselineContext ctx)
    {
        var names = ctx.Catalog.ToCaptureInput().ToDictionary(t => t.ObjectId, t => t.DisplayName);
        var images = raw.Images.GroupBy(i => i.CaptureSeq).ToDictionary(g => g.Key, g => g.Select(i =>
            $"{(i.ImageKind == ImageKind.Deleted ? "DELETED" : "INSERTED")} key={i.KeyJson ?? "null"} row={i.RowJson ?? "null"}").Order(StringComparer.Ordinal).ToList());
        return raw.Headers.OrderBy(h => h.CaptureSeq).Select(h => new Header(
            names[h.TableObjectId], h.Operation.ToString().ToUpperInvariant(), h.InsertedCount, h.DeletedCount, h.NestLevel, h.TranCount,
            h.ImageMode == ImageMode.Full ? "FULL" : "KEYS", string.Join(" | ", images.GetValueOrDefault(h.CaptureSeq) ?? []))).ToList();
    }
}
