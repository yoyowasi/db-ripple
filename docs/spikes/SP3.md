# SP3 — 자기 테이블 재갱신(RECURSIVE_TRIGGERS)

- **목적:** 트리거가 자기 테이블을 다시 UPDATE할 때(AW `uSalesOrderHeader`의 RevisionNumber 유형) 감사 트리거가 그 중첩 갱신에 발동하는지, 안 하면 재조회·최종 차이로 잔차가 보이는지 확인한다.
- **결과:** 미실행(2026-11-06 Docker 미설치)
- **증거 범위:** DB(SpikeLab_SP3 미니, ROLLBACK). AW_Sandbox 판은 **미완**(스크립트 없음).

## 방법

`spikes/sql/sp3_10_mini.sql` — 1부(master: DB 생성) → `common_audit_store.sql`, `common_hand_audit.sql`(-d SpikeLab_SP3) → 2부(`-- PART 2` 이후). 열 대응: Ref=Status, Ref2=RevisionNumber, Src=Comment. 사용자 트리거 `dbo.uSOH`는 AW와 같은 꼴(Status를 안 바꾸면 같은 행 Rev+1)이고, ON에서 무한 재귀를 막으려고 `NOT UPDATE(Ref2)`만 더했다. `UPDATE dbo.SOH SET Src='sp3' WHERE Id=1`을 OFF와 ON에서 한 번씩 실행한다. 비교 기준은 실행 전 복사본(`@before`)이다. 스냅샷(`AW_Sandbox_base`) 비교는 AW 판에서 한다.

## 판정(`verdict` 열)

| 값 | 뜻 | 예상 |
|---|---|---|
| `CAPTURED` | 중첩 갱신의 감사 헤더(nestLevel ≥ 2)가 있다 | ON에서 예상. OFF에서 나오면 "다른 트리거는 발동"이 확인된 것 |
| `RESIDUAL_BY_REREAD` | 중첩 헤더가 없고, 재조회 Rev ≠ 마지막 감사 after Rev | OFF에서 가장 가능성 높음(감사 Last가 원 문장의 inserted만 봄) |
| `NETDIFF_ONLY` | 재조회 대상(감사 키) 밖 행이 바뀜 | 이 미니에서는 나오면 안 됨 |
| `MISSED` | 어느 쪽으로도 안 보임 | 실패 |
| `ERROR` | 오류(ON에서 가드 없는 트리거면 217 예상) | — |

**통과:** OFF에서 `CAPTURED` 또는 `RESIDUAL_BY_REREAD`. **깨짐:** `MISSED` 또는 `NETDIFF_ONLY`.

## 설계 반영(§13 실패하면)

- `NETDIFF_ONLY`/`MISSED`: 재조회 범위를 테이블 전체로 넓힌다.
- `RESIDUAL_BY_REREAD`: §7.2 6단계대로 RowChange.after를 `REREAD`로 고치고 Residual `SAME_TABLE_TRIGGER`로 귀속(현재 설계 유지).
- `CAPTURED`(OFF): 중첩 묶음이 생기므로 깊이 2 같은 테이블 묶음의 부모를 그 테이블 트리거로 귀속하는 사례를 CL에 추가.

## 미완

- AW_Sandbox 판(`Sales.SalesOrderHeader` Comment UPDATE → uSalesOrderHeader의 RevisionNumber, 스냅샷 대비 최종 차이). SP1 AW 스크립트의 `dbr_audit_SalesOrderHeader_U`를 그대로 쓰면 된다.
