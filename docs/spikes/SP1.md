# SP1 — 전제 H1~H5 (트리거 깊이·순서)

- **목적:** §7.1 H1~H5가 SQL Server 2025에서 성립하는지 감사 헤더의 `nestLevel`·`captureSeq`로 확인한다.
- **결과:** 미실행(2026-11-06 Docker 미설치)
- **증거 범위:** DB(SpikeLab_SP1 미니 스키마, AW_Sandbox 1건 DELETE, 모두 ROLLBACK). 오늘은 정적 작성만.

## 방법

| 순서 | 스크립트 | DB |
|---|---|---|
| 1 | `spikes/sql/sp1_00_create_db.sql` | master (SpikeLab_SP1 생성, RECURSIVE_TRIGGERS OFF) |
| 2 | `spikes/sql/common_audit_store.sql` | `-d SpikeLab_SP1` |
| 3 | `spikes/sql/common_hand_audit.sql` | `-d SpikeLab_SP1` (손 감사 트리거 I/U/D 본문 + 순서 뷰) |
| 4 | `spikes/sql/sp1_10_schema.sql` | CL06·CL07·CL09·CL13 미니 스키마, 사용자 트리거, 감사 트리거 |
| 5 | `spikes/sql/sp1_20_run.sql` | 실험 4개. 각 실험은 BEGIN TRAN → 경계 → DML → 헤더 수집 → ROLLBACK → 판정 표 |
| 6 | `spikes/sql/sp1_30_aw_soh_delete.sql` | AW_Sandbox. 감사 저장소·손 감사 트리거 4개를 트랜잭션 안에서 만들고 ROLLBACK |
| 7 | `spikes/sql/sp1_99_drop.sql` | master |

판정 표의 `verdict`가 `HOLDS`/`BROKEN`을 바로 낸다. 결과 집합 전체를 로그로 저장한다(`sqlcmd -i … -o sp1.log -W -s"|"`).

## 가설별 예상값과 판정 기준

| 실험 | 문장 | 예상(성립) | 깨짐으로 보는 것 |
|---|---|---|---|
| CL06 | `DELETE cl06.P WHERE Id=1` (C, G CASCADE, C의 사용자 트리거가 Log INSERT) | **H2:** P·C·G 헤더 `nestLevel=1`, Log `nestLevel=2`. **H1:** seq(Log) < seq(C). 참고: G<C<P면 "연쇄의 반대 순서" 관찰 | 연쇄 대상(C, G)의 깊이 ≠ 1이거나 Log ≠ 2 → H2 깨짐. Log가 C 감사 뒤 → H1 깨짐 |
| CL07 | `INSERT cl07.A VALUES (1),(2)` (A→B,D, B→C) | **H2:** A=1, B=2, D=2, C=3. **H1:** seq(C)<seq(B)<seq(A), seq(D)<seq(A), C는 B와 D 사이에 오지 않는다 | 자식 감사가 부모 감사 뒤에 오거나 C가 B 묶음 밖(B 뒤) → H1 깨짐 |
| CL09 | `DELETE cl09.P WHERE Id=1` (1차 K{40,42}·M{20}, trg_M_D가 Q{30} 삭제 → 2차 K{41}) | **H4:** K 묶음 2개, 같은 키가 두 묶음에 없음. **H5:** Q와 K{41}이 `nestLevel=2`이고 seq < seq(M). **H2:** 1차 K, M, P는 1 | K{41}·Q가 M 감사 뒤(특히 P 뒤 = 문서 문구대로 "모든 1차 트리거 뒤") → H5 깨짐. 같은 K 키 중복 → H4 깨짐 |
| CL13 | `DELETE cl13.T WHERE Id=1`, 3회 반복 (T: 사용자 First·중간·Last, 감사 NONE, TC CASCADE+트리거) | **H3:** 매 회차 TC 그룹(trg_TC_1 Log + TC 감사)과 T 그룹(trg_T_1~3 Log + T 감사)이 섞이지 않음. T 감사 `auditOrder=NONE`. **H1:** TC 감사(Last)가 trg_TC_1 Log 뒤 | 그룹이 섞이면 H3 깨짐. 참고 칸: T 감사가 trg_T_3 Log보다 먼저인 회차 수(>0이면 NONE 표시가 필요한 근거) |
| AW | `DELETE Sales.SalesOrderHeader` 1건(상세·사유 있는 첫 주문) | **H2:** D 헤더(SOH, SOD, SOHSR) 모두 `nestLevel=1`. **H1:** iduSalesOrderDetail이 일으킨 SOH UPDATE 헤더(깊이 2, 0행 예상)가 SOH D 감사보다 먼저 | 위와 같음. 참고: SOH U 0행 발동이 기록되는지(A3), 첫 결과 집합으로 FK가 실제 CASCADE인지 먼저 본다 |

## 설계 반영 규칙(§7.1 '깨지면')

| 전제 | 깨지면 |
|---|---|
| H1 | 그 테이블의 트리거 문맥 자식은 `ambiguous` 이하. |
| H2 | FK 판정은 깊이와 상관없이 키 일치로 한다. 같은 문맥에 그 테이블을 쓰는 트리거가 있으면 `inferred`로 낮춘다. |
| H3 | 괄호 추론을 쓰지 않고 정적 쓰기 대상으로만 후보를 정한다(최대 `ambiguous`). |
| H4 | 문서 규칙(오류 1785). 2차 체인은 다른 루트로 같은 테이블에 닿을 수 있다 → CL09 결과로 루트 후보 수를 확인. |
| H5 | 2차 연쇄 묶음은 FK 키 일치로만 부모를 찾고, 확신도 `inferred` 이하, 순서 `observed_only`. |

## 주의

- AW 스크립트는 트랜잭션 동안 Sales 세 테이블에 스키마 잠금(Sch-M)을 잡는다. 샌드박스 전용.
- 손 감사 트리거(미니)는 `dbo.spike_InstallAudit`가 고정 본문으로 만든다. 본문 모양은 backend-2 생성기와 같다(다른 점: tableObjectId를 `OBJECT_ID(N'[s].[t]')`로 계산).
- CL13 순서는 보장되지 않는 영역이라 3회 반복값만 기록한다(일반화하지 않는다).
