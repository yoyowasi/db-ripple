using DbRipple.Contracts;
using Microsoft.Data.SqlClient;

namespace DbRipple.Execution;

/// <summary>
/// 문장 오류 판정(열린 질문 Q27, D3 규칙 3, §5.2 4, E13). 규칙은 C10 테스트(ErrorRuleTests)의 실측으로 정했다.
/// <list type="number">
/// <item>대표 오류: SqlException.Errors 가운데 심각도 11 이상인 첫 오류(3621 "문장 종료" 같은 정보성 오류는 뺀다). 없으면 첫 오류.</item>
/// <item>isToolFault: 오류 가운데 프로시저가 감사 트리거(ErrorClassification.IsToolFault)인 것이 있으면 true이고 그 오류를 대표로 쓴다. 상태는 failed.</item>
/// <item>trigger_blocked: (a) 3609(트리거 안에서 트랜잭션이 끝남, 트리거의 ROLLBACK) 이 있거나,
/// (b) 대표 오류의 프로시저가 사용자 DML 트리거이고 번호가 50000 이상(RAISERROR·THROW로 트리거가 스스로 거부)이다.</item>
/// <item>그 밖(엔진 오류: 제약 위반 547, 중첩 초과 217, 사용자 트리거 본문의 엔진 오류 포함)은 failed.</item>
/// <item>롤백 뒤 보정(호출자, PreviewExecutor): trigger_blocked인데 롤백 뒤 남은 변경이 있고 EndedByRollback이 false면 failed(3609 대표 오류가 아니어도, 예 COMMIT 뒤 RAISERROR, 인계 5).</item>
/// </list>
/// </summary>
public static class ErrorRules
{
    /// <summary>트리거 안에서 트랜잭션이 끝남(A22).</summary>
    public const int TransactionEndedInTrigger = 3609;

    /// <summary>사용자 정의 오류 번호 하한(RAISERROR 메시지 문자열은 50000, THROW는 50000 이상).</summary>
    public const int UserErrorMin = 50000;

    /// <summary>오류를 문장 상태와 StatementError로 바꾼다.</summary>
    public static (StatementStatus Status, StatementError Error) Classify(SqlException ex, BaselineContext baseline)
    {
        var errors = ex.Errors.Cast<SqlError>().Select(e => new ErrorInfo(e.Number, e.Class, e.Message, NullIfEmpty(e.Procedure))).ToList();
        var userTriggers = baseline.Catalog.Triggers
            .Where(t => t.ParentClass != "DATABASE" && !t.Name.StartsWith(Core.Capture.AuditTriggerGenerator.TriggerPrefix, StringComparison.Ordinal))
            .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Classify(errors, userTriggers);
    }

    /// <summary>순수 판정(단위 테스트용). userTriggerNames는 스키마 없는 사용자 DML 트리거 이름.</summary>
    public static (StatementStatus Status, StatementError Error) Classify(IReadOnlyList<ErrorInfo> errors, IReadOnlySet<string> userTriggerNames)
    {
        if (errors.Count == 0) throw new ArgumentException("오류가 없음", nameof(errors));
        var tool = errors.FirstOrDefault(e => ErrorClassification.IsToolFault(e.Procedure));
        var main = tool ?? errors.FirstOrDefault(e => e.Class >= 11) ?? errors[0];
        var status = StatementStatus.Failed;
        if (tool is null)
        {
            var blockedByRollback = errors.Any(e => e.Number == TransactionEndedInTrigger);
            var raisedByTrigger = main.Number >= UserErrorMin && main.Procedure is not null && userTriggerNames.Contains(LastPart(main.Procedure));
            if (blockedByRollback || raisedByTrigger) status = StatementStatus.TriggerBlocked;
        }
        return (status, new StatementError
        {
            ErrorNumber = main.Number,
            Message = main.Message,
            Procedure = main.Procedure,
            IsToolFault = tool is not null,
        });
    }

    /// <summary>
    /// 3609(트리거 안에서 트랜잭션이 끝남)가 ROLLBACK으로 났다고 볼 수 있는지(트리거 정적 분석으로). COMMIT으로 났으면 문장 변경이 확정된 것이라 trigger_blocked가 아니다.
    /// <list type="bullet">
    /// <item>후보 트리거: 문장 대상 테이블·연산에 붙은 활성 사용자 트리거 + 그 문장이 닿을 수 있는 테이블(FK 참조 동작 CASCADE·SET NULL·SET DEFAULT의 자식, 후보 트리거의 정적 쓰기 대상,
    /// 이를 되풀이)에 붙은 활성 사용자 트리거 전부(이벤트 무관). 오류 프로시저가 있으면 그 이름의 트리거도 더한다(실측: 3609의 SqlError.Procedure는 비어 있다).
    /// 3주차 인계 5: 연쇄 대상 테이블의 ROLLBACK 트리거(CATCH에서 자동 커밋 로그를 씀)가 대상 테이블 트리거만 보던 규칙에서 빠져 failed로 나왔다.</item>
    /// <item>후보 가운데 ROLLBACK이 있는 트리거가 있고, COMMIT·BEGIN TRAN·SAVE TRAN이 있거나 쓰기 대상을 알 수 없는 트리거(펼치지 못한 프로시저 호출 포함)가 없을 때만 true(닫힌 쪽).</item>
    /// <item>후보를 넓히면 "모름" 쪽(false)으로만 기우는 것은 아니다(ROLLBACK 트리거가 더 들어올 수 있음). 그래도 COMMIT 트리거가 하나라도 후보에 있으면 false라, 확정된 변경을 trigger_blocked로 덮는 쪽보다 failed로 보이는 쪽으로 닫힌다.</item>
    /// </list>
    /// </summary>
    public static bool EndedByRollback(string? procedure, string target, ChangeOperation kind, BaselineContext baseline)
    {
        var candidates = CascadeTriggers(target, kind, baseline);
        if (!string.IsNullOrEmpty(procedure))
            candidates.AddRange(baseline.Triggers.Where(t => !t.IsAuditTrigger && string.Equals(LastPart(t.Trigger), LastPart(procedure), StringComparison.OrdinalIgnoreCase)));
        candidates = candidates.Distinct().ToList();
        return candidates.Any(t => t.HasRollback) && !candidates.Any(t => t.HasTransactionControl || t.WriteTargetsUnknown || UnresolvedProcedureCalls(t).Count > 0);
    }

    /// <summary>
    /// 문장 하나가 발동시킬 수 있는 활성 사용자 트리거(정적 상한). 첫 테이블은 연산이 맞는 트리거만, 그 뒤 테이블은 이벤트를 가리지 않는다(연쇄 연산을 정적으로 다 알 수 없음).
    /// 테이블 간 이동: FK 참조 동작(부모 → 자식, NO_ACTION 제외, 비활성 FK 제외)과 트리거 정적 쓰기 대상. 비활성 트리거는 발동하지 않으므로 뺀다(E22).
    /// </summary>
    public static List<TriggerStaticInfo> CascadeTriggers(string target, ChangeOperation kind, BaselineContext baseline)
    {
        var tablesById = baseline.Catalog.Tables.ToDictionary(t => t.ObjectId);
        var idByName = baseline.Catalog.Tables.GroupBy(t => t.Schema + "." + t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ObjectId, StringComparer.OrdinalIgnoreCase);
        var children = baseline.Catalog.ForeignKeys
            .Where(f => !f.IsDisabled && (IsAction(f.DeleteAction) || IsAction(f.UpdateAction)))
            .GroupBy(f => f.ReferencedObjectId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ParentObjectId).Distinct().ToList());
        var active = baseline.Triggers.Where(t => !t.IsDisabled && !t.IsAuditTrigger).ToList();

        var result = new List<TriggerStaticInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Table, bool First)>();
        queue.Enqueue((target, true));
        while (queue.Count > 0)
        {
            var (table, first) = queue.Dequeue();
            if (!seen.Add(table)) continue;
            var fired = active.Where(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase) && (!first || t.Events.Contains(kind))).ToList();
            result.AddRange(fired);
            foreach (var w in fired.SelectMany(t => t.WriteTargets)) queue.Enqueue((w, false));
            if (idByName.TryGetValue(table, out var id) && children.TryGetValue(id, out var kids))
                foreach (var k in kids)
                    if (tablesById.TryGetValue(k, out var child)) queue.Enqueue((child.Schema + "." + child.Name, false));
        }
        return result;
    }

    /// <summary>
    /// 펼치지 못한 프로시저 호출(인계 1). 호환(Q38): ProcedureCalls가 있는데 ExpandedProcedureCalls·UnresolvedProcedureCalls가 모두 비면(펼치기 전 분석기) 전부 미상이다.
    /// Attribution CatalogIndex.UnresolvedProcedureCalls와 같은 식이다(프로젝트 참조가 없어 따로 둔다).
    /// </summary>
    public static IReadOnlyList<string> UnresolvedProcedureCalls(TriggerStaticInfo t) =>
        t.ExpandedProcedureCalls.Count == 0 && t.UnresolvedProcedureCalls.Count == 0 ? t.ProcedureCalls : t.UnresolvedProcedureCalls;

    static bool IsAction(string action) => action.ToUpperInvariant() is "CASCADE" or "SET_NULL" or "SET_DEFAULT";

    static string LastPart(string procedure)
    {
        var dot = procedure.LastIndexOf('.');
        return (dot >= 0 ? procedure[(dot + 1)..] : procedure).Trim('[', ']');
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>SqlError에서 판정에 쓰는 값.</summary>
public sealed record ErrorInfo(int Number, byte Class, string Message, string? Procedure);
