/* SP8 DM2 — SalesOrderDetail.OrderQty UPDATE 데모용 상세 행 고르기(읽기 전용: SELECT만).
   데모 문장: UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderDetailID = <고른 값>;
   설계 §10.7 DM2: iduSalesOrderDetail → TransactionHistory INSERT, Person.Person(Demographics) UPDATE, SalesOrderHeader.SubTotal UPDATE
   → uSalesOrderHeader → RevisionNumber 재갱신(깊이 3 SOH 묶음), SalesPerson·SalesTerritory SalesYTD UPDATE(3단 사슬).
   v1.3 관측: 이전에 쓴 주문은 SalesPerson·SalesTerritory UPDATE가 0행이었다. 그래서 SalesYTD가 실제로 바뀌는 상세 행을 고른다.
   uSalesOrderHeader의 SalesYTD 갱신 조건(AW 트리거 본문, 아래 5에서 원문 확인):
     - 갱신 대상 행: inserted.OrderDate가 dbo.ufnGetAccountingStartDate() ~ dbo.ufnGetAccountingEndDate() 사이인 주문의 SalesPersonID·TerritoryID
     - 새 값: 그 영업사원(지역)의 Status = 5이고 OrderDate가 같은 기간인 주문 SubTotal 합(처음부터 다시 계산)
     → 기간 안 주문이면 UPDATE 행이 생기고, 새 합이 지금 SalesYTD와 다르면 값이 바뀐다.
   실행: .\scripts\run-sql.ps1 spikes\sql\sp8-dm2-pick-order.sql -Database AW_Origin -Log spikes\logs\sp8-dm2.log */
SET NOCOUNT ON;

DECLARE @start datetime = dbo.ufnGetAccountingStartDate();
DECLARE @end   datetime = dbo.ufnGetAccountingEndDate();

/* 1. 회계 기간과 그 기간에 든 주문 수. 0이면 이 원본 데이터로는 SalesYTD 사슬을 보일 수 없다(대본에서 DM2 3단 사슬 문구를 뺀다). */
SELECT @start AS accounting_start,
       @end AS accounting_end,
       (SELECT COUNT(*) FROM Sales.SalesOrderHeader AS h WHERE h.OrderDate BETWEEN @start AND @end) AS orders_in_period,
       (SELECT COUNT(*) FROM Sales.SalesOrderHeader AS h
         WHERE h.OrderDate BETWEEN @start AND @end AND h.SalesPersonID IS NOT NULL AND h.Status = 5) AS shipped_with_salesperson,
       (SELECT MIN(h.OrderDate) FROM Sales.SalesOrderHeader AS h) AS min_order_date,
       (SELECT MAX(h.OrderDate) FROM Sales.SalesOrderHeader AS h) AS max_order_date;

/* 2. 후보 상세 행: 기간 안, Status 5, 영업사원·지역 있음. OrderQty + 1 뒤 예상 SalesYTD와 지금 값을 비교한다.
      line_delta = UnitPrice * (1 - UnitPriceDiscount)(LineTotal 식 기준 1개 증가분). SubTotal은 money라 계산은 근사값이다.
      개인 고객(PersonID)이고 Demographics에 TotalPurchaseYTD가 있으면 Person UPDATE도 보인다. */
WITH sp AS (
    SELECT h.SalesPersonID, SUM(h.SubTotal) AS shipped_sum
    FROM Sales.SalesOrderHeader AS h
    WHERE h.Status = 5 AND h.OrderDate BETWEEN @start AND @end AND h.SalesPersonID IS NOT NULL
    GROUP BY h.SalesPersonID
),
st AS (
    SELECT h.TerritoryID, SUM(h.SubTotal) AS shipped_sum
    FROM Sales.SalesOrderHeader AS h
    WHERE h.Status = 5 AND h.OrderDate BETWEEN @start AND @end AND h.TerritoryID IS NOT NULL
    GROUP BY h.TerritoryID
),
cand AS (
    SELECT d.SalesOrderDetailID, d.SalesOrderID, d.ProductID, d.OrderQty, d.UnitPrice, d.UnitPriceDiscount,
           CAST(d.UnitPrice * (1.0 - d.UnitPriceDiscount) AS decimal(19, 4)) AS line_delta,
           h.SalesPersonID, h.TerritoryID, h.OrderDate, c.PersonID,
           s.SalesYTD AS salesperson_ytd_now,
           sp.shipped_sum + CAST(d.UnitPrice * (1.0 - d.UnitPriceDiscount) AS money) AS salesperson_ytd_projected,
           t.SalesYTD AS territory_ytd_now,
           st.shipped_sum + CAST(d.UnitPrice * (1.0 - d.UnitPriceDiscount) AS money) AS territory_ytd_projected
    FROM Sales.SalesOrderDetail AS d
    INNER JOIN Sales.SalesOrderHeader AS h ON h.SalesOrderID = d.SalesOrderID
    INNER JOIN Sales.Customer AS c ON c.CustomerID = h.CustomerID
    INNER JOIN Sales.SalesPerson AS s ON s.BusinessEntityID = h.SalesPersonID
    INNER JOIN Sales.SalesTerritory AS t ON t.TerritoryID = h.TerritoryID
    INNER JOIN sp ON sp.SalesPersonID = h.SalesPersonID
    INNER JOIN st ON st.TerritoryID = h.TerritoryID
    WHERE h.Status = 5
      AND h.OrderDate BETWEEN @start AND @end
)
SELECT TOP (10)
       cand.SalesOrderDetailID, cand.SalesOrderID, cand.ProductID, cand.OrderQty, cand.line_delta,
       cand.SalesPersonID, cand.TerritoryID, cand.OrderDate, cand.PersonID,
       CASE WHEN p.Demographics.exist(N'declare default element namespace "http://schemas.microsoft.com/sqlserver/2004/07/adventure-works/IndividualSurvey"; /IndividualSurvey/TotalPurchaseYTD') = 1
            THEN 1 ELSE 0 END AS person_has_total_purchase_ytd,
       cand.salesperson_ytd_now, cand.salesperson_ytd_projected,
       CASE WHEN cand.salesperson_ytd_projected <> cand.salesperson_ytd_now THEN 1 ELSE 0 END AS salesperson_ytd_changes,
       cand.territory_ytd_now, cand.territory_ytd_projected,
       CASE WHEN cand.territory_ytd_projected <> cand.territory_ytd_now THEN 1 ELSE 0 END AS territory_ytd_changes
FROM cand
LEFT JOIN Person.Person AS p ON p.BusinessEntityID = cand.PersonID
WHERE cand.salesperson_ytd_projected <> cand.salesperson_ytd_now
  AND cand.territory_ytd_projected <> cand.territory_ytd_now
ORDER BY CASE WHEN cand.PersonID IS NULL THEN 1 ELSE 0 END, cand.SalesOrderDetailID;

/* 3. WHERE SalesOrderDetailID = x가 1행인지: PK는 (SalesOrderID, SalesOrderDetailID)이지만 SalesOrderDetailID는 IDENTITY라 전체에서 유일할 것이다. 기대값 0. */
SELECT COUNT(*) AS duplicated_detail_ids
FROM (SELECT d.SalesOrderDetailID, COUNT(*) AS n FROM Sales.SalesOrderDetail AS d GROUP BY d.SalesOrderDetailID) AS x
WHERE x.n > 1;

/* 4. 회계 기간 함수 원문(기간이 데이터 범위 밖이면 이 원문이 원인이다). */
SELECT N'dbo.ufnGetAccountingStartDate' AS module_name, OBJECT_DEFINITION(OBJECT_ID(N'dbo.ufnGetAccountingStartDate')) AS definition
UNION ALL
SELECT N'dbo.ufnGetAccountingEndDate', OBJECT_DEFINITION(OBJECT_ID(N'dbo.ufnGetAccountingEndDate'));

/* 5. 사슬에 든 트리거 원문(대본 문구를 원문으로 확인한다). */
SELECT OBJECT_SCHEMA_NAME(t.object_id) + N'.' + t.name AS trigger_name, OBJECT_DEFINITION(t.object_id) AS definition
FROM sys.triggers AS t
WHERE t.object_id IN (OBJECT_ID(N'Sales.iduSalesOrderDetail'), OBJECT_ID(N'Sales.uSalesOrderHeader'), OBJECT_ID(N'Person.iuPerson'))
ORDER BY trigger_name;
