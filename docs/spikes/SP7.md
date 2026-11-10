# SP7 — 열린 트랜잭션 안 최종 차이

**결과: 통과 (2026-11-10, DB 실측, 2회).** REVERT 뒤 같은 연결·트랜잭션에서 미커밋 변경을 스냅샷과 비교한 결과가 독립 확인 10항목과 모두 일치했다. AW 전 테이블(71개, 760,598행) 기준 시간은 다음과 같다.
- 열린 트랜잭션 안: 첫 실행 p50 1.55초·p95 2.03초, 다시 실행 p50 1.65초·p95 3.70초
- 롤백 뒤 트랜잭션 없이 비교(실패 경로): 첫 실행 p50 1.45초, 다시 실행 p50 1.88초

다시 실행한 까닭은 C12를 고쳤기 때문이다. 계산 열을 서버 비교 조건에서 뺐다(아래 "C12 변경"). 대체 경로(§13 재조회와 KEYSET 비교)는 필요 없다.

## 목적

설계 §13 SP7과 위험 R5를 닫는다. C12가 실행기의 열린 트랜잭션 안에서 미커밋 변경을 스냅샷과 정확히 비교하는지, AW 전 테이블 기준으로 수 초 안에 끝나는지를 확인한다.

## 방법

실행 코드는 `tests/DbRipple.Diff.Tests/Sp7Tests.cs`다. 기본 테스트 실행에서는 건너뛰고, 다음처럼 따로 돌린다.

```powershell
. .\scripts\env.ps1; $env:DBRIPPLE_RUN_SP7='1'; dotnet test tests\DbRipple.Diff.Tests --filter FullyQualifiedName~Sp7Tests
```

1. `TestBaseline.BuildAdventureWorksAsync("dif")`로 전용 기준선(원본, 샌드박스, 스냅샷)을 만든다. 7.4초 걸렸다.
2. 비교 대상은 샌드박스 카탈로그에서 audit 스키마를 뺀 71개 테이블이다. 모두 PK가 있다.
3. 시작 상태의 차이가 0건인지 확인한다.
4. 트랜잭션 밖에서 `CREATE USER sp7_dml WITHOUT LOGIN WITH DEFAULT_SCHEMA = dbo`와 `GRANT SELECT, INSERT, UPDATE, DELETE TO sp7_dml`(DB 수준 DML만)을 실행하고 차이를 기록한다.
5. DM1·DM2 대상과 기대값을 스냅샷에서 직접 질의해 정한다.
   - DM1: SOD 3행 이상과 SOHSR이 있는 첫 주문 51178(SOD 37751~37753, SOHSR 사유 1·2)
   - DM2: 판매원이 있는 주문 43659의 SalesOrderDetailID 1, OrderQty 1→2
6. 한 연결에서 차례로 실행한다: `SET XACT_ABORT ON; BEGIN TRAN` → `EXECUTE AS USER = N'sp7_dml'` → DM1 `DELETE Sales.SalesOrderHeader WHERE SalesOrderID = 51178` → DM2 `UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderDetailID = 1` → `REVERT`. C10과 같이 T-SQL 트랜잭션을 쓰고 SqlSession.Transaction은 null이다.
7. 같은 연결에서 NetDiffer 전 테이블 비교를 워밍업 1회 뒤 5회 실행하고, 매회 결과가 같은지 본다.
8. NetDiffer와 다른 방법(키 목록 질의, EXCEPT, COUNT_BIG)으로 독립 확인한다.
   - 연쇄 삭제된 SOD·SOHSR의 행 수와 키
   - 트리거 대상 행 키: TransactionHistory 새 키는 `live EXCEPT snap`의 TransactionID로, SOH 43659는 UPDATED와 SubTotal 포함 여부로
   - 71개 테이블 모두에서 `COUNT_BIG` 증감 = I − D, `live EXCEPT snap` 행 수 = I + U, `snap EXCEPT live` 행 수 = D + U. xml은 nvarchar(max)로, CLR 형식은 varbinary(max)로 바꿔 비교한다.
9. ROLLBACK한 뒤 트랜잭션 없이 전 테이블 비교를 워밍업 1회 뒤 5회 실행한다(실패 경로와 같은 호출). 차이가 4단계 직후 상태와 같은지 확인한다.
10. 테이블별로 1회씩 시간을 잰다.

## 결과

| 항목 | 결과 |
|---|---|
| EXECUTE AS / REVERT | 가장 중 USER_NAME()=sp7_dml, REVERT 뒤 dbo, @@TRANCOUNT 1. DM1·DM2 각 1행 |
| 열린 트랜잭션 안 시간 | 워밍업 1926ms, 5회 2028·1669·1460·1532·1553ms → **p50 1553ms, p95 2028ms**(5회라 p95는 최댓값) |
| 독립 확인 | 10/10 일치(SOH 키, SOD 행 수·키, SOHSR 행 수·키, SOD OrderQty, TransactionHistory 키, SOH 트리거 대상 키·SubTotal, 71개 테이블 건수 불일치 0) |
| 롤백 뒤(트랜잭션 없음) 시간 | 워밍업 1580ms, 5회 1447·1485·1368·1430·1575ms → **p50 1447ms, p95 1575ms** |
| 롤백 뒤 차이 | 2건. 사용자 생성 직후 상태와 같고, DM이 남긴 차이는 0 |
| 테이블별 1회 | 합계 1366ms. 상위는 Sales.Customer 219, Person.Person 177, Person.Address 101, dbo.DatabaseLog 67, SalesOrderDetail 66, TransactionHistory 65, SalesOrderHeader 60ms |

DM분 결과 10건:
- SOH: DELETED 51178, UPDATED 43659 [RevisionNumber, SubTotal, TotalDue]
- SOD: DELETED 3건, UPDATED (43659,1) [OrderQty, LineTotal]
- SOHSR: DELETED 2건
- Production.TransactionHistory: INSERTED 213443
- Person.Person: UPDATED 1045 [Demographics]

### 다시 실행 (13:00, C12 변경 뒤, 현재 로그)

| 항목 | 첫 실행 11:36 | 다시 실행 13:00 |
|---|---|---|
| 열린 트랜잭션 안 | p50 1553ms, p95 2028ms | 5회 1652·1652·1341·1796·3697ms → p50 1652ms, p95 3697ms |
| 롤백 뒤(트랜잭션 없음) | p50 1447ms, p95 1575ms | p50 1875ms, p95 2129ms |
| 테이블별 1회 합계 | 1366ms(최대 Sales.Customer 219ms) | 1509ms(최대 Person.Person 328ms) |
| 독립 확인 | 10/10 일치 | 10/10 일치 |
| 롤백 뒤 DM이 남긴 차이 | 0 | 0(남는 것은 CREATE USER·GRANT가 쓴 dbo.DatabaseLog 2행) |

3697ms 한 번은 다른 테스트와 겹친 탓으로 추정한다(확인 안 함). 그 사이 11:50 실행 하나는 AW 기준선을 만드는 단계에서 SqlClient 사전 로그인 핸드셰이크 시간 초과로 1시간 9분 뒤 끝났다. 이때 C12 코드는 실행되지 않았다. 원인(서버 부하 추정)은 확인하지 못했다.

### C12 변경 (exec-1 재현, R18)

비지속 계산 열 하나가 읽기만 해도 8115(산술 오버플로)를 내면 전 테이블 비교 전체가 실패했다.
- 키 있는 테이블: 계산 열(지속·비지속 모두)을 서버 비교 조건에서 뺀다. 계산 열은 같은 행의 다른 열에서 나오므로, 계산 열이 바뀌면 기본 열도 바뀐다. 바뀐 행의 이미지에는 계산 열을 계속 넣는다.
- 키 없는 테이블: 계산 열을 이미지와 다중집합 비교에서 모두 뺀다.
- 남는 위험: 오버플로하는 행 자체가 바뀐 경우에는 그 행의 이미지를 만들 때 8115가 난다. 트랜잭션 안이면 XACT_ABORT ON 때문에 트랜잭션도 끝난다. C10이 DiffAsync 예외로 처리한다.
- 테스트: `Computed_column_that_overflows_on_unchanged_row_does_not_break_diff`.

### 확인한 사실
1. REVERT 뒤 같은 연결·트랜잭션에서 샌드박스의 미커밋 변경과 데이터베이스 스냅샷을 3부분 이름 질의 하나로 함께 읽을 수 있다. 결과는 독립 확인과 일치했다.
2. AW 전 테이블 비교는 2초 안에 끝난다. 열린 트랜잭션 안과 밖의 차이는 작다.
3. AW의 DB DDL 트리거 `ddlDatabaseTriggerLog`는 샌드박스에서 트랜잭션 밖에 실행한 CREATE USER와 GRANT를 `dbo.DatabaseLog`에 자동 커밋으로 남긴다(행 1359 CREATE_USER, 1360 GRANT_DATABASE). 이 행은 스냅샷에 없으므로 최종 차이에 INSERTED로 나오고, 롤백 뒤에도 남는다.

### 추정·미확인
- 시간은 컨테이너 1대(메모리 4GB)에서 다른 테스트와 함께 돌 때 잰 값이다. 더 큰 DB에서 시간이 행 수에 비례하는지는 재지 않았다(추정).
- 동시 세션이 샌드박스에 쓸 때의 잠금 영향은 재지 않았다. 미리보기 잠금이 있으면 해당이 없을 것으로 추정한다.
- 대량 변경(D8 KEYS)일 때 C12의 결과 크기와 시간은 재지 않았다.

## 증거 범위

DB 실측(로컬 컨테이너 dbripple-sql, SQL Server 17.0.5035.5 CU10 Developer). 운영 환경이 아니다. 로그는 `logs/week2/sp7-aw-netdiff.log`다.

## 설계 반영

- §13 SP7과 R5를 "통과"로, §5.3의 "SP7로 확인한다"를 확인됨으로 바꾼다. 대체 경로(재조회와 KEYSET 비교)는 쓰지 않는다.
- §5.2 실패 뒤 전체 비교의 시간 근거: AW 기준 약 1.5초.
- 실행 사용자 생성: DB DDL 트리거가 있는 DB(AW)에서는 사용자를 만드는 DDL이 사용자 테이블에 확정 행을 남긴다. 그래서 기준선 구축 중 C9 설치 전에 사용자를 만든다(docs/week2-plan.md §9 Q12).
