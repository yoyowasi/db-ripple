/* SP20 — 4단계: 샌드박스(sa)에서 제품의 복원 뒤 정리(BaselineSql.AfterRestoreLocal)와 같은 조치 ALTER AUTHORIZATION ON DATABASE::SpikeLab_SP20 TO sa 를 한 뒤,
   소유자·dbo 매핑·EXECUTE AS OWNER 모듈 실행을 다시 확인하고 sp20_30의 "전"과 비교한다.
   실행(저장소 루트): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp20_40_alter_authorization_check.sql -Service sql -ContinueOnError -Log spikes\logs\sp20-40-after.log
   정리: .\spikes\sql\sp_container-run.ps1 spikes\sql\sp20_99_drop.sql -Service sql -Log spikes\logs\sp20-99-sandbox.log  그리고  -Service sql-origin 으로 한 번 더(원본 모사 쪽)
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
SET NOCOUNT ON;
USE master;
GO
DECLARE @sa sysname = SUSER_SNAME(0x01);
DECLARE @sql nvarchar(max) = N'ALTER AUTHORIZATION ON DATABASE::[SpikeLab_SP20] TO ' + QUOTENAME(@sa) + N';';
EXEC sys.sp_executesql @sql;
GO
SELECT 'after_fix_db_owner' AS k, ISNULL(SUSER_SNAME(owner_sid), N'(null: orphan)') AS v FROM sys.databases WHERE name = N'SpikeLab_SP20'
UNION ALL SELECT 'after_fix_dbo_maps_to_login', ISNULL(SUSER_SNAME(sid), N'(null: orphan)') FROM SpikeLab_SP20.sys.database_principals WHERE name = N'dbo';
GO
USE SpikeLab_SP20;
GO
DECLARE @r TABLE (step varchar(20), origLogin sysname NULL, suser sysname NULL, usr sysname NULL);
DECLARE @errNo int = NULL, @msg nvarchar(200) = NULL;
BEGIN TRANSACTION;
BEGIN TRY
    EXEC dbo.usp_as_owner;
END TRY
BEGIN CATCH
    SELECT @errNo = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 200);
END CATCH;
IF XACT_STATE() <> -1 INSERT @r SELECT step, origLogin, suser, usr FROM dbo.Whoami ORDER BY seq;
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
SELECT 'after_fix_module_run' AS what, @errNo AS errNo, @msg AS msg;
SELECT 'after_fix_whoami' AS what, step, origLogin, suser, usr FROM @r;
-- 판정: 조치 뒤에는 오류 없이 프로시저·트리거가 모두 실행되고 suser가 sa(또는 소유자로 바뀐 로그인)여야 한다.
SELECT 'H1_after_alter_authorization_modules_run_without_error' AS hypothesis,
       CASE WHEN @errNo IS NULL AND (SELECT COUNT(*) FROM @r) = 2 THEN 'PASS' ELSE 'FAIL' END AS verdict,
       CONCAT('errNo=', ISNULL(CAST(@errNo AS varchar(10)), 'none'), ' rows=', (SELECT COUNT(*) FROM @r)) AS detail;
GO
