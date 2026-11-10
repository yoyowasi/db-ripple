/* SP3 — AW 판(AW_SpikeA): uSalesOrderHeader가 같은 행의 RevisionNumber를 다시 갱신할 때
   (1) 감사 기록 inserted 이미지의 RevisionNumber, (2) 롤백 전 재조회 값, (3) 같은 연결·같은 트랜잭션에서 읽은 스냅샷 대비 최종 차이를 비교한다.
   순서: common_audit_store.sql(-Database AW_SpikeA) → 이 파일(-Database AW_SpikeA).
     1부: 환경 기록 → SOH UPDATE 손 감사 트리거(본문은 sp1_30의 dbr_audit_SalesOrderHeader_U와 같음, 사용자 Last가 없을 때만 Last)
          → 스냅샷 AW_SpikeA_snap(자동 커밋, 데이터 파일마다 경로) → BEGIN TRAN → SOH 1건 Comment UPDATE → 관찰 → ROLLBACK
     2부('-- PART 2' 이후, 실패했을 때 단독 재실행 가능): 스냅샷·감사 트리거·감사 저장소 삭제와 확인
   판정 열 verdict는 docs/spikes/SP3.md 기준(sp3_10_mini와 같은 CASE). 사용자 트리거 본문·순서는 바꾸지 않는다. */
SET QUOTED_IDENTIFIER ON;   /* sqlcmd 기본값 OFF. SOH에는 계산 열 인덱스가 있어 OFF면 DML이 1934로 실패한다(SP1 1차 실행) */
SET NOCOUNT ON;
GO
USE AW_SpikeA;
GO
/* 0. 환경: RECURSIVE_TRIGGERS(AW 기본 OFF), nested triggers, 세션 QUOTED_IDENTIFIER */
SELECT d.name AS db, d.is_recursive_triggers_on, d.compatibility_level,
       (SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = N'nested triggers') AS nestedTriggers,
       SESSIONPROPERTY('QUOTED_IDENTIFIER') AS sessionQuotedIdentifier
FROM sys.databases AS d WHERE d.name IN (N'AW_SpikeA', N'AW_Origin') ORDER BY d.name;
IF OBJECT_ID(N'audit.AuditHeader', N'U') IS NULL OR OBJECT_ID(N'audit.AuditRowImage', N'U') IS NULL OR OBJECT_ID(N'audit.CaptureSeq', N'SO') IS NULL
    RAISERROR(N'감사 저장소 없음: common_audit_store.sql을 -Database AW_SpikeA로 먼저 실행', 16, 1);
/* uSalesOrderHeader의 RevisionNumber 갱신 부분(재귀 가드가 없음을 로그로 남긴다) */
DECLARE @def nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'Sales.uSalesOrderHeader'));
SELECT CONVERT(nvarchar(4000), SUBSTRING(@def, CHARINDEX(N'IF NOT UPDATE', @def), 330)) AS uSalesOrderHeader_revisionPart;
GO
/* 1. 손 감사 트리거(SOH UPDATE) — sp1_30_aw_soh_delete.sql의 dbr_audit_SalesOrderHeader_U와 같은 본문 */
IF OBJECT_ID(N'Sales.dbr_audit_SalesOrderHeader_U', N'TR') IS NOT NULL DROP TRIGGER Sales.dbr_audit_SalesOrderHeader_U;
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
IF NOT EXISTS (SELECT 1 FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
               WHERE t.parent_id = OBJECT_ID(N'Sales.SalesOrderHeader') AND e.type_desc = N'UPDATE' AND e.is_last = 1)
    EXEC sys.sp_settriggerorder @triggername = N'[Sales].[dbr_audit_SalesOrderHeader_U]', @order = N'Last', @stmttype = N'UPDATE';
SELECT t.name AS triggerName, e.type_desc AS evt, e.is_first, e.is_last, t.is_disabled,
       OBJECTPROPERTY(t.object_id, 'ExecIsQuotedIdentOn') AS quotedIdentOn
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
WHERE t.parent_id = OBJECT_ID(N'Sales.SalesOrderHeader') ORDER BY t.name, e.type_desc;
GO
/* 2. 스냅샷(트랜잭션 밖, 원본 데이터 파일마다 논리 이름·스파스 파일 경로 지정) */
USE master;
GO
IF DB_ID(N'AW_SpikeA_snap') IS NOT NULL DROP DATABASE AW_SpikeA_snap;
GO
DECLARE @files nvarchar(max) = (
    SELECT STRING_AGG(CONVERT(nvarchar(max), N'(NAME = ' + QUOTENAME(f.name) + N', FILENAME = N''/var/opt/mssql/data/AW_SpikeA_snap_' + f.name + N'.ss'')'), N', ')
    FROM AW_SpikeA.sys.database_files AS f WHERE f.type = 0);
DECLARE @sql nvarchar(max) = N'CREATE DATABASE AW_SpikeA_snap ON ' + @files + N' AS SNAPSHOT OF AW_SpikeA;';
SELECT @sql AS snapshotDdl;
EXEC sys.sp_executesql @sql;
GO
SELECT name, DB_NAME(source_database_id) AS sourceDb, state_desc, is_read_only FROM sys.databases WHERE name = N'AW_SpikeA_snap';
GO
USE AW_SpikeA;
GO
/* 3. 실행과 관찰: 같은 연결·같은 트랜잭션에서 감사 기록 → 재조회 → 스냅샷 대비 최종 차이, 그 뒤 ROLLBACK */
DECLARE @id int = (SELECT MIN(SalesOrderID) FROM Sales.SalesOrderHeader);
DECLARE @hdr TABLE (captureSeq bigint, nestLevel int, insertedCount bigint, deletedCount bigint, tranCount int,
                    beforeRev int, beforeComment nvarchar(128), afterRev int, afterComment nvarchar(128));
DECLARE @reread TABLE (RevisionNumber int, Comment nvarchar(128), ModifiedDate datetime);
DECLARE @snap   TABLE (RevisionNumber int, Comment nvarchar(128), ModifiedDate datetime);
DECLARE @b bigint, @rc int, @err int = NULL, @tcAtRead int, @netCur int, @netSnap int, @netOther int;
BEGIN TRAN;
SET @b = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
BEGIN TRY
    UPDATE Sales.SalesOrderHeader SET Comment = N'sp3' WHERE SalesOrderID = @id;
    SET @rc = @@ROWCOUNT;
END TRY
BEGIN CATCH
    SET @err = ERROR_NUMBER();
    SELECT ERROR_NUMBER() AS errorNumber, ERROR_MESSAGE() AS errorMessage;
END CATCH
IF XACT_STATE() = 1
BEGIN
    /* (1) 감사 기록: 묶음마다 deleted/inserted 이미지의 RevisionNumber */
    INSERT @hdr
    SELECT h.captureSeq, h.nestLevel, h.insertedCount, h.deletedCount, h.tranCount,
           (SELECT MAX(CONVERT(int, JSON_VALUE(r.rowJson, '$.RevisionNumber'))) FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq AND r.imageKind = 'DELETED'),
           (SELECT MAX(JSON_VALUE(r.rowJson, '$.Comment')) FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq AND r.imageKind = 'DELETED'),
           (SELECT MAX(CONVERT(int, JSON_VALUE(r.rowJson, '$.RevisionNumber'))) FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq AND r.imageKind = 'INSERTED'),
           (SELECT MAX(JSON_VALUE(r.rowJson, '$.Comment')) FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq AND r.imageKind = 'INSERTED')
    FROM audit.AuditHeader AS h WHERE h.captureSeq > @b;
    /* (2) 롤백 전 재조회 */
    INSERT @reread SELECT RevisionNumber, Comment, ModifiedDate FROM Sales.SalesOrderHeader WHERE SalesOrderID = @id;
    /* (3) 스냅샷(기준) 값과 테이블 전체 최종 차이(행 단위 EXCEPT 양방향) */
    INSERT @snap SELECT RevisionNumber, Comment, ModifiedDate FROM AW_SpikeA_snap.Sales.SalesOrderHeader WHERE SalesOrderID = @id;
    SET @tcAtRead = @@TRANCOUNT;
    SELECT @netCur = COUNT(*) FROM (SELECT * FROM Sales.SalesOrderHeader EXCEPT SELECT * FROM AW_SpikeA_snap.Sales.SalesOrderHeader) AS x;
    SELECT @netSnap = COUNT(*) FROM (SELECT * FROM AW_SpikeA_snap.Sales.SalesOrderHeader EXCEPT SELECT * FROM Sales.SalesOrderHeader) AS x;
    SELECT @netOther = COUNT(*) FROM (SELECT * FROM Sales.SalesOrderHeader EXCEPT SELECT * FROM AW_SpikeA_snap.Sales.SalesOrderHeader) AS x WHERE x.SalesOrderID <> @id;
END
ROLLBACK;
SELECT 'AW' AS mode_, @id AS salesOrderId, @rc AS rowsAffected, @err AS errorNumber, @tcAtRead AS tranCountAtSnapshotRead, @@TRANCOUNT AS tranCountAfterRollback;
SELECT 'AW' AS mode_, ROW_NUMBER() OVER (ORDER BY captureSeq) AS pos, * FROM @hdr ORDER BY captureSeq;
SELECT N'snapshot(기준)' AS src, RevisionNumber, Comment, ModifiedDate FROM @snap
UNION ALL SELECT N'reread(롤백 전)', RevisionNumber, Comment, ModifiedDate FROM @reread;
DECLARE @lastAuditRev int   = (SELECT TOP (1) afterRev FROM @hdr ORDER BY captureSeq DESC);
DECLARE @nestedAuditRev int = (SELECT TOP (1) afterRev FROM @hdr WHERE nestLevel >= 2 ORDER BY captureSeq DESC);
DECLARE @rereadRev int = (SELECT RevisionNumber FROM @reread), @snapRev int = (SELECT RevisionNumber FROM @snap);
SELECT 'AW' AS mode_,
       (SELECT COUNT(*) FROM @hdr) AS auditHeaders,
       (SELECT COUNT(*) FROM @hdr WHERE nestLevel >= 2) AS nestedHeaders,
       @snapRev AS snapshotRev, @lastAuditRev AS lastAuditAfterRev, @nestedAuditRev AS nestedAuditAfterRev, @rereadRev AS rereadRev,
       CONCAT(@snapRev, '->', @rereadRev) AS finalDiffRev,
       @netCur AS netDiffRowsCurExceptSnap, @netSnap AS netDiffRowsSnapExceptCur, @netOther AS netDiffRowsOtherKeys,
       CASE WHEN @err IS NOT NULL THEN 'ERROR'
            WHEN EXISTS (SELECT 1 FROM @hdr WHERE nestLevel >= 2) THEN 'CAPTURED'
            WHEN @rereadRev <> @lastAuditRev THEN 'RESIDUAL_BY_REREAD'
            WHEN @netOther > 0 THEN 'NETDIFF_ONLY'
            ELSE 'MISSED' END AS verdict;
/* 롤백 뒤 원래 값으로 돌아왔는지 */
SELECT (SELECT RevisionNumber FROM Sales.SalesOrderHeader WHERE SalesOrderID = @id) AS revAfterRollback,
       (SELECT Comment FROM Sales.SalesOrderHeader WHERE SalesOrderID = @id) AS commentAfterRollback,
       @snapRev AS snapshotRev,
       (SELECT COUNT(*) FROM audit.AuditHeader) AS auditHeadersAfterRollback;
GO
-- PART 2 정리(1부가 중간에 실패했을 때 -Part 2로 단독 실행 가능)
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO
USE master;
GO
IF DB_ID(N'AW_SpikeA_snap') IS NOT NULL DROP DATABASE AW_SpikeA_snap;
GO
USE AW_SpikeA;
GO
IF OBJECT_ID(N'Sales.dbr_audit_SalesOrderHeader_U', N'TR') IS NOT NULL DROP TRIGGER Sales.dbr_audit_SalesOrderHeader_U;
IF OBJECT_ID(N'audit.AuditRowImage', N'U') IS NOT NULL DROP TABLE audit.AuditRowImage;
IF OBJECT_ID(N'audit.AuditHeader', N'U') IS NOT NULL DROP TABLE audit.AuditHeader;
IF OBJECT_ID(N'audit.AuditControl', N'U') IS NOT NULL DROP TABLE audit.AuditControl;
IF OBJECT_ID(N'audit.CaptureSeq', N'SO') IS NOT NULL DROP SEQUENCE audit.CaptureSeq;
GO
IF SCHEMA_ID(N'audit') IS NOT NULL DROP SCHEMA audit;
GO
SELECT DB_ID(N'AW_SpikeA_snap') AS snapshotDbId_null_expected, SCHEMA_ID(N'audit') AS auditSchema_null_expected,
       (SELECT COUNT(*) FROM sys.triggers WHERE name LIKE N'dbr[_]audit[_]%') AS auditTriggers_0_expected,
       (SELECT d.is_recursive_triggers_on FROM sys.databases AS d WHERE d.name = DB_NAME()) AS recursiveTriggersOn;
SELECT t.name AS triggerName, e.type_desc AS evt, e.is_first, e.is_last
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
WHERE t.parent_id = OBJECT_ID(N'Sales.SalesOrderHeader');
GO
