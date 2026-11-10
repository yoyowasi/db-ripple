using System.Text.Json;
using System.Text.RegularExpressions;
using DbRipple.Contracts;

namespace DbRipple.Preview;

/// <summary>
/// C14 미리보기 저장소(§4 C14): 메모리 + JSON 파일(ContractJson.Options), 활성 1건, 만료 30분(IPreviewStore.DefaultExpiry).
/// ASP.NET에 의존하지 않도록 Api가 아니라 Preview 프로젝트에 둔다(평가 CLI도 쓴다).
/// <list type="bullet">
/// <item>파일은 {Directory}/{previewId}.preview.json 한 개다. 저장하면 이전 건의 파일(*.preview.json만)을 지운다(활성 1건). 재시작 뒤에도 파일에서 읽는다.</item>
/// <item>만료 기준은 PreviewResult.CreatedAt + Expiry를 Clock으로 비교한다(§5.3 "미리보기를 만든 지 30분"). 만료된 건은 읽을 때 지운다.</item>
/// <item>previewId는 파일 이름이 되므로 영문자·숫자·밑줄·하이픈 1~64자만 받는다. 그 밖의 id는 없는 id로 본다(경로 탈출 방지).</item>
/// <item>행 페이지의 offset·limit 범위가 틀리면 ArgumentException(API 400). 없는 id·만료는 null/false(API 404).</item>
/// </list>
/// </summary>
public sealed partial class PreviewStore(string directory, TimeProvider clock) : IPreviewStore
{
    const string FileSuffix = ".preview.json";

    readonly SemaphoreSlim gate = new(1, 1);
    PreviewRecord? active;

    /// <summary>JSON 파일 폴더.</summary>
    public string Directory { get; } = directory;
    /// <summary>시계(만료 판정).</summary>
    public TimeProvider Clock { get; } = clock;
    /// <summary>만료 시간(기본 30분).</summary>
    public TimeSpan Expiry { get; init; } = IPreviewStore.DefaultExpiry;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <inheritdoc />
    public async Task SaveAsync(PreviewRecord record, CancellationToken ct)
    {
        var id = record.Result.PreviewId;
        if (!IdPattern().IsMatch(id)) throw new ArgumentException("previewId 형식이 올바르지 않습니다(영문자·숫자·밑줄·하이픈 1~64자).");
        await gate.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathOf(id);
            var temp = path + ".tmp";
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await JsonSerializer.SerializeAsync(fs, record, ContractJson.Options, ct);
            File.Move(temp, path, overwrite: true);
            foreach (var other in System.IO.Directory.EnumerateFiles(Directory, "*" + FileSuffix))
                if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) TryDelete(other);
            active = record;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<PreviewRecord?> GetAsync(string previewId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await LoadAsync(previewId, ct); }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<RowPage<RowChange>?> PageEventsAsync(string previewId, RowPageQuery query, CancellationToken ct)
    {
        Validate(query);
        if (await GetAsync(previewId, ct) is not { } r) return null;
        var rows = query.FiringId is { } f ? r.RowChanges.Where(x => x.FiringId == f) : r.RowChanges;
        return Page(rows, RowView.Event, query);
    }

    /// <inheritdoc />
    public async Task<RowPage<NetChange>?> PageNetAsync(string previewId, RowPageQuery query, CancellationToken ct)
    {
        Validate(query);
        if (await GetAsync(previewId, ct) is not { } r) return null;
        var rows = query.FiringId is { } f ? r.NetChanges.Where(x => x.ExplainedBy?.FiringIds.Contains(f) == true) : r.NetChanges;
        return Page(rows, RowView.Net, query);
    }

    /// <inheritdoc />
    public async Task<bool> ExportAsync(string previewId, Stream destination, CancellationToken ct)
    {
        if (await GetAsync(previewId, ct) is not { } r) return false;
        await JsonSerializer.SerializeAsync(destination, r, ContractJson.Options, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string previewId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var found = await LoadAsync(previewId, ct) is not null; // 만료된 건은 여기서 이미 지워지고 false
            if (!found) return false;
            active = null;
            TryDelete(PathOf(previewId));
            return true;
        }
        finally { gate.Release(); }
    }

    // gate 안에서만 부른다. 메모리의 활성 건 → 파일 순으로 찾고, 만료면 지우고 null.
    async Task<PreviewRecord?> LoadAsync(string previewId, CancellationToken ct)
    {
        if (!IdPattern().IsMatch(previewId)) return null;
        var record = active is { } a && a.Result.PreviewId == previewId ? a : await ReadFileAsync(previewId, ct);
        if (record is null) return null;
        if (Clock.GetUtcNow() >= record.Result.CreatedAt + Expiry)
        {
            if (active?.Result.PreviewId == previewId) active = null;
            TryDelete(PathOf(previewId));
            return null;
        }
        return active = record;
    }

    async Task<PreviewRecord?> ReadFileAsync(string previewId, CancellationToken ct)
    {
        var path = PathOf(previewId);
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return await JsonSerializer.DeserializeAsync<PreviewRecord>(fs, ContractJson.Options, ct);
        }
        catch (JsonException)
        {
            return null; // 깨진 파일은 없는 것으로 본다
        }
    }

    static void Validate(RowPageQuery q)
    {
        if (q.Offset < 0) throw new ArgumentOutOfRangeException(nameof(q), "offset은 0 이상이어야 합니다.");
        if (q.Limit is < 1 or > RowPageQuery.MaxLimit) throw new ArgumentOutOfRangeException(nameof(q), $"limit은 1..{RowPageQuery.MaxLimit}이어야 합니다.");
    }

    static RowPage<T> Page<T>(IEnumerable<T> rows, RowView view, RowPageQuery q)
    {
        var all = rows as IReadOnlyCollection<T> ?? rows.ToList();
        return new RowPage<T> { Total = all.Count, View = view, Offset = q.Offset, Limit = q.Limit, Rows = all.Skip(q.Offset).Take(q.Limit).ToList() };
    }

    string PathOf(string id) => Path.Combine(Directory, id + FileSuffix);

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* 다른 프로세스가 열고 있으면 다음 저장에서 다시 지운다 */ }
        catch (UnauthorizedAccessException) { }
    }
}
