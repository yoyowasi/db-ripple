/* SP1 — 실험 실행과 관찰(각 실험: BEGIN TRAN → 경계 → DML → 헤더 수집 → ROLLBACK → 판정).
   테이블 변수는 ROLLBACK 뒤에도 남으므로 관찰값을 거기에 담는다. 판정 기준은 docs/spikes/SP1.md. */
USE SpikeLab_SP1;
GO
/* ---------- CL06: DELETE cl06.P (Id=1) → C, G CASCADE, C의 사용자 트리거 → Log ---------- */
SET NOCOUNT ON;
DECLARE @obs TABLE (captureSeq bigint, tableName nvarchar(300), operation char(1), nestLevel int,
                    insertedCount bigint, deletedCount bigint, tranCount int, imageMode varchar(4), images nvarchar(max));
BEGIN TRAN;
DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
DELETE cl06.P WHERE Id = 1;
INSERT @obs
SELECT h.captureSeq, h.tableName, h.operation, h.nestLevel, h.insertedCount, h.deletedCount, h.tranCount, h.imageMode,
       (SELECT STRING_AGG(CONCAT(LEFT(r.imageKind, 1), N':', r.keyJson, N':', JSON_VALUE(r.rowJson, '$.Src')), N' ') FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq)
FROM audit.AuditHeader AS h WHERE h.captureSeq > @b;
ROLLBACK;
SELECT 'CL06' AS exp, ROW_NUMBER() OVER (ORDER BY captureSeq) AS pos, * FROM @obs ORDER BY captureSeq;
DECLARE @sP bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl06].[P]'),
        @sC bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl06].[C]'),
        @sG bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl06].[G]'),
        @sL bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl06].[Log]');
SELECT 'CL06' AS exp, v.hyp, v.rule_, v.verdict FROM (VALUES
 ('H2', N'P·C·G nestLevel=1, Log nestLevel=2',
   CASE WHEN (SELECT COUNT(*) FROM @obs WHERE tableName IN (N'[cl06].[P]', N'[cl06].[C]', N'[cl06].[G]') AND nestLevel = 1) = 3
         AND (SELECT COUNT(*) FROM @obs WHERE tableName = N'[cl06].[Log]' AND nestLevel = 2) = 1 THEN 'HOLDS' ELSE 'BROKEN' END),
 ('H1', N'seq(Log) < seq(C)  (C의 사용자 트리거 중첩 변경이 C 감사보다 먼저)',
   CASE WHEN @sL < @sC THEN 'HOLDS' ELSE 'BROKEN' END),
 ('A1(참고)', N'연쇄의 반대 순서 관찰: seq(G) < seq(C) < seq(P) ?',
   CASE WHEN @sG < @sC AND @sC < @sP THEN 'OBSERVED' ELSE 'OTHER_ORDER' END),
 ('A3(참고)', N'헤더 4개(P,C,G,Log)',
   CASE WHEN (SELECT COUNT(*) FROM @obs) = 4 THEN 'OK' ELSE 'CHECK' END)
) AS v (hyp, rule_, verdict);
GO
/* ---------- CL07: INSERT cl07.A (1),(2) → trg_A_I(B, D 쓰기) → trg_B_I(C 쓰기) ---------- */
SET NOCOUNT ON;
DECLARE @obs TABLE (captureSeq bigint, tableName nvarchar(300), operation char(1), nestLevel int,
                    insertedCount bigint, deletedCount bigint, tranCount int, imageMode varchar(4), images nvarchar(max));
BEGIN TRAN;
DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
INSERT cl07.A (Id) VALUES (1), (2);
INSERT @obs
SELECT h.captureSeq, h.tableName, h.operation, h.nestLevel, h.insertedCount, h.deletedCount, h.tranCount, h.imageMode,
       (SELECT STRING_AGG(CONCAT(LEFT(r.imageKind, 1), N':', r.keyJson), N' ') FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq)
FROM audit.AuditHeader AS h WHERE h.captureSeq > @b;
ROLLBACK;
SELECT 'CL07' AS exp, ROW_NUMBER() OVER (ORDER BY captureSeq) AS pos, * FROM @obs ORDER BY captureSeq;
DECLARE @sA bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl07].[A]'),
        @sB bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl07].[B]'),
        @sC bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl07].[C]'),
        @sD bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl07].[D]');
SELECT 'CL07' AS exp, v.hyp, v.rule_, v.verdict FROM (VALUES
 ('H2', N'nestLevel A=1, B=2, D=2, C=3',
   CASE WHEN (SELECT COUNT(*) FROM @obs WHERE (tableName = N'[cl07].[A]' AND nestLevel = 1) OR (tableName IN (N'[cl07].[B]', N'[cl07].[D]') AND nestLevel = 2)
              OR (tableName = N'[cl07].[C]' AND nestLevel = 3)) = 4 THEN 'HOLDS' ELSE 'BROKEN' END),
 ('H1', N'후위 순서: seq(C) < seq(B) < seq(A), seq(D) < seq(A)',
   CASE WHEN @sC < @sB AND @sB < @sA AND @sD < @sA THEN 'HOLDS' ELSE 'BROKEN' END),
 ('H1-괄호', N'C가 B와 D 사이에 오지 않음(seq(C) < seq(B) 또는 seq(C) < seq(D)이고 C가 B 묶음 안)',
   CASE WHEN @sC < @sB AND NOT (@sD < @sC AND @sC < @sB) THEN 'HOLDS' ELSE 'BROKEN' END),
 ('참고', N'형제 순서 B/D(보장 아님)', CASE WHEN @sB < @sD THEN 'B<D' ELSE 'D<B' END)
) AS v (hyp, rule_, verdict);
GO
/* ---------- CL09: DELETE cl09.P (Id=1) → 1차 K{40,42}, M{20}; trg_M_D가 Q{30} 삭제 → 2차 K{41} ---------- */
SET NOCOUNT ON;
DECLARE @obs TABLE (captureSeq bigint, tableName nvarchar(300), operation char(1), nestLevel int,
                    insertedCount bigint, deletedCount bigint, tranCount int, imageMode varchar(4), images nvarchar(max));
DECLARE @kimg TABLE (captureSeq bigint, keyJson nvarchar(max));
BEGIN TRAN;
DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
DELETE cl09.P WHERE Id = 1;
INSERT @obs
SELECT h.captureSeq, h.tableName, h.operation, h.nestLevel, h.insertedCount, h.deletedCount, h.tranCount, h.imageMode,
       (SELECT STRING_AGG(CONCAT(LEFT(r.imageKind, 1), N':', r.keyJson), N' ') FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq)
FROM audit.AuditHeader AS h WHERE h.captureSeq > @b;
INSERT @kimg SELECT r.captureSeq, r.keyJson FROM audit.AuditRowImage AS r JOIN audit.AuditHeader AS h ON h.captureSeq = r.captureSeq
WHERE h.captureSeq > @b AND h.tableName = N'[cl09].[K]';
ROLLBACK;
SELECT 'CL09' AS exp, ROW_NUMBER() OVER (ORDER BY captureSeq) AS pos, * FROM @obs ORDER BY captureSeq;
DECLARE @sM bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl09].[M]'),
        @sP bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl09].[P]'),
        @sQ bigint = (SELECT captureSeq FROM @obs WHERE tableName = N'[cl09].[Q]'),
        @sK2 bigint = (SELECT MAX(captureSeq) FROM @obs WHERE tableName = N'[cl09].[K]' AND images LIKE N'%"Id":41%');
SELECT 'CL09' AS exp, v.hyp, v.rule_, v.verdict FROM (VALUES
 ('H4', N'K의 같은 키가 두 묶음에 나오지 않음, K 묶음 2개({40,42}, {41})',
   CASE WHEN (SELECT COUNT(*) FROM @kimg) = (SELECT COUNT(DISTINCT keyJson) FROM @kimg)
         AND (SELECT COUNT(*) FROM @obs WHERE tableName = N'[cl09].[K]') = 2 THEN 'HOLDS' ELSE 'BROKEN' END),
 ('H5', N'2차 체인(Q, K{41})이 nestLevel=2이고 seq < seq(M) (trg_M_D가 끝나기 전)',
   CASE WHEN @sQ < @sM AND @sK2 < @sM
         AND (SELECT COUNT(*) FROM @obs WHERE captureSeq IN (@sQ, @sK2) AND nestLevel = 2) = 2 THEN 'HOLDS' ELSE 'BROKEN' END),
 ('H2', N'1차 체인 K{40,42}, M, P는 nestLevel=1',
   CASE WHEN (SELECT COUNT(*) FROM @obs WHERE nestLevel = 1) = 3 THEN 'HOLDS' ELSE 'BROKEN' END),
 ('참고', N'2차 체인이 1차 트리거 모두 뒤(문서 문구 그대로)?', CASE WHEN @sK2 > @sP AND @sQ > @sP THEN 'AFTER_ALL' ELSE 'INSIDE' END)
) AS v (hyp, rule_, verdict);
GO
/* ---------- CL13: DELETE cl13.T (Id=1) → TC CASCADE. T: trg_T_1 First, trg_T_2, trg_T_3 Last(사용자), 감사 NONE ---------- */
SET NOCOUNT ON;
DECLARE @run int = 1;
DECLARE @obs TABLE (run int, captureSeq bigint, tableName nvarchar(300), operation char(1), nestLevel int, src varchar(40), grp varchar(2));
WHILE @run <= 3   /* 3회 반복: NONE 위치가 바뀌는지 본다 */
BEGIN
    BEGIN TRAN;
    DECLARE @b bigint = (SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader);
    DELETE cl13.T WHERE Id = 1;
    INSERT @obs
    SELECT @run, h.captureSeq, h.tableName, h.operation, h.nestLevel, s.src,
           CASE WHEN h.tableName = N'[cl13].[TC]' OR s.src = 'trg_TC_1' THEN 'TC' ELSE 'T' END
    FROM audit.AuditHeader AS h
    OUTER APPLY (SELECT MAX(JSON_VALUE(r.rowJson, '$.Src')) AS src FROM audit.AuditRowImage AS r WHERE r.captureSeq = h.captureSeq AND h.tableName = N'[cl13].[Log]') AS s
    WHERE h.captureSeq > @b;
    ROLLBACK;
    SET @run += 1;
END
SELECT 'CL13' AS exp, run, ROW_NUMBER() OVER (PARTITION BY run ORDER BY captureSeq) AS pos, captureSeq, tableName, nestLevel, src, grp
FROM @obs ORDER BY run, captureSeq;
SELECT 'CL13' AS exp, v.hyp, v.rule_, v.verdict FROM (VALUES
 ('H3', N'모든 회차에서 TC 묶음과 T 묶음이 섞이지 않음(한 그룹의 최대 seq < 다른 그룹의 최소 seq)',
   CASE WHEN NOT EXISTS (SELECT 1 FROM (SELECT run, MIN(CASE WHEN grp = 'T' THEN captureSeq END) AS tMin, MAX(CASE WHEN grp = 'T' THEN captureSeq END) AS tMax,
                                              MIN(CASE WHEN grp = 'TC' THEN captureSeq END) AS cMin, MAX(CASE WHEN grp = 'TC' THEN captureSeq END) AS cMax
                                       FROM @obs GROUP BY run) AS g WHERE NOT (g.cMax < g.tMin OR g.tMax < g.cMin)) THEN 'HOLDS' ELSE 'BROKEN' END),
 ('H1(Last)', N'TC 감사(Last)가 trg_TC_1의 Log 뒤',
   CASE WHEN NOT EXISTS (SELECT 1 FROM @obs AS a JOIN @obs AS l ON l.run = a.run AND l.src = 'trg_TC_1'
                         WHERE a.tableName = N'[cl13].[TC]' AND a.captureSeq < l.captureSeq) THEN 'HOLDS' ELSE 'BROKEN' END),
 ('NONE', N'T 감사 트리거 auditOrder=NONE',
   CASE WHEN EXISTS (SELECT 1 FROM dbo.spike_AuditOrder WHERE triggerName = N'dbr_audit_T_D' AND auditOrder = 'NONE') THEN 'OK' ELSE 'CHECK' END),
 ('참고', N'T 감사가 trg_T_3(사용자 Last) Log보다 먼저인 회차 수(= NONE이면 자식이 감사 뒤에 올 수 있음)',
   CONVERT(varchar(10), (SELECT COUNT(*) FROM @obs AS a JOIN @obs AS l ON l.run = a.run AND l.src = 'trg_T_3'
                         WHERE a.tableName = N'[cl13].[T]' AND a.captureSeq < l.captureSeq)))
) AS v (hyp, rule_, verdict);
SELECT * FROM dbo.spike_AuditOrder WHERE tableName LIKE N'cl13.%' ORDER BY tableName, triggerName;
GO
