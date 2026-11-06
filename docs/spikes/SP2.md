# SP2 — 관찰자 효과

- **목적:** 감사 장치가 사용자 문장의 결과·행 수·IDENTITY를 바꾸지 않는지, 0행 발동이 기록되는지 확인한다(A3, A6, A9, A13).
- **결과:** 미실행(2026-11-06 Docker 미설치)
- **증거 범위:** DB(SpikeLab_SP2, ROLLBACK) + 드라이버(System.Data.SqlClient). (b)는 2주차 하네스 범위.

## 방법

1. `spikes/sql/sp2_10_observer.sql` 첫 배치 (a): 읽기 전용. AW_Sandbox·pubs·CascadeLab 각각에서 실행해 테이블·연산별 First/Last 점유와 `auditOrder`(LAST 가능/NONE)를 남긴다.
2. 같은 파일 1부(master) → `common_audit_store.sql`, `common_hand_audit.sql`(-d SpikeLab_SP2) → `-- PART 2` 이후: (c)(d)(e)를 한 트랜잭션에서 실행하고 ROLLBACK.
3. `spikes/sql/sp2c_rowcount.ps1`: `DBRIPPLE_SQL_CONNECTION` 환경 변수로 연결(비밀번호 출력 없음), ExecuteNonQuery 반환값 비교.
4. `spikes/sql/sp2_99_drop.sql`.

## 예상과 판정

| 항목 | 성립(예상) | 깨짐 → §13 '실패하면' |
|---|---|---|
| (a) | 사용자 Last가 없는 테이블·연산은 `LAST 가능`. 점유된 곳은 목록으로 남긴다 | `NONE` 목록은 매니페스트에 `auditOrder=NONE` 표시(H1 '깨지면' 규칙 대상) |
| (c) SQL | `@@ROWCOUNT`, `ROWCOUNT_BIG()`이 감사 유무와 관계없이 3 = 3 (`SAME`) | `DIFF` → 해당 테이블에 "관찰자 효과 가능" 표시, 필요하면 제외 |
| (c) 드라이버 | Plain 3 rows = Audited 3 rows = 3 | 다르면 위와 같음. `Par cascade` 값은 기록만 |
| (d) 직접 0행 | 헤더 1개(insertedCount 0) `RECORDED`(A3: 직접 대상은 0행도 발동) | `NOT_RECORDED`면 A3과 다름 → 설계의 `zeroRows` 노드 근거 재검토 |
| (d) 연쇄 0행 | 문서에 없음. 결과(`RECORDED`/`NOT_RECORDED`)를 그대로 기록 | 판정 아님. 결과를 §6.3 `zeroRows` 규칙에 반영 |
| (e) | 호출자 @@IDENTITY: plain 1, audited 1000. 사용자 트리거가 읽은 @@IDENTITY·SCOPE_IDENTITY()도 같은 값(감사가 Last이고 IDENTITY를 쓰지 않음) | 값이 바뀌면 관찰자 효과 → 해당 테이블 표시·제외 |

## 미완

- (a)를 AW·pubs·CascadeLab에서 돌린 결과 표(실행 단계에서 채움). (b) 하네스.
