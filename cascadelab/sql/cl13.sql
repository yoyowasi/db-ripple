-- CL13: 같은 테이블에 AFTER DELETE 트리거 3개(First, 순서 미지정, 사용자 Last) + CASCADE 자식과 그 트리거 (ambiguous, 감사 순서 NONE).
-- SP1 CL13(spikes/sql/sp1_10_schema.sql)과 같은 모양이다. T DELETE에는 사용자 Last가 있어 감사 트리거를 Last로 지정하지 못한다(A6, E15).
-- Log 키는 트리거마다 다른 1000 단위 오프셋이라 어느 트리거가 쓴 행인지 값으로 알 수 있다(정답 확인용, 제품 귀속에는 쓰이지 않는다).
-- sp_settriggerorder는 이 스키마의 트리거 속성만 바꾼다(서버 설정이 아니다).
USE CascadeLab;
GO
CREATE SCHEMA cl13;
GO
CREATE TABLE cl13.T (
    id   int         NOT NULL CONSTRAINT PK_cl13_T PRIMARY KEY,
    name varchar(20) NOT NULL
);
CREATE TABLE cl13.TC (
    id   int NOT NULL CONSTRAINT PK_cl13_TC PRIMARY KEY,
    t_id int NOT NULL CONSTRAINT FK_cl13_TC_T REFERENCES cl13.T (id) ON DELETE CASCADE
);
CREATE TABLE cl13.Log (
    id  int         NOT NULL CONSTRAINT PK_cl13_Log PRIMARY KEY,
    ref int         NOT NULL,
    src varchar(20) NOT NULL
);
GO
CREATE TRIGGER cl13.trg_T_1 ON cl13.T AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl13.Log (id, ref, src) SELECT 1000 + d.id, d.id, 'trg_T_1' FROM deleted AS d;
END;
GO
CREATE TRIGGER cl13.trg_T_2 ON cl13.T AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl13.Log (id, ref, src) SELECT 2000 + d.id, d.id, 'trg_T_2' FROM deleted AS d;
END;
GO
CREATE TRIGGER cl13.trg_T_3 ON cl13.T AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl13.Log (id, ref, src) SELECT 3000 + d.id, d.id, 'trg_T_3' FROM deleted AS d;
END;
GO
CREATE TRIGGER cl13.trg_TC_1 ON cl13.TC AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl13.Log (id, ref, src) SELECT 4000 + d.id, d.id, 'trg_TC_1' FROM deleted AS d;
END;
GO
EXEC sys.sp_settriggerorder @triggername = N'cl13.trg_T_1', @order = N'First', @stmttype = N'DELETE';
EXEC sys.sp_settriggerorder @triggername = N'cl13.trg_T_3', @order = N'Last', @stmttype = N'DELETE';
GO
INSERT INTO cl13.T (id, name) VALUES (1, 't1'), (2, 't2');
INSERT INTO cl13.TC (id, t_id) VALUES (50, 1), (51, 1), (60, 2);
GO
