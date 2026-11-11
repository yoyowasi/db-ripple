/* SP8 DM3 — HumanResources.Employee DELETE 데모 확인(읽기 전용: SELECT만).
   데모 문장: DELETE FROM HumanResources.Employee WHERE BusinessEntityID = <고른 값>;
   설계 §10.7 DM3: INSTEAD OF DELETE 트리거 dEmployee가 거부한다. 변경 0건, error 노드, [적용] 비활성.
   dEmployee 본문은 @@ROWCOUNT가 0이면 거부 없이 RETURN하므로 반드시 있는 직원을 고른다(0행이면 trigger_blocked가 아니라 ok, 변경 0).
   실행: .\scripts\run-sql.ps1 spikes\sql\sp8-dm3-employee.sql -Database AW_Origin -Log spikes\logs\sp8-dm3.log */
SET NOCOUNT ON;

/* 1. Employee의 트리거(INSTEAD OF DELETE 확인)와 원문(거부 방식: RAISERROR 심각도와 ROLLBACK 여부). */
SELECT OBJECT_SCHEMA_NAME(t.object_id) + N'.' + t.name AS trigger_name,
       t.is_instead_of_trigger, t.is_disabled,
       OBJECT_DEFINITION(t.object_id) AS definition
FROM sys.triggers AS t
WHERE t.parent_id = OBJECT_ID(N'HumanResources.Employee');

/* 2. 후보 직원(BusinessEntityID 오름차순 5명). INSTEAD OF가 원래 DELETE를 대신하므로 Employee를 참조하는 FK는 검사되지 않는다. */
SELECT TOP (5) e.BusinessEntityID, e.JobTitle, e.CurrentFlag
FROM HumanResources.Employee AS e
ORDER BY e.BusinessEntityID;

/* 3. Employee를 참조하는 FK(참고: INSTEAD OF가 없다면 547 경로였을 것). */
SELECT fk.name AS fk_name,
       OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id) AS child_table,
       fk.delete_referential_action_desc AS on_delete
FROM sys.foreign_keys AS fk
WHERE fk.referenced_object_id = OBJECT_ID(N'HumanResources.Employee')
ORDER BY fk.name;
