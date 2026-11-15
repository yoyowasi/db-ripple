using System.Text.Json.Serialization;
using DbRipple.Core.Catalog;

namespace DbRipple.Contracts;

/// <summary>진단 한 건(§6.4 Diagnostic, C1 줄 표시). 거부와 경고 모두 이 형식이다.</summary>
public sealed record Diagnostic
{
    /// <summary>문장 전역 번호(1부터). 파싱 실패처럼 문장을 특정할 수 없으면 null.</summary>
    public int? StatementIndex { get; init; }
    /// <summary>스크립트 전체 기준 위치(D5 v1.3).</summary>
    public required SourceSpan Span { get; init; }
    /// <summary>error = 거부, warning = 경고(D5).</summary>
    public required Severity Severity { get; init; }
    /// <summary>규칙 ID(예 "REJECT_TRUNCATE", §6.4). 전체 목록은 AnalysisRules(열린 질문 Q3).</summary>
    public required string RuleId { get; init; }
    /// <summary>사용자에게 보일 설명.</summary>
    public required string Message { get; init; }
    /// <summary>설계 근거 ID(예 "A21", "E11"). 없으면 null.</summary>
    public string? DocRef { get; init; }
}

/// <summary>분석기가 실행 가능으로 판정한 문장 1개(§4 C6, D5 "번호와 실행 의미"). 실행기(C10)는 Sql을 별도 명령으로 하나씩 실행한다.</summary>
public sealed record AnalyzedStatement
{
    /// <summary>문장 전역 번호(1부터, §6.2 Statement.index).</summary>
    public required int Index { get; init; }
    /// <summary>GO 배치 번호(1부터, §6.2 Statement.batch).</summary>
    public required int Batch { get; init; }
    /// <summary>배치 안 순번(1부터, §6.2 Statement.position).</summary>
    public required int Position { get; init; }
    /// <summary>INSERT/UPDATE/DELETE(D5 허용 목록), 저장 프로시저 호출이면 EXEC(2026-11-13).</summary>
    public required ChangeOperation Kind { get; init; }
    /// <summary>직접 대상 테이블 "스키마.테이블". 스키마를 생략한 이름의 해석 규칙은 열린 질문 Q4. EXEC면 null.</summary>
    public required string? Target { get; init; }
    /// <summary>EXEC: 부른 프로시저 "스키마.이름"(카탈로그 철자). 그 밖에는 null.</summary>
    public string? Procedure { get; init; }
    /// <summary>EXEC: 매개변수 원문 목록(표시용, 예 "@id = 5"). 그 밖에는 빈 배열.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];
    /// <summary>EXEC 실행·귀속 재료(분석기가 채움). JSON에는 내보내지 않는다.</summary>
    [JsonIgnore]
    public ProcedurePlan? Plan { get; init; }
    /// <summary>스크립트 전체 기준 위치.</summary>
    public required SourceSpan Span { get; init; }
    /// <summary>실행할 문장 원문(스크립트에서 오프셋으로 잘라낸 부분, 끝 세미콜론 포함 여부는 C6이 정함).</summary>
    public required string Sql { get; init; }
    /// <summary>문장 안 비결정 함수(GETDATE·NEWID 등)로 값이 정해지는 직접 대상 열(E1, RowChange.volatile STATEMENT_FUNCTION). 없으면 빈 배열.</summary>
    public IReadOnlyList<string> NondeterministicColumns { get; init; } = [];
}

/// <summary>
/// EXEC 문장의 실행·귀속 재료(2026-11-13, 내부 전달용: AnalyzedStatement → StatementResult → C13). JSON 계약 밖이다.
/// </summary>
public sealed record ProcedurePlan
{
    /// <summary>ExecSql 마지막 결과 집합의 반환 값 열 이름.</summary>
    public const string ReturnColumn = "dbr_rc";

    /// <summary>ExecSql 마지막 결과 집합의 트랜잭션 수 변화 열 이름(1이면 정상).</summary>
    public const string TranDeltaColumn = "dbr_dtc";

    /// <summary>"스키마.이름"(카탈로그 철자).</summary>
    public required string Procedure { get; init; }
    /// <summary>
    /// 실행할 SQL. 사용자 원문을 잇지 않고 분석기가 인용한 이름·리터럴로 만든다. 호출을 BEGIN TRAN 한 겹으로 감싸고 트랜잭션 수 변화를 돌려준다
    /// (Analysis ExecSqlBuilder.Build 참고). 마지막 결과 집합은 (dbr_rc, dbr_dtc). 거부된 호출이면 빈 문자열.
    /// </summary>
    public required string ExecSql { get; init; }
    /// <summary>펼친 본문(안쪽 호출 포함)의 정적 쓰기 대상 "스키마.테이블".</summary>
    public IReadOnlyList<string> WriteTargets { get; init; } = [];
    /// <summary>펼친 안쪽 프로시저 "스키마.이름"(대상 자신 제외).</summary>
    public IReadOnlyList<string> ExpandedCalls { get; init; } = [];
    /// <summary>직선형 루트 본문이 처음 실행하는 독립 DELETE. 정적 정보만으로 발동을 확정하지 않는다.</summary>
    public IndependentDelete? FirstIndependentDelete { get; init; }
}

/// <summary>분석한 루트 모듈과 그 첫 독립 DELETE 대상.</summary>
/// <param name="ModuleObjectId">루트 프로시저 개체 ID.</param>
/// <param name="TargetObjectId">삭제 대상의 개체 ID.</param>
/// <param name="Target">카탈로그의 스키마.테이블.</param>
/// <param name="Operation">확인한 쓰기 연산(DELETE).</param>
public sealed record IndependentDelete(int ModuleObjectId, int TargetObjectId, string Target, ChangeOperation Operation);

/// <summary>
/// C6 결과(§4 C6 `AnalysisResult{statements[], rejects[], warnings[]}`, §6.4 POST /api/analyze). 거부가 하나라도 있으면 전체를 실행하지 않는다(D5).
/// </summary>
public sealed record AnalysisResult
{
    /// <summary>파싱된 문장(거부가 있어도 파싱된 것은 담는다).</summary>
    public IReadOnlyList<AnalyzedStatement> Statements { get; init; } = [];
    /// <summary>거부(Severity = error). 파싱 실패도 여기 들어간다.</summary>
    public IReadOnlyList<Diagnostic> Rejects { get; init; } = [];
    /// <summary>경고(Severity = warning, D5 "경고(실행은 함)").</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; init; } = [];
    /// <summary>편집기 줄 표시용 전체 진단(Rejects 다음 Warnings). §6.4 "Diagnostic[]".</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => [.. Rejects, .. Warnings];
    /// <summary>실행 가능 여부(거부 0개, 문장 1개 이상). 빈 스크립트의 처리는 열린 질문 Q5.</summary>
    public bool IsAccepted => Rejects.Count == 0 && Statements.Count > 0;
}

/// <summary>
/// 트리거 1개의 본문 정적 분석 결과(§4 C7 "트리거 본문 정적 분석", §7.2 4단계, D5 외부 부작용 거부, E1·E8).
/// 2주차에 C6(Analysis 프로젝트)이 Core DbCatalog.Triggers의 Definition을 ScriptDom으로 분석해 만든다(재사용 후보: spikes/dotnet/Sp4.ScriptDom.Tests/TriggerAnalyzer.cs).
/// </summary>
public sealed record TriggerStaticInfo
{
    /// <summary>트리거 "스키마.이름".</summary>
    public required string Trigger { get; init; }
    /// <summary>트리거 개체 ID(sys.triggers.object_id).</summary>
    public required int ObjectId { get; init; }
    /// <summary>부모 테이블 "스키마.테이블"(DDL 트리거는 대상이 아니다).</summary>
    public required string Table { get; init; }
    /// <summary>발동 이벤트(INSERT/UPDATE/DELETE).</summary>
    public IReadOnlyList<ChangeOperation> Events { get; init; } = [];
    /// <summary>INSTEAD OF 여부(A5, §7.2 5단계).</summary>
    public required bool IsInsteadOf { get; init; }
    /// <summary>비활성 트리거(E22: 발동 후보에서 뺀다).</summary>
    public required bool IsDisabled { get; init; }
    /// <summary>암호화·CLR이라 본문을 읽지 못함(쓰기 대상 미상 → 동적 SQL처럼 모든 테이블 후보로 볼지는 열린 질문 Q6).</summary>
    public required bool IsEncrypted { get; init; }
    /// <summary>감사 트리거(dbr_audit_ 접두, C9 설치). 원인 후보에서 뺀다(§7.2 4단계 "감사 트리거 제외").</summary>
    public required bool IsAuditTrigger { get; init; }
    /// <summary>정적 쓰기 대상 테이블 "스키마.테이블"(INSERT/UPDATE/DELETE/MERGE 대상, 별칭·CTE·테이블 변수·#임시 제외, §7.2 4단계).</summary>
    public IReadOnlyList<string> WriteTargets { get; init; } = [];
    /// <summary>
    /// 쓰기 대상을 알 수 없음(Q6: 암호화·CLR이라 본문이 없거나 본문 파싱 실패). true면 WriteTargets는 비어 있고 의미가 없으며,
    /// 그 트리거가 낸 후보는 ambiguous 이하다(C13). 동적 SQL만 있는 트리거는 false(WriteTargets는 정적으로 보인 대상, HasDynamicSql로 따로 표시).
    /// C6(TriggerAnalyzer)이 채운다. 2026-11-10 통합 때 추가(비파괴, 기본 false).
    /// </summary>
    public bool WriteTargetsUnknown { get; init; }
    /// <summary>동적 SQL(EXEC 문자열, sp_executesql, EXEC @var). 있으면 모든 테이블에 쓸 수 있다고 본다(§7.2 4단계, E8).</summary>
    public required bool HasDynamicSql { get; init; }
    /// <summary>ROLLBACK 포함(E13 거부 후보 표시).</summary>
    public required bool HasRollback { get; init; }
    /// <summary>
    /// ROLLBACK이 아닌 트랜잭션 제어(COMMIT, BEGIN TRAN, SAVE TRAN) 포함. 트리거의 COMMIT은 미리보기 트랜잭션을 확정해 샌드박스에 변경을 남기므로
    /// 그 트리거가 붙은 테이블의 DML은 거부한다(AnalysisRules.RejectTriggerTransactionControl). 2026-11-10 통합 검토 때 추가(비파괴, 기본 false).
    /// </summary>
    public bool HasTransactionControl { get; init; }
    /// <summary>외부 부작용 표시(msdb, 4부분 이름, OPENQUERY/OPENROWSET, xp_). 하나라도 있으면 그 테이블 대상 DML은 거부(D5, E18).</summary>
    public IReadOnlyList<string> ExternalSideEffects { get; init; } = [];
    /// <summary>호출하는 저장 프로시저 이름(쓰기 대상 추적 범위 밖, 정보용).</summary>
    public IReadOnlyList<string> ProcedureCalls { get; init; } = [];
    /// <summary>
    /// ProcedureCalls 가운데 본문(sys.sql_modules)을 한 단계 펼쳐 쓰기 대상을 WriteTargets에 합친 프로시저 "스키마.이름"(2주차 인계 1, 예 dbo.uspLogError → dbo.ErrorLog).
    /// 2026-11-11 추가(비파괴, 기본 빈 배열). C6이 채운다.
    /// </summary>
    public IReadOnlyList<string> ExpandedProcedureCalls { get; init; } = [];
    /// <summary>
    /// ProcedureCalls 가운데 펼치지 못한 프로시저(카탈로그에 없음·다른 DB·본문 없음·암호화·파싱 실패·동적 SQL·재귀·깊이 상한 4 초과·안쪽 호출을 펼치지 못함. 2026-11-11 결정: AW uspLogError → uspPrintError 때문에 한 단계가 아니라 깊이 4까지 따라간다). 이것만 쓰기 대상 미상(Q6)으로 본다(인계 1: WRITES 하향은 진짜 미상에만).
    /// 호환 규칙: ProcedureCalls가 있는데 ExpandedProcedureCalls와 이 목록이 모두 비어 있으면(펼치기 전 분석기) C13은 ProcedureCalls 전부를 미상으로 본다. 2026-11-11 추가(비파괴).
    /// </summary>
    public IReadOnlyList<string> UnresolvedProcedureCalls { get; init; } = [];
    /// <summary>본문의 비결정 함수로 값이 정해지는 (테이블, 열)(E1 → RowChange.volatile TRIGGER_GETDATE).</summary>
    public IReadOnlyList<TableColumn> NondeterministicWrites { get; init; } = [];
}

/// <summary>테이블의 열 하나.</summary>
public sealed record TableColumn
{
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>열 이름.</summary>
    public required string Column { get; init; }
}

/// <summary>C6 배치 분석기(§4 C6). D5 허용 목록 판정, GO 배치·문장 나누기, 위치·경고 계산.</summary>
public interface IBatchAnalyzer
{
    /// <summary>
    /// §4 `Analyze(sql)`. baseline은 카탈로그에 기대는 D5 규칙(기반 사용자 테이블인지, 외부 부작용 트리거가 붙은 대상, 감사 트리거가 Last가 아닌 대상,
    /// 동적 SQL 트리거 경고)을 판정하려고 받는다. 판정할 수 없으면 닫힌 쪽(거부)으로 정한다. 예외를 던지지 않고 거부로 돌려준다(입력 오류는 결과다).
    /// </summary>
    AnalysisResult Analyze(string sql, BaselineContext baseline);
}

/// <summary>C6 트리거 본문 정적 분석기(§4 C7의 정적 분석 부분, 2주차 C6 담당).</summary>
public interface ITriggerAnalyzer
{
    /// <summary>카탈로그의 DML 트리거(테이블 부모)마다 1개. 감사 트리거도 IsAuditTrigger=true로 포함한다. 파싱 실패한 트리거는 HasDynamicSql=true처럼 보수적으로 표시한다.</summary>
    IReadOnlyList<TriggerStaticInfo> Analyze(DbCatalog catalog);
}
