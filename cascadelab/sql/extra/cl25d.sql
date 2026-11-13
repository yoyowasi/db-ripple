-- CL25d: 도달 폐포 안의 외부 부작용 트리거(answer-format v0.3 expected.rejects). 별도 DB CascadeLab_Reach에만 만든다.
--   cl*.sql 글롭(TestSupport CascadeLabSeed, 평가 하네스)이 줍지 않도록 sql/extra/ 아래에 둔다.
--   왜 CascadeLab이 아닌가: 제품 분석기의 도달 폐포 검사(BatchAnalyzer.CheckReachedTriggers)는 "쓰기 대상을 모르는 트리거"(동적 SQL 등)에 닿으면
--   모든 사용자 테이블에 닿는다고 보고 DB 안 모든 트리거를 판정한다. CascadeLab의 CL10(동적 SQL 트리거 trg_Region_del_dyn)이 그 경우다.
--   외부 부작용 트리거(xp_ 호출)가 CascadeLab 어딘가에 하나라도 있으면 CL10이 거부로 바뀌어 기대(ok + 경고)가 깨진다. 그래서 이 트리거는 따로 둔다.
--   이 DB는 평가 하네스(CLI cascadelab)가 만들지 않는다. 확인은 docs/home-test-plan.md의 N1 단계(분석 API)나 수동으로 한다.
--   트리거 본문의 xp_logevent는 거부돼 실행되지 않아야 한다(실행돼도 master의 이벤트 로그에 한 줄을 남길 뿐이다).
USE master;
GO
IF DB_ID(N'CascadeLab_Reach') IS NOT NULL
BEGIN
    ALTER DATABASE CascadeLab_Reach SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE CascadeLab_Reach;
END;
GO
CREATE DATABASE CascadeLab_Reach;
GO
ALTER DATABASE CascadeLab_Reach SET RECURSIVE_TRIGGERS OFF;
GO
USE CascadeLab_Reach;
GO
CREATE SCHEMA cl25d;
GO
CREATE TABLE cl25d.Parent (
    id int NOT NULL CONSTRAINT PK_cl25d_Parent PRIMARY KEY
);
CREATE TABLE cl25d.Child (
    id        int NOT NULL CONSTRAINT PK_cl25d_Child PRIMARY KEY,
    parent_id int NOT NULL CONSTRAINT FK_cl25d_Child_Parent REFERENCES cl25d.Parent (id) ON DELETE CASCADE
);
GO
-- Parent를 지우면 FK CASCADE로 Child가 지워지고 이 트리거가 발동한다(직접 대상 Parent에는 트리거가 없다 → 직접 대상 검사로는 안 걸리고 도달 폐포 검사로만 걸린다).
CREATE TRIGGER cl25d.trg_Child_del ON cl25d.Child AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    EXEC master.dbo.xp_logevent 60000, N'cl25d child deleted', 'informational';
END;
GO
CREATE PROCEDURE cl25d.usp_DeleteParent @id int AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM cl25d.Parent WHERE id = @id;
    RETURN 0;
END;
GO
INSERT INTO cl25d.Parent (id) VALUES (1), (2);
INSERT INTO cl25d.Child (id, parent_id) VALUES (10, 1), (20, 2);
GO
