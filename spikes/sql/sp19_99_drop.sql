/* SP19 정리 — SP19_로 시작하는 DB 하나와 그 스냅샷을 지운다(없으면 아무것도 안 함). 원본·샌드박스 컨테이너 각각에서 쓴다.
   SP19_ 이름만 지운다(CascadeLab·AW_Origin 같은 기존 DB는 이름 검사로 막힌다).
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_99_drop.sql -Service sql        -Vars @{ DbName = 'SP19_0G3_Sandbox' } -Log spikes/logs/sp19-cloud-99-drop-0g3-sandbox.log
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_99_drop.sql -Service sql-origin -Vars @{ DbName = 'SP19_0G3' }         -Log spikes/logs/sp19-cloud-99-drop-0g3-origin.log */
:on error exit
SET NOCOUNT ON;
IF N'$(DbName)' NOT LIKE N'SP19[_]%' THROW 50001, N'SP19_ 이름만 지운다', 1;
DECLARE @sql nvarchar(max) = N'';
-- 1. 이 DB의 스냅샷 먼저(스냅샷이 있으면 원본 DB를 DROP할 수 없다)
SELECT @sql += N'DROP DATABASE ' + QUOTENAME(name) + N'; '
FROM sys.databases WHERE source_database_id = DB_ID(N'$(DbName)');
IF @sql <> N'' EXEC (@sql);
-- 2. DB(다른 연결은 끊는다)
IF DB_ID(N'$(DbName)') IS NOT NULL
BEGIN
    IF (SELECT state_desc FROM sys.databases WHERE name = N'$(DbName)') = N'ONLINE'
        EXEC (N'ALTER DATABASE ' + N'[$(DbName)]' + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;');
    EXEC (N'DROP DATABASE ' + N'[$(DbName)]' + N';');
END;
SELECT 'left' AS what, COUNT(*) AS value FROM sys.databases WHERE name = N'$(DbName)' OR source_database_id = DB_ID(N'$(DbName)');
GO
