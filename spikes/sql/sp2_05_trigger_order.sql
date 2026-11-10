/* SP2 (a) — 현재 DB의 First/Last 점유와 감사 Last 지정 가능 여부(읽기 전용).
   sp2_10_observer.sql 첫 배치와 같은 쿼리. run-sql.ps1에 '첫 배치만' 옵션이 없어 다른 DB(AW·CascadeLab 등)에서 돌리려고 떼어 냈다.
   실행: .\scripts\run-sql.ps1 spikes\sql\sp2_05_trigger_order.sql -Database <DB> -Log spikes\logs\sp2-a-<DB>.log */
SELECT DB_NAME() AS db, OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS tableName, e.type_desc AS evt,
       MAX(CASE WHEN e.is_first = 1 THEN t.name END) AS firstTrigger,
       MAX(CASE WHEN e.is_last = 1 THEN t.name END) AS lastTrigger,
       COUNT(*) AS afterTriggers,
       CASE WHEN MAX(CASE WHEN e.is_last = 1 AND t.name NOT LIKE N'dbr[_]audit[_]%' THEN 1 ELSE 0 END) = 1 THEN N'NONE(사용자 Last 점유)' ELSE N'LAST 가능' END AS auditOrder
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
WHERE t.parent_class = 1 AND t.is_instead_of_trigger = 0
GROUP BY t.parent_id, e.type_desc
ORDER BY tableName, evt;
/* 참고: INSTEAD OF 트리거(순서 지정 불가), DB 옵션 */
SELECT DB_NAME() AS db, OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS tableName, t.name AS insteadOfTrigger, e.type_desc AS evt
FROM sys.triggers AS t JOIN sys.trigger_events AS e ON e.object_id = t.object_id
WHERE t.parent_class = 1 AND t.is_instead_of_trigger = 1
ORDER BY tableName, evt;
SELECT name AS db, is_recursive_triggers_on FROM sys.databases WHERE name = DB_NAME();
