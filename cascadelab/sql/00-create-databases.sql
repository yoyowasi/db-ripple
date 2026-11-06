-- CascadeLab 원본 역할 DB 생성. 순수 T-SQL + GO (sqlcmd 변수 없음).
-- CascadeLab    : RECURSIVE_TRIGGERS OFF, 스키마 cl01~cl08
-- CascadeLab_RT : RECURSIVE_TRIGGERS ON,  스키마 cl08만 (CL08b용)
-- 샌드박스·스냅샷(CascadeLab_Sandbox, CascadeLab_Sandbox_base)은 제품 C8이 만든다. 여기서 만들지 않는다.
USE master;
GO
IF DB_ID(N'CascadeLab') IS NOT NULL
BEGIN
    ALTER DATABASE CascadeLab SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE CascadeLab;
END;
GO
CREATE DATABASE CascadeLab;
GO
ALTER DATABASE CascadeLab SET RECURSIVE_TRIGGERS OFF;
GO
IF DB_ID(N'CascadeLab_RT') IS NOT NULL
BEGIN
    ALTER DATABASE CascadeLab_RT SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE CascadeLab_RT;
END;
GO
CREATE DATABASE CascadeLab_RT;
GO
ALTER DATABASE CascadeLab_RT SET RECURSIVE_TRIGGERS ON;
GO
-- 확인: 기대값 CascadeLab=0, CascadeLab_RT=1, nested triggers=1
SELECT name, is_recursive_triggers_on
FROM sys.databases
WHERE name IN (N'CascadeLab', N'CascadeLab_RT');
SELECT name, value_in_use
FROM sys.configurations
WHERE name = N'nested triggers';
GO
