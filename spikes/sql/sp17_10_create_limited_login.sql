/* SP17 — 원본 서버 모사 컨테이너(sql-origin, SQL Server 2022)에 "제한 로그인" dbr_origin_user를 만든다: db_backupoperator + 본인 DML(db_datareader·db_datawriter)만.
   db_owner·db_ddladmin·sysadmin·dbcreator 아님, 서버 수준 권한 없음(기본 public만).
   비밀번호는 .env의 ORIGIN_SQL_PASSWORD에서 읽는다. 이 파일에는 {{PASSWORD}} 토큰만 있고, 헬퍼(sp_container-run.ps1 -PasswordFromEnv)가 실행 때만 바꾼다. 값은 출력하지 않는다.
   사전: .env에 ORIGIN_SQL_PASSWORD=<8자 이상, 대소문자·숫자·기호 3종 이상, $ 없음>. 원본 DB(CascadeLab)가 이 컨테이너에 있어야 한다(docs/spikes/SP17.md 2단계).
   실행(저장소 루트): . .\scripts\env.ps1
                    .\spikes\sql\sp_container-run.ps1 spikes\sql\sp17_10_create_limited_login.sql -PasswordFromEnv ORIGIN_SQL_PASSWORD -Log spikes\logs\sp17-10-login.log
   멱등이다(이미 있으면 비밀번호만 다시 맞춘다). 원본 모사 컨테이너에서만 쓴다(운영·회사 서버에 쓰지 않는다). */
USE master;
GO
IF SUSER_ID(N'dbr_origin_user') IS NULL
    CREATE LOGIN [dbr_origin_user] WITH PASSWORD = N'{{PASSWORD}}', CHECK_POLICY = OFF, DEFAULT_DATABASE = [master];
ELSE
    ALTER LOGIN [dbr_origin_user] WITH PASSWORD = N'{{PASSWORD}}';
GO
USE CascadeLab;
GO
IF USER_ID(N'dbr_origin_user') IS NULL
    CREATE USER [dbr_origin_user] FOR LOGIN [dbr_origin_user];
ALTER ROLE db_backupoperator ADD MEMBER [dbr_origin_user];
ALTER ROLE db_datareader ADD MEMBER [dbr_origin_user];
ALTER ROLE db_datawriter ADD MEMBER [dbr_origin_user];
GO
-- 확인(비밀번호는 출력하지 않는다): 서버 수준 역할 없음, DB 역할은 위 셋뿐
SELECT 'server_roles' AS what, ISNULL(STRING_AGG(r.name, N','), N'(none)') AS value
FROM sys.server_role_members AS m INNER JOIN sys.server_principals AS r ON r.principal_id = m.role_principal_id
WHERE m.member_principal_id = SUSER_ID(N'dbr_origin_user');
SELECT 'db_roles' AS what, STRING_AGG(r.name, N',') WITHIN GROUP (ORDER BY r.name) AS value
FROM sys.database_role_members AS m INNER JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
WHERE m.member_principal_id = USER_ID(N'dbr_origin_user');
GO
