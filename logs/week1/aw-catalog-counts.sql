-- A24 재확인 독립 대조: sys.* 직접 집계(C7 코드를 거치지 않음). AW_Origin 읽기 전용.
SET NOCOUNT ON;
SELECT DB_NAME() AS db,
       (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0) AS userTables,
       (SELECT COUNT(*) FROM sys.foreign_keys) AS fks,
       (SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1) AS disabledFks,
       (SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1) AS untrustedFks,
       (SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 1 AND is_instead_of_trigger = 0) AS afterTriggers,
       (SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 1 AND is_instead_of_trigger = 1) AS insteadOfTriggers,
       (SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0) AS ddlTriggers,
       (SELECT COUNT(*) FROM sys.tables AS t WHERE t.is_ms_shipped = 0
          AND NOT EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.object_id = t.object_id AND i.is_primary_key = 1)) AS tablesWithoutPk,
       (SELECT COUNT(*) FROM sys.tables AS t WHERE t.is_ms_shipped = 0
          AND EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.object_id = t.object_id AND i.index_id = 0)) AS heaps,
       (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0 AND temporal_type <> 0) AS temporalTables,
       (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0 AND is_memory_optimized = 1) AS memoryOptimizedTables,
       (SELECT COUNT(*) FROM sys.views WHERE is_ms_shipped = 0) AS views,
       (SELECT COUNT(*) FROM sys.triggers AS tr JOIN sys.views AS v ON v.object_id = tr.parent_id) AS viewTriggers;

SELECT N'ON DELETE' AS side, delete_referential_action_desc AS action, COUNT(*) AS fks FROM sys.foreign_keys GROUP BY delete_referential_action_desc
UNION ALL
SELECT N'ON UPDATE', update_referential_action_desc, COUNT(*) FROM sys.foreign_keys GROUP BY update_referential_action_desc
ORDER BY side, action;

SELECT fk.name AS fk, OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id) AS child,
       OBJECT_SCHEMA_NAME(fk.referenced_object_id) + N'.' + OBJECT_NAME(fk.referenced_object_id) AS parent,
       fk.delete_referential_action_desc AS onDelete, fk.update_referential_action_desc AS onUpdate
FROM sys.foreign_keys AS fk
WHERE fk.delete_referential_action <> 0 OR fk.update_referential_action <> 0
ORDER BY fk.name;

SELECT OBJECT_SCHEMA_NAME(tr.parent_id) + N'.' + OBJECT_NAME(tr.parent_id) AS tbl, tr.name AS trg,
       CASE WHEN tr.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END AS kind,
       STRING_AGG(te.type_desc, ',') WITHIN GROUP (ORDER BY te.type) AS events,
       MAX(CAST(te.is_first AS int)) AS anyFirst, MAX(CAST(te.is_last AS int)) AS anyLast, MAX(CAST(tr.is_disabled AS int)) AS disabled
FROM sys.triggers AS tr JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
WHERE tr.parent_class = 1
GROUP BY tr.parent_id, tr.name, tr.is_instead_of_trigger
ORDER BY tbl, trg;

SELECT tr.name AS ddlTrigger, tr.is_disabled AS disabled, COUNT(*) AS eventRows,
       MAX(te.event_group_type_desc) AS eventGroup,
       SUM(CASE WHEN te.type_desc IN (N'CREATE_SCHEMA', N'CREATE_SEQUENCE', N'CREATE_TABLE', N'CREATE_TRIGGER', N'ALTER_TRIGGER') THEN 1 ELSE 0 END) AS installEventsCovered
FROM sys.triggers AS tr JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
WHERE tr.parent_class = 0
GROUP BY tr.name, tr.is_disabled;

-- 힙(클러스터형 인덱스 없음)과 그 PK
SELECT OBJECT_SCHEMA_NAME(t.object_id) + N'.' + t.name AS heapTable, pk.name AS pkName, pk.type_desc AS pkType
FROM sys.tables AS t
JOIN sys.indexes AS h ON h.object_id = t.object_id AND h.index_id = 0
LEFT JOIN sys.indexes AS pk ON pk.object_id = t.object_id AND pk.is_primary_key = 1
WHERE t.is_ms_shipped = 0
ORDER BY heapTable;

-- 스키마 소유자(감사 트리거 소유권 체인, SP10)
SELECT s.name AS sch, USER_NAME(s.principal_id) AS owner, COUNT(t.object_id) AS tables
FROM sys.schemas AS s JOIN sys.tables AS t ON t.schema_id = s.schema_id AND t.is_ms_shipped = 0
GROUP BY s.name, s.principal_id ORDER BY s.name;
