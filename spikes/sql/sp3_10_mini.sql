/* SP3 — 자기 테이블 재갱신(uSalesOrderHeader 유형), RECURSIVE_TRIGGERS OFF와 ON 비교 (미니)
   순서: 이 파일 1부(master) → common_audit_store.sql, common_hand_audit.sql (-d SpikeLab_SP3) → 이 파일 2부부터 끝까지.
   실행 편의상 한 파일이지만 1부와 2부를 나눠 실행한다(주석 '-- PART 2' 이후).
   열 대응: Ref = Status, Ref2 = RevisionNumber, Src = Comment */
USE master;
GO
IF DB_ID(N'SpikeLab_SP3') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP3 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP3;
END
GO
CREATE DATABASE SpikeLab_SP3;
GO
-- PART 2 (common_audit_store.sql, common_hand_audit.sql 설치 뒤)
USE SpikeLab_SP3;
GO
CREATE TABLE dbo.SOH (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
GO
/* AW uSalesOrderHeader와 같은 꼴: Status를 안 바꾸면 같은 행의 Rev를 +1. ON에서 무한 재귀를 막으려고 NOT UPDATE(Ref2)만 추가 */
CREATE TRIGGER dbo.uSOH ON dbo.SOH AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF NOT UPDATE(Ref) AND NOT UPDATE(Ref2)
        UPDATE dbo.SOH SET Ref2 = Ref2 + 1 WHERE Id IN (SELECT i.Id FROM inserted AS i);
END
GO
EXEC dbo.spike_InstallAudit N'dbo', N'SOH', 'U';
GO
INSERT dbo.SOH (Id, Ref, Ref2, Src) VALUES (1, 5, 0, 'init'), (2, 5, 0, 'init');
GO
CREATE OR ALTER PROCEDURE dbo.spike_sp3_run @label varchar(10)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @before TABLE (Id int PRIMARY KEY, Ref2 int, Src varchar(40));
    DECLARE @hdr TABLE (captureSeq bigint, nestLevel int, insertedCount bigint, afterRev int);
    DECLARE @now TABLE (Id int PRIMARY KEY, Ref2 int, Src varchar(40));
    DECLARE @err int = NULL;
    INSERT @before SELECT Id, Ref2, Src FROM dbo.SOH;
    BEGIN TRAN;
    DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
    BEGIN TRY
        UPDATE dbo.SOH SET Src = 'sp3' WHERE Id = 1;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_NUMBER();
    END CATCH
    IF XACT_STATE() <> -1
    BEGIN
        INSERT @hdr SELECT h.captureSeq, h.nestLevel, h.insertedCount,
               (SELECT MAX(CONVERT(int, JSON_VALUE(r.rowJson, '$.Ref2'))) FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq AND r.imageKind = 'INSERTED')
        FROM audit.AuditHeader AS h WHERE h.captureSeq > @b;
        INSERT @now SELECT Id, Ref2, Src FROM dbo.SOH;   /* 재조회(같은 트랜잭션) */
    END
    ROLLBACK;
    SELECT @label AS mode_, DATABASEPROPERTYEX(DB_NAME(), 'IsRecursiveTriggersEnabled') AS recursiveOn, @err AS errorNumber, * FROM @hdr ORDER BY captureSeq;
    DECLARE @lastAuditRev int = (SELECT TOP (1) afterRev FROM @hdr ORDER BY captureSeq DESC);
    DECLARE @rereadRev int = (SELECT Ref2 FROM @now WHERE Id = 1);
    SELECT @label AS mode_,
           (SELECT COUNT(*) FROM @hdr) AS auditHeaders,
           (SELECT COUNT(*) FROM @hdr WHERE nestLevel >= 2) AS nestedHeaders,
           @lastAuditRev AS lastAuditAfterRev, @rereadRev AS rereadRev,
           (SELECT COUNT(*) FROM @before AS b JOIN @now AS n ON n.Id = b.Id WHERE n.Ref2 <> b.Ref2 OR n.Src <> b.Src) AS netDiffRows,
           CASE WHEN @err IS NOT NULL THEN 'ERROR'
                WHEN EXISTS (SELECT 1 FROM @hdr WHERE nestLevel >= 2) THEN 'CAPTURED'
                WHEN @rereadRev <> @lastAuditRev THEN 'RESIDUAL_BY_REREAD'
                WHEN EXISTS (SELECT 1 FROM @before AS b JOIN @now AS n ON n.Id = b.Id WHERE n.Ref2 <> b.Ref2 AND n.Id <> 1) THEN 'NETDIFF_ONLY'
                ELSE 'MISSED' END AS verdict;
END
GO
ALTER DATABASE SpikeLab_SP3 SET RECURSIVE_TRIGGERS OFF;
GO
EXEC dbo.spike_sp3_run 'OFF';
GO
ALTER DATABASE SpikeLab_SP3 SET RECURSIVE_TRIGGERS ON;
GO
EXEC dbo.spike_sp3_run 'ON';
GO
ALTER DATABASE SpikeLab_SP3 SET RECURSIVE_TRIGGERS OFF;
GO
USE master;
GO
IF DB_ID(N'SpikeLab_SP3') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP3 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP3;
END
GO
