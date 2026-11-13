-- CL24: CATCH에서 ROLLBACK한 뒤 로그 프로시저(uspLogError 모양)가 쓰는 프로시저(answer-format v0.3).
--   EXEC cl24.usp_Withdraw @id = 1, @amt = 80 (잔액 50 → CHECK 위반 547):
--     TRY: BEGIN TRANSACTION → UPDATE(547) → CATCH: ROLLBACK TRANSACTION → EXEC cl24.usp_LogError(ErrLog INSERT, 트랜잭션 밖 자동 커밋) → RETURN -1.
--   실행 봉투(바깥 트랜잭션 1겹) 안에서는 ROLLBACK이 바깥 트랜잭션까지 되돌려 @@TRANCOUNT가 1 → 0이 되고, 프로시저가 돌아올 때 266(트랜잭션 수 불일치)이 난다.
--   기대(손 정답, SP11로 확정할 것): 문장 failed, 오류 266, isToolFault = false(수집 장치 오류 아님), 롤백 뒤 남은 ErrLog 1행(leavesResidue, CL12a와 같은 성질).
--   ErrLog에는 메시지 본문을 넣지 않는다(서버 메시지에 DB 이름이 들어가 평가 DB마다 달라진다). 번호·프로시저 이름·대상 id만 넣는다.
--   ErrLog 키는 IDENTITY라 같은 원본에서 반복해도 키가 겹치지 않는다(남은 행은 반복마다 1행씩 쌓인다).
USE CascadeLab;
GO
CREATE SCHEMA cl24;
GO
CREATE TABLE cl24.Acct (
    id  int NOT NULL CONSTRAINT PK_cl24_Acct PRIMARY KEY,
    bal int NOT NULL CONSTRAINT CK_cl24_Acct_bal CHECK (bal >= 0)
);
CREATE TABLE cl24.ErrLog (
    log_id   int     IDENTITY(1, 1) NOT NULL CONSTRAINT PK_cl24_ErrLog PRIMARY KEY,
    err_no   int     NOT NULL,
    err_proc sysname NULL,
    ref_id   int     NOT NULL
);
GO
-- AW의 dbo.uspLogError 모양: CATCH 안에서만 부르고 ERROR_*()를 읽어 로그 테이블에 쓴다.
CREATE PROCEDURE cl24.usp_LogError @ref_id int AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl24.ErrLog (err_no, err_proc, ref_id)
    VALUES (ERROR_NUMBER(), ERROR_PROCEDURE(), @ref_id);
END;
GO
CREATE PROCEDURE cl24.usp_Withdraw @id int, @amt int AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE cl24.Acct SET bal = bal - @amt WHERE id = @id;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        EXEC cl24.usp_LogError @ref_id = @id;
        RETURN -1;
    END CATCH;
    RETURN 0;
END;
GO
INSERT INTO cl24.Acct (id, bal) VALUES (1, 50), (2, 500);
GO
