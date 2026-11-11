-- CL12: ROLLBACK 트리거, ROWCOUNT_BIG() 0행 가드, @@IDENTITY를 읽는 트리거 (trigger_blocked, 롤백 뒤 남은 변경, 관찰자 효과).
--   CL12a: Ord UPDATE로 qty < 0인 행이 생기면 trg_Ord_check가 ROLLBACK한 뒤 ErrLog에 행을 남긴다(트랜잭션 밖에서 확정, A22).
--          ErrLog 키는 IDENTITY라 같은 원본에서 반복해도 키가 겹치지 않는다(남은 행은 사례마다 1행씩 쌓인다).
--   CL12b: 0행 UPDATE에서 가드가 바로 RETURN해 OrdHist에 0행 INSERT 묶음을 만들지 않는다. 가드는 SET 문보다 앞에 둔다(SET은 행 수를 0으로 만든다, A9).
--   CL12c: trg_Doc_ins가 DocNote INSERT 직후 @@IDENTITY를 IdentSeen에 적는다. 감사 저장소가 IDENTITY를 쓰지 않으면(D3 규칙 3) 그 값은 DocNote.note_id다(E25).
USE CascadeLab;
GO
CREATE SCHEMA cl12;
GO
CREATE TABLE cl12.Ord (
    id  int NOT NULL CONSTRAINT PK_cl12_Ord PRIMARY KEY,
    qty int NOT NULL
);
CREATE TABLE cl12.OrdHist (
    ord_id int NOT NULL CONSTRAINT PK_cl12_OrdHist PRIMARY KEY,
    qty    int NOT NULL
);
CREATE TABLE cl12.ErrLog (
    log_id int          IDENTITY(1, 1) NOT NULL CONSTRAINT PK_cl12_ErrLog PRIMARY KEY,
    ord_id int          NOT NULL,
    msg    varchar(100) NOT NULL
);
CREATE TABLE cl12.Doc (
    doc_id int         IDENTITY(1, 1) NOT NULL CONSTRAINT PK_cl12_Doc PRIMARY KEY,
    title  varchar(20) NOT NULL
);
CREATE TABLE cl12.DocNote (
    note_id int         IDENTITY(100, 1) NOT NULL CONSTRAINT PK_cl12_DocNote PRIMARY KEY,
    doc_id  int         NOT NULL CONSTRAINT FK_cl12_DocNote_Doc REFERENCES cl12.Doc (doc_id),
    note    varchar(20) NOT NULL
);
CREATE TABLE cl12.IdentSeen (
    title         varchar(20) NOT NULL CONSTRAINT PK_cl12_IdentSeen PRIMARY KEY,
    seen_identity int         NULL
);
GO
CREATE TRIGGER cl12.trg_Ord_check ON cl12.Ord AFTER UPDATE AS
BEGIN
    IF ROWCOUNT_BIG() = 0 RETURN;
    SET NOCOUNT ON;
    -- 테이블 변수는 롤백의 영향을 받지 않으므로 ROLLBACK 전에 거부 행을 담아 둔다.
    DECLARE @bad TABLE (id int NOT NULL PRIMARY KEY);
    INSERT INTO @bad (id) SELECT i.id FROM inserted AS i WHERE i.qty < 0;
    IF EXISTS (SELECT 1 FROM @bad)
    BEGIN
        ROLLBACK TRANSACTION;
        INSERT INTO cl12.ErrLog (ord_id, msg)
        SELECT b.id, 'negative qty' FROM @bad AS b;
        RETURN;
    END;
    INSERT INTO cl12.OrdHist (ord_id, qty)
    SELECT i.id, i.qty FROM inserted AS i;
END;
GO
CREATE TRIGGER cl12.trg_Doc_ins ON cl12.Doc AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO cl12.DocNote (doc_id, note)
    SELECT i.doc_id, 'auto' FROM inserted AS i;
    INSERT INTO cl12.IdentSeen (title, seen_identity)
    SELECT i.title, CAST(@@IDENTITY AS int) FROM inserted AS i;
END;
GO
INSERT INTO cl12.Ord (id, qty) VALUES (1, 5), (2, 7);
-- 시드 Doc 1행: trg_Doc_ins가 DocNote(note_id 100)와 IdentSeen('d0', 100)을 만든다.
INSERT INTO cl12.Doc (title) VALUES ('d0');
GO
