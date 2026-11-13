/* SP14 미니 판 — 가장(EXECUTE AS USER dbr_preview_user) 중 프로시저의 소유권 체인, 동적 SQL, 다른 DB 3부분 이름
   기대(추정): 정적 참조는 소유권 체인으로 통과 / 동적 SQL은 체인이 끊겨 권한 없는 객체에서 229 / 3부분 이름은 916(+ 트랜잭션 doomed, SP10 사실 4).
   실행: .\scripts\run-sql.ps1 spikes\sql\sp14_20_mini.sql -Part 1 -Log spikes\logs\sp14-20-part1.log
         .\scripts\run-sql.ps1 spikes\sql\sp14_20_mini.sql -Part 2 -Database SpikeLab_SP14 -ContinueOnError -Log spikes\logs\sp14-20-part2.log
   정리: .\scripts\run-sql.ps1 spikes\sql\sp_exec_99_drop.sql -Log spikes\logs\sp-exec-99-drop.log
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE master;
GO
IF DB_ID(N'SpikeLab_SP14') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP14 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP14; END;
IF DB_ID(N'SpikeLab_SP14_Other') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP14_Other SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP14_Other; END;
GO
CREATE DATABASE SpikeLab_SP14;
GO
CREATE DATABASE SpikeLab_SP14_Other;   /* 스냅샷 대신 쓰는 '다른 DB' */
GO
USE SpikeLab_SP14_Other;
GO
CREATE TABLE dbo.X (Id int NOT NULL PRIMARY KEY);
INSERT dbo.X VALUES (1);
GO
-- PART 2
USE SpikeLab_SP14;
GO
CREATE SCHEMA secret;
GO
CREATE TABLE secret.S (Id int NOT NULL PRIMARY KEY);
INSERT secret.S VALUES (1), (2);
CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, V int NOT NULL);
INSERT dbo.T VALUES (1, 0);
GO
/* 실행 사용자: 제품의 PreviewUserSql과 같은 최소 권한. dbo 스키마 DML + EXECUTE만, secret 스키마 권한 없음 */
CREATE USER dbr_preview_user WITHOUT LOGIN WITH DEFAULT_SCHEMA = dbo;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO dbr_preview_user;
GRANT EXECUTE ON SCHEMA::dbo TO dbr_preview_user;
GO
CREATE PROCEDURE dbo.usp_static_secret AS BEGIN SET NOCOUNT ON; SELECT COUNT(*) AS n FROM secret.S; END;   -- 정적 참조: dbo 소유 체인(secret 스키마도 dbo 소유)
GO
CREATE PROCEDURE dbo.usp_dynamic_secret AS BEGIN SET NOCOUNT ON; EXEC (N'SELECT COUNT(*) AS n FROM secret.S'); END;   -- 동적 SQL: 체인이 끊겨 사용자 권한으로 검사
GO
CREATE PROCEDURE dbo.usp_dynamic_dbo_table AS BEGIN SET NOCOUNT ON; EXEC (N'UPDATE dbo.T SET V = V + 1 WHERE Id = 1'); END;   -- 동적 SQL이지만 사용자가 dbo DML 권한을 가진 테이블
GO
CREATE PROCEDURE dbo.usp_crossdb_read AS BEGIN SET NOCOUNT ON; SELECT COUNT(*) AS n FROM SpikeLab_SP14_Other.dbo.X; END;
GO
CREATE PROCEDURE dbo.usp_crossdb_write AS BEGIN SET NOCOUNT ON; UPDATE SpikeLab_SP14_Other.dbo.X SET Id = Id WHERE Id = 1; END;
GO
SET NOCOUNT ON;
DECLARE @procs TABLE (n int IDENTITY(1, 1), p sysname);
INSERT @procs (p) VALUES (N'usp_static_secret'), (N'usp_dynamic_secret'), (N'usp_dynamic_dbo_table'), (N'usp_crossdb_read'), (N'usp_crossdb_write');
DECLARE @res TABLE (proc_name sysname, ok bit, errNo int NULL, xstate int NULL, userDuring sysname NULL, msg nvarchar(200) NULL);
DECLARE @n int = 1, @p sysname, @ok bit, @errNo int, @xstate int, @user sysname, @msg nvarchar(200);
WHILE @n <= (SELECT COUNT(*) FROM @procs)
BEGIN
    SELECT @p = p FROM @procs WHERE n = @n;
    SELECT @ok = 0, @errNo = NULL, @xstate = NULL, @user = NULL, @msg = NULL;
    BEGIN TRANSACTION;
    EXECUTE AS USER = N'dbr_preview_user';
    BEGIN TRY
        IF @p = N'usp_static_secret' EXEC dbo.usp_static_secret;
        ELSE IF @p = N'usp_dynamic_secret' EXEC dbo.usp_dynamic_secret;
        ELSE IF @p = N'usp_dynamic_dbo_table' EXEC dbo.usp_dynamic_dbo_table;
        ELSE IF @p = N'usp_crossdb_read' EXEC dbo.usp_crossdb_read;
        ELSE EXEC dbo.usp_crossdb_write;
        SELECT @ok = 1, @xstate = XACT_STATE(), @user = USER_NAME();
    END TRY
    BEGIN CATCH
        SELECT @errNo = ERROR_NUMBER(), @xstate = XACT_STATE(), @user = USER_NAME(), @msg = LEFT(ERROR_MESSAGE(), 200);
    END CATCH;
    IF XACT_STATE() = -1 ROLLBACK TRANSACTION;   -- doomed면 ROLLBACK 뒤 REVERT(SP10 사실 5)
    REVERT;
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    INSERT @res VALUES (@p, @ok, @errNo, @xstate, @user, @msg);
    SET @n += 1;
END;
SELECT proc_name, ok, errNo, xstate, userDuring, msg FROM @res ORDER BY (SELECT NULL);
SELECT 'H1_static_reference_passes_via_ownership_chain' AS hypothesis, CASE WHEN EXISTS (SELECT 1 FROM @res WHERE proc_name = N'usp_static_secret' AND ok = 1) THEN 'PASS' ELSE 'FAIL' END AS verdict
UNION ALL SELECT 'H2_dynamic_sql_on_unauthorized_table_gives_229', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE proc_name = N'usp_dynamic_secret' AND errNo = 229) THEN 'PASS' ELSE 'FAIL' END
UNION ALL SELECT 'H3_dynamic_sql_on_granted_table_succeeds', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE proc_name = N'usp_dynamic_dbo_table' AND ok = 1) THEN 'PASS' ELSE 'FAIL' END
UNION ALL SELECT 'H4_three_part_name_gives_916', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE proc_name = N'usp_crossdb_read' AND errNo = 916) AND EXISTS (SELECT 1 FROM @res WHERE proc_name = N'usp_crossdb_write' AND errNo = 916) THEN 'PASS' ELSE 'FAIL' END
UNION ALL SELECT 'H5_three_part_name_dooms_transaction', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE proc_name = N'usp_crossdb_read' AND xstate = -1) THEN 'PASS' ELSE 'FAIL' END;
GO
