/* SP17 — 원본(sql-origin의 CascadeLab)이 Split 한 바퀴(기준선 구축 + 미리보기 + [적용]의 읽기 단계) 전후에 DDL 한 건도 안 바뀌는지 보는 지문.
   [적용]을 하면 행 데이터(행 수·값)는 바뀐다. 이 지문은 스키마·권한·모듈·설정만 본다(행 수는 참고용으로 따로 낸다).
   실행(저장소 루트, 전과 후에 각각, sa로): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp17_20_origin_fingerprint.sql -Log spikes\logs\sp17-20-before.log
                                              .\spikes\sql\sp_container-run.ps1 spikes\sql\sp17_20_origin_fingerprint.sql -Log spikes\logs\sp17-20-after.log
   비교: 두 로그의 "k v" 줄에서 ddl_ 로 시작하는 줄이 모두 같아야 한다(copy_only_backups_* 와 rows_total 은 다를 수 있음).
   읽기만 한다. */
SET NOCOUNT ON;
USE master;
GO
SELECT 'ddl_objects' AS k, CONCAT(COUNT(*), ' max_modify=', CONVERT(varchar(33), MAX(modify_date), 126)) AS v FROM CascadeLab.sys.objects WHERE is_ms_shipped = 0
UNION ALL SELECT 'ddl_modules_checksum', CAST(CHECKSUM_AGG(CHECKSUM(object_id, definition)) AS varchar(20)) FROM CascadeLab.sys.sql_modules
UNION ALL SELECT 'ddl_schemas', CAST(COUNT(*) AS varchar(20)) FROM CascadeLab.sys.schemas
UNION ALL SELECT 'ddl_audit_schema_exists', CAST(COUNT(*) AS varchar(20)) FROM CascadeLab.sys.schemas WHERE name = N'audit'
UNION ALL SELECT 'ddl_principals', CONCAT(COUNT(*), ' ', STRING_AGG(name, N',') WITHIN GROUP (ORDER BY name)) FROM CascadeLab.sys.database_principals WHERE type IN ('S', 'U', 'R') AND is_fixed_role = 0
UNION ALL SELECT 'ddl_permissions', CAST(COUNT(*) AS varchar(20)) FROM CascadeLab.sys.database_permissions
UNION ALL SELECT 'ddl_triggers_total', CAST(COUNT(*) AS varchar(20)) FROM CascadeLab.sys.triggers
UNION ALL SELECT 'ddl_database_options', CONCAT(collation_name, ' compat=', compatibility_level, ' recursive=', is_recursive_triggers_on, ' trustworthy=', is_trustworthy_on, ' broker=', is_broker_enabled, ' state=', state_desc, ' owner=', ISNULL(SUSER_SNAME(owner_sid), N'(null)')) FROM sys.databases WHERE name = N'CascadeLab'
UNION ALL SELECT 'ddl_databases_on_server', CONCAT(COUNT(*), ' ', STRING_AGG(name, N',') WITHIN GROUP (ORDER BY name)) FROM sys.databases WHERE database_id > 4
UNION ALL SELECT 'copy_only_backups_CascadeLab', CAST(COUNT(*) AS varchar(20)) FROM msdb.dbo.backupset WHERE database_name = N'CascadeLab' AND is_copy_only = 1 AND type = 'D'
UNION ALL SELECT 'non_copy_only_backups_CascadeLab', CAST(COUNT(*) AS varchar(20)) FROM msdb.dbo.backupset WHERE database_name = N'CascadeLab' AND is_copy_only = 0
UNION ALL SELECT 'rows_total', CAST(SUM(row_count) AS varchar(30)) FROM CascadeLab.sys.dm_db_partition_stats WHERE index_id IN (0, 1) AND object_id IN (SELECT object_id FROM CascadeLab.sys.objects WHERE type = 'U' AND is_ms_shipped = 0);
GO
