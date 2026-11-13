-- CL23: 균형 잡힌 BEGIN/COMMIT 프로시저 + 트리거가 붙은 테이블 쓰기(answer-format v0.3).
--   EXEC cl23.usp_Pay @id = 1, @ledger_id = 900, @amt = 30:
--     BEGIN TRANSACTION → Acct UPDATE(trg_Acct_upd 발동 → AcctHist INSERT) → Ledger INSERT → COMMIT TRANSACTION.
--   실행 봉투(바깥 트랜잭션 1겹) 안에서 BEGIN은 @@TRANCOUNT를 2로, COMMIT은 1로 되돌리므로 균형이다(266 없음, 바깥 트랜잭션은 확정되지 않는다).
--   확인 대상: 프로시저 쓰기(PROC_DIRECT, depth 1)와 그 쓰기가 발동한 트리거 쓰기(TRIGGER, depth 2) 귀속.
--   트리거 본문은 단순 INSERT뿐이다(동적 SQL·프로시저 호출·트랜잭션 제어·외부 부작용 없음). 이 스키마가 CascadeLab의 다른 사례(특히 CL10의
--   "쓰기 대상 미상 → 모든 테이블 도달" 검사)를 거부로 바꾸지 않게 한다.
USE CascadeLab;
GO
CREATE SCHEMA cl23;
GO
CREATE TABLE cl23.Acct (
    id  int NOT NULL CONSTRAINT PK_cl23_Acct PRIMARY KEY,
    bal int NOT NULL
);
CREATE TABLE cl23.AcctHist (
    acct_id int NOT NULL CONSTRAINT PK_cl23_AcctHist PRIMARY KEY,
    bal     int NOT NULL
);
CREATE TABLE cl23.Ledger (
    ledger_id int NOT NULL CONSTRAINT PK_cl23_Ledger PRIMARY KEY,
    acct_id   int NOT NULL,
    amt       int NOT NULL
);
GO
CREATE TRIGGER cl23.trg_Acct_upd ON cl23.Acct AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl23.AcctHist (acct_id, bal)
    SELECT i.id, i.bal FROM inserted AS i;
END;
GO
CREATE PROCEDURE cl23.usp_Pay @id int, @ledger_id int, @amt int AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRANSACTION;
    UPDATE cl23.Acct SET bal = bal - @amt WHERE id = @id;
    INSERT INTO cl23.Ledger (ledger_id, acct_id, amt) VALUES (@ledger_id, @id, -@amt);
    COMMIT TRANSACTION;
    RETURN 0;
END;
GO
INSERT INTO cl23.Acct (id, bal) VALUES (1, 100), (2, 200);
GO
