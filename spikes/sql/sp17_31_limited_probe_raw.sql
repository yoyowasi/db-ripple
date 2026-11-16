/* SP17 보조(2026-11-16 실행 중 추가) — sp17_30의 TRY/CATCH는 마지막 오류(3013 "terminating abnormally", 5069 "statement failed")만 잡아
   RESTORE·ALTER DATABASE가 실제로 어떤 첫 오류로 막히는지 안 보인다. 같은 문장을 TRY 없이 EXECUTE AS LOGIN으로 실행해 sqlcmd가 오류 줄을 모두 찍게 한다.
   실행(저장소 루트, sa, 원본 모사 컨테이너): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp17_31_limited_probe_raw.sql -ContinueOnError -Log spikes\logs\sp17-31-probe-raw.log
   사전: sp17_10(제한 로그인), sp17_30(/var/opt/mssql/backup/sp17_probe_limited.bak). 배치마다 나눠 실패한 배치 뒤에도 REVERT가 돈다. 변경은 없다(모두 권한 오류로 막혀야 한다). */
USE master;
GO
EXECUTE AS LOGIN = N'dbr_origin_user';
GO
SELECT 'as_login' AS what, SUSER_SNAME() AS v;
GO
RESTORE HEADERONLY FROM DISK = N'/var/opt/mssql/backup/sp17_probe_limited.bak';
GO
RESTORE DATABASE [CascadeLab_sp17_copy] FROM DISK = N'/var/opt/mssql/backup/sp17_probe_limited.bak' WITH RECOVERY;
GO
RESTORE DATABASE [CascadeLab] FROM DISK = N'/var/opt/mssql/backup/sp17_probe_limited.bak' WITH REPLACE, RECOVERY;
GO
ALTER DATABASE [CascadeLab] SET RECURSIVE_TRIGGERS OFF;
GO
USE CascadeLab;
GO
ALTER TABLE cl01.A ADD sp17_probe int NULL;
GO
USE master;
GO
REVERT;
GO
SELECT 'after_revert' AS what, SUSER_SNAME() AS v;
IF DB_ID(N'CascadeLab_sp17_copy') IS NOT NULL BEGIN ALTER DATABASE CascadeLab_sp17_copy SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE CascadeLab_sp17_copy; END;
SELECT 'cl01_A_has_probe_column' AS what, COUNT(*) AS n FROM CascadeLab.sys.columns WHERE object_id = OBJECT_ID(N'CascadeLab.cl01.A') AND name = N'sp17_probe';
GO
