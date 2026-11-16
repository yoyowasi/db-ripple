/* SP19 정리 — 확장 이벤트 세션 sp19_trace를 멈추고 지운다(두 컨테이너 각각). 이벤트 파일(/var/opt/mssql/log/sp19_trace*.xel)은 컨테이너 안에서 따로 지운다:
     docker exec dbripple-sql-origin bash -c "rm -f /var/opt/mssql/log/sp19_trace*.xel"   (샌드박스는 dbripple-sql)
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_29_xe_drop.sql -Service sql-origin -Log spikes/logs/sp19-29-xe-drop-origin.log
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_29_xe_drop.sql -Service sql        -Log spikes/logs/sp19-29-xe-drop-sandbox.log */
SET NOCOUNT ON;
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'sp19_trace')
    DROP EVENT SESSION [sp19_trace] ON SERVER;
SELECT 'xe_sessions_left' AS what, COUNT(*) AS value FROM sys.server_event_sessions WHERE name = N'sp19_trace';
GO
