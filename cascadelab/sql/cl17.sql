-- CL17: 다중 문장, 뒤 문장이 앞 문장 결과에 의존 (문장 경계, E9·E24). 트리거 없음.
--   1) Txn 900 INSERT  2) 그 Txn을 조인해 Acct 잔액 UPDATE  3) Txn 900 DELETE
--   순 변경은 Acct 1행뿐이고 Txn은 삽입 뒤 삭제라 순 변경이 없다(발동 보기에는 세 묶음이 모두 보인다).
USE CascadeLab;
GO
CREATE SCHEMA cl17;
GO
CREATE TABLE cl17.Acct (
    id  int NOT NULL CONSTRAINT PK_cl17_Acct PRIMARY KEY,
    bal int NOT NULL
);
CREATE TABLE cl17.Txn (
    id      int NOT NULL CONSTRAINT PK_cl17_Txn PRIMARY KEY,
    acct_id int NOT NULL CONSTRAINT FK_cl17_Txn_Acct REFERENCES cl17.Acct (id),
    amt     int NOT NULL
);
GO
INSERT INTO cl17.Acct (id, bal) VALUES (1, 100), (2, 200);
INSERT INTO cl17.Txn (id, acct_id, amt) VALUES (1, 1, 100), (2, 2, 200);
GO
