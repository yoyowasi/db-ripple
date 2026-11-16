/* SP19 Q2 — 샌드박스를 기준 스냅샷으로 되돌리는 시간(RESTORE DATABASE … FROM DATABASE_SNAPSHOT). 제품은 미리보기마다 되돌리지 않는다(트랜잭션 + ROLLBACK, D2).
   되돌리기는 평가 회차 초기화(architecture.md 1036행)와, 샌드박스가 dirty일 때 전체 재구축 대신 쓸 수 있는 후보다. 그 시간을 크기마다 잰다.
   샌드박스 컨테이너(sql), sa. Host는 먼저 끈다(SINGLE_USER WITH ROLLBACK IMMEDIATE가 Host 연결을 끊는다).
     ./spikes/sql/sp19_run.ps1 spikes/sql/sp19_45_revert.sql -Service sql -Vars @{ SandboxDb = 'SP19_2G_Sandbox'; SnapshotDb = 'SP19_2G_Sandbox_base'; Table = 'dbo.StockLedger'; Key = 'LedgerId'; Col = 'RefNo'; Rows = '1000'; Rounds = '3' } -Log spikes/logs/sp19-45-revert-2g-1k.log
   회차마다: 그 테이블 트리거를 끄고(DDL도 되돌리기가 원래대로 돌린다) 키 순서 앞 Rows행의 문자열 열을 REVERSE로 바꿔 커밋 → 스파스 파일 크기 →
   SINGLE_USER → RESTORE … FROM DATABASE_SNAPSHOT(시간) → MULTI_USER → 같은 행이 스냅샷과 같은지(0이어야 함)·켜진 트리거 수가 원래대로인지 확인. */
:on error exit
SET NOCOUNT ON;
IF N'$(SandboxDb)' NOT LIKE N'SP19[_]%' THROW 50001, N'SP19 전용 샌드박스만 되돌린다', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(SnapshotDb)' AND source_database_id = DB_ID(N'$(SandboxDb)')) THROW 50002, N'스냅샷이 이 샌드박스의 것이 아님', 1;
IF (SELECT COUNT(*) FROM sys.databases WHERE source_database_id = DB_ID(N'$(SandboxDb)')) <> 1 THROW 50003, N'스냅샷이 1개가 아님(되돌리기 조건)', 1;
GO
DECLARE @round int = 1, @sql nvarchar(max), @t0 datetime2(7), @t1 datetime2(7), @t2 datetime2(7), @t3 datetime2(7), @t4 datetime2(7), @t5 datetime2(7),
        @sparse0 bigint, @sparse1 bigint, @d0 int, @d1 int, @trg0 int, @trg1 int, @log0 int, @log1 int;
DECLARE @res TABLE (round int, rows_ int, change_ms decimal(12, 1), sparse_before_mb decimal(12, 1), sparse_after_change_mb decimal(12, 1), diff_before int,
                    single_user_ms decimal(12, 1), revert_ms decimal(12, 1), multi_user_ms decimal(12, 1), diff_after int, triggers_on_before int, triggers_on_after int, log_mb_before int, log_mb_after int);
DECLARE @diffSql nvarchar(max) = N'SELECT @d = COUNT(*) FROM (SELECT k, c FROM (SELECT TOP ($(Rows)) $(Key) AS k, $(Col) AS c FROM [$(SandboxDb)].$(Table) ORDER BY $(Key)) AS a
    EXCEPT SELECT k, c FROM (SELECT TOP ($(Rows)) $(Key) AS k, $(Col) AS c FROM [$(SnapshotDb)].$(Table) ORDER BY $(Key)) AS b) AS d;';
DECLARE @trgSql nvarchar(max) = N'SELECT @n = COUNT(*) FROM [$(SandboxDb)].sys.triggers WHERE is_disabled = 0;';
WHILE @round <= $(Rounds)
BEGIN
    SELECT @sparse0 = SUM(size_on_disk_bytes) FROM sys.dm_io_virtual_file_stats(DB_ID(N'$(SnapshotDb)'), NULL);
    SELECT @log0 = size / 128 FROM sys.master_files WHERE database_id = DB_ID(N'$(SandboxDb)') AND type = 1;
    EXEC sys.sp_executesql @trgSql, N'@n int OUTPUT', @n = @trg0 OUTPUT;
    SET @t0 = SYSDATETIME();
    SET @sql = N'USE [$(SandboxDb)]; DISABLE TRIGGER ALL ON $(Table); WITH x AS (SELECT TOP ($(Rows)) $(Col) FROM $(Table) ORDER BY $(Key)) UPDATE x SET $(Col) = REVERSE($(Col));';
    EXEC (@sql);
    SET @t1 = SYSDATETIME();
    SELECT @sparse1 = SUM(size_on_disk_bytes) FROM sys.dm_io_virtual_file_stats(DB_ID(N'$(SnapshotDb)'), NULL);
    EXEC sys.sp_executesql @diffSql, N'@d int OUTPUT', @d = @d0 OUTPUT;
    SET @t2 = SYSDATETIME();
    ALTER DATABASE [$(SandboxDb)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    SET @t3 = SYSDATETIME();
    RESTORE DATABASE [$(SandboxDb)] FROM DATABASE_SNAPSHOT = N'$(SnapshotDb)';
    SET @t4 = SYSDATETIME();
    ALTER DATABASE [$(SandboxDb)] SET MULTI_USER;
    SET @t5 = SYSDATETIME();
    EXEC sys.sp_executesql @diffSql, N'@d int OUTPUT', @d = @d1 OUTPUT;
    EXEC sys.sp_executesql @trgSql, N'@n int OUTPUT', @n = @trg1 OUTPUT;
    SELECT @log1 = size / 128 FROM sys.master_files WHERE database_id = DB_ID(N'$(SandboxDb)') AND type = 1;
    INSERT @res VALUES (@round, $(Rows), DATEDIFF_BIG(MICROSECOND, @t0, @t1) / 1000.0, @sparse0 / 1048576.0, @sparse1 / 1048576.0, @d0,
                        DATEDIFF_BIG(MICROSECOND, @t2, @t3) / 1000.0, DATEDIFF_BIG(MICROSECOND, @t3, @t4) / 1000.0, DATEDIFF_BIG(MICROSECOND, @t4, @t5) / 1000.0,
                        @d1, @trg0, @trg1, @log0, @log1);
    SET @round += 1;
END;
SELECT * FROM @res ORDER BY round;
SELECT 'state_after' AS what, state_desc, user_access_desc FROM sys.databases WHERE name = N'$(SandboxDb)';
GO
