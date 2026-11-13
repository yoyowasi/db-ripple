/* SP13 — EXEC @rc 래퍼의 반환값·행 수·결과 집합: 서버 쪽 준비(전용 DB, 프로시저 6개)와 T-SQL로 볼 수 있는 값(@@ROWCOUNT, 반환값)
   클라이언트가 보는 값(결과 집합 수, 드라이버 RecordsAffected, ReturnValue 매개변수)은 spikes\sql\sp13_20_client.ps1이 잰다.
   실행: .\scripts\run-sql.ps1 spikes\sql\sp13_10_setup.sql -Part 1 -Log spikes\logs\sp13-part1.log
         .\scripts\run-sql.ps1 spikes\sql\sp13_10_setup.sql -Part 2 -Database SpikeLab_SP13 -ContinueOnError -Log spikes\logs\sp13-part2.log
   이어서(저장소 루트, 같은 PowerShell): . .\scripts\env.ps1; .\spikes\sql\sp13_20_client.ps1
   정리: .\scripts\run-sql.ps1 spikes\sql\sp_exec_99_drop.sql -Log spikes\logs\sp-exec-99-drop.log
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE master;
GO
IF DB_ID(N'SpikeLab_SP13') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP13 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP13;
END;
GO
CREATE DATABASE SpikeLab_SP13;
GO
-- PART 2
USE SpikeLab_SP13;
GO
CREATE TABLE dbo.T (Id int NOT NULL CONSTRAINT PK_T PRIMARY KEY, V int NOT NULL);
INSERT dbo.T (Id, V) VALUES (1, 10), (2, 20), (3, 30);
GO
/* 1. RETURN 5, UPDATE 1행, NOCOUNT OFF(기본) */
CREATE PROCEDURE dbo.p_ret5 AS
BEGIN
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    RETURN 5;
END;
GO
/* 2. RETURN 없음(암묵적 0), UPDATE 2행 */
CREATE PROCEDURE dbo.p_noret AS
BEGIN
    UPDATE dbo.T SET V = V + 1 WHERE Id IN (1, 2);
END;
GO
/* 3. 결과 집합 둘 + UPDATE 3행 + RETURN 7 */
CREATE PROCEDURE dbo.p_two_resultsets AS
BEGIN
    UPDATE dbo.T SET V = V + 1;
    SELECT Id, V FROM dbo.T ORDER BY Id;
    SELECT COUNT(*) AS n FROM dbo.T;
    RETURN 7;
END;
GO
/* 4. NOCOUNT ON + UPDATE 3행 + RETURN 9 */
CREATE PROCEDURE dbo.p_nocount_on AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.T SET V = V + 1;
    RETURN 9;
END;
GO
/* 5. 문장 오류(RAISERROR 16)만 내고 계속 진행한 뒤 RETURN 3 */
CREATE PROCEDURE dbo.p_raiserror_continue AS
BEGIN
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    RAISERROR (N'sp13 statement error', 16, 1);
    RETURN 3;
END;
GO
/* 6. THROW로 중단: 그 뒤 RETURN에 닿지 않는다 */
CREATE PROCEDURE dbo.p_throw AS
BEGIN
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    THROW 50000, N'sp13 thrown', 1;
    RETURN 11;
END;
GO
/* T-SQL로 볼 수 있는 값: EXEC @rc = ... 뒤의 @rc와 @@ROWCOUNT (오류를 내는 둘은 TRY/CATCH로 받는다). 모두 트랜잭션 안에서 실행하고 롤백한다. */
SET NOCOUNT ON;
DECLARE @out TABLE (proc_name varchar(30), rc int NULL, rowcount_after bigint NULL, errNo int NULL);
DECLARE @rc int, @rows bigint;
BEGIN TRANSACTION;
SET @rc = NULL; EXEC @rc = dbo.p_ret5;                      SET @rows = @@ROWCOUNT; INSERT @out VALUES ('p_ret5', @rc, @rows, NULL);
SET @rc = NULL; EXEC @rc = dbo.p_noret;                     SET @rows = @@ROWCOUNT; INSERT @out VALUES ('p_noret', @rc, @rows, NULL);
SET @rc = NULL; EXEC @rc = dbo.p_nocount_on;                SET @rows = @@ROWCOUNT; INSERT @out VALUES ('p_nocount_on', @rc, @rows, NULL);
SET @rc = NULL; EXEC @rc = dbo.p_raiserror_continue;        SET @rows = @@ROWCOUNT; INSERT @out VALUES ('p_raiserror_continue', @rc, @rows, NULL);
BEGIN TRY
    SET @rc = NULL; EXEC @rc = dbo.p_throw;
    INSERT @out VALUES ('p_throw', @rc, NULL, NULL);
END TRY
BEGIN CATCH
    INSERT @out VALUES ('p_throw', @rc, NULL, ERROR_NUMBER());   -- 오류로 끝나면 @rc가 할당되지 않는다(NULL 그대로)
END CATCH;
ROLLBACK TRANSACTION;
SELECT proc_name, rc, rowcount_after, errNo FROM @out;
GO
/* 결과 집합을 내는 p_two_resultsets는 T-SQL 변수로 받을 수 없어 클라이언트 스크립트에서 잰다(sp13_20_client.ps1). */
