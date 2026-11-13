/* SP17 — 제한 로그인(dbr_origin_user)이 원본에서 할 수 있는 것과 없는 것을 EXECUTE AS LOGIN으로 직접 해 본다(sa가 가장).
   읽기 단계(제품이 원본 연결로 하는 읽기 쿼리, BaselineSql과 같은 문장) / DML / DDL / BACKUP COPY_ONLY / RESTORE.
   실행(저장소 루트): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp17_30_limited_probe.sql -ContinueOnError -Log spikes\logs\sp17-30-probe.log
   사전: sp17_10_create_limited_login.sql. 원본 DB는 CascadeLab(cl01 스키마 사용).
   변경 있는 문장은 트랜잭션 + ROLLBACK 안에서 한다. 권한이 있어서 성공하면 그 사실이 곧 발견이다(원본 권한이 최소가 아니라는 뜻).
   남기는 것: /var/opt/mssql/backup/sp17_probe_limited.bak(호스트 ORIGIN_BACKUP_DIR 아래, COPY_ONLY 백업 1개). 지우려면 호스트에서 그 파일을 삭제한다.
   RESTORE 시도는 새 이름(CascadeLab_sp17_copy)으로만 한다: 권한이 있어서 성공해도 원본은 안 건드리고, 만들어진 DB는 끝의 정리 배치가 지운다. */
SET NOCOUNT ON;
USE master;
GO
IF SUSER_ID(N'dbr_origin_user') IS NULL THROW 50000, N'dbr_origin_user가 없다: sp17_10_create_limited_login.sql을 먼저 실행한다', 1;
GO
DECLARE @tests TABLE (n int IDENTITY(1, 1), label varchar(40), expectation varchar(60), sqltext nvarchar(max));
INSERT @tests (label, expectation, sqltext) VALUES
 ('identity',            'ok',                    N'SELECT CAST(@@SERVERNAME AS nvarchar(128)) AS s, CAST(SERVERPROPERTY(''MachineName'') AS nvarchar(128)) AS m;'),
 ('instance_marker',     'ok(행 없음이 정상)',      N'SELECT CAST(value AS nvarchar(128)) AS v FROM sys.extended_properties WHERE class = 0 AND name = N''dbripple_instance'';'),
 ('source_precheck',     'ok',                    N'SELECT DB_ID(N''CascadeLab'') AS id, (SELECT state_desc FROM sys.databases WHERE name = N''CascadeLab'') AS st;'),
 ('fingerprint_server',  'ok',                    N'SELECT CAST(SERVERPROPERTY(''ProductVersion'') AS nvarchar(128)) AS v, (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N''nested triggers'') AS nested;'),
 ('fingerprint_db',      'ok',                    N'SELECT collation_name, compatibility_level, is_trustworthy_on, is_broker_enabled, is_cdc_enabled, is_published FROM sys.databases WHERE name = N''CascadeLab'';'),
 ('fingerprint_session', 'ok',                    N'SELECT CAST(s.date_format AS nvarchar(3)) AS df FROM sys.dm_exec_sessions AS s WHERE s.session_id = @@SPID;'),
 ('special_filegroups',  'ok',                    N'USE CascadeLab; SELECT name, type FROM sys.filegroups WHERE type IN (N''FD'', N''FX'');'),
 ('assemblies',          'ok',                    N'USE CascadeLab; SELECT COUNT(*) AS n FROM sys.assemblies WHERE is_user_defined = 1;'),
 ('read_table',          'ok',                    N'USE CascadeLab; SELECT COUNT(*) AS n FROM cl01.A;'),
 ('dml_zero_rows',       'ok',                    N'USE CascadeLab; BEGIN TRANSACTION; DELETE FROM cl01.A WHERE id = -1; ROLLBACK TRANSACTION;'),
 ('ddl_create_table',    '실패(262 예상)',           N'USE CascadeLab; BEGIN TRANSACTION; CREATE TABLE dbo.sp17_probe (id int); ROLLBACK TRANSACTION;'),
 ('ddl_alter_table',     '실패(권한 오류 예상)',       N'USE CascadeLab; BEGIN TRANSACTION; ALTER TABLE cl01.A ADD sp17_probe int NULL; ROLLBACK TRANSACTION;'),
 ('ddl_create_trigger',  '실패(권한 오류 예상)',       N'USE CascadeLab; BEGIN TRANSACTION; EXEC(N''CREATE TRIGGER cl01.sp17_probe_trg ON cl01.A AFTER INSERT AS RETURN;''); ROLLBACK TRANSACTION;'),
 ('create_user',         '실패(권한 오류 예상)',       N'USE CascadeLab; BEGIN TRANSACTION; CREATE USER sp17_probe_user WITHOUT LOGIN; ROLLBACK TRANSACTION;'),
 ('backup_copy_only',    'ok(원본에 쓰는 것은 백업 파일뿐)', N'BACKUP DATABASE [CascadeLab] TO DISK = N''/var/opt/mssql/backup/sp17_probe_limited.bak'' WITH COPY_ONLY, INIT;'),
 ('restore_headeronly',  '실패(권한 오류 예상)',       N'RESTORE HEADERONLY FROM DISK = N''/var/opt/mssql/backup/sp17_probe_limited.bak'';'),
 ('restore_new_name',    '실패(3110 또는 262 예상)',   N'RESTORE DATABASE [CascadeLab_sp17_copy] FROM DISK = N''/var/opt/mssql/backup/sp17_probe_limited.bak'' WITH RECOVERY;'),
 ('alter_database',      '실패(권한 오류 예상)',       N'ALTER DATABASE [CascadeLab] SET RECURSIVE_TRIGGERS OFF;'),
 ('create_database',     '실패(262 예상)',           N'CREATE DATABASE [CascadeLab_sp17_probe];');

DECLARE @res TABLE (n int, label varchar(40), expectation varchar(60), ok bit, errNo int NULL, msg nvarchar(160) NULL);
DECLARE @i int = 1, @cnt int = (SELECT COUNT(*) FROM @tests), @label varchar(40), @exp varchar(60), @sql nvarchar(max), @ok bit, @errNo int, @msg nvarchar(160);
WHILE @i <= @cnt
BEGIN
    SELECT @label = label, @exp = expectation, @sql = sqltext FROM @tests WHERE n = @i;
    SELECT @ok = 0, @errNo = NULL, @msg = NULL;
    EXECUTE AS LOGIN = N'dbr_origin_user';
    BEGIN TRY
        EXEC sys.sp_executesql @sql;
        SET @ok = 1;
    END TRY
    BEGIN CATCH
        SELECT @errNo = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 160);
    END CATCH;
    IF XACT_STATE() = -1 ROLLBACK TRANSACTION;
    REVERT;
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    INSERT @res VALUES (@i, @label, @exp, @ok, @errNo, @msg);
    SET @i += 1;
END;
SELECT label, expectation, ok, errNo, msg FROM @res ORDER BY n;
-- 판정: 읽기·DML·BACKUP은 ok, DDL·RESTORE·ALTER DATABASE·CREATE DATABASE는 실패여야 한다(권한이 최소). 어긋나면 FAIL → docs/spikes/SP17.md에 실측을 적는다.
SELECT 'H1_reads_dml_and_copy_only_backup_succeed' AS hypothesis,
       CASE WHEN NOT EXISTS (SELECT 1 FROM @res WHERE label IN ('identity', 'source_precheck', 'fingerprint_server', 'fingerprint_db', 'fingerprint_session', 'special_filegroups', 'assemblies', 'read_table', 'dml_zero_rows', 'backup_copy_only') AND ok = 0) THEN 'PASS' ELSE 'FAIL' END AS verdict,
       (SELECT STRING_AGG(CONCAT(label, ':', ISNULL(CAST(errNo AS varchar(10)), '?')), ' ; ') FROM @res WHERE label IN ('identity', 'source_precheck', 'fingerprint_server', 'fingerprint_db', 'fingerprint_session', 'special_filegroups', 'assemblies', 'read_table', 'dml_zero_rows', 'backup_copy_only') AND ok = 0) AS detail
UNION ALL
SELECT 'H2_ddl_restore_alterdb_createdb_are_denied',
       CASE WHEN NOT EXISTS (SELECT 1 FROM @res WHERE label IN ('ddl_create_table', 'ddl_alter_table', 'ddl_create_trigger', 'create_user', 'restore_headeronly', 'restore_new_name', 'alter_database', 'create_database') AND ok = 1) THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(label, ' ; ') FROM @res WHERE label IN ('ddl_create_table', 'ddl_alter_table', 'ddl_create_trigger', 'create_user', 'restore_headeronly', 'restore_new_name', 'alter_database', 'create_database') AND ok = 1);
GO
/* 정리: 권한이 있어서 새 이름 DB가 만들어졌다면 지운다(sa) */
IF DB_ID(N'CascadeLab_sp17_copy') IS NOT NULL BEGIN ALTER DATABASE CascadeLab_sp17_copy SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE CascadeLab_sp17_copy; END;
IF DB_ID(N'CascadeLab_sp17_probe') IS NOT NULL BEGIN ALTER DATABASE CascadeLab_sp17_probe SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE CascadeLab_sp17_probe; END;
GO
