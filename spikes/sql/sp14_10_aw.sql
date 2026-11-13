/* SP14 AW 판 — 실행 사용자 dbr_preview_user가 AdventureWorks 저장 프로시저를 EXECUTE 권한만으로 실행할 수 있는가
   대상: HumanResources.uspUpdateEmployeeHireInfo (BEGIN TRY → BEGIN TRAN → UPDATE Employee → INSERT EmployeePayHistory → COMMIT, CATCH에서 ROLLBACK + dbo.uspLogError).
   준비(Docker, 저장소 루트): .\scripts\restore-aw-origin.ps1 -DbName AW_SpikeB     (복원 뒤 dbr_preview_user와 스키마 DML 권한을 만든다. AW_SpikeB는 스파이크 전용 사본이다. AW_Origin에는 쓰지 않는다)
   실행: .\scripts\run-sql.ps1 spikes\sql\sp14_10_aw.sql -Database AW_SpikeB -ContinueOnError -Log spikes\logs\sp14-10-aw.log
   스크립트가 사본(AW_SpikeB)에 하는 변경: GRANT EXECUTE ON SCHEMA::HumanResources(권한 DDL 1건, AW의 DDL 트리거가 dbo.DatabaseLog에 기록), 시나리오 b에서 dbo.ErrorLog 1행(롤백 뒤 남는다).
   시나리오(각각 SET XACT_ABORT ON; BEGIN TRAN → EXECUTE AS USER → EXEC → REVERT → ROLLBACK):
     c0 GRANT 전 실행(EXECUTE 권한 없음 → 229 예상)  a 성공 경로(직원 1)  b FK 위반 경로(없는 직원 999999: UPDATE 0행, INSERT가 FK 547 → CATCH → ROLLBACK → uspLogError)
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). 프로시저 본문·매개변수는 첫 결과 집합(모듈 정의)으로 먼저 확인한다. */
SET NOCOUNT ON;
SELECT DB_NAME() AS db;
IF DB_NAME() NOT IN (N'AW_SpikeB') THROW 50000, N'AW_SpikeB(스파이크 전용 사본)에서만 실행한다', 1;
IF USER_ID(N'dbr_preview_user') IS NULL THROW 50000, N'dbr_preview_user가 없다: scripts\restore-aw-origin.ps1 -DbName AW_SpikeB (또는 -PreviewUserOnly)로 만든다', 1;
IF OBJECT_ID(N'HumanResources.uspUpdateEmployeeHireInfo', N'P') IS NULL THROW 50000, N'HumanResources.uspUpdateEmployeeHireInfo가 없다', 1;
GO
/* 0. 모듈 정의(앞 1800자)와 매개변수: 본문이 이 스크립트의 가정(UPDATE Employee, INSERT EmployeePayHistory, CATCH에서 ROLLBACK + uspLogError)과 같은지 눈으로 확인한다 */
SELECT LEFT(OBJECT_DEFINITION(OBJECT_ID(N'HumanResources.uspUpdateEmployeeHireInfo')), 1800) AS definition_head;
SELECT p.name, TYPE_NAME(p.user_type_id) AS type_name FROM sys.parameters AS p WHERE p.object_id = OBJECT_ID(N'HumanResources.uspUpdateEmployeeHireInfo') ORDER BY p.parameter_id;
SELECT 'permissions_before_grant' AS what, dp.permission_name, dp.state_desc, dp.class_desc, ISNULL(SCHEMA_NAME(dp.major_id), '') AS schema_name
FROM sys.database_permissions AS dp WHERE dp.grantee_principal_id = USER_ID(N'dbr_preview_user') AND dp.permission_name = N'EXECUTE';
GO
/* 공용: 케이스 하나를 가장 사용자로 실행하고 결과를 dbo.sp14_result에 남기는 프로시저는 AW_SpikeB에 새 객체를 만들지 않으려고 쓰지 않는다. 대신 배치마다 같은 틀을 쓴다. */
IF OBJECT_ID(N'tempdb..#r') IS NOT NULL DROP TABLE #r;
CREATE TABLE #r (
    scenario varchar(8), ok bit, errNo int NULL, errMsg nvarchar(200) NULL, xstateAfter int NULL, tcAfter int NULL, userDuring sysname NULL,
    payHistRowsInTran int NULL, hireDateInTran date NULL, errorLogRowsAfterRollback int NULL
);
GO
/* 시나리오 틀(c0·a·b 공통): 트랜잭션 안 값은 변수에 모은다(임시 테이블 #r의 쓰기도 ROLLBACK에 되돌려진다). 끝의 ROLLBACK 뒤에 #r에 옮긴다. */
/* c0: GRANT 전. 실행 권한이 없어야 한다 */
SET XACT_ABORT ON;
DECLARE @errLog0 int = (SELECT COUNT(*) FROM dbo.ErrorLog);
DECLARE @ok bit = 0, @errNo int = NULL, @errMsg nvarchar(200) = NULL, @xstate int = NULL, @tc int = NULL, @user sysname = NULL;
BEGIN TRANSACTION;
EXECUTE AS USER = N'dbr_preview_user';
BEGIN TRY
    EXEC HumanResources.uspUpdateEmployeeHireInfo @BusinessEntityID = 1, @JobTitle = N'Chief Executive Officer', @HireDate = '2010-01-15',
        @RateChangeDate = '2026-01-01', @Rate = 50, @PayFrequency = 2, @CurrentFlag = 1;
    SET @ok = 1; SET @xstate = XACT_STATE(); SET @tc = @@TRANCOUNT; SET @user = USER_NAME();
END TRY
BEGIN CATCH
    SET @errNo = ERROR_NUMBER(); SET @errMsg = LEFT(ERROR_MESSAGE(), 200); SET @xstate = XACT_STATE(); SET @tc = @@TRANCOUNT; SET @user = USER_NAME();
END CATCH;
IF XACT_STATE() = -1 ROLLBACK TRANSACTION;   -- doomed면 ROLLBACK 뒤 REVERT(SP10 사실 5)
REVERT;
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
INSERT #r (scenario, ok, errNo, errMsg, xstateAfter, tcAfter, userDuring, errorLogRowsAfterRollback)
VALUES ('c0', @ok, @errNo, @errMsg, @xstate, @tc, @user, (SELECT COUNT(*) FROM dbo.ErrorLog) - @errLog0);
GO
/* GRANT: 이 스파이크가 검증하려는 조치(실행 사용자에게 스키마 단위 EXECUTE) */
GRANT EXECUTE ON SCHEMA::HumanResources TO dbr_preview_user;
SELECT 'permissions_after_grant' AS what, dp.permission_name, dp.state_desc, dp.class_desc, ISNULL(SCHEMA_NAME(dp.major_id), '') AS schema_name
FROM sys.database_permissions AS dp WHERE dp.grantee_principal_id = USER_ID(N'dbr_preview_user') AND dp.permission_name = N'EXECUTE';
GO
/* a: 성공 경로. 직원 1의 HireDate가 바뀌고 EmployeePayHistory 행이 늘어난다(트랜잭션 안). 롤백 뒤에는 원래대로 */
SET XACT_ABORT ON;
DECLARE @errLog0 int = (SELECT COUNT(*) FROM dbo.ErrorLog);
DECLARE @pay0 int = (SELECT COUNT(*) FROM HumanResources.EmployeePayHistory WHERE BusinessEntityID = 1);
DECLARE @hire0 date = (SELECT HireDate FROM HumanResources.Employee WHERE BusinessEntityID = 1);
DECLARE @ok bit = 0, @errNo int = NULL, @errMsg nvarchar(200) = NULL, @xstate int = NULL, @tc int = NULL, @user sysname = NULL, @payInTran int = NULL, @hireInTran date = NULL;
BEGIN TRANSACTION;
EXECUTE AS USER = N'dbr_preview_user';
BEGIN TRY
    EXEC HumanResources.uspUpdateEmployeeHireInfo @BusinessEntityID = 1, @JobTitle = N'Chief Executive Officer', @HireDate = '2010-01-15',
        @RateChangeDate = '2026-01-01', @Rate = 50, @PayFrequency = 2, @CurrentFlag = 1;
    SET @ok = 1; SET @xstate = XACT_STATE(); SET @tc = @@TRANCOUNT; SET @user = USER_NAME();
END TRY
BEGIN CATCH
    SET @errNo = ERROR_NUMBER(); SET @errMsg = LEFT(ERROR_MESSAGE(), 200); SET @xstate = XACT_STATE(); SET @tc = @@TRANCOUNT; SET @user = USER_NAME();
END CATCH;
IF XACT_STATE() = -1 ROLLBACK TRANSACTION;
REVERT;
IF @@TRANCOUNT > 0   -- 아직 열려 있으면 변경 확인 뒤 롤백
BEGIN
    SET @payInTran = (SELECT COUNT(*) FROM HumanResources.EmployeePayHistory WHERE BusinessEntityID = 1) - @pay0;
    SET @hireInTran = (SELECT HireDate FROM HumanResources.Employee WHERE BusinessEntityID = 1);
    ROLLBACK TRANSACTION;
END;
INSERT #r (scenario, ok, errNo, errMsg, xstateAfter, tcAfter, userDuring, payHistRowsInTran, hireDateInTran, errorLogRowsAfterRollback)
VALUES ('a', @ok, @errNo, @errMsg, @xstate, @tc, @user, @payInTran, @hireInTran, (SELECT COUNT(*) FROM dbo.ErrorLog) - @errLog0);
SELECT 'a_after_rollback' AS what, @hire0 AS hire_before, (SELECT HireDate FROM HumanResources.Employee WHERE BusinessEntityID = 1) AS hire_after,
       (SELECT COUNT(*) FROM HumanResources.EmployeePayHistory WHERE BusinessEntityID = 1) - @pay0 AS pay_rows_delta;
GO
/* b: FK 위반 경로. 없는 직원 999999 → UPDATE 0행, INSERT가 FK 위반(547) → CATCH ROLLBACK(봉투 트랜잭션까지 롤백) → uspLogError(ErrorLog 1행, 트랜잭션 밖 자동 커밋) → 프로시저 종료 때 266 예상 */
SET XACT_ABORT ON;
DECLARE @errLog0 int = (SELECT COUNT(*) FROM dbo.ErrorLog);
DECLARE @ok bit = 0, @errNo int = NULL, @errMsg nvarchar(200) = NULL, @xstate int = NULL, @tc int = NULL, @user sysname = NULL;
BEGIN TRANSACTION;
EXECUTE AS USER = N'dbr_preview_user';
BEGIN TRY
    EXEC HumanResources.uspUpdateEmployeeHireInfo @BusinessEntityID = 999999, @JobTitle = N'Nobody', @HireDate = '2010-01-15',
        @RateChangeDate = '2026-01-01', @Rate = 50, @PayFrequency = 2, @CurrentFlag = 1;
    SET @ok = 1; SET @xstate = XACT_STATE(); SET @tc = @@TRANCOUNT; SET @user = USER_NAME();
END TRY
BEGIN CATCH
    SET @errNo = ERROR_NUMBER(); SET @errMsg = LEFT(ERROR_MESSAGE(), 200); SET @xstate = XACT_STATE(); SET @tc = @@TRANCOUNT; SET @user = USER_NAME();
END CATCH;
IF XACT_STATE() = -1 ROLLBACK TRANSACTION;
REVERT;
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
INSERT #r (scenario, ok, errNo, errMsg, xstateAfter, tcAfter, userDuring, errorLogRowsAfterRollback)
VALUES ('b', @ok, @errNo, @errMsg, @xstate, @tc, @user, (SELECT COUNT(*) FROM dbo.ErrorLog) - @errLog0);
SELECT TOP (1) 'b_errorlog_last' AS what, ErrorLogID, ErrorNumber, ErrorProcedure, UserName FROM dbo.ErrorLog ORDER BY ErrorLogID DESC;
GO
SELECT scenario, ok, errNo, errMsg, xstateAfter, tcAfter, userDuring, payHistRowsInTran, hireDateInTran, errorLogRowsAfterRollback FROM #r ORDER BY scenario;
GO
/* 판정(예측은 쓴 시점의 추정). FAIL = 추정과 다름 → 실측 값을 docs/spikes/SP14.md에 적는다. */
SELECT 'H1_no_execute_grant_gives_229' AS hypothesis, CASE WHEN EXISTS (SELECT 1 FROM #r WHERE scenario = 'c0' AND errNo = 229) THEN 'PASS' ELSE 'FAIL' END AS verdict,
       (SELECT CONCAT('errNo=', ISNULL(CAST(errNo AS varchar(10)), 'none')) FROM #r WHERE scenario = 'c0') AS detail
UNION ALL
SELECT 'H2_schema_execute_grant_allows_success_path_as_preview_user', CASE WHEN EXISTS (SELECT 1 FROM #r WHERE scenario = 'a' AND ok = 1 AND userDuring = N'dbr_preview_user' AND payHistRowsInTran = 1 AND hireDateInTran = '2010-01-15') THEN 'PASS' ELSE 'FAIL' END,
       (SELECT CONCAT('ok=', ok, ' errNo=', ISNULL(CAST(errNo AS varchar(10)), 'none'), ' payRows=', ISNULL(CAST(payHistRowsInTran AS varchar(10)), 'null')) FROM #r WHERE scenario = 'a')
UNION ALL
SELECT 'H3_fk_violation_path_ends_with_266_and_logs_error_row', CASE WHEN EXISTS (SELECT 1 FROM #r WHERE scenario = 'b' AND errNo = 266 AND errorLogRowsAfterRollback = 1) THEN 'PASS' ELSE 'FAIL' END,
       (SELECT CONCAT('ok=', ok, ' errNo=', ISNULL(CAST(errNo AS varchar(10)), 'none'), ' errorLogRows=', ISNULL(CAST(errorLogRowsAfterRollback AS varchar(10)), 'null')) FROM #r WHERE scenario = 'b');
GO
