using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using Microsoft.Data.SqlClient;

namespace DbRipple.Preview;

/// <summary>sys.columns의 길이·정밀도·소수 자릿수(sqlType 재료). max_length는 바이트, -1 = max.</summary>
public readonly record struct ColumnTypeShape(int MaxLength, int Precision, int Scale);

/// <summary>열 형식 재료 공급원(Q37). 운영 구현은 샌드박스 DB의 sys.columns를 읽고, 테스트는 가짜로 바꾼다.</summary>
public interface IColumnTypeSource
{
    /// <summary>(object_id, column_id)별 형식 재료. 읽지 못하면 예외(공급자가 형식 이름만 두는 쪽으로 낮춘다).</summary>
    Task<IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>> ReadAsync(string sandboxDb, CancellationToken ct);
}

/// <summary>샌드박스 DB의 sys.columns를 읽는 운영 공급원. 연결 문자열은 비밀값이라 메시지·로그에 넣지 않는다.</summary>
public sealed class SqlColumnTypeSource(string connectionString) : IColumnTypeSource
{
    const string Query = """
        SELECT c.object_id, c.column_id, c.max_length, c.precision, c.scale
        FROM sys.columns AS c
        JOIN sys.tables AS t ON t.object_id = c.object_id
        WHERE t.is_ms_shipped = 0;
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>> ReadAsync(string sandboxDb, CancellationToken ct)
    {
        var cs = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = sandboxDb, Pooling = false }.ConnectionString;
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Query, conn) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var map = new Dictionary<(int, int), ColumnTypeShape>();
        while (await r.ReadAsync(ct))
            map[(r.GetInt32(0), r.GetInt32(1))] = new ColumnTypeShape(r.GetInt16(2), r.GetByte(3), r.GetByte(4));
        return map;
    }
}

/// <summary>
/// GET /api/catalog/schema 공급자(AD8, Q23·Q37). 입력은 ISandboxManager 문맥(샌드박스 카탈로그 + 매니페스트)과 IColumnTypeSource의 길이·정밀도다.
/// <list type="bullet">
/// <item>sqlType은 Contracts <see cref="SqlTypes.Format"/>으로 만든다. 형식 재료를 읽지 못하면 형식 이름만 두고(Q37) 그 결과는 캐시하지 않는다(다음 호출에서 다시 시도).</item>
/// <item>기준선(BaselineAt + 매니페스트 버전)마다 한 번 만들어 캐시한다. 재구축하면 BaselineAt이 바뀌어 새로 만든다.</item>
/// <item>covered·excludedReason·volatile·unobservable은 매니페스트에서, 계산 열은 카탈로그 IsComputed로 옮긴다(매니페스트 "COMPUTED"는 volatile이 아니다, Q7). 감사 저장소(audit.*)는 뺀다.</item>
/// <item>기준선이 없거나 구축 중이면 ISandboxManager.GetContextAsync가 던지는 PreviewConflictException(409)을 그대로 올린다.</item>
/// </list>
/// </summary>
public sealed class CatalogSchemaProvider(ISandboxManager sandbox, IColumnTypeSource columnTypes) : ICatalogSchemaProvider
{
    readonly object gate = new();
    (DateTimeOffset BaselineAt, string ManifestVersion, CatalogSchema Schema)? cache;

    /// <summary>운영 조립용: 연결 문자열로 샌드박스 sys.columns를 읽는다.</summary>
    public CatalogSchemaProvider(ISandboxManager sandbox, string connectionString) : this(sandbox, new SqlColumnTypeSource(connectionString)) { }

    /// <inheritdoc />
    public async Task<CatalogSchema> GetSchemaAsync(CancellationToken ct)
    {
        var baseline = await sandbox.GetContextAsync(ct);
        var version = baseline.Manifest.Version;
        lock (gate)
        {
            if (cache is { } c && c.BaselineAt == baseline.BackupFinishedAt && c.ManifestVersion == version) return c.Schema;
        }

        IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>? shapes = null;
        try { shapes = await columnTypes.ReadAsync(baseline.SandboxDb, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* Q37: 형식 이름만 둔다 */ }

        var schema = Build(baseline, shapes);
        if (shapes is not null)
            lock (gate) cache = (baseline.BackupFinishedAt, version, schema);
        return schema;
    }

    /// <summary>카탈로그·매니페스트·형식 재료로 스키마를 만든다(공개: 단위 시험용). shapes가 null이면 형식 이름만 둔다.</summary>
    public static CatalogSchema Build(BaselineContext baseline, IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>? shapes)
    {
        var catalog = baseline.Catalog;
        var manifest = baseline.Manifest;
        var manifestTables = manifest.Tables.ToDictionary(t => t.Table, StringComparer.Ordinal);
        var excluded = manifest.ExcludedTables.ToDictionary(t => t.Table, t => t.Reason, StringComparer.Ordinal);
        var columnsByTable = catalog.Columns.GroupBy(c => c.ObjectId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.ColumnId).ToList());
        var capture = catalog.ToCaptureInput().ToDictionary(t => t.ObjectId);

        var tables = new List<SchemaTable>();
        foreach (var t in catalog.Tables.OrderBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var info = capture[t.ObjectId];
            if (AuditTriggerGenerator.IsCaptureStore(info)) continue;
            var display = info.DisplayName;
            manifestTables.TryGetValue(display, out var mt);
            var key = TableKey.Choose(info);
            var unobservable = mt?.UnobservableColumns.ToHashSet(StringComparer.Ordinal);
            var volatileBy = mt?.VolatileColumns.ToDictionary(v => v.Column, v => v.Reason, StringComparer.Ordinal);
            var columns = (columnsByTable.GetValueOrDefault(t.ObjectId) ?? []).Select(c =>
            {
                string? reason = null;
                volatileBy?.TryGetValue(c.Name, out reason);
                var sqlType = shapes is not null && shapes.TryGetValue((c.ObjectId, c.ColumnId), out var s)
                    ? SqlTypes.Format(c.SystemTypeName, s.MaxLength, s.Precision, s.Scale)
                    : c.SystemTypeName;
                return new SchemaColumn
                {
                    Name = c.Name,
                    SqlType = sqlType,
                    IsNullable = c.IsNullable,
                    IsIdentity = c.IsIdentity,
                    IsComputed = c.IsComputed,
                    IsRowVersion = c.IsRowVersion,
                    Volatile = MapVolatile(reason),
                    Unobservable = unobservable?.Contains(c.Name) == true,
                };
            }).ToList();
            tables.Add(new SchemaTable
            {
                Schema = t.Schema,
                Name = t.Name,
                Table = display,
                KeyKind = key.Kind,
                KeyColumns = key.Columns,
                Covered = mt is { Covered: true },
                ExcludedReason = mt is { Covered: true } ? null : excluded.GetValueOrDefault(display),
                Columns = columns,
            });
        }

        return new CatalogSchema
        {
            Database = baseline.OriginDb,
            BaselineAt = baseline.BackupFinishedAt,
            ManifestVersion = manifest.Version,
            Tables = tables,
        };
    }

    // 매니페스트 사유 문자열 → 계약 열거형. "COMPUTED"는 IsComputed로 옮기므로 null.
    static VolatileReason? MapVolatile(string? reason) => reason switch
    {
        "IDENTITY" => VolatileReason.Identity,
        "ROWVERSION" => VolatileReason.Rowversion,
        "NONDETERMINISTIC_DEFAULT" => VolatileReason.NondeterministicDefault,
        "SEQUENCE_DEFAULT" => VolatileReason.SequenceDefault,
        _ => null,
    };
}
