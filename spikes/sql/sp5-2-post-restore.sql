-- SP5-2: 복원 뒤 구조 판정. AW_Origin 이름 고정(DB 이름이 바뀌면 안 됨).
USE AW_Origin;
GO
-- type: FG=행, FX=메모리 최적화, FD=FILESTREAM
SELECT name, type, type_desc, is_default FROM sys.filegroups;
GO
SELECT COUNT(*) AS memory_optimized_or_filestream_groups FROM sys.filegroups WHERE type IN ('FX', 'FD');
GO
SELECT fulltext_catalog_id, name, is_default FROM sys.fulltext_catalogs;
GO
SELECT OBJECT_SCHEMA_NAME(object_id) AS sch, OBJECT_NAME(object_id) AS tbl, fulltext_catalog_id, is_enabled
FROM sys.fulltext_indexes;
GO
SELECT name, type_desc, physical_name, size * 8 / 1024 AS size_mb FROM sys.database_files;
GO
