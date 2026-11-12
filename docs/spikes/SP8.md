# SP8 — AW DM1·DM2·DM3 실제 전파, 데모 대본 값

**결과: 값 확정 (2026-11-12, DB 실측).** DM1은 주문 43697, DM2는 상세 행 52, DM3은 직원 1로 정했다. 세 값은 `scripts/demo/demo.http`에 넣었다.
- DM1: 43697을 지우면 SalesOrderDetail 1행과 SalesOrderHeaderSalesReason 2행이 FK 연쇄로 지워진다. 기본값이던 43659는 SalesReason이 0행이라 연쇄가 하나만 보여서 바꿨다.
- DM2: **SalesYTD가 실제로 바뀌는 주문은 없다.** AW2025의 회계 기간 함수는 2003-07-01~2004-06-30을 돌려주는데, 주문 날짜는 2022-05-30~2025-06-29에 있다. 그래서 `uSalesOrderHeader`의 SalesPerson·SalesTerritory UPDATE는 늘 0행으로 끝난다. 대본에서는 3단 "값 변경" 사슬을 빼고 "SalesYTD UPDATE 0행 발동"으로 보인다.
- DM3: `dEmployee`(INSTEAD OF DELETE)가 거부하므로 결과는 `trigger_blocked`다.

이 값으로 실제 Host를 끝까지 돌려 보았다. 원본은 AW_Origin이 아니라 같은 백업에서 복원한 전용 사본 `dbrt_e2e_origin`을 썼다. 미리보기 결과와 [적용] 결과가 이 스파이크의 판정과 일치했다(아래 "Host 실측").

## 목적

설계 §10.7 데모 대본 DM1·DM2·DM3에 쓸 실제 값을 AW_Origin 데이터로 고른다. 대본이 말하는 전파가 그 값에서 정말 일어나는지 확인한다. 특히 v1.3에서 0행이던 DM2의 SalesYTD 사슬이 값을 바꾸는 주문이 있는지 판정한다.

## 방법

AW_Origin에서는 읽기 전용 조회(SELECT)만 했다. 데이터와 스키마는 바꾸지 않았다.

```powershell
.\scripts\run-sql.ps1 spikes\sql\sp8-dm1-pick-order.sql -Database AW_Origin -Log spikes\logs\sp8-dm1.log
.\scripts\run-sql.ps1 spikes\sql\sp8-dm2-pick-order.sql -Database AW_Origin -Log spikes\logs\sp8-dm2.log
.\scripts\run-sql.ps1 spikes\sql\sp8-dm3-employee.sql   -Database AW_Origin -Log spikes\logs\sp8-dm3.log
```

추가 조회 두 개도 같은 방법(`run-sql.ps1`, AW_Origin, SELECT만)으로 실행했다. 스크립트는 저장소에 넣지 않았다. 핵심 문장은 아래와 같다.
- `spikes/logs/sp8-triggers.log`: 트리거 원문을 한 줄씩 출력했다. `sqlcmd -W`가 긴 nvarchar를 256자에서 자르기 때문에, `sp8-dm2.log`·`sp8-dm3.log`의 `OBJECT_DEFINITION` 열은 앞부분만 나온다.
  `SELECT OBJECT_NAME(t.object_id), s.ordinal, s.value FROM sys.triggers AS t CROSS APPLY STRING_SPLIT(OBJECT_DEFINITION(t.object_id), NCHAR(10), 1) AS s WHERE t.object_id IN (iduSalesOrderDetail, uSalesOrderHeader, dEmployee)`
  호환 수준은 170이다.
- `spikes/logs/sp8-dm2-detail.log`: `sp8-dm2`의 후보 표가 비었기 때문에 DM2 상세 행을 다른 조건으로 다시 골랐다. 조건은 Status 5, 판매원 있음, 개인 고객의 Demographics에 TotalPurchaseYTD 있음, 주문 상세 1~3행이고, SalesOrderDetailID 오름차순으로 정렬했다. 같은 조회로 DM7 행(Product 680)도 확인했다.

## 결과

### DM1 (`sp8-dm1.log`)

| 항목 | 값 | 구분 |
|---|---|---|
| SOH를 참조하는 FK | `FK_SalesOrderDetail_SalesOrderHeader_SalesOrderID`, `FK_SalesOrderHeaderSalesReason_SalesOrderHeader_SalesOrderID` 둘 다 CASCADE, 활성. 다른 참조 없음 | 확인 |
| 관련 트리거 | SOD `iduSalesOrderDetail`(AFTER I/U/D), SOH `uSalesOrderHeader`(AFTER UPDATE), Person `iuPerson`(AFTER I/U). INSTEAD OF·비활성·First/Last 없음 | 확인 |
| 후보 | SOHSR이 있고 SOD가 1~3행인 주문 10개는 모두 SOD 1행·SOHSR 2행·개인 고객·TotalPurchaseYTD 있음(43697, 43702, 43703, ...) | 확인 |
| 기본값 43659 | SOD 12행, SOHSR 0행: 연쇄가 하나만 보인다 | 확인 |
| **선택** | **43697**: SOD 353, SOHSR 사유 5·9, 고객 21768(Person 14078) | — |
| 연쇄 대상 트리거 | `iduSalesOrderDetail`의 deleted 분기에는 Person UPDATE가 있지만, SOH와 조인하는데 SOH가 이미 지워져 0행으로 끝난다. SOH SubTotal UPDATE도 0행이다 | 원문으로 추정, Host 실측으로 확인(firing rows 0) |

### DM2 (`sp8-dm2.log`, `sp8-dm2-detail.log`, `sp8-triggers.log`)

| 항목 | 값 | 구분 |
|---|---|---|
| 회계 기간 | `ufnGetAccountingStartDate()` = 2003-07-01, `ufnGetAccountingEndDate()` = 2004-06-30 23:59:59.997(상수, 원문 확인) | 확인 |
| 주문 날짜 범위 | 2022-05-30 ~ 2025-06-29. 기간에 든 주문 0건, 그중 Status 5·판매원 있음 0건 | 확인 |
| SalesYTD 갱신 조건 | `uSalesOrderHeader`는 `IF UPDATE([SubTotal])`일 때 `WHERE BusinessEntityID IN (SELECT inserted.SalesPersonID FROM inserted WHERE inserted.OrderDate BETWEEN @StartDate AND @EndDate)`로 갱신한다. 지역도 같은 꼴이다 | 확인(원문) |
| **판정** | 기간에 든 주문이 0건이라 SalesPerson·SalesTerritory UPDATE는 어떤 상세 행을 바꿔도 0행이다. SalesYTD 값이 바뀌는 주문은 **없다**. `sp8-dm2` 2번 후보 표가 빈 것도 같은 이유다 | 확인(원문 + 데이터), Host 실측으로 확인 |
| 상세 ID 유일성 | SalesOrderDetailID 중복 0 | 확인 |
| **선택** | **52**: 주문 43663의 유일한 상세 행, ProductID 760, OrderQty 1, 판매원 276, 지역 4, 개인 고객 Person 483(TotalPurchaseYTD 있음) | — |
| 예상 전파 | TH INSERT 1, Person 483 Demographics UPDATE(+LineTotal, −옛 LineTotal), SOH 43663 SubTotal UPDATE, 그 뒤 `uSalesOrderHeader`가 RevisionNumber UPDATE(깊이 3)와 SalesPerson·SalesTerritory UPDATE 0행 | 원문으로 추정, Host 실측으로 확인 |

43663을 고른 이유: 상세가 1행이라 SubTotal이 그 행의 LineTotal과 같다. 그래서 화면에서 값 변화를 읽기 쉽다. 주문 43659의 상세 행 1(SP7에서 쓴 행)도 같은 모양이지만, 그 주문은 상세가 12행이다.

### DM3 (`sp8-dm3.log`, `sp8-triggers.log`)

| 항목 | 값 | 구분 |
|---|---|---|
| 트리거 | `HumanResources.dEmployee` INSTEAD OF DELETE, 활성 | 확인 |
| 거부 방식 | `@@ROWCOUNT` 0이면 RETURN. 아니면 `RAISERROR(..., 10, 1)`(심각도 10, 정보 메시지) 뒤 `IF @@TRANCOUNT > 0 ROLLBACK`. 오류는 트리거 안 ROLLBACK이 일으키는 3609다 | 확인(원문), Host 실측으로 확인 |
| Employee 참조 FK | 6개 모두 NO_ACTION(Document, EmployeeDepartmentHistory, EmployeePayHistory, JobCandidate, PurchaseOrderHeader, SalesPerson). INSTEAD OF가 DELETE를 대신하므로 검사되지 않는다 | 확인(FK) / 검사 안 됨은 추정 |
| **선택** | **1**(Chief Executive Officer, CurrentFlag 1). 있는 직원이면 된다 | — |

## Host 실측 (2026-11-12)

전용 원본 `dbrt_e2e_origin`에서 실행했다. 이 원본은 `AdventureWorks2025.bak`을 CLI `restore`로 복원하고 `restore-aw-origin.ps1 -PreviewUserOnly -DbName dbrt_e2e_origin`을 적용한 것이다. Host는 `DbRipple__OriginDb` 등 환경 변수로 띄웠다. 전체 기록은 `logs/e2e/host-e2e-2026-11-12.log`에 있고, 독립 SQL 확인은 `logs/e2e/sql-*.log`에 있다. 끝난 뒤 `dbrt_e2e_*` DB와 백업 파일은 지웠다.

| DM | 미리보기 | [적용] | 독립 SQL 확인 |
|---|---|---|---|
| DM1 43697 | ok. 묶음 5개: SOH DELETE 1(DIRECT), SOD DELETE 1·SOHSR DELETE 2(FK_CASCADE certain, FK 이름 일치), SOH UPDATE 0·Person UPDATE 0(TRIGGER inferred, iduSalesOrderDetail). netSummary SOD D1, SOH D1, SOHSR D2 | committed, netSummaryMatch true | 43697의 SOH·SOD·SOHSR 0행, 전체 건수 −1/−1/−2 |
| DM2 52 | ok. 묶음 12개: TH INSERT 1, Person UPDATE 1, SOH UPDATE 1(깊이 2) → SOH UPDATE 1(깊이 3), SalesPerson·SalesTerritory UPDATE 0(깊이 3). netSummary Person U1, TH I1, SOD U1, SOH U1 | committed, volatileIgnored TH 3열, identityPairs 1 | OrderQty 1→2, RevisionNumber 10→11, SubTotal 419.4589→838.9178, TotalPurchaseYTD +419.4589, SalesYTD는 AW_Origin과 같음 |
| DM3 1 | trigger_blocked, 오류 3609, 묶음 0, error 노드 + dEmployee REPLACES | precondition_failed [PREVIEW_NOT_OK] | — |

## 증거 범위

- 값 선택과 판정: DB 실측(로컬 컨테이너 AW_Origin, 읽기 전용 SELECT).
- 전파와 [적용]: API 실측(로컬 Host, 전용 사본 `dbrt_e2e_origin`) + DB 실측(독립 SQL).
- AW_Origin에서는 [적용]을 실행하지 않았다. 데모에서 AW_Origin에 [적용]하면 그 행이 실제로 바뀐다. 다시 하려면 원본을 다시 복원한다.

## 설계 반영(제안, 이 스파이크에서는 고치지 않음)

- §10.7 DM2: "SalesPerson·SalesTerritory SalesYTD UPDATE(3단 사슬)"을 "SalesYTD UPDATE 0행 발동(회계 기간 2003-07~2004-06에 든 주문 0건)"으로 바꾼다. 값이 바뀌는 사슬을 꼭 보이려면 데이터를 바꿔야 하는데, 그건 공개 샘플을 그대로 쓰는 원칙과 부딪친다. 그래서 0행 묶음(observed, rows 0)을 보이는 쪽을 권한다.
- §10.7 DM1: 기본 주문을 43659에서 43697로 바꾼다(SOHSR 연쇄가 보이게).
- `docs/spikes/README.md`의 SP8 행: "스크립트 준비, 실행 전"을 이 결과로 바꾼다(이 작업의 편집 범위 밖이라 고치지 않았다).
- `spikes/sql/sp8-dm2-pick-order.sql` 4·5번의 원문 출력은 `run-sql.ps1`(sqlcmd `-W`, 기본 열 폭)에서 256자로 잘린다. 원문이 필요하면 위의 `STRING_SPLIT` 줄 단위 조회를 쓴다.
