-- CL07: 트리거가 다른 테이블을 쓰는 3단 사슬. FK 없음.
-- A UPDATE -> trg_A_upd: B UPDATE -> trg_B_upd: C INSERT -> trg_C_ins: D UPDATE
-- 서버 옵션 nested triggers = 1(기본값)이 필요하다.
USE CascadeLab;
GO
CREATE SCHEMA cl07;
GO
CREATE TABLE cl07.A (
    id int NOT NULL CONSTRAINT PK_cl07_A PRIMARY KEY,
    v  int NOT NULL
);
CREATE TABLE cl07.B (
    id    int NOT NULL CONSTRAINT PK_cl07_B PRIMARY KEY,
    a_id  int NOT NULL,
    total int NOT NULL
);
CREATE TABLE cl07.C (
    b_id int         NOT NULL CONSTRAINT PK_cl07_C PRIMARY KEY,
    note varchar(20) NOT NULL
);
CREATE TABLE cl07.D (
    id  int NOT NULL CONSTRAINT PK_cl07_D PRIMARY KEY,
    cnt int NOT NULL
);
GO
CREATE TRIGGER cl07.trg_A_upd ON cl07.A AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE b
    SET total = b.total + 1
    FROM cl07.B AS b
    INNER JOIN inserted AS i ON b.a_id = i.id;
END;
GO
CREATE TRIGGER cl07.trg_B_upd ON cl07.B AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl07.C (b_id, note)
    SELECT i.id, 'from B' FROM inserted AS i;
END;
GO
CREATE TRIGGER cl07.trg_C_ins ON cl07.C AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    UPDATE cl07.D
    SET cnt = cnt + (SELECT COUNT(*) FROM inserted)
    WHERE id = 1;
END;
GO
INSERT INTO cl07.A (id, v) VALUES (1, 0), (2, 0);
INSERT INTO cl07.B (id, a_id, total) VALUES (10, 1, 0), (11, 1, 0), (20, 2, 0);
INSERT INTO cl07.D (id, cnt) VALUES (1, 0);
GO
