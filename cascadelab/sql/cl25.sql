-- CL25: EXEC 거부 사례 모음(answer-format v0.3 expected.rejects). 이 파일의 프로시저는 모두 분석 단계에서 거부돼야 하고 실행되지 않는다.
--   CL25a: EXEC cl25.usp_Dynamic    동적 SQL(sp_executesql)        → 프로시저 본문의 동적 SQL
--   CL25b: EXEC cl25.usp_ExtraCommit 짝이 안 맞는 COMMIT(BEGIN 없음) → 바깥 트랜잭션을 확정해 롤백으로 되돌릴 수 없다
--   CL25c: EXEC cl25.usp_CrossDb     다른 DB 3부분 이름 쓰기        → 샌드박스 밖 DB를 건드린다
--   (CL25d: 도달 폐포 안 외부 부작용 트리거는 별도 DB가 필요하다 → cascadelab/sql/extra/cl25d.sql. 이유는 그 파일 머리말과 answer-format.md v0.3)
--   프로시저 이름만 보고 거부하지 않고 본문을 읽어 거부하는지 보려고 셋 모두 평범한 이름과 평범한 호출(EXEC cl25.usp_X @id = 1)로 둔다.
--   오라클은 거부 사례를 실행하지 않는다(CL25b의 COMMIT이 오라클 트랜잭션을 확정해 원본을 오염시키므로). 시드 행은 확인용일 뿐이다.
--   ruleId는 be-exec-1이 AnalysisRules에서 정한다. 사례 파일에는 접두 패턴 자리표시를 쓰고 통합 때 확정한다.
USE CascadeLab;
GO
CREATE SCHEMA cl25;
GO
CREATE TABLE cl25.T (
    id int NOT NULL CONSTRAINT PK_cl25_T PRIMARY KEY,
    v  int NOT NULL
);
GO
CREATE PROCEDURE cl25.usp_Dynamic @id int AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @sql nvarchar(200) = N'UPDATE cl25.T SET v = v + 1 WHERE id = ' + CAST(@id AS nvarchar(11)) + N';';
    EXEC sys.sp_executesql @sql;
    RETURN 0;
END;
GO
CREATE PROCEDURE cl25.usp_ExtraCommit @id int AS
BEGIN
    SET NOCOUNT ON;
    UPDATE cl25.T SET v = v + 1 WHERE id = @id;
    COMMIT TRANSACTION;   -- 짝이 되는 BEGIN이 이 프로시저에 없다
    RETURN 0;
END;
GO
-- 다른 DB(master)의 3부분 이름. tempdb는 분석기가 #임시 객체 때문에 외부로 보지 않으므로 쓰지 않는다(2026-11-13 통합).
-- master.dbo.cl25_scratch는 만들지 않는다: 이름 해석은 실행 때까지 미뤄지므로 CREATE는 성공하고, 거부되지 않으면 실행 때 208로 실패해 아무것도 쓰지 않는다.
CREATE PROCEDURE cl25.usp_CrossDb @id int AS
BEGIN
    SET NOCOUNT ON;
    UPDATE master.dbo.cl25_scratch SET v = v + 1 WHERE id = @id;
    RETURN 0;
END;
GO
INSERT INTO cl25.T (id, v) VALUES (1, 0), (2, 0);
GO
