# SP3 — 자기 테이블 재갱신(RECURSIVE_TRIGGERS)

- **목적:** 트리거가 자기 테이블을 다시 UPDATE할 때(AW `uSalesOrderHeader`의 RevisionNumber 유형) 감사 트리거가 그 중첩 갱신에 발동하는지, 안 하면 재조회·최종 차이로 잔차가 보이는지 확인한다.
- **결과:** **통과, `CAPTURED`**(미니 OFF·ON, AW OFF 모두). RECURSIVE_TRIGGERS OFF에서도 감사 트리거(Last)는 같은 테이블 트리거의 중첩 갱신에 발동해 nestLevel 2 묶음을 남긴다. 바깥 문장 묶음의 inserted에는 중첩 갱신 전 값이 담기고, 그 묶음의 captureSeq가 더 크다.
- **증거 범위:** DB 실측(컨테이너 dbripple-sql, 2026-11-10). SQL Server 2025 RTM-CU10(17.0.5035.5), Linux.
  - SpikeLab_SP3 미니: 실험마다 ROLLBACK, 끝에 DB 삭제.
  - AW_SpikeA: SOH 1건 UPDATE 후 ROLLBACK. 스냅샷과 감사 객체는 끝에 지웠다.

## 방법

| 순서 | 스크립트 | DB | 로그(`spikes/logs/`) |
|---|---|---|---|
| 1 | `spikes/sql/sp3_10_mini.sql -Part 1` | master (SpikeLab_SP3 생성) | `sp3-10-mini-part1.log` |
| 2 | `spikes/sql/common_audit_store.sql` | SpikeLab_SP3 | `sp3-11-audit-store.log` |
| 3 | `spikes/sql/common_hand_audit.sql` | SpikeLab_SP3 | `sp3-12-hand-audit.log` |
| 4 | `spikes/sql/sp3_10_mini.sql -Part 2` | master → SpikeLab_SP3 (OFF·ON 실행 후 DB 삭제) | `sp3-10-mini-part2.log` (QI OFF 1차: `sp3-qioff-10-mini-part2.log`) |
| 5 | `spikes/sql/common_audit_store.sql` | AW_SpikeA (감사 저장소, 자동 커밋) | `sp3-19-aw-audit-store.log` |
| 6 | `spikes/sql/sp3_20_aw.sql` (새로 작성) | AW_SpikeA: 1부 감사 트리거 → 스냅샷 → 실험 → ROLLBACK, 2부(`-- PART 2`) 정리 | `sp3-20-aw.log` |

**미니(`sp3_10_mini.sql`)**

- 열 대응: Ref=Status, Ref2=RevisionNumber, Src=Comment.
- 사용자 트리거 `dbo.uSOH`는 AW와 같은 꼴이다(Status를 안 바꾸면 같은 행 Rev+1). ON에서 무한 재귀를 막으려고 `NOT UPDATE(Ref2)`만 더했다.
- `UPDATE dbo.SOH SET Src='sp3' WHERE Id=1`을 OFF와 ON에서 한 번씩 실행한다. 비교 기준은 실행 전 복사본(`@before`)이다.

**AW 판(`sp3_20_aw.sql`)**

- RECURSIVE_TRIGGERS는 AW 기본값(OFF) 그대로 둔다.
- `Sales.dbr_audit_SalesOrderHeader_U`를 만든다. 본문은 sp1_30과 같고, 사용자 Last가 없어서 Last로 둔다.
- 스냅샷 `AW_SpikeA_snap`을 만든다. 데이터 파일마다 경로를 준다(`AdventureWorks` → `/var/opt/mssql/data/AW_SpikeA_snap_AdventureWorks.ss`).
- 같은 연결·같은 트랜잭션에서 아래를 차례로 한 뒤 ROLLBACK한다.
  - `UPDATE Sales.SalesOrderHeader SET Comment = N'sp3' WHERE SalesOrderID = 43659`(가장 작은 ID)
  - (1) 감사 묶음별 deleted/inserted 이미지의 RevisionNumber·Comment
  - (2) 재조회
  - (3) 스냅샷 값, 테이블 전체 EXCEPT 양방향
- 판정식은 미니와 같다. `NETDIFF_ONLY`는 "감사 키 밖 행의 최종 차이 > 0"으로 계산했다.
- 2부에서 스냅샷·감사 트리거·감사 저장소를 지웠다.

**실행 메모**

- 모든 단계를 `run-sql.ps1`(sqlcmd `-b`)로 실행했다. 로그 첫 줄은 모두 `exit=0`이다. 의도한 오류를 관찰하는 단계가 없어 `-ContinueOnError`는 쓰지 않았다.
- `sp3_20_aw.sql`은 새로 썼다. 첫 배치에 `SET QUOTED_IDENTIFIER ON`을 넣었다. SOH 계산 열 인덱스 때문에 SP1 1차 실행이 1934로 실패했기 때문이다.
- AW 판은 2회 실행했다. 1회차 뒤 중첩 묶음의 before 이미지를 보려고 `beforeComment` 열만 더했다. 판정식은 그대로이고, 1회차도 같은 값의 CAPTURED였다. 1회차 로그는 2회차 로그로 덮어썼다.
- QI ON 재실행(09:12): run-sql.ps1에 `-I`가 들어간 뒤 미니(1~4단계)를 다시 돌렸다.
  - 확인 쿼리를 `sp3_10_mini.sql` 2부에 넣었다. `sys.sql_modules`에서 `uses_quoted_identifier = 0`인 모듈은 0행이다(`sp3-10-mini-part2.log` 5~11행: 5개 중 0).
  - 결과는 OFF 1차(`sp3-qioff-10-mini-part2.log`)와 같다.
- AW 판(5~6단계)은 다시 돌리지 않았다. 처음부터 스크립트의 `SET QUOTED_IDENTIFIER ON` 아래에서 실행됐고, 만든 트리거도 ON이다(`sp3-20-aw.log` 5~6행 sessionQuotedIdentifier 1, 20~21행 quotedIdentOn 1). 5단계 저장소는 OFF 세션에서 만들었지만 모듈이 없다(테이블·시퀀스뿐).

## 판정(`verdict` 열)

| 값 | 뜻 | 예상 |
|---|---|---|
| `CAPTURED` | 중첩 갱신의 감사 헤더(nestLevel ≥ 2)가 있다 | ON에서 예상. OFF에서 나오면 "다른 트리거는 발동"이 확인된 것 |
| `RESIDUAL_BY_REREAD` | 중첩 헤더가 없고, 재조회 Rev ≠ 마지막 감사 after Rev | OFF에서 가장 가능성 높음(감사 Last가 원 문장의 inserted만 봄) |
| `NETDIFF_ONLY` | 재조회 대상(감사 키) 밖 행이 바뀜 | 이 미니에서는 나오면 안 됨 |
| `MISSED` | 어느 쪽으로도 안 보임 | 실패 |
| `ERROR` | 오류(ON에서 가드 없는 트리거면 217 예상) | — |

**통과:** OFF에서 `CAPTURED` 또는 `RESIDUAL_BY_REREAD`. **깨짐:** `MISSED` 또는 `NETDIFF_ONLY`.

## 결과

### 판정

| 판 | RECURSIVE_TRIGGERS | 감사 묶음(captureSeq: 깊이, Rev before→after) | 재조회 Rev | 최종 차이 | verdict | 근거 |
|---|---|---|---|---|---|---|
| 미니 | OFF (0) | 1: 깊이 2, after 1 / 2: 깊이 1, after 0 | 1 | 1행 | **CAPTURED** | `sp3-10-mini-part2.log` 16~17, 20행 |
| 미니 | ON (1) | 3: 깊이 2, after 1 / 4: 깊이 1, after 0 | 1 | 1행 | **CAPTURED** | 같은 로그 23~24, 27행 |
| AW SalesOrderID 43659 | OFF (0) | 1: 깊이 2, 10→11 / 2: 깊이 1, 10→10 | 11 | 스냅샷 10 → 11, 1행(다른 키 0행) | **CAPTURED** | `sp3-20-aw.log` 5~6, 33~44행 |

판정 기준(통과: OFF에서 `CAPTURED` 또는 `RESIDUAL_BY_REREAD`)에 따라 **통과**다.

### 확인한 사실

- **OFF에서도 감사는 중첩 갱신을 잡는다.** 감사 트리거는 같은 테이블 사용자 트리거의 중첩 UPDATE에 발동했다(깊이 2 묶음).
  - RECURSIVE_TRIGGERS OFF가 막은 것은 같은 트리거의 재발동뿐이었다.
  - 근거: 재귀 가드가 없는 uSalesOrderHeader(`sp3-20-aw.log` 9~15행)가 Rev를 한 번만 올렸고(10→11), 오류가 없었다(33행 errorNumber NULL).
- **바깥 묶음의 inserted는 중첩 갱신 전 값이다.** 바깥 문장 묶음(깊이 1)의 inserted Rev는 AW 10(37행), 미니 0(17행)이다. 재조회 값(11, 1)은 깊이 2 묶음의 after와 같다.
- **captureSeq 순서는 같은 행의 실제 변경 순서와 반대다.**
  - AW 깊이 2 묶음(seq 1)의 before Comment가 이미 `sp3`이다(36행). 바깥 문장(seq 2)이 먼저 바꾸고 중첩 갱신이 뒤에 바꿨는데, Last 후위 기록이라 중첩 쪽이 먼저 기록됐다.
  - 그래서 "captureSeq가 가장 큰 묶음의 after가 최종값"이라는 가정은 틀린다(44행 lastAuditAfterRev 10 ≠ rereadRev 11).
- **다른 스파이크의 같은 관측(SP10 AW 판):** spike-b가 실행했고, 로그는 내가 읽어 확인했다.
  - AW_SpikeB(OFF)에서 SOD 1행 UPDATE → iduSalesOrderDetail의 SOH UPDATE(깊이 2) → uSalesOrderHeader의 RevisionNumber 자기 갱신이 깊이 3 묶음으로 잡혔다(`sp10-20-aw-part2.log` 70행 `nest=3,2`).
  - 기록 순서도 SOH U(3)가 SOH U(2)보다 먼저다(74행). RevisionNumber는 10→11로 한 번만 올랐다(86행).
- **스냅샷 비교:** 같은 연결·같은 트랜잭션에서 스냅샷 DB를 읽었다(33행 tranCountAtSnapshotRead 1). 최종 차이는 대상 1행뿐이다(44행: EXCEPT 양방향 1행, 다른 키 0행). ROLLBACK 뒤 원래 값으로 돌아왔다(47행).
- **미니 ON·OFF:** 감사 결과가 같았다. 미니 uSOH는 `NOT UPDATE(Ref2)` 가드 때문에 ON에서 재발동해도 다시 갱신하지 않는다.
- **환경:** AW_Origin과 AW_SpikeA 모두 RECURSIVE_TRIGGERS OFF, nested triggers 1이다(5~6행).
- **정리:** 스냅샷·감사 스키마·감사 트리거가 남지 않았다. SOH 트리거 순서도 원래대로(uSalesOrderHeader만, None)다(58, 61행).

### 추정

- 같은 테이블에 Last 감사 트리거가 있으면, 그 테이블의 다른 트리거가 같은 테이블을 갱신할 때마다 중첩 묶음이 생긴다고 본다. INSERT·DELETE 자기 갱신은 확인하지 않았다.

## 설계 반영(§13 실패하면)

- `NETDIFF_ONLY`/`MISSED`: 재조회 범위를 테이블 전체로 넓힌다.
- `RESIDUAL_BY_REREAD`: §7.2 6단계대로 RowChange.after를 `REREAD`로 고치고 Residual `SAME_TABLE_TRIGGER`로 귀속(현재 설계 유지).
- `CAPTURED`(OFF): 중첩 묶음이 생기므로 깊이 2 같은 테이블 묶음의 부모를 그 테이블 트리거로 귀속하는 사례를 CL에 추가.

## 설계 반영 제안(2026-11-10 결과 기준)

결과가 `CAPTURED`(OFF)이므로 위 규칙의 세 번째 줄을 적용한다. 깊이 2 같은 테이블 묶음의 부모를 그 테이블 트리거로 귀속하는 사례를 CL08a로 둔다(qa-1에 알렸다). 그 밖에 오케스트레이터가 반영할 후보는 아래와 같다.

1. **E14 처리 경로 변경**
   - 대상: §6.2 Residual 예시와 그 설명, §9 E14, D3 단점, R3, §13 SP3 행.
   - "OFF면 감사가 자기 테이블 재갱신을 놓친다"는 전제가 실측과 다르다.
   - uSalesOrderHeader의 RevisionNumber 갱신은 잔차가 아니라 깊이 2 SOH U 묶음으로 설명된다. 트리거 문맥 부모는 바깥 SOH U 묶음이고, 원인 후보는 SOH UPDATE 트리거 가운데 정적 쓰기 대상에 SOH가 있는 uSalesOrderHeader다(`inferred`). §7.2 4단계 규칙만으로 처리된다.
   - E14의 기본 처리는 4단계로 바꾼다. 재조회·최종 차이 잔차(`SAME_TABLE_TRIGGER`)는 감사가 놓친 경우의 안전망으로 남긴다.
2. **§7.2 6단계: 같은 행의 "감사 after"를 고르는 규칙**
   - 같은 (테이블, 키)를 여러 묶음이 바꿨을 때, 재조회와 비교할 "감사 after"는 captureSeq 최댓값 묶음에서 가져오면 안 된다.
   - 괄호 트리의 전위 순서(부모 묶음 → 그 자식들, 형제는 captureSeq 순)에서 마지막 묶음의 after를 쓴다. SP3에서는 깊이 2 묶음 after(11)가 재조회(11)와 같아 잔차가 생기지 않는다.
   - captureSeq 최댓값(바깥 묶음 Rev 10)과 비교하면, 이미 깊이 2 묶음이 설명한 변경을 `SAME_TABLE_TRIGGER` 잔차로 한 번 더 만든다(M1·M2 중복 위험).
   - 재조회 값이 달라 after를 고칠 때도 그 마지막 묶음의 RowChange를 고친다.
3. **§6.2 Firing 설명:** "트리거 문맥 자식의 captureSeq는 항상 부모 묶음보다 작다" 뒤에 한 줄을 덧붙인다. 같은 행을 부모·자식 묶음이 모두 바꾸면 실제 변경 순서는 captureSeq와 반대다(부모 문장 → 자식).
4. **§6.2 AW 예시 갱신:** NetChange `explainedBy`는 `firingIds: [바깥 SOH U, 깊이 2 SOH U]`, `residualId: null`이 된다. Residual r-1 예시는 다른 사례(예: 감사 제외 테이블)로 바꾼다.

## 판정식의 한계 (2026-11-10 독립 검토 반영)

- `CAPTURED` 판정(sp3_10_mini.sql:66, sp3_20_aw.sql:128)은 nestLevel 2 이상 헤더가 있는지만 본다. 값 일치(깊이 2 묶음 after = 재조회 = 최종 차이)는 `sp3-20-aw.log` 44행의 별도 열로 확인했다.
- 통과 기준("잔차로 검출")보다 앞 단계(감사 묶음)에서 잡혀 기준을 넓혀 판정했다. 잔차(안전망) 경로 자체는 실행되지 않았다.

## 미확인

- AW에서 RECURSIVE_TRIGGERS ON은 실행하지 않았다. uSalesOrderHeader에 가드가 없어 오류 217이 예상되고, 그 CATCH의 uspLogError가 트랜잭션 밖에서 dbo.ErrorLog에 커밋하기 때문이다.
- OFF에서 사용자 트리거 자신이 재발동하지 않았다는 직접 관측은 없다.
  - 미니는 가드 때문에 OFF·ON 결과가 같다.
  - 간접 근거는 AW의 Rev +1, 오류 없음이다.
  - qa-1이 CL08a 감사 대조(QI ON 재설치본, `cascadelab/verification/logs/audit-2026-11-10.txt`, 5회)에서도 같은 결론이라고 알려 왔다. 깊이 2 묶음이 깊이 1 묶음보다 먼저였다. 내가 직접 확인하지는 않았다.
- KEYS 모드, 여러 행 UPDATE, Status·SubTotal 갱신 경로(SalesPerson·SalesTerritory 갱신), INSERT/DELETE 자기 갱신은 하지 않았다.

## 남은 변경(AW_SpikeA)

- AW DDL 트리거 ddlDatabaseTriggerLog가 감사 저장소·트리거의 설치와 정리 DDL을 dbo.DatabaseLog에 커밋했다. AW 판 2회 실행으로 26행이 늘었다(927 → 953, 현재 값은 `logs/week1/env-evidence.log`: AW_Origin 927, AW_SpikeA 953).
- 그 밖의 객체·데이터는 원래대로다. 감사 스키마·트리거·스냅샷이 없고, SOH 43659는 Rev 10, Comment NULL이다.
