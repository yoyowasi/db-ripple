/* SP16 — @@TRANCOUNT를 보고 분기하는 프로시저: 실행 봉투(바깥 트랜잭션 1겹·2겹) 안에서 본 최종 차이 대 자동 커밋 단독 실행의 최종 차이
   질문: 미리보기(봉투 안)가 보여 주는 변경이, 사용자가 같은 EXEC를 단독(자동 커밋)으로 실제 실행했을 때의 변경과 같은가?
   프로시저 3종: p_own_tran_if_none(트랜잭션이 없으면 자기 것을 열고, 있으면 SAVE) / p_branch_data(@@TRANCOUNT에 따라 쓰는 값이 다름) / p_requires_tran(트랜잭션 밖이면 아무것도 안 함).
   실행: .\scripts\run-sql.ps1 spikes\sql\sp16_10_run.sql -Part 1 -Log spikes\logs\sp16-part1.log
         .\scripts\run-sql.ps1 spikes\sql\sp16_10_run.sql -Part 2 -Database SpikeLab_SP16 -ContinueOnError -Log spikes\logs\sp16-part2.log
   정리: .\scripts\run-sql.ps1 spikes\sql\sp_exec_99_drop.sql -Log spikes\logs\sp-exec-99-drop.log
   각 시나리오는 시작 전에 T를 시드(1:10,2:20)로 되돌린다. 비교 값 = 프로시저 직후 T 내용(봉투 안은 열린 트랜잭션 안에서 읽은 값 = 미리보기의 최종 차이).
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE master;
GO
IF DB_ID(N'SpikeLab_SP16') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP16 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP16;
END;
GO
CREATE DATABASE SpikeLab_SP16;
GO
-- PART 2
USE SpikeLab_SP16;
GO
CREATE TABLE dbo.T (Id int NOT NULL CONSTRAINT PK_T PRIMARY KEY, V int NOT NULL);
CREATE TABLE dbo.Result (
    seq       int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Result PRIMARY KEY,
    scenario  varchar(24) NOT NULL,
    depth     int         NOT NULL,   -- 0 = 자동 커밋 단독, 1·2 = 바깥 트랜잭션 겹 수
    errNo     int         NULL,
    tcAfter   int         NULL,
    tSnap     varchar(60) NULL,       -- 프로시저 직후 T 내용
    tSnapFinal varchar(60) NULL       -- 봉투의 마지막 ROLLBACK(또는 자동 커밋 확정) 뒤 T 내용
);
GO
/* 1. 트랜잭션이 없으면 자기 것을 열고 있으면 SAVE: 흔한 "호출자 트랜잭션에 참여" 패턴. 쓰는 값은 호출 방식과 무관하다 */
CREATE PROCEDURE dbo.p_own_tran_if_none AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @own bit = 0;
    IF @@TRANCOUNT = 0 BEGIN BEGIN TRANSACTION; SET @own = 1; END
    ELSE SAVE TRANSACTION sp_own;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    IF @own = 1 COMMIT TRANSACTION;
    RETURN 0;
END;
GO
/* 2. 호출 방식에 따라 쓰는 값이 달라지는 패턴(미리보기와 실제가 어긋나는 위험) */
CREATE PROCEDURE dbo.p_branch_data AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT > 0 UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    ELSE UPDATE dbo.T SET V = V + 2 WHERE Id = 1;
    RETURN 0;
END;
GO
/* 3. 트랜잭션 밖이면 아무것도 하지 않는 패턴 */
CREATE PROCEDURE dbo.p_requires_tran AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT = 0 RETURN -1;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    RETURN 0;
END;
GO
CREATE PROCEDURE dbo.run_scenario @proc sysname, @depth int AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @@TRANCOUNT <> 0 THROW 50001, N'run_scenario는 열린 트랜잭션 없이 시작해야 한다', 1;
    DELETE dbo.T;
    INSERT dbo.T (Id, V) VALUES (1, 10), (2, 20);

    DECLARE @i int = 0, @errNo int = NULL, @tc int = NULL, @snap varchar(60) = NULL;
    BEGIN TRY
        WHILE @i < @depth BEGIN BEGIN TRANSACTION; SET @i += 1; END;
        IF @proc = N'p_own_tran_if_none' EXEC dbo.p_own_tran_if_none;
        ELSE IF @proc = N'p_branch_data' EXEC dbo.p_branch_data;
        ELSE IF @proc = N'p_requires_tran' EXEC dbo.p_requires_tran;
        ELSE THROW 50002, N'알 수 없는 시나리오', 1;
        SET @tc = @@TRANCOUNT;
        SET @snap = (SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T);
    END TRY
    BEGIN CATCH
        SET @errNo = ERROR_NUMBER();
        SET @tc = @@TRANCOUNT;
        SET @snap = (SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T);
    END CATCH;
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    INSERT dbo.Result (scenario, depth, errNo, tcAfter, tSnap, tSnapFinal)
    VALUES (@proc, @depth, @errNo, @tc, @snap, (SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T));
END;
GO
SET NOCOUNT ON;
DECLARE @procs TABLE (n int IDENTITY(1, 1), p sysname);
INSERT @procs (p) VALUES (N'p_own_tran_if_none'), (N'p_branch_data'), (N'p_requires_tran');
DECLARE @n int = 1, @d int, @p sysname;
WHILE @n <= 3
BEGIN
    SELECT @p = p FROM @procs WHERE n = @n;
    SET @d = 0;
    WHILE @d <= 2
    BEGIN
        EXEC dbo.run_scenario @p, @d;
        SET @d += 1;
    END;
    SET @n += 1;
END;
GO
SELECT scenario, depth, errNo, tcAfter, tSnap AS t_after_exec, tSnapFinal AS t_after_final FROM dbo.Result ORDER BY seq;
GO
/* 판정: "자동 커밋 단독(depth 0)의 프로시저 직후 T" 대 "봉투 1·2겹의 프로시저 직후 T". SAME이면 미리보기가 실제와 같은 변경을 보인다. DIFF면 @@TRANCOUNT 분기 때문에 미리보기가 실제와 다르다(경고·거부 후보). */
WITH r AS (
    SELECT scenario,
           MAX(CASE WHEN depth = 0 THEN tSnap END) AS solo,
           MAX(CASE WHEN depth = 1 THEN tSnap END) AS env1,
           MAX(CASE WHEN depth = 2 THEN tSnap END) AS env2
    FROM dbo.Result GROUP BY scenario
)
SELECT scenario, solo AS autocommit_alone, env1 AS envelope_1, env2 AS envelope_2,
       CASE WHEN solo = env1 AND solo = env2 THEN 'SAME' ELSE 'DIFF' END AS preview_matches_real,
       CASE scenario
            WHEN N'p_own_tran_if_none' THEN 'SAME 예상'
            WHEN N'p_branch_data'      THEN 'DIFF 예상(+2 대 +1)'
            WHEN N'p_requires_tran'    THEN 'DIFF 예상(단독은 변경 없음)'
       END AS expectation
FROM r ORDER BY scenario;
GO
