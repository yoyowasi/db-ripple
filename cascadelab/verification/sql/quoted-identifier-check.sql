-- CascadeLab·CascadeLab_RT 모듈의 생성 시 SET 옵션 확인(읽기 전용) + 두 DB에 붙은 다른 세션.
-- 실행: .\scripts\run-sql.ps1 cascadelab\verification\sql\quoted-identifier-check.sql -Database master -Log cascadelab\verification\logs\<이름>.log
SET NOCOUNT ON;
SELECT N'session' AS what, SESSIONPROPERTY('QUOTED_IDENTIFIER') AS quoted_identifier, SESSIONPROPERTY('ANSI_NULLS') AS ansi_nulls;
SELECT s.session_id, DB_NAME(s.database_id) AS db, s.status, s.program_name, r.command
FROM sys.dm_exec_sessions AS s
LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
WHERE s.session_id <> @@SPID AND s.database_id IN (DB_ID(N'CascadeLab'), DB_ID(N'CascadeLab_RT'));
GO
USE CascadeLab;
GO
SET NOCOUNT ON;
SELECT DB_NAME() AS db, OBJECT_SCHEMA_NAME(m.object_id) AS sch, OBJECT_NAME(m.object_id) AS obj, o.type_desc,
       m.uses_quoted_identifier, m.uses_ansi_nulls
FROM sys.sql_modules AS m
JOIN sys.objects AS o ON o.object_id = m.object_id
ORDER BY sch, obj;
GO
USE CascadeLab_RT;
GO
SET NOCOUNT ON;
SELECT DB_NAME() AS db, OBJECT_SCHEMA_NAME(m.object_id) AS sch, OBJECT_NAME(m.object_id) AS obj, o.type_desc,
       m.uses_quoted_identifier, m.uses_ansi_nulls
FROM sys.sql_modules AS m
JOIN sys.objects AS o ON o.object_id = m.object_id
ORDER BY sch, obj;
GO
