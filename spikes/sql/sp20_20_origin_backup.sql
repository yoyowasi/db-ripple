/* SP20 — 2단계: 원본 모사 컨테이너에서 SpikeLab_SP20을 COPY_ONLY 백업한다(호스트 ORIGIN_BACKUP_DIR 아래에 파일이 생긴다).
   실행(저장소 루트): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp20_20_origin_backup.sql -Log spikes\logs\sp20-20-backup.log
   그다음 호스트에서(PowerShell): New-Item -ItemType Directory -Force .data\import | Out-Null; Copy-Item .data\origin-backup\sp20_owner_db.bak .data\import\sp20_owner_db.bak -Force
   (샌드박스 컨테이너는 .data\import를 /var/opt/mssql/import로 본다) */
SET NOCOUNT ON;
USE master;
GO
BACKUP DATABASE [SpikeLab_SP20] TO DISK = N'/var/opt/mssql/backup/sp20_owner_db.bak' WITH COPY_ONLY, INIT;
GO
