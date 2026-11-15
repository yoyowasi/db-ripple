using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbRipple.Core.Capture;

/// <summary>수집 범위 매니페스트(§6.2).</summary>
public sealed record CaptureManifest(
    string Version,
    IReadOnlyList<ManifestTable> Tables,
    IReadOnlyList<ExcludedTable> ExcludedTables,
    IReadOnlyList<string> Notes)
{
    public const string Last = "LAST";
    public const string None = "NONE";

    /// <summary>직접 DELETE가 0행이어도 발동 헤더를 기록한다. 예전 매니페스트는 이 보장이 없다.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool RecordsZeroRowDirectDelete { get; init; }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public sealed record ManifestTable(
    string Table,
    bool Covered,
    AuditOrderInfo AuditOrder,
    IReadOnlyList<string> UnobservableColumns,
    IReadOnlyList<VolatileColumn> VolatileColumns);

public sealed record AuditOrderInfo(string Insert, string Update, string Delete);

public sealed record VolatileColumn(string Column, string Reason);

public sealed record ExcludedTable(string Table, string Reason);
