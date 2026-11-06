/* SP2 — 관찰자 효과 (a)(c)(d)(e). (b)는 2주차 하네스.
   (a)만 읽기 전용이라 AW_Sandbox·pubs·CascadeLab에서도 그대로 실행한다(파일 첫 배치만).
   나머지: 1부(master) → common_audit_store.sql, common_hand_audit.sql (-d SpikeLab_SP2) → '-- PART 2' 이후 */
/* (a) 현재 DB의 First/Last 점유와 감사 Last 지정 가능 여부 */
SELECT DB_NAME() AS db, OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS tableName, e.type_desc AS evt,
       MAX(CASE WHEN e.is_first = 1 THEN t.name END) AS firstTrigger,
       MAX(CASE WHEN e.is_last = 1 THEN t.name END) AS lastTrigger,
       COUNT(*) AS afterTriggers,
       CASE WHEN MAX(CASE WHEN e.is_last = 1 AND t.name NOT LIKE N'dbr[_]audit[_]%' THEN 1 ELSE 0 END) = 1 THEN 'NONE(사용자 Last 점유)' ELSE 'LAST 가능' END AS auditOrder
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
WHERE t.parent_class = 1 AND t.is_instead_of_trigger = 0
GROUP BY t.parent_id, e.type_desc
ORDER BY tableName, evt;
GO
USE master;
GO
IF DB_ID(N'SpikeLab_SP2') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP2 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP2; END
GO
CREATE DATABASE SpikeLab_SP2;
GO
-- PART 2 (common_audit_store.sql, common_hand_audit.sql 설치 뒤)
USE SpikeLab_SP2;
GO
CREATE TABLE dbo.Plain   (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);   /* 감사 없음 */
CREATE TABLE dbo.Audited (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);   /* 감사 있음 */
CREATE TABLE dbo.Par     (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE dbo.Chi     (Id int NOT NULL PRIMARY KEY, Ref int NULL REFERENCES dbo.Par (Id) ON DELETE CASCADE, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE dbo.IdLog   (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE dbo.IdPlain   (RowId int IDENTITY(1,1) NOT NULL PRIMARY KEY, Id int NOT NULL, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE dbo.IdAudited (RowId int IDENTITY(1000,1) NOT NULL PRIMARY KEY, Id int NOT NULL UNIQUE, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
GO
INSERT dbo.Plain   (Id) VALUES (1), (2), (3);
INSERT dbo.Audited (Id) VALUES (1), (2), (3);
INSERT dbo.Par (Id) VALUES (1), (2);
INSERT dbo.Chi (Id, Ref) VALUES (10, 1);   /* Par 2에는 자식 없음 → 연쇄 0행 */
GO
/* (e) 사용자 트리거가 읽는 @@IDENTITY·SCOPE_IDENTITY(): 감사 유무별 (IdAudited의 Id 열은 감사 템플릿 키로 쓰기 위한 것) */
CREATE TRIGGER dbo.trg_IdPlain_I ON dbo.IdPlain AFTER INSERT AS
BEGIN SET NOCOUNT ON; INSERT dbo.IdLog (Id, Ref, Ref2, Src) SELECT 1, CONVERT(int, @@IDENTITY), CONVERT(int, SCOPE_IDENTITY()), 'plain'; END
GO
CREATE TRIGGER dbo.trg_IdAudited_I ON dbo.IdAudited AFTER INSERT AS
BEGIN SET NOCOUNT ON; INSERT dbo.IdLog (Id, Ref, Ref2, Src) SELECT 2, CONVERT(int, @@IDENTITY), CONVERT(int, SCOPE_IDENTITY()), 'audited'; END
GO
EXEC dbo.spike_InstallAudit N'dbo', N'Audited',   'U';
EXEC dbo.spike_InstallAudit N'dbo', N'Par',       'D';
EXEC dbo.spike_InstallAudit N'dbo', N'Chi',       'D';
EXEC dbo.spike_InstallAudit N'dbo', N'IdAudited', 'I';
GO
/* (c) DML 직후 @@ROWCOUNT·ROWCOUNT_BIG() (d) 0행 발동 (e) @@IDENTITY */
SET NOCOUNT ON;
DECLARE @r TABLE (chk varchar(40), plainVal bigint NULL, auditedVal bigint NULL);
DECLARE @rc1 int, @rb1 bigint, @rc2 int, @rb2 bigint, @b bigint;
BEGIN TRAN;
SET @b = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
UPDATE dbo.Plain   SET Ref = 1 WHERE Id <= 3;
SELECT @rc1 = @@ROWCOUNT, @rb1 = ROWCOUNT_BIG();
UPDATE dbo.Audited SET Ref = 1 WHERE Id <= 3;
SELECT @rc2 = @@ROWCOUNT, @rb2 = ROWCOUNT_BIG();
INSERT @r VALUES ('c_@@ROWCOUNT', @rc1, @rc2), ('c_ROWCOUNT_BIG', @rb1, @rb2);
UPDATE dbo.Audited SET Ref = 2 WHERE 1 = 0;   /* (d) 직접 대상 0행 */
INSERT @r SELECT 'd_direct_zero_row_header', NULL, COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @b AND tableName = N'[dbo].[Audited]' AND insertedCount = 0;
DELETE dbo.Par WHERE Id = 2;                  /* (d) FK 연쇄 0행 */
INSERT @r SELECT 'd_cascade_zero_row_header', NULL, COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @b AND tableName = N'[dbo].[Chi]';
DECLARE @id1 numeric(38,0), @id2 numeric(38,0);
INSERT dbo.IdPlain (Id) VALUES (1);
SET @id1 = @@IDENTITY;      /* 바로 읽는다: 다른 INSERT가 끼면 값이 바뀔 수 있다 */
INSERT dbo.IdAudited (Id) VALUES (1);
SET @id2 = @@IDENTITY;
INSERT @r VALUES ('e_caller_@@IDENTITY', @id1, @id2);
INSERT @r SELECT 'e_trigger_@@IDENTITY', (SELECT Ref FROM dbo.IdLog WHERE Id = 1), (SELECT Ref FROM dbo.IdLog WHERE Id = 2);
INSERT @r SELECT 'e_trigger_SCOPE_IDENTITY', (SELECT Ref2 FROM dbo.IdLog WHERE Id = 1), (SELECT Ref2 FROM dbo.IdLog WHERE Id = 2);
ROLLBACK;
SELECT r.*, CASE WHEN r.chk LIKE 'c[_]%' THEN CASE WHEN r.plainVal = r.auditedVal THEN 'SAME' ELSE 'DIFF' END
                 WHEN r.chk LIKE 'd[_]%' THEN CASE WHEN r.auditedVal > 0 THEN 'RECORDED' ELSE 'NOT_RECORDED' END
                 ELSE '기대: plain=1, audited=1000 (감사가 IDENTITY를 안 바꿈)' END AS verdict
FROM @r AS r;
GO
/* 다음: sp2c_rowcount.ps1 → sp2_99_drop.sql */
