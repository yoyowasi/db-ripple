-- CL08: 트리거가 같은 테이블을 갱신. 같은 정의를 두 DB에 만든다.
--   CascadeLab    (RECURSIVE_TRIGGERS OFF) -> CL08a
--   CascadeLab_RT (RECURSIVE_TRIGGERS ON)  -> CL08b
-- trg_Acct_upd는 inserted.touched < 3인 행의 touched를 1 올린다.
-- ON이면 자기 자신을 다시 발동해 touched가 3에서 멈춘다(가드 덕분에 0행 UPDATE를 만들지 않는다).
USE CascadeLab;
GO
CREATE SCHEMA cl08;
GO
CREATE TABLE cl08.Acct (
    id      int NOT NULL CONSTRAINT PK_cl08_Acct PRIMARY KEY,
    bal     int NOT NULL,
    touched int NOT NULL CONSTRAINT DF_cl08_Acct_touched DEFAULT (0)
);
GO
CREATE TRIGGER cl08.trg_Acct_upd ON cl08.Acct AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted WHERE touched < 3) RETURN;
    UPDATE a
    SET touched = a.touched + 1
    FROM cl08.Acct AS a
    INNER JOIN inserted AS i ON a.id = i.id
    WHERE i.touched < 3;
END;
GO
INSERT INTO cl08.Acct (id, bal, touched) VALUES (1, 100, 0), (2, 200, 0);
GO

USE CascadeLab_RT;
GO
CREATE SCHEMA cl08;
GO
CREATE TABLE cl08.Acct (
    id      int NOT NULL CONSTRAINT PK_cl08_Acct PRIMARY KEY,
    bal     int NOT NULL,
    touched int NOT NULL CONSTRAINT DF_cl08_Acct_touched DEFAULT (0)
);
GO
CREATE TRIGGER cl08.trg_Acct_upd ON cl08.Acct AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted WHERE touched < 3) RETURN;
    UPDATE a
    SET touched = a.touched + 1
    FROM cl08.Acct AS a
    INNER JOIN inserted AS i ON a.id = i.id
    WHERE i.touched < 3;
END;
GO
INSERT INTO cl08.Acct (id, bal, touched) VALUES (1, 100, 0), (2, 200, 0);
GO
