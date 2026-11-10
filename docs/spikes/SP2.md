# SP2 — 관찰자 효과

- **목적:** 감사 장치가 사용자 문장의 결과·행 수·IDENTITY를 바꾸지 않는지, 0행 발동이 기록되는지 확인한다(A3, A6, A9, A13).
- **결과:** **성립** — 2026-11-10 실행. (a) AW·CascadeLab·CascadeLab_RT에는 사용자 First/Last 점유가 없어 모든 연산이 `LAST 가능`이다. (c) 행 수는 감사 유무와 관계없이 같다. 트리거 안 행 수는 호출자에게도 다음 트리거에도 새지 않았다. (d) 직접 대상 0행은 기록된다. **FK 연쇄 0행은 자식 트리거가 발동하지 않는다.** (e) IDENTITY 값은 바뀌지 않는다. pubs는 미확인(미설치), (b) 하네스는 2주차다.
- **증거 범위:** DB 실측(컨테이너 dbripple-sql, SQL Server 2025 RTM-CU10 17.0.5035.5, 2026-11-10) + 드라이버(System.Data.SqlClient, Windows PowerShell 5.1). SpikeLab_SP2는 끝에서 삭제했다. (a)는 읽기 전용 메타데이터 조회다. CascadeLab은 qa-1의 QUOTED_IDENTIFIER ON 재설치(09:09) 뒤 상태로 쟀다.

## 방법

1. (a) `spikes/sql/sp2_05_trigger_order.sql`(2026-11-10 추가): `sp2_10_observer.sql` 첫 배치와 같은 쿼리에 INSTEAD OF 목록과 RECURSIVE_TRIGGERS를 더했다. `run-sql.ps1`에 '첫 배치만' 옵션이 없어서 떼어 냈다. 읽기 전용이며 다음 DB에서 실행했다: AW_SpikeB, AW_Origin, CascadeLab, CascadeLab_RT. 참고로 backend-3의 샌드박스 AW_Sandbox, CascadeLab_Sandbox, CascadeLab_RT_Sandbox에서도 실행했다(조회만).
2. `spikes/sql/sp2_10_observer.sql` 1부(master) → `common_audit_store.sql`, `common_hand_audit.sql`(-d SpikeLab_SP2) → `-- PART 2` 이후: (c)(d)(e)를 한 트랜잭션에서 실행하고 ROLLBACK한다.
3. `spikes/sql/sp2c_rowcount.ps1`: `. .\scripts\env.ps1`로 `DBRIPPLE_SQL_CONNECTION`을 프로세스 환경에만 싣고(출력 없음) ExecuteNonQuery 반환값을 비교한다. 로그 첫 줄은 run-sql과 같은 형식으로 직접 남겼다.
4. `spikes/sql/sp2_99_drop.sql`.
5. 실행(PowerShell, 저장소 루트). `-ContinueOnError`는 쓰지 않았다. 로그 13개는 모두 첫 줄이 `exit=0`이다.
   ```
   .\scripts\run-sql.ps1 spikes\sql\sp2_05_trigger_order.sql -Database <DB> -Log spikes\logs\sp2-a-<DB>.log
   .\scripts\run-sql.ps1 spikes\sql\sp2_10_observer.sql -Part 1 -Log spikes\logs\sp2-10-part1.log
   .\scripts\run-sql.ps1 spikes\sql\common_audit_store.sql -Database SpikeLab_SP2 -Log spikes\logs\sp2-10-common-audit-store.log
   .\scripts\run-sql.ps1 spikes\sql\common_hand_audit.sql -Database SpikeLab_SP2 -Log spikes\logs\sp2-10-common-hand-audit.log
   .\scripts\run-sql.ps1 spikes\sql\sp2_10_observer.sql -Part 2 -Database SpikeLab_SP2 -Log spikes\logs\sp2-10-part2.log
   . .\scripts\env.ps1; powershell -NoProfile -File spikes\sql\sp2c_rowcount.ps1   # → spikes\logs\sp2-c-rowcount.log
   .\scripts\run-sql.ps1 spikes\sql\sp2_99_drop.sql -Log spikes\logs\sp2-99-drop.log
   ```

**스크립트 수정과 추가(2026-11-10)**

- (c) 보조 추가: 감사 트리거의 마지막 INSERT는 발동 행 수와 같아서, '트리거 행 수가 호출자에게 새는지'를 가르지 못한다. 그래서 감사와 무관한 대조 테이블 3개를 더했다(판정 기준은 그대로).
  - `Last5`: 트리거 마지막 문장이 5행, NOCOUNT ON.
  - `NoCnt`: NOCOUNT 없이 2행을 넣는 트리거.
  - `Seq2`: First 트리거가 5행을 넣은 뒤, Last 트리거가 AW와 같은 모양(`DECLARE` → `SET @Count = @@ROWCOUNT`)으로 시작 시점 값을 읽는다.
  - `sp2c_rowcount.ps1`에도 `Last5`·`NoCnt` 경우를 더했다.
- (d) 대조 추가: 부모 DELETE 헤더(`d_cascade_parent_header`)와 자식 1행 연쇄(`d_cascade_one_row_header`).
- 판정 문구·(a)의 `auditOrder` 리터럴을 `N''`로 바꿨다(로그의 `??` 방지). 끝에 감사 순서와 `uses_quoted_identifier = 0` 모듈 수 확인을 더했다.
- QUOTED_IDENTIFIER: 모든 실행은 `run-sql.ps1 -I` 반영(09:07:58) 뒤에 했다. SpikeLab_SP2 모듈 12개 중 OFF는 0개다(`sp2-10-part2.log:41` `modules 12 0`).

## 예상과 판정

| 항목 | 성립(예상) | 깨짐 → §13 '실패하면' |
|---|---|---|
| (a) | 사용자 Last가 없는 테이블·연산은 `LAST 가능`. 점유된 곳은 목록으로 남긴다 | `NONE` 목록은 매니페스트에 `auditOrder=NONE` 표시(H1 '깨지면' 규칙 대상) |
| (c) SQL | `@@ROWCOUNT`, `ROWCOUNT_BIG()`이 감사 유무와 관계없이 3 = 3 (`SAME`) | `DIFF` → 해당 테이블에 "관찰자 효과 가능" 표시, 필요하면 제외 |
| (c) 드라이버 | Plain 3 rows = Audited 3 rows = 3 | 다르면 위와 같음. `Par cascade` 값은 기록만 |
| (d) 직접 0행 | 헤더 1개(insertedCount 0) `RECORDED`(A3: 직접 대상은 0행도 발동) | `NOT_RECORDED`면 A3과 다름 → 설계의 `zeroRows` 노드 근거 재검토 |
| (d) 연쇄 0행 | 문서에 없음. 결과(`RECORDED`/`NOT_RECORDED`)를 그대로 기록 | 판정 아님. 결과를 §6.3 `zeroRows` 규칙에 반영 |
| (e) | 호출자 @@IDENTITY: plain 1, audited 1000. 사용자 트리거가 읽은 @@IDENTITY·SCOPE_IDENTITY()도 같은 값(감사가 Last이고 IDENTITY를 쓰지 않음) | 값이 바뀌면 관찰자 효과 → 해당 테이블 표시·제외 |

## 결과

### 판정

| 항목 | 판정 | 근거(로그 줄) |
|---|---|---|
| (a) AW | **성립**: AFTER 트리거 슬롯 11개 모두 First·Last 점유가 없어 `LAST 가능`. `NONE` 0개 | `sp2-a-AW_SpikeB.log:13` `AW_SpikeB Sales.SalesOrderDetail UPDATE NULL NULL 1 LAST 가능` 외 10행(`:16` `(11 rows affected)`). AW_Origin도 같다(`sp2-a-AW_Origin.log`) |
| (a) CascadeLab | **성립**: 슬롯 5개 모두 `LAST 가능`, `NONE` 0개 | `sp2-a-CascadeLab.log:8` `CascadeLab cl08.Acct UPDATE NULL NULL 1 LAST 가능`(`:10` `(5 rows affected)`) |
| (a) CascadeLab_RT | **성립**: 슬롯 1개 `LAST 가능`(RECURSIVE_TRIGGERS 1) | `sp2-a-CascadeLab_RT.log:4` `CascadeLab_RT cl08.Acct UPDATE NULL NULL 1 LAST 가능` |
| (a) pubs | **판정 불가**(pubs 미설치) | — |
| (c) SQL | **성립**(SAME) | `sp2-10-part2.log:19` `c_@@ROWCOUNT 3 3 SAME`, `:20` `c_ROWCOUNT_BIG 3 3 SAME` |
| (c) 드라이버 | **성립**(Plain 3 = Audited 3) | `sp2-c-rowcount.log:5` `Plain 3 rows … 3`, `:6` `Audited 3 rows … 3`. 기록: `:8` `Par cascade … 1` |
| (d) 직접 0행 | **성립**(RECORDED) | `sp2-10-part2.log:26` `d_direct_zero_row_header NULL 1 RECORDED` |
| (d) 연쇄 0행 | **기록: NOT_RECORDED**(자식 0행 FK 연쇄는 자식 AFTER 트리거를 발동하지 않음) | `:27` `d_cascade_zero_row_header NULL 0 NOT_RECORDED`. 대조: `:28` `d_cascade_parent_header NULL 1 RECORDED`, `:29` `d_cascade_one_row_header NULL 1 RECORDED` |
| (e) | **성립** | `:30` `e_caller_@@IDENTITY 1 1000`, `:31` `e_trigger_@@IDENTITY 1 1000`, `:32` `e_trigger_SCOPE_IDENTITY NULL NULL`. 트리거 안 SCOPE_IDENTITY()는 예상(1/1000)과 달리 감사 유무와 무관하게 NULL이었다(트리거는 별도 범위). 감사 유무 차이를 가르는 측정은 호출자·트리거의 @@IDENTITY다(2026-11-10 독립 검토 반영) |

§13 SP2 통과 기준("결과 동일(E_obs = 0), 행 수 동일") → (a)(c)(d)(e) 범위에서 **성립**. (b)는 2주차 하네스다. 1주차 수동 비교 1건은 아래 확인한 사실 7에 있다.

### 확인한 사실

1. (a) AW(AW_SpikeB = AW_Origin)의 AFTER 트리거 슬롯은 11개다(Person.Person I/U, Production.WorkOrder I/U, Purchasing.PurchaseOrderDetail I/U, Purchasing.PurchaseOrderHeader U, Sales.SalesOrderDetail I/U/D, Sales.SalesOrderHeader U). 모두 First·Last가 비어 있다. INSTEAD OF는 HumanResources.Employee `dEmployee`, Purchasing.Vendor `dVendor`(둘 다 DELETE) 2개로 순서 지정 대상이 아니다(`sp2-a-AW_SpikeB.log:19` 외).
2. (a) CascadeLab의 사용자 트리거 슬롯(cl06.Line D, cl07.A U, cl07.B U, cl07.C I, cl08.Acct U)과 CascadeLab_RT(cl08.Acct U)에도 First·Last 점유가 없다. qa-1의 재설치 보고(is_first=0, is_last=0)와 같다.
3. (a) 참고: backend-3의 샌드박스에는 감사 트리거가 실제로 모든 슬롯의 Last로 설치돼 있다. AW_Sandbox 213개, CascadeLab_Sandbox 75개, CascadeLab_RT_Sandbox 3개다(예: `sp2-a-AW_Sandbox.log:180` `Sales.SalesOrderDetail UPDATE NULL dbr_audit_SalesOrderDetail_U 2 LAST 가능`). First 점유와 사용자 Last는 0개다.
4. (c) 트리거 안 행 수는 호출자에게 새지 않는다.
   - 마지막 문장이 5행인 트리거(NOCOUNT ON) 뒤에도 호출자의 `@@ROWCOUNT`·`ROWCOUNT_BIG()`은 3이다(`sp2-10-part2.log:21`·`:22`). NOCOUNT 없는 트리거 뒤에도 3이다(`:23`·`:24`).
   - First 트리거가 5행을 넣은 뒤 Last 트리거가 시작할 때 읽은 `@@ROWCOUNT`도 3이다(`:25` `c_aux_between_triggers_@@ROWCOUNT 3 3 SAME`).
5. (c) 드라이버 `RecordsAffected`에 대해:
   - NOCOUNT 없는 트리거의 행은 **더해진다**(`sp2-c-rowcount.log:10` `NoCnt 3 rows (trigger without NOCOUNT, +2 rows) 5`). NOCOUNT ON 트리거는 3이다(`:9`).
   - FK 연쇄로 지운 자식 행은 더해지지 않는다(`:8` `Par cascade 1`). 0행 UPDATE는 0이다(`:7`).
6. (d) 직접 대상 0행 UPDATE는 감사 헤더 1개(insertedCount 0)를 남긴다. 자식이 없는 부모 DELETE의 FK 연쇄(ON DELETE CASCADE)는 자식의 AFTER 트리거를 **발동하지 않는다**. 부모 헤더는 남는다. 자식 1행 연쇄는 발동한다.
7. (b) 1주차 수동 비교 1건(SP10 AW 판 데이터): AW_SpikeB에서 `Sales.SalesOrderDetail` 1행 UPDATE를 비교했다. 감사 없음(R1 dbo)과 감사 있음(R3 dbo)의 결과가 같다.
   - 같은 항목: `@@ROWCOUNT` 1, 오류 없음, 바뀐 테이블 4개와 그 지문, 값(OrderQty 2, SubTotal 22590.61, RevisionNumber 11, TotalPurchaseYTD −6053.67).
   - 근거: `sp10-20-aw-part1.log:28`~`:46`과 `sp10-20-aw-part2.log:76`~`:94`. 예: 두 로그 모두 `fp:[Sales].[SalesOrderHeader] … 31465/126638580/-`. TransactionHistory는 IDENTITY·GETDATE() 열 때문에 행 수와 변경 여부만 비교했다.
8. (e) 감사 저장소에 IDENTITY가 없으므로 호출자와 사용자 트리거의 `@@IDENTITY`(1/1000)와 트리거 안 `SCOPE_IDENTITY()`(NULL/NULL)가 감사 유무로 바뀌지 않는다.
9. SpikeLab_SP2의 감사 트리거 4개는 모두 Last로 지정됐다(`sp2-10-part2.log:35`~`:38`).

### 추정

- 4의 결과로 보아, D3 규칙 2의 "마지막 문장이 발동 행 수와 같은 INSERT"는 이 버전에서는 `@@ROWCOUNT` 누출을 막는 데 필요하지 않다(방어용). 표본은 SpikeLab_SP2의 단순 트리거다.
- 사용자 Last가 이미 있어 감사가 `NONE`(순서 미지정)이 되는 경우에도, 4의 '트리거 사이' 결과로 보아 감사 트리거의 행 수가 뒤 트리거의 시작 `@@ROWCOUNT`에 보이지 않을 것이다. 실제 NONE 사례는 데이터에 없어 실측하지 않았다.

### 설계 반영 제안 (오케스트레이터가 architecture.md에 반영)

1. §6.3 `zeroRows`(A3): "FK 연쇄 대상에서 연쇄 행이 0개면 자식 AFTER 트리거는 발동하지 않는다(SP2(d) 실측)." 연쇄 0행 노드는 감사 기록으로 만들 수 없으므로, `zeroRows` 노드는 직접 대상과 트리거 본문 안 문장(SP10 AW에서 SalesPerson·SalesTerritory U 0행 발동 기록 확인)에만 둔다. 연쇄 0행을 보이려면 정적 FK 메타데이터로 '발동 없음' 간선을 따로 그린다.
2. D3 규칙 2·A9: 트리거 안 행 수는 호출자의 `@@ROWCOUNT`·`ROWCOUNT_BIG()`과 다음 트리거의 시작 `@@ROWCOUNT`에 새지 않았다(SP2(c) 보조). '마지막 문장 행 수' 규칙은 방어용으로 남기고 근거를 실측으로 바꾼다. First 금지의 근거 중 '@@ROWCOUNT 영향 미문서'는 이 실측으로 약해진다. 순서(H1·H3, SP1) 근거는 그대로다.
3. D3 규칙 2 `SET NOCOUNT ON`: 필수다. 없으면 트리거 행이 드라이버 `RecordsAffected`에 더해진다(감사와 무관한 대조 트리거 NoCnt로 3 → 5. 감사 트리거 자체를 NOCOUNT 없이 잰 것은 아니다). 영향 행 수를 보여 줄 때는 드라이버 합계보다 문장 단위 `@@ROWCOUNT`가 정확하다. 사용자 트리거가 NOCOUNT 없이 행을 바꾸면 드라이버 합계는 원본에서도 같이 커진다(관찰자 효과는 아님).
4. 매니페스트 `auditOrder`: AW·CascadeLab·CascadeLab_RT는 모든 연산이 `LAST`다. `NONE` 경로는 실데이터에 없다. 그래서 CascadeLab에 사용자 Last 트리거 사례를 하나 두는 것을 제안한다(qa-1 범위).
5. §6.2·D3 규칙 3(감사 저장소 IDENTITY 금지, SEQUENCE 사용): (e)로 확인했으니 그대로 유지한다.

### 미확인

- pubs: 미설치라 (a)를 못 쟀다.
- (b) 하네스(2주차). 1주차 수동 비교는 AW 문장 1건뿐이다.
- `NONE`(사용자 Last 점유) 실사례에서의 관찰자 효과. MERGE·INSTEAD OF·BULK INSERT(FIRE_TRIGGERS)의 0행 발동.
- 제품 드라이버(Microsoft.Data.SqlClient)의 `RecordsAffected`: 이번에는 System.Data.SqlClient(.NET Framework)로 쟀다.
