namespace DbRipple.Contracts;

/// <summary>
/// C6 진단 규칙 ID 전체 목록(열린 질문 Q3, Diagnostic.RuleId). 거부는 REJECT_*, 경고는 WARN_*.
/// C6(Analysis)이 정한 목록을 2026-11-10 통합 때 계약 상수로 올렸다(값 문자열은 그대로, API·프런트가 같은 값을 쓴다).
/// </summary>
public static class AnalysisRules
{
    // ---- 거부: 스크립트 수준 ----
    /// <summary>빈 스크립트·주석뿐인 스크립트(Q5).</summary>
    public const string RejectEmptyScript = "REJECT_EMPTY_SCRIPT";
    /// <summary>ScriptDom 파싱 오류(D5 "파싱에 실패하면 거부").</summary>
    public const string RejectParseError = "REJECT_PARSE_ERROR";
    /// <summary>`GO 2`처럼 반복 횟수를 붙인 GO(SP4).</summary>
    public const string RejectGoCount = "REJECT_GO_COUNT";
    /// <summary>분석기 내부 오류(닫힌 쪽으로 거부).</summary>
    public const string RejectAnalysisError = "REJECT_ANALYSIS_ERROR";

    // ---- 거부: D5 문장 범주 ----
    /// <summary>모든 DDL(CREATE/ALTER/DROP 등).</summary>
    public const string RejectDdl = "REJECT_DDL";
    /// <summary>SELECT ... INTO.</summary>
    public const string RejectSelectInto = "REJECT_SELECT_INTO";
    /// <summary>TRUNCATE TABLE(트리거 미발동, A21).</summary>
    public const string RejectTruncate = "REJECT_TRUNCATE";
    /// <summary>BULK INSERT·INSERT BULK(A21).</summary>
    public const string RejectBulkInsert = "REJECT_BULK_INSERT";
    /// <summary>OPENROWSET·OPENDATASOURCE·OPENQUERY.</summary>
    public const string RejectExternalRowset = "REJECT_EXTERNAL_ROWSET";
    /// <summary>IGNORE_TRIGGERS 테이블 힌트(트리거를 건너뛰어 수집이 깨진다).</summary>
    public const string RejectIgnoreTriggers = "REJECT_IGNORE_TRIGGERS";
    /// <summary>MERGE(A3).</summary>
    public const string RejectMerge = "REJECT_MERGE";
    /// <summary>BEGIN/COMMIT/ROLLBACK/SAVE TRANSACTION.</summary>
    public const string RejectTransactionControl = "REJECT_TRANSACTION_CONTROL";
    /// <summary>SET 옵션(SET NOCOUNT, SET IDENTITY_INSERT, SET TRANSACTION ISOLATION LEVEL 등).</summary>
    public const string RejectSetOption = "REJECT_SET_OPTION";
    /// <summary>USE.</summary>
    public const string RejectUse = "REJECT_USE";
    /// <summary>EXEC, sp_executesql, INSERT ... EXEC.</summary>
    public const string RejectExec = "REJECT_EXEC";
    /// <summary>변수(DECLARE, SET @v, @v·@@ 참조)와 제어 흐름(IF, WHILE, BEGIN...END, TRY...CATCH 등).</summary>
    public const string RejectVariableOrFlow = "REJECT_VARIABLE_OR_FLOW";
    /// <summary>#임시 테이블, 테이블 변수.</summary>
    public const string RejectTempOrTableVariable = "REJECT_TEMP_OR_TABLE_VARIABLE";
    /// <summary>OUTPUT 절(A11, 관찰자 효과).</summary>
    public const string RejectOutput = "REJECT_OUTPUT";
    /// <summary>3·4부분 이름(다른 DB, 링크드 서버, A18).</summary>
    public const string RejectCrossDatabase = "REJECT_CROSS_DATABASE";
    /// <summary>DBCC, BACKUP, RESTORE, WAITFOR 등 범위 밖 관리 명령.</summary>
    public const string RejectAdminCommand = "REJECT_ADMIN_COMMAND";
    /// <summary>허용 목록(INSERT/UPDATE/DELETE) 밖의 그 밖의 문장(SELECT 포함).</summary>
    public const string RejectNotAllowed = "REJECT_NOT_ALLOWED";

    // ---- 거부: 카탈로그에 기대는 규칙 ----
    /// <summary>대상이 기반 사용자 테이블이 아니다(뷰·동의어·CTE·없는 이름).</summary>
    public const string RejectTargetNotUserTable = "REJECT_TARGET_NOT_USER_TABLE";
    /// <summary>대상이 수집 장치 저장소(audit 스키마)다.</summary>
    public const string RejectCaptureStore = "REJECT_CAPTURE_STORE";
    /// <summary>대상에 외부 부작용 트리거(msdb, 4부분 이름, OPENQUERY, xp_, 분석 못 한 본문)가 붙어 있다(D5, E18).</summary>
    public const string RejectTriggerExternalEffect = "REJECT_TRIGGER_EXTERNAL_EFFECT";
    /// <summary>대상에 본문을 읽을 수 없는(암호화·CLR) 활성 트리거가 붙어 있어 외부 부작용을 배제할 수 없다(닫힌 쪽).</summary>
    public const string RejectTriggerOpaque = "REJECT_TRIGGER_OPAQUE";
    /// <summary>대상에 ROLLBACK이 아닌 트랜잭션 제어(COMMIT·BEGIN TRAN·SAVE TRAN)를 하는 활성 트리거가 붙어 있다. 트리거의 COMMIT은 미리보기 트랜잭션을 확정한다(2026-11-10 추가).</summary>
    public const string RejectTriggerTransactionControl = "REJECT_TRIGGER_TRANSACTION_CONTROL";

    // ---- 거부: EXEC 미리보기(2026-11-13) ----
    /// <summary>지원하지 않는 호출 모양: 반환 변수(EXEC @rc =), OUTPUT, 위치 매개변수, 리터럴이 아닌 값, WITH RESULT SETS 등 옵션, AT 링크드 서버, EXECUTE AS 문맥, 번호 프로시저(;n), 중복 매개변수.</summary>
    public const string RejectProcCallForm = "REJECT_PROC_CALL_FORM";
    /// <summary>카탈로그의 T-SQL 사용자 프로시저가 아니다(없는 이름, sp_ 접두, 시스템·확장 프로시저).</summary>
    public const string RejectProcNotFound = "REJECT_PROC_NOT_FOUND";
    /// <summary>본문을 읽을 수 없다(암호화·CLR, 파싱 실패).</summary>
    public const string RejectProcOpaque = "REJECT_PROC_OPAQUE";
    /// <summary>모듈에 EXECUTE AS OWNER·SELF·사용자 절이 있다(펼친 안쪽 프로시저 포함).</summary>
    public const string RejectProcExecuteAs = "REJECT_PROC_EXECUTE_AS";
    /// <summary>매개변수 대조 실패: 없는 이름, 기본값 없는 필수 매개변수 누락, OUTPUT·READONLY 매개변수가 있는 프로시저(1차).</summary>
    public const string RejectProcParameter = "REJECT_PROC_PARAMETER";
    /// <summary>펼친 본문에 동적 SQL(EXEC 문자열, EXEC @var, sp_executesql·sp_prepare 계열).</summary>
    public const string RejectProcDynamicSql = "REJECT_PROC_DYNAMIC_SQL";
    /// <summary>펼치지 못한 안쪽 호출(카탈로그에 없음, 본문 없음, 재귀, 깊이 상한 4 초과 등).</summary>
    public const string RejectProcUnresolvedCall = "REJECT_PROC_UNRESOLVED_CALL";
    /// <summary>펼친 본문의 외부 부작용(다른 DB 3·4부분 이름, msdb, OPENQUERY 등, 시스템 프로시저, Service Broker, RAISERROR WITH LOG).</summary>
    public const string RejectProcExternalEffect = "REJECT_PROC_EXTERNAL_EFFECT";
    /// <summary>SAVE TRAN 또는 저장점으로 ROLLBACK.</summary>
    public const string RejectProcSavepoint = "REJECT_PROC_SAVEPOINT";
    /// <summary>BEGIN 없는 COMMIT, 또는 모듈 안 COMMIT이 BEGIN TRAN보다 많다(도구 트랜잭션을 확정할 수 있음).</summary>
    public const string RejectProcUnbalancedCommit = "REJECT_PROC_UNBALANCED_COMMIT";
    /// <summary>펼친 본문의 DDL(#임시 테이블 제외), SELECT ... INTO 실제 테이블.</summary>
    public const string RejectProcDdl = "REJECT_PROC_DDL";
    /// <summary>허용 목록 밖 문장(TRUNCATE, BULK INSERT, WAITFOR, DBCC, USE, EXECUTE AS·REVERT·SETUSER, GRANT·DENY·REVOKE, BACKUP·RESTORE, KILL·SHUTDOWN·RECONFIGURE, 인식하지 못한 문장).</summary>
    public const string RejectProcStatementNotAllowed = "REJECT_PROC_STATEMENT_NOT_ALLOWED";
    /// <summary>CATCH 밖(성공 경로)에서 @@TRANCOUNT·XACT_STATE()를 읽는다. 도구 트랜잭션 안이라 값이 원본과 다르다(SP16 뒤 재검토).</summary>
    public const string RejectProcTrancountBranch = "REJECT_PROC_TRANCOUNT_BRANCH";
    /// <summary>쓰기 대상이 기반 사용자 테이블이 아니다(뷰·동의어·없는 이름·감사 저장소).</summary>
    public const string RejectProcWriteTarget = "REJECT_PROC_WRITE_TARGET";

    // ---- 경고 4종(D5 "경고(실행은 함)") ----
    /// <summary>ORDER BY 없는 TOP이 붙은 UPDATE/DELETE(행 선택이 비결정적).</summary>
    public const string WarnTopWithoutOrder = "WARN_TOP_WITHOUT_ORDER";
    /// <summary>문장 안 비결정 함수(GETDATE, NEWID, SYSDATETIME, NEXT VALUE FOR 등, E1).</summary>
    public const string WarnNondeterministicFunction = "WARN_NONDETERMINISTIC_FUNCTION";
    /// <summary>감사 트리거가 Last가 아닌 대상 테이블·연산(E15).</summary>
    public const string WarnAuditNotLast = "WARN_AUDIT_NOT_LAST";
    /// <summary>대상에 동적 SQL을 쓰는 트리거가 붙어 있다(E8).</summary>
    public const string WarnTriggerDynamicSql = "WARN_TRIGGER_DYNAMIC_SQL";

    // ---- 경고: EXEC 미리보기(2026-11-13) ----
    /// <summary>균형 잡힌 BEGIN/COMMIT TRAN(도구 트랜잭션 안이라 중첩 트랜잭션이 되어 COMMIT이 확정하지 않는다).</summary>
    public const string WarnProcNestedTransaction = "WARN_PROC_NESTED_TRANSACTION";
    /// <summary>ROLLBACK(CATCH 안 포함). 실행되면 미리보기가 실패로 끝난다("프로시저가 트랜잭션을 롤백함").</summary>
    public const string WarnProcRollback = "WARN_PROC_ROLLBACK";
    /// <summary>TRY/CATCH가 오류를 삼킨다(CATCH에 THROW·RAISERROR 없음).</summary>
    public const string WarnProcCatchSwallows = "WARN_PROC_CATCH_SWALLOWS";
    /// <summary>CATCH 안 XACT_STATE()·@@TRANCOUNT 검사(SP16 뒤 재검토).</summary>
    public const string WarnProcXactStateCheck = "WARN_PROC_XACT_STATE_CHECK";
    /// <summary>커서.</summary>
    public const string WarnProcCursor = "WARN_PROC_CURSOR";
    /// <summary>#임시 테이블(변경 수집 밖).</summary>
    public const string WarnProcTempTable = "WARN_PROC_TEMP_TABLE";
    /// <summary>테이블 변수(변경 수집 밖).</summary>
    public const string WarnProcTableVariable = "WARN_PROC_TABLE_VARIABLE";
    /// <summary>결과 집합을 낸다(개수만 세고 버림).</summary>
    public const string WarnProcResultSet = "WARN_PROC_RESULT_SET";
    /// <summary>비결정 값(GETDATE·NEWID 등)으로 쓰는 열.</summary>
    public const string WarnProcNondeterministicWrite = "WARN_PROC_NONDETERMINISTIC_WRITE";

    /// <summary>거부 규칙 전체.</summary>
    public static IReadOnlyList<string> AllRejects { get; } =
    [
        RejectEmptyScript, RejectParseError, RejectGoCount, RejectAnalysisError, RejectDdl, RejectSelectInto, RejectTruncate,
        RejectBulkInsert, RejectExternalRowset, RejectIgnoreTriggers, RejectMerge, RejectTransactionControl, RejectSetOption,
        RejectUse, RejectExec, RejectVariableOrFlow, RejectTempOrTableVariable, RejectOutput, RejectCrossDatabase,
        RejectAdminCommand, RejectNotAllowed, RejectTargetNotUserTable, RejectCaptureStore, RejectTriggerExternalEffect, RejectTriggerOpaque, RejectTriggerTransactionControl,
        RejectProcCallForm, RejectProcNotFound, RejectProcOpaque, RejectProcExecuteAs, RejectProcParameter, RejectProcDynamicSql, RejectProcUnresolvedCall,
        RejectProcExternalEffect, RejectProcSavepoint, RejectProcUnbalancedCommit, RejectProcDdl, RejectProcStatementNotAllowed, RejectProcTrancountBranch, RejectProcWriteTarget,
    ];

    /// <summary>경고 규칙 전체.</summary>
    public static IReadOnlyList<string> AllWarnings { get; } =
    [
        WarnTopWithoutOrder, WarnNondeterministicFunction, WarnAuditNotLast, WarnTriggerDynamicSql,
        WarnProcNestedTransaction, WarnProcRollback, WarnProcCatchSwallows, WarnProcXactStateCheck, WarnProcCursor, WarnProcTempTable,
        WarnProcTableVariable, WarnProcResultSet, WarnProcNondeterministicWrite,
    ];
}
