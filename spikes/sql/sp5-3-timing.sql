-- SP5-3: 시간 측정. master에서 실행. 배치마다 SYSDATETIME() 기록 후 마지막에 결과 표.
-- 이름 고정: AW_Origin(원본, 변경 금지), AW_Timing, AW_Timing_Snap. FILESTREAM 파일이 있으면 MOVE 처리가 부족하므로 sp5-2 결과를 먼저 확인.
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'tempdb..##sp5_t') IS NOT NULL DROP TABLE ##sp5_t;
CREATE TABLE ##sp5_t (step nvarchar(40), t0 datetime2(3), t1 datetime2(3));
GO
IF DB_ID(N'AW_Timing_Snap') IS NOT NULL DROP DATABASE AW_Timing_Snap;
IF DB_ID(N'AW_Timing') IS NOT NULL DROP DATABASE AW_Timing;
GO
-- 1) BACKUP COPY_ONLY
INSERT ##sp5_t(step, t0) VALUES (N'backup_copy_only', SYSDATETIME());
GO
BACKUP DATABASE AW_Origin TO DISK = N'/var/opt/mssql/backup/AW_Origin_copyonly.bak' WITH COPY_ONLY, INIT, STATS = 25;
GO
UPDATE ##sp5_t SET t1 = SYSDATETIME() WHERE step = N'backup_copy_only';
GO
-- 2) RESTORE WITH MOVE (데이터·로그 파일만)
INSERT ##sp5_t(step, t0) VALUES (N'restore_with_move', SYSDATETIME());
GO
DECLARE @mv nvarchar(max);
SELECT @mv = STRING_AGG(CAST(N'MOVE N''' + name + N''' TO N''/var/opt/mssql/data/AW_Timing_' + name + CASE type WHEN 1 THEN N'.ldf' ELSE N'.mdf' END + N'''' AS nvarchar(max)), N', ')
FROM AW_Origin.sys.database_files WHERE type IN (0, 1);
DECLARE @sql nvarchar(max) = N'RESTORE DATABASE AW_Timing FROM DISK = N''/var/opt/mssql/backup/AW_Origin_copyonly.bak'' WITH ' + @mv + N', STATS = 25;';
EXEC sys.sp_executesql @sql;
GO
UPDATE ##sp5_t SET t1 = SYSDATETIME() WHERE step = N'restore_with_move';
GO
-- 3) 스냅샷 생성
INSERT ##sp5_t(step, t0) VALUES (N'snapshot_create', SYSDATETIME());
GO
DECLARE @files nvarchar(max);
SELECT @files = STRING_AGG(CAST(N'(NAME = ' + QUOTENAME(name) + N', FILENAME = N''/var/opt/mssql/data/AW_Timing_Snap_' + name + N'.ss'')' AS nvarchar(max)), N', ')
FROM AW_Timing.sys.database_files WHERE type = 0;
DECLARE @sql nvarchar(max) = N'CREATE DATABASE AW_Timing_Snap ON ' + @files + N' AS SNAPSHOT OF AW_Timing;';
EXEC sys.sp_executesql @sql;
GO
UPDATE ##sp5_t SET t1 = SYSDATETIME() WHERE step = N'snapshot_create';
GO
-- 4) 스냅샷으로 되돌리기 (원본 복사본에 변경을 조금 준 뒤)
SELECT TOP (1000) * INTO AW_Timing.dbo.sp5_junk FROM sys.all_objects;
GO
INSERT ##sp5_t(step, t0) VALUES (N'snapshot_revert', SYSDATETIME());
GO
RESTORE DATABASE AW_Timing FROM DATABASE_SNAPSHOT = N'AW_Timing_Snap';
GO
UPDATE ##sp5_t SET t1 = SYSDATETIME() WHERE step = N'snapshot_revert';
GO
SELECT step, t0, t1, DATEDIFF(MILLISECOND, t0, t1) AS ms FROM ##sp5_t;
GO
-- 정리 (되돌리기 뒤에도 스냅샷은 남으므로 직접 삭제)
IF DB_ID(N'AW_Timing_Snap') IS NOT NULL DROP DATABASE AW_Timing_Snap;
IF DB_ID(N'AW_Timing') IS NOT NULL DROP DATABASE AW_Timing;
DROP TABLE ##sp5_t;
GO
