-- SP0: 에디션 확인과 스냅샷 생성 가능 여부. 순수 T-SQL + GO. master에서 실행.
SELECT SERVERPROPERTY('Edition') AS Edition,
       SERVERPROPERTY('EngineEdition') AS EngineEdition,
       SERVERPROPERTY('ProductVersion') AS ProductVersion,
       SERVERPROPERTY('ProductLevel') AS ProductLevel,
       SERVERPROPERTY('ProductUpdateLevel') AS ProductUpdateLevel;
GO
SELECT @@VERSION AS VersionText;
GO
-- 작은 DB 생성 (SP0_Small, SP0_Small_Snap 이름 고정: 아래 정리 구문과 일치해야 함)
IF DB_ID(N'SP0_Small_Snap') IS NOT NULL DROP DATABASE SP0_Small_Snap;
IF DB_ID(N'SP0_Small') IS NOT NULL DROP DATABASE SP0_Small;
GO
CREATE DATABASE SP0_Small;
GO
-- 스냅샷: sys.database_files의 데이터 파일(type=0) 논리 이름마다 경로를 지정
DECLARE @files nvarchar(max);
SELECT @files = STRING_AGG(CAST(N'(NAME = ' + QUOTENAME(name) + N', FILENAME = N''/var/opt/mssql/data/SP0_Small_Snap_' + name + N'.ss'')' AS nvarchar(max)), N', ')
FROM SP0_Small.sys.database_files WHERE type = 0;
DECLARE @sql nvarchar(max) = N'CREATE DATABASE SP0_Small_Snap ON ' + @files + N' AS SNAPSHOT OF SP0_Small;';
PRINT @sql;
BEGIN TRY
    EXEC sys.sp_executesql @sql;
    SELECT N'SNAPSHOT_OK' AS Result;
END TRY
BEGIN CATCH
    SELECT N'SNAPSHOT_FAIL' AS Result, ERROR_NUMBER() AS ErrNo, ERROR_MESSAGE() AS ErrMsg;
END CATCH
GO
SELECT name, source_database_id, DB_NAME(source_database_id) AS source_name
FROM sys.databases WHERE name IN (N'SP0_Small', N'SP0_Small_Snap');
GO
-- 정리
IF DB_ID(N'SP0_Small_Snap') IS NOT NULL DROP DATABASE SP0_Small_Snap;
IF DB_ID(N'SP0_Small') IS NOT NULL DROP DATABASE SP0_Small;
GO
