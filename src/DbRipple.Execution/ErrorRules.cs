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
    /// 후보 트리거: 오류 프로시저가 있으면 그 이름의 트리거, 없으면(실측: 3609의 SqlError.Procedure는 비어 있다) 문장 대상 테이블·연산에 붙은 활성 사용자 트리거.
    /// 후보 가운데 ROLLBACK이 있는 트리거가 있고, COMMIT·BEGIN TRAN·SAVE TRAN이 있거나 쓰기 대상을 알 수 없는 트리거가 없을 때만 true(닫힌 쪽).
    /// </summary>
    public static bool EndedByRollback(string? procedure, string target, ChangeOperation kind, BaselineContext baseline)
    {
        var candidates = (string.IsNullOrEmpty(procedure)
            ? baseline.Triggers.Where(t => !t.IsDisabled && string.Equals(t.Table, target, StringComparison.OrdinalIgnoreCase) && t.Events.Contains(kind))
            : baseline.Triggers.Where(t => string.Equals(LastPart(t.Trigger), LastPart(procedure), StringComparison.OrdinalIgnoreCase)))
            .Where(t => !t.IsAuditTrigger).ToList();
        return candidates.Any(t => t.HasRollback) && !candidates.Any(t => t.HasTransactionControl || t.WriteTargetsUnknown);
    }

    static string LastPart(string procedure)
    {
        var dot = procedure.LastIndexOf('.');
        return (dot >= 0 ? procedure[(dot + 1)..] : procedure).Trim('[', ']');
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>SqlError에서 판정에 쓰는 값.</summary>
public sealed record ErrorInfo(int Number, byte Class, string Message, string? Procedure);
