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
-- trigger cl01.A I
CREATE TRIGGER [cl01].[dbr_audit_A_I] ON [cl01].[A]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1221579390, N'[cl01].[A]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.A U
CREATE TRIGGER [cl01].[dbr_audit_A_U] ON [cl01].[A]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1221579390, N'[cl01].[A]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.A D
CREATE TRIGGER [cl01].[dbr_audit_A_D] ON [cl01].[A]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1221579390, N'[cl01].[A]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl01.A
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_A_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_A_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_A_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl01.B I
CREATE TRIGGER [cl01].[dbr_audit_B_I] ON [cl01].[B]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1253579504, N'[cl01].[B]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[a_id] AS [a_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.B U
CREATE TRIGGER [cl01].[dbr_audit_B_U] ON [cl01].[B]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1253579504, N'[cl01].[B]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[a_id] AS [a_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[a_id] AS [a_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.B D
CREATE TRIGGER [cl01].[dbr_audit_B_D] ON [cl01].[B]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1253579504, N'[cl01].[B]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[a_id] AS [a_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl01.B
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_B_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_B_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_B_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl01.C I
CREATE TRIGGER [cl01].[dbr_audit_C_I] ON [cl01].[C]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1301579675, N'[cl01].[C]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.C U
CREATE TRIGGER [cl01].[dbr_audit_C_U] ON [cl01].[C]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1301579675, N'[cl01].[C]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.C D
CREATE TRIGGER [cl01].[dbr_audit_C_D] ON [cl01].[C]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1301579675, N'[cl01].[C]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl01.C
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_C_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_C_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_C_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl01.D I
CREATE TRIGGER [cl01].[dbr_audit_D_I] ON [cl01].[D]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1349579846, N'[cl01].[D]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[c_id] AS [c_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.D U
CREATE TRIGGER [cl01].[dbr_audit_D_U] ON [cl01].[D]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1349579846, N'[cl01].[D]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[c_id] AS [c_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[c_id] AS [c_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl01.D D
CREATE TRIGGER [cl01].[dbr_audit_D_D] ON [cl01].[D]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1349579846, N'[cl01].[D]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[c_id] AS [c_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl01.D
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_D_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_D_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl01].[dbr_audit_D_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl02.Dept I
CREATE TRIGGER [cl02].[dbr_audit_Dept_I] ON [cl02].[Dept]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1397580017, N'[cl02].[Dept]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl02.Dept U
CREATE TRIGGER [cl02].[dbr_audit_Dept_U] ON [cl02].[Dept]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1397580017, N'[cl02].[Dept]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl02.Dept D
CREATE TRIGGER [cl02].[dbr_audit_Dept_D] ON [cl02].[Dept]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1397580017, N'[cl02].[Dept]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl02.Dept
EXEC sys.sp_settriggerorder @triggername = N'[cl02].[dbr_audit_Dept_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl02].[dbr_audit_Dept_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl02].[dbr_audit_Dept_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl02.Emp I
CREATE TRIGGER [cl02].[dbr_audit_Emp_I] ON [cl02].[Emp]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1429580131, N'[cl02].[Emp]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[dept_id] AS [dept_id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl02.Emp U
CREATE TRIGGER [cl02].[dbr_audit_Emp_U] ON [cl02].[Emp]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1429580131, N'[cl02].[Emp]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[dept_id] AS [dept_id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[dept_id] AS [dept_id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl02.Emp D
CREATE TRIGGER [cl02].[dbr_audit_Emp_D] ON [cl02].[Emp]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1429580131, N'[cl02].[Emp]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[dept_id] AS [dept_id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl02.Emp
EXEC sys.sp_settriggerorder @triggername = N'[cl02].[dbr_audit_Emp_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl02].[dbr_audit_Emp_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl02].[dbr_audit_Emp_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl03.Category I
CREATE TRIGGER [cl03].[dbr_audit_Category_I] ON [cl03].[Category]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1477580302, N'[cl03].[Category]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Category U
CREATE TRIGGER [cl03].[dbr_audit_Category_U] ON [cl03].[Category]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1477580302, N'[cl03].[Category]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Category D
CREATE TRIGGER [cl03].[dbr_audit_Category_D] ON [cl03].[Category]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1477580302, N'[cl03].[Category]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl03.Category
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Category_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Category_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Category_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl03.Grp I
CREATE TRIGGER [cl03].[dbr_audit_Grp_I] ON [cl03].[Grp]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1573580644, N'[cl03].[Grp]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Grp U
CREATE TRIGGER [cl03].[dbr_audit_Grp_U] ON [cl03].[Grp]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1573580644, N'[cl03].[Grp]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Grp D
CREATE TRIGGER [cl03].[dbr_audit_Grp_D] ON [cl03].[Grp]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1573580644, N'[cl03].[Grp]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl03.Grp
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Grp_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Grp_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Grp_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl03.Item I
CREATE TRIGGER [cl03].[dbr_audit_Item_I] ON [cl03].[Item]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1605580758, N'[cl03].[Item]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[grp_id] AS [grp_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Item U
CREATE TRIGGER [cl03].[dbr_audit_Item_U] ON [cl03].[Item]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1605580758, N'[cl03].[Item]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[grp_id] AS [grp_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[grp_id] AS [grp_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Item D
CREATE TRIGGER [cl03].[dbr_audit_Item_D] ON [cl03].[Item]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1605580758, N'[cl03].[Item]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[grp_id] AS [grp_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl03.Item
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Item_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Item_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Item_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl03.Product I
CREATE TRIGGER [cl03].[dbr_audit_Product_I] ON [cl03].[Product]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1509580416, N'[cl03].[Product]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[category_id] AS [category_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Product U
CREATE TRIGGER [cl03].[dbr_audit_Product_U] ON [cl03].[Product]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1509580416, N'[cl03].[Product]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[category_id] AS [category_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[category_id] AS [category_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl03.Product D
CREATE TRIGGER [cl03].[dbr_audit_Product_D] ON [cl03].[Product]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1509580416, N'[cl03].[Product]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[category_id] AS [category_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl03.Product
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Product_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Product_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl03].[dbr_audit_Product_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl04.Ch I
CREATE TRIGGER [cl04].[dbr_audit_Ch_I] ON [cl04].[Ch]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1701581100, N'[cl04].[Ch]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl04.Ch U
CREATE TRIGGER [cl04].[dbr_audit_Ch_U] ON [cl04].[Ch]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1701581100, N'[cl04].[Ch]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl04.Ch D
CREATE TRIGGER [cl04].[dbr_audit_Ch_D] ON [cl04].[Ch]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1701581100, N'[cl04].[Ch]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl04.Ch
EXEC sys.sp_settriggerorder @triggername = N'[cl04].[dbr_audit_Ch_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl04].[dbr_audit_Ch_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl04].[dbr_audit_Ch_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl04.P I
CREATE TRIGGER [cl04].[dbr_audit_P_I] ON [cl04].[P]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1669580986, N'[cl04].[P]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl04.P U
CREATE TRIGGER [cl04].[dbr_audit_P_U] ON [cl04].[P]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1669580986, N'[cl04].[P]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl04.P D
CREATE TRIGGER [cl04].[dbr_audit_P_D] ON [cl04].[P]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1669580986, N'[cl04].[P]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[name] AS [name] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl04.P
EXEC sys.sp_settriggerorder @triggername = N'[cl04].[dbr_audit_P_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl04].[dbr_audit_P_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl04].[dbr_audit_P_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl05.P I
CREATE TRIGGER [cl05].[dbr_audit_P_I] ON [cl05].[P]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1749581271, N'[cl05].[P]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.P U
CREATE TRIGGER [cl05].[dbr_audit_P_U] ON [cl05].[P]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1749581271, N'[cl05].[P]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.P D
CREATE TRIGGER [cl05].[dbr_audit_P_D] ON [cl05].[P]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1749581271, N'[cl05].[P]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl05.P
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_P_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_P_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_P_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl05.X I
CREATE TRIGGER [cl05].[dbr_audit_X_I] ON [cl05].[X]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1781581385, N'[cl05].[X]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.X U
CREATE TRIGGER [cl05].[dbr_audit_X_U] ON [cl05].[X]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1781581385, N'[cl05].[X]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.X D
CREATE TRIGGER [cl05].[dbr_audit_X_D] ON [cl05].[X]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1781581385, N'[cl05].[X]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl05.X
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_X_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_X_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_X_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl05.X2 I
CREATE TRIGGER [cl05].[dbr_audit_X2_I] ON [cl05].[X2]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1829581556, N'[cl05].[X2]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[x_id] AS [x_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.X2 U
CREATE TRIGGER [cl05].[dbr_audit_X2_U] ON [cl05].[X2]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1829581556, N'[cl05].[X2]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[x_id] AS [x_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[x_id] AS [x_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.X2 D
CREATE TRIGGER [cl05].[dbr_audit_X2_D] ON [cl05].[X2]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1829581556, N'[cl05].[X2]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[x_id] AS [x_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl05.X2
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_X2_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_X2_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_X2_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl05.Y I
CREATE TRIGGER [cl05].[dbr_audit_Y_I] ON [cl05].[Y]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1877581727, N'[cl05].[Y]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.Y U
CREATE TRIGGER [cl05].[dbr_audit_Y_U] ON [cl05].[Y]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1877581727, N'[cl05].[Y]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.Y D
CREATE TRIGGER [cl05].[dbr_audit_Y_D] ON [cl05].[Y]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1877581727, N'[cl05].[Y]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[p_id] AS [p_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl05.Y
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_Y_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_Y_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_Y_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl05.Y2 I
CREATE TRIGGER [cl05].[dbr_audit_Y2_I] ON [cl05].[Y2]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1925581898, N'[cl05].[Y2]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[y_id] AS [y_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.Y2 U
CREATE TRIGGER [cl05].[dbr_audit_Y2_U] ON [cl05].[Y2]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1925581898, N'[cl05].[Y2]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[y_id] AS [y_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[y_id] AS [y_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl05.Y2 D
CREATE TRIGGER [cl05].[dbr_audit_Y2_D] ON [cl05].[Y2]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1925581898, N'[cl05].[Y2]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[y_id] AS [y_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl05.Y2
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_Y2_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_Y2_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl05].[dbr_audit_Y2_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl06.Line I
CREATE TRIGGER [cl06].[dbr_audit_Line_I] ON [cl06].[Line]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2005582183, N'[cl06].[Line]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[ord_id] AS [ord_id], i.[qty] AS [qty] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl06.Line U
CREATE TRIGGER [cl06].[dbr_audit_Line_U] ON [cl06].[Line]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2005582183, N'[cl06].[Line]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[ord_id] AS [ord_id], d.[qty] AS [qty] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[ord_id] AS [ord_id], i.[qty] AS [qty] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl06.Line D
CREATE TRIGGER [cl06].[dbr_audit_Line_D] ON [cl06].[Line]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2005582183, N'[cl06].[Line]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[ord_id] AS [ord_id], d.[qty] AS [qty] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl06.Line
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_Line_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_Line_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_Line_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl06.LineLog I
CREATE TRIGGER [cl06].[dbr_audit_LineLog_I] ON [cl06].[LineLog]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2053582354, N'[cl06].[LineLog]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[line_id] AS [line_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[line_id] AS [line_id], i.[action] AS [action] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl06.LineLog U
CREATE TRIGGER [cl06].[dbr_audit_LineLog_U] ON [cl06].[LineLog]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2053582354, N'[cl06].[LineLog]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[line_id] AS [line_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[line_id] AS [line_id], d.[action] AS [action] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[line_id] AS [line_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[line_id] AS [line_id], i.[action] AS [action] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl06.LineLog D
CREATE TRIGGER [cl06].[dbr_audit_LineLog_D] ON [cl06].[LineLog]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2053582354, N'[cl06].[LineLog]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[line_id] AS [line_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[line_id] AS [line_id], d.[action] AS [action] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl06.LineLog
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_LineLog_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_LineLog_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_LineLog_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl06.Ord I
CREATE TRIGGER [cl06].[dbr_audit_Ord_I] ON [cl06].[Ord]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1973582069, N'[cl06].[Ord]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl06.Ord U
CREATE TRIGGER [cl06].[dbr_audit_Ord_U] ON [cl06].[Ord]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1973582069, N'[cl06].[Ord]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl06.Ord D
CREATE TRIGGER [cl06].[dbr_audit_Ord_D] ON [cl06].[Ord]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1973582069, N'[cl06].[Ord]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl06.Ord
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_Ord_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_Ord_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl06].[dbr_audit_Ord_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl07.A I
CREATE TRIGGER [cl07].[dbr_audit_A_I] ON [cl07].[A]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2101582525, N'[cl07].[A]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[v] AS [v] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.A U
CREATE TRIGGER [cl07].[dbr_audit_A_U] ON [cl07].[A]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2101582525, N'[cl07].[A]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[v] AS [v] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[v] AS [v] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.A D
CREATE TRIGGER [cl07].[dbr_audit_A_D] ON [cl07].[A]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2101582525, N'[cl07].[A]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[v] AS [v] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl07.A
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_A_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_A_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_A_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl07.B I
CREATE TRIGGER [cl07].[dbr_audit_B_I] ON [cl07].[B]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2133582639, N'[cl07].[B]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[a_id] AS [a_id], i.[total] AS [total] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.B U
CREATE TRIGGER [cl07].[dbr_audit_B_U] ON [cl07].[B]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2133582639, N'[cl07].[B]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[a_id] AS [a_id], d.[total] AS [total] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[a_id] AS [a_id], i.[total] AS [total] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.B D
CREATE TRIGGER [cl07].[dbr_audit_B_D] ON [cl07].[B]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2133582639, N'[cl07].[B]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[a_id] AS [a_id], d.[total] AS [total] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl07.B
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_B_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_B_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_B_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl07.C I
CREATE TRIGGER [cl07].[dbr_audit_C_I] ON [cl07].[C]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 18099105, N'[cl07].[C]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[b_id] AS [b_id], i.[note] AS [note] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.C U
CREATE TRIGGER [cl07].[dbr_audit_C_U] ON [cl07].[C]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 18099105, N'[cl07].[C]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[b_id] AS [b_id], d.[note] AS [note] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[b_id] AS [b_id], i.[note] AS [note] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.C D
CREATE TRIGGER [cl07].[dbr_audit_C_D] ON [cl07].[C]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 18099105, N'[cl07].[C]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[b_id] AS [b_id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[b_id] AS [b_id], d.[note] AS [note] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl07.C
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_C_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_C_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_C_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger cl07.D I
CREATE TRIGGER [cl07].[dbr_audit_D_I] ON [cl07].[D]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 50099219, N'[cl07].[D]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[cnt] AS [cnt] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.D U
CREATE TRIGGER [cl07].[dbr_audit_D_U] ON [cl07].[D]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 50099219, N'[cl07].[D]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[cnt] AS [cnt] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[id] AS [id], i.[cnt] AS [cnt] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger cl07.D D
CREATE TRIGGER [cl07].[dbr_audit_D_D] ON [cl07].[D]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 50099219, N'[cl07].[D]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[id] AS [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[id] AS [id], d.[cnt] AS [cnt] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order cl07.D
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_D_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_D_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[cl07].[dbr_audit_D_D]', @order = N'Last', @stmttype = N'DELETE';
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
    SELECT @dbr_seq, 130099504, N'[cl08].[Acct]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
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
    SELECT @dbr_seq, 130099504, N'[cl08].[Acct]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
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
    SELECT @dbr_seq, 130099504, N'[cl08].[Acct]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
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
