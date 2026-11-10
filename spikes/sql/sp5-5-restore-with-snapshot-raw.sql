-- SP5-5: sp5-4 보강. 스냅샷이 있을 때 RESTORE가 내는 원래 오류(3013 앞의 오류)를 TRY 없이 본다. master에서 -ContinueOnError로 실행.
-- 이름 고정: AW_SnapTest, AW_SnapTest_Snap (AW_Origin은 읽기만).
IF DB_ID(N'AW_SnapTest_Snap') IS NOT NULL DROP DATABASE AW_SnapTest_Snap;
IF DB_ID(N'AW_SnapTest') IS NOT NULL DROP DATABASE AW_SnapTest;
GO
BACKUP DATABASE AW_Origin TO DISK = N'/var/opt/mssql/backup/AW_SnapTest.bak' WITH COPY_ONLY, INIT;
GO
RESTORE DATABASE AW_SnapTest FROM DISK = N'/var/opt/mssql/backup/AW_SnapTest.bak'
WITH MOVE N'AdventureWorks' TO N'/var/opt/mssql/data/AW_SnapTest_AdventureWorks.mdf',
     MOVE N'AdventureWorks_log' TO N'/var/opt/mssql/data/AW_SnapTest_AdventureWorks_log.ldf';
GO
CREATE DATABASE AW_SnapTest_Snap ON (NAME = [AdventureWorks], FILENAME = N'/var/opt/mssql/data/AW_SnapTest_Snap_AdventureWorks.ss')
AS SNAPSHOT OF AW_SnapTest;
GO
-- 스냅샷이 있는 상태에서 RESTORE … REPLACE (거부 예상: 원래 오류 번호를 본다)
RESTORE DATABASE AW_SnapTest FROM DISK = N'/var/opt/mssql/backup/AW_SnapTest.bak' WITH REPLACE;
GO
-- 스냅샷을 지운 뒤에는 같은 RESTORE가 되는지
DROP DATABASE AW_SnapTest_Snap;
GO
RESTORE DATABASE AW_SnapTest FROM DISK = N'/var/opt/mssql/backup/AW_SnapTest.bak' WITH REPLACE;
GO
SELECT name, state_desc FROM sys.databases WHERE name LIKE N'AW_SnapTest%';
GO
DROP DATABASE AW_SnapTest;
GO
