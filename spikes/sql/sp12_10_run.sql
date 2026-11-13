/* SP12 — 깊이 신호: 2단 중첩 프로시저 안 DML·FK 연쇄·트리거에서 TRIGGER_NESTLEVEL()과 @@NESTLEVEL이 어떻게 나오는가
   (감사 트리거의 nestLevel = 인자 없는 TRIGGER_NESTLEVEL(), A7. 프로시저 중첩이 깊이에 더해지는지 본다.)
   실행: .\scripts\run-sql.ps1 spikes\sql\sp12_10_run.sql -Part 1 -Log spikes\logs\sp12-part1.log
         .\scripts\run-sql.ps1 spikes\sql\sp12_10_run.sql -Part 2 -Database SpikeLab_SP12 -Log spikes\logs\sp12-part2.log
   정리: .\scripts\run-sql.ps1 spikes\sql\sp_exec_99_drop.sql -Log spikes\logs\sp-exec-99-drop.log
   시나리오 S1~S6은 각자 배치(최상위 @@NESTLEVEL 0)에서 BEGIN TRAN → 문장 → 신호 읽기 → ROLLBACK. 신호는 사용자 트리거·프로시저가 dbo.SigLog에 남긴다(감사 트리거 대신 손으로 만든 신호 수집기).
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE master;
GO
IF DB_ID(N'SpikeLab_SP12') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP12 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP12;
END;
GO
CREATE DATABASE SpikeLab_SP12;
GO
-- PART 2
USE SpikeLab_SP12;
GO
CREATE TABLE dbo.P (Id int NOT NULL CONSTRAINT PK_P PRIMARY KEY, V int NOT NULL);
CREATE TABLE dbo.C (Id int NOT NULL CONSTRAINT PK_C PRIMARY KEY, PId int NOT NULL CONSTRAINT FK_C_P REFERENCES dbo.P (Id) ON DELETE CASCADE, V int NOT NULL);
CREATE TABLE dbo.M (Id int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_M PRIMARY KEY, Note varchar(20) NOT NULL);
CREATE TABLE dbo.SigLog (
    seq       int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_SigLog PRIMARY KEY,
    who       sysname NOT NULL,        -- OBJECT_NAME(@@PROCID): 트리거 또는 프로시저 이름
    nestLevel int     NOT NULL,        -- @@NESTLEVEL
    trgNest   int     NOT NULL,        -- TRIGGER_NESTLEVEL() (인자 없음 = 모든 트리거)
    tc        int     NOT NULL         -- @@TRANCOUNT
);
CREATE TABLE dbo.SigResult (
    scenario  varchar(8) NOT NULL,
    seq       int        NOT NULL,
    who       sysname    NOT NULL,
    nestLevel int        NOT NULL,
    trgNest   int        NOT NULL,
    tc        int        NOT NULL,
    CONSTRAINT PK_SigResult PRIMARY KEY (scenario, seq)
);
GO
CREATE TRIGGER dbo.trg_P_upd ON dbo.P AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
    UPDATE c SET V = c.V + 1 FROM dbo.C AS c INNER JOIN inserted AS i ON i.Id = c.PId;
END;
GO
CREATE TRIGGER dbo.trg_C_upd ON dbo.C AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
    INSERT dbo.M (Note) VALUES ('from C');
END;
GO
CREATE TRIGGER dbo.trg_M_ins ON dbo.M AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
END;
GO
CREATE TRIGGER dbo.trg_C_del ON dbo.C AFTER DELETE AS   -- FK 연쇄(P 삭제)로 발동한다
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
END;
GO
CREATE PROCEDURE dbo.usp_Inner AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
    UPDATE dbo.P SET V = V + 1 WHERE Id = 1;
END;
GO
CREATE PROCEDURE dbo.usp_Outer AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
    EXEC dbo.usp_Inner;
END;
GO
CREATE PROCEDURE dbo.usp_Outer2 AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
    EXEC dbo.usp_Outer;
END;
GO
CREATE PROCEDURE dbo.usp_Del AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.SigLog (who, nestLevel, trgNest, tc) VALUES (OBJECT_NAME(@@PROCID), @@NESTLEVEL, TRIGGER_NESTLEVEL(), @@TRANCOUNT);
    DELETE dbo.P WHERE Id = 2;
END;
GO
INSERT dbo.P (Id, V) VALUES (1, 0), (2, 0);
INSERT dbo.C (Id, PId, V) VALUES (10, 1, 0), (20, 2, 0);
DELETE dbo.SigLog;
GO
/* S1: 프로시저 없이 직접 UPDATE(기준). 기대 트리거 깊이 1·2·3 */
SET NOCOUNT ON;
DECLARE @r TABLE (seq int, who sysname, nestLevel int, trgNest int, tc int);
BEGIN TRANSACTION;
UPDATE dbo.P SET V = V + 1 WHERE Id = 1;
INSERT @r SELECT seq, who, nestLevel, trgNest, tc FROM dbo.SigLog ORDER BY seq;
ROLLBACK TRANSACTION;
INSERT dbo.SigResult SELECT 'S1', seq, who, nestLevel, trgNest, tc FROM @r;
GO
/* S2: EXEC usp_Inner(프로시저 1단) → 같은 UPDATE */
SET NOCOUNT ON;
DECLARE @r TABLE (seq int, who sysname, nestLevel int, trgNest int, tc int);
BEGIN TRANSACTION;
EXEC dbo.usp_Inner;
INSERT @r SELECT seq, who, nestLevel, trgNest, tc FROM dbo.SigLog ORDER BY seq;
ROLLBACK TRANSACTION;
INSERT dbo.SigResult SELECT 'S2', seq, who, nestLevel, trgNest, tc FROM @r;
GO
/* S3: EXEC usp_Outer(프로시저 2단 중첩) */
SET NOCOUNT ON;
DECLARE @r TABLE (seq int, who sysname, nestLevel int, trgNest int, tc int);
BEGIN TRANSACTION;
EXEC dbo.usp_Outer;
INSERT @r SELECT seq, who, nestLevel, trgNest, tc FROM dbo.SigLog ORDER BY seq;
ROLLBACK TRANSACTION;
INSERT dbo.SigResult SELECT 'S3', seq, who, nestLevel, trgNest, tc FROM @r;
GO
/* S4: EXEC usp_Outer2(프로시저 3단 중첩, 깊이 상한 4 안) */
SET NOCOUNT ON;
DECLARE @r TABLE (seq int, who sysname, nestLevel int, trgNest int, tc int);
BEGIN TRANSACTION;
EXEC dbo.usp_Outer2;
INSERT @r SELECT seq, who, nestLevel, trgNest, tc FROM dbo.SigLog ORDER BY seq;
ROLLBACK TRANSACTION;
INSERT dbo.SigResult SELECT 'S4', seq, who, nestLevel, trgNest, tc FROM @r;
GO
/* S5: EXEC usp_Del(프로시저 1단) → DELETE P → FK 연쇄로 C 삭제 → trg_C_del */
SET NOCOUNT ON;
DECLARE @r TABLE (seq int, who sysname, nestLevel int, trgNest int, tc int);
BEGIN TRANSACTION;
EXEC dbo.usp_Del;
INSERT @r SELECT seq, who, nestLevel, trgNest, tc FROM dbo.SigLog ORDER BY seq;
ROLLBACK TRANSACTION;
INSERT dbo.SigResult SELECT 'S5', seq, who, nestLevel, trgNest, tc FROM @r;
GO
/* S6: 프로시저 없이 직접 DELETE P(FK 연쇄 기준) */
SET NOCOUNT ON;
DECLARE @r TABLE (seq int, who sysname, nestLevel int, trgNest int, tc int);
BEGIN TRANSACTION;
DELETE dbo.P WHERE Id = 2;
INSERT @r SELECT seq, who, nestLevel, trgNest, tc FROM dbo.SigLog ORDER BY seq;
ROLLBACK TRANSACTION;
INSERT dbo.SigResult SELECT 'S6', seq, who, nestLevel, trgNest, tc FROM @r;
GO
/* 결과 표 */
SELECT scenario, seq, who, nestLevel, trgNest, nestLevel - trgNest AS procDepth, tc FROM dbo.SigResult ORDER BY scenario, seq;
GO
/* 판정. 각 신호 행의 procDepth(= @@NESTLEVEL - TRIGGER_NESTLEVEL())가 그 시나리오의 프로시저 중첩 수와 같고, 트리거 깊이(trgNest)는 프로시저 중첩에 영향받지 않아야 한다.
   FAIL = 추정과 다름 → 실측 값을 docs/spikes/SP12.md에 적는다. */
WITH expected AS (
    SELECT * FROM (VALUES
        ('S1', 0), ('S2', 1), ('S3', 2), ('S4', 3), ('S5', 1), ('S6', 0)
    ) AS v (scenario, procDepth)
), trg AS (
    SELECT r.scenario, r.who, r.trgNest, r.nestLevel - r.trgNest AS procDepth, e.procDepth AS expectedProcDepth
    FROM dbo.SigResult AS r INNER JOIN expected AS e ON e.scenario = r.scenario
    WHERE r.who LIKE N'trg[_]%'
)
SELECT 'H1_trigger_depth_unchanged_by_procedure_nesting' AS hypothesis,
       CASE WHEN NOT EXISTS (
                SELECT 1 FROM trg AS a INNER JOIN trg AS b ON a.who = b.who AND a.scenario = 'S1' AND b.scenario IN ('S2', 'S3', 'S4') AND a.trgNest <> b.trgNest)
            AND EXISTS (SELECT 1 FROM trg WHERE scenario = 'S4') THEN 'PASS' ELSE 'FAIL' END AS verdict,
       (SELECT STRING_AGG(CONCAT(scenario, ':', who, '=', trgNest), ' ; ') WITHIN GROUP (ORDER BY scenario, who) FROM trg WHERE who IN (N'trg_P_upd', N'trg_M_ins')) AS detail
UNION ALL
SELECT 'H2_nestlevel_minus_trigger_nestlevel_equals_procedure_depth',
       CASE WHEN NOT EXISTS (SELECT 1 FROM trg WHERE procDepth <> expectedProcDepth) AND EXISTS (SELECT 1 FROM trg) THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(CONCAT(scenario, ':', who, '=', procDepth, '/', expectedProcDepth), ' ; ') WITHIN GROUP (ORDER BY scenario, who) FROM trg WHERE procDepth <> expectedProcDepth)
UNION ALL
SELECT 'H3_fk_cascade_trigger_depth_equals_parent_level_with_and_without_procedure',
       CASE WHEN EXISTS (SELECT 1 FROM trg WHERE scenario = 'S6' AND who = N'trg_C_del')
             AND (SELECT MIN(trgNest) FROM trg WHERE scenario = 'S5' AND who = N'trg_C_del') = (SELECT MIN(trgNest) FROM trg WHERE scenario = 'S6' AND who = N'trg_C_del')
            THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(CONCAT(scenario, ':', trgNest), ' ; ') WITHIN GROUP (ORDER BY scenario) FROM trg WHERE who = N'trg_C_del')
UNION ALL
SELECT 'H4_procedure_does_not_raise_trigger_nestlevel',
       CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.SigResult WHERE who LIKE N'usp[_]%' AND trgNest <> 0) THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(CONCAT(scenario, ':', who, '=', trgNest), ' ; ') WITHIN GROUP (ORDER BY scenario, seq) FROM dbo.SigResult WHERE who LIKE N'usp[_]%' AND trgNest <> 0);
GO
