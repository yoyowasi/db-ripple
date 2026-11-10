-- CascadeLab_Sandbox 감사 장치 확인(읽기 전용). 실행: .\scripts\run-sql.ps1 cascadelab\verification\sql\sandbox-check.sql -Database CascadeLab_Sandbox -Log cascadelab\verification\logs\sandbox-check.log
SET NOCOUNT ON;
SELECT DB_NAME() AS db, OBJECT_ID(N'audit.AuditHeader') AS audit_header_id, OBJECT_ID(N'audit.AuditRowImage') AS audit_image_id,
       OBJECT_ID(N'audit.AuditControl') AS audit_control_id, OBJECT_ID(N'audit.CaptureSeq') AS capture_seq_id;
SELECT d.name, d.is_recursive_triggers_on, d.compatibility_level, d.is_read_committed_snapshot_on
FROM sys.databases AS d WHERE d.name IN (N'CascadeLab', N'CascadeLab_Sandbox', N'CascadeLab_Sandbox_base');
IF OBJECT_ID(N'audit.AuditControl') IS NOT NULL EXEC(N'SELECT id, mode, cap FROM audit.AuditControl;');
IF OBJECT_ID(N'audit.AuditHeader') IS NOT NULL EXEC(N'SELECT COUNT_BIG(*) AS header_rows, MAX(captureSeq) AS max_seq FROM audit.AuditHeader;');
PRINT '== 트리거 모듈 SET 옵션(사용자/감사별 개수) ==';
SELECT CASE WHEN o.name LIKE N'dbr[_]audit[_]%' THEN 'audit' ELSE 'user' END AS kind,
       m.uses_quoted_identifier, m.uses_ansi_nulls, COUNT(*) AS modules
FROM sys.sql_modules AS m
JOIN sys.objects AS o ON o.object_id = m.object_id
WHERE o.type = 'TR'
GROUP BY CASE WHEN o.name LIKE N'dbr[_]audit[_]%' THEN 'audit' ELSE 'user' END, m.uses_quoted_identifier, m.uses_ansi_nulls;
PRINT '== triggers (사용자 + 감사) ==';
SELECT OBJECT_SCHEMA_NAME(tr.parent_id) + N'.' + OBJECT_NAME(tr.parent_id) AS on_table, te.type_desc AS event,
       OBJECT_SCHEMA_NAME(tr.object_id) + N'.' + tr.name AS trg,
       CASE WHEN te.is_first = 1 THEN 'FIRST' WHEN te.is_last = 1 THEN 'LAST' ELSE 'NONE' END AS ord, tr.is_disabled
FROM sys.triggers AS tr
JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
WHERE tr.parent_class = 1
ORDER BY on_table, event, trg;
