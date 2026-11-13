/* SP17 — Split 기준선 뒤 샌드박스 컨테이너(2025, sa)의 상태를 읽기만 해서 확인한다.
   실행(저장소 루트): .\scripts\run-sql.ps1 spikes\sql\sp17_25_sandbox_check.sql -Log spikes\logs\sp17-25-sandbox.log
   기대: CascadeLab_Sandbox(ONLINE, 소유자 sa)와 CascadeLab_Sandbox_base(스냅샷, source = CascadeLab_Sandbox)가 있고, 감사 스키마·감사 트리거가 설치돼 있으며,
   서버 옵션 nested triggers가 원본 값과 같다. dbr_preview_user가 있다(4b). 읽기만 한다. */
SET NOCOUNT ON;
SELECT d.name, d.state_desc, d.compatibility_level, d.collation_name, ISNULL(SUSER_SNAME(d.owner_sid), N'(null: orphan)') AS owner_login,
       d.is_recursive_triggers_on, d.is_trustworthy_on, ISNULL(src.name, N'') AS snapshot_of
FROM sys.databases AS d LEFT JOIN sys.databases AS src ON src.database_id = d.source_database_id
WHERE d.name LIKE N'CascadeLab[_]Sandbox%' ORDER BY d.name;
SELECT name AS server_option, CAST(value_in_use AS int) AS value_in_use FROM sys.configurations WHERE name IN (N'nested triggers', N'disallow results from triggers') ORDER BY name;
SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(60)) AS sandbox_version, CAST(SERVERPROPERTY('Collation') AS nvarchar(128)) AS server_collation;
GO
USE CascadeLab_Sandbox;
GO
SELECT 'audit_schema' AS what, COUNT(*) AS n FROM sys.schemas WHERE name = N'audit'
UNION ALL SELECT 'audit_triggers', COUNT(*) FROM sys.triggers WHERE name LIKE N'dbr[_]audit[_]%'
UNION ALL SELECT 'dbr_preview_user', COUNT(*) FROM sys.database_principals WHERE name = N'dbr_preview_user'
UNION ALL SELECT 'user_tables', COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0 AND SCHEMA_NAME(schema_id) <> N'audit'
UNION ALL SELECT 'cl25_schema', COUNT(*) FROM sys.schemas WHERE name = N'cl25';
GO
