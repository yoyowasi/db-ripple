/* SP20 — 고아 dbo와 EXECUTE AS OWNER: 1단계(원본 모사 컨테이너, sa). 다른 로그인(sp20_owner)이 소유한 DB와 EXECUTE AS OWNER 모듈을 만든다.
   실행(저장소 루트): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp20_10_origin_prep.sql -Log spikes\logs\sp20-10-prep.log
   이어서: sp20_20_origin_backup.sql(원본에서 COPY_ONLY 백업) → 호스트에서 .bak을 가져오기 폴더로 복사 → sp20_30(샌드박스 sa 복원 + 확인) → sp20_40(ALTER AUTHORIZATION 뒤 확인) → sp20_99(정리)
   sp20_owner의 비밀번호는 이 스크립트가 NEWID로 즉석에서 만들어 쓰고 출력하지 않는다(그 로그인으로 접속할 일이 없다). 전용 DB SpikeLab_SP20만 만든다. 실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
SET NOCOUNT ON;
USE master;
GO
IF DB_ID(N'SpikeLab_SP20') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP20 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP20; END;
GO
IF SUSER_ID(N'sp20_owner') IS NULL
BEGIN
    DECLARE @pw nvarchar(80) = CONCAT(N'Sp20!a1', REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N''));
    DECLARE @sql nvarchar(400) = N'CREATE LOGIN [sp20_owner] WITH PASSWORD = N''' + @pw + N''', CHECK_POLICY = OFF, DEFAULT_DATABASE = [master];';
    EXEC sys.sp_executesql @sql;
END;
GO
CREATE DATABASE SpikeLab_SP20;
GO
ALTER AUTHORIZATION ON DATABASE::SpikeLab_SP20 TO [sp20_owner];
GO
USE SpikeLab_SP20;
GO
CREATE TABLE dbo.T (Id int NOT NULL CONSTRAINT PK_T PRIMARY KEY, V int NOT NULL);
CREATE TABLE dbo.Whoami (
    seq     int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Whoami PRIMARY KEY,
    step    varchar(20) NOT NULL,
    origLogin sysname NULL,   -- ORIGINAL_LOGIN()
    suser   sysname NULL,     -- SUSER_SNAME()
    usr     sysname NULL      -- USER_NAME()
);
INSERT dbo.T (Id, V) VALUES (1, 0);
GO
/* EXECUTE AS OWNER 프로시저: 실행 주체가 DB 소유자(dbo)로 바뀐다 */
CREATE PROCEDURE dbo.usp_as_owner WITH EXECUTE AS OWNER AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.Whoami (step, origLogin, suser, usr) VALUES ('proc', ORIGINAL_LOGIN(), SUSER_SNAME(), USER_NAME());
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
END;
GO
/* EXECUTE AS OWNER 트리거 */
CREATE TRIGGER dbo.trg_T_upd ON dbo.T WITH EXECUTE AS OWNER AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.Whoami (step, origLogin, suser, usr) VALUES ('trigger', ORIGINAL_LOGIN(), SUSER_SNAME(), USER_NAME());
END;
GO
/* 확인: 원본에서의 소유자와 모듈 실행(롤백) */
SELECT 'db_owner' AS k, ISNULL(SUSER_SNAME(owner_sid), N'(null: orphan)') AS v FROM sys.databases WHERE name = N'SpikeLab_SP20'
UNION ALL SELECT 'dbo_maps_to_login', ISNULL(SUSER_SNAME(sid), N'(null: orphan)') FROM sys.database_principals WHERE name = N'dbo';
GO
DECLARE @r TABLE (step varchar(20), origLogin sysname NULL, suser sysname NULL, usr sysname NULL);
BEGIN TRANSACTION;
EXEC dbo.usp_as_owner;
INSERT @r SELECT step, origLogin, suser, usr FROM dbo.Whoami ORDER BY seq;
ROLLBACK TRANSACTION;
SELECT 'origin_module_run' AS what, step, origLogin, suser, usr FROM @r;
GO
