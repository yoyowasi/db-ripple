-- CL10: 동적 SQL 트리거(후보 확신도). 데모 DM4(SET NULL + 3단 CASCADE + 동적 SQL 트리거)의 원본이다.
--   Region <-CASCADE- Store <-CASCADE- Shelf <-CASCADE- Slot   (3단 CASCADE)
--   Region <-SET NULL- Manager
--   Region AFTER DELETE 트리거 2개(순서 지정 없음):
--     trg_Region_del_dyn : sp_executesql로 Counter를 갱신(동적 SQL -> 모든 테이블에 쓸 수 있는 후보, §7.2 4단계)
--     trg_Region_del_log : RegionLog에 정적 INSERT
USE CascadeLab;
GO
CREATE SCHEMA cl10;
GO
CREATE TABLE cl10.Region (
    id   int         NOT NULL CONSTRAINT PK_cl10_Region PRIMARY KEY,
    name varchar(20) NOT NULL
);
CREATE TABLE cl10.Store (
    id        int NOT NULL CONSTRAINT PK_cl10_Store PRIMARY KEY,
    region_id int NOT NULL CONSTRAINT FK_cl10_Store_Region REFERENCES cl10.Region (id) ON DELETE CASCADE
);
CREATE TABLE cl10.Shelf (
    id       int NOT NULL CONSTRAINT PK_cl10_Shelf PRIMARY KEY,
    store_id int NOT NULL CONSTRAINT FK_cl10_Shelf_Store REFERENCES cl10.Store (id) ON DELETE CASCADE
);
CREATE TABLE cl10.Slot (
    id       int NOT NULL CONSTRAINT PK_cl10_Slot PRIMARY KEY,
    shelf_id int NOT NULL CONSTRAINT FK_cl10_Slot_Shelf REFERENCES cl10.Shelf (id) ON DELETE CASCADE
);
CREATE TABLE cl10.Manager (
    id        int         NOT NULL CONSTRAINT PK_cl10_Manager PRIMARY KEY,
    region_id int         NULL CONSTRAINT FK_cl10_Manager_Region REFERENCES cl10.Region (id) ON DELETE SET NULL,
    name      varchar(20) NOT NULL
);
CREATE TABLE cl10.Counter (
    name varchar(20) NOT NULL CONSTRAINT PK_cl10_Counter PRIMARY KEY,
    n    int         NOT NULL
);
CREATE TABLE cl10.RegionLog (
    region_id int         NOT NULL CONSTRAINT PK_cl10_RegionLog PRIMARY KEY,
    name      varchar(20) NOT NULL
);
GO
CREATE TRIGGER cl10.trg_Region_del_dyn ON cl10.Region AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @n int = (SELECT COUNT(*) FROM deleted);
    EXEC sys.sp_executesql
        N'UPDATE cl10.Counter SET n = n + @n WHERE name = ''region_deleted'';',
        N'@n int',
        @n = @n;
END;
GO
CREATE TRIGGER cl10.trg_Region_del_log ON cl10.Region AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl10.RegionLog (region_id, name)
    SELECT d.id, d.name FROM deleted AS d;
END;
GO
INSERT INTO cl10.Region (id, name) VALUES (1, 'r1'), (2, 'r2');
INSERT INTO cl10.Store (id, region_id) VALUES (10, 1), (11, 1), (20, 2);
INSERT INTO cl10.Shelf (id, store_id) VALUES (100, 10), (101, 10), (110, 11), (200, 20);
INSERT INTO cl10.Slot (id, shelf_id) VALUES (1000, 100), (1001, 101), (2000, 200);
INSERT INTO cl10.Manager (id, region_id, name) VALUES (500, 1, 'm500'), (501, 1, 'm501'), (502, 2, 'm502'), (503, NULL, 'm503');
INSERT INTO cl10.Counter (name, n) VALUES ('region_deleted', 0);
GO
