/* SP6 — 형식별 FOR JSON 직렬화: (A) 변환 없이, (B) §6.1 변환, (C) OPENJSON 왕복, (D) text/ntext/image 트리거 참조(오류 311 시점) */
USE master;
GO
IF DB_ID(N'SpikeLab_SP6') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP6 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP6; END
GO
CREATE DATABASE SpikeLab_SP6;
GO
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
        1, N'한글 "따옴표" \역슬래시 ''홑' + NCHAR(55357) + NCHAR(56832), 'text', N'ntext 한글', 0x0102);
INSERT dbo.Types (Id) VALUES (2);   /* 모두 NULL */
GO
/* (A) 변환 없이 열마다 FOR JSON PATH, INCLUDE_NULL_VALUES */
SET NOCOUNT ON;
DECLARE @res TABLE (col sysname, typ sysname, ok bit, errNo int NULL, json nvarchar(max) NULL);
DECLARE @c sysname, @t sysname, @sql nvarchar(max), @j nvarchar(max);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT c.name, TYPE_NAME(c.user_type_id) FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'dbo.Types') AND c.name <> N'Id' ORDER BY c.column_id;
OPEN cur; FETCH NEXT FROM cur INTO @c, @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SET @j = (SELECT Id, ' + QUOTENAME(@c) + N' FROM dbo.Types ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES);';
    BEGIN TRY
        EXEC sys.sp_executesql @sql, N'@j nvarchar(max) OUTPUT', @j = @j OUTPUT;
        INSERT @res VALUES (@c, @t, 1, NULL, @j);
    END TRY
    BEGIN CATCH
        INSERT @res VALUES (@c, @t, 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 300));
    END CATCH
    FETCH NEXT FROM cur INTO @c, @t;
END
CLOSE cur; DEALLOCATE cur;
SELECT 'A_no_conversion' AS part, * FROM @res;
GO
/* (B) §6.1 변환 + (C) OPENJSON 왕복 비교 */
SET NOCOUNT ON;
DECLARE @j nvarchar(max) = (
    SELECT Id, CONVERT(nvarchar(max), cXml) AS cXml, cHier.ToString() AS cHier,
           cGeog.STAsText() AS cGeog, cGeog.STSrid AS [cGeog@srid], cGeom.STAsText() AS cGeom, cGeom.STSrid AS [cGeom@srid],
           cMoney, cSmallMoney, cVarbin, cBin, cRv, cDto, cDt2, cTime, cDec, cBig, cFloat, cReal, cGuid,
           CONVERT(nvarchar(4000), cVariant) AS cVariant, CONVERT(nvarchar(128), SQL_VARIANT_PROPERTY(cVariant, 'BaseType')) AS [cVariant@type],
           cBit, cNvar
    FROM dbo.Types ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES);
SELECT 'B_converted_json' AS part, @j AS json, ISJSON(@j) AS isJson;
SELECT 'C_roundtrip' AS part, t.Id, v.col, v.same
FROM dbo.Types AS t
JOIN OPENJSON(@j) WITH (Id int, cXml nvarchar(max), cHier nvarchar(4000), cGeog nvarchar(max), [cGeog@srid] int '$."cGeog@srid"',
        cGeom nvarchar(max), cMoney money, cSmallMoney smallmoney, cVarbin varchar(max), cBin varchar(max), cRv varchar(100),
        cDto datetimeoffset(7), cDt2 datetime2(7), cTime time(7), cDec nvarchar(100), cBig nvarchar(40), cFloat float, cReal real,
        cGuid uniqueidentifier, cBit bit, cNvar nvarchar(100)) AS o ON o.Id = t.Id
CROSS APPLY (VALUES
    ('xml',       CASE WHEN (t.cXml IS NULL AND o.cXml IS NULL) OR CONVERT(nvarchar(max), t.cXml) = o.cXml THEN 1 ELSE 0 END),
    ('hierarchyid', CASE WHEN (t.cHier IS NULL AND o.cHier IS NULL) OR t.cHier = hierarchyid::Parse(o.cHier) THEN 1 ELSE 0 END),
    ('geography', CASE WHEN (t.cGeog IS NULL AND o.cGeog IS NULL) OR t.cGeog.STEquals(geography::STGeomFromText(o.cGeog, o.[cGeog@srid])) = 1 THEN 1 ELSE 0 END),
    ('money',     CASE WHEN (t.cMoney IS NULL AND o.cMoney IS NULL) OR t.cMoney = o.cMoney THEN 1 ELSE 0 END),
    ('smallmoney', CASE WHEN (t.cSmallMoney IS NULL AND o.cSmallMoney IS NULL) OR t.cSmallMoney = o.cSmallMoney THEN 1 ELSE 0 END),
    ('varbinary', CASE WHEN (t.cVarbin IS NULL AND o.cVarbin IS NULL) OR t.cVarbin = CAST(N'' AS xml).value('xs:base64Binary(sql:column("o.cVarbin"))', 'varbinary(max)') THEN 1 ELSE 0 END),
    ('binary',    CASE WHEN (t.cBin IS NULL AND o.cBin IS NULL) OR t.cBin = CAST(N'' AS xml).value('xs:base64Binary(sql:column("o.cBin"))', 'varbinary(max)') THEN 1 ELSE 0 END),
    ('rowversion', CASE WHEN CONVERT(varbinary(8), t.cRv) = CAST(N'' AS xml).value('xs:base64Binary(sql:column("o.cRv"))', 'varbinary(max)') THEN 1 ELSE 0 END),
    ('datetimeoffset', CASE WHEN (t.cDto IS NULL AND o.cDto IS NULL) OR (t.cDto = o.cDto AND DATEPART(TZOFFSET, t.cDto) = DATEPART(TZOFFSET, o.cDto)) THEN 1 ELSE 0 END),
    ('datetime2', CASE WHEN (t.cDt2 IS NULL AND o.cDt2 IS NULL) OR t.cDt2 = o.cDt2 THEN 1 ELSE 0 END),
    ('time',      CASE WHEN (t.cTime IS NULL AND o.cTime IS NULL) OR t.cTime = o.cTime THEN 1 ELSE 0 END),
    ('decimal(38,10) 원문', CASE WHEN (t.cDec IS NULL AND o.cDec IS NULL) OR CONVERT(nvarchar(100), t.cDec) = o.cDec THEN 1 ELSE 0 END),
    ('bigint 원문', CASE WHEN (t.cBig IS NULL AND o.cBig IS NULL) OR CONVERT(nvarchar(40), t.cBig) = o.cBig THEN 1 ELSE 0 END),
    ('float',     CASE WHEN (t.cFloat IS NULL AND o.cFloat IS NULL) OR t.cFloat = o.cFloat THEN 1 ELSE 0 END),
    ('real',      CASE WHEN (t.cReal IS NULL AND o.cReal IS NULL) OR t.cReal = o.cReal THEN 1 ELSE 0 END),
    ('uniqueidentifier', CASE WHEN (t.cGuid IS NULL AND o.cGuid IS NULL) OR t.cGuid = o.cGuid THEN 1 ELSE 0 END),
    ('bit',       CASE WHEN (t.cBit IS NULL AND o.cBit IS NULL) OR t.cBit = o.cBit THEN 1 ELSE 0 END),
    ('nvarchar',  CASE WHEN (t.cNvar IS NULL AND o.cNvar IS NULL) OR t.cNvar = o.cNvar COLLATE Latin1_General_100_BIN2 THEN 1 ELSE 0 END)
) AS v (col, same)
ORDER BY t.Id, v.col;
/* srid 속성 이름 확인: 숫자로 나오는가, '@'가 특별 취급되는가 */
SELECT 'B_srid_attr' AS part, [key], value, type FROM OPENJSON(JSON_QUERY(@j, '$[0]')) WHERE [key] LIKE N'%@%';
GO
/* (D) text/ntext/image 트리거 참조: 생성 시점 오류인지 */
DECLARE @d TABLE (case_ nvarchar(60), ok bit, errNo int NULL, msg nvarchar(300) NULL);
DECLARE @s nvarchar(max);
SET @s = N'CREATE TRIGGER dbo.trg_text_ref ON dbo.Types AFTER UPDATE AS BEGIN SET NOCOUNT ON; DECLARE @n int = (SELECT COUNT(*) FROM inserted WHERE cText IS NOT NULL); END';
BEGIN TRY EXEC (@s); INSERT @d VALUES (N'text 열 참조 트리거 생성', 1, NULL, NULL); END TRY
BEGIN CATCH INSERT @d VALUES (N'text 열 참조 트리거 생성', 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 300)); END CATCH
SET @s = N'CREATE TRIGGER dbo.trg_star_ref ON dbo.Types AFTER UPDATE AS BEGIN SET NOCOUNT ON; DECLARE @j nvarchar(max) = (SELECT * FROM inserted FOR JSON PATH); END';
BEGIN TRY EXEC (@s); INSERT @d VALUES (N'SELECT * FROM inserted 트리거 생성', 1, NULL, NULL); END TRY
BEGIN CATCH INSERT @d VALUES (N'SELECT * FROM inserted 트리거 생성', 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 300)); END CATCH
/* 생성됐다면 실행 시점 확인 */
IF OBJECT_ID(N'dbo.trg_star_ref', N'TR') IS NOT NULL OR OBJECT_ID(N'dbo.trg_text_ref', N'TR') IS NOT NULL
BEGIN
    BEGIN TRAN;
    BEGIN TRY UPDATE dbo.Types SET cBit = cBit WHERE Id = 1; INSERT @d VALUES (N'실행 시점(UPDATE 발동)', 1, NULL, NULL); END TRY
    BEGIN CATCH INSERT @d VALUES (N'실행 시점(UPDATE 발동)', 0, ERROR_NUMBER(), LEFT(ERROR_MESSAGE(), 300)); END CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
END
SELECT 'D_text_in_trigger' AS part, * FROM @d;
GO
USE master;
GO
IF DB_ID(N'SpikeLab_SP6') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP6 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP6; END
GO
