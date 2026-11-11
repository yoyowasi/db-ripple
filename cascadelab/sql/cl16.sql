-- CL16: 비결정 값(E1~E3)과 새 IDENTITY를 참조하는 자식(E2, D7 짝 매핑).
--   Ord.id          IDENTITY
--   Ord.invoice_no  SEQUENCE 기본값(NEXT VALUE FOR, 트랜잭션과 무관하게 소비, A13)
--   Ord.created_at  SYSDATETIME() 기본값
--   Ord.row_guid    NEWID() 기본값
--   trg_Ord_ins     새 Ord마다 OrdLine 1행: ord_id = 새 IDENTITY, added_at = GETDATE()(트리거 안 비결정 값)
--   OrdLine.memo    부모 customer를 복사한다. 같은 문장의 여러 자식 행을 비결정 아닌 값으로 구별하기 위해서다(손 정답 match).
-- 시드 행(Ord id 1·2, OrdLine id 1·2)은 시드 때 한 번 확정되므로 그 키는 결정적이다.
USE CascadeLab;
GO
CREATE SCHEMA cl16;
GO
CREATE SEQUENCE cl16.InvoiceNo AS int START WITH 5000 INCREMENT BY 1;
GO
CREATE TABLE cl16.Ord (
    id         int              IDENTITY(1, 1) NOT NULL CONSTRAINT PK_cl16_Ord PRIMARY KEY,
    customer   varchar(20)      NOT NULL,
    note       varchar(20)      NULL,
    invoice_no int              NOT NULL CONSTRAINT DF_cl16_Ord_invoice_no DEFAULT (NEXT VALUE FOR cl16.InvoiceNo),
    created_at datetime2(3)     NOT NULL CONSTRAINT DF_cl16_Ord_created_at DEFAULT (SYSDATETIME()),
    row_guid   uniqueidentifier NOT NULL CONSTRAINT DF_cl16_Ord_row_guid DEFAULT (NEWID()),
    touched_at datetime2(3)     NULL
);
CREATE TABLE cl16.OrdLine (
    id       int         IDENTITY(1, 1) NOT NULL CONSTRAINT PK_cl16_OrdLine PRIMARY KEY,
    ord_id   int         NOT NULL CONSTRAINT FK_cl16_OrdLine_Ord REFERENCES cl16.Ord (id),
    line_no  int         NOT NULL,
    qty      int         NOT NULL,
    memo     varchar(20) NOT NULL,
    added_at datetime    NOT NULL,
    CONSTRAINT UQ_cl16_OrdLine_ord_line UNIQUE (ord_id, line_no)
);
GO
CREATE TRIGGER cl16.trg_Ord_ins ON cl16.Ord AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl16.OrdLine (ord_id, line_no, qty, memo, added_at)
    SELECT i.id, 1, 1, i.customer, GETDATE() FROM inserted AS i;
END;
GO
INSERT INTO cl16.Ord (customer) VALUES ('s1');
INSERT INTO cl16.Ord (customer) VALUES ('s2');
GO
