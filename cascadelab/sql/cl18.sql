-- CL18: NO ACTION 위반 오류 (failed, 변경 0). 트리거 없음.
--   Cust <-NO ACTION- Inv,  Cust <-CASCADE- Note
--   Cust 1 DELETE: CASCADE(Note)를 먼저 적용하고 NO ACTION(Inv)을 나중에 검사해 547로 실패한다(A2, DELETE 규칙).
--   AFTER 트리거(감사 포함)는 모든 연쇄와 제약 검사가 성공해야 발동하므로 묶음이 남지 않는다(A1).
USE CascadeLab;
GO
CREATE SCHEMA cl18;
GO
CREATE TABLE cl18.Cust (
    id   int         NOT NULL CONSTRAINT PK_cl18_Cust PRIMARY KEY,
    name varchar(20) NOT NULL
);
CREATE TABLE cl18.Inv (
    id      int NOT NULL CONSTRAINT PK_cl18_Inv PRIMARY KEY,
    cust_id int NOT NULL CONSTRAINT FK_cl18_Inv_Cust REFERENCES cl18.Cust (id)
);
CREATE TABLE cl18.Note (
    id      int NOT NULL CONSTRAINT PK_cl18_Note PRIMARY KEY,
    cust_id int NOT NULL CONSTRAINT FK_cl18_Note_Cust REFERENCES cl18.Cust (id) ON DELETE CASCADE
);
GO
INSERT INTO cl18.Cust (id, name) VALUES (1, 'c1'), (2, 'c2'), (3, 'c3');
INSERT INTO cl18.Inv (id, cust_id) VALUES (10, 1);
INSERT INTO cl18.Note (id, cust_id) VALUES (100, 1), (200, 2);
GO
