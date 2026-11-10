/* SP10 — 실행 주체: 열린 트랜잭션 안 EXECUTE AS USER/REVERT 반복, 감사 트리거의 소유권 체인, 음성 대조, 다른 DB 읽기
   순서: 1부(master) → common_audit_store.sql, common_hand_audit.sql (-d SpikeLab_SP10) → '-- PART 2' 이후 */
USE master;
GO
IF DB_ID(N'SpikeLab_SP10') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP10 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP10; END
IF DB_ID(N'SpikeLab_SP10_Other') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP10_Other SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP10_Other; END
GO
CREATE DATABASE SpikeLab_SP10;
GO
CREATE DATABASE SpikeLab_SP10_Other;   /* 스냅샷 대신 쓰는 '다른 DB' */
GO
USE SpikeLab_SP10_Other;
GO
CREATE TABLE dbo.X (Id int NOT NULL PRIMARY KEY);
INSERT dbo.X VALUES (1);
GO
-- PART 2 (common_audit_store.sql, common_hand_audit.sql 설치 뒤)
USE SpikeLab_SP10;
GO
CREATE TABLE dbo.T  (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE dbo.T2 (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
INSERT dbo.T  VALUES (1, 0, 0, 'a');
INSERT dbo.T2 VALUES (1, 0, 0, 'a');
GO
EXEC dbo.spike_InstallAudit N'dbo', N'T', 'U';   /* 트리거 dbo 소유, audit 스키마 dbo 소유 → 소유권 체인 */
GO
/* 음성 대조: 소유자가 다른 감사 저장소 */
CREATE USER spk_owner WITHOUT LOGIN;
GO
CREATE SCHEMA auditx AUTHORIZATION spk_owner;
GO
CREATE SEQUENCE auditx.Seq AS bigint START WITH 1 INCREMENT BY 1;
CREATE TABLE auditx.Hdr (seq bigint NOT NULL PRIMARY KEY, nestLevel int NOT NULL);
GO
CREATE TRIGGER dbo.dbr_audit_T2_U ON dbo.T2 AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @s bigint;
    SET @s = NEXT VALUE FOR auditx.Seq;
    INSERT auditx.Hdr (seq, nestLevel) VALUES (@s, TRIGGER_NESTLEVEL());
END
GO
CREATE USER spk_user WITHOUT LOGIN;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO spk_user;   /* DML만. audit·auditx 권한 없음 */
GO
/* 1. 열린 트랜잭션 안 5회 반복 */
SET NOCOUNT ON;
DECLARE @log TABLE (i int, step varchar(30), userName sysname NULL, tranCount int, ok bit, errNo int NULL, xstate int, detail nvarchar(200) NULL);
DECLARE @hdr TABLE (captureSeq bigint, nestLevel int, tranCount int);
DECLARE @i int = 1, @n int;
DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
BEGIN TRAN;
WHILE @i <= 5
BEGIN
    EXECUTE AS USER = N'spk_user';
    BEGIN TRY
        UPDATE dbo.T SET Ref = Ref + 1 WHERE Id = 1;
        INSERT @log VALUES (@i, 'update_as_user', USER_NAME(), @@TRANCOUNT, 1, NULL, XACT_STATE(), NULL);
    END TRY
    BEGIN CATCH
        INSERT @log VALUES (@i, 'update_as_user', USER_NAME(), @@TRANCOUNT, 0, ERROR_NUMBER(), XACT_STATE(), LEFT(ERROR_MESSAGE(), 200));
    END CATCH
    BEGIN TRY
        SELECT @n = COUNT(*) FROM audit.AuditHeader;   /* 직접 읽기는 229 예상(권한 없음 확인) */
        INSERT @log VALUES (@i, 'direct_audit_read', USER_NAME(), @@TRANCOUNT, 1, NULL, XACT_STATE(), NULL);
    END TRY
    BEGIN CATCH
        INSERT @log VALUES (@i, 'direct_audit_read', USER_NAME(), @@TRANCOUNT, 0, ERROR_NUMBER(), XACT_STATE(), LEFT(ERROR_MESSAGE(), 200));
    END CATCH
    REVERT;
    BEGIN TRY
        SELECT @n = COUNT(*) FROM SpikeLab_SP10_Other.dbo.X;   /* REVERT 뒤 다른 DB 읽기: 성공 예상 */
        INSERT @log VALUES (@i, 'other_db_after_revert', USER_NAME(), @@TRANCOUNT, 1, NULL, XACT_STATE(), NULL);
    END TRY
    BEGIN CATCH
        INSERT @log VALUES (@i, 'other_db_after_revert', USER_NAME(), @@TRANCOUNT, 0, ERROR_NUMBER(), XACT_STATE(), LEFT(ERROR_MESSAGE(), 200));
    END CATCH
    SET @i += 1;
END
INSERT @hdr SELECT captureSeq, nestLevel, tranCount FROM audit.AuditHeader WHERE captureSeq > @b;
/* 가장 중 다른 DB 읽기: 실패(916 등) 예상. 트랜잭션을 망칠 수 있어 마지막에 둔다 */
EXECUTE AS USER = N'spk_user';
BEGIN TRY
    SELECT @n = COUNT(*) FROM SpikeLab_SP10_Other.dbo.X;
    INSERT @log VALUES (0, 'other_db_impersonating', USER_NAME(), @@TRANCOUNT, 1, NULL, XACT_STATE(), NULL);
END TRY
BEGIN CATCH
    INSERT @log VALUES (0, 'other_db_impersonating', USER_NAME(), @@TRANCOUNT, 0, ERROR_NUMBER(), XACT_STATE(), LEFT(ERROR_MESSAGE(), 200));
END CATCH
/* 1차 실행(2026-11-10): 여기서 트랜잭션이 doomed(-1)이면 REVERT가 3930으로 실패하고 배치가 끝났다
   → REVERT를 TRY로 감싸 결과를 남기고, ROLLBACK 뒤에 아직 가장 중이면 REVERT */
BEGIN TRY
    REVERT;
    INSERT @log VALUES (0, 'revert_after_other_db', USER_NAME(), @@TRANCOUNT, 1, NULL, XACT_STATE(), NULL);
END TRY
BEGIN CATCH
    INSERT @log VALUES (0, 'revert_after_other_db', USER_NAME(), @@TRANCOUNT, 0, ERROR_NUMBER(), XACT_STATE(), LEFT(ERROR_MESSAGE(), 200));
END CATCH
IF @@TRANCOUNT > 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
INSERT @log VALUES (0, 'state_after_rollback', USER_NAME(), @@TRANCOUNT, 1, NULL, XACT_STATE(), NULL);
SELECT * FROM @log ORDER BY i, step;
SELECT * FROM @hdr ORDER BY captureSeq;
SELECT v.chk, v.verdict FROM (VALUES
 ('repeat_impersonation', CASE WHEN (SELECT COUNT(*) FROM @log WHERE step = 'update_as_user' AND ok = 1 AND userName = N'spk_user') = 5 THEN 'PASS' ELSE 'FAIL' END),
 ('ownership_chain_header_and_sequence', CASE WHEN (SELECT COUNT(*) FROM @hdr) = 5 THEN 'PASS' ELSE 'FAIL' END),
 ('no_direct_audit_permission', CASE WHEN (SELECT COUNT(*) FROM @log WHERE step = 'direct_audit_read' AND ok = 0 AND errNo = 229) = 5 THEN 'PASS' ELSE 'CHECK' END),
 ('other_db_after_revert_ok', CASE WHEN (SELECT COUNT(*) FROM @log WHERE step = 'other_db_after_revert' AND ok = 1) = 5 THEN 'PASS' ELSE 'FAIL' END),
 ('other_db_impersonating_fails', CASE WHEN EXISTS (SELECT 1 FROM @log WHERE step = 'other_db_impersonating' AND ok = 0) THEN 'PASS' ELSE 'FAIL' END)
) AS v (chk, verdict);
GO
/* 2. 음성 대조: 소유자가 다른 감사 저장소 → 229 예상, 트리거 안 오류라 트랜잭션 doomed(-1) 예상 */
SET NOCOUNT ON;
DECLARE @r TABLE (errNo int NULL, xstate int, msg nvarchar(300) NULL);
BEGIN TRAN;
EXECUTE AS USER = N'spk_user';
BEGIN TRY
    UPDATE dbo.T2 SET Ref = Ref + 1 WHERE Id = 1;
    INSERT @r VALUES (NULL, XACT_STATE(), N'no error');
END TRY
BEGIN CATCH
    INSERT @r VALUES (ERROR_NUMBER(), XACT_STATE(), LEFT(ERROR_MESSAGE(), 300));
END CATCH
/* doomed면 REVERT가 3930일 수 있어(1부 참고) TRY로 감싸 기록하고 ROLLBACK 뒤 REVERT */
DECLARE @rv int = NULL, @rvx int = NULL;
BEGIN TRY REVERT; END TRY
BEGIN CATCH SELECT @rv = ERROR_NUMBER(), @rvx = XACT_STATE(); END CATCH
IF @@TRANCOUNT > 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
SELECT 'negative_control' AS chk, errNo, xstate, msg, @rv AS revertErrNo, @rvx AS revertXstate, USER_NAME() AS userAfter,
       CASE WHEN errNo = 229 THEN 'PASS' ELSE 'CHECK' END AS verdict FROM @r;
GO
/* QUOTED_IDENTIFIER OFF로 저장된 모듈(0행이어야 함, 2026-11-10 run-sql -I 이후 재실행) */
SELECT 'quoted_identifier_off_modules' AS chk, OBJECT_SCHEMA_NAME(object_id) AS schemaName, OBJECT_NAME(object_id) AS objectName FROM sys.sql_modules WHERE uses_quoted_identifier = 0;
SELECT 'modules' AS chk, COUNT(*) AS modules, SUM(CASE WHEN uses_quoted_identifier = 0 THEN 1 ELSE 0 END) AS quotedIdentOff FROM sys.sql_modules;
GO
USE master;
GO
IF DB_ID(N'SpikeLab_SP10') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP10 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP10; END
IF DB_ID(N'SpikeLab_SP10_Other') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP10_Other SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP10_Other; END
GO
