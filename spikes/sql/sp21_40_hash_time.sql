/* SP21 — 트랜잭션 안 시간: 모든 사용자 테이블의 (COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)))를 한 번 계산하는 데 걸리는 시간.
   KEYSET_HASH의 해시는 [적용] 트랜잭션 안(잠금을 잡은 채)에서 두 번(적용 전·후) 계산되므로 이 시간이 잠금 보유 시간에 더해진다.
   실행(저장소 루트, sa): .\scripts\run-sql.ps1 spikes\sql\sp21_40_hash_time.sql -Database AW_Origin -Log spikes\logs\sp21-40-aw.log          (AW, 샌드박스 컨테이너)
                          .\scripts\run-sql.ps1 spikes\sql\sp21_40_hash_time.sql -Database CascadeLab -Log spikes\logs\sp21-40-cl.log       (CascadeLab; cl20.usp_Generate로 키운 뒤 다시 재면 큰 표본)
   읽기만 한다(해시 계산과 시간 측정). 3회 연속 재서 첫 회(캐시 차가움)와 뒤 둘을 구분한다. 결과는 가장 느린 테이블 상위와 전체 합이다.
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
SET NOCOUNT ON;
DECLARE @tables TABLE (n int IDENTITY(1, 1), fullname nvarchar(300), skip bit);
INSERT @tables (fullname, skip)
SELECT QUOTENAME(s.name) + N'.' + QUOTENAME(t.name),
       CASE WHEN s.name = N'audit' THEN 1 ELSE 0 END
FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0;
DELETE @tables WHERE skip = 1;

DECLARE @res TABLE (round int, fullname nvarchar(300), cnt bigint, cs int, ms int);
DECLARE @round int = 1, @i int, @cnt int = (SELECT COUNT(*) FROM @tables), @name nvarchar(300), @sql nvarchar(max), @t0 datetime2(3), @c bigint, @h int;
WHILE @round <= 3
BEGIN
    SET @i = 1;
    WHILE @i <= @cnt
    BEGIN
        SELECT @name = fullname FROM @tables WHERE n = @i;
        SET @t0 = SYSDATETIME();
        SET @sql = N'SELECT @c = COUNT_BIG(*), @h = CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM ' + @name + N';';
        BEGIN TRY
            EXEC sys.sp_executesql @sql, N'@c bigint OUTPUT, @h int OUTPUT', @c = @c OUTPUT, @h = @h OUTPUT;
        END TRY
        BEGIN CATCH
            SELECT @c = NULL, @h = NULL;   -- 해시를 못 구하는 테이블(권한·형식 문제)은 기록만 하고 건너뛴다
        END CATCH;
        INSERT @res VALUES (@round, @name, @c, @h, DATEDIFF(MILLISECOND, @t0, SYSDATETIME()));
        SET @i += 1;
    END;
    SET @round += 1;
END;
SELECT 'tables' AS what, @cnt AS n;
SELECT round, COUNT(*) AS tables_hashed, SUM(cnt) AS total_rows, SUM(ms) AS total_ms, MAX(ms) AS slowest_table_ms FROM @res WHERE cnt IS NOT NULL GROUP BY round ORDER BY round;
SELECT TOP (10) fullname, cnt, ms AS round3_ms FROM @res WHERE round = 3 AND cnt IS NOT NULL ORDER BY ms DESC, cnt DESC;
SELECT 'tables_not_hashed' AS what, COUNT(*) AS n FROM @res WHERE round = 1 AND cnt IS NULL;
GO
