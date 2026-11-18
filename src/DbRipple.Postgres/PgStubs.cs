using DbRipple.Contracts;
using DbRipple.Preview;

namespace DbRipple.Postgres;

/// <summary>
/// 열 형식 재료(PostgreSQL). 카탈로그의 형식 이름이 이미 format_type() 결과(예 "character varying(50)", "numeric(10,2)")라
/// 길이·정밀도를 따로 읽지 않는다. 빈 사전이면 공급자가 형식 이름을 그대로 쓴다.
/// </summary>
public sealed class PgColumnTypeSource : IColumnTypeSource
{
    public Task<IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>> ReadAsync(string sandboxDb, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<(int ObjectId, int ColumnId), ColumnTypeShape>>(new Dictionary<(int, int), ColumnTypeShape>());
}

/// <summary>PostgreSQL 최소판은 [적용]이 없다(API 501). 화면은 SandboxStatus.Engine으로 단추를 미리 막는다.</summary>
public sealed class PgApplyNotSupported : IApplyService
{
    public Task<ApplyResult?> ApplyAsync(string previewId, ApplyRequest request, CancellationToken ct) =>
        throw new NotImplementedException("PostgreSQL 최소판은 [적용]을 지원하지 않습니다.");
}
