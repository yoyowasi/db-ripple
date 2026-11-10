-- SP5-6: 전체 텍스트 카탈로그가 있는 AW를 FTS 구성 요소 없는 컨테이너에 복원했을 때의 영향. master에서 -ContinueOnError로 실행.
-- 이름 고정: AW_FtTest(임시 복사본, 끝에서 지움). AW_Origin은 건드리지 않는다.
SELECT SERVERPROPERTY('IsFullTextInstalled') AS IsFullTextInstalled;
GO
IF DB_ID(N'AW_FtTest') IS NOT NULL DROP DATABASE AW_FtTest;
GO
RESTORE DATABASE AW_FtTest FROM DISK = N'/var/opt/mssql/backup/AdventureWorks2025.bak'
WITH MOVE N'AdventureWorks' TO N'/var/opt/mssql/data/AW_FtTest_AdventureWorks.mdf',
     MOVE N'AdventureWorks_log' TO N'/var/opt/mssql/data/AW_FtTest_AdventureWorks_log.ldf';
GO
USE AW_FtTest;
SELECT OBJECT_SCHEMA_NAME(object_id) AS sch, OBJECT_NAME(object_id) AS tbl, change_tracking_state_desc, is_enabled
FROM sys.fulltext_indexes;
GO
-- 전체 텍스트 인덱스가 걸린 테이블의 DML(트랜잭션 안, 롤백)
BEGIN TRAN;
UPDATE Production.ProductReview SET Comments = Comments + N'' WHERE ProductReviewID = 1;
SELECT @@ROWCOUNT AS updatedRows;
DELETE FROM Production.ProductReview WHERE ProductReviewID = 2;
SELECT @@ROWCOUNT AS deletedRows;
ROLLBACK;
SELECT @@TRANCOUNT AS tranCountAfterRollback;
GO
-- 전체 텍스트 질의(FTS 미설치면 오류 예상)
SELECT COUNT(*) AS containsRows FROM Production.ProductReview WHERE CONTAINS(Comments, N'bike');
GO
USE master;
DROP DATABASE AW_FtTest;
GO
