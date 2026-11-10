/* SP1 — AW 복사본(AW_SpikeA, 2026-11-10 AW_Sandbox에서 변경: AW_Sandbox는 backend-3 전용): Sales.SalesOrderHeader 1건 DELETE 관찰.
   감사 저장소·손 감사 트리거를 트랜잭션 안에서 만들고 끝에 ROLLBACK.
   사용자 트리거 본문·순서는 바꾸지 않는다. 대상 테이블에만 감사 트리거를 둔다. */
USE AW_SpikeA;
GO
SET QUOTED_IDENTIFIER ON;   /* sqlcmd 기본값은 OFF. SOH에는 계산 열 인덱스가 있어 OFF면 DELETE가 1934로 실패한다(2026-11-10 1차 실행) */
SET NOCOUNT ON;
SET XACT_ABORT ON;   /* 중간 오류 시 전체 롤백 */
BEGIN TRAN;
GO
/* 0. FK·트리거 메타데이터(기대값 확인용) */
SELECT OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id) AS childTable, fk.name, fk.delete_referential_action_desc, fk.is_disabled
FROM sys.foreign_keys AS fk WHERE fk.referenced_object_id = OBJECT_ID(N'Sales.SalesOrderHeader');
SELECT OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS tableName, t.name, e.type_desc, e.is_first, e.is_last, t.is_disabled
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
WHERE t.parent_id IN (OBJECT_ID(N'Sales.SalesOrderHeader'), OBJECT_ID(N'Sales.SalesOrderDetail'), OBJECT_ID(N'Sales.SalesOrderHeaderSalesReason'));
SELECT name, is_recursive_triggers_on FROM sys.databases WHERE name = DB_NAME();
GO
/* 1. 감사 저장소(없을 때만, 계약과 같은 정의) */
IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA audit AUTHORIZATION dbo');
GO
IF OBJECT_ID(N'audit.CaptureSeq', N'SO') IS NULL CREATE SEQUENCE audit.CaptureSeq AS bigint START WITH 1 INCREMENT BY 1;
IF OBJECT_ID(N'audit.AuditHeader', N'U') IS NULL
CREATE TABLE audit.AuditHeader (captureSeq bigint NOT NULL CONSTRAINT PK_AuditHeader PRIMARY KEY, tableObjectId int NOT NULL, tableName nvarchar(300) NOT NULL,
    operation char(1) NOT NULL CONSTRAINT CK_AuditHeader_operation CHECK (operation IN ('I','U','D')), insertedCount bigint NOT NULL, deletedCount bigint NOT NULL,
    nestLevel int NOT NULL, tranCount int NOT NULL, imageMode varchar(4) NOT NULL CONSTRAINT CK_AuditHeader_imageMode CHECK (imageMode IN ('FULL','KEYS')));
IF OBJECT_ID(N'audit.AuditRowImage', N'U') IS NULL
CREATE TABLE audit.AuditRowImage (captureSeq bigint NOT NULL, imageKind varchar(8) NOT NULL, ordinal int NOT NULL, keyJson nvarchar(max) NULL, rowJson nvarchar(max) NULL,
    CONSTRAINT PK_AuditRowImage PRIMARY KEY (captureSeq, imageKind, ordinal));
IF OBJECT_ID(N'audit.AuditControl', N'U') IS NULL
CREATE TABLE audit.AuditControl (id tinyint NOT NULL CONSTRAINT PK_AuditControl PRIMARY KEY CONSTRAINT CK_AuditControl_id CHECK (id = 1),
    mode varchar(4) NOT NULL CONSTRAINT CK_AuditControl_mode CHECK (mode IN ('FULL','KEYS')), cap bigint NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM audit.AuditControl WHERE id = 1) INSERT audit.AuditControl (id, mode, cap) VALUES (1, 'FULL', 100000);
GO
/* 2. 손 감사 트리거 4개 */
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeader_D] ON [Sales].[SalesOrderHeader] AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, OBJECT_ID(N'[Sales].[SalesOrderHeader]'), N'[Sales].[SalesOrderHeader]', 'D',
           (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
           (SELECT d.SalesOrderID AS SalesOrderID FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
           CASE WHEN @dbr_mode = 'KEYS' THEN NULL
                ELSE (SELECT d.SalesOrderID AS SalesOrderID, d.RevisionNumber AS RevisionNumber, d.Status AS Status, d.SubTotal AS SubTotal, d.ModifiedDate AS ModifiedDate
                      FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END
GO
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeader_U] ON [Sales].[SalesOrderHeader] AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, OBJECT_ID(N'[Sales].[SalesOrderHeader]'), N'[Sales].[SalesOrderHeader]', 'U',
           (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
           (SELECT d.SalesOrderID AS SalesOrderID FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
           CASE WHEN @dbr_mode = 'KEYS' THEN NULL
                ELSE (SELECT d.SalesOrderID AS SalesOrderID, d.RevisionNumber AS RevisionNumber, d.Status AS Status, d.SubTotal AS SubTotal, d.Comment AS Comment, d.ModifiedDate AS ModifiedDate
                      FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
           (SELECT i.SalesOrderID AS SalesOrderID FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
           CASE WHEN @dbr_mode = 'KEYS' THEN NULL
                ELSE (SELECT i.SalesOrderID AS SalesOrderID, i.RevisionNumber AS RevisionNumber, i.Status AS Status, i.SubTotal AS SubTotal, i.Comment AS Comment, i.ModifiedDate AS ModifiedDate
                      FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END
GO
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderDetail_D] ON [Sales].[SalesOrderDetail] AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, OBJECT_ID(N'[Sales].[SalesOrderDetail]'), N'[Sales].[SalesOrderDetail]', 'D',
           (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
           (SELECT d.SalesOrderID AS SalesOrderID, d.SalesOrderDetailID AS SalesOrderDetailID FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
           CASE WHEN @dbr_mode = 'KEYS' THEN NULL
                ELSE (SELECT d.SalesOrderID AS SalesOrderID, d.SalesOrderDetailID AS SalesOrderDetailID, d.OrderQty AS OrderQty, d.ProductID AS ProductID, d.UnitPrice AS UnitPrice
                      FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END
GO
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeaderSalesReason_D] ON [Sales].[SalesOrderHeaderSalesReason] AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, OBJECT_ID(N'[Sales].[SalesOrderHeaderSalesReason]'), N'[Sales].[SalesOrderHeaderSalesReason]', 'D',
           (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
           (SELECT d.SalesOrderID AS SalesOrderID, d.SalesReasonID AS SalesReasonID FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
           CASE WHEN @dbr_mode = 'KEYS' THEN NULL
                ELSE (SELECT d.SalesOrderID AS SalesOrderID, d.SalesReasonID AS SalesReasonID, d.ModifiedDate AS ModifiedDate
                      FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END
GO
/* 사용자 Last가 없을 때만 Last 지정 */
DECLARE @t TABLE (trg nvarchar(300), tbl nvarchar(300), evt nvarchar(10));
INSERT @t VALUES (N'[Sales].[dbr_audit_SalesOrderHeader_D]', N'Sales.SalesOrderHeader', N'DELETE'),
                 (N'[Sales].[dbr_audit_SalesOrderHeader_U]', N'Sales.SalesOrderHeader', N'UPDATE'),
                 (N'[Sales].[dbr_audit_SalesOrderDetail_D]', N'Sales.SalesOrderDetail', N'DELETE'),
                 (N'[Sales].[dbr_audit_SalesOrderHeaderSalesReason_D]', N'Sales.SalesOrderHeaderSalesReason', N'DELETE');
DECLARE @trg nvarchar(300), @tbl nvarchar(300), @evt nvarchar(10);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT trg, tbl, evt FROM @t;
OPEN c; FETCH NEXT FROM c INTO @trg, @tbl, @evt;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
                   WHERE t.parent_id = OBJECT_ID(@tbl) AND e.type_desc = @evt AND e.is_last = 1)
        EXEC sys.sp_settriggerorder @triggername = @trg, @order = N'Last', @stmttype = @evt;
    FETCH NEXT FROM c INTO @trg, @tbl, @evt;
END
CLOSE c; DEALLOCATE c;
/* 확인: QUOTED_IDENTIFIER OFF로 저장된 모듈(0행이어야 함. AW 자체 모듈 포함, 2026-11-10 run-sql -I 이후 재실행) */
SELECT OBJECT_SCHEMA_NAME(object_id) AS schemaName, OBJECT_NAME(object_id) AS objectName, uses_quoted_identifier FROM sys.sql_modules WHERE uses_quoted_identifier = 0;
SELECT COUNT(*) AS modules, SUM(CASE WHEN uses_quoted_identifier = 0 THEN 1 ELSE 0 END) AS quotedIdentOff,
       SESSIONPROPERTY('QUOTED_IDENTIFIER') AS sessionQuotedIdentifier FROM sys.sql_modules;
GO
/* 3. 실행과 관찰: 상세·사유가 모두 있는 주문 1건 */
DECLARE @id int = (SELECT TOP (1) h.SalesOrderID FROM Sales.SalesOrderHeader AS h
                   WHERE EXISTS (SELECT 1 FROM Sales.SalesOrderHeaderSalesReason AS r WHERE r.SalesOrderID = h.SalesOrderID)
                   ORDER BY h.SalesOrderID);
DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
SELECT @id AS salesOrderId,
       (SELECT COUNT(*) FROM Sales.SalesOrderDetail WHERE SalesOrderID = @id) AS expectDetailRows,
       (SELECT COUNT(*) FROM Sales.SalesOrderHeaderSalesReason WHERE SalesOrderID = @id) AS expectReasonRows;
DELETE Sales.SalesOrderHeader WHERE SalesOrderID = @id;
SELECT @@ROWCOUNT AS rowcountAfterDelete;
SELECT ROW_NUMBER() OVER (ORDER BY h.captureSeq) AS pos, h.captureSeq, h.tableName, h.operation, h.nestLevel, h.insertedCount, h.deletedCount, h.tranCount, h.imageMode
FROM audit.AuditHeader AS h WHERE h.captureSeq > @b ORDER BY h.captureSeq;
/* 판정 보조: 연쇄 대상(SOD, SOHSR) 깊이 = 직접 대상 깊이(H2), iduSalesOrderDetail이 일으킨 SOH UPDATE(0행 예상)는 깊이 2이고 SOH 감사(D)보다 먼저(H1) */
SELECT v.hyp, v.verdict FROM (VALUES
 ('H2', CASE WHEN (SELECT COUNT(DISTINCT nestLevel) FROM audit.AuditHeader WHERE captureSeq > @b AND operation = 'D') = 1
              AND (SELECT MAX(nestLevel) FROM audit.AuditHeader WHERE captureSeq > @b AND operation = 'D') = 1 THEN 'HOLDS' ELSE 'BROKEN' END),
 ('H1', CASE WHEN NOT EXISTS (SELECT 1 FROM audit.AuditHeader AS u
                              WHERE u.captureSeq > @b AND u.operation = 'U' AND u.tableName = N'[Sales].[SalesOrderHeader]'
                                AND u.captureSeq > (SELECT captureSeq FROM audit.AuditHeader WHERE captureSeq > @b AND operation = 'D' AND tableName = N'[Sales].[SalesOrderHeader]'))
             THEN 'HOLDS' ELSE 'BROKEN' END),
 (N'A3(참고) SOH U 0행 발동', CASE WHEN EXISTS (SELECT 1 FROM audit.AuditHeader WHERE captureSeq > @b AND operation = 'U' AND insertedCount = 0) THEN 'RECORDED' ELSE 'NOT_RECORDED' END)
) AS v (hyp, verdict);
GO
ROLLBACK;
GO
SELECT @@TRANCOUNT AS tranCountAfterRollback, OBJECT_ID(N'Sales.dbr_audit_SalesOrderHeader_D') AS auditTriggerGone_null_expected;
GO
