-- CL03: ON DELETE SET DEFAULT. 트리거 없음.
-- Category/Product: 기본값 0이 부모에 있음 -> 성공 사례(CL03)
-- Grp/Item        : 기본값 99가 부모에 없음 -> 실패 사례(CL03b)
USE CascadeLab;
GO
CREATE SCHEMA cl03;
GO
CREATE TABLE cl03.Category (
    id   int         NOT NULL CONSTRAINT PK_cl03_Category PRIMARY KEY,
    name varchar(20) NOT NULL
);
CREATE TABLE cl03.Product (
    id          int NOT NULL CONSTRAINT PK_cl03_Product PRIMARY KEY,
    category_id int NOT NULL
        CONSTRAINT DF_cl03_Product_category_id DEFAULT (0)
        CONSTRAINT FK_cl03_Product_Category REFERENCES cl03.Category (id) ON DELETE SET DEFAULT
);
CREATE TABLE cl03.Grp (
    id int NOT NULL CONSTRAINT PK_cl03_Grp PRIMARY KEY
);
CREATE TABLE cl03.Item (
    id     int NOT NULL CONSTRAINT PK_cl03_Item PRIMARY KEY,
    grp_id int NOT NULL
        CONSTRAINT DF_cl03_Item_grp_id DEFAULT (99)
        CONSTRAINT FK_cl03_Item_Grp REFERENCES cl03.Grp (id) ON DELETE SET DEFAULT
);
GO
INSERT INTO cl03.Category (id, name) VALUES (0, 'none'), (1, 'c1'), (2, 'c2');
INSERT INTO cl03.Product (id, category_id) VALUES (10, 1), (11, 1), (20, 2);
INSERT INTO cl03.Grp (id) VALUES (1), (2);
INSERT INTO cl03.Item (id, grp_id) VALUES (10, 1), (20, 2);
GO
