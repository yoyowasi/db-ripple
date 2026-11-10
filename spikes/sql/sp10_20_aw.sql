/* SP10 AW 판 — AW_SpikeB(이 스파이크 전용 AW 복사본)에서 DML 전용 사용자(EXECUTE AS USER)와 dbo의 결과를 비교한다.
   문장: UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 (1행)
     → iduSalesOrderDetail: Production.TransactionHistory INSERT, Person.Person UPDATE 2회(형식화 xml .modify, → iuPerson),
       Sales.SalesOrderHeader UPDATE(→ uSalesOrderHeader: SOH RevisionNumber, dbo.ufnGetAccounting*(), SalesPerson·SalesTerritory 0행)
   실행: R1 dbo, R2 spk_dml(감사 없음) → [common_audit_store.sql] → 감사 트리거 설치(자동 커밋) → R3 dbo, R4 spk_dml → 정리.
         실행마다 BEGIN TRAN … ROLLBACK. 사용자 문장은 모듈 밖 ad hoc 배치에서 실행한다(모듈 안이면 소유권 체인이 섞임).
   spk_dml: WITHOUT LOGIN, SCHEMA::Sales에 SELECT·INSERT·UPDATE·DELETE만(Production·Person·dbo·audit·xml 스키마 컬렉션 권한 없음).
   감사: 계약 저장소(common_audit_store.sql) + 생성기(C9 TriggerSql·SelectItems) 본문 모양의 손 감사 트리거를 관련 테이블 6개 I/U/D에 설치(Last).
         common_hand_audit.sql의 spike_InstallAudit는 미니 테이블 모양(Id, Ref, Ref2, Src) 전제라 AW에 쓸 수 없어 같은 본문을 sys.columns로 만든다.
   AW 사용자 트리거 본문·순서는 바꾸지 않는다. 끝에서 감사 객체·측정 프로시저·사용자를 지운다.
   순서: -Part 1 (-Database AW_SpikeB) → common_audit_store.sql (-Database AW_SpikeB) → -Part 2 (-Database AW_SpikeB) */
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
USE AW_SpikeB;
GO
/* 0. 이전 실행 잔여물 정리(감사 트리거·저장소·측정 프로시저·사용자) */
IF XACT_STATE() <> 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
DECLARE @drop nvarchar(max) = N'';
SELECT @drop += N'DROP TRIGGER ' + QUOTENAME(OBJECT_SCHEMA_NAME(object_id)) + N'.' + QUOTENAME(name) + N'; '
FROM sys.triggers WHERE parent_class = 1 AND name LIKE N'dbr[_]audit[_]%';
EXEC (@drop);
IF OBJECT_ID(N'audit.AuditRowImage', N'U') IS NOT NULL DROP TABLE audit.AuditRowImage;
IF OBJECT_ID(N'audit.AuditHeader', N'U') IS NOT NULL DROP TABLE audit.AuditHeader;
IF OBJECT_ID(N'audit.AuditControl', N'U') IS NOT NULL DROP TABLE audit.AuditControl;
IF OBJECT_ID(N'audit.CaptureSeq', N'SO') IS NOT NULL DROP SEQUENCE audit.CaptureSeq;
IF SCHEMA_ID(N'audit') IS NOT NULL DROP SCHEMA audit;
IF OBJECT_ID(N'dbo.spk10_Measure', N'P') IS NOT NULL DROP PROCEDURE dbo.spk10_Measure;
IF USER_ID(N'spk_dml') IS NOT NULL DROP USER spk_dml;
GO
CREATE USER spk_dml WITHOUT LOGIN;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::Sales TO spk_dml;
GO
/* 측정(도구 계정 dbo로 REVERT 뒤 호출): 대상 행·연쇄 행 값, ErrorLog, 전 테이블 지문(행 수/BINARY_CHECKSUM/xml 해시), 감사 요약 */
CREATE PROCEDURE dbo.spk10_Measure @o int, @d int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @person int = (SELECT c.PersonID FROM Sales.SalesOrderHeader AS h JOIN Sales.Customer AS c ON c.CustomerID = h.CustomerID WHERE h.SalesOrderID = @o);
    SELECT v.metric, v.value FROM (VALUES
        (N'SOD.OrderQty',  (SELECT CONVERT(nvarchar(400), OrderQty) FROM Sales.SalesOrderDetail WHERE SalesOrderID = @o AND SalesOrderDetailID = @d)),
        (N'SOD.LineTotal', (SELECT CONVERT(nvarchar(400), LineTotal) FROM Sales.SalesOrderDetail WHERE SalesOrderID = @o AND SalesOrderDetailID = @d)),
        (N'TH.rows(ref)',  (SELECT CONVERT(nvarchar(400), COUNT(*)) FROM Production.TransactionHistory WHERE ReferenceOrderID = @o AND ReferenceOrderLineID = @d)),
        (N'Person.TotalPurchaseYTD', (SELECT CONVERT(nvarchar(400), Demographics.value(N'declare default element namespace "http://schemas.microsoft.com/sqlserver/2004/07/adventure-works/IndividualSurvey"; (/IndividualSurvey/TotalPurchaseYTD)[1]', 'money'))
                                     FROM Person.Person WHERE BusinessEntityID = @person)),
        (N'SOH.SubTotal',  (SELECT CONVERT(nvarchar(400), SubTotal) FROM Sales.SalesOrderHeader WHERE SalesOrderID = @o)),
        (N'SOH.RevisionNumber', (SELECT CONVERT(nvarchar(400), RevisionNumber) FROM Sales.SalesOrderHeader WHERE SalesOrderID = @o)),
        (N'SalesPerson.SalesYTD', (SELECT CONVERT(nvarchar(400), sp.SalesYTD) FROM Sales.SalesPerson AS sp JOIN Sales.SalesOrderHeader AS h ON h.SalesPersonID = sp.BusinessEntityID WHERE h.SalesOrderID = @o)),
        (N'SalesTerritory.SalesYTD', (SELECT CONVERT(nvarchar(400), st.SalesYTD) FROM Sales.SalesTerritory AS st JOIN Sales.SalesOrderHeader AS h ON h.TerritoryID = st.TerritoryID WHERE h.SalesOrderID = @o)),
        (N'ErrorLog.rows', (SELECT CONVERT(nvarchar(400), COUNT(*)) FROM dbo.ErrorLog))
    ) AS v (metric, value);
    /* 전 테이블 지문(audit 스키마 제외). xml 열은 BINARY_CHECKSUM이 무시하므로 해시를 따로 더한다 */
    DECLARE @fp nvarchar(max);
    SELECT @fp = STRING_AGG(CONVERT(nvarchar(max), N'SELECT N''fp:' + REPLACE(QUOTENAME(s.name) + N'.' + QUOTENAME(t.name), N'''', N'''''') + N''' AS metric, CONCAT(COUNT_BIG(*), N''/'', CHECKSUM_AGG(BINARY_CHECKSUM(*)), N''/'', '
                   + ISNULL(x.expr, N'N''-''') + N') AS value FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)), N' UNION ALL ')
    FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id
    OUTER APPLY (SELECT N'CHECKSUM_AGG(CHECKSUM(' + STRING_AGG(CONVERT(nvarchar(max), N'HASHBYTES(''SHA2_256'', CONVERT(nvarchar(max), ' + QUOTENAME(c.name) + N'))'), N', ') + N'))' AS expr
                 FROM sys.columns AS c WHERE c.object_id = t.object_id AND c.system_type_id = 241 HAVING COUNT(*) > 0) AS x
    WHERE t.is_ms_shipped = 0 AND s.name <> N'audit';
    EXEC sys.sp_executesql @fp;
    /* 감사 요약(설치돼 있을 때). 실행마다 ROLLBACK하므로 AuditHeader에는 이번 실행 행만 있다 */
    IF OBJECT_ID(N'audit.AuditHeader', N'U') IS NOT NULL
    BEGIN
        EXEC sys.sp_executesql N'
        SELECT N''audit:'' + tableName + N'':'' + operation AS metric,
               CONCAT(N''firings='', COUNT(*), N'' ins='', SUM(insertedCount), N'' del='', SUM(deletedCount),
                      N'' nest='', STRING_AGG(CONVERT(nvarchar(10), nestLevel), N'','') WITHIN GROUP (ORDER BY captureSeq), N'' tran='', MIN(tranCount)) AS value
        FROM audit.AuditHeader GROUP BY tableName, operation
        UNION ALL
        SELECT N''audit:order'', STRING_AGG(CONVERT(nvarchar(max), tableName + N'' '' + operation + N''('' + CONVERT(nvarchar(10), nestLevel) + N'')''), N'' > '') WITHIN GROUP (ORDER BY captureSeq)
        FROM audit.AuditHeader
        UNION ALL
        SELECT N''audit:images'', CONCAT(COUNT(*), N'' rows, validJson='', SUM(CASE WHEN ISJSON(rowJson) = 1 THEN 1 ELSE 0 END)) FROM audit.AuditRowImage;';
    END
END;
GO
/* 대상 행: 영업 사원이 있는 주문 + 고객 개인의 Demographics에 TotalPurchaseYTD가 있음(없으면 개인 고객 주문) */
IF OBJECT_ID(N'tempdb..#target') IS NOT NULL DROP TABLE #target;
IF OBJECT_ID(N'tempdb..#res') IS NOT NULL DROP TABLE #res;
CREATE TABLE #target (o int NOT NULL, d int NOT NULL);
CREATE TABLE #res (run varchar(8) NOT NULL, metric nvarchar(300) NOT NULL, value nvarchar(400) NULL);
INSERT #target (o, d)
SELECT TOP (1) h.SalesOrderID, sd.SalesOrderDetailID
FROM Sales.SalesOrderHeader AS h
JOIN Sales.Customer AS c ON c.CustomerID = h.CustomerID
JOIN Person.Person AS p ON p.BusinessEntityID = c.PersonID
CROSS APPLY (SELECT TOP (1) x.SalesOrderDetailID FROM Sales.SalesOrderDetail AS x WHERE x.SalesOrderID = h.SalesOrderID ORDER BY x.SalesOrderDetailID) AS sd
WHERE p.Demographics.exist(N'declare default element namespace "http://schemas.microsoft.com/sqlserver/2004/07/adventure-works/IndividualSurvey"; /IndividualSurvey/TotalPurchaseYTD') = 1
ORDER BY CASE WHEN h.SalesPersonID IS NOT NULL THEN 0 ELSE 1 END, h.SalesOrderID;
SELECT 'target' AS part, t.o AS salesOrderId, t.d AS salesOrderDetailId, h.CustomerID, c.PersonID, h.SalesPersonID, h.TerritoryID, h.OrderDate
FROM #target AS t JOIN Sales.SalesOrderHeader AS h ON h.SalesOrderID = t.o JOIN Sales.Customer AS c ON c.CustomerID = h.CustomerID;
/* spk_dml의 직접 권한(연쇄 대상에는 권한이 없어야 실험이 의미 있다) */
EXECUTE AS USER = N'spk_dml';
SELECT 'perm_spk_dml' AS part, USER_NAME() AS userName,
       HAS_PERMS_BY_NAME(N'Sales.SalesOrderDetail', N'OBJECT', N'UPDATE') AS sod_update,
       HAS_PERMS_BY_NAME(N'Production.TransactionHistory', N'OBJECT', N'INSERT') AS th_insert,
       HAS_PERMS_BY_NAME(N'Person.Person', N'OBJECT', N'UPDATE') AS person_update,
       HAS_PERMS_BY_NAME(N'Person.IndividualSurveySchemaCollection', N'XML SCHEMA COLLECTION', N'EXECUTE') AS xsc_execute,
       HAS_PERMS_BY_NAME(N'dbo.ufnGetAccountingStartDate', N'OBJECT', N'EXECUTE') AS ufn_execute,
       HAS_PERMS_BY_NAME(N'dbo.uspLogError', N'OBJECT', N'EXECUTE') AS usplogerror_execute;
REVERT;
DECLARE @m TABLE (metric nvarchar(300), value nvarchar(400));
DECLARE @o int, @d int;
SELECT @o = o, @d = d FROM #target;
INSERT @m EXEC dbo.spk10_Measure @o, @d;
INSERT #res (run, metric, value) SELECT 'base', metric, value FROM @m;
SELECT 'base_values' AS part, metric, value FROM @m WHERE metric NOT LIKE N'fp:%';
GO
/* R1: dbo, 감사 없음 */
IF XACT_STATE() <> 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
DECLARE @run varchar(8) = 'R1', @asUser bit = 0;
DECLARE @o int, @d int; SELECT @o = o, @d = d FROM #target;
DECLARE @m TABLE (metric nvarchar(300), value nvarchar(400));
DECLARE @rc int = NULL, @err int = NULL, @msg nvarchar(400) = NULL, @xs int = NULL, @tc int = NULL, @usr sysname = NULL, @rvErr int = NULL;
BEGIN TRAN;
IF @asUser = 1 EXECUTE AS USER = N'spk_dml';
SET @usr = USER_NAME();
BEGIN TRY
    UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderID = @o AND SalesOrderDetailID = @d;
    SET @rc = @@ROWCOUNT;
END TRY
BEGIN CATCH
    SELECT @err = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 400);
END CATCH
SELECT @xs = XACT_STATE(), @tc = @@TRANCOUNT;
IF USER_NAME() <> N'dbo' BEGIN BEGIN TRY REVERT; END TRY BEGIN CATCH SET @rvErr = ERROR_NUMBER(); END CATCH END
IF XACT_STATE() = -1 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
INSERT @m EXEC dbo.spk10_Measure @o, @d;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT #res (run, metric, value) SELECT @run, metric, value FROM @m;
INSERT #res (run, metric, value) VALUES (@run, N'stmt.user', @usr), (@run, N'stmt.@@ROWCOUNT', CONVERT(nvarchar(20), @rc)),
    (@run, N'stmt.errNo', CONVERT(nvarchar(20), @err)), (@run, N'stmt.errMsg', @msg), (@run, N'stmt.xactState', CONVERT(nvarchar(5), @xs)),
    (@run, N'stmt.tranCount', CONVERT(nvarchar(5), @tc)), (@run, N'revert.errNo', CONVERT(nvarchar(20), @rvErr));
GO
/* R2: spk_dml, 감사 없음 */
IF XACT_STATE() <> 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
DECLARE @run varchar(8) = 'R2', @asUser bit = 1;
DECLARE @o int, @d int; SELECT @o = o, @d = d FROM #target;
DECLARE @m TABLE (metric nvarchar(300), value nvarchar(400));
DECLARE @rc int = NULL, @err int = NULL, @msg nvarchar(400) = NULL, @xs int = NULL, @tc int = NULL, @usr sysname = NULL, @rvErr int = NULL;
BEGIN TRAN;
IF @asUser = 1 EXECUTE AS USER = N'spk_dml';
SET @usr = USER_NAME();
BEGIN TRY
    UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderID = @o AND SalesOrderDetailID = @d;
    SET @rc = @@ROWCOUNT;
END TRY
BEGIN CATCH
    SELECT @err = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 400);
END CATCH
SELECT @xs = XACT_STATE(), @tc = @@TRANCOUNT;
IF USER_NAME() <> N'dbo' BEGIN BEGIN TRY REVERT; END TRY BEGIN CATCH SET @rvErr = ERROR_NUMBER(); END CATCH END
IF XACT_STATE() = -1 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
INSERT @m EXEC dbo.spk10_Measure @o, @d;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT #res (run, metric, value) SELECT @run, metric, value FROM @m;
INSERT #res (run, metric, value) VALUES (@run, N'stmt.user', @usr), (@run, N'stmt.@@ROWCOUNT', CONVERT(nvarchar(20), @rc)),
    (@run, N'stmt.errNo', CONVERT(nvarchar(20), @err)), (@run, N'stmt.errMsg', @msg), (@run, N'stmt.xactState', CONVERT(nvarchar(5), @xs)),
    (@run, N'stmt.tranCount', CONVERT(nvarchar(5), @tc)), (@run, N'revert.errNo', CONVERT(nvarchar(20), @rvErr));
GO
/* 1부 결과(감사 없음). 비교 규칙: 값은 정확히 같아야 SAME. 지문은 정확히 같아야 SAME이고,
   TransactionHistory만 IDENTITY·GETDATE() 열 때문에 실행마다 달라지므로 '변경 여부와 행 수'로 비교한다 */
IF OBJECT_ID(N'tempdb..#cmp') IS NOT NULL DROP TABLE #cmp;
SELECT k.metric, b.value AS base, p.value AS pv, q.value AS qv,
       CASE WHEN k.metric = N'stmt.user' THEN 'n/a'
            WHEN k.metric = N'fp:[Production].[TransactionHistory]'
                 THEN CASE WHEN CASE WHEN p.value = b.value THEN 0 ELSE 1 END = CASE WHEN q.value = b.value THEN 0 ELSE 1 END
                                AND LEFT(p.value, CHARINDEX(N'/', p.value)) = LEFT(q.value, CHARINDEX(N'/', q.value)) THEN N'SAME(변경 여부·행 수)' ELSE 'DIFF' END
            WHEN ISNULL(p.value, N'<null>') = ISNULL(q.value, N'<null>') THEN 'SAME' ELSE 'DIFF' END AS cmp
INTO #cmp
FROM (SELECT DISTINCT metric FROM #res) AS k
LEFT JOIN #res AS b ON b.metric = k.metric AND b.run = 'base'
LEFT JOIN #res AS p ON p.metric = k.metric AND p.run = 'R1'
LEFT JOIN #res AS q ON q.metric = k.metric AND q.run = 'R2';
SELECT 'cmp_no_audit' AS part, metric, base, pv AS R1_dbo, qv AS R2_spk_dml, cmp AS r1_vs_r2
FROM #cmp WHERE metric NOT LIKE N'fp:%' OR ISNULL(pv, N'') <> ISNULL(base, N'') OR ISNULL(qv, N'') <> ISNULL(base, N'') OR cmp = 'DIFF'
ORDER BY metric;
SELECT 'verdict_no_audit' AS part, 'aw_dml_user_same_as_dbo' AS chk,
       CASE WHEN NOT EXISTS (SELECT 1 FROM #cmp WHERE cmp = 'DIFF') THEN 'PASS' ELSE 'FAIL' END AS verdict,
       (SELECT COUNT(*) FROM #cmp WHERE metric LIKE N'fp:%' AND ISNULL(pv, N'') <> ISNULL(base, N'')) AS changedTables_R1,
       (SELECT COUNT(*) FROM #cmp WHERE metric LIKE N'fp:%' AND ISNULL(qv, N'') <> ISNULL(base, N'')) AS changedTables_R2;
GO
-- PART 2 (common_audit_store.sql을 AW_SpikeB에 설치한 뒤. 같은 세션이 아니므로 #res는 1부와 이어지지 않는다 → 기준·R1·R2를 다시 잰다)
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
USE AW_SpikeB;
GO
IF XACT_STATE() <> 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
/* 손 감사 트리거: 생성기 TriggerSql·SelectItems 모양(키 = PK, text/ntext/image 제외, xml→CONVERT, hierarchyid→ToString, 공간→STAsText+@srid) */
DECLARE @targets TABLE (sch sysname, tbl sysname);
INSERT @targets VALUES (N'Sales', N'SalesOrderDetail'), (N'Production', N'TransactionHistory'), (N'Person', N'Person'),
                       (N'Sales', N'SalesOrderHeader'), (N'Sales', N'SalesPerson'), (N'Sales', N'SalesTerritory');
DECLARE @ops TABLE (op char(1), evt nvarchar(10));
INSERT @ops VALUES ('I', N'INSERT'), ('U', N'UPDATE'), ('D', N'DELETE');
DECLARE @sch sysname, @tbl sysname, @op char(1), @evt nvarchar(10), @full nvarchar(300), @trg nvarchar(300), @oid int, @sql nvarchar(max), @img nvarchar(max), @keyItems nvarchar(max), @rowItems nvarchar(max);
DECLARE ci CURSOR LOCAL FAST_FORWARD FOR SELECT t.sch, t.tbl, o.op, o.evt FROM @targets AS t CROSS JOIN @ops AS o;
OPEN ci; FETCH NEXT FROM ci INTO @sch, @tbl, @op, @evt;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @full = QUOTENAME(@sch) + N'.' + QUOTENAME(@tbl);
    SET @trg = QUOTENAME(@sch) + N'.' + QUOTENAME(N'dbr_audit_' + @tbl + N'_' + @op);
    SET @oid = OBJECT_ID(@full);
    SELECT @keyItems = STRING_AGG(CONVERT(nvarchar(max), N'{A}.' + QUOTENAME(c.name) + N' AS ' + QUOTENAME(c.name)), N', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
    FROM sys.indexes AS i JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = @oid AND i.is_primary_key = 1;
    SELECT @rowItems = STRING_AGG(CONVERT(nvarchar(max),
             CASE WHEN c.system_type_id = 241 THEN N'CONVERT(nvarchar(max), {A}.' + QUOTENAME(c.name) + N') AS ' + QUOTENAME(c.name)
                  WHEN ty.name = N'hierarchyid' THEN N'{A}.' + QUOTENAME(c.name) + N'.ToString() AS ' + QUOTENAME(c.name)
                  WHEN ty.name IN (N'geography', N'geometry') THEN N'{A}.' + QUOTENAME(c.name) + N'.STAsText() AS ' + QUOTENAME(c.name) + N', {A}.' + QUOTENAME(c.name) + N'.STSrid AS ' + QUOTENAME(c.name + N'@srid')
                  WHEN ty.is_assembly_type = 1 THEN N'CONVERT(nvarchar(max), {A}.' + QUOTENAME(c.name) + N') AS ' + QUOTENAME(c.name)
                  ELSE N'{A}.' + QUOTENAME(c.name) + N' AS ' + QUOTENAME(c.name) END), N', ') WITHIN GROUP (ORDER BY c.column_id)
    FROM sys.columns AS c JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = @oid AND c.system_type_id NOT IN (34, 35, 99);   /* image, text, ntext 제외 */
    SET @img = N'
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, ''{KIND}'', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT ' + @keyItems + N' FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = ''KEYS'' THEN NULL ELSE (SELECT ' + @rowItems + N' FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM {SRC} AS {A};';
    SET @sql = N'CREATE TRIGGER ' + @trg + N' ON ' + @full + N'
AFTER ' + @evt + N'
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, ''FULL'');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, ' + CONVERT(nvarchar(20), @oid) + N', N''' + REPLACE(@full, N'''', N'''''') + N''', ''' + @op + N''', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;'
        + CASE WHEN @op <> 'I' THEN REPLACE(REPLACE(REPLACE(@img, N'{KIND}', N'DELETED'), N'{SRC}', N'deleted'), N'{A}', N'd') ELSE N'' END
        + CASE WHEN @op <> 'D' THEN REPLACE(REPLACE(REPLACE(@img, N'{KIND}', N'INSERTED'), N'{SRC}', N'inserted'), N'{A}', N'i') ELSE N'' END + N'
END;';
    EXEC sys.sp_executesql @sql;
    IF NOT EXISTS (SELECT 1 FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
                   WHERE t.parent_id = @oid AND e.type_desc = @evt AND e.is_last = 1)
        EXEC sys.sp_settriggerorder @triggername = @trg, @order = N'Last', @stmttype = @evt;
    FETCH NEXT FROM ci INTO @sch, @tbl, @op, @evt;
END
CLOSE ci; DEALLOCATE ci;
SELECT 'audit_installed' AS part, OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS tbl, t.name, e.type_desc, e.is_first, e.is_last, m.uses_quoted_identifier
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id JOIN sys.sql_modules AS m ON m.object_id = t.object_id
WHERE t.name LIKE N'dbr[_]audit[_]%' ORDER BY tbl, t.name;
EXECUTE AS USER = N'spk_dml';
SELECT 'perm_spk_dml_audit' AS part, USER_NAME() AS userName,
       HAS_PERMS_BY_NAME(N'audit.CaptureSeq', N'OBJECT', N'UPDATE') AS seq_update,
       HAS_PERMS_BY_NAME(N'audit.AuditHeader', N'OBJECT', N'INSERT') AS header_insert,
       HAS_PERMS_BY_NAME(N'audit.AuditControl', N'OBJECT', N'SELECT') AS control_select;
REVERT;
GO
/* 기준·대상 다시 준비(2부는 새 세션) */
IF OBJECT_ID(N'tempdb..#target') IS NOT NULL DROP TABLE #target;
IF OBJECT_ID(N'tempdb..#res') IS NOT NULL DROP TABLE #res;
CREATE TABLE #target (o int NOT NULL, d int NOT NULL);
CREATE TABLE #res (run varchar(8) NOT NULL, metric nvarchar(300) NOT NULL, value nvarchar(400) NULL);
INSERT #target (o, d)
SELECT TOP (1) h.SalesOrderID, sd.SalesOrderDetailID
FROM Sales.SalesOrderHeader AS h
JOIN Sales.Customer AS c ON c.CustomerID = h.CustomerID
JOIN Person.Person AS p ON p.BusinessEntityID = c.PersonID
CROSS APPLY (SELECT TOP (1) x.SalesOrderDetailID FROM Sales.SalesOrderDetail AS x WHERE x.SalesOrderID = h.SalesOrderID ORDER BY x.SalesOrderDetailID) AS sd
WHERE p.Demographics.exist(N'declare default element namespace "http://schemas.microsoft.com/sqlserver/2004/07/adventure-works/IndividualSurvey"; /IndividualSurvey/TotalPurchaseYTD') = 1
ORDER BY CASE WHEN h.SalesPersonID IS NOT NULL THEN 0 ELSE 1 END, h.SalesOrderID;
SELECT 'target' AS part, o AS salesOrderId, d AS salesOrderDetailId FROM #target;
DECLARE @m TABLE (metric nvarchar(300), value nvarchar(400));
DECLARE @o int, @d int;
SELECT @o = o, @d = d FROM #target;
INSERT @m EXEC dbo.spk10_Measure @o, @d;
INSERT #res (run, metric, value) SELECT 'base', metric, value FROM @m;
GO
/* R3: dbo, 감사 있음 */
IF XACT_STATE() <> 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
DECLARE @run varchar(8) = 'R3', @asUser bit = 0;
DECLARE @o int, @d int; SELECT @o = o, @d = d FROM #target;
DECLARE @m TABLE (metric nvarchar(300), value nvarchar(400));
DECLARE @rc int = NULL, @err int = NULL, @msg nvarchar(400) = NULL, @xs int = NULL, @tc int = NULL, @usr sysname = NULL, @rvErr int = NULL;
BEGIN TRAN;
IF @asUser = 1 EXECUTE AS USER = N'spk_dml';
SET @usr = USER_NAME();
BEGIN TRY
    UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderID = @o AND SalesOrderDetailID = @d;
    SET @rc = @@ROWCOUNT;
END TRY
BEGIN CATCH
    SELECT @err = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 400);
END CATCH
SELECT @xs = XACT_STATE(), @tc = @@TRANCOUNT;
IF USER_NAME() <> N'dbo' BEGIN BEGIN TRY REVERT; END TRY BEGIN CATCH SET @rvErr = ERROR_NUMBER(); END CATCH END
IF XACT_STATE() = -1 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
INSERT @m EXEC dbo.spk10_Measure @o, @d;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT #res (run, metric, value) SELECT @run, metric, value FROM @m;
INSERT #res (run, metric, value) VALUES (@run, N'stmt.user', @usr), (@run, N'stmt.@@ROWCOUNT', CONVERT(nvarchar(20), @rc)),
    (@run, N'stmt.errNo', CONVERT(nvarchar(20), @err)), (@run, N'stmt.errMsg', @msg), (@run, N'stmt.xactState', CONVERT(nvarchar(5), @xs)),
    (@run, N'stmt.tranCount', CONVERT(nvarchar(5), @tc)), (@run, N'revert.errNo', CONVERT(nvarchar(20), @rvErr));
GO
/* R4: spk_dml, 감사 있음 */
IF XACT_STATE() <> 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
DECLARE @run varchar(8) = 'R4', @asUser bit = 1;
DECLARE @o int, @d int; SELECT @o = o, @d = d FROM #target;
DECLARE @m TABLE (metric nvarchar(300), value nvarchar(400));
DECLARE @rc int = NULL, @err int = NULL, @msg nvarchar(400) = NULL, @xs int = NULL, @tc int = NULL, @usr sysname = NULL, @rvErr int = NULL;
BEGIN TRAN;
IF @asUser = 1 EXECUTE AS USER = N'spk_dml';
SET @usr = USER_NAME();
BEGIN TRY
    UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderID = @o AND SalesOrderDetailID = @d;
    SET @rc = @@ROWCOUNT;
END TRY
BEGIN CATCH
    SELECT @err = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 400);
END CATCH
SELECT @xs = XACT_STATE(), @tc = @@TRANCOUNT;
IF USER_NAME() <> N'dbo' BEGIN BEGIN TRY REVERT; END TRY BEGIN CATCH SET @rvErr = ERROR_NUMBER(); END CATCH END
IF XACT_STATE() = -1 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
INSERT @m EXEC dbo.spk10_Measure @o, @d;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT #res (run, metric, value) SELECT @run, metric, value FROM @m;
INSERT #res (run, metric, value) VALUES (@run, N'stmt.user', @usr), (@run, N'stmt.@@ROWCOUNT', CONVERT(nvarchar(20), @rc)),
    (@run, N'stmt.errNo', CONVERT(nvarchar(20), @err)), (@run, N'stmt.errMsg', @msg), (@run, N'stmt.xactState', CONVERT(nvarchar(5), @xs)),
    (@run, N'stmt.tranCount', CONVERT(nvarchar(5), @tc)), (@run, N'revert.errNo', CONVERT(nvarchar(20), @rvErr));
GO
/* 2부 결과(감사 있음). 비교 규칙은 1부와 같다 */
IF OBJECT_ID(N'tempdb..#cmp') IS NOT NULL DROP TABLE #cmp;
SELECT k.metric, b.value AS base, p.value AS pv, q.value AS qv,
       CASE WHEN k.metric = N'stmt.user' THEN 'n/a'
            WHEN k.metric = N'fp:[Production].[TransactionHistory]'
                 THEN CASE WHEN CASE WHEN p.value = b.value THEN 0 ELSE 1 END = CASE WHEN q.value = b.value THEN 0 ELSE 1 END
                                AND LEFT(p.value, CHARINDEX(N'/', p.value)) = LEFT(q.value, CHARINDEX(N'/', q.value)) THEN N'SAME(변경 여부·행 수)' ELSE 'DIFF' END
            WHEN ISNULL(p.value, N'<null>') = ISNULL(q.value, N'<null>') THEN 'SAME' ELSE 'DIFF' END AS cmp
INTO #cmp
FROM (SELECT DISTINCT metric FROM #res) AS k
LEFT JOIN #res AS b ON b.metric = k.metric AND b.run = 'base'
LEFT JOIN #res AS p ON p.metric = k.metric AND p.run = 'R3'
LEFT JOIN #res AS q ON q.metric = k.metric AND q.run = 'R4';
SELECT 'cmp_audit' AS part, metric, base, pv AS R3_dbo, qv AS R4_spk_dml, cmp AS r3_vs_r4
FROM #cmp WHERE metric NOT LIKE N'fp:%' OR ISNULL(pv, N'') <> ISNULL(base, N'') OR ISNULL(qv, N'') <> ISNULL(base, N'') OR cmp = 'DIFF'
ORDER BY metric;
SELECT 'verdict' AS part, v.chk, v.verdict FROM (VALUES
 ('aw_dml_user_same_as_dbo_with_audit', CASE WHEN NOT EXISTS (SELECT 1 FROM #cmp WHERE cmp = 'DIFF') THEN 'PASS' ELSE 'FAIL' END),
 ('aw_audit_recorded_for_dml_user', CASE WHEN EXISTS (SELECT 1 FROM #cmp WHERE metric LIKE N'audit:%:%' AND qv IS NOT NULL) THEN 'PASS' ELSE 'FAIL' END),
 ('aw_no_error_dml_user', CASE WHEN EXISTS (SELECT 1 FROM #cmp WHERE metric = N'stmt.user' AND qv = N'spk_dml')
                                AND EXISTS (SELECT 1 FROM #cmp WHERE metric = N'stmt.errNo' AND qv IS NULL) THEN 'PASS' ELSE 'FAIL' END)
) AS v (chk, verdict);
GO
/* 정리: 감사 트리거·저장소·측정 프로시저·사용자. 이 실험이 남긴 ErrorLog 행이 있으면 보여 주고 지운다 */
IF XACT_STATE() <> 0 ROLLBACK;
IF USER_NAME() <> N'dbo' REVERT;
SELECT 'errorlog_left' AS part, ErrorLogID, ErrorTime, ErrorNumber, ErrorProcedure, LEFT(ErrorMessage, 200) AS msg FROM dbo.ErrorLog;
DECLARE @drop nvarchar(max) = N'';
SELECT @drop += N'DROP TRIGGER ' + QUOTENAME(OBJECT_SCHEMA_NAME(object_id)) + N'.' + QUOTENAME(name) + N'; '
FROM sys.triggers WHERE parent_class = 1 AND name LIKE N'dbr[_]audit[_]%';
EXEC (@drop);
IF OBJECT_ID(N'audit.AuditRowImage', N'U') IS NOT NULL DROP TABLE audit.AuditRowImage;
IF OBJECT_ID(N'audit.AuditHeader', N'U') IS NOT NULL DROP TABLE audit.AuditHeader;
IF OBJECT_ID(N'audit.AuditControl', N'U') IS NOT NULL DROP TABLE audit.AuditControl;
IF OBJECT_ID(N'audit.CaptureSeq', N'SO') IS NOT NULL DROP SEQUENCE audit.CaptureSeq;
IF SCHEMA_ID(N'audit') IS NOT NULL DROP SCHEMA audit;
IF OBJECT_ID(N'dbo.spk10_Measure', N'P') IS NOT NULL DROP PROCEDURE dbo.spk10_Measure;
IF USER_ID(N'spk_dml') IS NOT NULL DROP USER spk_dml;
SELECT 'cleanup_check' AS part,
       (SELECT COUNT(*) FROM sys.triggers WHERE name LIKE N'dbr[_]audit[_]%') AS auditTriggers,
       SCHEMA_ID(N'audit') AS auditSchema, USER_ID(N'spk_dml') AS spkUser, OBJECT_ID(N'dbo.spk10_Measure') AS measureProc,
       (SELECT COUNT(*) FROM sys.sql_modules WHERE uses_quoted_identifier = 0) AS modulesQuotedIdentOff;
GO
