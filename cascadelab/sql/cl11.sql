-- CL11: INSTEAD OF INSERT / INSTEAD OF UPDATE (REPLACES, INSTEAD_OF 귀속).
-- Item에는 연쇄 FK가 없다(A5: 연쇄 FK가 있는 테이블에는 그 이벤트의 INSTEAD OF를 둘 수 없다).
-- INSTEAD OF 트리거 안에서 같은 테이블에 같은 연산을 하면 INSTEAD OF가 다시 불리지 않고 제약·AFTER 트리거 처리로 간다(CREATE TRIGGER 문서).
--   trg_Item_ioi: 이름을 대문자로 바꿔 넣고 ItemLog에 (id, 'I')
--   trg_Item_iou: qty >= 0인 행만 반영하고, 반영한 행은 (id, 'U'), 거른 행은 (id, 'R')을 ItemLog에
USE CascadeLab;
GO
CREATE SCHEMA cl11;
GO
CREATE TABLE cl11.Item (
    id   int         NOT NULL CONSTRAINT PK_cl11_Item PRIMARY KEY,
    name varchar(20) NOT NULL,
    qty  int         NOT NULL
);
CREATE TABLE cl11.ItemLog (
    item_id int     NOT NULL,
    action  char(1) NOT NULL,
    CONSTRAINT PK_cl11_ItemLog PRIMARY KEY (item_id, action)
);
GO
CREATE TRIGGER cl11.trg_Item_ioi ON cl11.Item INSTEAD OF INSERT AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl11.Item (id, name, qty)
    SELECT i.id, UPPER(i.name), i.qty FROM inserted AS i;
    INSERT INTO cl11.ItemLog (item_id, action)
    SELECT i.id, 'I' FROM inserted AS i;
END;
GO
CREATE TRIGGER cl11.trg_Item_iou ON cl11.Item INSTEAD OF UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE it
    SET name = i.name, qty = i.qty
    FROM cl11.Item AS it
    INNER JOIN inserted AS i ON it.id = i.id
    WHERE i.qty >= 0;
    INSERT INTO cl11.ItemLog (item_id, action)
    SELECT i.id, CASE WHEN i.qty >= 0 THEN 'U' ELSE 'R' END FROM inserted AS i;
END;
GO
-- 시드는 INSTEAD OF INSERT를 거친다(이름이 대문자로 저장되고 ItemLog에 (1,'I'), (2,'I')가 남는다).
INSERT INTO cl11.Item (id, name, qty) VALUES (1, 'a', 5), (2, 'b', 2);
GO
