using System.Text.RegularExpressions;
using DbRipple.Core.Capture;

namespace DbRipple.Core.Catalog;

public sealed record CatalogColumn(
    int ObjectId, int ColumnId, string Name, string SystemTypeName, bool IsUserClrType,
    bool IsNullable, bool IsComputed, bool IsIdentity, bool IsRowVersion, string? DefaultDefinition)
{
    // SEQUENCE/GETDATE/NEWID 류 기본값
    static readonly Regex Nondeterministic = new(
        @"NEXT\s+VALUE\s+FOR|GETDATE|GETUTCDATE|SYSDATETIME|SYSUTCDATETIME|SYSDATETIMEOFFSET|CURRENT_TIMESTAMP|NEWID|NEWSEQUENTIALID|RAND\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool HasNondeterministicDefault => DefaultDefinition is not null && Nondeterministic.IsMatch(DefaultDefinition);
}

public sealed record CatalogIndexColumn(int ObjectId, int IndexId, string IndexName, bool IsPrimaryKey, bool IsUnique, int KeyOrdinal, string Column, bool IsNullable);

public sealed record CatalogForeignKeyColumn(
    int ObjectId, string Name, int ParentObjectId, int ReferencedObjectId,
    string DeleteAction, string UpdateAction, bool IsDisabled, bool IsNotTrusted,
    int Ordinal, string ParentColumn, string ReferencedColumn);

/// <summary>트리거 1개 × 이벤트 1개. Definition이 NULL이면 암호화(또는 CLR).</summary>
public sealed record CatalogTriggerEvent(
    int ObjectId, string Name, int ParentId, string ParentClass, bool IsInsteadOf, bool IsDisabled,
    string Event, bool IsFirst, bool IsLast, string? Definition)
{
    public bool IsEncrypted => Definition is null;
}

/// <summary>Owner는 테이블의 실효 소유자(개체 소유자 없으면 스키마 소유자) DB 사용자 이름.</summary>
public sealed record CatalogTable(int ObjectId, string Schema, string Name, TemporalType Temporal, bool IsMemoryOptimized, string Owner = "dbo");

public sealed record CatalogFile(int FileId, string LogicalName, string TypeDesc, string PhysicalName);

/// <summary>C7 카탈로그 스냅샷.</summary>
public sealed record DbCatalog(
    IReadOnlyList<CatalogTable> Tables,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<CatalogIndexColumn> KeyIndexes,
    IReadOnlyList<CatalogForeignKeyColumn> ForeignKeys,
    IReadOnlyList<CatalogTriggerEvent> Triggers,
    IReadOnlyList<CatalogFile> Files)
{
    /// <summary>C9 입력으로 바꾼다.</summary>
    public IReadOnlyList<TableInfo> ToCaptureInput() => Tables.Select(t =>
    {
        var cols = Columns.Where(c => c.ObjectId == t.ObjectId).OrderBy(c => c.ColumnId)
            .Select(c => new ColumnInfo(c.Name, c.SystemTypeName, c.IsComputed, c.IsIdentity, c.IsRowVersion, c.IsUserClrType, c.HasNondeterministicDefault))
            .ToList();
        var idx = KeyIndexes.Where(i => i.ObjectId == t.ObjectId).GroupBy(i => i.IndexId).OrderBy(g => g.Key).ToList();
        var pk = idx.FirstOrDefault(g => g.First().IsPrimaryKey)?.OrderBy(i => i.KeyOrdinal).Select(i => i.Column).ToList();
        var uq = idx.Where(g => !g.First().IsPrimaryKey && g.First().IsUnique && g.All(i => !i.IsNullable))
            .Select(g => (IReadOnlyList<string>)g.OrderBy(i => i.KeyOrdinal).Select(i => i.Column).ToList()).ToList();
        var after = Triggers.Where(tr => tr.ParentId == t.ObjectId && !tr.IsInsteadOf).ToList();
        return new TableInfo(t.Schema, t.Name, t.ObjectId, cols, pk, uq, t.Temporal, t.IsMemoryOptimized,
            after.Where(x => x.IsFirst).Select(x => ToOp(x.Event)).OfType<DmlOperation>().Distinct().ToList(),
            after.Where(x => x.IsLast).Select(x => ToOp(x.Event)).OfType<DmlOperation>().Distinct().ToList(),
            t.Owner);
    }).ToList();

    static DmlOperation? ToOp(string ev) => ev.ToUpperInvariant() switch
    {
        "INSERT" => DmlOperation.Insert,
        "UPDATE" => DmlOperation.Update,
        "DELETE" => DmlOperation.Delete,
        _ => null,
    };
}
