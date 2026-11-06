-- SP5-4: 스냅샷이 있는 DB에 RESTORE를 시도하면 거부되는지. master에서 실행.
-- 이름 고정: AW_Origin은 건드리지 않고 복사본 AW_SnapTest 사용.
SET NOCOUNT ON;
GO
IF DB_ID(N'AW_SnapTest_Snap') IS NOT NULL DROP DATABASE AW_SnapTest_Snap;
IF DB_ID(N'AW_SnapTest') IS NOT NULL DROP DATABASE AW_SnapTest;
GO
BACKUP DATABASE AW_Origin TO DISK = N'/var/opt/mssql/backup/AW_SnapTest.bak' WITH COPY_ONLY, INIT;
GO
DECLARE @mv nvarchar(max);
SELECT @mv = STRING_AGG(CAST(N'MOVE N''' + name + N''' TO N''/var/opt/mssql/data/AW_SnapTest_' + name + CASE type WHEN 1 THEN N'.ldf' ELSE N'.mdf' END + N'''' AS nvarchar(max)), N', ')
FROM AW_Origin.sys.database_files WHERE type IN (0, 1);
DECLARE @sql nvarchar(max) = N'RESTORE DATABASE AW_SnapTest FROM DISK = N''/var/opt/mssql/backup/AW_SnapTest.bak'' WITH ' + @mv + N';';
EXEC sys.sp_executesql @sql;
GO
DECLARE @files nvarchar(max);
SELECT @files = STRING_AGG(CAST(N'(NAME = ' + QUOTENAME(name) + N', FILENAME = N''/var/opt/mssql/data/AW_SnapTest_Snap_' + name + N'.ss'')' AS nvarchar(max)), N', ')
FROM AW_SnapTest.sys.database_files WHERE type = 0;
DECLARE @sql nvarchar(max) = N'CREATE DATABASE AW_SnapTest_Snap ON ' + @files + N' AS SNAPSHOT OF AW_SnapTest;';
EXEC sys.sp_executesql @sql;
GO
-- 스냅샷이 있는 상태에서 같은 DB에 RESTORE 시도
BEGIN TRY
    RESTORE DATABASE AW_SnapTest FROM DISK = N'/var/opt/mssql/backup/AW_SnapTest.bak' WITH REPLACE;
    SELECT N'RESTORE_ALLOWED' AS Result, CAST(NULL AS int) AS ErrNo, CAST(NULL AS nvarchar(4000)) AS ErrMsg;
END TRY
BEGIN CATCH
    SELECT N'RESTORE_REJECTED' AS Result, ERROR_NUMBER() AS ErrNo, ERROR_MESSAGE() AS ErrMsg;
END CATCH
GO
-- 정리
IF DB_ID(N'AW_SnapTest_Snap') IS NOT NULL DROP DATABASE AW_SnapTest_Snap;
IF DB_ID(N'AW_SnapTest') IS NOT NULL DROP DATABASE AW_SnapTest;
GO
