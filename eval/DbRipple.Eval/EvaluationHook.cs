using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>
/// 평가 훅(§10.3): 제품 실행기(C10)가 모든 문장·REVERT 뒤, ROLLBACK 전에 같은 연결로 부른다(exec-1과 확인: 실패 문장이 있으면 부르지 않음, 훅 예외는 ROLLBACK 뒤 다시 던짐).
/// 자기 해시 비교기로 기준 스냅샷(before) 대 샌드박스(after, 미커밋)의 순 변경을 기록한다(E_obs·E_cap 기준).
/// 감사 스키마(context.AuditSchema)만 뺀다. 매니페스트 notes에 나온 테이블(설치·실행 사용자 DDL이 쓴 dbo.DatabaseLog 등)도 비교한다:
/// 기준 스냅샷은 설치 뒤에 만들므로 그 행은 차이에 나오지 않고, 미리보기 중 그 테이블이 바뀌면 진짜 변경이다(2026-11-10 검토). 읽기만 하고 트랜잭션·세션 옵션은 건드리지 않는다.
/// 한 인스턴스는 미리보기 한 건용이다(사례마다 새로 만든다).
/// </summary>
public sealed class EvaluationHook(HashSnapshotComparer comparer) : IEvaluationHook
{
    /// <summary>비교기.</summary>
    public HashSnapshotComparer Comparer { get; } = comparer;

    /// <summary>호출 횟수(정상은 0 또는 1).</summary>
    public int Calls { get; private set; }

    /// <summary>마지막 호출에서 기록한 샌드박스 순 변경. 호출되지 않았으면 null.</summary>
    public IReadOnlyList<OracleRowChange>? LastSandboxChanges { get; private set; }

    /// <summary>마지막 호출의 비교기 점검 문제.</summary>
    public IReadOnlyList<string> LastIssues { get; private set; } = [];

    /// <summary>마지막 호출의 정정 기록 후보(인계 6, HashComparison.Notes).</summary>
    public IReadOnlyList<string> LastNotes { get; private set; } = [];

    /// <summary>마지막 호출에서 뺀 테이블과 사유.</summary>
    public IReadOnlyList<string> LastExcluded { get; private set; } = [];

    /// <summary>마지막 호출에서 비교한 테이블(지표의 비결정 열 판정에 쓴다).</summary>
    public IReadOnlyList<OracleTable> LastTables { get; private set; } = [];

    /// <inheritdoc />
    public async Task OnBeforeRollbackAsync(EvaluationHookContext context, CancellationToken ct)
    {
        Calls++;
        var (tables, excluded) = await Comparer.LoadTablesAsync(context.Session, context.SandboxDb,
            [context.AuditSchema], [], ct);
        var before = await Comparer.TakeAsync(context.Session, context.SnapshotDb, tables, ct);
        var after = await Comparer.TakeAsync(context.Session, context.SandboxDb, tables, ct);
        var cmp = Comparer.Compare(before, after);
        LastTables = tables;
        LastExcluded = excluded;
        LastIssues = cmp.Issues;
        LastNotes = cmp.Notes;
        LastSandboxChanges = cmp.Changes;
    }
}
