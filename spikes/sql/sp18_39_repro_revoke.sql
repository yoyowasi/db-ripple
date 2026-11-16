/* SP18 보조(2026-11-16) — sp18_30에서 준 임시 권한을 거두고, 원본에 남은 스냅샷(제품 이름 CascadeLab_pre…)이 없는지 본다.
   실행(저장소 루트, sa, 원본 모사 컨테이너): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp18_39_repro_revoke.sql -Log spikes\logs\sp18-39-revoke.log */
SET NOCOUNT ON;
USE master;
GO
REVOKE CREATE ANY DATABASE FROM [dbr_origin_user];
GO
SELECT 'leftover_create_any_database_grants' AS what, COUNT(*) AS n FROM sys.server_permissions WHERE grantee_principal_id = SUSER_ID(N'dbr_origin_user') AND permission_name = N'CREATE ANY DATABASE' AND state = 'G'
UNION ALL SELECT 'snapshot_dbs_on_origin', COUNT(*) FROM sys.databases WHERE source_database_id IS NOT NULL
UNION ALL SELECT 'user_dbs_on_origin', COUNT(*) FROM sys.databases WHERE database_id > 4;
GO
