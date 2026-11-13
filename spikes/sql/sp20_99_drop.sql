/* SP20 정리: SpikeLab_SP20과 sp20_owner 로그인을 지운다(원본 모사 컨테이너와 샌드박스 컨테이너 양쪽에서 각각 실행).
   실행(저장소 루트): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp20_99_drop.sql -Service sql-origin -Log spikes\logs\sp20-99-origin.log
                      .\spikes\sql\sp_container-run.ps1 spikes\sql\sp20_99_drop.sql -Service sql        -Log spikes\logs\sp20-99-sandbox.log
   호스트의 .bak 파일(.data\origin-backup\sp20_owner_db.bak, .data\import\sp20_owner_db.bak)은 손으로 지운다. */
USE master;
GO
IF DB_ID(N'SpikeLab_SP20') IS NOT NULL BEGIN ALTER DATABASE SpikeLab_SP20 SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SpikeLab_SP20; END;
GO
IF SUSER_ID(N'sp20_owner') IS NOT NULL DROP LOGIN [sp20_owner];
GO
