/* SP6 (E)(F) — 제품 생성기(C9 AuditTriggerGenerator·AuditValueExpressions, 2026-11-10 작업 트리) 모양 실측
   (E) TriggerSql과 같은 모양의 감사 트리거 [dbo].[dbr_audit_Types_U](Last)를 만들고 실제 UPDATE로 발동해
       AuditRowImage.rowJson·keyJson을 OPENJSON으로 되읽어 원값과 비교한다. 행 1 경계값, 행 2 NULL, 행 3 정밀도·Z/M·제어 문자.
       생성기와 다른 점: tableObjectId 리터럴 대신 OBJECT_ID(N'[dbo].[Types]')(값 같음). text/ntext/image는 생성기처럼 이미지에서 뺀다.
   (F) 추가 탐침: F1 float·real 원문 자릿수와 되읽기, F2 sql_variant 기본 형식별 FOR JSON(생성기는 변환 없음),
       F3 CLR 형식에 CONVERT(nvarchar(max))(사용자 CLR UDT 경로의 대리 확인 — 사용자 UDT는 서버 CLR 설정 변경이 필요해 범위 밖),
       F4 열 이름의 '.'·'@'와 생성기 키([열@srid]) 충돌.
   순서: 1부(master) → common_audit_store.sql (-Database SpikeLab_SP6) → '-- PART 2' 이후(끝에서 DB 삭제) */
SET QUOTED_IDENTIFIER ON;
USE master;
GO
IF DB_ID(N'SpikeLab_SP6') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP6 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP6; END
GO
CREATE DATABASE SpikeLab_SP6;
GO
-- PART 2 (common_audit_store.sql 설치 뒤)
SET QUOTED_IDENTIFIER ON;   /* 제품 실행 연결(SqlClient) 기본값과 맞춤. sqlcmd 기본은 OFF */
USE SpikeLab_SP6;
GO
CREATE TABLE dbo.Types (
    Id int NOT NULL PRIMARY KEY,
    cXml xml NULL, cHier hierarchyid NULL, cGeog geography NULL, cGeom geometry NULL,
    cMoney money NULL, cSmallMoney smallmoney NULL, cVarbin varbinary(50) NULL, cBin binary(4) NULL, cRv rowversion,
    cDto datetimeoffset(7) NULL, cDt2 datetime2(7) NULL, cTime time(7) NULL, cDec decimal(38,10) NULL,
    cBig bigint NULL, cFloat float NULL, cReal real NULL, cGuid uniqueidentifier NULL, cVariant sql_variant NULL,
    cBit bit NULL, cNvar nvarchar(100) NULL, cText text NULL, cNtext ntext NULL, cImage image NULL);
GO
INSERT dbo.Types (Id, cXml, cHier, cGeog, cGeom, cMoney, cSmallMoney, cVarbin, cBin, cDto, cDt2, cTime, cDec, cBig, cFloat, cReal, cGuid, cVariant, cBit, cNvar, cText, cNtext, cImage)
VALUES (1, N'<a x="1">t&amp;</a>', hierarchyid::Parse(N'/1/2/'), geography::STGeomFromText(N'POINT(126.9780 37.5665)', 4326),
        geometry::STGeomFromText(N'LINESTRING(0 0, 1 1)', 0), 922337203685477.5807, -214748.3648, 0x00FF10, 0xDEADBEEF,
        '2026-11-06T11:20:00.1234567+09:00', '9999-12-31T23:59:59.9999999', '23:59:59.9999999', 1234567890123456789012345678.0123456789,
        9223372036854775807, 1.7976931348623157E+308, 3.4028234E+38, 'F0E1D2C3-B4A5-4697-8899-AABBCCDDEEFF', CONVERT(sql_variant, CONVERT(decimal(5,2), 1.25)),
        1, N'한글 "따옴표" \역슬래시 ''홑' + NCHAR(55357) + NCHAR(56832), 'text', N'ntext 한글', 0x0102),
       (3, N'<r><![CDATA[x<y & "z"]]></r>', hierarchyid::GetRoot(),
        geography::STGeomFromText(N'POINT(126.97801234567891 37.566512345678912 10.5 2.25)', 4326),
        geometry::STGeomFromText(N'POINT(0.30000000000000004 0.1 7 8)', 3857), -922337203685477.5808, 214748.3647, 0x, 0x00000000,
        '0001-01-01T00:00:00.0000000-14:00', '0001-01-01T00:00:00.0000000', '00:00:00.0000001', -9999999999999999999999999999.9999999999,
        -9223372036854775808, 0.1E0 + 0.2E0, CONVERT(real, 0.1), '00000000-0000-0000-0000-000000000000',
        CONVERT(sql_variant, CONVERT(datetime, '2026-11-06T11:20:00.123')),
        0, NCHAR(9) + N'탭' + NCHAR(10) + N'줄' + NCHAR(13) + NCHAR(0) + NCHAR(31) + N'/<>&', NULL, NULL, NULL);
INSERT dbo.Types (Id) VALUES (2);   /* 모두 NULL */
GO
/* (E) 생성기 TriggerSql(U) 모양 그대로 — SelectItems: xml→CONVERT(nvarchar(max)), hierarchyid→.ToString(),
   geography/geometry→.STAsText() + .STSrid AS [열@srid], 그 밖(sql_variant 포함)→변환 없음. text/ntext/image 제외 */
CREATE TRIGGER [dbo].[dbr_audit_Types_U] ON [dbo].[Types]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);
    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;
    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;
    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');
    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)
    SELECT @dbr_seq, OBJECT_ID(N'[dbo].[Types]'), N'[dbo].[Types]', 'U', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'DELETED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT d.[Id] AS [Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT d.[Id] AS [Id], CONVERT(nvarchar(max), d.[cXml]) AS [cXml], d.[cHier].ToString() AS [cHier], d.[cGeog].STAsText() AS [cGeog], d.[cGeog].STSrid AS [cGeog@srid], d.[cGeom].STAsText() AS [cGeom], d.[cGeom].STSrid AS [cGeom@srid], d.[cMoney] AS [cMoney], d.[cSmallMoney] AS [cSmallMoney], d.[cVarbin] AS [cVarbin], d.[cBin] AS [cBin], d.[cRv] AS [cRv], d.[cDto] AS [cDto], d.[cDt2] AS [cDt2], d.[cTime] AS [cTime], d.[cDec] AS [cDec], d.[cBig] AS [cBig], d.[cFloat] AS [cFloat], d.[cReal] AS [cReal], d.[cGuid] AS [cGuid], d.[cVariant] AS [cVariant], d.[cBit] AS [cBit], d.[cNvar] AS [cNvar] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM deleted AS d;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, 'INSERTED', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[Id] AS [Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE (SELECT i.[Id] AS [Id], CONVERT(nvarchar(max), i.[cXml]) AS [cXml], i.[cHier].ToString() AS [cHier], i.[cGeog].STAsText() AS [cGeog], i.[cGeog].STSrid AS [cGeog@srid], i.[cGeom].STAsText() AS [cGeom], i.[cGeom].STSrid AS [cGeom@srid], i.[cMoney] AS [cMoney], i.[cSmallMoney] AS [cSmallMoney], i.[cVarbin] AS [cVarbin], i.[cBin] AS [cBin], i.[cRv] AS [cRv], i.[cDto] AS [cDto], i.[cDt2] AS [cDt2], i.[cTime] AS [cTime], i.[cDec] AS [cDec], i.[cBig] AS [cBig], i.[cFloat] AS [cFloat], i.[cReal] AS [cReal], i.[cGuid] AS [cGuid], i.[cVariant] AS [cVariant], i.[cBit] AS [cBit], i.[cNvar] AS [cNvar] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i;
END;
GO
EXEC sys.sp_settriggerorder @triggername = N'[dbo].[dbr_audit_Types_U]', @order = N'Last', @stmttype = N'UPDATE';
/* QUOTED_IDENTIFIER OFF로 저장된 모듈(0행이어야 함, 2026-11-10 run-sql -I 이후 재실행) */
SELECT 'E_module_qi' AS part, OBJECT_NAME(object_id) AS objectName, uses_quoted_identifier FROM sys.sql_modules;
GO
SET NOCOUNT ON;
DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
DECLARE @err int = NULL, @msg nvarchar(300) = NULL;
BEGIN TRAN;
BEGIN TRY
    UPDATE dbo.Types SET cBit = cBit;   /* 3행, 값은 그대로(rowversion만 바뀜) */
END TRY
BEGIN CATCH
    SELECT @err = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 300);
END CATCH
SELECT 'E_fire' AS part, @err AS errNo, @msg AS msg, XACT_STATE() AS xstate;
SELECT 'E_header' AS part, captureSeq, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode
FROM audit.AuditHeader WHERE captureSeq > @b;
SELECT 'E_image_count' AS part, imageKind, COUNT(*) AS n, SUM(CASE WHEN ISJSON(rowJson) = 1 THEN 1 ELSE 0 END) AS validRowJson,
       SUM(CASE WHEN ISJSON(keyJson) = 1 THEN 1 ELSE 0 END) AS validKeyJson
FROM audit.AuditRowImage WHERE captureSeq > @b GROUP BY imageKind;
SELECT 'E_row3_kv' AS part, r.keyJson, kv.[key], kv.value, kv.type
FROM audit.AuditRowImage AS r CROSS APPLY OPENJSON(r.rowJson) AS kv
WHERE r.captureSeq > @b AND r.imageKind = 'INSERTED' AND JSON_VALUE(r.keyJson, '$.Id') = N'3';
SELECT 'E_roundtrip' AS part, t.Id, v.col, v.same
FROM audit.AuditRowImage AS r
CROSS APPLY OPENJSON(r.rowJson) WITH (Id int, cXml nvarchar(max), cHier nvarchar(4000), cGeog nvarchar(max), [cGeog@srid] int '$."cGeog@srid"',
        cGeom nvarchar(max), [cGeom@srid] int '$."cGeom@srid"', cMoney money, cMoneyRaw nvarchar(40) '$.cMoney', cSmallMoney smallmoney,
        cVarbin varchar(max), cBin varchar(max), cRv varchar(100), cDto datetimeoffset(7), cDt2 datetime2(7), cTime time(7),
        cDec nvarchar(100), cBig nvarchar(40), cFloatRaw nvarchar(40) '$.cFloat', cRealRaw nvarchar(40) '$.cReal',
        cGuid uniqueidentifier, cVariant nvarchar(4000), cBit bit, cNvar nvarchar(100)) AS o
JOIN dbo.Types AS t ON t.Id = o.Id
CROSS APPLY (VALUES
    (N'xml',       CASE WHEN (t.cXml IS NULL AND o.cXml IS NULL) OR CONVERT(nvarchar(max), t.cXml) = o.cXml THEN 1 ELSE 0 END),
    (N'hierarchyid', CASE WHEN (t.cHier IS NULL AND o.cHier IS NULL) OR t.cHier = hierarchyid::Parse(o.cHier) THEN 1 ELSE 0 END),
    (N'geography STEquals', CASE WHEN (t.cGeog IS NULL AND o.cGeog IS NULL) OR t.cGeog.STEquals(geography::STGeomFromText(o.cGeog, o.[cGeog@srid])) = 1 THEN 1 ELSE 0 END),
    (N'geography 이진 동일', CASE WHEN (t.cGeog IS NULL AND o.cGeog IS NULL) OR CONVERT(varbinary(max), t.cGeog) = CONVERT(varbinary(max), geography::STGeomFromText(o.cGeog, o.[cGeog@srid])) THEN 1 ELSE 0 END),
    (N'geometry STEquals', CASE WHEN (t.cGeom IS NULL AND o.cGeom IS NULL) OR t.cGeom.STEquals(geometry::STGeomFromText(o.cGeom, o.[cGeom@srid])) = 1 THEN 1 ELSE 0 END),
    (N'geometry 이진 동일', CASE WHEN (t.cGeom IS NULL AND o.cGeom IS NULL) OR CONVERT(varbinary(max), t.cGeom) = CONVERT(varbinary(max), geometry::STGeomFromText(o.cGeom, o.[cGeom@srid])) THEN 1 ELSE 0 END),
    (N'money',     CASE WHEN (t.cMoney IS NULL AND o.cMoney IS NULL) OR t.cMoney = o.cMoney THEN 1 ELSE 0 END),
    (N'money 원문', CASE WHEN (t.cMoney IS NULL AND o.cMoneyRaw IS NULL) OR CONVERT(nvarchar(40), t.cMoney, 2) = o.cMoneyRaw THEN 1 ELSE 0 END),
    (N'smallmoney', CASE WHEN (t.cSmallMoney IS NULL AND o.cSmallMoney IS NULL) OR t.cSmallMoney = o.cSmallMoney THEN 1 ELSE 0 END),
    (N'varbinary', CASE WHEN (t.cVarbin IS NULL AND o.cVarbin IS NULL) OR t.cVarbin = CAST(N'' AS xml).value('xs:base64Binary(sql:column("o.cVarbin"))', 'varbinary(max)') THEN 1 ELSE 0 END),
    (N'binary',    CASE WHEN (t.cBin IS NULL AND o.cBin IS NULL) OR t.cBin = CAST(N'' AS xml).value('xs:base64Binary(sql:column("o.cBin"))', 'varbinary(max)') THEN 1 ELSE 0 END),
    (N'rowversion', CASE WHEN CONVERT(varbinary(8), t.cRv) = CAST(N'' AS xml).value('xs:base64Binary(sql:column("o.cRv"))', 'varbinary(max)') THEN 1 ELSE 0 END),
    (N'datetimeoffset', CASE WHEN (t.cDto IS NULL AND o.cDto IS NULL) OR (t.cDto = o.cDto AND DATEPART(TZOFFSET, t.cDto) = DATEPART(TZOFFSET, o.cDto)) THEN 1 ELSE 0 END),
    (N'datetime2', CASE WHEN (t.cDt2 IS NULL AND o.cDt2 IS NULL) OR t.cDt2 = o.cDt2 THEN 1 ELSE 0 END),
    (N'time',      CASE WHEN (t.cTime IS NULL AND o.cTime IS NULL) OR t.cTime = o.cTime THEN 1 ELSE 0 END),
    (N'decimal(38,10) 원문', CASE WHEN (t.cDec IS NULL AND o.cDec IS NULL) OR CONVERT(nvarchar(100), t.cDec) = o.cDec THEN 1 ELSE 0 END),
    (N'bigint 원문', CASE WHEN (t.cBig IS NULL AND o.cBig IS NULL) OR CONVERT(nvarchar(40), t.cBig) = o.cBig THEN 1 ELSE 0 END),
    (N'float',     CASE WHEN (t.cFloat IS NULL AND o.cFloatRaw IS NULL) OR t.cFloat = TRY_CONVERT(float, o.cFloatRaw) THEN 1 ELSE 0 END),
    (N'real',      CASE WHEN (t.cReal IS NULL AND o.cRealRaw IS NULL) OR t.cReal = TRY_CONVERT(real, o.cRealRaw) THEN 1 ELSE 0 END),
    (N'uniqueidentifier', CASE WHEN (t.cGuid IS NULL AND o.cGuid IS NULL) OR t.cGuid = o.cGuid THEN 1 ELSE 0 END),
    (N'sql_variant(원 형식을 알 때)', CASE WHEN t.cVariant IS NULL AND o.cVariant IS NULL THEN 1
                     WHEN CONVERT(sysname, SQL_VARIANT_PROPERTY(t.cVariant, 'BaseType')) = N'decimal' AND CONVERT(decimal(38,10), t.cVariant) = TRY_CONVERT(decimal(38,10), o.cVariant) THEN 1
                     WHEN CONVERT(sysname, SQL_VARIANT_PROPERTY(t.cVariant, 'BaseType')) = N'datetime' AND CONVERT(datetime, t.cVariant) = TRY_CONVERT(datetime, o.cVariant, 126) THEN 1
                     ELSE 0 END),
    (N'bit',       CASE WHEN (t.cBit IS NULL AND o.cBit IS NULL) OR t.cBit = o.cBit THEN 1 ELSE 0 END),
    (N'nvarchar',  CASE WHEN (t.cNvar IS NULL AND o.cNvar IS NULL) OR t.cNvar = o.cNvar COLLATE Latin1_General_100_BIN2 THEN 1 ELSE 0 END)
) AS v (col, same)
WHERE r.captureSeq > @b AND r.imageKind = 'INSERTED'
ORDER BY t.Id, v.col;
/* 공간 형식: 생성기(STAsText)와 원본 확장 표기(AsTextZM) 대조 */
SELECT 'E_spatial_text' AS part, t.Id, t.cGeog.STAsText() AS geogSTAsText, t.cGeog.AsTextZM() AS geogAsTextZM,
       t.cGeom.STAsText() AS geomSTAsText, t.cGeom.AsTextZM() AS geomAsTextZM
FROM dbo.Types AS t WHERE t.Id IN (1, 3) ORDER BY t.Id;
/* 대안 표기 ToString()(= CONVERT(nvarchar(max)))로 되읽으면 Z/M까지 이진 동일한가 */
SELECT 'E_spatial_alt' AS part, t.Id,
       CASE WHEN CONVERT(varbinary(max), geography::STGeomFromText(t.cGeog.ToString(), t.cGeog.STSrid)) = CONVERT(varbinary(max), t.cGeog) THEN 1 ELSE 0 END AS geogToStringBinarySame,
       CASE WHEN CONVERT(varbinary(max), geometry::STGeomFromText(t.cGeom.ToString(), t.cGeom.STSrid)) = CONVERT(varbinary(max), t.cGeom) THEN 1 ELSE 0 END AS geomToStringBinarySame
FROM dbo.Types AS t WHERE t.Id IN (1, 3) ORDER BY t.Id;
IF @@TRANCOUNT > 0 ROLLBACK;
GO
/* (F1) float·real: FOR JSON 원문 자릿수와 되읽기. 스타일 3(17자리)과 대조 */
SET NOCOUNT ON;
SELECT 'F1_float' AS part, v.label, CONVERT(varchar(30), v.f, 3) AS float17, j.txt AS forJson,
       CASE WHEN TRY_CONVERT(float, j.txt) = v.f THEN 1 ELSE 0 END AS sameForJson,
       CASE WHEN TRY_CONVERT(float, CONVERT(varchar(30), v.f, 3)) = v.f THEN 1 ELSE 0 END AS sameStyle3
FROM (VALUES (N'0.1+0.2', 0.1E0 + 0.2E0), (N'1/3', 1E0 / 3E0), (N'max', 1.7976931348623157E+308), (N'-max', -1.7976931348623157E+308),
             (N'min normal', 2.2250738585072014E-308), (N'2^53+2', 9007199254740994E0), (N'123456789.123456789', 123456789.123456789E0),
             (N'0.1', 0.1E0), (N'1e23', 1E23), (N'100', 100E0)) AS v (label, f)
CROSS APPLY (SELECT JSON_VALUE((SELECT v.f AS f FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), '$.f') AS txt) AS j;
SELECT 'F1_real' AS part, v.label, CONVERT(varchar(30), CONVERT(float, v.r), 3) AS asFloat17, j.txt AS forJson,
       CASE WHEN TRY_CONVERT(real, j.txt) = v.r THEN 1 ELSE 0 END AS sameForJson,
       CASE WHEN TRY_CONVERT(real, CONVERT(varchar(30), CONVERT(float, v.r), 3)) = v.r THEN 1 ELSE 0 END AS sameStyle3
FROM (VALUES (N'0.1', CONVERT(real, 0.1)), (N'max', CONVERT(real, 3.4028234E+38)), (N'1/3', CONVERT(real, 1E0 / 3E0)),
             (N'16777216', CONVERT(real, 16777216)), (N'1.17549435E-38', CONVERT(real, 1.17549435E-38)),
             (N'1000+2^-14', CONVERT(real, 1000.00006103515625))) AS v (label, r)
CROSS APPLY (SELECT JSON_VALUE((SELECT v.r AS r FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), '$.r') AS txt) AS j;
GO
/* (F2) sql_variant 기본 형식별 FOR JSON(생성기 기본 경로 = 변환 없음). 형식 정보가 남는가, 오류가 나는가 */
SET NOCOUNT ON;
CREATE TABLE dbo.Variants (Id int NOT NULL PRIMARY KEY, v sql_variant NULL);
INSERT dbo.Variants (Id, v) VALUES
 (1, CONVERT(sql_variant, CONVERT(int, 7))), (2, CONVERT(sql_variant, CONVERT(bigint, 9223372036854775807))),
 (3, CONVERT(sql_variant, CONVERT(decimal(38,10), 1234567890123456789012345678.0123456789))), (4, CONVERT(sql_variant, 0.1E0 + 0.2E0)),
 (5, CONVERT(sql_variant, CONVERT(real, 0.1))), (6, CONVERT(sql_variant, CONVERT(money, 922337203685477.5807))),
 (7, CONVERT(sql_variant, CONVERT(datetime, '2026-11-06T11:20:00.123'))), (8, CONVERT(sql_variant, CONVERT(datetime2(7), '2026-11-06T11:20:00.1234567'))),
 (9, CONVERT(sql_variant, CONVERT(datetimeoffset(7), '2026-11-06T11:20:00.1234567+09:00'))), (10, CONVERT(sql_variant, CONVERT(date, '2026-11-06'))),
 (11, CONVERT(sql_variant, CONVERT(time(7), '23:59:59.9999999'))), (12, CONVERT(sql_variant, CONVERT(varbinary(10), 0x00FF10))),
 (13, CONVERT(sql_variant, CONVERT(nvarchar(20), N'한글'))), (14, CONVERT(sql_variant, CONVERT(varchar(20), 'abc'))),
 (15, CONVERT(sql_variant, CONVERT(uniqueidentifier, 'F0E1D2C3-B4A5-4697-8899-AABBCCDDEEFF'))), (16, CONVERT(sql_variant, CONVERT(bit, 1))),
 (17, CONVERT(sql_variant, CONVERT(smalldatetime, '2026-11-06T11:20:00'))), (18, CONVERT(sql_variant, CONVERT(char(3), 'ab')));
DECLARE @f2 TABLE (Id int, baseType sysname NULL, ok bit, errNo int NULL, json nvarchar(400) NULL);
DECLARE @id int, @j nvarchar(max);
DECLARE c2 CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM dbo.Variants ORDER BY Id;
OPEN c2; FETCH NEXT FROM c2 INTO @id;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC sys.sp_executesql N'SET @j = (SELECT x.v AS v FROM dbo.Variants AS x WHERE x.Id = @id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES);',
             N'@id int, @j nvarchar(max) OUTPUT', @id = @id, @j = @j OUTPUT;
        INSERT @f2 SELECT @id, CONVERT(sysname, SQL_VARIANT_PROPERTY(v, 'BaseType')), 1, NULL, LEFT(@j, 400) FROM dbo.Variants WHERE Id = @id;
    END TRY
    BEGIN CATCH
        INSERT @f2 SELECT @id, CONVERT(sysname, SQL_VARIANT_PROPERTY(v, 'BaseType')), 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 400) FROM dbo.Variants WHERE Id = @id;
    END CATCH
    FETCH NEXT FROM c2 INTO @id;
END
CLOSE c2; DEALLOCATE c2;
SELECT 'F2_variant' AS part, * FROM @f2 ORDER BY Id;
GO
/* (F3) CLR 형식에 CONVERT(nvarchar(max)) — 생성기의 사용자 CLR UDT 경로를 시스템 CLR 형식으로 대리 확인 */
SET NOCOUNT ON;
DECLARE @f3 TABLE (Id int, expr nvarchar(100), ok bit, errNo int NULL, val nvarchar(400) NULL);
DECLARE @id int, @e nvarchar(100), @s nvarchar(max), @out nvarchar(max);
DECLARE c3 CURSOR LOCAL FAST_FORWARD FOR
    SELECT i.Id, x.e FROM (VALUES (1), (3)) AS i (Id)
    CROSS JOIN (VALUES (N'CONVERT(nvarchar(max), cHier)'), (N'CONVERT(nvarchar(max), cGeog)'), (N'CONVERT(nvarchar(max), cGeom)'),
                       (N'cGeog.ToString()'), (N'cGeom.ToString()')) AS x (e);
OPEN c3; FETCH NEXT FROM c3 INTO @id, @e;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @s = N'SET @out = (SELECT ' + @e + N' FROM dbo.Types WHERE Id = @id);';
    BEGIN TRY
        EXEC sys.sp_executesql @s, N'@id int, @out nvarchar(max) OUTPUT', @id = @id, @out = @out OUTPUT;
        INSERT @f3 VALUES (@id, @e, 1, NULL, LEFT(@out, 400));
    END TRY
    BEGIN CATCH
        INSERT @f3 VALUES (@id, @e, 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 400));
    END CATCH
    FETCH NEXT FROM c3 INTO @id, @e;
END
CLOSE c3; DEALLOCATE c3;
SELECT 'F3_clr_convert' AS part, * FROM @f3 ORDER BY Id, expr;
GO
/* (F4) 열 이름의 '.'·'@'와 생성기 키 충돌: FOR JSON PATH 결과와, 같은 모양 트리거의 생성·발동 */
SET NOCOUNT ON;
CREATE TABLE dbo.Names (Id int NOT NULL PRIMARY KEY, [a] int NULL, [a.b] int NULL, [g] geography NULL, [g@srid] int NULL, [q"x\y] int NULL);
INSERT dbo.Names (Id, [a], [a.b], [g], [g@srid], [q"x\y]) VALUES (1, 10, 20, geography::STGeomFromText(N'POINT(127 37.5)', 4326), 99, 5);
DECLARE @f4 TABLE (case_ nvarchar(80), ok bit, errNo int NULL, val nvarchar(400) NULL);
DECLARE @c nvarchar(80), @s nvarchar(max), @out nvarchar(max);
DECLARE c4 CURSOR LOCAL FAST_FORWARD FOR SELECT c, s FROM (VALUES
    (N'점 이름만 [a.b]', N'SET @out = (SELECT x.[Id] AS [Id], x.[a.b] AS [a.b] FROM dbo.Names AS x FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES);'),
    (N'[a]와 [a.b] 함께', N'SET @out = (SELECT x.[Id] AS [Id], x.[a] AS [a], x.[a.b] AS [a.b] FROM dbo.Names AS x FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES);'),
    (N'[g@srid] 생성 키와 같은 이름의 실제 열', N'SET @out = (SELECT x.[Id] AS [Id], x.[g].STAsText() AS [g], x.[g].STSrid AS [g@srid], x.[g@srid] AS [g@srid] FROM dbo.Names AS x FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES);'),
    (N'따옴표·역슬래시 이름', N'SET @out = (SELECT x.[Id] AS [Id], x.[q"x\y] AS [q"x\y] FROM dbo.Names AS x FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES);')
) AS v (c, s);
OPEN c4; FETCH NEXT FROM c4 INTO @c, @s;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC sys.sp_executesql @s, N'@out nvarchar(max) OUTPUT', @out = @out OUTPUT;
        INSERT @f4 VALUES (@c, 1, NULL, LEFT(@out, 400));
    END TRY
    BEGIN CATCH
        INSERT @f4 VALUES (@c, 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 400));
    END CATCH
    FETCH NEXT FROM c4 INTO @c, @s;
END
CLOSE c4; DEALLOCATE c4;
/* 같은 열 집합으로 생성기 모양 트리거(U)를 만들고 발동 */
SET @s = N'CREATE TRIGGER [dbo].[dbr_audit_Names_U] ON [dbo].[Names] AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @dbr_seq bigint = NEXT VALUE FOR audit.CaptureSeq;
    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)
    SELECT @dbr_seq, ''INSERTED'', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        (SELECT i.[Id] AS [Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
        (SELECT i.[Id] AS [Id], i.[a] AS [a], i.[a.b] AS [a.b], i.[g].STAsText() AS [g], i.[g].STSrid AS [g@srid], i.[g@srid] AS [g@srid], i.[q"x\y] AS [q"x\y] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
    FROM inserted AS i;
END';
BEGIN TRY EXEC (@s); INSERT @f4 VALUES (N'트리거 생성(충돌 열 포함)', 1, NULL, NULL); END TRY
BEGIN CATCH INSERT @f4 VALUES (N'트리거 생성(충돌 열 포함)', 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 400)); END CATCH
IF OBJECT_ID(N'dbo.dbr_audit_Names_U', N'TR') IS NOT NULL
BEGIN
    DECLARE @fe int = NULL, @fm nvarchar(380) = NULL, @fx int = NULL, @fj nvarchar(400) = NULL;
    BEGIN TRAN;
    BEGIN TRY
        UPDATE dbo.Names SET [a] = [a] WHERE Id = 1;
        SET @fj = (SELECT TOP (1) LEFT(rowJson, 400) FROM audit.AuditRowImage ORDER BY captureSeq DESC);
    END TRY
    BEGIN CATCH
        SELECT @fe = ERROR_NUMBER(), @fm = LEFT(ERROR_MESSAGE(), 380), @fx = XACT_STATE();
    END CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @f4 VALUES (N'발동(UPDATE)', CASE WHEN @fe IS NULL THEN 1 ELSE 0 END, @fe, COALESCE(@fj, CONCAT(N'xstate=', @fx, N' ', @fm)));
END
SELECT 'F4_names' AS part, * FROM @f4;
GO
USE master;
GO
IF DB_ID(N'SpikeLab_SP6') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP6 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP6; END
GO
