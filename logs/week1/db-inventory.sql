-- 1주차 종료 시점 사용자 DB 목록(읽기 전용)
SELECT name, state_desc, user_access_desc, DB_NAME(source_database_id) AS snapshot_of, is_recursive_triggers_on
FROM sys.databases WHERE database_id > 4 ORDER BY name;
GO
