/* SP20 — 3단계: 샌드박스 컨테이너(SQL Server 2025, sa)에서 다른 로그인이 소유한 DB의 백업을 복원하고, 소유자·dbo 매핑·EXECUTE AS OWNER 모듈 실행을 확인한다(ALTER AUTHORIZATION 전).
   실행(저장소 루트): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp20_30_sandbox_restore_check.sql -Service sql -ContinueOnError -Log spikes\logs\sp20-30-restore.log
   사전: .data\import\sp20_owner_db.bak(sp20_20 설명). 샌드박스에는 sp20_owner 로그인이 없다.
   복원 이름은 SpikeLab_SP20이다(전용). 제품의 AfterRestoreLocal(ALTER AUTHORIZATION … TO sa)과 같은 조치는 sp20_40에서 한다.
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). 논리 파일 이름은 CREATE DATABASE 기본값(SpikeLab_SP20, SpikeLab_SP20_log)이라고 가정하고, 먼저 FILELISTONLY로 보여 준다. */
SET NOCOUNT ON;
USE master;
GO
IF SUSER_ID(N'sp20_owner') IS NOT NULL PRINT N'주의: 샌드박스에 이미 sp20_owner 로그인이 있다(이 실험은 없다고 가정한다)';
IF DB_ID(N'SpikeLab_SP20') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP20 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP20; END;
GO
RESTORE FILELISTONLY FROM DISK = N'/var/opt/mssql/import/sp20_owner_db.bak';
GO
DECLARE @data nvarchar(512) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512));
DECLARE @log nvarchar(512) = CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(512));
DECLARE @sql nvarchar(max) = N'RESTORE DATABASE [SpikeLab_SP20] FROM DISK = N''/var/opt/mssql/import/sp20_owner_db.bak'' WITH RECOVERY, '
    + N'MOVE N''SpikeLab_SP20'' TO ' + QUOTENAME(@data + N'SpikeLab_SP20.mdf', '''') + N', '
    + N'MOVE N''SpikeLab_SP20_log'' TO ' + QUOTENAME(@log + N'SpikeLab_SP20_log.ldf', '''') + N';';
EXEC sys.sp_executesql @sql;
GO
/* 확인 1: 소유자와 dbo 매핑 (ALTER AUTHORIZATION 전) */
SELECT 'before_fix_db_owner' AS k, ISNULL(SUSER_SNAME(owner_sid), N'(null: orphan)') AS v FROM sys.databases WHERE name = N'SpikeLab_SP20'
UNION ALL SELECT 'before_fix_owner_sid', CONVERT(nvarchar(100), owner_sid, 1) FROM sys.databases WHERE name = N'SpikeLab_SP20'
UNION ALL SELECT 'before_fix_dbo_maps_to_login', ISNULL(SUSER_SNAME(sid), N'(null: orphan)') FROM SpikeLab_SP20.sys.database_principals WHERE name = N'dbo'
UNION ALL SELECT 'before_fix_sa_sid', CONVERT(nvarchar(100), SUSER_SID(N'sa'), 1);
GO
/* 확인 2: EXECUTE AS OWNER 프로시저와 트리거를 (롤백 안에서) 실행한다. 고아 dbo면 15517(또는 비슷한 가장 오류)이 난다. */
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
SELECT 'before_fix_module_run' AS what, @errNo AS errNo, @msg AS msg;
SELECT 'before_fix_whoami' AS what, step, origLogin, suser, usr FROM @r;
GO
