/* SP21 — KEYSET_HASH 검출력(1): 약한 검증 모드가 미리보기 밖 테이블의 (COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)))로 변경을 알아채는가.
   같은 행 수로 값만 바뀌는 변경을 잡는지, 못 잡는 틈(비교 불가 형식 xml·text·image, 값을 바꿨다 되돌린 경우)이 어디인지 잰다.
   실행: .\scripts\run-sql.ps1 spikes\sql\sp21_10_hash_detection.sql -Part 1 -Log spikes\logs\sp21-10-part1.log
         .\scripts\run-sql.ps1 spikes\sql\sp21_10_hash_detection.sql -Part 2 -Database SpikeLab_SP21 -ContinueOnError -Log spikes\logs\sp21-10-part2.log
   정리: 같은 폴더 sp21_99_drop.sql
   각 시나리오는 BEGIN TRAN → 변경 → 해시 → ROLLBACK이고 결과는 롤백 뒤 dbo.HashResult에 옮긴다. 실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE master;
GO
IF DB_ID(N'SpikeLab_SP21') IS NOT NULL
BEGIN
    ALTER DATABASE SpikeLab_SP21 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SpikeLab_SP21;
END;
GO
CREATE DATABASE SpikeLab_SP21;
GO
-- PART 2
USE SpikeLab_SP21;
GO
CREATE TABLE dbo.B (Id int NOT NULL CONSTRAINT PK_B PRIMARY KEY, V int NOT NULL, Name varchar(20) NOT NULL);
CREATE TABLE dbo.D (Id int NOT NULL CONSTRAINT PK_D PRIMARY KEY, Doc xml NULL, Txt varchar(max) NULL);
CREATE TABLE dbo.HashResult (
    scenario varchar(40) NOT NULL, tbl varchar(10) NOT NULL,
    cntBefore bigint NOT NULL, cntAfter bigint NOT NULL,
    csAggBefore int NULL, csAggAfter int NULL,
    sumBefore bigint NULL, sumAfter bigint NULL,
    CONSTRAINT PK_HashResult PRIMARY KEY (scenario, tbl)
);
INSERT dbo.B (Id, V, Name) VALUES (1, 10, 'abc'), (2, 20, 'def'), (3, 30, 'ghi'), (4, 40, 'jkl');
INSERT dbo.D (Id, Doc, Txt) VALUES (1, N'<a>1</a>', 'one'), (2, N'<a>2</a>', 'two');
GO
/* m1: 한 행의 값 +1(행 수 그대로) */
SET NOCOUNT ON;
DECLARE @b0 TABLE (c bigint, h int, s bigint);
DECLARE @b1 TABLE (c bigint, h int, s bigint);
INSERT @b0 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
BEGIN TRANSACTION;
UPDATE dbo.B SET V = V + 1 WHERE Id = 1;
INSERT @b1 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
ROLLBACK TRANSACTION;
INSERT dbo.HashResult SELECT 'm1_value_plus_one', 'B', a.c, b.c, a.h, b.h, a.s, b.s FROM @b0 AS a, @b1 AS b;
GO
/* m2: 두 행의 V를 맞바꾼다(값의 다중집합·합계는 그대로, 행 수 그대로) */
SET NOCOUNT ON;
DECLARE @b0 TABLE (c bigint, h int, s bigint);
DECLARE @b1 TABLE (c bigint, h int, s bigint);
INSERT @b0 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
BEGIN TRANSACTION;
UPDATE b SET V = CASE b.Id WHEN 1 THEN 20 WHEN 2 THEN 10 END FROM dbo.B AS b WHERE b.Id IN (1, 2);
INSERT @b1 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
ROLLBACK TRANSACTION;
INSERT dbo.HashResult SELECT 'm2_swap_two_rows', 'B', a.c, b.c, a.h, b.h, a.s, b.s FROM @b0 AS a, @b1 AS b;
GO
/* m3: 바꿨다가 되돌린다(순 변경 없음) → 해시는 그대로여야 한다 */
SET NOCOUNT ON;
DECLARE @b0 TABLE (c bigint, h int, s bigint);
DECLARE @b1 TABLE (c bigint, h int, s bigint);
INSERT @b0 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
BEGIN TRANSACTION;
UPDATE dbo.B SET V = V + 5 WHERE Id = 3;
UPDATE dbo.B SET V = V - 5 WHERE Id = 3;
INSERT @b1 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
ROLLBACK TRANSACTION;
INSERT dbo.HashResult SELECT 'm3_change_and_revert', 'B', a.c, b.c, a.h, b.h, a.s, b.s FROM @b0 AS a, @b1 AS b;
GO
/* m4: 대소문자만 바꾼다('abc' → 'ABC'). BINARY_CHECKSUM은 이진 기준이라 잡고, CHECKSUM은 대소문자 무시 정렬에서 못 잡는다 */
SET NOCOUNT ON;
DECLARE @b0 TABLE (c bigint, h int, s bigint);
DECLARE @b1 TABLE (c bigint, h int, s bigint);
INSERT @b0 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
BEGIN TRANSACTION;
UPDATE dbo.B SET Name = 'ABC' WHERE Id = 1;
INSERT @b1 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.B;
ROLLBACK TRANSACTION;
INSERT dbo.HashResult SELECT 'm4_case_only_change', 'B', a.c, b.c, a.h, b.h, a.s, b.s FROM @b0 AS a, @b1 AS b;
GO
/* m5: xml 열만 바꾼다. BINARY_CHECKSUM은 xml(비교 불가 형식)을 무시하므로 못 잡을 것이다(알려진 틈) */
SET NOCOUNT ON;
DECLARE @b0 TABLE (c bigint, h int, s bigint);
DECLARE @b1 TABLE (c bigint, h int, s bigint);
INSERT @b0 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.D;
BEGIN TRANSACTION;
UPDATE dbo.D SET Doc = N'<a>999</a>' WHERE Id = 1;
INSERT @b1 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.D;
ROLLBACK TRANSACTION;
INSERT dbo.HashResult SELECT 'm5_xml_only_change', 'D', a.c, b.c, a.h, b.h, a.s, b.s FROM @b0 AS a, @b1 AS b;
GO
/* m6: varchar(max) 열만 바꾼다(비교 가능 형식, 잡아야 한다) */
SET NOCOUNT ON;
DECLARE @b0 TABLE (c bigint, h int, s bigint);
DECLARE @b1 TABLE (c bigint, h int, s bigint);
INSERT @b0 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.D;
BEGIN TRANSACTION;
UPDATE dbo.D SET Txt = 'changed' WHERE Id = 2;
INSERT @b1 SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)), SUM(CAST(BINARY_CHECKSUM(*) AS bigint)) FROM dbo.D;
ROLLBACK TRANSACTION;
INSERT dbo.HashResult SELECT 'm6_varchar_max_change', 'D', a.c, b.c, a.h, b.h, a.s, b.s FROM @b0 AS a, @b1 AS b;
GO
SELECT scenario, tbl, cntBefore, cntAfter, csAggBefore, csAggAfter, sumBefore, sumAfter,
       CASE WHEN cntBefore <> cntAfter OR ISNULL(csAggBefore, 0) <> ISNULL(csAggAfter, 0) THEN 'DETECTED' ELSE 'not detected' END AS by_checksum_agg,
       CASE WHEN cntBefore <> cntAfter OR ISNULL(sumBefore, 0) <> ISNULL(sumAfter, 0) THEN 'DETECTED' ELSE 'not detected' END AS by_sum
FROM dbo.HashResult ORDER BY scenario;
GO
/* 판정(예측은 쓴 시점의 추정): m1·m2·m4·m6는 잡아야 하고 m3(순 변경 없음)은 못 잡는 게 정상이다. m5(xml)는 알려진 틈이라 "못 잡음"이면 PASS(틈 확인),
   잡으면 FAIL(추정과 다름 → 틈이 없으니 설계 문구를 고친다). FAIL = 추정과 다름 → docs/spikes/SP21.md에 실측을 적는다. */
WITH d AS (
    SELECT scenario, CASE WHEN cntBefore <> cntAfter OR ISNULL(csAggBefore, 0) <> ISNULL(csAggAfter, 0) THEN 1 ELSE 0 END AS detected FROM dbo.HashResult
)
SELECT 'H1_same_rowcount_value_changes_are_detected(m1,m2,m4,m6)' AS hypothesis,
       CASE WHEN (SELECT MIN(detected) FROM d WHERE scenario IN ('m1_value_plus_one', 'm2_swap_two_rows', 'm4_case_only_change', 'm6_varchar_max_change')) = 1 THEN 'PASS' ELSE 'FAIL' END AS verdict,
       (SELECT STRING_AGG(CONCAT(scenario, '=', detected), ' ; ') FROM d WHERE scenario IN ('m1_value_plus_one', 'm2_swap_two_rows', 'm4_case_only_change', 'm6_varchar_max_change')) AS detail
UNION ALL
SELECT 'H2_net_zero_change_is_not_reported(m3)', CASE WHEN (SELECT detected FROM d WHERE scenario = 'm3_change_and_revert') = 0 THEN 'PASS' ELSE 'FAIL' END, NULL
UNION ALL
SELECT 'H3_xml_only_change_is_a_blind_spot(m5)', CASE WHEN (SELECT detected FROM d WHERE scenario = 'm5_xml_only_change') = 0 THEN 'PASS' ELSE 'FAIL' END, NULL;
GO
