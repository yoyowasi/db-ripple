/* SP19 — 제품(Host) 연결이 보내는 명령마다 서버 쪽 시간을 남기는 확장 이벤트 세션 sp19_trace(측정 전용, 끝나면 sp19_29_xe_drop.sql로 지운다).
   Host는 단계별 시간을 로그·상태 API에 내지 않는다(SandboxManager.cs:225-227이 SqlBaselineSteps·SqlOriginSteps를 log 없이 만든다). 그래서 서버에서 잰다.
   대상: 응용 프로그램 이름이 db-ripple인 연결(env.ps1·Engine.cs:63)의 sql_batch_completed·rpc_completed. 측정 스크립트(sqlcmd, sp19-measure)는 빠진다.
   두 컨테이너에 각각 실행한다(같은 Docker 호스트 커널의 시계라 두 서버의 timestamp를 한 시간선으로 놓을 수 있다. Windows는 WSL2 VM, Linux는 호스트 커널).
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_20_xe_start.sql -Service sql-origin -Log spikes/logs/sp19-20-xe-origin.log
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_20_xe_start.sql -Service sql        -Log spikes/logs/sp19-20-xe-sandbox.log
   명령 본문(batch_text·statement)도 남는다. 제품 명령에는 비밀번호가 없다(PreviewUserSql은 WITHOUT LOGIN). 결과 파일에는 본문 앞 120자만 옮긴다. */
:on error exit
SET NOCOUNT ON;
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'sp19_trace')
    DROP EVENT SESSION [sp19_trace] ON SERVER;
CREATE EVENT SESSION [sp19_trace] ON SERVER
ADD EVENT sqlserver.sql_batch_completed (
    ACTION (sqlserver.session_id, sqlserver.database_name, sqlserver.client_app_name)
    WHERE sqlserver.client_app_name = N'db-ripple'),
ADD EVENT sqlserver.rpc_completed (
    ACTION (sqlserver.session_id, sqlserver.database_name, sqlserver.client_app_name)
    WHERE sqlserver.client_app_name = N'db-ripple')
ADD TARGET package0.event_file (SET filename = N'/var/opt/mssql/log/sp19_trace.xel', max_file_size = 64, max_rollover_files = 8)
WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, EVENT_RETENTION_MODE = NO_EVENT_LOSS, STARTUP_STATE = OFF);
ALTER EVENT SESSION [sp19_trace] ON SERVER STATE = START;
SELECT 'xe_running' AS what, COUNT(*) AS value FROM sys.dm_xe_sessions WHERE name = N'sp19_trace';
GO
