-- 읽기 전용: AW uWorkOrder 트리거 본문과 ErrorLog·DatabaseLog 행 수
SELECT OBJECT_DEFINITION(OBJECT_ID(N'Production.uWorkOrder')) AS uWorkOrderDefinition;
SELECT (SELECT COUNT(*) FROM dbo.ErrorLog) AS errorLogRows, (SELECT COUNT(*) FROM dbo.DatabaseLog) AS databaseLogRows;
GO
