-- AW_Sandbox 감사 수집 스모크: BEGIN TRAN 안 DML 2건 → 감사 헤더·행 이미지 확인 → ROLLBACK
-- 1) Sales.SalesOrderDetail 1행 OrderQty UPDATE(개인 고객 주문, iduSalesOrderDetail 연쇄 발동)
-- 2) Sales.SalesOrderHeader 1행 DELETE(상세 2행, ON DELETE CASCADE 2개)
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @sod int, @soh int, @qty smallint;
SELECT TOP (1) @sod = d.SalesOrderDetailID, @qty = d.OrderQty
FROM Sales.SalesOrderDetail AS d
JOIN Sales.SalesOrderHeader AS h ON h.SalesOrderID = d.SalesOrderID
JOIN Sales.Customer AS c ON c.CustomerID = h.CustomerID
WHERE c.PersonID IS NOT NULL AND c.StoreID IS NULL
ORDER BY d.SalesOrderDetailID;
SELECT TOP (1) @soh = h.SalesOrderID
FROM Sales.SalesOrderHeader AS h
WHERE (SELECT COUNT(*) FROM Sales.SalesOrderDetail AS d WHERE d.SalesOrderID = h.SalesOrderID) = 2
  AND EXISTS (SELECT 1 FROM Sales.SalesOrderHeaderSalesReason AS r WHERE r.SalesOrderID = h.SalesOrderID)
ORDER BY h.SalesOrderID;

SELECT DB_NAME() AS db, SESSIONPROPERTY('QUOTED_IDENTIFIER') AS sessionQI, @@TRANCOUNT AS tranCountBefore,
       (SELECT COUNT(*) FROM audit.AuditHeader) AS headersBefore, (SELECT mode FROM audit.AuditControl WHERE id = 1) AS mode,
       @sod AS updSalesOrderDetailID, @qty AS orderQtyBefore, @soh AS delSalesOrderID,
       (SELECT COUNT(*) FROM Sales.SalesOrderDetail WHERE SalesOrderID = @soh) AS sohDetails,
       (SELECT COUNT(*) FROM Sales.SalesOrderHeaderSalesReason WHERE SalesOrderID = @soh) AS sohReasons;

BEGIN TRAN;
DECLARE @b0 bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderDetailID = @sod;
DECLARE @rc1 int = @@ROWCOUNT;
DECLARE @b1 bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
DELETE FROM Sales.SalesOrderHeader WHERE SalesOrderID = @soh;
DECLARE @rc2 int = @@ROWCOUNT;

SELECT @rc1 AS rowcountAfterUpdate, @rc2 AS rowcountAfterDelete, @b0 AS boundary0, @b1 AS boundary1;

-- 헤더 표(문장 1 = 경계 0 초과 ~ 경계 1, 문장 2 = 경계 1 초과)
SELECT CASE WHEN h.captureSeq <= @b1 THEN 1 ELSE 2 END AS stmt, h.captureSeq, h.tableName, h.operation,
       h.insertedCount, h.deletedCount, h.nestLevel, h.tranCount, h.imageMode,
       (SELECT COUNT(*) FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq) AS images
FROM audit.AuditHeader AS h
WHERE h.captureSeq > @b0
ORDER BY h.captureSeq;

-- 행 이미지 수
SELECT CASE WHEN r.captureSeq <= @b1 THEN 1 ELSE 2 END AS stmt, r.imageKind, COUNT(*) AS images,
       SUM(CASE WHEN r.rowJson IS NOT NULL THEN 1 ELSE 0 END) AS withRowJson, SUM(CASE WHEN ISJSON(r.rowJson) = 1 THEN 1 ELSE 0 END) AS validJson
FROM audit.AuditRowImage AS r
WHERE r.captureSeq > @b0
GROUP BY CASE WHEN r.captureSeq <= @b1 THEN 1 ELSE 2 END, r.imageKind
ORDER BY stmt, r.imageKind;
SELECT COUNT(*) AS totalImages FROM audit.AuditRowImage WHERE captureSeq > @b0;

-- rowJson 예: 문장 1 SalesOrderDetail UPDATE의 DELETED/INSERTED(OrderQty·LineTotal 전후)
SELECT r.captureSeq, r.imageKind, r.ordinal, r.keyJson, JSON_VALUE(r.rowJson, '$.OrderQty') AS orderQty,
       JSON_VALUE(r.rowJson, '$.LineTotal') AS lineTotal, LEN(r.rowJson) AS rowJsonLength
FROM audit.AuditRowImage AS r
JOIN audit.AuditHeader AS h ON h.captureSeq = r.captureSeq
WHERE h.captureSeq > @b0 AND h.captureSeq <= @b1 AND h.tableName = N'[Sales].[SalesOrderDetail]' AND h.operation = 'U'
ORDER BY r.imageKind;
-- 같은 묶음 INSERTED rowJson 전체(sqlcmd는 긴 값을 자르므로 원문 1행 + 속성별로도 보임)
DECLARE @rowJson nvarchar(max) = (
    SELECT r.rowJson FROM audit.AuditRowImage AS r JOIN audit.AuditHeader AS h ON h.captureSeq = r.captureSeq
    WHERE h.captureSeq > @b0 AND h.captureSeq <= @b1 AND h.tableName = N'[Sales].[SalesOrderDetail]' AND h.operation = 'U' AND r.imageKind = 'INSERTED');
PRINT @rowJson;
SELECT [key], [value], CASE [type] WHEN 0 THEN 'null' WHEN 1 THEN 'string' WHEN 2 THEN 'number' WHEN 3 THEN 'bool' ELSE 'other' END AS jsonType
FROM OPENJSON(@rowJson);

ROLLBACK;

SELECT @@TRANCOUNT AS tranCountAfter,
       (SELECT COUNT(*) FROM audit.AuditHeader) AS headersAfterRollback,
       (SELECT COUNT(*) FROM audit.AuditRowImage) AS imagesAfterRollback,
       (SELECT OrderQty FROM Sales.SalesOrderDetail WHERE SalesOrderDetailID = @sod) AS orderQtyAfterRollback,
       (SELECT COUNT(*) FROM Sales.SalesOrderHeader WHERE SalesOrderID = @soh) AS sohRowAfterRollback,
       (SELECT COUNT(*) FROM dbo.ErrorLog) AS errorLogRows,
       (SELECT CAST(current_value AS bigint) FROM sys.sequences WHERE object_id = OBJECT_ID(N'audit.CaptureSeq')) AS captureSeqCurrent;
