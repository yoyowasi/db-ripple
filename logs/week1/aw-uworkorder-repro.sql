-- uWorkOrder 경로 재현(AW_SpikeB 전용, 스파이크 복사본). WorkOrder.OrderQty UPDATE → uWorkOrder가 TransactionHistory INSERT(ActualCost 미지정)
-- → 실패하면 CATCH에서 ROLLBACK 뒤 uspLogError가 ErrorLog에 트랜잭션 밖 행을 남기는지 본다. run-sql.ps1 -Database AW_SpikeB -ContinueOnError로 실행.
SELECT DB_NAME() AS db, (SELECT COUNT(*) FROM dbo.ErrorLog) AS errorLogBefore;
SELECT c.name, c.is_nullable, OBJECT_NAME(dc.object_id) AS defaultConstraint
FROM sys.columns AS c LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = OBJECT_ID(N'Production.TransactionHistory') AND c.name = N'ActualCost';
GO
BEGIN TRY
    BEGIN TRAN;
    UPDATE Production.WorkOrder SET OrderQty = OrderQty + 1 WHERE WorkOrderID = 1;
    SELECT N'UPDATE_OK' AS result, @@TRANCOUNT AS tranCount;
    ROLLBACK;
END TRY
BEGIN CATCH
    SELECT N'UPDATE_FAILED' AS result, ERROR_NUMBER() AS errNo, ERROR_PROCEDURE() AS errProc,
           LEFT(ERROR_MESSAGE(), 200) AS errMsg, XACT_STATE() AS xactState, @@TRANCOUNT AS tranCount;
    IF @@TRANCOUNT > 0 ROLLBACK;
END CATCH;
GO
SELECT (SELECT COUNT(*) FROM dbo.ErrorLog) AS errorLogAfter, @@TRANCOUNT AS tranCountAfter;
SELECT TOP (2) ErrorLogID, ErrorNumber, ErrorProcedure, LEFT(ErrorMessage, 160) AS msg FROM dbo.ErrorLog ORDER BY ErrorLogID DESC;
GO
