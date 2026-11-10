-- R18 재현: 비지속 계산 열의 식이 오버플로하는 행. 감사 트리거(생성기와 같은 모양: SET NOCOUNT ON, 행마다 FOR JSON PATH)가 그 열을 읽으면
-- 원래는 성공하는 UPDATE가 트리거 안 오류로 실패하는지, 오류의 번호·프로시저·메시지가 무엇인지 본다. master에서 -ContinueOnError로 실행. 임시 DB dbr_r18은 끝에서 지운다.
IF DB_ID(N'dbr_r18') IS NOT NULL DROP DATABASE dbr_r18;
GO
CREATE DATABASE dbr_r18;
GO
USE dbr_r18;
GO
CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY, a int NOT NULL, c AS (a * 1000));  -- 비지속 계산 열
INSERT INTO dbo.T (id, a) VALUES (1, 1), (2, 3000000);  -- id 2는 c 계산 시 int 오버플로
CREATE TABLE dbo.AuditLike (seq int IDENTITY PRIMARY KEY, rowJson nvarchar(max) NULL);  -- 재현용(제품 감사 저장소 아님)
GO
-- (1) 감사 없음: c를 읽지 않는 UPDATE는 성공해야 한다
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.T SET a = a WHERE id = 2;
    SELECT N'no_audit' AS phase, N'OK' AS result, @@ROWCOUNT AS rowsAffected;
    ROLLBACK;
END TRY
BEGIN CATCH
    SELECT N'no_audit' AS phase, N'FAILED' AS result, ERROR_NUMBER() AS errNo, ERROR_PROCEDURE() AS errProc, LEFT(ERROR_MESSAGE(), 200) AS errMsg;
    IF @@TRANCOUNT > 0 ROLLBACK;
END CATCH;
GO
CREATE TRIGGER dbo.dbr_audit_T_U ON dbo.T AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.AuditLike (rowJson)
    SELECT (SELECT i.id AS id, i.a AS a, i.c AS c FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) FROM inserted AS i;
END;
GO
-- (2) 감사 있음: 같은 UPDATE
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.T SET a = a WHERE id = 2;
    SELECT N'with_audit' AS phase, N'OK' AS result;
    ROLLBACK;
END TRY
BEGIN CATCH
    SELECT N'with_audit' AS phase, N'FAILED' AS result, ERROR_NUMBER() AS errNo, ERROR_PROCEDURE() AS errProc,
           LEFT(ERROR_MESSAGE(), 200) AS errMsg, XACT_STATE() AS xactState;
    IF @@TRANCOUNT > 0 ROLLBACK;
END CATCH;
GO
-- (3) 감사 있음, 오버플로 없는 행은 성공
BEGIN TRAN;
UPDATE dbo.T SET a = a WHERE id = 1;
SELECT N'with_audit_ok_row' AS phase, (SELECT COUNT(*) FROM dbo.AuditLike) AS auditRows;
ROLLBACK;
GO
USE master;
DROP DATABASE dbr_r18;
GO
