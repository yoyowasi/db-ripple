/* SP19 Q3 — KEYSET_HASH의 원본 전체 테이블 해시 시간(큰 DB). sp21_40_hash_time.sql을 제품 모양에 맞춘 판:
   - 제품처럼 비교 가능 열만 BINARY_CHECKSUM에 넣고(KeysetVerifier.IsComparable, ApplySteps.cs:353-375), 테이블 50개를 UNION ALL 명령 하나로 묶는다(TablesPerQuery, ApplySteps.cs:358·449).
   - 제한 로그인 dbr_origin_user로(EXECUTE AS LOGIN), SET LOCK_TIMEOUT 10000(OriginLockTimeoutMs), BEGIN TRAN 안에서 읽는다(제품 [적용] 트랜잭션과 같은 격리 수준 READ COMMITTED). 끝에 ROLLBACK.
   - 미리보기 테이블(건수만 읽음)은 없다고 보고 모든 테이블을 해시한다(가장 비싼 경우).
   회차: 1회차 전 CHECKPOINT + DBCC DROPCLEANBUFFERS(차가운 캐시, sa) → 1~3회차 묶음 명령 → 4회차는 테이블마다 따로(느린 테이블 순위).
   원본 모사 컨테이너, 읽기만(바꾸지 않는다). 실행:
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_50_hash_time.sql -Database SP19_5G -Log spikes/logs/sp19-50-hash-5g.log */
SET NOCOUNT ON;
DECLARE @tables TABLE (n int IDENTITY(1, 1), label nvarchar(300), part nvarchar(max));
INSERT @tables (label, part)
SELECT s.name + N'.' + t.name,
       N'SELECT N''' + REPLACE(s.name + N'.' + t.name, N'''', N'''''') + N''' AS t, COUNT_BIG(*) AS n, '
       + CASE WHEN COUNT(cc.name) = 0 THEN N'CAST(NULL AS int)'
              ELSE N'CHECKSUM_AGG(BINARY_CHECKSUM(' + STRING_AGG(CAST(QUOTENAME(cc.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY cc.column_id) + N'))' END
       + N' AS h FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + CASE WHEN t.is_memory_optimized = 1 THEN N' WITH (SNAPSHOT)' ELSE N'' END
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
LEFT JOIN (SELECT c.object_id, c.name, c.column_id
           FROM sys.columns AS c JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
           WHERE c.is_computed = 0 AND ty.is_assembly_type = 0
             AND TYPE_NAME(c.system_type_id) NOT IN (N'xml', N'text', N'ntext', N'image', N'sql_variant', N'geography', N'geometry', N'hierarchyid', N'timestamp', N'rowversion', N'json', N'vector')) AS cc
       ON cc.object_id = t.object_id
WHERE t.is_ms_shipped = 0 AND t.is_external = 0
GROUP BY s.name, t.name, t.is_memory_optimized
ORDER BY s.name, t.name;
DECLARE @cnt int = (SELECT COUNT(*) FROM @tables), @chunks int;
SET @chunks = (@cnt + 49) / 50;
SELECT 'db' AS what, DB_NAME() AS value UNION ALL SELECT 'tables', CAST(@cnt AS nvarchar(20)) UNION ALL SELECT 'chunks_of_50', CAST(@chunks AS nvarchar(20))
UNION ALL SELECT 'data_used_mb', CAST(CAST(SUM(FILEPROPERTY(name, 'SpaceUsed')) / 128.0 AS decimal(12, 1)) AS nvarchar(20)) FROM sys.database_files WHERE type = 0
UNION ALL SELECT 'buffer_pool_target_mb', CAST(committed_target_kb / 1024 AS nvarchar(20)) FROM sys.dm_os_sys_info;

CHECKPOINT;
DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS;

EXECUTE AS LOGIN = N'dbr_origin_user';
SET LOCK_TIMEOUT 10000;
SELECT 'session' AS what, SUSER_SNAME() AS login_, @@LOCK_TIMEOUT AS lock_timeout_ms,
       (SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID) AS isolation_level;
DECLARE @states TABLE (t nvarchar(300), n bigint, h int);
DECLARE @res TABLE (round int, chunk int, tables_ int, rows_ bigint, ms decimal(12, 1), err int, errmsg nvarchar(400));
DECLARE @per TABLE (label nvarchar(300), rows_ bigint, ms decimal(12, 1));
DECLARE @round int = 1, @c int, @sql nvarchar(max), @t0 datetime2(7), @err int, @msg nvarchar(400), @sep nvarchar(20) = NCHAR(10) + N'UNION ALL' + NCHAR(10);
BEGIN TRAN;
WHILE @round <= 3
BEGIN
    SET @c = 0;
    WHILE @c < @chunks
    BEGIN
        SELECT @sql = STRING_AGG(part, @sep) WITHIN GROUP (ORDER BY n) + N';'
        FROM @tables WHERE n > @c * 50 AND n <= (@c + 1) * 50;
        DELETE @states;
        SELECT @err = NULL, @msg = NULL, @t0 = SYSDATETIME();
        BEGIN TRY
            INSERT @states EXEC (@sql);
        END TRY
        BEGIN CATCH
            SELECT @err = ERROR_NUMBER(), @msg = LEFT(ERROR_MESSAGE(), 400);
        END CATCH;
        INSERT @res SELECT @round, @c + 1, (SELECT COUNT(*) FROM @states), (SELECT SUM(n) FROM @states), DATEDIFF_BIG(MICROSECOND, @t0, SYSDATETIME()) / 1000.0, @err, @msg;
        IF @err IS NOT NULL BREAK;
        SET @c += 1;
    END;
    SET @round += 1;
END;
-- 4회차: 테이블마다 따로(같은 트랜잭션, 따뜻해진 캐시)
DECLARE @i int = 1, @label nvarchar(300);
WHILE @i <= @cnt
BEGIN
    SELECT @sql = part + N';', @label = label FROM @tables WHERE n = @i;
    DELETE @states;
    SET @t0 = SYSDATETIME();
    BEGIN TRY
        INSERT @states EXEC (@sql);
    END TRY
    BEGIN CATCH
        INSERT @states VALUES (@label, NULL, NULL);
    END CATCH;
    INSERT @per SELECT @label, (SELECT n FROM @states), DATEDIFF_BIG(MICROSECOND, @t0, SYSDATETIME()) / 1000.0;
    SET @i += 1;
END;
ROLLBACK;
REVERT;
SELECT round, chunk, tables_, rows_, ms, err, errmsg FROM @res ORDER BY round, chunk;
SELECT round, SUM(ms) AS round_ms, SUM(rows_) AS rows_, MAX(ms) AS slowest_chunk_ms FROM @res GROUP BY round ORDER BY round;
SELECT TOP (8) label, rows_, ms AS round4_ms FROM @per ORDER BY ms DESC;
SELECT 'round4_total_ms' AS what, SUM(ms) AS value FROM @per;
SELECT 'tran_left' AS what, @@TRANCOUNT AS value, SUSER_SNAME() AS login_after_revert;
GO
