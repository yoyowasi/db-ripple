-- CL21: 분기 없는 프로시저(EXEC, answer-format v0.3). FK·트리거 없음.
--   EXEC cl21.usp_Outer: Parent 1행 UPDATE, Spare 0행 UPDATE(해당 id 없음), Log INSERT, 안쪽 프로시저 usp_Inner 호출(Touch 1행 UPDATE).
--   모든 쓰기가 EXEC 문장 창 안의 프로시저 직접 쓰기다(causeKind PROC_DIRECT, depth 1). 프로시저는 BEGIN TRAN·COMMIT·분기·동적 SQL이 없다.
--   Log 키는 IDENTITY라 실행마다 값이 다르다(answer-format v0.2 match·identity).
USE CascadeLab;
GO
CREATE SCHEMA cl21;
GO
CREATE TABLE cl21.Parent (
    id  int NOT NULL CONSTRAINT PK_cl21_Parent PRIMARY KEY,
    qty int NOT NULL
);
CREATE TABLE cl21.Spare (
    id  int NOT NULL CONSTRAINT PK_cl21_Spare PRIMARY KEY,
    qty int NOT NULL
);
CREATE TABLE cl21.Touch (
    parent_id int NOT NULL CONSTRAINT PK_cl21_Touch PRIMARY KEY,
    touched   int NOT NULL
);
CREATE TABLE cl21.Log (
    log_id int         IDENTITY(1, 1) NOT NULL CONSTRAINT PK_cl21_Log PRIMARY KEY,
    msg    varchar(30) NOT NULL
);
GO
CREATE PROCEDURE cl21.usp_Inner @id int AS
BEGIN
    SET NOCOUNT ON;
    UPDATE cl21.Touch SET touched = touched + 1 WHERE parent_id = @id;
    RETURN 0;
END;
GO
CREATE PROCEDURE cl21.usp_Outer @id int, @delta int AS
BEGIN
    SET NOCOUNT ON;
    UPDATE cl21.Parent SET qty = qty + @delta WHERE id = @id;
    UPDATE cl21.Spare SET qty = qty + @delta WHERE id = @id;   -- Spare에는 이 id가 없다(시드는 id 100뿐): 0행
    INSERT INTO cl21.Log (msg) VALUES ('outer');
    EXEC cl21.usp_Inner @id = @id;
    RETURN 0;
END;
GO
INSERT INTO cl21.Parent (id, qty) VALUES (1, 10), (2, 20);
INSERT INTO cl21.Spare (id, qty) VALUES (100, 0);
INSERT INTO cl21.Touch (parent_id, touched) VALUES (1, 0), (2, 0);
GO
