/* SP18 — 원본에 데이터베이스 스냅샷을 만들 때 실패하는 경우의 오류 번호(제한 로그인, 잘못된 경로, 이름 충돌, 없는 원본, 권한을 준 뒤 성공)
   [적용]의 검증 모드를 정할 때 "스냅샷 실패 = 오류, KEYSET은 명시 동의"로 가르려면 실패의 종류를 오류 번호로 알아야 한다.
   실행(저장소 루트, sa, 원본 모사 컨테이너): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp18_10_snapshot_errors.sql -ContinueOnError -Log spikes\logs\sp18-10.log
   사전: 원본 모사 컨테이너에 CascadeLab과 CascadeLab_RT가 있다(SP17 2단계), sp17_10_create_limited_login.sql 실행(시나리오 s1·s9).
   만드는 것: 스냅샷 DB CascadeLab_sp18(성공한 시나리오가 만들고 같은 배치에서 지운다), 시나리오 s9의 임시 권한(GRANT CREATE ANY DATABASE → 같은 시나리오에서 REVOKE).
   원본 CascadeLab의 데이터·스키마는 건드리지 않는다. 실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
SET NOCOUNT ON;
USE master;
GO
IF DB_ID(N'CascadeLab') IS NULL THROW 50000, N'CascadeLab이 없다', 1;
IF SUSER_ID(N'dbr_origin_user') IS NULL THROW 50000, N'dbr_origin_user가 없다: sp17_10_create_limited_login.sql을 먼저 실행한다', 1;
GO
IF DB_ID(N'CascadeLab_sp18') IS NOT NULL DROP DATABASE CascadeLab_sp18;   -- 이전 실행이 남긴 것
GO
DECLARE @dir nvarchar(512) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512));
DECLARE @res TABLE (n int, scenario varchar(60), asWho varchar(30), ok bit, errNo int NULL, sev int NULL, st int NULL, msg nvarchar(200) NULL);
DECLARE @sc TABLE (n int, scenario varchar(60), asWho varchar(30), snapName sysname, source sysname, dirPath nvarchar(512), extraFile bit);
INSERT @sc VALUES
 (1, 's1_limited_login_good_path',         'limited', N'CascadeLab_sp18', N'CascadeLab', @dir, 0),
 (2, 's2_sa_bad_directory',                'sa',      N'CascadeLab_sp18', N'CascadeLab', N'/no/such/dir/', 0),
 (3, 's3_sa_good_path_baseline',           'sa',      N'CascadeLab_sp18', N'CascadeLab', @dir, 0),
 (4, 's4_sa_name_collision_existing_db',   'sa',      N'CascadeLab_RT',   N'CascadeLab', @dir, 0),
 (5, 's5_sa_source_does_not_exist',        'sa',      N'CascadeLab_sp18', N'NoSuchDb_sp18', @dir, 0),
 (6, 's6_sa_extra_bogus_file',             'sa',      N'CascadeLab_sp18', N'CascadeLab', @dir, 1),
 (7, 's7_sa_root_dir_not_writable',        'sa',      N'CascadeLab_sp18', N'CascadeLab', N'/', 0);

DECLARE @n int = 1, @cnt int = (SELECT COUNT(*) FROM @sc);
DECLARE @scenario varchar(60), @who varchar(30), @snap sysname, @src sysname, @dirp nvarchar(512), @extra bit;
DECLARE @files nvarchar(max), @sql nvarchar(max), @ok bit, @errNo int, @sev int, @st int, @msg nvarchar(200);
WHILE @n <= @cnt
BEGIN
    SELECT @scenario = scenario, @who = asWho, @snap = snapName, @src = source, @dirp = dirPath, @extra = extraFile FROM @sc WHERE n = @n;
    -- 원본 CascadeLab의 데이터 파일(type 0) 논리 이름으로 파일 목록을 만든다. 없는 원본(s5)은 CascadeLab의 파일 이름을 그대로 쓴다(원본 이름만 다르게).
    SELECT @files = STRING_AGG(CAST(N'(NAME = ' + QUOTENAME(f.name, '''') + N', FILENAME = ' + QUOTENAME(@dirp + f.name + N'_sp18.ss', '''') + N')' AS nvarchar(max)), N', ')
    FROM sys.master_files AS f WHERE f.database_id = DB_ID(N'CascadeLab') AND f.type = 0;
    IF @extra = 1 SET @files = @files + N', (NAME = N''bogus_sp18'', FILENAME = ' + QUOTENAME(@dirp + N'bogus_sp18.ss', '''') + N')';
    SET @sql = N'CREATE DATABASE ' + QUOTENAME(@snap) + N' ON ' + @files + N' AS SNAPSHOT OF ' + QUOTENAME(@src) + N';';
    SELECT @ok = 0, @errNo = NULL, @sev = NULL, @st = NULL, @msg = NULL;
    IF @who = 'limited' EXECUTE AS LOGIN = N'dbr_origin_user';
    BEGIN TRY
        EXEC sys.sp_executesql @sql;
        SET @ok = 1;
    END TRY
    BEGIN CATCH
        SELECT @errNo = ERROR_NUMBER(), @sev = ERROR_SEVERITY(), @st = ERROR_STATE(), @msg = LEFT(ERROR_MESSAGE(), 200);
    END CATCH;
    IF @who = 'limited' REVERT;
    INSERT @res VALUES (@n, @scenario, @who, @ok, @errNo, @sev, @st, @msg);
    IF DB_ID(N'CascadeLab_sp18') IS NOT NULL AND @ok = 1 DROP DATABASE CascadeLab_sp18;   -- 성공한 스냅샷은 바로 지운다
    SET @n += 1;
END;

-- s9: 제한 로그인에 CREATE ANY DATABASE를 임시로 주고 같은 스냅샷을 만든다(권한을 주면 성공하는지, 최소 권한에 이 권한이 더 필요한지). 끝에서 반드시 REVOKE.
BEGIN TRY
    GRANT CREATE ANY DATABASE TO [dbr_origin_user];
    SET @sql = N'CREATE DATABASE [CascadeLab_sp18] ON ' + (SELECT STRING_AGG(CAST(N'(NAME = ' + QUOTENAME(f.name, '''') + N', FILENAME = ' + QUOTENAME(@dir + f.name + N'_sp18.ss', '''') + N')' AS nvarchar(max)), N', ')
                                                              FROM sys.master_files AS f WHERE f.database_id = DB_ID(N'CascadeLab') AND f.type = 0) + N' AS SNAPSHOT OF [CascadeLab];';
    SELECT @ok = 0, @errNo = NULL, @sev = NULL, @st = NULL, @msg = NULL;
    EXECUTE AS LOGIN = N'dbr_origin_user';
    BEGIN TRY
        EXEC sys.sp_executesql @sql;
        SET @ok = 1;
    END TRY
    BEGIN CATCH
        SELECT @errNo = ERROR_NUMBER(), @sev = ERROR_SEVERITY(), @st = ERROR_STATE(), @msg = LEFT(ERROR_MESSAGE(), 200);
    END CATCH;
    REVERT;
    INSERT @res VALUES (9, 's9_limited_with_create_any_database', 'limited', @ok, @errNo, @sev, @st, @msg);
    IF DB_ID(N'CascadeLab_sp18') IS NOT NULL AND @ok = 1 DROP DATABASE CascadeLab_sp18;
END TRY
BEGIN CATCH
    INSERT @res VALUES (9, 's9_limited_with_create_any_database', 'limited', 0, ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), LEFT(ERROR_MESSAGE(), 200));
END CATCH;
REVOKE CREATE ANY DATABASE FROM [dbr_origin_user];

SELECT n, scenario, asWho, ok, errNo, sev, st, msg FROM @res ORDER BY n;
-- 판정(예측은 쓴 시점의 추정): 성공 경로는 PASS, 나머지는 오류 번호를 docs/spikes/SP18.md 표에 옮긴다. 번호가 서로 다르면 제품이 종류별로 가를 수 있다.
SELECT 'H1_good_path_by_sa_succeeds' AS hypothesis, CASE WHEN EXISTS (SELECT 1 FROM @res WHERE scenario = 's3_sa_good_path_baseline' AND ok = 1) THEN 'PASS' ELSE 'FAIL' END AS verdict
UNION ALL SELECT 'H2_limited_login_fails_with_permission_error_262', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE scenario = 's1_limited_login_good_path' AND ok = 0 AND errNo = 262) THEN 'PASS' ELSE 'FAIL' END
UNION ALL SELECT 'H3_bad_directory_fails_with_file_system_error_5133', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE scenario = 's2_sa_bad_directory' AND ok = 0 AND errNo = 5133) THEN 'PASS' ELSE 'FAIL' END
UNION ALL SELECT 'H4_name_collision_fails_with_1801', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE scenario = 's4_sa_name_collision_existing_db' AND ok = 0 AND errNo = 1801) THEN 'PASS' ELSE 'FAIL' END
UNION ALL SELECT 'H5_missing_source_fails_with_911', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE scenario = 's5_sa_source_does_not_exist' AND ok = 0 AND errNo = 911) THEN 'PASS' ELSE 'FAIL' END
UNION ALL SELECT 'H6_create_any_database_lets_limited_login_succeed', CASE WHEN EXISTS (SELECT 1 FROM @res WHERE scenario = 's9_limited_with_create_any_database' AND ok = 1) THEN 'PASS' ELSE 'FAIL' END;
GO
-- 확인: 이 스크립트가 남긴 것이 없어야 한다(스냅샷 DB 0개, 제한 로그인의 임시 권한 0개)
SELECT 'leftover_snapshot_dbs' AS what, COUNT(*) AS n FROM sys.databases WHERE name = N'CascadeLab_sp18'
UNION ALL SELECT 'leftover_create_any_database_grants', COUNT(*) FROM sys.server_permissions WHERE grantee_principal_id = SUSER_ID(N'dbr_origin_user') AND permission_name = N'CREATE ANY DATABASE' AND state = 'G';
GO
