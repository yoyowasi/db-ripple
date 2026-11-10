SELECT SESSIONPROPERTY('QUOTED_IDENTIFIER') AS sessionQI;
GO
-- 모든 감사 대상 테이블에서 감사 트리거를 실제로 발동해 본다(테이블마다 BEGIN TRAN … ROLLBACK).
--  A. 0행 INSERT/UPDATE/DELETE: 직접 대상 0행 발동 → 트리거 본문 컴파일·실행, 헤더 operation·건수 확인
--  B. 1행 UPDATE(SET c = c): 실제 값으로 rowJson 생성, DML 직후 @@ROWCOUNT 확인
-- 결과는 테이블 변수에 모은다(ROLLBACK 영향 없음).
SET NOCOUNT ON;
SET XACT_ABORT OFF;
DECLARE @res TABLE (tbl nvarchar(300), phase char(1), hdrI int, hdrU int, hdrD int, zeroRowHdr int, images int, imagesWithRow int, badJson int, rc bigint NULL, err nvarchar(2000) NULL);
DECLARE @id int, @q nvarchar(300), @cols nvarchar(max), @upd sysname, @sql nvarchar(max), @before bigint, @rc bigint;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.object_id, QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
    FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id
    WHERE t.is_ms_shipped = 0 AND s.name <> N'audit'
    ORDER BY s.name, t.name;
OPEN c; FETCH NEXT FROM c INTO @id, @q;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @cols = NULL; SET @upd = NULL;
    SELECT @cols = STRING_AGG(CAST(QUOTENAME(name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY column_id)
    FROM sys.columns WHERE object_id = @id AND is_identity = 0 AND is_computed = 0 AND system_type_id <> 189;
    SELECT TOP (1) @upd = name FROM sys.columns
    WHERE object_id = @id AND is_identity = 0 AND is_computed = 0 AND system_type_id <> 189 ORDER BY column_id DESC;

    -- A. 0행 발동
    SELECT @before = ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader;
    BEGIN TRY
        BEGIN TRAN;
        SET @sql = CASE WHEN @cols IS NULL THEN N'' ELSE N'INSERT INTO ' + @q + N' (' + @cols + N') SELECT ' + @cols + N' FROM ' + @q + N' WHERE 1 = 0; ' END
                 + N'UPDATE ' + @q + N' SET ' + QUOTENAME(@upd) + N' = ' + QUOTENAME(@upd) + N' WHERE 1 = 0; '
                 + N'DELETE FROM ' + @q + N' WHERE 1 = 0;';
        EXEC sys.sp_executesql @sql;
        INSERT @res (tbl, phase, hdrI, hdrU, hdrD, zeroRowHdr, images, imagesWithRow, badJson)
        SELECT @q, 'A',
               SUM(CASE WHEN h.operation = 'I' THEN 1 ELSE 0 END), SUM(CASE WHEN h.operation = 'U' THEN 1 ELSE 0 END), SUM(CASE WHEN h.operation = 'D' THEN 1 ELSE 0 END),
               SUM(CASE WHEN h.insertedCount = 0 AND h.deletedCount = 0 THEN 1 ELSE 0 END),
               (SELECT COUNT(*) FROM audit.AuditRowImage WHERE captureSeq > @before), 0, 0
        FROM audit.AuditHeader AS h WHERE h.captureSeq > @before AND h.tableObjectId = @id;
        ROLLBACK;
    END TRY
    BEGIN CATCH
        INSERT @res (tbl, phase, hdrI, hdrU, hdrD, zeroRowHdr, images, imagesWithRow, badJson, err) VALUES (@q, 'A', 0, 0, 0, 0, 0, 0, 0, CONCAT(ERROR_NUMBER(), N' ', ERROR_PROCEDURE(), N': ', ERROR_MESSAGE()));
        IF XACT_STATE() <> 0 ROLLBACK;
    END CATCH;

    -- B. 1행 UPDATE
    SELECT @before = ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader;
    BEGIN TRY
        BEGIN TRAN;
        SET @sql = N'UPDATE TOP (1) ' + @q + N' SET ' + QUOTENAME(@upd) + N' = ' + QUOTENAME(@upd) + N'; SET @rc = @@ROWCOUNT;';
        EXEC sys.sp_executesql @sql, N'@rc bigint OUTPUT', @rc = @rc OUTPUT;
        INSERT @res (tbl, phase, hdrI, hdrU, hdrD, zeroRowHdr, images, imagesWithRow, badJson, rc)
        SELECT @q, 'B',
               (SELECT COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @before AND operation = 'I'),
               (SELECT COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @before AND operation = 'U'),
               (SELECT COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @before AND operation = 'D'),
               (SELECT COUNT(*) FROM audit.AuditHeader WHERE captureSeq > @before AND insertedCount = 0 AND deletedCount = 0),
               (SELECT COUNT(*) FROM audit.AuditRowImage WHERE captureSeq > @before),
               (SELECT COUNT(*) FROM audit.AuditRowImage WHERE captureSeq > @before AND rowJson IS NOT NULL),
               (SELECT COUNT(*) FROM audit.AuditRowImage WHERE captureSeq > @before AND rowJson IS NOT NULL AND ISJSON(rowJson) = 0),
               @rc;
        ROLLBACK;
    END TRY
    BEGIN CATCH
        INSERT @res (tbl, phase, hdrI, hdrU, hdrD, zeroRowHdr, images, imagesWithRow, badJson, err) VALUES (@q, 'B', 0, 0, 0, 0, 0, 0, 0, CONCAT(ERROR_NUMBER(), N' ', ERROR_PROCEDURE(), N': ', ERROR_MESSAGE()));
        IF XACT_STATE() <> 0 ROLLBACK;
    END CATCH;
    FETCH NEXT FROM c INTO @id, @q;
END
CLOSE c; DEALLOCATE c;

SELECT phase, COUNT(*) AS tables, SUM(CASE WHEN err IS NULL THEN 0 ELSE 1 END) AS errors,
       SUM(hdrI) AS hdrI, SUM(hdrU) AS hdrU, SUM(hdrD) AS hdrD, SUM(zeroRowHdr) AS zeroRowHdr,
       SUM(images) AS images, SUM(imagesWithRow) AS imagesWithRow, SUM(badJson) AS badJson,
       SUM(CASE WHEN phase = 'B' AND err IS NULL AND rc <> 1 THEN 1 ELSE 0 END) AS rowcountNot1
FROM @res GROUP BY phase ORDER BY phase;
SELECT tbl, phase, err FROM @res WHERE err IS NOT NULL ORDER BY tbl, phase;
SELECT tbl, hdrI, hdrU, hdrD, zeroRowHdr FROM @res WHERE phase = 'A' AND err IS NULL AND NOT (hdrI = 1 AND hdrU = 1 AND hdrD = 1) ORDER BY tbl;
SELECT tbl, rc, hdrI, hdrU, hdrD, images, imagesWithRow FROM @res WHERE phase = 'B' AND err IS NULL AND (rc <> 1 OR hdrU < 1) ORDER BY tbl;
SELECT COUNT(*) AS errorLogRows FROM dbo.ErrorLog;
