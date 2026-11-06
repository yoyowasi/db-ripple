/* SP1 — 미니 스키마 CL06·CL07·CL09·CL13 (모든 테이블: Id int PK, Ref int, Ref2 int, Src varchar(40))
   전제: SpikeLab_SP1에 common_audit_store.sql, common_hand_audit.sql 설치 완료 */
USE SpikeLab_SP1;
GO
CREATE SCHEMA cl06;
GO
CREATE SCHEMA cl07;
GO
CREATE SCHEMA cl09;
GO
CREATE SCHEMA cl13;
GO
/* CL06: P ←CASCADE C ←CASCADE G, C의 사용자 트리거가 Log에 쓴다 (H2 깊이, H1) */
CREATE TABLE cl06.P   (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl06.C   (Id int NOT NULL PRIMARY KEY, Ref int NULL REFERENCES cl06.P (Id) ON DELETE CASCADE, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl06.G   (Id int NOT NULL PRIMARY KEY, Ref int NULL REFERENCES cl06.C (Id) ON DELETE CASCADE, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl06.Log (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
GO
CREATE TRIGGER cl06.trg_C_D ON cl06.C AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT cl06.Log (Id, Ref, Src) SELECT d.Id, d.Id, 'trg_C_D' FROM deleted AS d;
END
GO
/* CL07: A 트리거 → B, D 쓰기, B 트리거 → C 쓰기 (3단 사슬, H1 괄호) */
CREATE TABLE cl07.A (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl07.B (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl07.C (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl07.D (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
GO
CREATE TRIGGER cl07.trg_A_I ON cl07.A AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    INSERT cl07.B (Id, Ref, Src) SELECT i.Id * 10, i.Id, 'trg_A_I' FROM inserted AS i;
    INSERT cl07.D (Id, Ref, Src) SELECT i.Id * 10, i.Id, 'trg_A_I' FROM inserted AS i;
END
GO
CREATE TRIGGER cl07.trg_B_I ON cl07.B AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    INSERT cl07.C (Id, Ref, Src) SELECT i.Id * 10, i.Id, 'trg_B_I' FROM inserted AS i;
END
GO
/* CL09: P ←CASCADE K, P ←CASCADE M, Q ←CASCADE K. M의 트리거가 Q를 지워 2차 CASCADE가 K에 닿는다 (H4·H5) */
CREATE TABLE cl09.P (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl09.Q (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl09.M (Id int NOT NULL PRIMARY KEY, Ref int NULL REFERENCES cl09.P (Id) ON DELETE CASCADE, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl09.K (Id int NOT NULL PRIMARY KEY,
                     Ref  int NULL REFERENCES cl09.P (Id) ON DELETE CASCADE,
                     Ref2 int NULL REFERENCES cl09.Q (Id) ON DELETE CASCADE, Src varchar(40) NULL);
GO
CREATE TRIGGER cl09.trg_M_D ON cl09.M AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    DELETE q FROM cl09.Q AS q JOIN deleted AS d ON q.Id = d.Ref2;   /* M.Ref2 = Q.Id (FK 없음) */
END
GO
/* CL13: T에 사용자 AFTER DELETE 트리거 3개(First, 중간, Last 점유) + 자식 TC(CASCADE)와 그 트리거 (H3, NONE) */
CREATE TABLE cl13.T   (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl13.TC  (Id int NOT NULL PRIMARY KEY, Ref int NULL REFERENCES cl13.T (Id) ON DELETE CASCADE, Ref2 int NULL, Src varchar(40) NULL);
CREATE TABLE cl13.Log (Id int NOT NULL PRIMARY KEY, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL);
GO
CREATE TRIGGER cl13.trg_T_1 ON cl13.T AFTER DELETE AS
BEGIN SET NOCOUNT ON; INSERT cl13.Log (Id, Ref, Src) SELECT 1000 + d.Id, d.Id, 'trg_T_1' FROM deleted AS d; END
GO
CREATE TRIGGER cl13.trg_T_2 ON cl13.T AFTER DELETE AS
BEGIN SET NOCOUNT ON; INSERT cl13.Log (Id, Ref, Src) SELECT 2000 + d.Id, d.Id, 'trg_T_2' FROM deleted AS d; END
GO
CREATE TRIGGER cl13.trg_T_3 ON cl13.T AFTER DELETE AS
BEGIN SET NOCOUNT ON; INSERT cl13.Log (Id, Ref, Src) SELECT 3000 + d.Id, d.Id, 'trg_T_3' FROM deleted AS d; END
GO
CREATE TRIGGER cl13.trg_TC_1 ON cl13.TC AFTER DELETE AS
BEGIN SET NOCOUNT ON; INSERT cl13.Log (Id, Ref, Src) SELECT 4000 + d.Id, d.Id, 'trg_TC_1' FROM deleted AS d; END
GO
EXEC sys.sp_settriggerorder @triggername = N'[cl13].[trg_T_1]', @order = N'First', @stmttype = N'DELETE';
EXEC sys.sp_settriggerorder @triggername = N'[cl13].[trg_T_3]', @order = N'Last',  @stmttype = N'DELETE';
GO
/* 감사 트리거 설치(사용자 트리거 뒤에: 사용자 Last가 있는 cl13.T는 NONE이 된다) */
EXEC dbo.spike_InstallAudit N'cl06', N'P',   'D';
EXEC dbo.spike_InstallAudit N'cl06', N'C',   'D';
EXEC dbo.spike_InstallAudit N'cl06', N'G',   'D';
EXEC dbo.spike_InstallAudit N'cl06', N'Log', 'I';
EXEC dbo.spike_InstallAudit N'cl07', N'A',   'I';
EXEC dbo.spike_InstallAudit N'cl07', N'B',   'I';
EXEC dbo.spike_InstallAudit N'cl07', N'C',   'I';
EXEC dbo.spike_InstallAudit N'cl07', N'D',   'I';
EXEC dbo.spike_InstallAudit N'cl09', N'P',   'D';
EXEC dbo.spike_InstallAudit N'cl09', N'Q',   'D';
EXEC dbo.spike_InstallAudit N'cl09', N'M',   'D';
EXEC dbo.spike_InstallAudit N'cl09', N'K',   'D';
EXEC dbo.spike_InstallAudit N'cl13', N'T',   'D';
EXEC dbo.spike_InstallAudit N'cl13', N'TC',  'D';
EXEC dbo.spike_InstallAudit N'cl13', N'Log', 'I';
GO
/* 기본 데이터(실험은 모두 ROLLBACK하므로 유지된다) */
INSERT cl06.P (Id) VALUES (1);
INSERT cl06.C (Id, Ref) VALUES (10, 1), (11, 1);
INSERT cl06.G (Id, Ref) VALUES (100, 10), (101, 11);
INSERT cl09.P (Id) VALUES (1);
INSERT cl09.Q (Id) VALUES (30);
INSERT cl09.M (Id, Ref, Ref2) VALUES (20, 1, 30);
INSERT cl09.K (Id, Ref, Ref2) VALUES (40, 1, NULL), (41, NULL, 30), (42, 1, 30);
INSERT cl13.T (Id) VALUES (1);
INSERT cl13.TC (Id, Ref) VALUES (50, 1), (51, 1);
GO
/* 확인: 감사 순서(cl13.T만 NONE이어야 함), 손 트리거 본문 한 개 */
SELECT * FROM dbo.spike_AuditOrder ORDER BY tableName, evt, triggerName;
SELECT OBJECT_DEFINITION(OBJECT_ID(N'[cl06].[dbr_audit_C_D]')) AS sampleAuditTrigger;
GO
