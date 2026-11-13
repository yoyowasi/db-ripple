-- CL22: 프로시저가 CASCADE FK 자식(ChildA)을 먼저 DELETE한 뒤 부모를 DELETE한다(answer-format v0.3).
--   EXEC cl22.usp_DeleteParent @id = 1:
--     ChildA(10, 11)는 프로시저가 직접 지운다 → 부모 DELETE 때 FK가 지울 행이 남아 있지 않다(CASCADE 0행). 원인은 FK가 아니라 PROC_DIRECT다.
--     ChildB(100)는 프로시저가 건드리지 않아 부모 DELETE의 FK CASCADE로 지워진다(진짜 FK 연쇄, certain).
--   확인 대상: FK certain 오판 방지. 제품이 ChildA를 "부모가 지워졌고 FK CASCADE가 있다"는 이유로 FK_CASCADE certain으로 귀속하면 과신이다.
--   트리거 없음, 프로시저에 분기·트랜잭션·동적 SQL 없음.
USE CascadeLab;
GO
CREATE SCHEMA cl22;
GO
CREATE TABLE cl22.Parent (
    id int NOT NULL CONSTRAINT PK_cl22_Parent PRIMARY KEY
);
CREATE TABLE cl22.ChildA (
    id        int NOT NULL CONSTRAINT PK_cl22_ChildA PRIMARY KEY,
    parent_id int NOT NULL CONSTRAINT FK_cl22_ChildA_Parent REFERENCES cl22.Parent (id) ON DELETE CASCADE
);
CREATE TABLE cl22.ChildB (
    id        int NOT NULL CONSTRAINT PK_cl22_ChildB PRIMARY KEY,
    parent_id int NOT NULL CONSTRAINT FK_cl22_ChildB_Parent REFERENCES cl22.Parent (id) ON DELETE CASCADE
);
GO
CREATE PROCEDURE cl22.usp_DeleteParent @id int AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM cl22.ChildA WHERE parent_id = @id;
    DELETE FROM cl22.Parent WHERE id = @id;
    RETURN 0;
END;
GO
INSERT INTO cl22.Parent (id) VALUES (1), (2);
INSERT INTO cl22.ChildA (id, parent_id) VALUES (10, 1), (11, 1), (20, 2);
INSERT INTO cl22.ChildB (id, parent_id) VALUES (100, 1), (200, 2);
GO
