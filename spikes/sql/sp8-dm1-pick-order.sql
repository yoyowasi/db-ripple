/* SP8 DM1 — SalesOrderHeader 1건 DELETE 데모용 주문 고르기(읽기 전용: SELECT만, 데이터·스키마를 바꾸지 않는다).
   데모 문장: DELETE FROM Sales.SalesOrderHeader WHERE SalesOrderID = <고른 값>;
   설계 §10.7 DM1: FK 연쇄로 SalesOrderDetail·SalesOrderHeaderSalesReason이 삭제되고, 연쇄 대상에서 iduSalesOrderDetail이 발동한다.
   트리거 본문 분기(DELETE에서는 UPDATE(col)이 거짓, SOH 행은 이미 지워짐)에 따라 추가 변경이 없을 수 있다("발동했지만 변경 없음").
   실행: .\scripts\run-sql.ps1 spikes\sql\sp8-dm1-pick-order.sql -Database AW_Origin -Log spikes\logs\sp8-dm1.log
   예상과 실측은 Host 미리보기(scripts\demo\demo.http DM1)로 확정한다. 이 스크립트는 후보와 근거만 보인다. */
SET NOCOUNT ON;

/* 1. SalesOrderHeader를 참조하는 FK와 참조 동작. CASCADE 2개(SOD, SOHSR) 밖에 NO ACTION 참조가 있으면 그 주문은 DELETE가 547로 실패한다. */
SELECT fk.name AS fk_name,
       OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id) AS child_table,
       fk.delete_referential_action_desc AS on_delete,
       fk.is_disabled
FROM sys.foreign_keys AS fk
WHERE fk.referenced_object_id = OBJECT_ID(N'Sales.SalesOrderHeader')
ORDER BY fk.name;

/* 2. 관련 테이블의 사용자 DML 트리거(이벤트, INSTEAD OF, 비활성, First/Last). */
SELECT OBJECT_SCHEMA_NAME(t.parent_id) + N'.' + OBJECT_NAME(t.parent_id) AS table_name,
       t.name AS trigger_name,
       te.type_desc COLLATE DATABASE_DEFAULT AS event_type,
       te.is_first, te.is_last,
       t.is_instead_of_trigger, t.is_disabled
FROM sys.triggers AS t
INNER JOIN sys.trigger_events AS te ON te.object_id = t.object_id
WHERE t.parent_class = 1
  AND t.parent_id IN (OBJECT_ID(N'Sales.SalesOrderHeader'), OBJECT_ID(N'Sales.SalesOrderDetail'),
                      OBJECT_ID(N'Sales.SalesOrderHeaderSalesReason'), OBJECT_ID(N'Person.Person'))
ORDER BY table_name, trigger_name, event_type;

/* 3. 후보 주문: SalesReason이 있고(두 연쇄가 모두 보이게) 상세가 1~3행. 고객이 개인(PersonID)인지와 Demographics에 TotalPurchaseYTD가 있는지도 보인다
      (iduSalesOrderDetail의 deleted 분기 Person UPDATE 조건. 다만 그 UPDATE는 SOH와 조인하므로 SOH가 먼저 지워졌으면 0행일 수 있다). */
WITH cand AS (
    SELECT h.SalesOrderID,
           h.Status,
           h.CustomerID,
           c.PersonID,
           (SELECT COUNT(*) FROM Sales.SalesOrderDetail AS d WHERE d.SalesOrderID = h.SalesOrderID) AS detail_rows,
           (SELECT COUNT(*) FROM Sales.SalesOrderHeaderSalesReason AS r WHERE r.SalesOrderID = h.SalesOrderID) AS reason_rows
    FROM Sales.SalesOrderHeader AS h
    INNER JOIN Sales.Customer AS c ON c.CustomerID = h.CustomerID
)
SELECT TOP (10)
       cand.SalesOrderID, cand.Status, cand.CustomerID, cand.PersonID,
       cand.detail_rows, cand.reason_rows,
       CASE WHEN p.Demographics.exist(N'declare default element namespace "http://schemas.microsoft.com/sqlserver/2004/07/adventure-works/IndividualSurvey"; /IndividualSurvey/TotalPurchaseYTD') = 1
            THEN 1 ELSE 0 END AS person_has_total_purchase_ytd
FROM cand
LEFT JOIN Person.Person AS p ON p.BusinessEntityID = cand.PersonID
WHERE cand.reason_rows > 0
  AND cand.detail_rows BETWEEN 1 AND 3
ORDER BY cand.detail_rows, cand.SalesOrderID;

/* 4. 기본 데모 주문(43659, Host .http 예시)의 같은 값. 후보 표에서 고른 주문이 있으면 demo.http의 @dm1Order를 그 값으로 바꾼다. */
SELECT h.SalesOrderID,
       (SELECT COUNT(*) FROM Sales.SalesOrderDetail AS d WHERE d.SalesOrderID = h.SalesOrderID) AS detail_rows,
       (SELECT COUNT(*) FROM Sales.SalesOrderHeaderSalesReason AS r WHERE r.SalesOrderID = h.SalesOrderID) AS reason_rows
FROM Sales.SalesOrderHeader AS h
WHERE h.SalesOrderID = 43659;
