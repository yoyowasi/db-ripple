-- CL09: 트리거의 DELETE가 2차 CASCADE를 일으켜 1차 체인과 같은 자식(K)에 닿는다 (H4·H5, 루트 모호성).
-- SP1 CL09(spikes/sql/sp1_10_schema.sql)와 같은 모양에 이름 붙은 제약과 방관자 행만 더했다.
--   P <-CASCADE- M,  P <-CASCADE- K,  Q <-CASCADE- K
--   M.q_ref는 Q.id를 가리키지만 FK가 아니다. trg_M_del이 지워진 M의 q_ref로 Q를 지운다(2차 체인 시작).
USE CascadeLab;
GO
CREATE SCHEMA cl09;
GO
CREATE TABLE cl09.P (
    id int NOT NULL CONSTRAINT PK_cl09_P PRIMARY KEY
);
CREATE TABLE cl09.Q (
    id int NOT NULL CONSTRAINT PK_cl09_Q PRIMARY KEY
);
CREATE TABLE cl09.M (
    id    int NOT NULL CONSTRAINT PK_cl09_M PRIMARY KEY,
    p_id  int NOT NULL CONSTRAINT FK_cl09_M_P REFERENCES cl09.P (id) ON DELETE CASCADE,
    q_ref int NULL
);
CREATE TABLE cl09.K (
    id   int NOT NULL CONSTRAINT PK_cl09_K PRIMARY KEY,
    p_id int NULL CONSTRAINT FK_cl09_K_P REFERENCES cl09.P (id) ON DELETE CASCADE,
    q_id int NULL CONSTRAINT FK_cl09_K_Q REFERENCES cl09.Q (id) ON DELETE CASCADE
);
GO
CREATE TRIGGER cl09.trg_M_del ON cl09.M AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    DELETE q
    FROM cl09.Q AS q
    INNER JOIN deleted AS d ON q.id = d.q_ref;
END;
GO
INSERT INTO cl09.P (id) VALUES (1), (2);
INSERT INTO cl09.Q (id) VALUES (30), (31);
INSERT INTO cl09.M (id, p_id, q_ref) VALUES (20, 1, 30), (21, 2, 31);
INSERT INTO cl09.K (id, p_id, q_id) VALUES (40, 1, NULL), (41, NULL, 30), (42, 1, 30), (50, 2, 31);
GO
