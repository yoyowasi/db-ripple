/* SP11 — 트랜잭션 제어 프로시저와 실행 봉투(XACT_ABORT ON, 바깥 트랜잭션 0·1·2겹)
   프로시저 5패턴(균형 / 남는 COMMIT / CATCH ROLLBACK + 로그 INSERT / BEGIN만 / SAVE) × 봉투 3종(자동 커밋, 1겹, 2겹) = 15 시나리오.
   실행: .\scripts\run-sql.ps1 spikes\sql\sp11_10_run.sql -Part 1 -Log spikes\logs\sp11-part1.log
         .\scripts\run-sql.ps1 spikes\sql\sp11_10_run.sql -Part 2 -Database SpikeLab_SP11 -ContinueOnError -Log spikes\logs\sp11-part2.log
   정리: .\scripts\run-sql.ps1 spikes\sql\sp_exec_99_drop.sql -Log spikes\logs\sp-exec-99-drop.log
   의도한 오류(266·3902 등)는 시나리오 안 TRY/CATCH가 받는다. -ContinueOnError는 예상 밖 오류에서도 나머지를 계속 돌리려는 것이다.
   모든 DDL은 이 스파이크 전용 DB(SpikeLab_SP11)에만 한다. 실행하지 않은 상태에서 쓴 스크립트다(정적 검토만, docs/spikes/SP11.md "결과"는 비어 있다). */
USE master;
GO
IF DB_ID(N'SpikeLab_SP11') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP11 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP11;
END;
GO
CREATE DATABASE SpikeLab_SP11;
GO
-- PART 2
USE SpikeLab_SP11;
GO
CREATE TABLE dbo.T (
    Id int NOT NULL CONSTRAINT PK_T PRIMARY KEY,
    V  int NOT NULL CONSTRAINT CK_T_V CHECK (V < 1000)
);
CREATE TABLE dbo.Log (
    LogId int         IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Log PRIMARY KEY,
    Msg   varchar(40) NOT NULL
);
CREATE TABLE dbo.Result (
    seq      int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Result PRIMARY KEY,
    scenario varchar(24)  NOT NULL,   -- 프로시저 이름
    depth    int          NOT NULL,   -- 바깥 트랜잭션 겹 수(0 = 자동 커밋)
    step     varchar(16)  NOT NULL,   -- before | after_exec | caught | after_final
    tc       int          NULL,       -- @@TRANCOUNT
    xstate   int          NULL,       -- XACT_STATE()
    txid     bigint       NULL,       -- 현재 트랜잭션 ID(바뀌었는지만 본다)
    errNo    int          NULL,
    sev      int          NULL,
    tSnap    varchar(60)  NULL,       -- dbo.T 내용 "Id:V,..."
    logRows  int          NULL,
    msg      nvarchar(160) NULL
);
GO
/* 1. 균형: BEGIN → UPDATE → COMMIT */
CREATE PROCEDURE dbo.p_balanced AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRANSACTION;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    COMMIT TRANSACTION;
    RETURN 0;
END;
GO
/* 2. 남는 COMMIT: 짝이 되는 BEGIN 없음 */
CREATE PROCEDURE dbo.p_extra_commit AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    COMMIT TRANSACTION;
    RETURN 0;
END;
GO
/* 3. CATCH에서 ROLLBACK 뒤 로그 INSERT(uspLogError 모양). CHECK 위반(547)을 일부러 낸다 */
CREATE PROCEDURE dbo.p_catch_rollback_log AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE dbo.T SET V = 5000 WHERE Id = 1;   -- CK_T_V 위반
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        INSERT INTO dbo.Log (Msg) VALUES ('caught');
        RETURN -1;
    END CATCH;
    RETURN 0;
END;
GO
/* 4. BEGIN만: COMMIT·ROLLBACK 없이 돌아간다 */
CREATE PROCEDURE dbo.p_begin_only AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRANSACTION;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    RETURN 0;
END;
GO
/* 5. SAVE: 세이브포인트를 만들고 그 지점까지만 되돌린다(카운트는 안 바뀐다) */
CREATE PROCEDURE dbo.p_save AS
BEGIN
    SET NOCOUNT ON;
    SAVE TRANSACTION sp1;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 1;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 2;
    ROLLBACK TRANSACTION sp1;
    UPDATE dbo.T SET V = V + 1 WHERE Id = 3;
    RETURN 0;
END;
GO
/* 시나리오 실행기. 항상 열린 트랜잭션 0개에서 시작하고 0개로 끝낸다(자기 자신은 266을 내지 않게).
   중간 기록은 테이블 변수에 모은다(롤백의 영향을 받지 않음). 끝나고 dbo.Result에 옮긴다. */
CREATE PROCEDURE dbo.run_scenario @proc sysname, @depth int AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @@TRANCOUNT <> 0 THROW 50001, N'run_scenario는 열린 트랜잭션 없이 시작해야 한다', 1;

    DELETE dbo.T;
    DELETE dbo.Log;
    INSERT dbo.T (Id, V) VALUES (1, 10), (2, 20), (3, 30);

    DECLARE @r TABLE (step varchar(16), tc int, xstate int, txid bigint NULL, errNo int NULL, sev int NULL, tSnap varchar(60) NULL, logRows int NULL, msg nvarchar(160) NULL);
    DECLARE @i int = 0;
    BEGIN TRY
        WHILE @i < @depth
        BEGIN
            BEGIN TRANSACTION;
            SET @i += 1;
        END;
        IF @depth > 0 UPDATE dbo.T SET V = V + 100 WHERE Id = 3;   -- 앞선 문장을 모사: 바깥 트랜잭션 안의 쓰기(프로시저의 ROLLBACK이 이것도 되돌리는지 본다)
        INSERT @r (step, tc, xstate, txid, tSnap, logRows)
        SELECT 'before', @@TRANCOUNT, XACT_STATE(), (SELECT transaction_id FROM sys.dm_tran_current_transaction),
               (SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T), (SELECT COUNT(*) FROM dbo.Log);

        IF @proc = N'p_balanced' EXEC dbo.p_balanced;
        ELSE IF @proc = N'p_extra_commit' EXEC dbo.p_extra_commit;
        ELSE IF @proc = N'p_catch_rollback_log' EXEC dbo.p_catch_rollback_log;
        ELSE IF @proc = N'p_begin_only' EXEC dbo.p_begin_only;
        ELSE IF @proc = N'p_save' EXEC dbo.p_save;
        ELSE THROW 50002, N'알 수 없는 시나리오', 1;

        INSERT @r (step, tc, xstate, txid, tSnap, logRows)
        SELECT 'after_exec', @@TRANCOUNT, XACT_STATE(), (SELECT transaction_id FROM sys.dm_tran_current_transaction),
               (SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T), (SELECT COUNT(*) FROM dbo.Log);
    END TRY
    BEGIN CATCH
        INSERT @r (step, tc, xstate, txid, errNo, sev, tSnap, logRows, msg)
        SELECT 'caught', @@TRANCOUNT, XACT_STATE(), (SELECT transaction_id FROM sys.dm_tran_current_transaction), ERROR_NUMBER(), ERROR_SEVERITY(),
               (SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T), (SELECT COUNT(*) FROM dbo.Log), LEFT(ERROR_MESSAGE(), 160);
    END CATCH;

    -- 봉투의 마지막: 남은 트랜잭션을 모두 되돌린다(미리보기의 ROLLBACK). 자동 커밋·COMMIT으로 확정된 변경은 남는다.
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    INSERT @r (step, tc, xstate, txid, tSnap, logRows)
    SELECT 'after_final', @@TRANCOUNT, XACT_STATE(), (SELECT transaction_id FROM sys.dm_tran_current_transaction),
           (SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T), (SELECT COUNT(*) FROM dbo.Log);

    INSERT dbo.Result (scenario, depth, step, tc, xstate, txid, errNo, sev, tSnap, logRows, msg)
    SELECT @proc, @depth, step, tc, xstate, txid, errNo, sev, tSnap, logRows, msg FROM @r;
END;
GO
SET NOCOUNT ON;
DECLARE @procs TABLE (n int IDENTITY(1, 1), p sysname);
INSERT @procs (p) VALUES (N'p_balanced'), (N'p_extra_commit'), (N'p_catch_rollback_log'), (N'p_begin_only'), (N'p_save');
DECLARE @n int = 1, @d int, @p sysname;
WHILE @n <= 5
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
/* 결과 표: 시나리오별 before / after_exec / caught / after_final */
SELECT scenario, depth, step, tc, xstate, errNo, sev, tSnap, logRows, msg
FROM dbo.Result ORDER BY seq;
GO
/* 판정. 예측은 이 스크립트를 쓴 시점의 추정이다. FAIL = 추정과 다름 → 실측 값을 docs/spikes/SP11.md에 적고 설계를 고친다(실패가 곧 결함은 아니다). */
WITH f AS (
    SELECT scenario, depth,
           MAX(CASE WHEN step = 'before'      THEN tc END)     AS tcBefore,
           MAX(CASE WHEN step = 'after_exec'  THEN tc END)     AS tcAfterExec,
           MAX(CASE WHEN step = 'caught'      THEN errNo END)  AS errNo,
           MAX(CASE WHEN step = 'caught'      THEN sev END)    AS sev,
           MAX(CASE WHEN step = 'after_final' THEN tSnap END)  AS tSnapFinal,
           MAX(CASE WHEN step = 'after_final' THEN logRows END) AS logFinal
    FROM dbo.Result GROUP BY scenario, depth
)
SELECT 'H1_balanced_no_error_any_envelope' AS hypothesis,
       CASE WHEN COUNT(*) = 3 AND MIN(CASE WHEN errNo IS NULL AND tcBefore = tcAfterExec THEN 1 ELSE 0 END) = 1 THEN 'PASS' ELSE 'FAIL' END AS verdict,
       CONCAT('rows=', COUNT(*)) AS detail
FROM f WHERE scenario = N'p_balanced'
UNION ALL
SELECT 'H2_balanced_rolled_back_in_envelope_1_and_2',
       CASE WHEN SUM(CASE WHEN depth IN (1, 2) AND tSnapFinal = '1:10,2:20,3:30' THEN 1 ELSE 0 END) = 2 THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(CONCAT(depth, '=', tSnapFinal), ' ; ') FROM f f2 WHERE f2.scenario = N'p_balanced')
FROM f WHERE scenario = N'p_balanced'
UNION ALL
SELECT 'H3_extra_commit_in_envelope_1_leaks_change_past_rollback',
       CASE WHEN MAX(CASE WHEN depth = 1 THEN tSnapFinal END) <> '1:10,2:20,3:30' THEN 'PASS' ELSE 'FAIL' END,
       CONCAT('depth1 final=', MAX(CASE WHEN depth = 1 THEN tSnapFinal END))
FROM f WHERE scenario = N'p_extra_commit'
UNION ALL
SELECT 'H4_catch_rollback_log_in_envelope_1_gives_266_and_keeps_log_row',
       CASE WHEN MAX(CASE WHEN depth = 1 THEN errNo END) = 266 AND MAX(CASE WHEN depth = 1 THEN logFinal END) = 1 THEN 'PASS' ELSE 'FAIL' END,
       CONCAT('depth1 err=', MAX(CASE WHEN depth = 1 THEN errNo END), ' sev=', MAX(CASE WHEN depth = 1 THEN sev END), ' logRows=', MAX(CASE WHEN depth = 1 THEN logFinal END))
FROM f WHERE scenario = N'p_catch_rollback_log'
UNION ALL
SELECT 'H5_begin_only_gives_266_in_every_envelope',
       CASE WHEN COUNT(*) = 3 AND MIN(CASE WHEN errNo = 266 THEN 1 ELSE 0 END) = 1 THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(CONCAT(depth, '=', ISNULL(CAST(errNo AS varchar(10)), 'none')), ' ; ') FROM f f3 WHERE f3.scenario = N'p_begin_only')
FROM f WHERE scenario = N'p_begin_only'
UNION ALL
SELECT 'H6_save_keeps_trancount_and_undoes_only_to_savepoint_in_envelope_1',
       CASE WHEN MAX(CASE WHEN depth = 1 THEN tcBefore END) = MAX(CASE WHEN depth = 1 THEN tcAfterExec END) AND MAX(CASE WHEN depth = 1 THEN errNo END) IS NULL THEN 'PASS' ELSE 'FAIL' END,
       (SELECT STRING_AGG(CONCAT(depth, '=', ISNULL(CAST(errNo AS varchar(10)), 'none')), ' ; ') FROM f f4 WHERE f4.scenario = N'p_save')
FROM f WHERE scenario = N'p_save';
GO
