using System.Text.RegularExpressions;
using DbRipple.Core.Capture;

namespace DbRipple.Core.Catalog;

public sealed record CatalogColumn(
    int ObjectId, int ColumnId, string Name, string SystemTypeName, bool IsUserClrType,
    bool IsNullable, bool IsComputed, bool IsIdentity, bool IsRowVersion, string? DefaultDefinition)
{
    // SEQUENCE/GETDATE/NEWID 류 기본값, 그리고 실행 환경(로그인·서버·DB)에 따라 달라지는 기본값(2026-11-13 Split: 샌드박스와 원본에서 값이 다름)
    static readonly Regex Nondeterministic = new(
        @"NEXT\s+VALUE\s+FOR|GETDATE|GETUTCDATE|SYSDATETIME|SYSUTCDATETIME|SYSDATETIMEOFFSET|CURRENT_TIMESTAMP|NEWID|NEWSEQUENTIALID|RAND\s*\(" +
        @"|\b(SUSER_SNAME|SUSER_NAME|SUSER_ID|SUSER_SID|ORIGINAL_LOGIN|HOST_NAME|HOST_ID|APP_NAME|DB_NAME|DB_ID|USER_NAME|USER_ID|SESSION_CONTEXT|CONTEXT_INFO)\s*\(" +
        @"|\b(CURRENT_USER|SESSION_USER|SYSTEM_USER)\b|@@(SERVERNAME|SERVICENAME|SPID)\b",
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

/// <summary>
/// 저장 프로시저 모듈 1개(sys.procedures + sys.sql_modules, 2주차 인계 1). Definition이 NULL이면 암호화·CLR·확장 프로시저라 본문을 펼칠 수 없다.
/// 2026-11-11 추가(비파괴). C6 TriggerAnalyzer가 트리거가 부르는 프로시저의 쓰기 대상을 펼칠 때 쓴다.
/// </summary>
public sealed record CatalogModule(int ObjectId, string Schema, string Name, string? Definition)
{
    public bool IsEncrypted => Definition is null;
}

/// <summary>C7 카탈로그 스냅샷.</summary>
public sealed record DbCatalog(
    IReadOnlyList<CatalogTable> Tables,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<CatalogIndexColumn> KeyIndexes,
    IReadOnlyList<CatalogForeignKeyColumn> ForeignKeys,
    IReadOnlyList<CatalogTriggerEvent> Triggers,
    IReadOnlyList<CatalogFile> Files,
    IReadOnlyList<CatalogModule>? Procedures = null)
{
    /// <summary>프로시저 모듈(인계 1). 생성자에서 생략했으면(3주차 이전 호출자) 빈 목록 = "펼칠 본문 없음"이다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<CatalogModule> ProcedureModules => Procedures ?? [];

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
