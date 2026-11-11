using DbRipple.Core.Capture;

namespace DbRipple.Contracts;

// GET /api/catalog/schema 계약(§4 C1 자동완성, §6.4 "자동완성용 테이블·열 목록", Q23)과 열 sqlType(§6.1 "열 메타데이터에 sqlType을 함께 둔다", Q25).
// 2026-11-11 3주차 추가(비파괴). 행 값(RowChange·NetChange)에는 형식을 싣지 않고, 프런트가 이 응답을 캐시해 테이블·열 이름으로 찾는다(Q25 결정 제안).

/// <summary>자동완성·그리드용 스키마(GET /api/catalog/schema 200 본문, Q23). 현재 기준선 샌드박스 카탈로그에서 만든다(원본과 지문이 같으므로 같은 스키마로 본다).</summary>
public sealed record CatalogSchema
{
    /// <summary>원본 역할 DB 이름(사용자가 쓰는 대상, PreviewResult.baseline.originDb).</summary>
    public required string Database { get; init; }
    /// <summary>기준 시각(백업 완료). 프런트 캐시 무효화 키로 쓴다.</summary>
    public required DateTimeOffset BaselineAt { get; init; }
    /// <summary>매니페스트 버전.</summary>
    public required string ManifestVersion { get; init; }
    /// <summary>사용자 테이블("스키마.테이블" 순). 감사 저장소(audit.*)는 넣지 않는다.</summary>
    public IReadOnlyList<SchemaTable> Tables { get; init; } = [];
}

/// <summary>테이블 하나(Q23).</summary>
public sealed record SchemaTable
{
    /// <summary>스키마 이름(자동완성 "스키마." 다음 후보).</summary>
    public required string Schema { get; init; }
    /// <summary>테이블 이름.</summary>
    public required string Name { get; init; }
    /// <summary>"스키마.테이블"(결과 DTO의 table 필드와 같은 표시형).</summary>
    public required string Table { get; init; }
    /// <summary>행 식별 키 종류(Core TableKey.Choose: PK → NOT NULL 유일 인덱스 → ROW). 그리드 키 열 고정에 쓴다.</summary>
    public required KeyKind KeyKind { get; init; }
    /// <summary>키 열(키 순서). ROW면 빈 배열.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
    /// <summary>감사 트리거를 깔았는지(매니페스트 covered). false면 최종 차이로만 수집한다(E17).</summary>
    public required bool Covered { get; init; }
    /// <summary>제외 사유(매니페스트 excludedTables.reason). 덮었으면 null.</summary>
    public string? ExcludedReason { get; init; }
    /// <summary>열(column_id 순).</summary>
    public IReadOnlyList<SchemaColumn> Columns { get; init; } = [];
}

/// <summary>열 하나(Q23, Q25).</summary>
public sealed record SchemaColumn
{
    /// <summary>열 이름.</summary>
    public required string Name { get; init; }
    /// <summary>
    /// 선언 형식 문자열(§6.1, Q25): SqlTypes.Format 결과(예 "decimal(19,4)", "nvarchar(50)", "nvarchar(max)", "datetime2(7)", "hierarchyid").
    /// 길이·정밀도를 읽지 못하면 형식 이름만 둔다(예 "decimal"). 프런트는 괄호 유무 둘 다 받아야 한다.
    /// </summary>
    public required string SqlType { get; init; }
    /// <summary>NULL 허용.</summary>
    public required bool IsNullable { get; init; }
    /// <summary>IDENTITY(E2, 그리드 "예상값").</summary>
    public bool IsIdentity { get; init; }
    /// <summary>계산 열(E4, 그리드 "파생").</summary>
    public bool IsComputed { get; init; }
    /// <summary>rowversion(E4).</summary>
    public bool IsRowVersion { get; init; }
    /// <summary>
    /// 매니페스트 volatileColumns의 사유를 계약 열거형으로 옮긴 값(IDENTITY, NONDETERMINISTIC_DEFAULT, ROWVERSION). 매니페스트의 "COMPUTED"는 IsComputed로 옮기고 여기는 null(Q7). 없으면 null.
    /// </summary>
    public VolatileReason? Volatile { get; init; }
    /// <summary>행 이미지에서 뺀 열(매니페스트 unobservableColumns: text/ntext/image, JSON 키 충돌, §6.1). 최종 차이에서만 값이 나온다.</summary>
    public bool Unobservable { get; init; }
}

/// <summary>
/// GET /api/catalog/schema 공급자(C7 결과를 자동완성 형식으로, Q23). Preview 프로젝트가 구현한다(ISandboxManager 문맥 + 길이·정밀도 조회, 기준선마다 캐시).
/// </summary>
public interface ICatalogSchemaProvider
{
    /// <summary>현재 기준선 스키마. 기준선이 없거나 building이면 PreviewConflictException(API 409).</summary>
    Task<CatalogSchema> GetSchemaAsync(CancellationToken ct);
}

/// <summary>SQL 선언 형식 문자열(Q25). sys.columns의 max_length(바이트, −1 = max)·precision·scale로 만든다.</summary>
public static class SqlTypes
{
    /// <summary>
    /// typeName(Core CatalogColumn.SystemTypeName: 시스템 형식명, CLR이면 형식명)과 sys.columns 값으로 선언형을 만든다.
    /// 길이형(char, varchar, binary, varbinary, nchar, nvarchar): "(n)" 또는 "(max)", n은 nchar·nvarchar면 max_length/2.
    /// decimal·numeric: "(p,s)". datetime2·datetimeoffset·time: "(scale)". 그 밖(int, money, float, xml, hierarchyid 등): 이름만.
    /// maxLength·precision·scale이 null이면 이름만 돌려준다.
    /// </summary>
    public static string Format(string typeName, int? maxLength, int? precision, int? scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        var name = typeName.ToLowerInvariant();
        switch (name)
        {
            case "char" or "varchar" or "binary" or "varbinary" when maxLength is int len:
                return len < 0 ? $"{name}(max)" : $"{name}({len})";
            case "nchar" or "nvarchar" when maxLength is int nlen:
                return nlen < 0 ? $"{name}(max)" : $"{name}({nlen / 2})";
            case "decimal" or "numeric" when precision is int p && scale is int s:
                return $"{name}({p},{s})";
            case "datetime2" or "datetimeoffset" or "time" when scale is int fs:
                return $"{name}({fs})";
            default:
                return typeName;
        }
    }
}
