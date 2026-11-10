-- CascadeLab 설치 확인(읽기 전용). 실행: .\scripts\run-sql.ps1 cascadelab\verification\sql\install-check.sql -Database master -Log cascadelab\verification\logs\install-check.log
SET NOCOUNT ON;
SELECT name, is_recursive_triggers_on, compatibility_level, collation_name
FROM sys.databases
WHERE name IN (N'CascadeLab', N'CascadeLab_RT')
ORDER BY name;
SELECT name, value_in_use FROM sys.configurations WHERE name = N'nested triggers';
GO
USE CascadeLab;
GO
SET NOCOUNT ON;
PRINT '== CascadeLab tables (rows) ==';
SELECT s.name + N'.' + t.name AS tbl, SUM(p.row_count) AS row_count
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.dm_db_partition_stats AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY s.name, t.name
ORDER BY tbl;
PRINT '== CascadeLab foreign keys ==';
SELECT fk.name, OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id) AS child,
       OBJECT_SCHEMA_NAME(fk.referenced_object_id) + N'.' + OBJECT_NAME(fk.referenced_object_id) AS parent,
       fk.delete_referential_action_desc AS on_delete, fk.update_referential_action_desc AS on_update,
       fk.is_not_trusted, fk.is_disabled
FROM sys.foreign_keys AS fk
ORDER BY fk.name;
PRINT '== CascadeLab triggers ==';
SELECT OBJECT_SCHEMA_NAME(tr.object_id) + N'.' + tr.name AS trg,
       OBJECT_SCHEMA_NAME(tr.parent_id) + N'.' + OBJECT_NAME(tr.parent_id) AS on_table,
       te.type_desc AS event, te.is_first, te.is_last, tr.is_disabled, tr.is_instead_of_trigger
FROM sys.triggers AS tr
JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
WHERE tr.parent_class = 1
ORDER BY trg;
GO
USE CascadeLab_RT;
GO
SET NOCOUNT ON;
PRINT '== CascadeLab_RT tables (rows) ==';
SELECT s.name + N'.' + t.name AS tbl, SUM(p.row_count) AS row_count
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.dm_db_partition_stats AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY s.name, t.name
ORDER BY tbl;
PRINT '== CascadeLab_RT triggers ==';
SELECT OBJECT_SCHEMA_NAME(tr.object_id) + N'.' + tr.name AS trg,
       OBJECT_SCHEMA_NAME(tr.parent_id) + N'.' + OBJECT_NAME(tr.parent_id) AS on_table,
       te.type_desc AS event, te.is_first, te.is_last, tr.is_disabled
FROM sys.triggers AS tr
JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
WHERE tr.parent_class = 1
ORDER BY trg;
GO
