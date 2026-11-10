-- schema
CREATE SCHEMA audit AUTHORIZATION dbo;
GO
-- sequence
CREATE SEQUENCE audit.CaptureSeq AS bigint START WITH 1 INCREMENT BY 1;
GO
-- AuditHeader
CREATE TABLE audit.AuditHeader (
    captureSeq bigint NOT NULL CONSTRAINT PK_AuditHeader PRIMARY KEY,
    tableObjectId int NOT NULL,
    tableName nvarchar(300) NOT NULL,
    operation char(1) NOT NULL CONSTRAINT CK_AuditHeader_operation CHECK (operation IN ('I', 'U', 'D')),
    insertedCount bigint NOT NULL,
    deletedCount bigint NOT NULL,
    nestLevel int NOT NULL,
    tranCount int NOT NULL,
    imageMode varchar(4) NOT NULL CONSTRAINT CK_AuditHeader_imageMode CHECK (imageMode IN ('FULL', 'KEYS'))
);
GO
-- AuditRowImage
CREATE TABLE audit.AuditRowImage (
    captureSeq bigint NOT NULL,
    imageKind varchar(8) NOT NULL,
    ordinal int NOT NULL,
    keyJson nvarchar(max) NULL,
    rowJson nvarchar(max) NULL,
    CONSTRAINT PK_AuditRowImage PRIMARY KEY (captureSeq, imageKind, ordinal)
);
GO
-- AuditControl
CREATE TABLE audit.AuditControl (
    id tinyint NOT NULL CONSTRAINT PK_AuditControl PRIMARY KEY CONSTRAINT CK_AuditControl_id CHECK (id = 1),
    mode varchar(4) NOT NULL CONSTRAINT CK_AuditControl_mode CHECK (mode IN ('FULL', 'KEYS')),
    cap bigint NOT NULL
);
INSERT INTO audit.AuditControl (id, mode, cap) VALUES (1, 'FULL', 100000);
GO
-- trigger cl08.Acct I
CREATE TRIGGER [cl08].[dbr_audit_Acct_I] ON [cl08].[Acct]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1221579390, N'[cl08].[Acct]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[bal] AS [bal], i.[touched] AS [touched] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl08.Acct U
CREATE TRIGGER [cl08].[dbr_audit_Acct_U] ON [cl08].[Acct]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1221579390, N'[cl08].[Acct]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[bal] AS [bal], d.[touched] AS [touched] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[bal] AS [bal], i.[touched] AS [touched] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl08.Acct D
CREATE TRIGGER [cl08].[dbr_audit_Acct_D] ON [cl08].[Acct]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1221579390, N'[cl08].[Acct]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[bal] AS [bal], d.[touched] AS [touched] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl08.Acct
EXEC sys.sp_settriggerorder @triggername = N'[cl08].[dbr_audit_Acct_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl08].[dbr_audit_Acct_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl08].[dbr_audit_Acct_D]', @order = N'Last', @stmttype = N'DELETE';
GO
