using DbRipple.Core.Sql;

namespace DbRipple.Core.Capture;

public enum DmlOperation { Insert, Update, Delete }

public enum TemporalType { None = 0, History = 1, SystemVersioned = 2 }

public enum KeyKind { PK, UNIQUE, ROW }

/// <summary>감사 대상 열. TypeName은 시스템 형식명(CLR UDT면 UDT 이름).</summary>
public sealed record ColumnInfo(
    string Name,
    string TypeName,
    bool IsComputed = false,
    bool IsIdentity = false,
    bool IsRowVersion = false,
    bool IsUserClrType = false,
    bool HasNondeterministicDefault = false);

/// <summary>C9 입력 테이블. UserFirst/UserLast는 기존 사용자 AFTER 트리거가 점유한 연산. Owner는 실효 소유자(감사 트리거도 이 소유자 것).</summary>
public sealed record TableInfo(
    string Schema,
    string Name,
    int ObjectId,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string>? PrimaryKey = null,
    IReadOnlyList<IReadOnlyList<string>>? UniqueNotNullKeys = null,
    TemporalType Temporal = TemporalType.None,
    bool IsMemoryOptimized = false,
    IReadOnlyCollection<DmlOperation>? UserFirst = null,
    IReadOnlyCollection<DmlOperation>? UserLast = null,
    string Owner = "dbo")
{
    public string QuotedName => SqlText.Quote(Schema, Name);
    public string DisplayName => Schema + "." + Name;
    public bool HasUserLast(DmlOperation op) => UserLast?.Contains(op) == true;
}

/// <summary>행 식별 키: PK, 없으면 NOT NULL 유일 인덱스, 없으면 ROW.</summary>
public sealed record TableKey(KeyKind Kind, IReadOnlyList<string> Columns)
{
    public static TableKey Choose(TableInfo t)
    {
        if (t.PrimaryKey is { Count: > 0 } pk) return new(KeyKind.PK, pk);
        var uq = t.UniqueNotNullKeys?.FirstOrDefault(k => k.Count > 0);
        return uq is not null ? new(KeyKind.UNIQUE, uq) : new(KeyKind.ROW, Array.Empty<string>());
    }
}

public sealed record SqlBatch(string Label, string Sql);

public sealed record InstallPlan(IReadOnlyList<SqlBatch> Batches, CaptureManifest Manifest);
