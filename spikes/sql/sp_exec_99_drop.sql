/* EXEC 스파이크(SP11·SP12·SP13·SP14·SP16)가 만든 전용 DB를 지운다. 없는 DB는 건너뛴다.
   실행: .\scripts\run-sql.ps1 spikes\sql\sp_exec_99_drop.sql -Log spikes\logs\sp-exec-99-drop.log
   AW_SpikeB(SP14 AW 판 사본)는 지우지 않는다(다른 스파이크가 쓴다). 지우려면 DROP DATABASE를 직접 한다. */
USE master;
GO
DECLARE @names TABLE (n int IDENTITY(1, 1), name sysname);
INSERT @names (name) VALUES (N'SpikeLab_SP11'), (N'SpikeLab_SP12'), (N'SpikeLab_SP13'), (N'SpikeLab_SP14'), (N'SpikeLab_SP14_Other'), (N'SpikeLab_SP16');
DECLARE @i int = 1, @name sysname, @sql nvarchar(400);
WHILE @i <= (SELECT COUNT(*) FROM @names)
BEGIN
    SELECT @name = name FROM @names WHERE n = @i;
    IF DB_ID(@name) IS NOT NULL
    BEGIN
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';';
        EXEC sys.sp_executesql @sql;
        PRINT CONCAT(N'dropped ', @name);
    END;
    SET @i += 1;
END;
GO
