-- CL06: CASCADE 대상 테이블(Line)의 사용자 AFTER DELETE 트리거가 LineLog에 쓴다.
-- LineLog의 PK는 IDENTITY가 아니라 line_id라서 삽입 키를 손으로 셀 수 있다.
USE CascadeLab;
GO
CREATE SCHEMA cl06;
GO
CREATE TABLE cl06.Ord (
    id int NOT NULL CONSTRAINT PK_cl06_Ord PRIMARY KEY
);
CREATE TABLE cl06.Line (
    id     int NOT NULL CONSTRAINT PK_cl06_Line PRIMARY KEY,
    ord_id int NOT NULL CONSTRAINT FK_cl06_Line_Ord REFERENCES cl06.Ord (id) ON DELETE CASCADE,
    qty    int NOT NULL
);
CREATE TABLE cl06.LineLog (
    line_id int     NOT NULL CONSTRAINT PK_cl06_LineLog PRIMARY KEY,
    action  char(1) NOT NULL
);
GO
CREATE TRIGGER cl06.trg_Line_del ON cl06.Line AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl06.LineLog (line_id, action)
    SELECT d.id, 'D' FROM deleted AS d;
END;
GO
INSERT INTO cl06.Ord (id) VALUES (1), (2);
INSERT INTO cl06.Line (id, ord_id, qty) VALUES (10, 1, 5), (11, 1, 3), (20, 2, 7);
GO
