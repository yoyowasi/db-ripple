/* SP19 — 원본 모사 컨테이너의 SP19 합성 DB에 제한 로그인 dbr_origin_user의 사용자를 만들고 SP17과 같은 역할(db_backupoperator·db_datareader·db_datawriter)만 준다.
   로그인은 SP17(sp17_10_create_limited_login.sql)에서 이미 만들었다(여기서는 비밀번호를 다루지 않는다).
   실행: ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_15_limited_login.sql -Vars @{ DbName = 'SP19_2G' } -Log spikes/logs/sp19-15-login-2g.log */
:on error exit
SET NOCOUNT ON;
IF N'$(DbName)' NOT LIKE N'SP19[_]%' THROW 50001, N'SP19 전용 DB만', 1;
IF SUSER_ID(N'dbr_origin_user') IS NULL THROW 50002, N'로그인 dbr_origin_user가 없음(SP17 sp17_10 먼저)', 1;
GO
USE [$(DbName)];
IF USER_ID(N'dbr_origin_user') IS NULL CREATE USER [dbr_origin_user] FOR LOGIN [dbr_origin_user];
ALTER ROLE db_backupoperator ADD MEMBER [dbr_origin_user];
ALTER ROLE db_datareader ADD MEMBER [dbr_origin_user];
ALTER ROLE db_datawriter ADD MEMBER [dbr_origin_user];
SELECT 'db_roles' AS what, STRING_AGG(r.name, N',') WITHIN GROUP (ORDER BY r.name) AS value
FROM sys.database_role_members AS m INNER JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
WHERE m.member_principal_id = USER_ID(N'dbr_origin_user');
SELECT 'server_roles' AS what, ISNULL((SELECT STRING_AGG(r.name, N',') FROM sys.server_role_members AS m INNER JOIN sys.server_principals AS r ON r.principal_id = m.role_principal_id
       WHERE m.member_principal_id = SUSER_ID(N'dbr_origin_user')), N'(none)') AS value;
GO
