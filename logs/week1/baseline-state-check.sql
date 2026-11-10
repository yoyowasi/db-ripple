-- 기준선 상태 확인(읽기 전용): 스냅샷 원본 관계, 샌드박스 감사 트리거 수·Last 지정 수, 감사 저장소 행 수
SELECT d.name, d.state_desc, DB_NAME(d.source_database_id) AS snapshot_of, d.create_date
FROM sys.databases AS d
WHERE d.name IN (N'AW_Origin', N'AW_Sandbox', N'AW_Sandbox_base', N'CascadeLab', N'CascadeLab_Sandbox', N'CascadeLab_Sandbox_base', N'CascadeLab_RT', N'CascadeLab_RT_Sandbox', N'CascadeLab_RT_Sandbox_base')
ORDER BY d.name;
GO
USE AW_Sandbox;
SELECT COUNT(*) AS auditTriggers,
       SUM(CASE WHEN te.is_last = 1 THEN 1 ELSE 0 END) AS auditLastSlots,
       SUM(CASE WHEN te.is_first = 1 THEN 1 ELSE 0 END) AS auditFirstSlots
FROM sys.triggers AS t
JOIN sys.trigger_events AS te ON te.object_id = t.object_id
WHERE t.name LIKE N'dbr[_]audit[_]%';
SELECT COUNT(*) AS userDmlTriggers FROM sys.triggers WHERE parent_class = 1 AND name NOT LIKE N'dbr[_]audit[_]%';
SELECT (SELECT COUNT(*) FROM audit.AuditHeader) AS headerRows, (SELECT COUNT(*) FROM audit.AuditRowImage) AS imageRows,
       (SELECT mode FROM audit.AuditControl WHERE id = 1) AS mode, (SELECT cap FROM audit.AuditControl WHERE id = 1) AS cap;
SELECT COUNT(*) AS modulesWithQuotedIdentifierOff FROM sys.sql_modules WHERE uses_quoted_identifier = 0;
GO
