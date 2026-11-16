/* SP19 — 원본 모사 컨테이너(2022 또는 2025, GENERATE_SERIES 때문에 2022 이상)에 합성 DB를 만든다(ERP 비슷한 주문·주문행·재고 원장 + 작은 코드 테이블 30개).
   실행(저장소 루트, sa): 보통은 sp19_00_run.ps1이 크기별 값을 넣어 부른다.
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_10_gen_db.sql -Vars @{ DbName = 'SP19_0G3'; Scale = '0.35'; LogMB = '32'; DataMB = '436' } -Log spikes/logs/sp19-cloud-10-gen-0g3.log
   변수: DbName(SP19_로 시작해야 함), Scale(배율 S. 2026-11-16 실측 S 0.35 → 사용 229MB, S 3.0 → 1,877MB, S 7.6 → 4,736MB, 즉 S 1.0 ≈ 사용 약 0.63GB),
         LogMB(마지막 로그 파일 목표 크기, 줄일 수 없으면 그대로), DataMB(데이터 파일 처음 크기 MB, 0이면 S × 680 + 64).
   크기 표(sp19_00_run.ps1과 같음): 0g3 S 0.35·DataMB 436·LogMB 32 / 1g S 1.6·DataMB 0(=1152)·LogMB 128 / 2g S 3.0·DataMB 2104·LogMB 200 / 5g S 7.6·DataMB 5232·LogMB 500.
   2026-11-16 로컬 실행 때는 DataMB 변수가 없었다(0G3은 S × 880 + 128 = 436MB, 2G·5G는 S × 680 + 64). 위 DataMB 값은 그때와 같은 파일 크기를 만든다.
   행 수(배율 S): Customer 20,000S, Item 5,000S, SalesOrder 400,000S, SalesOrderLine 2,000,000S(주문당 5행), StockLedger 1,500,000S, Code01~30 각 100행.
   값은 순번 해시·NEWID 조각을 섞어 백업 압축률이 너무 높지 않게 한다(압축률은 결과에 적는다).
   단순 복구 모델로 TABLOCK 적재(최소 로깅) → 비클러스터형 인덱스·FK(WITH CHECK) → CHECKPOINT → 로그를 LogMB로 맞춤 → 전체 복구 모델(원본 개발 서버 모사). */
:on error exit
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
IF N'$(DbName)' NOT LIKE N'SP19[_]%' THROW 50001, N'SP19 전용 이름(SP19_...)만 만든다', 1;
IF DB_ID(N'$(DbName)') IS NOT NULL THROW 50002, N'이미 있음: 먼저 지우거나 다른 이름을 쓴다', 1;
DECLARE @dataMB int = $(DataMB);
IF @dataMB <= 0 SET @dataMB = CAST(CEILING($(Scale) * 680.0) AS int) + 64;
DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(N'$(DbName)')
    + N' ON (NAME = N''$(DbName)'', FILENAME = N''/var/opt/mssql/data/$(DbName).mdf'', SIZE = ' + CAST(@dataMB AS nvarchar(20)) + N'MB, FILEGROWTH = 256MB)'
    + N' LOG ON (NAME = N''$(DbName)_log'', FILENAME = N''/var/opt/mssql/data/$(DbName)_log.ldf'', SIZE = 512MB, FILEGROWTH = 256MB);';
EXEC (@sql);
ALTER DATABASE [$(DbName)] SET RECOVERY SIMPLE;
SELECT 'created' AS what, @dataMB AS data_mb_presized, SYSDATETIME() AS at;
GO
USE [$(DbName)];
GO
CREATE TABLE dbo.Customer (
    CustomerId int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    Code varchar(20) NOT NULL,
    Name nvarchar(100) NOT NULL,
    Region char(2) NOT NULL,
    CreditLimit decimal(18, 2) NOT NULL,
    CreatedAt datetime2(3) NOT NULL,
    Note nvarchar(200) NULL);
CREATE TABLE dbo.Item (
    ItemId int NOT NULL CONSTRAINT PK_Item PRIMARY KEY,
    Code varchar(30) NOT NULL,
    Name nvarchar(100) NOT NULL,
    Category smallint NOT NULL,
    UnitPrice decimal(18, 4) NOT NULL,
    Spec nvarchar(400) NULL);
CREATE TABLE dbo.SalesOrder (
    OrderId int NOT NULL CONSTRAINT PK_SalesOrder PRIMARY KEY,
    CustomerId int NOT NULL,
    OrderDate date NOT NULL,
    Status tinyint NOT NULL,
    Total decimal(18, 2) NOT NULL,
    Memo nvarchar(200) NULL,
    CreatedAt datetime2(3) NOT NULL,
    CreatedBy varchar(30) NOT NULL);
CREATE TABLE dbo.SalesOrderLine (
    OrderId int NOT NULL,
    LineNum smallint NOT NULL,
    ItemId int NOT NULL,
    Qty decimal(18, 3) NOT NULL,
    UnitPrice decimal(18, 4) NOT NULL,
    Amount decimal(18, 2) NOT NULL,
    Remark nvarchar(100) NULL,
    CONSTRAINT PK_SalesOrderLine PRIMARY KEY (OrderId, LineNum));
CREATE TABLE dbo.StockLedger (
    LedgerId bigint NOT NULL CONSTRAINT PK_StockLedger PRIMARY KEY,
    ItemId int NOT NULL,
    TxnDate datetime2(3) NOT NULL,
    TxnType char(2) NOT NULL,
    Qty decimal(18, 3) NOT NULL,
    RefNo varchar(30) NOT NULL,
    Payload nvarchar(300) NULL);
GO
-- 작은 코드 테이블 30개(테이블 수에 비례하는 감사 설치·해시 비용을 ERP 비슷하게)
DECLARE @i int = 1, @t sysname, @sql nvarchar(max);
WHILE @i <= 30
BEGIN
    SET @t = N'Code' + RIGHT(N'0' + CAST(@i AS nvarchar(2)), 2);
    SET @sql = N'CREATE TABLE dbo.' + QUOTENAME(@t) + N' (Id int NOT NULL CONSTRAINT ' + QUOTENAME(N'PK_' + @t) + N' PRIMARY KEY, Name nvarchar(50) NOT NULL, SortNo int NOT NULL, UseYn char(1) NOT NULL);'
        + N' INSERT dbo.' + QUOTENAME(@t) + N' (Id, Name, SortNo, UseYn) SELECT g.value, CONCAT(N''코드 '', g.value), g.value * 10, CASE WHEN g.value % 7 = 0 THEN ''N'' ELSE ''Y'' END FROM GENERATE_SERIES(1, 100) AS g;';
    EXEC (@sql);
    SET @i += 1;
END;
GO
DECLARE @n int = CAST(20000 * $(Scale) AS int);
INSERT dbo.Customer WITH (TABLOCK) (CustomerId, Code, Name, Region, CreditLimit, CreatedAt, Note)
SELECT g.value, CONCAT('C', RIGHT(CONCAT('0000000', g.value), 7)), CONCAT(N'거래처 ', g.value, N' ', LEFT(CONVERT(nvarchar(36), NEWID()), 8)),
       CHOOSE(1 + g.value % 8, 'SE', 'BS', 'IC', 'DG', 'GJ', 'DJ', 'US', 'GG'), (g.value % 500) * 100000.00,
       DATEADD(minute, -(g.value % 5000000), CAST('2026-11-01' AS datetime2(3))),
       CASE WHEN g.value % 4 = 0 THEN NULL ELSE CONCAT(N'비고 ', g.value % 977) END
FROM GENERATE_SERIES(1, @n) AS g;
SET @n = CAST(5000 * $(Scale) AS int);
INSERT dbo.Item WITH (TABLOCK) (ItemId, Code, Name, Category, UnitPrice, Spec)
SELECT g.value, CONCAT('ITM-', RIGHT(CONCAT('000000', g.value), 6)), CONCAT(N'품목 ', g.value), g.value % 40, (g.value % 9973) * 1.2345,
       CONCAT(N'규격 ', g.value % 113, N'x', g.value % 71, N' ', CONVERT(nvarchar(36), NEWID()))
FROM GENERATE_SERIES(1, @n) AS g;
SELECT 'customer_item_done' AS what, SYSDATETIME() AS at;
GO
DECLARE @n bigint = CAST(400000 * $(Scale) AS bigint), @cust bigint = CAST(20000 * $(Scale) AS bigint), @from bigint = 1, @chunk bigint = 500000;
WHILE @from <= @n
BEGIN
    INSERT dbo.SalesOrder WITH (TABLOCK) (OrderId, CustomerId, OrderDate, Status, Total, Memo, CreatedAt, CreatedBy)
    SELECT CAST(g.value AS int), CAST(1 + ABS(CHECKSUM(g.value * 31)) % @cust AS int), DATEADD(day, -CAST(g.value % 3650 AS int), CAST('2026-11-01' AS date)),
           CAST(g.value % 5 AS tinyint), (ABS(CHECKSUM(g.value * 17)) % 100000000) / 100.0,
           CASE WHEN g.value % 5 = 0 THEN NULL ELSE CONCAT(N'주문 메모 ', g.value % 997, N' ', LEFT(CONVERT(nvarchar(36), NEWID()), 8 + CAST(g.value % 28 AS int))) END,
           DATEADD(second, -CAST(g.value % 300000000 AS int), CAST('2026-11-01' AS datetime2(3))), CONCAT('user', g.value % 250)
    FROM GENERATE_SERIES(@from, CASE WHEN @from + @chunk - 1 < @n THEN @from + @chunk - 1 ELSE @n END) AS g;
    SET @from += @chunk;
END;
SELECT 'salesorder_done' AS what, @n AS rows_, SYSDATETIME() AS at;
GO
DECLARE @n bigint = CAST(400000 * $(Scale) AS bigint) * 5, @items bigint = CAST(5000 * $(Scale) AS bigint), @from bigint = 1, @chunk bigint = 1000000;
WHILE @from <= @n
BEGIN
    INSERT dbo.SalesOrderLine WITH (TABLOCK) (OrderId, LineNum, ItemId, Qty, UnitPrice, Amount, Remark)
    SELECT CAST((g.value - 1) / 5 + 1 AS int), CAST((g.value - 1) % 5 + 1 AS smallint), CAST(1 + ABS(CHECKSUM(g.value * 13)) % @items AS int),
           (ABS(CHECKSUM(g.value * 7)) % 100000) / 1000.0, (ABS(CHECKSUM(g.value * 11)) % 10000000) / 100.0, (ABS(CHECKSUM(g.value * 19)) % 1000000000) / 100.0,
           CASE WHEN g.value % 3 = 0 THEN NULL ELSE CONCAT(N'비고-', g.value % 1000, N'-', LEFT(CONVERT(nvarchar(36), NEWID()), 12)) END
    FROM GENERATE_SERIES(@from, CASE WHEN @from + @chunk - 1 < @n THEN @from + @chunk - 1 ELSE @n END) AS g;
    SET @from += @chunk;
END;
SELECT 'salesorderline_done' AS what, @n AS rows_, SYSDATETIME() AS at;
GO
DECLARE @n bigint = CAST(1500000 * $(Scale) AS bigint), @items bigint = CAST(5000 * $(Scale) AS bigint), @from bigint = 1, @chunk bigint = 1000000;
WHILE @from <= @n
BEGIN
    INSERT dbo.StockLedger WITH (TABLOCK) (LedgerId, ItemId, TxnDate, TxnType, Qty, RefNo, Payload)
    SELECT g.value, CAST(1 + ABS(CHECKSUM(g.value * 23)) % @items AS int), DATEADD(second, -CAST(g.value % 300000000 AS int), CAST('2026-11-01' AS datetime2(3))),
           CHOOSE(1 + CAST(g.value % 6 AS int), 'IN', 'OU', 'AD', 'RT', 'MV', 'DF'), (ABS(CHECKSUM(g.value * 29)) % 1000000) / 1000.0 - 500,
           CONCAT('REF', RIGHT(CONCAT('000000000', g.value % 999999937), 9)),
           CONCAT(N'LOT', RIGHT(CONCAT('0000000', g.value % 9999991), 7), N' ', CONVERT(nvarchar(36), NEWID()), N' ',
                  CHOOSE(1 + CAST(g.value % 6 AS int), N'입고 검수 완료', N'출고 지시', N'재고 조정(실사)', N'반품 입고', N'이동 출고 창고A→창고B', N'불량 처리'),
                  N' ', CONVERT(nvarchar(36), NEWID()))
    FROM GENERATE_SERIES(@from, CASE WHEN @from + @chunk - 1 < @n THEN @from + @chunk - 1 ELSE @n END) AS g;
    SET @from += @chunk;
END;
SELECT 'stockledger_done' AS what, @n AS rows_, SYSDATETIME() AS at;
GO
CREATE UNIQUE INDEX UX_Customer_Code ON dbo.Customer (Code);
CREATE UNIQUE INDEX UX_Item_Code ON dbo.Item (Code);
CREATE INDEX IX_SalesOrder_Customer ON dbo.SalesOrder (CustomerId) INCLUDE (OrderDate);
CREATE INDEX IX_SalesOrder_OrderDate ON dbo.SalesOrder (OrderDate);
CREATE INDEX IX_SalesOrderLine_Item ON dbo.SalesOrderLine (ItemId);
CREATE INDEX IX_StockLedger_Item_Date ON dbo.StockLedger (ItemId, TxnDate);
ALTER TABLE dbo.SalesOrder WITH CHECK ADD CONSTRAINT FK_SalesOrder_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId);
ALTER TABLE dbo.SalesOrderLine WITH CHECK ADD CONSTRAINT FK_SalesOrderLine_Order FOREIGN KEY (OrderId) REFERENCES dbo.SalesOrder (OrderId);
ALTER TABLE dbo.SalesOrderLine WITH CHECK ADD CONSTRAINT FK_SalesOrderLine_Item FOREIGN KEY (ItemId) REFERENCES dbo.Item (ItemId);
ALTER TABLE dbo.StockLedger WITH CHECK ADD CONSTRAINT FK_StockLedger_Item FOREIGN KEY (ItemId) REFERENCES dbo.Item (ItemId);
SELECT 'indexes_fk_done' AS what, SYSDATETIME() AS at;
GO
CHECKPOINT;
DBCC SHRINKFILE (N'$(DbName)_log', $(LogMB)) WITH NO_INFOMSGS;
DECLARE @logMB int = (SELECT size / 128 FROM sys.database_files WHERE type = 1);
IF @logMB < $(LogMB)
BEGIN
    DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' MODIFY FILE (NAME = N''$(DbName)_log'', SIZE = $(LogMB)MB);';
    EXEC (@sql);
END;
GO
USE master;
ALTER DATABASE [$(DbName)] SET RECOVERY FULL;
GO
USE [$(DbName)];
-- 결과: 파일 크기·사용량, 테이블별 행 수·예약 크기
SELECT 'file' AS what, name, type_desc, size / 128 AS size_mb, CAST(FILEPROPERTY(name, 'SpaceUsed') / 128.0 AS decimal(12, 1)) AS used_mb FROM sys.database_files;
SELECT 'table' AS what, s.name + N'.' + t.name AS tbl, SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END) AS rows_,
       CAST(SUM(p.reserved_page_count) / 128.0 AS decimal(12, 1)) AS reserved_mb, COUNT(DISTINCT p.index_id) AS indexes
FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id JOIN sys.dm_db_partition_stats AS p ON p.object_id = t.object_id
GROUP BY s.name, t.name HAVING SUM(p.reserved_page_count) > 8 ORDER BY reserved_mb DESC;
SELECT 'totals' AS what, COUNT(*) AS tables_, (SELECT COUNT(*) FROM sys.indexes AS i JOIN sys.tables AS t ON t.object_id = i.object_id WHERE i.index_id > 0) AS indexes_,
       (SELECT COUNT(*) FROM sys.foreign_keys) AS fks, (SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE index_id IN (0, 1) AND OBJECTPROPERTY(object_id, 'IsUserTable') = 1) AS rows_total
FROM sys.tables;
SELECT 'done' AS what, DATABASEPROPERTYEX(DB_NAME(), 'Recovery') AS recovery, SYSDATETIME() AS at;
GO
