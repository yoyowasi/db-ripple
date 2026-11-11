/* DM7 — 미리보기 뒤 원본 행을 바꾸고 [적용]하면 drift_detected가 나와야 한다(설계 §10.7 DM7, §5.3 드리프트 검사).
   데모 전용. 로컬 컨테이너의 AW_Origin(AdventureWorks 공개 샘플 사본)에서만 실행한다. 다른 DB에서는 THROW로 멈춘다.
   바꾸는 행: Production.Product ProductID 680의 ListPrice(+0.01). Product에는 사용자 DML 트리거가 없어 이 변경이 다른 행으로 번지지 않는다.

   순서(scripts\demo\demo.http DM7):
     1) Host에서 DM7 미리보기: UPDATE Production.Product SET ListPrice = ListPrice + 1.00 WHERE ProductID = 680;
     2) 이 파일 PART 1:  .\scripts\run-sql.ps1 scripts\demo\dm7-drift.sql -Database AW_Origin -Part 1 -Log logs\week3\dm7-drift.log
     3) DM7 [적용] → status drift_detected, verification.driftRows에 Production.Product (680) CHANGED ListPrice
     4) 되돌리기 PART 2: .\scripts\run-sql.ps1 scripts\demo\dm7-drift.sql -Database AW_Origin -Part 2 -Log logs\week3\dm7-revert.log
        PART 1을 여러 번 실행했다면 그 횟수만큼 PART 2를 실행하거나 .\scripts\restore-aw-origin.ps1로 원본을 다시 복원한다.
        그다음 POST /api/sandbox/refresh로 기준선을 다시 만든다.
   -Part 없이 실행하면 바꾸고 바로 되돌리므로 드리프트가 남지 않는다. 반드시 -Part 1 / -Part 2로 나눠 실행한다. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'AW_Origin'
    THROW 50000, N'dm7-drift.sql runs only on the local AW_Origin demo copy.', 1;
IF NOT EXISTS (SELECT 1 FROM Production.Product WHERE ProductID = 680)
    THROW 50000, N'Production.Product ProductID 680 not found.', 1;

SELECT p.ProductID, p.ListPrice AS before_list_price FROM Production.Product AS p WHERE p.ProductID = 680;
UPDATE Production.Product SET ListPrice = ListPrice + 0.01 WHERE ProductID = 680;
SELECT p.ProductID, p.ListPrice AS after_list_price FROM Production.Product AS p WHERE p.ProductID = 680;
GO
-- PART 2 되돌리기(PART 1의 +0.01을 뺀다)
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'AW_Origin'
    THROW 50000, N'dm7-drift.sql runs only on the local AW_Origin demo copy.', 1;

SELECT p.ProductID, p.ListPrice AS before_revert FROM Production.Product AS p WHERE p.ProductID = 680;
UPDATE Production.Product SET ListPrice = ListPrice - 0.01 WHERE ProductID = 680;
SELECT p.ProductID, p.ListPrice AS after_revert FROM Production.Product AS p WHERE p.ProductID = 680;
GO
