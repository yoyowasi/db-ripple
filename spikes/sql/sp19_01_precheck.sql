/* SP19 사전 확인(읽기만) — 두 컨테이너 각각에서 실행해 주버전, 데이터 경로, 남은 SP19 DB·XE 세션, 제한 로그인, CascadeLab 유무를 본다.
   결과 줄은 "precheck <항목> <값>" 모양이라 sp19_00_run.ps1이 줄 단위로 읽는다.
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_01_precheck.sql -Service sql-origin -Log spikes/logs/sp19-cloud-01-precheck-origin.log
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_01_precheck.sql -Service sql        -Log spikes/logs/sp19-cloud-01-precheck-sandbox.log */
SET NOCOUNT ON;
SELECT CONCAT('precheck major ', CAST(SERVERPROPERTY('ProductMajorVersion') AS nvarchar(10))) AS line
UNION ALL SELECT CONCAT('precheck version ', CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(30)))
UNION ALL SELECT CONCAT('precheck edition ', REPLACE(CAST(SERVERPROPERTY('Edition') AS nvarchar(100)), N' ', N'_'))
UNION ALL SELECT CONCAT('precheck data_dir ', CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260)))
UNION ALL SELECT CONCAT('precheck sp19_dbs ', COUNT(*)) FROM sys.databases WHERE name LIKE N'SP19[_]%'
UNION ALL SELECT CONCAT('precheck sp19_db ', name) FROM sys.databases WHERE name LIKE N'SP19[_]%'
UNION ALL SELECT CONCAT('precheck xe_sp19_trace ', COUNT(*)) FROM sys.server_event_sessions WHERE name = N'sp19_trace'
UNION ALL SELECT CONCAT('precheck login_dbr_origin_user ', CASE WHEN SUSER_ID(N'dbr_origin_user') IS NULL THEN 0 ELSE 1 END)
UNION ALL SELECT CONCAT('precheck db_CascadeLab ', CASE WHEN DB_ID(N'CascadeLab') IS NULL THEN 0 ELSE 1 END)
UNION ALL SELECT CONCAT('precheck physical_memory_mb ', physical_memory_kb / 1024) FROM sys.dm_os_sys_info
UNION ALL SELECT CONCAT('precheck committed_target_mb ', committed_target_kb / 1024) FROM sys.dm_os_sys_info;
GO
