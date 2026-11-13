/* SP21 정리: 미니 판 DB SpikeLab_SP21을 지운다(sp21 스키마 정리는 sp21_23_outside_trigger_drop.sql).
   실행: .\scripts\run-sql.ps1 spikes\sql\sp21_99_drop.sql -Log spikes\logs\sp21-99-drop.log */
USE master;
GO
IF DB_ID(N'SpikeLab_SP21') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP21 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP21;
END;
GO
