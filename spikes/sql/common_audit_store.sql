/* common_audit_store.sql — 감사 저장소 계약 DDL (§6.2, 오케스트레이터 계약)
   현재 DB에 설치한다(USE 없음). 이미 있으면 건너뛴다. 트랜잭션 안에서도 실행 가능.
   실행: sqlcmd -d <SpikeLab_SPn> -i common_audit_store.sql  (각 spN_00_create_db.sql 다음) */
SET NOCOUNT ON;
IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA audit AUTHORIZATION dbo');
GO
IF OBJECT_ID(N'audit.CaptureSeq', N'SO') IS NULL
    CREATE SEQUENCE audit.CaptureSeq AS bigint START WITH 1 INCREMENT BY 1;
GO
IF OBJECT_ID(N'audit.AuditHeader', N'U') IS NULL
CREATE TABLE audit.AuditHeader (
    captureSeq    bigint        NOT NULL CONSTRAINT PK_AuditHeader PRIMARY KEY,
    tableObjectId int           NOT NULL,
    tableName     nvarchar(300) NOT NULL,
    operation     char(1)       NOT NULL CONSTRAINT CK_AuditHeader_operation CHECK (operation IN ('I','U','D')),
    insertedCount bigint        NOT NULL,
    deletedCount  bigint        NOT NULL,
    nestLevel     int           NOT NULL,
    tranCount     int           NOT NULL,
    imageMode     varchar(4)    NOT NULL CONSTRAINT CK_AuditHeader_imageMode CHECK (imageMode IN ('FULL','KEYS'))
);
GO
IF OBJECT_ID(N'audit.AuditRowImage', N'U') IS NULL
CREATE TABLE audit.AuditRowImage (
    captureSeq bigint        NOT NULL,
    imageKind  varchar(8)    NOT NULL,
    ordinal    int           NOT NULL,
    keyJson    nvarchar(max) NULL,
    rowJson    nvarchar(max) NULL,
    CONSTRAINT PK_AuditRowImage PRIMARY KEY (captureSeq, imageKind, ordinal)
);
GO
IF OBJECT_ID(N'audit.AuditControl', N'U') IS NULL
BEGIN
    CREATE TABLE audit.AuditControl (
        id   tinyint    NOT NULL CONSTRAINT PK_AuditControl PRIMARY KEY CONSTRAINT CK_AuditControl_id CHECK (id = 1),
        mode varchar(4) NOT NULL CONSTRAINT CK_AuditControl_mode CHECK (mode IN ('FULL','KEYS')),
        cap  bigint     NOT NULL
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM audit.AuditControl WHERE id = 1)
    INSERT audit.AuditControl (id, mode, cap) VALUES (1, 'FULL', 100000);
GO
/* 확인 */
SELECT s.name AS schemaName, o.name AS objectName, o.type_desc, USER_NAME(OBJECTPROPERTY(o.object_id, 'OwnerId')) AS ownerName
FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE s.name = N'audit' AND o.type IN ('U','SO')
ORDER BY o.name;
GO
