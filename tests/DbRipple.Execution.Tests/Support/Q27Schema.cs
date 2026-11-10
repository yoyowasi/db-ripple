namespace DbRipple.Execution.Tests.Support;

/// <summary>
/// Q27·위험 6·7 실측용 작은 스키마(dbrt_exe_q27_ 전용 기준선). 사용자 트리거는 Blk.v 값으로 골라 한 가지 동작만 한다.
/// v=1 RAISERROR+ROLLBACK, v=2 THROW, v=3 RAISERROR만, v=4 ROLLBACK 뒤 ErrLog INSERT(롤백 뒤 남는 변경), v=5 트리거 본문의 FK 위반(엔진 오류).
/// Calc.c는 비지속 계산 열이라 id 2 행을 감사 트리거가 읽으면 8115(R18). Ins는 INSTEAD OF DELETE가 RAISERROR만 한다.
/// </summary>
public static class Q27Schema
{
    public static readonly string[] Batches =
    [
        """
        CREATE TABLE dbo.Parent (id int NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
        CREATE TABLE dbo.Child (id int NOT NULL CONSTRAINT PK_Child PRIMARY KEY, p_id int NOT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent (id));
        INSERT INTO dbo.Parent (id) VALUES (1), (2);
        INSERT INTO dbo.Child (id, p_id) VALUES (10, 1);
        CREATE TABLE dbo.Calc (id int NOT NULL CONSTRAINT PK_Calc PRIMARY KEY, a int NOT NULL, c AS (a * 1000));
        INSERT INTO dbo.Calc (id, a) VALUES (1, 1), (2, 3000000);
        CREATE TABLE dbo.Blk (id int NOT NULL CONSTRAINT PK_Blk PRIMARY KEY, v int NOT NULL);
        INSERT INTO dbo.Blk (id, v) VALUES (1, 0);
        CREATE TABLE dbo.ErrLog (id int IDENTITY CONSTRAINT PK_ErrLog PRIMARY KEY, msg nvarchar(100) NOT NULL);
        CREATE TABLE dbo.Ins (id int NOT NULL CONSTRAINT PK_Ins PRIMARY KEY);
        INSERT INTO dbo.Ins (id) VALUES (1);
        """,
        """
        CREATE TRIGGER dbo.trg_Blk_rb ON dbo.Blk AFTER UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM inserted WHERE v = 1) BEGIN RAISERROR (N'blk rollback', 16, 1); ROLLBACK TRANSACTION; END;
        END;
        """,
        """
        CREATE TRIGGER dbo.trg_Blk_throw ON dbo.Blk AFTER UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM inserted WHERE v = 2) THROW 50001, N'blk throw', 1;
        END;
        """,
        """
        CREATE TRIGGER dbo.trg_Blk_raise ON dbo.Blk AFTER UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM inserted WHERE v = 3) RAISERROR (N'blk raise only', 16, 1);
        END;
        """,
        """
        CREATE TRIGGER dbo.trg_Blk_log ON dbo.Blk AFTER UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM inserted WHERE v = 4)
            BEGIN
                ROLLBACK TRANSACTION;
                INSERT INTO dbo.ErrLog (msg) VALUES (N'after rollback');
            END;
        END;
        """,
        """
        CREATE TRIGGER dbo.trg_Blk_engine ON dbo.Blk AFTER UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM inserted WHERE v = 5) INSERT INTO dbo.Child (id, p_id) VALUES (99, 999);
        END;
        """,
        """
        CREATE TRIGGER dbo.trg_Ins_iod ON dbo.Ins INSTEAD OF DELETE AS
        BEGIN
            SET NOCOUNT ON;
            RAISERROR (N'no delete', 16, 1);
        END;
        """,
    ];
}
