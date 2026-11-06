/* common_hand_audit.sql — 미니 스키마용 손 감사 트리거(I/U/D 3개 본문) 설치 프로시저
   계약: [스키마].[dbr_audit_<테이블>_<I|U|D>], SET NOCOUNT ON → NEXT VALUE FOR → AuditControl mode →
   헤더 1행 → 행 이미지(U: DELETED 다음 INSERTED), 연산별 Last(사용자 Last가 있으면 생략, First 금지).
   본문 모양은 backend-2 생성기(C9)와 같게 맞춤(변수명·ISNULL 기본값·tableName N'[s].[t]').
   다른 점: tableObjectId는 리터럴 대신 OBJECT_ID(N'[s].[t]')(값은 같음).
   전제: 미니 테이블은 모두 (Id int PK, Ref int NULL, Ref2 int NULL, Src varchar(40) NULL) 모양.
   실행: common_audit_store.sql 다음, 같은 DB에서. */
CREATE OR ALTER PROCEDURE dbo.spike_InstallAudit
    @schema sysname, @table sysname, @op char(1)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @full nvarchar(300) = QUOTENAME(@schema) + N'.' + QUOTENAME(@table);
    DECLARE @trg  nvarchar(300) = QUOTENAME(@schema) + N'.' + QUOTENAME(N'dbr_audit_' + @table + N'_' + @op);
    DECLARE @evt  nvarchar(10)  = CASE @op WHEN 'I' THEN N'INSERT' WHEN 'U' THEN N'UPDATE' WHEN 'D' THEN N'DELETE' END;
    IF @evt IS NULL BEGIN RAISERROR(N'op must be I, U or D', 16, 1); RETURN; END;
    DECLARE @lit  nvarchar(400) = N'N''' + REPLACE(@full, N'''', N'''''') + N'''';
    DECLARE @img  nvarchar(max) = N'
    INSERT audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, ''{KIND}'', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
           (SELECT x.Id AS Id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
           CASE WHEN @dbr_mode = ''KEYS'' THEN NULL
                ELSE (SELECT x.Id AS Id, x.Ref AS Ref, x.Ref2 AS Ref2, x.Src AS Src FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM {PSEUDO} AS x;';
    DECLARE @del nvarchar(max) = REPLACE(REPLACE(@img, N'{KIND}', N'DELETED'),  N'{PSEUDO}', N'deleted');
    DECLARE @ins nvarchar(max) = REPLACE(REPLACE(@img, N'{KIND}', N'INSERTED'), N'{PSEUDO}', N'inserted');
    DECLARE @sql nvarchar(max) = N'CREATE TRIGGER ' + @trg + N' ON ' + @full + N' AFTER ' + @evt + N' AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, ''FULL'');
    INSERT audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, OBJECT_ID(' + @lit + N'), ' + @lit + N', ''' + @op + N''',
           (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;'
      + CASE @op WHEN 'I' THEN @ins WHEN 'D' THEN @del ELSE @del + @ins END + N'
END';
    EXEC sys.sp_executesql @sql;
    IF NOT EXISTS (SELECT 1 FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
                   WHERE t.parent_id = OBJECT_ID(@full) AND e.type_desc = @evt AND e.is_last = 1)
        EXEC sys.sp_settriggerorder @triggername = @trg, @order = N'Last', @stmttype = @evt;
END
GO
/* 감사 트리거 순서 조회(모든 스파이크 공용) */
CREATE OR ALTER VIEW dbo.spike_AuditOrder
AS
SELECT OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS tableName,
       t.name AS triggerName, e.type_desc AS evt, e.is_first, e.is_last,
       CASE WHEN t.name LIKE N'dbr[_]audit[_]%' THEN CASE WHEN e.is_last = 1 THEN 'LAST' ELSE 'NONE' END END AS auditOrder
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
WHERE t.parent_class = 1;
GO
