-- session
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
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
-- trigger HumanResources.Department I
CREATE TRIGGER [HumanResources].[dbr_audit_Department_I] ON [HumanResources].[Department]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 178099675, N'[HumanResources].[Department]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[DepartmentID] AS [DepartmentID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[DepartmentID] AS [DepartmentID], i.[Name] AS [Name], i.[GroupName] AS [GroupName], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.Department U
CREATE TRIGGER [HumanResources].[dbr_audit_Department_U] ON [HumanResources].[Department]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 178099675, N'[HumanResources].[Department]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[DepartmentID] AS [DepartmentID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[DepartmentID] AS [DepartmentID], d.[Name] AS [Name], d.[GroupName] AS [GroupName], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[DepartmentID] AS [DepartmentID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[DepartmentID] AS [DepartmentID], i.[Name] AS [Name], i.[GroupName] AS [GroupName], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.Department D
CREATE TRIGGER [HumanResources].[dbr_audit_Department_D] ON [HumanResources].[Department]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 178099675, N'[HumanResources].[Department]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[DepartmentID] AS [DepartmentID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[DepartmentID] AS [DepartmentID], d.[Name] AS [Name], d.[GroupName] AS [GroupName], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order HumanResources.Department
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Department_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Department_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Department_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger HumanResources.Employee I
CREATE TRIGGER [HumanResources].[dbr_audit_Employee_I] ON [HumanResources].[Employee]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 370100359, N'[HumanResources].[Employee]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[NationalIDNumber] AS [NationalIDNumber], i.[LoginID] AS [LoginID], i.[OrganizationNode].ToString() AS [OrganizationNode], i.[OrganizationLevel] AS [OrganizationLevel], i.[JobTitle] AS [JobTitle], i.[BirthDate] AS [BirthDate], i.[MaritalStatus] AS [MaritalStatus], i.[Gender] AS [Gender], i.[HireDate] AS [HireDate], i.[SalariedFlag] AS [SalariedFlag], i.[VacationHours] AS [VacationHours], i.[SickLeaveHours] AS [SickLeaveHours], i.[CurrentFlag] AS [CurrentFlag], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.Employee U
CREATE TRIGGER [HumanResources].[dbr_audit_Employee_U] ON [HumanResources].[Employee]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 370100359, N'[HumanResources].[Employee]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[NationalIDNumber] AS [NationalIDNumber], d.[LoginID] AS [LoginID], d.[OrganizationNode].ToString() AS [OrganizationNode], d.[OrganizationLevel] AS [OrganizationLevel], d.[JobTitle] AS [JobTitle], d.[BirthDate] AS [BirthDate], d.[MaritalStatus] AS [MaritalStatus], d.[Gender] AS [Gender], d.[HireDate] AS [HireDate], d.[SalariedFlag] AS [SalariedFlag], d.[VacationHours] AS [VacationHours], d.[SickLeaveHours] AS [SickLeaveHours], d.[CurrentFlag] AS [CurrentFlag], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[NationalIDNumber] AS [NationalIDNumber], i.[LoginID] AS [LoginID], i.[OrganizationNode].ToString() AS [OrganizationNode], i.[OrganizationLevel] AS [OrganizationLevel], i.[JobTitle] AS [JobTitle], i.[BirthDate] AS [BirthDate], i.[MaritalStatus] AS [MaritalStatus], i.[Gender] AS [Gender], i.[HireDate] AS [HireDate], i.[SalariedFlag] AS [SalariedFlag], i.[VacationHours] AS [VacationHours], i.[SickLeaveHours] AS [SickLeaveHours], i.[CurrentFlag] AS [CurrentFlag], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.Employee D
CREATE TRIGGER [HumanResources].[dbr_audit_Employee_D] ON [HumanResources].[Employee]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 370100359, N'[HumanResources].[Employee]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[NationalIDNumber] AS [NationalIDNumber], d.[LoginID] AS [LoginID], d.[OrganizationNode].ToString() AS [OrganizationNode], d.[OrganizationLevel] AS [OrganizationLevel], d.[JobTitle] AS [JobTitle], d.[BirthDate] AS [BirthDate], d.[MaritalStatus] AS [MaritalStatus], d.[Gender] AS [Gender], d.[HireDate] AS [HireDate], d.[SalariedFlag] AS [SalariedFlag], d.[VacationHours] AS [VacationHours], d.[SickLeaveHours] AS [SickLeaveHours], d.[CurrentFlag] AS [CurrentFlag], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order HumanResources.Employee
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Employee_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Employee_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Employee_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger HumanResources.EmployeeDepartmentHistory I
CREATE TRIGGER [HumanResources].[dbr_audit_EmployeeDepartmentHistory_I] ON [HumanResources].[EmployeeDepartmentHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 578101100, N'[HumanResources].[EmployeeDepartmentHistory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[StartDate] AS [StartDate], i.[DepartmentID] AS [DepartmentID], i.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[DepartmentID] AS [DepartmentID], i.[ShiftID] AS [ShiftID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.EmployeeDepartmentHistory U
CREATE TRIGGER [HumanResources].[dbr_audit_EmployeeDepartmentHistory_U] ON [HumanResources].[EmployeeDepartmentHistory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 578101100, N'[HumanResources].[EmployeeDepartmentHistory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[StartDate] AS [StartDate], d.[DepartmentID] AS [DepartmentID], d.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[DepartmentID] AS [DepartmentID], d.[ShiftID] AS [ShiftID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[StartDate] AS [StartDate], i.[DepartmentID] AS [DepartmentID], i.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[DepartmentID] AS [DepartmentID], i.[ShiftID] AS [ShiftID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.EmployeeDepartmentHistory D
CREATE TRIGGER [HumanResources].[dbr_audit_EmployeeDepartmentHistory_D] ON [HumanResources].[EmployeeDepartmentHistory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 578101100, N'[HumanResources].[EmployeeDepartmentHistory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[StartDate] AS [StartDate], d.[DepartmentID] AS [DepartmentID], d.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[DepartmentID] AS [DepartmentID], d.[ShiftID] AS [ShiftID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order HumanResources.EmployeeDepartmentHistory
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_EmployeeDepartmentHistory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_EmployeeDepartmentHistory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_EmployeeDepartmentHistory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger HumanResources.EmployeePayHistory I
CREATE TRIGGER [HumanResources].[dbr_audit_EmployeePayHistory_I] ON [HumanResources].[EmployeePayHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 626101271, N'[HumanResources].[EmployeePayHistory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[RateChangeDate] AS [RateChangeDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[RateChangeDate] AS [RateChangeDate], i.[Rate] AS [Rate], i.[PayFrequency] AS [PayFrequency], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.EmployeePayHistory U
CREATE TRIGGER [HumanResources].[dbr_audit_EmployeePayHistory_U] ON [HumanResources].[EmployeePayHistory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 626101271, N'[HumanResources].[EmployeePayHistory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[RateChangeDate] AS [RateChangeDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[RateChangeDate] AS [RateChangeDate], d.[Rate] AS [Rate], d.[PayFrequency] AS [PayFrequency], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[RateChangeDate] AS [RateChangeDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[RateChangeDate] AS [RateChangeDate], i.[Rate] AS [Rate], i.[PayFrequency] AS [PayFrequency], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.EmployeePayHistory D
CREATE TRIGGER [HumanResources].[dbr_audit_EmployeePayHistory_D] ON [HumanResources].[EmployeePayHistory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 626101271, N'[HumanResources].[EmployeePayHistory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[RateChangeDate] AS [RateChangeDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[RateChangeDate] AS [RateChangeDate], d.[Rate] AS [Rate], d.[PayFrequency] AS [PayFrequency], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order HumanResources.EmployeePayHistory
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_EmployeePayHistory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_EmployeePayHistory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_EmployeePayHistory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger HumanResources.JobCandidate I
CREATE TRIGGER [HumanResources].[dbr_audit_JobCandidate_I] ON [HumanResources].[JobCandidate]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 722101613, N'[HumanResources].[JobCandidate]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[JobCandidateID] AS [JobCandidateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[JobCandidateID] AS [JobCandidateID], i.[BusinessEntityID] AS [BusinessEntityID], CONVERT(nvarchar(max), i.[Resume]) AS [Resume], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.JobCandidate U
CREATE TRIGGER [HumanResources].[dbr_audit_JobCandidate_U] ON [HumanResources].[JobCandidate]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 722101613, N'[HumanResources].[JobCandidate]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[JobCandidateID] AS [JobCandidateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[JobCandidateID] AS [JobCandidateID], d.[BusinessEntityID] AS [BusinessEntityID], CONVERT(nvarchar(max), d.[Resume]) AS [Resume], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[JobCandidateID] AS [JobCandidateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[JobCandidateID] AS [JobCandidateID], i.[BusinessEntityID] AS [BusinessEntityID], CONVERT(nvarchar(max), i.[Resume]) AS [Resume], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.JobCandidate D
CREATE TRIGGER [HumanResources].[dbr_audit_JobCandidate_D] ON [HumanResources].[JobCandidate]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 722101613, N'[HumanResources].[JobCandidate]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[JobCandidateID] AS [JobCandidateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[JobCandidateID] AS [JobCandidateID], d.[BusinessEntityID] AS [BusinessEntityID], CONVERT(nvarchar(max), d.[Resume]) AS [Resume], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order HumanResources.JobCandidate
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_JobCandidate_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_JobCandidate_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_JobCandidate_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger HumanResources.Shift I
CREATE TRIGGER [HumanResources].[dbr_audit_Shift_I] ON [HumanResources].[Shift]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1326627769, N'[HumanResources].[Shift]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ShiftID] AS [ShiftID], i.[Name] AS [Name], i.[StartTime] AS [StartTime], i.[EndTime] AS [EndTime], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.Shift U
CREATE TRIGGER [HumanResources].[dbr_audit_Shift_U] ON [HumanResources].[Shift]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1326627769, N'[HumanResources].[Shift]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ShiftID] AS [ShiftID], d.[Name] AS [Name], d.[StartTime] AS [StartTime], d.[EndTime] AS [EndTime], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ShiftID] AS [ShiftID], i.[Name] AS [Name], i.[StartTime] AS [StartTime], i.[EndTime] AS [EndTime], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger HumanResources.Shift D
CREATE TRIGGER [HumanResources].[dbr_audit_Shift_D] ON [HumanResources].[Shift]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1326627769, N'[HumanResources].[Shift]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ShiftID] AS [ShiftID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ShiftID] AS [ShiftID], d.[Name] AS [Name], d.[StartTime] AS [StartTime], d.[EndTime] AS [EndTime], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order HumanResources.Shift
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Shift_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Shift_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[HumanResources].[dbr_audit_Shift_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.Address I
CREATE TRIGGER [Person].[dbr_audit_Address_I] ON [Person].[Address]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1653580929, N'[Person].[Address]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[AddressID] AS [AddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[AddressID] AS [AddressID], i.[AddressLine1] AS [AddressLine1], i.[AddressLine2] AS [AddressLine2], i.[City] AS [City], i.[StateProvinceID] AS [StateProvinceID], i.[PostalCode] AS [PostalCode], i.[SpatialLocation].ToString() AS [SpatialLocation], i.[SpatialLocation].STSrid AS [SpatialLocation@srid], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.Address U
CREATE TRIGGER [Person].[dbr_audit_Address_U] ON [Person].[Address]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1653580929, N'[Person].[Address]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[AddressID] AS [AddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[AddressID] AS [AddressID], d.[AddressLine1] AS [AddressLine1], d.[AddressLine2] AS [AddressLine2], d.[City] AS [City], d.[StateProvinceID] AS [StateProvinceID], d.[PostalCode] AS [PostalCode], d.[SpatialLocation].ToString() AS [SpatialLocation], d.[SpatialLocation].STSrid AS [SpatialLocation@srid], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[AddressID] AS [AddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[AddressID] AS [AddressID], i.[AddressLine1] AS [AddressLine1], i.[AddressLine2] AS [AddressLine2], i.[City] AS [City], i.[StateProvinceID] AS [StateProvinceID], i.[PostalCode] AS [PostalCode], i.[SpatialLocation].ToString() AS [SpatialLocation], i.[SpatialLocation].STSrid AS [SpatialLocation@srid], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.Address D
CREATE TRIGGER [Person].[dbr_audit_Address_D] ON [Person].[Address]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1653580929, N'[Person].[Address]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[AddressID] AS [AddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[AddressID] AS [AddressID], d.[AddressLine1] AS [AddressLine1], d.[AddressLine2] AS [AddressLine2], d.[City] AS [City], d.[StateProvinceID] AS [StateProvinceID], d.[PostalCode] AS [PostalCode], d.[SpatialLocation].ToString() AS [SpatialLocation], d.[SpatialLocation].STSrid AS [SpatialLocation@srid], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.Address
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Address_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Address_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Address_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.AddressType I
CREATE TRIGGER [Person].[dbr_audit_AddressType_I] ON [Person].[AddressType]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1701581100, N'[Person].[AddressType]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[AddressTypeID] AS [AddressTypeID], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.AddressType U
CREATE TRIGGER [Person].[dbr_audit_AddressType_U] ON [Person].[AddressType]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1701581100, N'[Person].[AddressType]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[AddressTypeID] AS [AddressTypeID], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[AddressTypeID] AS [AddressTypeID], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.AddressType D
CREATE TRIGGER [Person].[dbr_audit_AddressType_D] ON [Person].[AddressType]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1701581100, N'[Person].[AddressType]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[AddressTypeID] AS [AddressTypeID], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.AddressType
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_AddressType_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_AddressType_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_AddressType_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.BusinessEntity I
CREATE TRIGGER [Person].[dbr_audit_BusinessEntity_I] ON [Person].[BusinessEntity]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1909581841, N'[Person].[BusinessEntity]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.BusinessEntity U
CREATE TRIGGER [Person].[dbr_audit_BusinessEntity_U] ON [Person].[BusinessEntity]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1909581841, N'[Person].[BusinessEntity]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.BusinessEntity D
CREATE TRIGGER [Person].[dbr_audit_BusinessEntity_D] ON [Person].[BusinessEntity]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1909581841, N'[Person].[BusinessEntity]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.BusinessEntity
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntity_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntity_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntity_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.BusinessEntityAddress I
CREATE TRIGGER [Person].[dbr_audit_BusinessEntityAddress_I] ON [Person].[BusinessEntityAddress]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1957582012, N'[Person].[BusinessEntityAddress]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[AddressID] AS [AddressID], i.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[AddressID] AS [AddressID], i.[AddressTypeID] AS [AddressTypeID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.BusinessEntityAddress U
CREATE TRIGGER [Person].[dbr_audit_BusinessEntityAddress_U] ON [Person].[BusinessEntityAddress]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1957582012, N'[Person].[BusinessEntityAddress]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[AddressID] AS [AddressID], d.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[AddressID] AS [AddressID], d.[AddressTypeID] AS [AddressTypeID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[AddressID] AS [AddressID], i.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[AddressID] AS [AddressID], i.[AddressTypeID] AS [AddressTypeID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.BusinessEntityAddress D
CREATE TRIGGER [Person].[dbr_audit_BusinessEntityAddress_D] ON [Person].[BusinessEntityAddress]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1957582012, N'[Person].[BusinessEntityAddress]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[AddressID] AS [AddressID], d.[AddressTypeID] AS [AddressTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[AddressID] AS [AddressID], d.[AddressTypeID] AS [AddressTypeID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.BusinessEntityAddress
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntityAddress_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntityAddress_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntityAddress_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.BusinessEntityContact I
CREATE TRIGGER [Person].[dbr_audit_BusinessEntityContact_I] ON [Person].[BusinessEntityContact]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2005582183, N'[Person].[BusinessEntityContact]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PersonID] AS [PersonID], i.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PersonID] AS [PersonID], i.[ContactTypeID] AS [ContactTypeID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.BusinessEntityContact U
CREATE TRIGGER [Person].[dbr_audit_BusinessEntityContact_U] ON [Person].[BusinessEntityContact]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2005582183, N'[Person].[BusinessEntityContact]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PersonID] AS [PersonID], d.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PersonID] AS [PersonID], d.[ContactTypeID] AS [ContactTypeID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PersonID] AS [PersonID], i.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PersonID] AS [PersonID], i.[ContactTypeID] AS [ContactTypeID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.BusinessEntityContact D
CREATE TRIGGER [Person].[dbr_audit_BusinessEntityContact_D] ON [Person].[BusinessEntityContact]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2005582183, N'[Person].[BusinessEntityContact]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PersonID] AS [PersonID], d.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PersonID] AS [PersonID], d.[ContactTypeID] AS [ContactTypeID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.BusinessEntityContact
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntityContact_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntityContact_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_BusinessEntityContact_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.ContactType I
CREATE TRIGGER [Person].[dbr_audit_ContactType_I] ON [Person].[ContactType]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2053582354, N'[Person].[ContactType]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ContactTypeID] AS [ContactTypeID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.ContactType U
CREATE TRIGGER [Person].[dbr_audit_ContactType_U] ON [Person].[ContactType]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2053582354, N'[Person].[ContactType]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ContactTypeID] AS [ContactTypeID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ContactTypeID] AS [ContactTypeID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.ContactType D
CREATE TRIGGER [Person].[dbr_audit_ContactType_D] ON [Person].[ContactType]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2053582354, N'[Person].[ContactType]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ContactTypeID] AS [ContactTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ContactTypeID] AS [ContactTypeID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.ContactType
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_ContactType_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_ContactType_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_ContactType_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.CountryRegion I
CREATE TRIGGER [Person].[dbr_audit_CountryRegion_I] ON [Person].[CountryRegion]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2117582582, N'[Person].[CountryRegion]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CountryRegionCode] AS [CountryRegionCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CountryRegionCode] AS [CountryRegionCode], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.CountryRegion U
CREATE TRIGGER [Person].[dbr_audit_CountryRegion_U] ON [Person].[CountryRegion]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2117582582, N'[Person].[CountryRegion]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CountryRegionCode] AS [CountryRegionCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CountryRegionCode] AS [CountryRegionCode], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CountryRegionCode] AS [CountryRegionCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CountryRegionCode] AS [CountryRegionCode], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.CountryRegion D
CREATE TRIGGER [Person].[dbr_audit_CountryRegion_D] ON [Person].[CountryRegion]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2117582582, N'[Person].[CountryRegion]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CountryRegionCode] AS [CountryRegionCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CountryRegionCode] AS [CountryRegionCode], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.CountryRegion
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_CountryRegion_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_CountryRegion_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_CountryRegion_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.EmailAddress I
CREATE TRIGGER [Person].[dbr_audit_EmailAddress_I] ON [Person].[EmailAddress]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 322100188, N'[Person].[EmailAddress]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[EmailAddressID] AS [EmailAddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[EmailAddressID] AS [EmailAddressID], i.[EmailAddress] AS [EmailAddress], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.EmailAddress U
CREATE TRIGGER [Person].[dbr_audit_EmailAddress_U] ON [Person].[EmailAddress]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 322100188, N'[Person].[EmailAddress]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[EmailAddressID] AS [EmailAddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[EmailAddressID] AS [EmailAddressID], d.[EmailAddress] AS [EmailAddress], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[EmailAddressID] AS [EmailAddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[EmailAddressID] AS [EmailAddressID], i.[EmailAddress] AS [EmailAddress], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.EmailAddress D
CREATE TRIGGER [Person].[dbr_audit_EmailAddress_D] ON [Person].[EmailAddress]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 322100188, N'[Person].[EmailAddress]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[EmailAddressID] AS [EmailAddressID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[EmailAddressID] AS [EmailAddressID], d.[EmailAddress] AS [EmailAddress], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.EmailAddress
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_EmailAddress_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_EmailAddress_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_EmailAddress_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.Password I
CREATE TRIGGER [Person].[dbr_audit_Password_I] ON [Person].[Password]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 850102069, N'[Person].[Password]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PasswordHash] AS [PasswordHash], i.[PasswordSalt] AS [PasswordSalt], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.Password U
CREATE TRIGGER [Person].[dbr_audit_Password_U] ON [Person].[Password]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 850102069, N'[Person].[Password]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PasswordHash] AS [PasswordHash], d.[PasswordSalt] AS [PasswordSalt], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PasswordHash] AS [PasswordHash], i.[PasswordSalt] AS [PasswordSalt], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.Password D
CREATE TRIGGER [Person].[dbr_audit_Password_D] ON [Person].[Password]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 850102069, N'[Person].[Password]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PasswordHash] AS [PasswordHash], d.[PasswordSalt] AS [PasswordSalt], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.Password
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Password_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Password_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Password_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.Person I
CREATE TRIGGER [Person].[dbr_audit_Person_I] ON [Person].[Person]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 898102240, N'[Person].[Person]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PersonType] AS [PersonType], i.[NameStyle] AS [NameStyle], i.[Title] AS [Title], i.[FirstName] AS [FirstName], i.[MiddleName] AS [MiddleName], i.[LastName] AS [LastName], i.[Suffix] AS [Suffix], i.[EmailPromotion] AS [EmailPromotion], CONVERT(nvarchar(max), i.[AdditionalContactInfo]) AS [AdditionalContactInfo], CONVERT(nvarchar(max), i.[Demographics]) AS [Demographics], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.Person U
CREATE TRIGGER [Person].[dbr_audit_Person_U] ON [Person].[Person]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 898102240, N'[Person].[Person]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PersonType] AS [PersonType], d.[NameStyle] AS [NameStyle], d.[Title] AS [Title], d.[FirstName] AS [FirstName], d.[MiddleName] AS [MiddleName], d.[LastName] AS [LastName], d.[Suffix] AS [Suffix], d.[EmailPromotion] AS [EmailPromotion], CONVERT(nvarchar(max), d.[AdditionalContactInfo]) AS [AdditionalContactInfo], CONVERT(nvarchar(max), d.[Demographics]) AS [Demographics], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PersonType] AS [PersonType], i.[NameStyle] AS [NameStyle], i.[Title] AS [Title], i.[FirstName] AS [FirstName], i.[MiddleName] AS [MiddleName], i.[LastName] AS [LastName], i.[Suffix] AS [Suffix], i.[EmailPromotion] AS [EmailPromotion], CONVERT(nvarchar(max), i.[AdditionalContactInfo]) AS [AdditionalContactInfo], CONVERT(nvarchar(max), i.[Demographics]) AS [Demographics], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.Person D
CREATE TRIGGER [Person].[dbr_audit_Person_D] ON [Person].[Person]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 898102240, N'[Person].[Person]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PersonType] AS [PersonType], d.[NameStyle] AS [NameStyle], d.[Title] AS [Title], d.[FirstName] AS [FirstName], d.[MiddleName] AS [MiddleName], d.[LastName] AS [LastName], d.[Suffix] AS [Suffix], d.[EmailPromotion] AS [EmailPromotion], CONVERT(nvarchar(max), d.[AdditionalContactInfo]) AS [AdditionalContactInfo], CONVERT(nvarchar(max), d.[Demographics]) AS [Demographics], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.Person
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Person_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Person_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_Person_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.PersonPhone I
CREATE TRIGGER [Person].[dbr_audit_PersonPhone_I] ON [Person].[PersonPhone]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1042102753, N'[Person].[PersonPhone]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PhoneNumber] AS [PhoneNumber], i.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PhoneNumber] AS [PhoneNumber], i.[PhoneNumberTypeID] AS [PhoneNumberTypeID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.PersonPhone U
CREATE TRIGGER [Person].[dbr_audit_PersonPhone_U] ON [Person].[PersonPhone]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1042102753, N'[Person].[PersonPhone]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PhoneNumber] AS [PhoneNumber], d.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PhoneNumber] AS [PhoneNumber], d.[PhoneNumberTypeID] AS [PhoneNumberTypeID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PhoneNumber] AS [PhoneNumber], i.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[PhoneNumber] AS [PhoneNumber], i.[PhoneNumberTypeID] AS [PhoneNumberTypeID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.PersonPhone D
CREATE TRIGGER [Person].[dbr_audit_PersonPhone_D] ON [Person].[PersonPhone]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1042102753, N'[Person].[PersonPhone]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PhoneNumber] AS [PhoneNumber], d.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[PhoneNumber] AS [PhoneNumber], d.[PhoneNumberTypeID] AS [PhoneNumberTypeID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.PersonPhone
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_PersonPhone_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_PersonPhone_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_PersonPhone_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.PhoneNumberType I
CREATE TRIGGER [Person].[dbr_audit_PhoneNumberType_I] ON [Person].[PhoneNumberType]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1074102867, N'[Person].[PhoneNumberType]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[PhoneNumberTypeID] AS [PhoneNumberTypeID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.PhoneNumberType U
CREATE TRIGGER [Person].[dbr_audit_PhoneNumberType_U] ON [Person].[PhoneNumberType]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1074102867, N'[Person].[PhoneNumberType]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[PhoneNumberTypeID] AS [PhoneNumberTypeID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[PhoneNumberTypeID] AS [PhoneNumberTypeID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.PhoneNumberType D
CREATE TRIGGER [Person].[dbr_audit_PhoneNumberType_D] ON [Person].[PhoneNumberType]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1074102867, N'[Person].[PhoneNumberType]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[PhoneNumberTypeID] AS [PhoneNumberTypeID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[PhoneNumberTypeID] AS [PhoneNumberTypeID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.PhoneNumberType
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_PhoneNumberType_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_PhoneNumberType_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_PhoneNumberType_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Person.StateProvince I
CREATE TRIGGER [Person].[dbr_audit_StateProvince_I] ON [Person].[StateProvince]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1742629251, N'[Person].[StateProvince]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[StateProvinceID] AS [StateProvinceID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[StateProvinceID] AS [StateProvinceID], i.[StateProvinceCode] AS [StateProvinceCode], i.[CountryRegionCode] AS [CountryRegionCode], i.[IsOnlyStateProvinceFlag] AS [IsOnlyStateProvinceFlag], i.[Name] AS [Name], i.[TerritoryID] AS [TerritoryID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.StateProvince U
CREATE TRIGGER [Person].[dbr_audit_StateProvince_U] ON [Person].[StateProvince]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1742629251, N'[Person].[StateProvince]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[StateProvinceID] AS [StateProvinceID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[StateProvinceID] AS [StateProvinceID], d.[StateProvinceCode] AS [StateProvinceCode], d.[CountryRegionCode] AS [CountryRegionCode], d.[IsOnlyStateProvinceFlag] AS [IsOnlyStateProvinceFlag], d.[Name] AS [Name], d.[TerritoryID] AS [TerritoryID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[StateProvinceID] AS [StateProvinceID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[StateProvinceID] AS [StateProvinceID], i.[StateProvinceCode] AS [StateProvinceCode], i.[CountryRegionCode] AS [CountryRegionCode], i.[IsOnlyStateProvinceFlag] AS [IsOnlyStateProvinceFlag], i.[Name] AS [Name], i.[TerritoryID] AS [TerritoryID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Person.StateProvince D
CREATE TRIGGER [Person].[dbr_audit_StateProvince_D] ON [Person].[StateProvince]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1742629251, N'[Person].[StateProvince]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[StateProvinceID] AS [StateProvinceID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[StateProvinceID] AS [StateProvinceID], d.[StateProvinceCode] AS [StateProvinceCode], d.[CountryRegionCode] AS [CountryRegionCode], d.[IsOnlyStateProvinceFlag] AS [IsOnlyStateProvinceFlag], d.[Name] AS [Name], d.[TerritoryID] AS [TerritoryID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Person.StateProvince
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_StateProvince_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_StateProvince_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Person].[dbr_audit_StateProvince_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.BillOfMaterials I
CREATE TRIGGER [Production].[dbr_audit_BillOfMaterials_I] ON [Production].[BillOfMaterials]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1781581385, N'[Production].[BillOfMaterials]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BillOfMaterialsID] AS [BillOfMaterialsID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BillOfMaterialsID] AS [BillOfMaterialsID], i.[ProductAssemblyID] AS [ProductAssemblyID], i.[ComponentID] AS [ComponentID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[UnitMeasureCode] AS [UnitMeasureCode], i.[BOMLevel] AS [BOMLevel], i.[PerAssemblyQty] AS [PerAssemblyQty], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.BillOfMaterials U
CREATE TRIGGER [Production].[dbr_audit_BillOfMaterials_U] ON [Production].[BillOfMaterials]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1781581385, N'[Production].[BillOfMaterials]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BillOfMaterialsID] AS [BillOfMaterialsID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BillOfMaterialsID] AS [BillOfMaterialsID], d.[ProductAssemblyID] AS [ProductAssemblyID], d.[ComponentID] AS [ComponentID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[UnitMeasureCode] AS [UnitMeasureCode], d.[BOMLevel] AS [BOMLevel], d.[PerAssemblyQty] AS [PerAssemblyQty], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BillOfMaterialsID] AS [BillOfMaterialsID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BillOfMaterialsID] AS [BillOfMaterialsID], i.[ProductAssemblyID] AS [ProductAssemblyID], i.[ComponentID] AS [ComponentID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[UnitMeasureCode] AS [UnitMeasureCode], i.[BOMLevel] AS [BOMLevel], i.[PerAssemblyQty] AS [PerAssemblyQty], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.BillOfMaterials D
CREATE TRIGGER [Production].[dbr_audit_BillOfMaterials_D] ON [Production].[BillOfMaterials]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1781581385, N'[Production].[BillOfMaterials]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BillOfMaterialsID] AS [BillOfMaterialsID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BillOfMaterialsID] AS [BillOfMaterialsID], d.[ProductAssemblyID] AS [ProductAssemblyID], d.[ComponentID] AS [ComponentID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[UnitMeasureCode] AS [UnitMeasureCode], d.[BOMLevel] AS [BOMLevel], d.[PerAssemblyQty] AS [PerAssemblyQty], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.BillOfMaterials
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_BillOfMaterials_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_BillOfMaterials_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_BillOfMaterials_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.Culture I
CREATE TRIGGER [Production].[dbr_audit_Culture_I] ON [Production].[Culture]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 34099162, N'[Production].[Culture]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CultureID] AS [CultureID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Culture U
CREATE TRIGGER [Production].[dbr_audit_Culture_U] ON [Production].[Culture]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 34099162, N'[Production].[Culture]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CultureID] AS [CultureID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CultureID] AS [CultureID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Culture D
CREATE TRIGGER [Production].[dbr_audit_Culture_D] ON [Production].[Culture]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 34099162, N'[Production].[Culture]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CultureID] AS [CultureID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.Culture
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Culture_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Culture_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Culture_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.Document I
CREATE TRIGGER [Production].[dbr_audit_Document_I] ON [Production].[Document]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 210099789, N'[Production].[Document]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[DocumentNode].ToString() AS [DocumentNode], i.[DocumentLevel] AS [DocumentLevel], i.[Title] AS [Title], i.[Owner] AS [Owner], i.[FolderFlag] AS [FolderFlag], i.[FileName] AS [FileName], i.[FileExtension] AS [FileExtension], i.[Revision] AS [Revision], i.[ChangeNumber] AS [ChangeNumber], i.[Status] AS [Status], i.[DocumentSummary] AS [DocumentSummary], i.[Document] AS [Document], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Document U
CREATE TRIGGER [Production].[dbr_audit_Document_U] ON [Production].[Document]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 210099789, N'[Production].[Document]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[DocumentNode].ToString() AS [DocumentNode], d.[DocumentLevel] AS [DocumentLevel], d.[Title] AS [Title], d.[Owner] AS [Owner], d.[FolderFlag] AS [FolderFlag], d.[FileName] AS [FileName], d.[FileExtension] AS [FileExtension], d.[Revision] AS [Revision], d.[ChangeNumber] AS [ChangeNumber], d.[Status] AS [Status], d.[DocumentSummary] AS [DocumentSummary], d.[Document] AS [Document], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[DocumentNode].ToString() AS [DocumentNode], i.[DocumentLevel] AS [DocumentLevel], i.[Title] AS [Title], i.[Owner] AS [Owner], i.[FolderFlag] AS [FolderFlag], i.[FileName] AS [FileName], i.[FileExtension] AS [FileExtension], i.[Revision] AS [Revision], i.[ChangeNumber] AS [ChangeNumber], i.[Status] AS [Status], i.[DocumentSummary] AS [DocumentSummary], i.[Document] AS [Document], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Document D
CREATE TRIGGER [Production].[dbr_audit_Document_D] ON [Production].[Document]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 210099789, N'[Production].[Document]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[DocumentNode].ToString() AS [DocumentNode], d.[DocumentLevel] AS [DocumentLevel], d.[Title] AS [Title], d.[Owner] AS [Owner], d.[FolderFlag] AS [FolderFlag], d.[FileName] AS [FileName], d.[FileExtension] AS [FileExtension], d.[Revision] AS [Revision], d.[ChangeNumber] AS [ChangeNumber], d.[Status] AS [Status], d.[DocumentSummary] AS [DocumentSummary], d.[Document] AS [Document], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.Document
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Document_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Document_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Document_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.Illustration I
CREATE TRIGGER [Production].[dbr_audit_Illustration_I] ON [Production].[Illustration]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 690101499, N'[Production].[Illustration]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[IllustrationID] AS [IllustrationID], CONVERT(nvarchar(max), i.[Diagram]) AS [Diagram], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Illustration U
CREATE TRIGGER [Production].[dbr_audit_Illustration_U] ON [Production].[Illustration]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 690101499, N'[Production].[Illustration]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[IllustrationID] AS [IllustrationID], CONVERT(nvarchar(max), d.[Diagram]) AS [Diagram], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[IllustrationID] AS [IllustrationID], CONVERT(nvarchar(max), i.[Diagram]) AS [Diagram], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Illustration D
CREATE TRIGGER [Production].[dbr_audit_Illustration_D] ON [Production].[Illustration]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 690101499, N'[Production].[Illustration]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[IllustrationID] AS [IllustrationID], CONVERT(nvarchar(max), d.[Diagram]) AS [Diagram], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.Illustration
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Illustration_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Illustration_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Illustration_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.Location I
CREATE TRIGGER [Production].[dbr_audit_Location_I] ON [Production].[Location]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 754101727, N'[Production].[Location]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[LocationID] AS [LocationID], i.[Name] AS [Name], i.[CostRate] AS [CostRate], i.[Availability] AS [Availability], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Location U
CREATE TRIGGER [Production].[dbr_audit_Location_U] ON [Production].[Location]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 754101727, N'[Production].[Location]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[LocationID] AS [LocationID], d.[Name] AS [Name], d.[CostRate] AS [CostRate], d.[Availability] AS [Availability], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[LocationID] AS [LocationID], i.[Name] AS [Name], i.[CostRate] AS [CostRate], i.[Availability] AS [Availability], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Location D
CREATE TRIGGER [Production].[dbr_audit_Location_D] ON [Production].[Location]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 754101727, N'[Production].[Location]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[LocationID] AS [LocationID], d.[Name] AS [Name], d.[CostRate] AS [CostRate], d.[Availability] AS [Availability], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.Location
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Location_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Location_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Location_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.Product I
CREATE TRIGGER [Production].[dbr_audit_Product_I] ON [Production].[Product]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1106102981, N'[Production].[Product]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[Name] AS [Name], i.[ProductNumber] AS [ProductNumber], i.[MakeFlag] AS [MakeFlag], i.[FinishedGoodsFlag] AS [FinishedGoodsFlag], i.[Color] AS [Color], i.[SafetyStockLevel] AS [SafetyStockLevel], i.[ReorderPoint] AS [ReorderPoint], i.[StandardCost] AS [StandardCost], i.[ListPrice] AS [ListPrice], i.[Size] AS [Size], i.[SizeUnitMeasureCode] AS [SizeUnitMeasureCode], i.[WeightUnitMeasureCode] AS [WeightUnitMeasureCode], i.[Weight] AS [Weight], i.[DaysToManufacture] AS [DaysToManufacture], i.[ProductLine] AS [ProductLine], i.[Class] AS [Class], i.[Style] AS [Style], i.[ProductSubcategoryID] AS [ProductSubcategoryID], i.[ProductModelID] AS [ProductModelID], i.[SellStartDate] AS [SellStartDate], i.[SellEndDate] AS [SellEndDate], i.[DiscontinuedDate] AS [DiscontinuedDate], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Product U
CREATE TRIGGER [Production].[dbr_audit_Product_U] ON [Production].[Product]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1106102981, N'[Production].[Product]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[Name] AS [Name], d.[ProductNumber] AS [ProductNumber], d.[MakeFlag] AS [MakeFlag], d.[FinishedGoodsFlag] AS [FinishedGoodsFlag], d.[Color] AS [Color], d.[SafetyStockLevel] AS [SafetyStockLevel], d.[ReorderPoint] AS [ReorderPoint], d.[StandardCost] AS [StandardCost], d.[ListPrice] AS [ListPrice], d.[Size] AS [Size], d.[SizeUnitMeasureCode] AS [SizeUnitMeasureCode], d.[WeightUnitMeasureCode] AS [WeightUnitMeasureCode], d.[Weight] AS [Weight], d.[DaysToManufacture] AS [DaysToManufacture], d.[ProductLine] AS [ProductLine], d.[Class] AS [Class], d.[Style] AS [Style], d.[ProductSubcategoryID] AS [ProductSubcategoryID], d.[ProductModelID] AS [ProductModelID], d.[SellStartDate] AS [SellStartDate], d.[SellEndDate] AS [SellEndDate], d.[DiscontinuedDate] AS [DiscontinuedDate], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[Name] AS [Name], i.[ProductNumber] AS [ProductNumber], i.[MakeFlag] AS [MakeFlag], i.[FinishedGoodsFlag] AS [FinishedGoodsFlag], i.[Color] AS [Color], i.[SafetyStockLevel] AS [SafetyStockLevel], i.[ReorderPoint] AS [ReorderPoint], i.[StandardCost] AS [StandardCost], i.[ListPrice] AS [ListPrice], i.[Size] AS [Size], i.[SizeUnitMeasureCode] AS [SizeUnitMeasureCode], i.[WeightUnitMeasureCode] AS [WeightUnitMeasureCode], i.[Weight] AS [Weight], i.[DaysToManufacture] AS [DaysToManufacture], i.[ProductLine] AS [ProductLine], i.[Class] AS [Class], i.[Style] AS [Style], i.[ProductSubcategoryID] AS [ProductSubcategoryID], i.[ProductModelID] AS [ProductModelID], i.[SellStartDate] AS [SellStartDate], i.[SellEndDate] AS [SellEndDate], i.[DiscontinuedDate] AS [DiscontinuedDate], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.Product D
CREATE TRIGGER [Production].[dbr_audit_Product_D] ON [Production].[Product]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1106102981, N'[Production].[Product]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[Name] AS [Name], d.[ProductNumber] AS [ProductNumber], d.[MakeFlag] AS [MakeFlag], d.[FinishedGoodsFlag] AS [FinishedGoodsFlag], d.[Color] AS [Color], d.[SafetyStockLevel] AS [SafetyStockLevel], d.[ReorderPoint] AS [ReorderPoint], d.[StandardCost] AS [StandardCost], d.[ListPrice] AS [ListPrice], d.[Size] AS [Size], d.[SizeUnitMeasureCode] AS [SizeUnitMeasureCode], d.[WeightUnitMeasureCode] AS [WeightUnitMeasureCode], d.[Weight] AS [Weight], d.[DaysToManufacture] AS [DaysToManufacture], d.[ProductLine] AS [ProductLine], d.[Class] AS [Class], d.[Style] AS [Style], d.[ProductSubcategoryID] AS [ProductSubcategoryID], d.[ProductModelID] AS [ProductModelID], d.[SellStartDate] AS [SellStartDate], d.[SellEndDate] AS [SellEndDate], d.[DiscontinuedDate] AS [DiscontinuedDate], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.Product
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Product_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Product_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_Product_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductCategory I
CREATE TRIGGER [Production].[dbr_audit_ProductCategory_I] ON [Production].[ProductCategory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1346103836, N'[Production].[ProductCategory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductCategoryID] AS [ProductCategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductCategoryID] AS [ProductCategoryID], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductCategory U
CREATE TRIGGER [Production].[dbr_audit_ProductCategory_U] ON [Production].[ProductCategory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1346103836, N'[Production].[ProductCategory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductCategoryID] AS [ProductCategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductCategoryID] AS [ProductCategoryID], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductCategoryID] AS [ProductCategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductCategoryID] AS [ProductCategoryID], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductCategory D
CREATE TRIGGER [Production].[dbr_audit_ProductCategory_D] ON [Production].[ProductCategory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1346103836, N'[Production].[ProductCategory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductCategoryID] AS [ProductCategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductCategoryID] AS [ProductCategoryID], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductCategory
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductCategory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductCategory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductCategory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductCostHistory I
CREATE TRIGGER [Production].[dbr_audit_ProductCostHistory_I] ON [Production].[ProductCostHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1394104007, N'[Production].[ProductCostHistory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[StandardCost] AS [StandardCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductCostHistory U
CREATE TRIGGER [Production].[dbr_audit_ProductCostHistory_U] ON [Production].[ProductCostHistory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1394104007, N'[Production].[ProductCostHistory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[StandardCost] AS [StandardCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[StandardCost] AS [StandardCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductCostHistory D
CREATE TRIGGER [Production].[dbr_audit_ProductCostHistory_D] ON [Production].[ProductCostHistory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1394104007, N'[Production].[ProductCostHistory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[StandardCost] AS [StandardCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductCostHistory
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductCostHistory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductCostHistory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductCostHistory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductDescription I
CREATE TRIGGER [Production].[dbr_audit_ProductDescription_I] ON [Production].[ProductDescription]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1458104235, N'[Production].[ProductDescription]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductDescriptionID] AS [ProductDescriptionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductDescriptionID] AS [ProductDescriptionID], i.[Description] AS [Description], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductDescription U
CREATE TRIGGER [Production].[dbr_audit_ProductDescription_U] ON [Production].[ProductDescription]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1458104235, N'[Production].[ProductDescription]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductDescriptionID] AS [ProductDescriptionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductDescriptionID] AS [ProductDescriptionID], d.[Description] AS [Description], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductDescriptionID] AS [ProductDescriptionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductDescriptionID] AS [ProductDescriptionID], i.[Description] AS [Description], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductDescription D
CREATE TRIGGER [Production].[dbr_audit_ProductDescription_D] ON [Production].[ProductDescription]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1458104235, N'[Production].[ProductDescription]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductDescriptionID] AS [ProductDescriptionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductDescriptionID] AS [ProductDescriptionID], d.[Description] AS [Description], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductDescription
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductDescription_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductDescription_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductDescription_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductDocument I
CREATE TRIGGER [Production].[dbr_audit_ProductDocument_I] ON [Production].[ProductDocument]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1506104406, N'[Production].[ProductDocument]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[DocumentNode].ToString() AS [DocumentNode], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductDocument U
CREATE TRIGGER [Production].[dbr_audit_ProductDocument_U] ON [Production].[ProductDocument]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1506104406, N'[Production].[ProductDocument]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[DocumentNode].ToString() AS [DocumentNode], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[DocumentNode].ToString() AS [DocumentNode], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductDocument D
CREATE TRIGGER [Production].[dbr_audit_ProductDocument_D] ON [Production].[ProductDocument]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1506104406, N'[Production].[ProductDocument]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[DocumentNode].ToString() AS [DocumentNode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[DocumentNode].ToString() AS [DocumentNode], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductDocument
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductDocument_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductDocument_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductDocument_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductInventory I
CREATE TRIGGER [Production].[dbr_audit_ProductInventory_I] ON [Production].[ProductInventory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1538104520, N'[Production].[ProductInventory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[LocationID] AS [LocationID], i.[Shelf] AS [Shelf], i.[Bin] AS [Bin], i.[Quantity] AS [Quantity], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductInventory U
CREATE TRIGGER [Production].[dbr_audit_ProductInventory_U] ON [Production].[ProductInventory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1538104520, N'[Production].[ProductInventory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[LocationID] AS [LocationID], d.[Shelf] AS [Shelf], d.[Bin] AS [Bin], d.[Quantity] AS [Quantity], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[LocationID] AS [LocationID], i.[Shelf] AS [Shelf], i.[Bin] AS [Bin], i.[Quantity] AS [Quantity], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductInventory D
CREATE TRIGGER [Production].[dbr_audit_ProductInventory_D] ON [Production].[ProductInventory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1538104520, N'[Production].[ProductInventory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[LocationID] AS [LocationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[LocationID] AS [LocationID], d.[Shelf] AS [Shelf], d.[Bin] AS [Bin], d.[Quantity] AS [Quantity], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductInventory
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductInventory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductInventory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductInventory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductListPriceHistory I
CREATE TRIGGER [Production].[dbr_audit_ProductListPriceHistory_I] ON [Production].[ProductListPriceHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1634104862, N'[Production].[ProductListPriceHistory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[ListPrice] AS [ListPrice], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductListPriceHistory U
CREATE TRIGGER [Production].[dbr_audit_ProductListPriceHistory_U] ON [Production].[ProductListPriceHistory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1634104862, N'[Production].[ProductListPriceHistory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[ListPrice] AS [ListPrice], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[ListPrice] AS [ListPrice], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductListPriceHistory D
CREATE TRIGGER [Production].[dbr_audit_ProductListPriceHistory_D] ON [Production].[ProductListPriceHistory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1634104862, N'[Production].[ProductListPriceHistory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[ListPrice] AS [ListPrice], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductListPriceHistory
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductListPriceHistory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductListPriceHistory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductListPriceHistory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductModel I
CREATE TRIGGER [Production].[dbr_audit_ProductModel_I] ON [Production].[ProductModel]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1698105090, N'[Production].[ProductModel]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductModelID] AS [ProductModelID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductModelID] AS [ProductModelID], i.[Name] AS [Name], CONVERT(nvarchar(max), i.[CatalogDescription]) AS [CatalogDescription], CONVERT(nvarchar(max), i.[Instructions]) AS [Instructions], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductModel U
CREATE TRIGGER [Production].[dbr_audit_ProductModel_U] ON [Production].[ProductModel]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1698105090, N'[Production].[ProductModel]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductModelID] AS [ProductModelID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductModelID] AS [ProductModelID], d.[Name] AS [Name], CONVERT(nvarchar(max), d.[CatalogDescription]) AS [CatalogDescription], CONVERT(nvarchar(max), d.[Instructions]) AS [Instructions], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductModelID] AS [ProductModelID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductModelID] AS [ProductModelID], i.[Name] AS [Name], CONVERT(nvarchar(max), i.[CatalogDescription]) AS [CatalogDescription], CONVERT(nvarchar(max), i.[Instructions]) AS [Instructions], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductModel D
CREATE TRIGGER [Production].[dbr_audit_ProductModel_D] ON [Production].[ProductModel]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1698105090, N'[Production].[ProductModel]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductModelID] AS [ProductModelID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductModelID] AS [ProductModelID], d.[Name] AS [Name], CONVERT(nvarchar(max), d.[CatalogDescription]) AS [CatalogDescription], CONVERT(nvarchar(max), d.[Instructions]) AS [Instructions], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductModel
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModel_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModel_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModel_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductModelIllustration I
CREATE TRIGGER [Production].[dbr_audit_ProductModelIllustration_I] ON [Production].[ProductModelIllustration]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1746105261, N'[Production].[ProductModelIllustration]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductModelID] AS [ProductModelID], i.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductModelID] AS [ProductModelID], i.[IllustrationID] AS [IllustrationID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductModelIllustration U
CREATE TRIGGER [Production].[dbr_audit_ProductModelIllustration_U] ON [Production].[ProductModelIllustration]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1746105261, N'[Production].[ProductModelIllustration]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductModelID] AS [ProductModelID], d.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductModelID] AS [ProductModelID], d.[IllustrationID] AS [IllustrationID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductModelID] AS [ProductModelID], i.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductModelID] AS [ProductModelID], i.[IllustrationID] AS [IllustrationID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductModelIllustration D
CREATE TRIGGER [Production].[dbr_audit_ProductModelIllustration_D] ON [Production].[ProductModelIllustration]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1746105261, N'[Production].[ProductModelIllustration]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductModelID] AS [ProductModelID], d.[IllustrationID] AS [IllustrationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductModelID] AS [ProductModelID], d.[IllustrationID] AS [IllustrationID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductModelIllustration
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModelIllustration_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModelIllustration_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModelIllustration_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductModelProductDescriptionCulture I
CREATE TRIGGER [Production].[dbr_audit_ProductModelProductDescriptionCulture_I] ON [Production].[ProductModelProductDescriptionCulture]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1778105375, N'[Production].[ProductModelProductDescriptionCulture]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductModelID] AS [ProductModelID], i.[ProductDescriptionID] AS [ProductDescriptionID], i.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductModelID] AS [ProductModelID], i.[ProductDescriptionID] AS [ProductDescriptionID], i.[CultureID] AS [CultureID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductModelProductDescriptionCulture U
CREATE TRIGGER [Production].[dbr_audit_ProductModelProductDescriptionCulture_U] ON [Production].[ProductModelProductDescriptionCulture]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1778105375, N'[Production].[ProductModelProductDescriptionCulture]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductModelID] AS [ProductModelID], d.[ProductDescriptionID] AS [ProductDescriptionID], d.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductModelID] AS [ProductModelID], d.[ProductDescriptionID] AS [ProductDescriptionID], d.[CultureID] AS [CultureID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductModelID] AS [ProductModelID], i.[ProductDescriptionID] AS [ProductDescriptionID], i.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductModelID] AS [ProductModelID], i.[ProductDescriptionID] AS [ProductDescriptionID], i.[CultureID] AS [CultureID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductModelProductDescriptionCulture D
CREATE TRIGGER [Production].[dbr_audit_ProductModelProductDescriptionCulture_D] ON [Production].[ProductModelProductDescriptionCulture]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1778105375, N'[Production].[ProductModelProductDescriptionCulture]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductModelID] AS [ProductModelID], d.[ProductDescriptionID] AS [ProductDescriptionID], d.[CultureID] AS [CultureID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductModelID] AS [ProductModelID], d.[ProductDescriptionID] AS [ProductDescriptionID], d.[CultureID] AS [CultureID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductModelProductDescriptionCulture
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModelProductDescriptionCulture_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModelProductDescriptionCulture_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductModelProductDescriptionCulture_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductPhoto I
CREATE TRIGGER [Production].[dbr_audit_ProductPhoto_I] ON [Production].[ProductPhoto]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1810105489, N'[Production].[ProductPhoto]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductPhotoID] AS [ProductPhotoID], i.[ThumbNailPhoto] AS [ThumbNailPhoto], i.[ThumbnailPhotoFileName] AS [ThumbnailPhotoFileName], i.[LargePhoto] AS [LargePhoto], i.[LargePhotoFileName] AS [LargePhotoFileName], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductPhoto U
CREATE TRIGGER [Production].[dbr_audit_ProductPhoto_U] ON [Production].[ProductPhoto]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1810105489, N'[Production].[ProductPhoto]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductPhotoID] AS [ProductPhotoID], d.[ThumbNailPhoto] AS [ThumbNailPhoto], d.[ThumbnailPhotoFileName] AS [ThumbnailPhotoFileName], d.[LargePhoto] AS [LargePhoto], d.[LargePhotoFileName] AS [LargePhotoFileName], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductPhotoID] AS [ProductPhotoID], i.[ThumbNailPhoto] AS [ThumbNailPhoto], i.[ThumbnailPhotoFileName] AS [ThumbnailPhotoFileName], i.[LargePhoto] AS [LargePhoto], i.[LargePhotoFileName] AS [LargePhotoFileName], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductPhoto D
CREATE TRIGGER [Production].[dbr_audit_ProductPhoto_D] ON [Production].[ProductPhoto]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1810105489, N'[Production].[ProductPhoto]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductPhotoID] AS [ProductPhotoID], d.[ThumbNailPhoto] AS [ThumbNailPhoto], d.[ThumbnailPhotoFileName] AS [ThumbnailPhotoFileName], d.[LargePhoto] AS [LargePhoto], d.[LargePhotoFileName] AS [LargePhotoFileName], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductPhoto
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductPhoto_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductPhoto_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductPhoto_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductProductPhoto I
CREATE TRIGGER [Production].[dbr_audit_ProductProductPhoto_I] ON [Production].[ProductProductPhoto]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1842105603, N'[Production].[ProductProductPhoto]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[ProductPhotoID] AS [ProductPhotoID], i.[Primary] AS [Primary], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductProductPhoto U
CREATE TRIGGER [Production].[dbr_audit_ProductProductPhoto_U] ON [Production].[ProductProductPhoto]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1842105603, N'[Production].[ProductProductPhoto]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[ProductPhotoID] AS [ProductPhotoID], d.[Primary] AS [Primary], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[ProductPhotoID] AS [ProductPhotoID], i.[Primary] AS [Primary], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductProductPhoto D
CREATE TRIGGER [Production].[dbr_audit_ProductProductPhoto_D] ON [Production].[ProductProductPhoto]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1842105603, N'[Production].[ProductProductPhoto]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[ProductPhotoID] AS [ProductPhotoID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[ProductPhotoID] AS [ProductPhotoID], d.[Primary] AS [Primary], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductProductPhoto
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductProductPhoto_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductProductPhoto_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductProductPhoto_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductReview I
CREATE TRIGGER [Production].[dbr_audit_ProductReview_I] ON [Production].[ProductReview]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1890105774, N'[Production].[ProductReview]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductReviewID] AS [ProductReviewID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductReviewID] AS [ProductReviewID], i.[ProductID] AS [ProductID], i.[ReviewerName] AS [ReviewerName], i.[ReviewDate] AS [ReviewDate], i.[EmailAddress] AS [EmailAddress], i.[Rating] AS [Rating], i.[Comments] AS [Comments], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductReview U
CREATE TRIGGER [Production].[dbr_audit_ProductReview_U] ON [Production].[ProductReview]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1890105774, N'[Production].[ProductReview]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductReviewID] AS [ProductReviewID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductReviewID] AS [ProductReviewID], d.[ProductID] AS [ProductID], d.[ReviewerName] AS [ReviewerName], d.[ReviewDate] AS [ReviewDate], d.[EmailAddress] AS [EmailAddress], d.[Rating] AS [Rating], d.[Comments] AS [Comments], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductReviewID] AS [ProductReviewID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductReviewID] AS [ProductReviewID], i.[ProductID] AS [ProductID], i.[ReviewerName] AS [ReviewerName], i.[ReviewDate] AS [ReviewDate], i.[EmailAddress] AS [EmailAddress], i.[Rating] AS [Rating], i.[Comments] AS [Comments], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductReview D
CREATE TRIGGER [Production].[dbr_audit_ProductReview_D] ON [Production].[ProductReview]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1890105774, N'[Production].[ProductReview]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductReviewID] AS [ProductReviewID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductReviewID] AS [ProductReviewID], d.[ProductID] AS [ProductID], d.[ReviewerName] AS [ReviewerName], d.[ReviewDate] AS [ReviewDate], d.[EmailAddress] AS [EmailAddress], d.[Rating] AS [Rating], d.[Comments] AS [Comments], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductReview
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductReview_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductReview_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductReview_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ProductSubcategory I
CREATE TRIGGER [Production].[dbr_audit_ProductSubcategory_I] ON [Production].[ProductSubcategory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1954106002, N'[Production].[ProductSubcategory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductSubcategoryID] AS [ProductSubcategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductSubcategoryID] AS [ProductSubcategoryID], i.[ProductCategoryID] AS [ProductCategoryID], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductSubcategory U
CREATE TRIGGER [Production].[dbr_audit_ProductSubcategory_U] ON [Production].[ProductSubcategory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1954106002, N'[Production].[ProductSubcategory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductSubcategoryID] AS [ProductSubcategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductSubcategoryID] AS [ProductSubcategoryID], d.[ProductCategoryID] AS [ProductCategoryID], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductSubcategoryID] AS [ProductSubcategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductSubcategoryID] AS [ProductSubcategoryID], i.[ProductCategoryID] AS [ProductCategoryID], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ProductSubcategory D
CREATE TRIGGER [Production].[dbr_audit_ProductSubcategory_D] ON [Production].[ProductSubcategory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1954106002, N'[Production].[ProductSubcategory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductSubcategoryID] AS [ProductSubcategoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductSubcategoryID] AS [ProductSubcategoryID], d.[ProductCategoryID] AS [ProductCategoryID], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ProductSubcategory
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductSubcategory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductSubcategory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ProductSubcategory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.ScrapReason I
CREATE TRIGGER [Production].[dbr_audit_ScrapReason_I] ON [Production].[ScrapReason]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1294627655, N'[Production].[ScrapReason]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ScrapReasonID] AS [ScrapReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ScrapReasonID] AS [ScrapReasonID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ScrapReason U
CREATE TRIGGER [Production].[dbr_audit_ScrapReason_U] ON [Production].[ScrapReason]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1294627655, N'[Production].[ScrapReason]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ScrapReasonID] AS [ScrapReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ScrapReasonID] AS [ScrapReasonID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ScrapReasonID] AS [ScrapReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ScrapReasonID] AS [ScrapReasonID], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.ScrapReason D
CREATE TRIGGER [Production].[dbr_audit_ScrapReason_D] ON [Production].[ScrapReason]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1294627655, N'[Production].[ScrapReason]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ScrapReasonID] AS [ScrapReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ScrapReasonID] AS [ScrapReasonID], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.ScrapReason
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ScrapReason_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ScrapReason_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_ScrapReason_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.TransactionHistory I
CREATE TRIGGER [Production].[dbr_audit_TransactionHistory_I] ON [Production].[TransactionHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1854629650, N'[Production].[TransactionHistory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[TransactionID] AS [TransactionID], i.[ProductID] AS [ProductID], i.[ReferenceOrderID] AS [ReferenceOrderID], i.[ReferenceOrderLineID] AS [ReferenceOrderLineID], i.[TransactionDate] AS [TransactionDate], i.[TransactionType] AS [TransactionType], i.[Quantity] AS [Quantity], i.[ActualCost] AS [ActualCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.TransactionHistory U
CREATE TRIGGER [Production].[dbr_audit_TransactionHistory_U] ON [Production].[TransactionHistory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1854629650, N'[Production].[TransactionHistory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[TransactionID] AS [TransactionID], d.[ProductID] AS [ProductID], d.[ReferenceOrderID] AS [ReferenceOrderID], d.[ReferenceOrderLineID] AS [ReferenceOrderLineID], d.[TransactionDate] AS [TransactionDate], d.[TransactionType] AS [TransactionType], d.[Quantity] AS [Quantity], d.[ActualCost] AS [ActualCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[TransactionID] AS [TransactionID], i.[ProductID] AS [ProductID], i.[ReferenceOrderID] AS [ReferenceOrderID], i.[ReferenceOrderLineID] AS [ReferenceOrderLineID], i.[TransactionDate] AS [TransactionDate], i.[TransactionType] AS [TransactionType], i.[Quantity] AS [Quantity], i.[ActualCost] AS [ActualCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.TransactionHistory D
CREATE TRIGGER [Production].[dbr_audit_TransactionHistory_D] ON [Production].[TransactionHistory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1854629650, N'[Production].[TransactionHistory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[TransactionID] AS [TransactionID], d.[ProductID] AS [ProductID], d.[ReferenceOrderID] AS [ReferenceOrderID], d.[ReferenceOrderLineID] AS [ReferenceOrderLineID], d.[TransactionDate] AS [TransactionDate], d.[TransactionType] AS [TransactionType], d.[Quantity] AS [Quantity], d.[ActualCost] AS [ActualCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.TransactionHistory
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_TransactionHistory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_TransactionHistory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_TransactionHistory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.TransactionHistoryArchive I
CREATE TRIGGER [Production].[dbr_audit_TransactionHistoryArchive_I] ON [Production].[TransactionHistoryArchive]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1934629935, N'[Production].[TransactionHistoryArchive]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[TransactionID] AS [TransactionID], i.[ProductID] AS [ProductID], i.[ReferenceOrderID] AS [ReferenceOrderID], i.[ReferenceOrderLineID] AS [ReferenceOrderLineID], i.[TransactionDate] AS [TransactionDate], i.[TransactionType] AS [TransactionType], i.[Quantity] AS [Quantity], i.[ActualCost] AS [ActualCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.TransactionHistoryArchive U
CREATE TRIGGER [Production].[dbr_audit_TransactionHistoryArchive_U] ON [Production].[TransactionHistoryArchive]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1934629935, N'[Production].[TransactionHistoryArchive]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[TransactionID] AS [TransactionID], d.[ProductID] AS [ProductID], d.[ReferenceOrderID] AS [ReferenceOrderID], d.[ReferenceOrderLineID] AS [ReferenceOrderLineID], d.[TransactionDate] AS [TransactionDate], d.[TransactionType] AS [TransactionType], d.[Quantity] AS [Quantity], d.[ActualCost] AS [ActualCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[TransactionID] AS [TransactionID], i.[ProductID] AS [ProductID], i.[ReferenceOrderID] AS [ReferenceOrderID], i.[ReferenceOrderLineID] AS [ReferenceOrderLineID], i.[TransactionDate] AS [TransactionDate], i.[TransactionType] AS [TransactionType], i.[Quantity] AS [Quantity], i.[ActualCost] AS [ActualCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.TransactionHistoryArchive D
CREATE TRIGGER [Production].[dbr_audit_TransactionHistoryArchive_D] ON [Production].[TransactionHistoryArchive]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1934629935, N'[Production].[TransactionHistoryArchive]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[TransactionID] AS [TransactionID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[TransactionID] AS [TransactionID], d.[ProductID] AS [ProductID], d.[ReferenceOrderID] AS [ReferenceOrderID], d.[ReferenceOrderLineID] AS [ReferenceOrderLineID], d.[TransactionDate] AS [TransactionDate], d.[TransactionType] AS [TransactionType], d.[Quantity] AS [Quantity], d.[ActualCost] AS [ActualCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.TransactionHistoryArchive
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_TransactionHistoryArchive_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_TransactionHistoryArchive_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_TransactionHistoryArchive_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.UnitMeasure I
CREATE TRIGGER [Production].[dbr_audit_UnitMeasure_I] ON [Production].[UnitMeasure]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2014630220, N'[Production].[UnitMeasure]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[UnitMeasureCode] AS [UnitMeasureCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[UnitMeasureCode] AS [UnitMeasureCode], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.UnitMeasure U
CREATE TRIGGER [Production].[dbr_audit_UnitMeasure_U] ON [Production].[UnitMeasure]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2014630220, N'[Production].[UnitMeasure]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[UnitMeasureCode] AS [UnitMeasureCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[UnitMeasureCode] AS [UnitMeasureCode], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[UnitMeasureCode] AS [UnitMeasureCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[UnitMeasureCode] AS [UnitMeasureCode], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.UnitMeasure D
CREATE TRIGGER [Production].[dbr_audit_UnitMeasure_D] ON [Production].[UnitMeasure]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2014630220, N'[Production].[UnitMeasure]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[UnitMeasureCode] AS [UnitMeasureCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[UnitMeasureCode] AS [UnitMeasureCode], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.UnitMeasure
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_UnitMeasure_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_UnitMeasure_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_UnitMeasure_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.WorkOrder I
CREATE TRIGGER [Production].[dbr_audit_WorkOrder_I] ON [Production].[WorkOrder]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2126630619, N'[Production].[WorkOrder]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[WorkOrderID] AS [WorkOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[WorkOrderID] AS [WorkOrderID], i.[ProductID] AS [ProductID], i.[OrderQty] AS [OrderQty], i.[StockedQty] AS [StockedQty], i.[ScrappedQty] AS [ScrappedQty], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[DueDate] AS [DueDate], i.[ScrapReasonID] AS [ScrapReasonID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.WorkOrder U
CREATE TRIGGER [Production].[dbr_audit_WorkOrder_U] ON [Production].[WorkOrder]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2126630619, N'[Production].[WorkOrder]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[WorkOrderID] AS [WorkOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[WorkOrderID] AS [WorkOrderID], d.[ProductID] AS [ProductID], d.[OrderQty] AS [OrderQty], d.[StockedQty] AS [StockedQty], d.[ScrappedQty] AS [ScrappedQty], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[DueDate] AS [DueDate], d.[ScrapReasonID] AS [ScrapReasonID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[WorkOrderID] AS [WorkOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[WorkOrderID] AS [WorkOrderID], i.[ProductID] AS [ProductID], i.[OrderQty] AS [OrderQty], i.[StockedQty] AS [StockedQty], i.[ScrappedQty] AS [ScrappedQty], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[DueDate] AS [DueDate], i.[ScrapReasonID] AS [ScrapReasonID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.WorkOrder D
CREATE TRIGGER [Production].[dbr_audit_WorkOrder_D] ON [Production].[WorkOrder]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2126630619, N'[Production].[WorkOrder]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[WorkOrderID] AS [WorkOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[WorkOrderID] AS [WorkOrderID], d.[ProductID] AS [ProductID], d.[OrderQty] AS [OrderQty], d.[StockedQty] AS [StockedQty], d.[ScrappedQty] AS [ScrappedQty], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[DueDate] AS [DueDate], d.[ScrapReasonID] AS [ScrapReasonID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.WorkOrder
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_WorkOrder_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_WorkOrder_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_WorkOrder_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Production.WorkOrderRouting I
CREATE TRIGGER [Production].[dbr_audit_WorkOrderRouting_I] ON [Production].[WorkOrderRouting]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 59147256, N'[Production].[WorkOrderRouting]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[WorkOrderID] AS [WorkOrderID], i.[ProductID] AS [ProductID], i.[OperationSequence] AS [OperationSequence] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[WorkOrderID] AS [WorkOrderID], i.[ProductID] AS [ProductID], i.[OperationSequence] AS [OperationSequence], i.[LocationID] AS [LocationID], i.[ScheduledStartDate] AS [ScheduledStartDate], i.[ScheduledEndDate] AS [ScheduledEndDate], i.[ActualStartDate] AS [ActualStartDate], i.[ActualEndDate] AS [ActualEndDate], i.[ActualResourceHrs] AS [ActualResourceHrs], i.[PlannedCost] AS [PlannedCost], i.[ActualCost] AS [ActualCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.WorkOrderRouting U
CREATE TRIGGER [Production].[dbr_audit_WorkOrderRouting_U] ON [Production].[WorkOrderRouting]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 59147256, N'[Production].[WorkOrderRouting]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[WorkOrderID] AS [WorkOrderID], d.[ProductID] AS [ProductID], d.[OperationSequence] AS [OperationSequence] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[WorkOrderID] AS [WorkOrderID], d.[ProductID] AS [ProductID], d.[OperationSequence] AS [OperationSequence], d.[LocationID] AS [LocationID], d.[ScheduledStartDate] AS [ScheduledStartDate], d.[ScheduledEndDate] AS [ScheduledEndDate], d.[ActualStartDate] AS [ActualStartDate], d.[ActualEndDate] AS [ActualEndDate], d.[ActualResourceHrs] AS [ActualResourceHrs], d.[PlannedCost] AS [PlannedCost], d.[ActualCost] AS [ActualCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[WorkOrderID] AS [WorkOrderID], i.[ProductID] AS [ProductID], i.[OperationSequence] AS [OperationSequence] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[WorkOrderID] AS [WorkOrderID], i.[ProductID] AS [ProductID], i.[OperationSequence] AS [OperationSequence], i.[LocationID] AS [LocationID], i.[ScheduledStartDate] AS [ScheduledStartDate], i.[ScheduledEndDate] AS [ScheduledEndDate], i.[ActualStartDate] AS [ActualStartDate], i.[ActualEndDate] AS [ActualEndDate], i.[ActualResourceHrs] AS [ActualResourceHrs], i.[PlannedCost] AS [PlannedCost], i.[ActualCost] AS [ActualCost], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Production.WorkOrderRouting D
CREATE TRIGGER [Production].[dbr_audit_WorkOrderRouting_D] ON [Production].[WorkOrderRouting]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 59147256, N'[Production].[WorkOrderRouting]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[WorkOrderID] AS [WorkOrderID], d.[ProductID] AS [ProductID], d.[OperationSequence] AS [OperationSequence] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[WorkOrderID] AS [WorkOrderID], d.[ProductID] AS [ProductID], d.[OperationSequence] AS [OperationSequence], d.[LocationID] AS [LocationID], d.[ScheduledStartDate] AS [ScheduledStartDate], d.[ScheduledEndDate] AS [ScheduledEndDate], d.[ActualStartDate] AS [ActualStartDate], d.[ActualEndDate] AS [ActualEndDate], d.[ActualResourceHrs] AS [ActualResourceHrs], d.[PlannedCost] AS [PlannedCost], d.[ActualCost] AS [ActualCost], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Production.WorkOrderRouting
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_WorkOrderRouting_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_WorkOrderRouting_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Production].[dbr_audit_WorkOrderRouting_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Purchasing.ProductVendor I
CREATE TRIGGER [Purchasing].[dbr_audit_ProductVendor_I] ON [Purchasing].[ProductVendor]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2002106173, N'[Purchasing].[ProductVendor]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[BusinessEntityID] AS [BusinessEntityID], i.[AverageLeadTime] AS [AverageLeadTime], i.[StandardPrice] AS [StandardPrice], i.[LastReceiptCost] AS [LastReceiptCost], i.[LastReceiptDate] AS [LastReceiptDate], i.[MinOrderQty] AS [MinOrderQty], i.[MaxOrderQty] AS [MaxOrderQty], i.[OnOrderQty] AS [OnOrderQty], i.[UnitMeasureCode] AS [UnitMeasureCode], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.ProductVendor U
CREATE TRIGGER [Purchasing].[dbr_audit_ProductVendor_U] ON [Purchasing].[ProductVendor]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2002106173, N'[Purchasing].[ProductVendor]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[BusinessEntityID] AS [BusinessEntityID], d.[AverageLeadTime] AS [AverageLeadTime], d.[StandardPrice] AS [StandardPrice], d.[LastReceiptCost] AS [LastReceiptCost], d.[LastReceiptDate] AS [LastReceiptDate], d.[MinOrderQty] AS [MinOrderQty], d.[MaxOrderQty] AS [MaxOrderQty], d.[OnOrderQty] AS [OnOrderQty], d.[UnitMeasureCode] AS [UnitMeasureCode], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ProductID] AS [ProductID], i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ProductID] AS [ProductID], i.[BusinessEntityID] AS [BusinessEntityID], i.[AverageLeadTime] AS [AverageLeadTime], i.[StandardPrice] AS [StandardPrice], i.[LastReceiptCost] AS [LastReceiptCost], i.[LastReceiptDate] AS [LastReceiptDate], i.[MinOrderQty] AS [MinOrderQty], i.[MaxOrderQty] AS [MaxOrderQty], i.[OnOrderQty] AS [OnOrderQty], i.[UnitMeasureCode] AS [UnitMeasureCode], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.ProductVendor D
CREATE TRIGGER [Purchasing].[dbr_audit_ProductVendor_D] ON [Purchasing].[ProductVendor]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2002106173, N'[Purchasing].[ProductVendor]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ProductID] AS [ProductID], d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ProductID] AS [ProductID], d.[BusinessEntityID] AS [BusinessEntityID], d.[AverageLeadTime] AS [AverageLeadTime], d.[StandardPrice] AS [StandardPrice], d.[LastReceiptCost] AS [LastReceiptCost], d.[LastReceiptDate] AS [LastReceiptDate], d.[MinOrderQty] AS [MinOrderQty], d.[MaxOrderQty] AS [MaxOrderQty], d.[OnOrderQty] AS [OnOrderQty], d.[UnitMeasureCode] AS [UnitMeasureCode], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Purchasing.ProductVendor
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_ProductVendor_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_ProductVendor_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_ProductVendor_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Purchasing.PurchaseOrderDetail I
CREATE TRIGGER [Purchasing].[dbr_audit_PurchaseOrderDetail_I] ON [Purchasing].[PurchaseOrderDetail]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2130106629, N'[Purchasing].[PurchaseOrderDetail]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID], i.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID], i.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID], i.[DueDate] AS [DueDate], i.[OrderQty] AS [OrderQty], i.[ProductID] AS [ProductID], i.[UnitPrice] AS [UnitPrice], i.[LineTotal] AS [LineTotal], i.[ReceivedQty] AS [ReceivedQty], i.[RejectedQty] AS [RejectedQty], i.[StockedQty] AS [StockedQty], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.PurchaseOrderDetail U
CREATE TRIGGER [Purchasing].[dbr_audit_PurchaseOrderDetail_U] ON [Purchasing].[PurchaseOrderDetail]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2130106629, N'[Purchasing].[PurchaseOrderDetail]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID], d.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID], d.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID], d.[DueDate] AS [DueDate], d.[OrderQty] AS [OrderQty], d.[ProductID] AS [ProductID], d.[UnitPrice] AS [UnitPrice], d.[LineTotal] AS [LineTotal], d.[ReceivedQty] AS [ReceivedQty], d.[RejectedQty] AS [RejectedQty], d.[StockedQty] AS [StockedQty], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID], i.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID], i.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID], i.[DueDate] AS [DueDate], i.[OrderQty] AS [OrderQty], i.[ProductID] AS [ProductID], i.[UnitPrice] AS [UnitPrice], i.[LineTotal] AS [LineTotal], i.[ReceivedQty] AS [ReceivedQty], i.[RejectedQty] AS [RejectedQty], i.[StockedQty] AS [StockedQty], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.PurchaseOrderDetail D
CREATE TRIGGER [Purchasing].[dbr_audit_PurchaseOrderDetail_D] ON [Purchasing].[PurchaseOrderDetail]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2130106629, N'[Purchasing].[PurchaseOrderDetail]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID], d.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID], d.[PurchaseOrderDetailID] AS [PurchaseOrderDetailID], d.[DueDate] AS [DueDate], d.[OrderQty] AS [OrderQty], d.[ProductID] AS [ProductID], d.[UnitPrice] AS [UnitPrice], d.[LineTotal] AS [LineTotal], d.[ReceivedQty] AS [ReceivedQty], d.[RejectedQty] AS [RejectedQty], d.[StockedQty] AS [StockedQty], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Purchasing.PurchaseOrderDetail
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_PurchaseOrderDetail_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_PurchaseOrderDetail_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_PurchaseOrderDetail_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Purchasing.PurchaseOrderHeader I
CREATE TRIGGER [Purchasing].[dbr_audit_PurchaseOrderHeader_I] ON [Purchasing].[PurchaseOrderHeader]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 78623323, N'[Purchasing].[PurchaseOrderHeader]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID], i.[RevisionNumber] AS [RevisionNumber], i.[Status] AS [Status], i.[EmployeeID] AS [EmployeeID], i.[VendorID] AS [VendorID], i.[ShipMethodID] AS [ShipMethodID], i.[OrderDate] AS [OrderDate], i.[ShipDate] AS [ShipDate], i.[SubTotal] AS [SubTotal], i.[TaxAmt] AS [TaxAmt], i.[Freight] AS [Freight], i.[TotalDue] AS [TotalDue], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.PurchaseOrderHeader U
CREATE TRIGGER [Purchasing].[dbr_audit_PurchaseOrderHeader_U] ON [Purchasing].[PurchaseOrderHeader]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 78623323, N'[Purchasing].[PurchaseOrderHeader]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID], d.[RevisionNumber] AS [RevisionNumber], d.[Status] AS [Status], d.[EmployeeID] AS [EmployeeID], d.[VendorID] AS [VendorID], d.[ShipMethodID] AS [ShipMethodID], d.[OrderDate] AS [OrderDate], d.[ShipDate] AS [ShipDate], d.[SubTotal] AS [SubTotal], d.[TaxAmt] AS [TaxAmt], d.[Freight] AS [Freight], d.[TotalDue] AS [TotalDue], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[PurchaseOrderID] AS [PurchaseOrderID], i.[RevisionNumber] AS [RevisionNumber], i.[Status] AS [Status], i.[EmployeeID] AS [EmployeeID], i.[VendorID] AS [VendorID], i.[ShipMethodID] AS [ShipMethodID], i.[OrderDate] AS [OrderDate], i.[ShipDate] AS [ShipDate], i.[SubTotal] AS [SubTotal], i.[TaxAmt] AS [TaxAmt], i.[Freight] AS [Freight], i.[TotalDue] AS [TotalDue], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.PurchaseOrderHeader D
CREATE TRIGGER [Purchasing].[dbr_audit_PurchaseOrderHeader_D] ON [Purchasing].[PurchaseOrderHeader]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 78623323, N'[Purchasing].[PurchaseOrderHeader]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[PurchaseOrderID] AS [PurchaseOrderID], d.[RevisionNumber] AS [RevisionNumber], d.[Status] AS [Status], d.[EmployeeID] AS [EmployeeID], d.[VendorID] AS [VendorID], d.[ShipMethodID] AS [ShipMethodID], d.[OrderDate] AS [OrderDate], d.[ShipDate] AS [ShipDate], d.[SubTotal] AS [SubTotal], d.[TaxAmt] AS [TaxAmt], d.[Freight] AS [Freight], d.[TotalDue] AS [TotalDue], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Purchasing.PurchaseOrderHeader
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_PurchaseOrderHeader_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_PurchaseOrderHeader_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_PurchaseOrderHeader_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Purchasing.ShipMethod I
CREATE TRIGGER [Purchasing].[dbr_audit_ShipMethod_I] ON [Purchasing].[ShipMethod]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1358627883, N'[Purchasing].[ShipMethod]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ShipMethodID] AS [ShipMethodID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ShipMethodID] AS [ShipMethodID], i.[Name] AS [Name], i.[ShipBase] AS [ShipBase], i.[ShipRate] AS [ShipRate], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.ShipMethod U
CREATE TRIGGER [Purchasing].[dbr_audit_ShipMethod_U] ON [Purchasing].[ShipMethod]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1358627883, N'[Purchasing].[ShipMethod]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ShipMethodID] AS [ShipMethodID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ShipMethodID] AS [ShipMethodID], d.[Name] AS [Name], d.[ShipBase] AS [ShipBase], d.[ShipRate] AS [ShipRate], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ShipMethodID] AS [ShipMethodID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ShipMethodID] AS [ShipMethodID], i.[Name] AS [Name], i.[ShipBase] AS [ShipBase], i.[ShipRate] AS [ShipRate], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.ShipMethod D
CREATE TRIGGER [Purchasing].[dbr_audit_ShipMethod_D] ON [Purchasing].[ShipMethod]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1358627883, N'[Purchasing].[ShipMethod]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ShipMethodID] AS [ShipMethodID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ShipMethodID] AS [ShipMethodID], d.[Name] AS [Name], d.[ShipBase] AS [ShipBase], d.[ShipRate] AS [ShipRate], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Purchasing.ShipMethod
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_ShipMethod_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_ShipMethod_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_ShipMethod_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Purchasing.Vendor I
CREATE TRIGGER [Purchasing].[dbr_audit_Vendor_I] ON [Purchasing].[Vendor]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2046630334, N'[Purchasing].[Vendor]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[AccountNumber] AS [AccountNumber], i.[Name] AS [Name], i.[CreditRating] AS [CreditRating], i.[PreferredVendorStatus] AS [PreferredVendorStatus], i.[ActiveFlag] AS [ActiveFlag], i.[PurchasingWebServiceURL] AS [PurchasingWebServiceURL], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.Vendor U
CREATE TRIGGER [Purchasing].[dbr_audit_Vendor_U] ON [Purchasing].[Vendor]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2046630334, N'[Purchasing].[Vendor]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[AccountNumber] AS [AccountNumber], d.[Name] AS [Name], d.[CreditRating] AS [CreditRating], d.[PreferredVendorStatus] AS [PreferredVendorStatus], d.[ActiveFlag] AS [ActiveFlag], d.[PurchasingWebServiceURL] AS [PurchasingWebServiceURL], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[AccountNumber] AS [AccountNumber], i.[Name] AS [Name], i.[CreditRating] AS [CreditRating], i.[PreferredVendorStatus] AS [PreferredVendorStatus], i.[ActiveFlag] AS [ActiveFlag], i.[PurchasingWebServiceURL] AS [PurchasingWebServiceURL], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Purchasing.Vendor D
CREATE TRIGGER [Purchasing].[dbr_audit_Vendor_D] ON [Purchasing].[Vendor]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2046630334, N'[Purchasing].[Vendor]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[AccountNumber] AS [AccountNumber], d.[Name] AS [Name], d.[CreditRating] AS [CreditRating], d.[PreferredVendorStatus] AS [PreferredVendorStatus], d.[ActiveFlag] AS [ActiveFlag], d.[PurchasingWebServiceURL] AS [PurchasingWebServiceURL], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Purchasing.Vendor
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_Vendor_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_Vendor_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Purchasing].[dbr_audit_Vendor_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.CountryRegionCurrency I
CREATE TRIGGER [Sales].[dbr_audit_CountryRegionCurrency_I] ON [Sales].[CountryRegionCurrency]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2085582468, N'[Sales].[CountryRegionCurrency]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CountryRegionCode] AS [CountryRegionCode], i.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CountryRegionCode] AS [CountryRegionCode], i.[CurrencyCode] AS [CurrencyCode], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.CountryRegionCurrency U
CREATE TRIGGER [Sales].[dbr_audit_CountryRegionCurrency_U] ON [Sales].[CountryRegionCurrency]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2085582468, N'[Sales].[CountryRegionCurrency]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CountryRegionCode] AS [CountryRegionCode], d.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CountryRegionCode] AS [CountryRegionCode], d.[CurrencyCode] AS [CurrencyCode], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CountryRegionCode] AS [CountryRegionCode], i.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CountryRegionCode] AS [CountryRegionCode], i.[CurrencyCode] AS [CurrencyCode], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.CountryRegionCurrency D
CREATE TRIGGER [Sales].[dbr_audit_CountryRegionCurrency_D] ON [Sales].[CountryRegionCurrency]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2085582468, N'[Sales].[CountryRegionCurrency]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CountryRegionCode] AS [CountryRegionCode], d.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CountryRegionCode] AS [CountryRegionCode], d.[CurrencyCode] AS [CurrencyCode], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.CountryRegionCurrency
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CountryRegionCurrency_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CountryRegionCurrency_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CountryRegionCurrency_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.CreditCard I
CREATE TRIGGER [Sales].[dbr_audit_CreditCard_I] ON [Sales].[CreditCard]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2099048, N'[Sales].[CreditCard]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CreditCardID] AS [CreditCardID], i.[CardType] AS [CardType], i.[CardNumber] AS [CardNumber], i.[ExpMonth] AS [ExpMonth], i.[ExpYear] AS [ExpYear], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.CreditCard U
CREATE TRIGGER [Sales].[dbr_audit_CreditCard_U] ON [Sales].[CreditCard]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2099048, N'[Sales].[CreditCard]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CreditCardID] AS [CreditCardID], d.[CardType] AS [CardType], d.[CardNumber] AS [CardNumber], d.[ExpMonth] AS [ExpMonth], d.[ExpYear] AS [ExpYear], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CreditCardID] AS [CreditCardID], i.[CardType] AS [CardType], i.[CardNumber] AS [CardNumber], i.[ExpMonth] AS [ExpMonth], i.[ExpYear] AS [ExpYear], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.CreditCard D
CREATE TRIGGER [Sales].[dbr_audit_CreditCard_D] ON [Sales].[CreditCard]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 2099048, N'[Sales].[CreditCard]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CreditCardID] AS [CreditCardID], d.[CardType] AS [CardType], d.[CardNumber] AS [CardNumber], d.[ExpMonth] AS [ExpMonth], d.[ExpYear] AS [ExpYear], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.CreditCard
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CreditCard_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CreditCard_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CreditCard_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.Currency I
CREATE TRIGGER [Sales].[dbr_audit_Currency_I] ON [Sales].[Currency]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 66099276, N'[Sales].[Currency]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CurrencyCode] AS [CurrencyCode], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.Currency U
CREATE TRIGGER [Sales].[dbr_audit_Currency_U] ON [Sales].[Currency]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 66099276, N'[Sales].[Currency]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CurrencyCode] AS [CurrencyCode], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CurrencyCode] AS [CurrencyCode], i.[Name] AS [Name], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.Currency D
CREATE TRIGGER [Sales].[dbr_audit_Currency_D] ON [Sales].[Currency]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 66099276, N'[Sales].[Currency]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CurrencyCode] AS [CurrencyCode] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CurrencyCode] AS [CurrencyCode], d.[Name] AS [Name], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.Currency
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Currency_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Currency_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Currency_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.CurrencyRate I
CREATE TRIGGER [Sales].[dbr_audit_CurrencyRate_I] ON [Sales].[CurrencyRate]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 98099390, N'[Sales].[CurrencyRate]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CurrencyRateID] AS [CurrencyRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CurrencyRateID] AS [CurrencyRateID], i.[CurrencyRateDate] AS [CurrencyRateDate], i.[FromCurrencyCode] AS [FromCurrencyCode], i.[ToCurrencyCode] AS [ToCurrencyCode], i.[AverageRate] AS [AverageRate], i.[EndOfDayRate] AS [EndOfDayRate], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.CurrencyRate U
CREATE TRIGGER [Sales].[dbr_audit_CurrencyRate_U] ON [Sales].[CurrencyRate]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 98099390, N'[Sales].[CurrencyRate]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CurrencyRateID] AS [CurrencyRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CurrencyRateID] AS [CurrencyRateID], d.[CurrencyRateDate] AS [CurrencyRateDate], d.[FromCurrencyCode] AS [FromCurrencyCode], d.[ToCurrencyCode] AS [ToCurrencyCode], d.[AverageRate] AS [AverageRate], d.[EndOfDayRate] AS [EndOfDayRate], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CurrencyRateID] AS [CurrencyRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CurrencyRateID] AS [CurrencyRateID], i.[CurrencyRateDate] AS [CurrencyRateDate], i.[FromCurrencyCode] AS [FromCurrencyCode], i.[ToCurrencyCode] AS [ToCurrencyCode], i.[AverageRate] AS [AverageRate], i.[EndOfDayRate] AS [EndOfDayRate], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.CurrencyRate D
CREATE TRIGGER [Sales].[dbr_audit_CurrencyRate_D] ON [Sales].[CurrencyRate]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 98099390, N'[Sales].[CurrencyRate]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CurrencyRateID] AS [CurrencyRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CurrencyRateID] AS [CurrencyRateID], d.[CurrencyRateDate] AS [CurrencyRateDate], d.[FromCurrencyCode] AS [FromCurrencyCode], d.[ToCurrencyCode] AS [ToCurrencyCode], d.[AverageRate] AS [AverageRate], d.[EndOfDayRate] AS [EndOfDayRate], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.CurrencyRate
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CurrencyRate_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CurrencyRate_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_CurrencyRate_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.Customer I
CREATE TRIGGER [Sales].[dbr_audit_Customer_I] ON [Sales].[Customer]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 130099504, N'[Sales].[Customer]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CustomerID] AS [CustomerID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CustomerID] AS [CustomerID], i.[PersonID] AS [PersonID], i.[StoreID] AS [StoreID], i.[TerritoryID] AS [TerritoryID], i.[AccountNumber] AS [AccountNumber], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.Customer U
CREATE TRIGGER [Sales].[dbr_audit_Customer_U] ON [Sales].[Customer]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 130099504, N'[Sales].[Customer]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CustomerID] AS [CustomerID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CustomerID] AS [CustomerID], d.[PersonID] AS [PersonID], d.[StoreID] AS [StoreID], d.[TerritoryID] AS [TerritoryID], d.[AccountNumber] AS [AccountNumber], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[CustomerID] AS [CustomerID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[CustomerID] AS [CustomerID], i.[PersonID] AS [PersonID], i.[StoreID] AS [StoreID], i.[TerritoryID] AS [TerritoryID], i.[AccountNumber] AS [AccountNumber], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.Customer D
CREATE TRIGGER [Sales].[dbr_audit_Customer_D] ON [Sales].[Customer]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 130099504, N'[Sales].[Customer]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[CustomerID] AS [CustomerID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[CustomerID] AS [CustomerID], d.[PersonID] AS [PersonID], d.[StoreID] AS [StoreID], d.[TerritoryID] AS [TerritoryID], d.[AccountNumber] AS [AccountNumber], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.Customer
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Customer_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Customer_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Customer_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.PersonCreditCard I
CREATE TRIGGER [Sales].[dbr_audit_PersonCreditCard_I] ON [Sales].[PersonCreditCard]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1010102639, N'[Sales].[PersonCreditCard]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[CreditCardID] AS [CreditCardID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.PersonCreditCard U
CREATE TRIGGER [Sales].[dbr_audit_PersonCreditCard_U] ON [Sales].[PersonCreditCard]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1010102639, N'[Sales].[PersonCreditCard]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[CreditCardID] AS [CreditCardID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[CreditCardID] AS [CreditCardID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.PersonCreditCard D
CREATE TRIGGER [Sales].[dbr_audit_PersonCreditCard_D] ON [Sales].[PersonCreditCard]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1010102639, N'[Sales].[PersonCreditCard]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[CreditCardID] AS [CreditCardID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[CreditCardID] AS [CreditCardID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.PersonCreditCard
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_PersonCreditCard_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_PersonCreditCard_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_PersonCreditCard_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesOrderDetail I
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderDetail_I] ON [Sales].[SalesOrderDetail]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 286624064, N'[Sales].[SalesOrderDetail]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesOrderDetailID] AS [SalesOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesOrderDetailID] AS [SalesOrderDetailID], i.[CarrierTrackingNumber] AS [CarrierTrackingNumber], i.[OrderQty] AS [OrderQty], i.[ProductID] AS [ProductID], i.[SpecialOfferID] AS [SpecialOfferID], i.[UnitPrice] AS [UnitPrice], i.[UnitPriceDiscount] AS [UnitPriceDiscount], i.[LineTotal] AS [LineTotal], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesOrderDetail U
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderDetail_U] ON [Sales].[SalesOrderDetail]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 286624064, N'[Sales].[SalesOrderDetail]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesOrderDetailID] AS [SalesOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesOrderDetailID] AS [SalesOrderDetailID], d.[CarrierTrackingNumber] AS [CarrierTrackingNumber], d.[OrderQty] AS [OrderQty], d.[ProductID] AS [ProductID], d.[SpecialOfferID] AS [SpecialOfferID], d.[UnitPrice] AS [UnitPrice], d.[UnitPriceDiscount] AS [UnitPriceDiscount], d.[LineTotal] AS [LineTotal], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesOrderDetailID] AS [SalesOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesOrderDetailID] AS [SalesOrderDetailID], i.[CarrierTrackingNumber] AS [CarrierTrackingNumber], i.[OrderQty] AS [OrderQty], i.[ProductID] AS [ProductID], i.[SpecialOfferID] AS [SpecialOfferID], i.[UnitPrice] AS [UnitPrice], i.[UnitPriceDiscount] AS [UnitPriceDiscount], i.[LineTotal] AS [LineTotal], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesOrderDetail D
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderDetail_D] ON [Sales].[SalesOrderDetail]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 286624064, N'[Sales].[SalesOrderDetail]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesOrderDetailID] AS [SalesOrderDetailID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesOrderDetailID] AS [SalesOrderDetailID], d.[CarrierTrackingNumber] AS [CarrierTrackingNumber], d.[OrderQty] AS [OrderQty], d.[ProductID] AS [ProductID], d.[SpecialOfferID] AS [SpecialOfferID], d.[UnitPrice] AS [UnitPrice], d.[UnitPriceDiscount] AS [UnitPriceDiscount], d.[LineTotal] AS [LineTotal], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesOrderDetail
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderDetail_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderDetail_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderDetail_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesOrderHeader I
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeader_I] ON [Sales].[SalesOrderHeader]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 398624463, N'[Sales].[SalesOrderHeader]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesOrderID] AS [SalesOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[RevisionNumber] AS [RevisionNumber], i.[OrderDate] AS [OrderDate], i.[DueDate] AS [DueDate], i.[ShipDate] AS [ShipDate], i.[Status] AS [Status], i.[OnlineOrderFlag] AS [OnlineOrderFlag], i.[SalesOrderNumber] AS [SalesOrderNumber], i.[PurchaseOrderNumber] AS [PurchaseOrderNumber], i.[AccountNumber] AS [AccountNumber], i.[CustomerID] AS [CustomerID], i.[SalesPersonID] AS [SalesPersonID], i.[TerritoryID] AS [TerritoryID], i.[BillToAddressID] AS [BillToAddressID], i.[ShipToAddressID] AS [ShipToAddressID], i.[ShipMethodID] AS [ShipMethodID], i.[CreditCardID] AS [CreditCardID], i.[CreditCardApprovalCode] AS [CreditCardApprovalCode], i.[CurrencyRateID] AS [CurrencyRateID], i.[SubTotal] AS [SubTotal], i.[TaxAmt] AS [TaxAmt], i.[Freight] AS [Freight], i.[TotalDue] AS [TotalDue], i.[Comment] AS [Comment], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesOrderHeader U
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeader_U] ON [Sales].[SalesOrderHeader]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 398624463, N'[Sales].[SalesOrderHeader]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesOrderID] AS [SalesOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[RevisionNumber] AS [RevisionNumber], d.[OrderDate] AS [OrderDate], d.[DueDate] AS [DueDate], d.[ShipDate] AS [ShipDate], d.[Status] AS [Status], d.[OnlineOrderFlag] AS [OnlineOrderFlag], d.[SalesOrderNumber] AS [SalesOrderNumber], d.[PurchaseOrderNumber] AS [PurchaseOrderNumber], d.[AccountNumber] AS [AccountNumber], d.[CustomerID] AS [CustomerID], d.[SalesPersonID] AS [SalesPersonID], d.[TerritoryID] AS [TerritoryID], d.[BillToAddressID] AS [BillToAddressID], d.[ShipToAddressID] AS [ShipToAddressID], d.[ShipMethodID] AS [ShipMethodID], d.[CreditCardID] AS [CreditCardID], d.[CreditCardApprovalCode] AS [CreditCardApprovalCode], d.[CurrencyRateID] AS [CurrencyRateID], d.[SubTotal] AS [SubTotal], d.[TaxAmt] AS [TaxAmt], d.[Freight] AS [Freight], d.[TotalDue] AS [TotalDue], d.[Comment] AS [Comment], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesOrderID] AS [SalesOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[RevisionNumber] AS [RevisionNumber], i.[OrderDate] AS [OrderDate], i.[DueDate] AS [DueDate], i.[ShipDate] AS [ShipDate], i.[Status] AS [Status], i.[OnlineOrderFlag] AS [OnlineOrderFlag], i.[SalesOrderNumber] AS [SalesOrderNumber], i.[PurchaseOrderNumber] AS [PurchaseOrderNumber], i.[AccountNumber] AS [AccountNumber], i.[CustomerID] AS [CustomerID], i.[SalesPersonID] AS [SalesPersonID], i.[TerritoryID] AS [TerritoryID], i.[BillToAddressID] AS [BillToAddressID], i.[ShipToAddressID] AS [ShipToAddressID], i.[ShipMethodID] AS [ShipMethodID], i.[CreditCardID] AS [CreditCardID], i.[CreditCardApprovalCode] AS [CreditCardApprovalCode], i.[CurrencyRateID] AS [CurrencyRateID], i.[SubTotal] AS [SubTotal], i.[TaxAmt] AS [TaxAmt], i.[Freight] AS [Freight], i.[TotalDue] AS [TotalDue], i.[Comment] AS [Comment], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesOrderHeader D
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeader_D] ON [Sales].[SalesOrderHeader]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 398624463, N'[Sales].[SalesOrderHeader]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesOrderID] AS [SalesOrderID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[RevisionNumber] AS [RevisionNumber], d.[OrderDate] AS [OrderDate], d.[DueDate] AS [DueDate], d.[ShipDate] AS [ShipDate], d.[Status] AS [Status], d.[OnlineOrderFlag] AS [OnlineOrderFlag], d.[SalesOrderNumber] AS [SalesOrderNumber], d.[PurchaseOrderNumber] AS [PurchaseOrderNumber], d.[AccountNumber] AS [AccountNumber], d.[CustomerID] AS [CustomerID], d.[SalesPersonID] AS [SalesPersonID], d.[TerritoryID] AS [TerritoryID], d.[BillToAddressID] AS [BillToAddressID], d.[ShipToAddressID] AS [ShipToAddressID], d.[ShipMethodID] AS [ShipMethodID], d.[CreditCardID] AS [CreditCardID], d.[CreditCardApprovalCode] AS [CreditCardApprovalCode], d.[CurrencyRateID] AS [CurrencyRateID], d.[SubTotal] AS [SubTotal], d.[TaxAmt] AS [TaxAmt], d.[Freight] AS [Freight], d.[TotalDue] AS [TotalDue], d.[Comment] AS [Comment], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesOrderHeader
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderHeader_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderHeader_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderHeader_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesOrderHeaderSalesReason I
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeaderSalesReason_I] ON [Sales].[SalesOrderHeaderSalesReason]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 654625375, N'[Sales].[SalesOrderHeaderSalesReason]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesReasonID] AS [SalesReasonID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesOrderHeaderSalesReason U
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeaderSalesReason_U] ON [Sales].[SalesOrderHeaderSalesReason]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 654625375, N'[Sales].[SalesOrderHeaderSalesReason]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesReasonID] AS [SalesReasonID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesOrderID] AS [SalesOrderID], i.[SalesReasonID] AS [SalesReasonID], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesOrderHeaderSalesReason D
CREATE TRIGGER [Sales].[dbr_audit_SalesOrderHeaderSalesReason_D] ON [Sales].[SalesOrderHeaderSalesReason]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 654625375, N'[Sales].[SalesOrderHeaderSalesReason]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesOrderID] AS [SalesOrderID], d.[SalesReasonID] AS [SalesReasonID], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesOrderHeaderSalesReason
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderHeaderSalesReason_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderHeaderSalesReason_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderHeaderSalesReason_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesPerson I
CREATE TRIGGER [Sales].[dbr_audit_SalesPerson_I] ON [Sales].[SalesPerson]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 686625489, N'[Sales].[SalesPerson]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[TerritoryID] AS [TerritoryID], i.[SalesQuota] AS [SalesQuota], i.[Bonus] AS [Bonus], i.[CommissionPct] AS [CommissionPct], i.[SalesYTD] AS [SalesYTD], i.[SalesLastYear] AS [SalesLastYear], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesPerson U
CREATE TRIGGER [Sales].[dbr_audit_SalesPerson_U] ON [Sales].[SalesPerson]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 686625489, N'[Sales].[SalesPerson]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[TerritoryID] AS [TerritoryID], d.[SalesQuota] AS [SalesQuota], d.[Bonus] AS [Bonus], d.[CommissionPct] AS [CommissionPct], d.[SalesYTD] AS [SalesYTD], d.[SalesLastYear] AS [SalesLastYear], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[TerritoryID] AS [TerritoryID], i.[SalesQuota] AS [SalesQuota], i.[Bonus] AS [Bonus], i.[CommissionPct] AS [CommissionPct], i.[SalesYTD] AS [SalesYTD], i.[SalesLastYear] AS [SalesLastYear], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesPerson D
CREATE TRIGGER [Sales].[dbr_audit_SalesPerson_D] ON [Sales].[SalesPerson]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 686625489, N'[Sales].[SalesPerson]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[TerritoryID] AS [TerritoryID], d.[SalesQuota] AS [SalesQuota], d.[Bonus] AS [Bonus], d.[CommissionPct] AS [CommissionPct], d.[SalesYTD] AS [SalesYTD], d.[SalesLastYear] AS [SalesLastYear], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesPerson
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesPerson_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesPerson_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesPerson_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesPersonQuotaHistory I
CREATE TRIGGER [Sales].[dbr_audit_SalesPersonQuotaHistory_I] ON [Sales].[SalesPersonQuotaHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 878626173, N'[Sales].[SalesPersonQuotaHistory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[QuotaDate] AS [QuotaDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[QuotaDate] AS [QuotaDate], i.[SalesQuota] AS [SalesQuota], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesPersonQuotaHistory U
CREATE TRIGGER [Sales].[dbr_audit_SalesPersonQuotaHistory_U] ON [Sales].[SalesPersonQuotaHistory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 878626173, N'[Sales].[SalesPersonQuotaHistory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[QuotaDate] AS [QuotaDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[QuotaDate] AS [QuotaDate], d.[SalesQuota] AS [SalesQuota], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[QuotaDate] AS [QuotaDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[QuotaDate] AS [QuotaDate], i.[SalesQuota] AS [SalesQuota], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesPersonQuotaHistory D
CREATE TRIGGER [Sales].[dbr_audit_SalesPersonQuotaHistory_D] ON [Sales].[SalesPersonQuotaHistory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 878626173, N'[Sales].[SalesPersonQuotaHistory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[QuotaDate] AS [QuotaDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[QuotaDate] AS [QuotaDate], d.[SalesQuota] AS [SalesQuota], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesPersonQuotaHistory
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesPersonQuotaHistory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesPersonQuotaHistory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesPersonQuotaHistory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesReason I
CREATE TRIGGER [Sales].[dbr_audit_SalesReason_I] ON [Sales].[SalesReason]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 942626401, N'[Sales].[SalesReason]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesReasonID] AS [SalesReasonID], i.[Name] AS [Name], i.[ReasonType] AS [ReasonType], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesReason U
CREATE TRIGGER [Sales].[dbr_audit_SalesReason_U] ON [Sales].[SalesReason]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 942626401, N'[Sales].[SalesReason]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesReasonID] AS [SalesReasonID], d.[Name] AS [Name], d.[ReasonType] AS [ReasonType], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesReasonID] AS [SalesReasonID], i.[Name] AS [Name], i.[ReasonType] AS [ReasonType], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesReason D
CREATE TRIGGER [Sales].[dbr_audit_SalesReason_D] ON [Sales].[SalesReason]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 942626401, N'[Sales].[SalesReason]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesReasonID] AS [SalesReasonID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesReasonID] AS [SalesReasonID], d.[Name] AS [Name], d.[ReasonType] AS [ReasonType], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesReason
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesReason_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesReason_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesReason_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesTaxRate I
CREATE TRIGGER [Sales].[dbr_audit_SalesTaxRate_I] ON [Sales].[SalesTaxRate]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 974626515, N'[Sales].[SalesTaxRate]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesTaxRateID] AS [SalesTaxRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesTaxRateID] AS [SalesTaxRateID], i.[StateProvinceID] AS [StateProvinceID], i.[TaxType] AS [TaxType], i.[TaxRate] AS [TaxRate], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesTaxRate U
CREATE TRIGGER [Sales].[dbr_audit_SalesTaxRate_U] ON [Sales].[SalesTaxRate]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 974626515, N'[Sales].[SalesTaxRate]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesTaxRateID] AS [SalesTaxRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesTaxRateID] AS [SalesTaxRateID], d.[StateProvinceID] AS [StateProvinceID], d.[TaxType] AS [TaxType], d.[TaxRate] AS [TaxRate], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SalesTaxRateID] AS [SalesTaxRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SalesTaxRateID] AS [SalesTaxRateID], i.[StateProvinceID] AS [StateProvinceID], i.[TaxType] AS [TaxType], i.[TaxRate] AS [TaxRate], i.[Name] AS [Name], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesTaxRate D
CREATE TRIGGER [Sales].[dbr_audit_SalesTaxRate_D] ON [Sales].[SalesTaxRate]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 974626515, N'[Sales].[SalesTaxRate]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SalesTaxRateID] AS [SalesTaxRateID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SalesTaxRateID] AS [SalesTaxRateID], d.[StateProvinceID] AS [StateProvinceID], d.[TaxType] AS [TaxType], d.[TaxRate] AS [TaxRate], d.[Name] AS [Name], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesTaxRate
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTaxRate_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTaxRate_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTaxRate_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesTerritory I
CREATE TRIGGER [Sales].[dbr_audit_SalesTerritory_I] ON [Sales].[SalesTerritory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1054626800, N'[Sales].[SalesTerritory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[TerritoryID] AS [TerritoryID], i.[Name] AS [Name], i.[CountryRegionCode] AS [CountryRegionCode], i.[Group] AS [Group], i.[SalesYTD] AS [SalesYTD], i.[SalesLastYear] AS [SalesLastYear], i.[CostYTD] AS [CostYTD], i.[CostLastYear] AS [CostLastYear], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesTerritory U
CREATE TRIGGER [Sales].[dbr_audit_SalesTerritory_U] ON [Sales].[SalesTerritory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1054626800, N'[Sales].[SalesTerritory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[TerritoryID] AS [TerritoryID], d.[Name] AS [Name], d.[CountryRegionCode] AS [CountryRegionCode], d.[Group] AS [Group], d.[SalesYTD] AS [SalesYTD], d.[SalesLastYear] AS [SalesLastYear], d.[CostYTD] AS [CostYTD], d.[CostLastYear] AS [CostLastYear], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[TerritoryID] AS [TerritoryID], i.[Name] AS [Name], i.[CountryRegionCode] AS [CountryRegionCode], i.[Group] AS [Group], i.[SalesYTD] AS [SalesYTD], i.[SalesLastYear] AS [SalesLastYear], i.[CostYTD] AS [CostYTD], i.[CostLastYear] AS [CostLastYear], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesTerritory D
CREATE TRIGGER [Sales].[dbr_audit_SalesTerritory_D] ON [Sales].[SalesTerritory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1054626800, N'[Sales].[SalesTerritory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[TerritoryID] AS [TerritoryID], d.[Name] AS [Name], d.[CountryRegionCode] AS [CountryRegionCode], d.[Group] AS [Group], d.[SalesYTD] AS [SalesYTD], d.[SalesLastYear] AS [SalesLastYear], d.[CostYTD] AS [CostYTD], d.[CostLastYear] AS [CostLastYear], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesTerritory
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTerritory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTerritory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTerritory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SalesTerritoryHistory I
CREATE TRIGGER [Sales].[dbr_audit_SalesTerritoryHistory_I] ON [Sales].[SalesTerritoryHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1230627427, N'[Sales].[SalesTerritoryHistory]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[StartDate] AS [StartDate], i.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[TerritoryID] AS [TerritoryID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesTerritoryHistory U
CREATE TRIGGER [Sales].[dbr_audit_SalesTerritoryHistory_U] ON [Sales].[SalesTerritoryHistory]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1230627427, N'[Sales].[SalesTerritoryHistory]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[StartDate] AS [StartDate], d.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[TerritoryID] AS [TerritoryID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[StartDate] AS [StartDate], i.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[TerritoryID] AS [TerritoryID], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SalesTerritoryHistory D
CREATE TRIGGER [Sales].[dbr_audit_SalesTerritoryHistory_D] ON [Sales].[SalesTerritoryHistory]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1230627427, N'[Sales].[SalesTerritoryHistory]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[StartDate] AS [StartDate], d.[TerritoryID] AS [TerritoryID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[TerritoryID] AS [TerritoryID], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SalesTerritoryHistory
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTerritoryHistory_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTerritoryHistory_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesTerritoryHistory_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.ShoppingCartItem I
CREATE TRIGGER [Sales].[dbr_audit_ShoppingCartItem_I] ON [Sales].[ShoppingCartItem]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1470628282, N'[Sales].[ShoppingCartItem]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ShoppingCartItemID] AS [ShoppingCartItemID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ShoppingCartItemID] AS [ShoppingCartItemID], i.[ShoppingCartID] AS [ShoppingCartID], i.[Quantity] AS [Quantity], i.[ProductID] AS [ProductID], i.[DateCreated] AS [DateCreated], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.ShoppingCartItem U
CREATE TRIGGER [Sales].[dbr_audit_ShoppingCartItem_U] ON [Sales].[ShoppingCartItem]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1470628282, N'[Sales].[ShoppingCartItem]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ShoppingCartItemID] AS [ShoppingCartItemID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ShoppingCartItemID] AS [ShoppingCartItemID], d.[ShoppingCartID] AS [ShoppingCartID], d.[Quantity] AS [Quantity], d.[ProductID] AS [ProductID], d.[DateCreated] AS [DateCreated], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ShoppingCartItemID] AS [ShoppingCartItemID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ShoppingCartItemID] AS [ShoppingCartItemID], i.[ShoppingCartID] AS [ShoppingCartID], i.[Quantity] AS [Quantity], i.[ProductID] AS [ProductID], i.[DateCreated] AS [DateCreated], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.ShoppingCartItem D
CREATE TRIGGER [Sales].[dbr_audit_ShoppingCartItem_D] ON [Sales].[ShoppingCartItem]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1470628282, N'[Sales].[ShoppingCartItem]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ShoppingCartItemID] AS [ShoppingCartItemID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ShoppingCartItemID] AS [ShoppingCartItemID], d.[ShoppingCartID] AS [ShoppingCartID], d.[Quantity] AS [Quantity], d.[ProductID] AS [ProductID], d.[DateCreated] AS [DateCreated], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.ShoppingCartItem
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_ShoppingCartItem_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_ShoppingCartItem_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_ShoppingCartItem_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SpecialOffer I
CREATE TRIGGER [Sales].[dbr_audit_SpecialOffer_I] ON [Sales].[SpecialOffer]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1550628567, N'[Sales].[SpecialOffer]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SpecialOfferID] AS [SpecialOfferID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SpecialOfferID] AS [SpecialOfferID], i.[Description] AS [Description], i.[DiscountPct] AS [DiscountPct], i.[Type] AS [Type], i.[Category] AS [Category], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[MinQty] AS [MinQty], i.[MaxQty] AS [MaxQty], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SpecialOffer U
CREATE TRIGGER [Sales].[dbr_audit_SpecialOffer_U] ON [Sales].[SpecialOffer]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1550628567, N'[Sales].[SpecialOffer]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SpecialOfferID] AS [SpecialOfferID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SpecialOfferID] AS [SpecialOfferID], d.[Description] AS [Description], d.[DiscountPct] AS [DiscountPct], d.[Type] AS [Type], d.[Category] AS [Category], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[MinQty] AS [MinQty], d.[MaxQty] AS [MaxQty], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SpecialOfferID] AS [SpecialOfferID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SpecialOfferID] AS [SpecialOfferID], i.[Description] AS [Description], i.[DiscountPct] AS [DiscountPct], i.[Type] AS [Type], i.[Category] AS [Category], i.[StartDate] AS [StartDate], i.[EndDate] AS [EndDate], i.[MinQty] AS [MinQty], i.[MaxQty] AS [MaxQty], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SpecialOffer D
CREATE TRIGGER [Sales].[dbr_audit_SpecialOffer_D] ON [Sales].[SpecialOffer]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1550628567, N'[Sales].[SpecialOffer]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SpecialOfferID] AS [SpecialOfferID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SpecialOfferID] AS [SpecialOfferID], d.[Description] AS [Description], d.[DiscountPct] AS [DiscountPct], d.[Type] AS [Type], d.[Category] AS [Category], d.[StartDate] AS [StartDate], d.[EndDate] AS [EndDate], d.[MinQty] AS [MinQty], d.[MaxQty] AS [MaxQty], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SpecialOffer
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SpecialOffer_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SpecialOffer_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SpecialOffer_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.SpecialOfferProduct I
CREATE TRIGGER [Sales].[dbr_audit_SpecialOfferProduct_I] ON [Sales].[SpecialOfferProduct]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1694629080, N'[Sales].[SpecialOfferProduct]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SpecialOfferID] AS [SpecialOfferID], i.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SpecialOfferID] AS [SpecialOfferID], i.[ProductID] AS [ProductID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SpecialOfferProduct U
CREATE TRIGGER [Sales].[dbr_audit_SpecialOfferProduct_U] ON [Sales].[SpecialOfferProduct]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1694629080, N'[Sales].[SpecialOfferProduct]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SpecialOfferID] AS [SpecialOfferID], d.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SpecialOfferID] AS [SpecialOfferID], d.[ProductID] AS [ProductID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SpecialOfferID] AS [SpecialOfferID], i.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SpecialOfferID] AS [SpecialOfferID], i.[ProductID] AS [ProductID], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.SpecialOfferProduct D
CREATE TRIGGER [Sales].[dbr_audit_SpecialOfferProduct_D] ON [Sales].[SpecialOfferProduct]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1694629080, N'[Sales].[SpecialOfferProduct]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SpecialOfferID] AS [SpecialOfferID], d.[ProductID] AS [ProductID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SpecialOfferID] AS [SpecialOfferID], d.[ProductID] AS [ProductID], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.SpecialOfferProduct
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SpecialOfferProduct_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SpecialOfferProduct_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SpecialOfferProduct_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger Sales.Store I
CREATE TRIGGER [Sales].[dbr_audit_Store_I] ON [Sales].[Store]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1806629479, N'[Sales].[Store]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[Name] AS [Name], i.[SalesPersonID] AS [SalesPersonID], CONVERT(nvarchar(max), i.[Demographics]) AS [Demographics], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.Store U
CREATE TRIGGER [Sales].[dbr_audit_Store_U] ON [Sales].[Store]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1806629479, N'[Sales].[Store]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[Name] AS [Name], d.[SalesPersonID] AS [SalesPersonID], CONVERT(nvarchar(max), d.[Demographics]) AS [Demographics], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[BusinessEntityID] AS [BusinessEntityID], i.[Name] AS [Name], i.[SalesPersonID] AS [SalesPersonID], CONVERT(nvarchar(max), i.[Demographics]) AS [Demographics], i.[rowguid] AS [rowguid], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger Sales.Store D
CREATE TRIGGER [Sales].[dbr_audit_Store_D] ON [Sales].[Store]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1806629479, N'[Sales].[Store]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[BusinessEntityID] AS [BusinessEntityID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[BusinessEntityID] AS [BusinessEntityID], d.[Name] AS [Name], d.[SalesPersonID] AS [SalesPersonID], CONVERT(nvarchar(max), d.[Demographics]) AS [Demographics], d.[rowguid] AS [rowguid], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order Sales.Store
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Store_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Store_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_Store_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger dbo.AWBuildVersion I
CREATE TRIGGER [dbo].[dbr_audit_AWBuildVersion_I] ON [dbo].[AWBuildVersion]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1749581271, N'[dbo].[AWBuildVersion]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SystemInformationID] AS [SystemInformationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SystemInformationID] AS [SystemInformationID], i.[Database Version] AS [Database Version], i.[VersionDate] AS [VersionDate], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger dbo.AWBuildVersion U
CREATE TRIGGER [dbo].[dbr_audit_AWBuildVersion_U] ON [dbo].[AWBuildVersion]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1749581271, N'[dbo].[AWBuildVersion]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SystemInformationID] AS [SystemInformationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SystemInformationID] AS [SystemInformationID], d.[Database Version] AS [Database Version], d.[VersionDate] AS [VersionDate], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[SystemInformationID] AS [SystemInformationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[SystemInformationID] AS [SystemInformationID], i.[Database Version] AS [Database Version], i.[VersionDate] AS [VersionDate], i.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger dbo.AWBuildVersion D
CREATE TRIGGER [dbo].[dbr_audit_AWBuildVersion_D] ON [dbo].[AWBuildVersion]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1749581271, N'[dbo].[AWBuildVersion]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[SystemInformationID] AS [SystemInformationID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[SystemInformationID] AS [SystemInformationID], d.[Database Version] AS [Database Version], d.[VersionDate] AS [VersionDate], d.[ModifiedDate] AS [ModifiedDate] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order dbo.AWBuildVersion
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_AWBuildVersion_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_AWBuildVersion_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_AWBuildVersion_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger dbo.DatabaseLog I
CREATE TRIGGER [dbo].[dbr_audit_DatabaseLog_I] ON [dbo].[DatabaseLog]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1525580473, N'[dbo].[DatabaseLog]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[DatabaseLogID] AS [DatabaseLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[DatabaseLogID] AS [DatabaseLogID], i.[PostTime] AS [PostTime], i.[DatabaseUser] AS [DatabaseUser], i.[Event] AS [Event], i.[Schema] AS [Schema], i.[Object] AS [Object], i.[TSQL] AS [TSQL], CONVERT(nvarchar(max), i.[XmlEvent]) AS [XmlEvent] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger dbo.DatabaseLog U
CREATE TRIGGER [dbo].[dbr_audit_DatabaseLog_U] ON [dbo].[DatabaseLog]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1525580473, N'[dbo].[DatabaseLog]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[DatabaseLogID] AS [DatabaseLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[DatabaseLogID] AS [DatabaseLogID], d.[PostTime] AS [PostTime], d.[DatabaseUser] AS [DatabaseUser], d.[Event] AS [Event], d.[Schema] AS [Schema], d.[Object] AS [Object], d.[TSQL] AS [TSQL], CONVERT(nvarchar(max), d.[XmlEvent]) AS [XmlEvent] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[DatabaseLogID] AS [DatabaseLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[DatabaseLogID] AS [DatabaseLogID], i.[PostTime] AS [PostTime], i.[DatabaseUser] AS [DatabaseUser], i.[Event] AS [Event], i.[Schema] AS [Schema], i.[Object] AS [Object], i.[TSQL] AS [TSQL], CONVERT(nvarchar(max), i.[XmlEvent]) AS [XmlEvent] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger dbo.DatabaseLog D
CREATE TRIGGER [dbo].[dbr_audit_DatabaseLog_D] ON [dbo].[DatabaseLog]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1525580473, N'[dbo].[DatabaseLog]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[DatabaseLogID] AS [DatabaseLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[DatabaseLogID] AS [DatabaseLogID], d.[PostTime] AS [PostTime], d.[DatabaseUser] AS [DatabaseUser], d.[Event] AS [Event], d.[Schema] AS [Schema], d.[Object] AS [Object], d.[TSQL] AS [TSQL], CONVERT(nvarchar(max), d.[XmlEvent]) AS [XmlEvent] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order dbo.DatabaseLog
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_DatabaseLog_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_DatabaseLog_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_DatabaseLog_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- trigger dbo.ErrorLog I
CREATE TRIGGER [dbo].[dbr_audit_ErrorLog_I] ON [dbo].[ErrorLog]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1557580587, N'[dbo].[ErrorLog]', 'I', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ErrorLogID] AS [ErrorLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ErrorLogID] AS [ErrorLogID], i.[ErrorTime] AS [ErrorTime], i.[UserName] AS [UserName], i.[ErrorNumber] AS [ErrorNumber], i.[ErrorSeverity] AS [ErrorSeverity], i.[ErrorState] AS [ErrorState], i.[ErrorProcedure] AS [ErrorProcedure], i.[ErrorLine] AS [ErrorLine], i.[ErrorMessage] AS [ErrorMessage] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger dbo.ErrorLog U
CREATE TRIGGER [dbo].[dbr_audit_ErrorLog_U] ON [dbo].[ErrorLog]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1557580587, N'[dbo].[ErrorLog]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ErrorLogID] AS [ErrorLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ErrorLogID] AS [ErrorLogID], d.[ErrorTime] AS [ErrorTime], d.[UserName] AS [UserName], d.[ErrorNumber] AS [ErrorNumber], d.[ErrorSeverity] AS [ErrorSeverity], d.[ErrorState] AS [ErrorState], d.[ErrorProcedure] AS [ErrorProcedure], d.[ErrorLine] AS [ErrorLine], d.[ErrorMessage] AS [ErrorMessage] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[ErrorLogID] AS [ErrorLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[ErrorLogID] AS [ErrorLogID], i.[ErrorTime] AS [ErrorTime], i.[UserName] AS [UserName], i.[ErrorNumber] AS [ErrorNumber], i.[ErrorSeverity] AS [ErrorSeverity], i.[ErrorState] AS [ErrorState], i.[ErrorProcedure] AS [ErrorProcedure], i.[ErrorLine] AS [ErrorLine], i.[ErrorMessage] AS [ErrorMessage] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
-- trigger dbo.ErrorLog D
CREATE TRIGGER [dbo].[dbr_audit_ErrorLog_D] ON [dbo].[ErrorLog]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, 1557580587, N'[dbo].[ErrorLog]', 'D', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[ErrorLogID] AS [ErrorLogID] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[ErrorLogID] AS [ErrorLogID], d.[ErrorTime] AS [ErrorTime], d.[UserName] AS [UserName], d.[ErrorNumber] AS [ErrorNumber], d.[ErrorSeverity] AS [ErrorSeverity], d.[ErrorState] AS [ErrorState], d.[ErrorProcedure] AS [ErrorProcedure], d.[ErrorLine] AS [ErrorLine], d.[ErrorMessage] AS [ErrorMessage] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
END;
GO
-- order dbo.ErrorLog
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_ErrorLog_I]', @order = N'Last', @stmttype = N'INSERT';
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_ErrorLog_U]', @order = N'Last', @stmttype = N'UPDATE';
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_ErrorLog_D]', @order = N'Last', @stmttype = N'DELETE';
GO
-- reset
DELETE FROM audit.AuditRowImage;
DELETE FROM audit.AuditHeader;
GO
