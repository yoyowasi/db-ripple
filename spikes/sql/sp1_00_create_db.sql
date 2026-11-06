/* SP1 — 실험 DB 생성(master에서 실행). 다음: common_audit_store.sql, common_hand_audit.sql을 -d SpikeLab_SP1로 실행 */
USE master;
GO
IF DB_ID(N'SpikeLab_SP1') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP1 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP1;
END
GO
CREATE DATABASE SpikeLab_SP1;
GO
ALTER DATABASE SpikeLab_SP1 SET RECURSIVE_TRIGGERS OFF;
GO
SELECT name, is_recursive_triggers_on, compatibility_level,
       (SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = N'nested triggers') AS nestedTriggers,
       CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS productVersion
FROM sys.databases WHERE name = N'SpikeLab_SP1';
GO
