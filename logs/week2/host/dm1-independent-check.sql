-- 2주차 통합 Host 확인: DM1(SOH 43659 DELETE) 연쇄 대상의 독립 확인(기준 스냅샷 읽기만)
SET NOCOUNT ON;
SELECT N'SOD' AS t, COUNT(*) AS n FROM Sales.SalesOrderDetail WHERE SalesOrderID = 43659
UNION ALL SELECT N'SOHSR', COUNT(*) FROM Sales.SalesOrderHeaderSalesReason WHERE SalesOrderID = 43659;
SELECT COUNT(*) AS preview_user FROM sys.database_principals WHERE name = N'dbr_preview_user';
SELECT COUNT(*) AS databaselog_create_user FROM dbo.DatabaseLog WHERE Event = N'CREATE_USER' AND TSQL LIKE N'%dbr_preview_user%';
