-- CL20: Big. 부모 1 : 자식 2 ON DELETE CASCADE + 자식 AFTER 트리거, 행 수 생성기 (M5, DM6, D8).
--   Parent(id, total) <-CASCADE- Child(id, parent_id, qty);  ChildLog(child_id, parent_id, qty)
--   부모 p의 자식 id는 2p-1, 2p. qty = 1, Parent.total = 2(자식 qty 합).
--   trg_Child_del: 삭제된 Child마다 ChildLog 1행(FK 연쇄로 지워질 때도 발동, A1)
--   trg_Child_upd: Child qty 증가분을 부모 total에 더한다(Child id는 바꾸지 않는다고 본다)
-- 대량 데이터는 cl20.usp_Generate로만 만든다(week3-plan Q39, AD11). 시드는 @parents = 3.
-- usp_Generate는 원본 역할 DB에서 기준선(백업)을 만들기 전에 부른다. 감사 트리거가 깔린 샌드박스에서 부르면 Parent DELETE가 수집된다.
USE CascadeLab;
GO
CREATE SCHEMA cl20;
GO
CREATE TABLE cl20.Parent (
    id    int NOT NULL CONSTRAINT PK_cl20_Parent PRIMARY KEY,
    total int NOT NULL
);
CREATE TABLE cl20.Child (
    id        int NOT NULL CONSTRAINT PK_cl20_Child PRIMARY KEY,
    parent_id int NOT NULL CONSTRAINT FK_cl20_Child_Parent REFERENCES cl20.Parent (id) ON DELETE CASCADE,
    qty       int NOT NULL
);
-- FK 연쇄 삭제가 Child를 부모 키로 찾게 한다(대량 M5). 쓰기 비용: Child 행마다 인덱스 행 1개(int 2개).
CREATE INDEX IX_cl20_Child_parent_id ON cl20.Child (parent_id);
CREATE TABLE cl20.ChildLog (
    child_id  int NOT NULL CONSTRAINT PK_cl20_ChildLog PRIMARY KEY,
    parent_id int NOT NULL,
    qty       int NOT NULL
);
GO
CREATE TRIGGER cl20.trg_Child_del ON cl20.Child AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl20.ChildLog (child_id, parent_id, qty)
    SELECT d.id, d.parent_id, d.qty FROM deleted AS d;
END;
GO
CREATE TRIGGER cl20.trg_Child_upd ON cl20.Child AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE p
    SET total = p.total + x.delta
    FROM cl20.Parent AS p
    INNER JOIN (
        SELECT i.parent_id, SUM(i.qty - d.qty) AS delta
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.id = i.id
        GROUP BY i.parent_id
    ) AS x ON x.parent_id = p.id;
END;
GO
-- 멱등 재생성: 기존 행을 모두 지우고 Parent 1..@parents, Child 1..2*@parents를 만든다.
-- TRUNCATE는 트리거를 발동하지 않는다(A21). Child는 다른 FK가 참조하지 않아 TRUNCATE할 수 있고, Parent는 참조되므로 DELETE한다(Child가 비어 연쇄 행 0).
CREATE PROCEDURE cl20.usp_Generate
    @parents int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @parents IS NULL OR @parents < 1 OR @parents > 1000000
        THROW 50000, N'cl20.usp_Generate: @parents must be between 1 and 1000000.', 1;

    BEGIN TRANSACTION;
    TRUNCATE TABLE cl20.ChildLog;
    TRUNCATE TABLE cl20.Child;
    DELETE FROM cl20.Parent;

    WITH d AS (
        SELECT v.n FROM (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) AS v (n)
    ),
    nums AS (
        SELECT d6.n * 100000 + d5.n * 10000 + d4.n * 1000 + d3.n * 100 + d2.n * 10 + d1.n + 1 AS n
        FROM d AS d1
        CROSS JOIN d AS d2
        CROSS JOIN d AS d3
        CROSS JOIN d AS d4
        CROSS JOIN d AS d5
        CROSS JOIN d AS d6
    )
    INSERT INTO cl20.Parent (id, total)
    SELECT nums.n, 2 FROM nums WHERE nums.n <= @parents;

    INSERT INTO cl20.Child (id, parent_id, qty)
    SELECT 2 * p.id - 1, p.id, 1 FROM cl20.Parent AS p
    UNION ALL
    SELECT 2 * p.id, p.id, 1 FROM cl20.Parent AS p;
    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT_BIG(*) FROM cl20.Parent) AS parents,
        (SELECT COUNT_BIG(*) FROM cl20.Child) AS children,
        (SELECT COUNT_BIG(*) FROM cl20.ChildLog) AS childLogs;
END;
GO
EXEC cl20.usp_Generate @parents = 3;
GO
