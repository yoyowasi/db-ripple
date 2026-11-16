/* SP19 — 공개 AdventureWorks2025.bak을 원본 모사 컨테이너에 SP19_AW로 복원한다(AW 크기 단계의 사전 조건).
   2026-11-16 로컬 결과: 원본 모사가 SQL Server 2022(주버전 16)면 실패한다. 백업 SoftwareVersionMajor 17, DatabaseVersion 998 → Msg 3169
   ("backed up on a server running database version 998 … this server supports version 958") → Msg 3013(spikes/logs/sp19-05-aw-on-origin.log).
   그래서 AW 단계는 원본 모사를 2025(샌드박스와 같은 이미지, ORIGIN_IMAGE_TAG)로 띄운 경우에만 쓴다. sp19_00_run.ps1은 원본 주버전이 17 미만이면 AW를 건너뛴다.
   사전: .data/backup/AdventureWorks2025.bak을 .data/origin-backup/sp19_aw2025.bak으로 복사(원본 백업 폴더 = 컨테이너 /var/opt/mssql/backup). 끝나면 복사본을 지운다.
   실행(저장소 루트): ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_05_aw_on_origin.sql -Log spikes/logs/sp19-cloud-05-aw-on-origin.log
   (2022에서 실패를 다시 보려면 -ContinueOnError) */
SET NOCOUNT ON;
SELECT 'origin_major' AS what, CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS value;
GO
RESTORE HEADERONLY FROM DISK = N'/var/opt/mssql/backup/sp19_aw2025.bak';
GO
RESTORE DATABASE [SP19_AW] FROM DISK = N'/var/opt/mssql/backup/sp19_aw2025.bak'
WITH MOVE N'AdventureWorks' TO N'/var/opt/mssql/data/SP19_AW.mdf', MOVE N'AdventureWorks_log' TO N'/var/opt/mssql/data/SP19_AW_log.ldf';
GO
SELECT 'sp19_aw_exists' AS what, CASE WHEN DB_ID(N'SP19_AW') IS NULL THEN 0 ELSE 1 END AS value;
GO
