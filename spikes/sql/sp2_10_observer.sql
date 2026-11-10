/* SP2 — 관찰자 효과 (a)(c)(d)(e). (b)는 2주차 하네스.
   (a)만 읽기 전용이라 AW_Sandbox·pubs·CascadeLab에서도 그대로 실행한다(파일 첫 배치만).
   나머지: 1부(master) → common_audit_store.sql, common_hand_audit.sql (-d SpikeLab_SP2) → '-- PART 2' 이후 */
/* (a) 현재 DB의 First/Last 점유와 감사 Last 지정 가능 여부 */
SELECT DB_NAME() AS db, OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS tableName, e.type_desc AS evt,
       MAX(CASE WHEN e.is_first = 1 THEN t.name END) AS firstTrigger,
       MAX(CASE WHEN e.is_last = 1 THEN t.name END) AS lastTrigger,
       COUNT(*) AS afterTriggers,
       CASE WHEN MAX(CASE WHEN e.is_last = 1 AND t.name NOT LIKE N'dbr[_]audit[_]%' THEN 1 ELSE 0 END) = 1 THEN N'NONE(사용자 Last 점유)' ELSE N'LAST 가능' END AS auditOrder
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
/* (c 보조, 2026-11-10 추가) 감사 트리거의 마지막 INSERT는 발동 행 수와 같아 '트리거 행 수가 호출자에게 새는지'를 가르지 못한다.
   마지막 문장이 5행인 트리거(NOCOUNT ON)와 NOCOUNT 없이 2행을 넣는 트리거로 따로 본다 */
CREATE TABLE dbo.Last5   (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE dbo.NoCnt   (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE dbo.Scratch (Seq int NOT NULL, Src varchar(40) NULL);
GO
INSERT dbo.Plain   (Id) VALUES (1), (2), (3);
INSERT dbo.Audited (Id) VALUES (1), (2), (3);
INSERT dbo.Par (Id) VALUES (1), (2);
INSERT dbo.Chi (Id, Ref) VALUES (10, 1);   /* Par 2에는 자식 없음 → 연쇄 0행 */
INSERT dbo.Last5 (Id) VALUES (1), (2), (3);
INSERT dbo.NoCnt (Id) VALUES (1), (2), (3);
GO
CREATE TRIGGER dbo.trg_Last5_U ON dbo.Last5 AFTER UPDATE AS
BEGIN SET NOCOUNT ON; INSERT dbo.Scratch (Seq, Src) SELECT v.n, 'last5' FROM (VALUES (1), (2), (3), (4), (5)) AS v (n); END
GO
CREATE TRIGGER dbo.trg_NoCnt_U ON dbo.NoCnt AFTER UPDATE AS
BEGIN INSERT dbo.Scratch (Seq, Src) VALUES (1, 'nocnt'), (2, 'nocnt'); END
GO
/* (c 보조2) 앞 트리거의 마지막 문장 행 수(5)가 다음 트리거 시작 시점 @@ROWCOUNT에 보이는가(D3 'First 금지' 근거, 감사 NONE일 때의 위험).
   First 트리거가 5행을 넣고, Last 트리거가 AW와 같은 모양(DECLARE → SET @Count = @@ROWCOUNT)으로 읽는다 */
CREATE TABLE dbo.Seq2 (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
GO
INSERT dbo.Seq2 (Id) VALUES (1), (2), (3);
GO
CREATE TRIGGER dbo.trg_Seq2_first ON dbo.Seq2 AFTER UPDATE AS
BEGIN SET NOCOUNT ON; INSERT dbo.Scratch (Seq, Src) SELECT v.n, 'first5' FROM (VALUES (1), (2), (3), (4), (5)) AS v (n); END
GO
CREATE TRIGGER dbo.trg_Seq2_last ON dbo.Seq2 AFTER UPDATE AS
BEGIN
    DECLARE @Count int;
    SET @Count = @@ROWCOUNT;
    SET NOCOUNT ON;
    INSERT dbo.IdLog (Id, Ref, Ref2, Src) SELECT 3, @Count, (SELECT COUNT(*) FROM inserted), 'seq2_last';
END
GO
EXEC sys.sp_settriggerorder @triggername = N'dbo.trg_Seq2_first', @order = N'First', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'dbo.trg_Seq2_last', @order = N'Last', @stmttype = N'UPDATE';
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
/* (c 보조) plainVal = Plain 3행 기준, auditedVal = 보조 트리거 테이블 3행 UPDATE 직후 값. 새면 5 또는 2가 된다 */
UPDATE dbo.Last5 SET Ref = 1 WHERE Id <= 3;
SELECT @rc2 = @@ROWCOUNT, @rb2 = ROWCOUNT_BIG();
INSERT @r VALUES ('c_aux_last5_@@ROWCOUNT', @rc1, @rc2), ('c_aux_last5_ROWCOUNT_BIG', @rb1, @rb2);
UPDATE dbo.NoCnt SET Ref = 1 WHERE Id <= 3;
SELECT @rc2 = @@ROWCOUNT, @rb2 = ROWCOUNT_BIG();
INSERT @r VALUES ('c_aux_nocount_@@ROWCOUNT', @rc1, @rc2), ('c_aux_nocount_ROWCOUNT_BIG', @rb1, @rb2);
/* (c 보조2) plainVal = 3 기준, auditedVal = Last 트리거가 시작할 때 읽은 @@ROWCOUNT. 앞 트리거 값이 보이면 5 */
UPDATE dbo.Seq2 SET Ref = 1 WHERE Id <= 3;
INSERT @r SELECT 'c_aux_between_triggers_@@ROWCOUNT', @rc1, (SELECT Ref FROM dbo.IdLog WHERE Id = 3);
UPDATE dbo.Audited SET Ref = 2 WHERE 1 = 0;   /* (d) 직접 대상 0행 */
INSERT @r SELECT 'd_direct_zero_row_header', NULL, COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @b AND tableName = N'[dbo].[Audited]' AND insertedCount = 0;
DELETE dbo.Par WHERE Id = 2;                  /* (d) FK 연쇄 0행 */
INSERT @r SELECT 'd_cascade_zero_row_header', NULL, COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @b AND tableName = N'[dbo].[Chi]';
/* (d 대조) 부모 DELETE 자체는 기록됐는가, 자식 1행 연쇄는 발동하는가 */
INSERT @r SELECT 'd_cascade_parent_header', NULL, COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @b AND tableName = N'[dbo].[Par]' AND deletedCount = 1;
DELETE dbo.Par WHERE Id = 1;                  /* 자식 Chi 10 연쇄 1행 */
INSERT @r SELECT 'd_cascade_one_row_header', NULL, COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @b AND tableName = N'[dbo].[Chi]' AND deletedCount = 1;
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
                 ELSE N'기대: plain=1, audited=1000 (감사가 IDENTITY를 안 바꿈)' END AS verdict
FROM @r AS r;
GO
/* 감사 순서(Last)와 QUOTED_IDENTIFIER OFF로 저장된 모듈(0행이어야 함, 2026-11-10 run-sql -I 이후) */
SELECT 'audit_order' AS chk, tableName, triggerName, evt, is_first, is_last, auditOrder FROM dbo.spike_AuditOrder WHERE auditOrder IS NOT NULL ORDER BY tableName, evt;
SELECT 'modules' AS chk, COUNT(*) AS modules, SUM(CASE WHEN uses_quoted_identifier = 0 THEN 1 ELSE 0 END) AS quotedIdentOff FROM sys.sql_modules;
GO
/* 다음: sp2c_rowcount.ps1 → sp2_99_drop.sql */
