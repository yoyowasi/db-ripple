/* SP18 보조(2026-11-16 실행 중 추가) — sp18_10 s9에서 CREATE ANY DATABASE만으로는 제한 로그인이 스냅샷을 못 만들었다(1817 "Only the owner of database … or the system administrator").
   그러면 무엇이 있어야 하는가: (s10) CREATE ANY DATABASE + db_owner 역할, (s13) CREATE ANY DATABASE + CONTROL ON DATABASE, (s11) CREATE ANY DATABASE + DB 소유자, (s12) DB 소유자만(CREATE ANY DATABASE 없음).
   시나리오마다 제품의 검증 모드 결정 SQL(ApplySteps.cs OriginSnapshot.CapabilitySql)과 같은 HAS_PERMS_BY_NAME 값을 같이 남긴다(결정과 실제의 어긋남 확인).
   실행(저장소 루트, sa, 원본 모사 컨테이너): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp18_20_snapshot_owner.sql -ContinueOnError -Log spikes\logs\sp18-20-owner.log
   만드는 것: 전용 DB SpikeLab_SP18(끝에서 지움), 임시 권한(끝에서 REVOKE). 원본 CascadeLab은 건드리지 않는다. */
SET NOCOUNT ON;
USE master;
GO
IF SUSER_ID(N'dbr_origin_user') IS NULL THROW 50000, N'dbr_origin_user가 없다: sp17_10_create_limited_login.sql을 먼저 실행한다', 1;
GO
IF DB_ID(N'SpikeLab_SP18_ss') IS NOT NULL DROP DATABASE SpikeLab_SP18_ss;
IF DB_ID(N'SpikeLab_SP18') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP18 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP18; END;
GO
CREATE DATABASE SpikeLab_SP18;
GO
USE SpikeLab_SP18;
CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY);
CREATE USER [dbr_origin_user] FOR LOGIN [dbr_origin_user];
GO
USE master;
GO
CREATE TABLE #res (n int, scenario varchar(60), canCreateDbCheck int, hasDbControl int, isDbOwnerRole int, isOwner int, ok bit, errNo int NULL, msg nvarchar(200) NULL);
GO
/* 시나리오 하나: 제한 로그인으로 스냅샷 시도 + 그때의 권한 판정 값 */
CREATE OR ALTER PROCEDURE #try_snapshot @n int, @scenario varchar(60)
AS
BEGIN
    DECLARE @data nvarchar(512) = (SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX(N'/', REVERSE(physical_name)) + 1) FROM sys.master_files WHERE database_id = DB_ID(N'SpikeLab_SP18') AND type = 0);
    DECLARE @sql nvarchar(max) = N'CREATE DATABASE [SpikeLab_SP18_ss] ON (NAME = N''SpikeLab_SP18'', FILENAME = ' + QUOTENAME(@data + N'SpikeLab_SP18_ss.ss', '''') + N') AS SNAPSHOT OF [SpikeLab_SP18];';
    DECLARE @can int, @ctl int, @role int, @own int, @ok bit = 0, @errNo int = NULL, @msg nvarchar(200) = NULL;
    EXECUTE AS LOGIN = N'dbr_origin_user';
    SELECT @can = CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE') = 1 OR HAS_PERMS_BY_NAME(N'master', N'DATABASE', N'CREATE DATABASE') = 1 THEN 1 ELSE 0 END,
           @ctl = HAS_PERMS_BY_NAME(N'SpikeLab_SP18', N'DATABASE', N'CONTROL');
    BEGIN TRY
        EXEC sys.sp_executesql @sql;
        SET @ok = 1;
    END TRY
    BEGIN CATCH
        SELECT @errNo = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 200);
    END CATCH;
    REVERT;
    SELECT @role = CASE WHEN EXISTS (SELECT 1 FROM SpikeLab_SP18.sys.database_role_members AS m
                                     INNER JOIN SpikeLab_SP18.sys.database_principals AS r ON r.principal_id = m.role_principal_id
                                     INNER JOIN SpikeLab_SP18.sys.database_principals AS u ON u.principal_id = m.member_principal_id
                                     WHERE r.name = N'db_owner' AND u.sid = SUSER_SID(N'dbr_origin_user')) THEN 1 ELSE 0 END,
           @own = CASE WHEN (SELECT owner_sid FROM sys.databases WHERE name = N'SpikeLab_SP18') = SUSER_SID(N'dbr_origin_user') THEN 1 ELSE 0 END;
    INSERT #res VALUES (@n, @scenario, @can, @ctl, @role, @own, @ok, @errNo, @msg);
    IF DB_ID(N'SpikeLab_SP18_ss') IS NOT NULL DROP DATABASE SpikeLab_SP18_ss;
END;
GO
/* s09: CREATE ANY DATABASE만(일반 사용자, sp18_10 s9와 같은 조건을 전용 DB로) — 제품 검사는 "가능", 실제는 1817로 예상 */
GRANT CREATE ANY DATABASE TO [dbr_origin_user];
EXEC #try_snapshot 9, 's09_create_any_db_only_plain_user';
GO
/* s10: CREATE ANY DATABASE + db_owner 역할(소유자는 sa) */
EXEC (N'USE SpikeLab_SP18; ALTER ROLE db_owner ADD MEMBER [dbr_origin_user];');
EXEC #try_snapshot 10, 's10_create_any_db_plus_db_owner_role';
GO
/* s13: CREATE ANY DATABASE + CONTROL ON DATABASE만(db_owner 역할 아님) — 제품 검사를 HAS_PERMS_BY_NAME(원본, DATABASE, CONTROL)로 고칠 수 있는지 */
EXEC (N'USE SpikeLab_SP18; ALTER ROLE db_owner DROP MEMBER [dbr_origin_user]; GRANT CONTROL ON DATABASE::SpikeLab_SP18 TO [dbr_origin_user];');
EXEC #try_snapshot 13, 's13_create_any_db_plus_control_on_db';
EXEC (N'USE SpikeLab_SP18; REVOKE CONTROL ON DATABASE::SpikeLab_SP18 FROM [dbr_origin_user];');
GO
/* s11: CREATE ANY DATABASE + DB 소유자(사용자를 지우고 소유자로) */
EXEC (N'USE SpikeLab_SP18; DROP USER [dbr_origin_user];');
ALTER AUTHORIZATION ON DATABASE::SpikeLab_SP18 TO [dbr_origin_user];
EXEC #try_snapshot 11, 's11_create_any_db_plus_db_owner(owner_sid)';
GO
/* s12: DB 소유자만(CREATE ANY DATABASE 없음) */
REVOKE CREATE ANY DATABASE FROM [dbr_origin_user];
EXEC #try_snapshot 12, 's12_db_owner_only_no_create_any_db';
GO
SELECT n, scenario, canCreateDbCheck AS product_capability_check, hasDbControl, isDbOwnerRole, isOwner, ok, errNo, msg FROM #res ORDER BY n;
SELECT 'H7_capability_check_matches_actual' AS hypothesis,
       CASE WHEN NOT EXISTS (SELECT 1 FROM #res WHERE canCreateDbCheck <> ok) THEN 'PASS' ELSE 'FAIL' END AS verdict,
       (SELECT STRING_AGG(CONCAT(scenario, ': check=', canCreateDbCheck, ' actual=', ok, ' err=', ISNULL(CAST(errNo AS varchar(10)), '-')), ' ; ') FROM #res WHERE canCreateDbCheck <> ok) AS detail
UNION ALL
-- 고친 검사 후보: CREATE DATABASE 권한 AND 원본 DB CONTROL(db_owner·소유자는 CONTROL을 가진다)
SELECT 'H8_check_with_db_control_matches_actual',
       CASE WHEN NOT EXISTS (SELECT 1 FROM #res WHERE (CASE WHEN canCreateDbCheck = 1 AND hasDbControl = 1 THEN 1 ELSE 0 END) <> ok) THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(CONCAT(scenario, ': check=', CASE WHEN canCreateDbCheck = 1 AND hasDbControl = 1 THEN 1 ELSE 0 END, ' actual=', ok), ' ; ') FROM #res WHERE (CASE WHEN canCreateDbCheck = 1 AND hasDbControl = 1 THEN 1 ELSE 0 END) <> ok);
GO
/* 정리 */
REVOKE CREATE ANY DATABASE FROM [dbr_origin_user];
IF DB_ID(N'SpikeLab_SP18_ss') IS NOT NULL DROP DATABASE SpikeLab_SP18_ss;
IF DB_ID(N'SpikeLab_SP18') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP18 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP18; END;
GO
SELECT 'leftover_dbs' AS what, COUNT(*) AS n FROM sys.databases WHERE name LIKE N'SpikeLab[_]SP18%'
UNION ALL SELECT 'leftover_create_any_database_grants', COUNT(*) FROM sys.server_permissions WHERE grantee_principal_id = SUSER_ID(N'dbr_origin_user') AND permission_name = N'CREATE ANY DATABASE' AND state = 'G';
GO
