# 평가 보고서

> 상태: 2026-11-12 첫 전체 실행 반영(2026-11-13: EXEC 사례 CL21~CL25와 정답 형식 v0.3을 더했다. **이 사례들은 평가를 아직 실행하지 않았다**, §2.1). **CascadeLab CL01~CL20(변형 30개) M1~M4를 제품·B0·B2로 20회 반복해 쟀고(`logs/eval/eval-20261112-143835`), M5를 N = 1~10만 행으로 쟀다(`logs/eval-m5/m5-20261112-145013`, 10만 행은 `m5-20261112-145801`).** 아직 측정 전: SP9 브라우저 렌더 시간, 누적 구성 등(§6.3). 수치는 CSV와 로그에서만 옮기고 손으로 만든 값은 넣지 않는다(설계 §10.6).
> 설계: [architecture.md](architecture.md) v1.6 §10 · 결정: [week3-plan.md](week3-plan.md) · 지표 열 정의: `eval/DbRipple.Eval/MetricsCsv.cs` 주석 · 자동 생성 보고서: `report` 명령이 실행 폴더에 쓰는 `report.md`(`eval/DbRipple.Eval/ReportBuilder.cs`)

이 문서는 사람이 읽는 보고서다. 실행마다 생기는 `report.md`는 CSV 표를 그대로 옮긴 부록이고, 이 문서의 §6 결과 표는 그 `report.md`와 CSV에서 옮겨 채운다.

## 1. 주장과 지표

| 주장 | 지표 | 상태 |
|---|---|---|
| 미리보기가 실제 실행과 같다 | M1 일치율 | CL01~CL20 측정함(2026-11-12, §6.1) |
| 연쇄·트리거 변경까지 찾는다 | M2 탐지 | CL01~CL20 측정함(2026-11-12, §6.1) |
| 원인과 순서를 맞게 귀속한다(신규성의 근거) | M3 귀속 정확도, M4 순서 안정성 | CL01~CL20 측정함(2026-11-12, §6.1) |
| 쓸 만한 시간 안에 끝난다 | M5 응답 시간 | N = 1~10만 측정함(2026-11-12, §6.2). 브라우저 렌더(SP9)는 측정 전 |

신규성 주장은 M3·M4에 둔다. 가장 강한 반론인 "사본 + 최종 차이"(B2)를 같은 사례·같은 지표로 비교한다(§5).

## 2. 데이터셋

### 2.1 CascadeLab (합성 정답 DB, 저장소에 포함)

공개 샘플에 없는 연쇄 패턴을 담은 합성 DB다. 사례마다 손 정답(변경 키 집합, 간선, 기대 오류)을 JSON으로 썼다. 형식은 answer-format v0.3([cascadelab/answer-format.md](../cascadelab/answer-format.md))다(2026-11-13, v0.2에 필드만 더했고 v0.2 파일은 그대로 v0.3 파일이다). 사례 파일은 변형을 포함해 38개다(`cascadelab/cases/`). 2026-11-12 평가는 그 가운데 CL01~CL20의 30개를 쟀고, 나머지 8개(CL21~CL25)는 2026-11-13에 작성했으며 실행 전이다. 사례별 문장과 기대값은 [cascadelab/README.md](../cascadelab/README.md) "사례 요약"에 있다.

| 패턴 | 사례 | 확인 대상 | 손 정답 대 실DB 대조 |
|---|---|---|---|
| CL01 | CL01 | ON DELETE CASCADE 4단, FK 귀속 `certain` | 2026-11-10 일치 |
| CL02 | CL02 | SET NULL | 2026-11-10 일치 |
| CL03 | CL03, CL03b | SET DEFAULT, 기본값이 부모에 없음(547) | 2026-11-10 일치 |
| CL04 | CL04, CL04b | ON UPDATE CASCADE(PK 변경, 다중·단일 행) | 2026-11-10 일치 |
| CL05 | CL05 | 한 부모에서 갈라지는 체인 2개 | 2026-11-10 일치 |
| CL06 | CL06 | 연쇄 대상 테이블의 AFTER 트리거(H2) | 2026-11-10 일치 |
| CL07 | CL07 | 트리거 3단 사슬(H1) | 2026-11-10 일치 |
| CL08 | CL08a, CL08b | 같은 테이블 재갱신(RECURSIVE_TRIGGERS OFF·ON) | 2026-11-10 일치 |
| CL09 | CL09 | 트리거 DELETE가 2차 CASCADE로 같은 자식에 다시 닿음(H4·H5) | 2026-11-12 키 일치(오라클, 20회) |
| CL10 | CL10 | 3단 CASCADE + SET NULL + 동적 SQL 트리거(DM4) | 2026-11-12 키 일치(오라클, 20회) |
| CL11 | CL11, CL11b | INSTEAD OF INSERT·UPDATE | 2026-11-12 키 일치(오라클, 20회) |
| CL12 | CL12a, CL12b, CL12c | ROLLBACK 트리거와 롤백 뒤 남은 변경, `ROWCOUNT_BIG()` 0행 가드, @@IDENTITY를 읽는 트리거 | 2026-11-12 키 일치(오라클, 20회) |
| CL13 | CL13 | AFTER 트리거 3개, 사용자 Last, 감사 순서 NONE | 2026-11-12 키 일치(오라클, 20회) |
| CL14 | CL14, CL14b | 힙(PK 없음, 중복 행) | 2026-11-12 키 일치(오라클, 20회) |
| CL15 | CL15, CL15b | PK를 바꾸는 다중 행 UPDATE(UNPAIRED, BY_UNIQUE) | 2026-11-12 키 일치(오라클, 20회) |
| CL16 | CL16, CL16b | IDENTITY·SEQUENCE·SYSDATETIME·NEWID, 새 IDENTITY를 참조하는 자식 | 2026-11-12 키 일치(오라클, 20회) |
| CL17 | CL17 | 앞 문장 결과에 의존하는 다중 문장 | 2026-11-12 키 일치(오라클, 20회) |
| CL18 | CL18 | NO ACTION 위반(547)과 not_run | 2026-11-12 키 일치(오라클, 20회) |
| CL19 | CL19 | xml·hierarchyid·geography·text 열 | 2026-11-12 키 일치(오라클, 20회) |
| CL20 | CL20, CL20b | 행 수 생성기(`cl20.usp_Generate @parents = N`), M5 DELETE·UPDATE 틀 | 2026-11-12 키 일치(오라클, 20회) |
| CL21 | CL21 | EXEC: 분기 없는 프로시저, 안쪽 프로시저 1개(`PROC_DIRECT`, 반환값) | 미실행(2026-11-13 작성, 정적 검토만) |
| CL22 | CL22 | EXEC: 프로시저가 CASCADE 자식 하나를 먼저 DELETE한 뒤 부모를 DELETE(`PROC_DIRECT`와 FK 연쇄 구분) | 미실행 |
| CL23 | CL23 | EXEC: 균형 BEGIN/COMMIT 프로시저 + 트리거 쓰기(트리거 깊이 2) | 미실행 |
| CL24 | CL24 | EXEC: CATCH에서 ROLLBACK한 뒤 로그 프로시저가 씀(failed, 266, `isToolFault=false`, 롤백 뒤 남은 변경) | 미실행 |
| CL25 | CL25a, CL25b, CL25c, CL25d | 거부 사례: 동적 SQL(`REJECT_PROC_DYNAMIC_SQL`), 짝 안 맞는 COMMIT(`REJECT_PROC_UNBALANCED_COMMIT`), 다른 DB(master) 3부분 이름(`REJECT_PROC_EXTERNAL_EFFECT`), 도달 폐포 안 외부 부작용 트리거(`REJECT_TRIGGER_EXTERNAL_EFFECT`, DB `CascadeLab_Reach`) | 미실행 |

CL01~CL08b의 대조 기록은 [cascadelab/verification/report-2026-11-10.md](../cascadelab/verification/report-2026-11-10.md)다. CL09~CL20은 별도 대조 기록 없이 2026-11-12 평가 실행(`logs/eval/eval-20261112-143835`)에서 오라클 상태와 변경 키 집합이 손 정답과 20회 모두 같았다(`cases.csv`의 `status_match`·`oracle_vs_hand_keys_match`). M1의 셀 비교 상대는 오라클 값이다(§4, `total` = 미리보기 대 오라클).

CL21~CL25의 정답은 정답 형식 v0.3을 쓴다. 노드 `P:<스키마.프로시저>`(프로시저 호출 노드), 간선·원인 `PROC_DIRECT`, `statements[].returnValue`, `statementStatus[].isToolFault`, `expected.rejects[]`(`{ruleId, statementIndex}`)가 새로 생겼다. 규칙 이름(CL25a~d)과 EXEC 사례 그래프 모양(S 노드 없이 `P:`가 뿌리)은 통합 때 확정했다. 이 사례들의 정답은 SP11~SP16(준비됨, 미실행)의 추정에 기대므로 DB로 확인하기 전까지는 정답이 틀릴 수 있다(§8 16번).

### 2.2 AdventureWorks (공개 샘플, 시연)

공식 백업을 `AW_Origin`(원본 역할)에 복원해 쓴다. 정답 집합이 없어 지표 대신 시연과 스파이크에 쓴다. 대본은 설계 §10.7과 `scripts/demo/demo.http`다.

| ID | 시나리오 | 상태 |
|---|---|---|
| DM1 | SalesOrderHeader 1건 DELETE(FK 연쇄, 연쇄 대상 트리거 발동) | 2주차 Host HTTP로 확인(화면 확인 전). SP8 조회 준비 |
| DM2 | SalesOrderDetail.OrderQty UPDATE(트리거 3단 사슬) | SalesYTD가 실제로 바뀌는 주문을 SP8로 고를 예정(실행 전) |
| DM3 | HumanResources.Employee DELETE(INSTEAD OF 거부) | SP8 조회 준비, 실행 전 |
| DM4 | CascadeLab CL10(모호한 귀속) | 실행 전 |
| DM5 | pubs(PK 없는 테이블, ROLLBACK 트리거) | 안내만(pubs는 저장소에 넣지 않음) |
| DM6 | CascadeLab CL20 틀 5만 행 DELETE(상한, KEYS 모드) | 실행 전 |
| DM7 | 미리보기 뒤 원본 행을 바꾸고 [적용] → `drift_detected` | `scripts/demo/dm7-drift.sql` 준비, 실행 전 |

### 2.3 측정 환경

2026-11-12 실행의 `env.json`(`logs/eval/eval-20261112-143835/env.json`, M5는 `logs/eval-m5/m5-20261112-145013/env.json`·`m5-20261112-145801/env.json`) 기준이다. 2주차 실행(`logs/week2/eval/eval-20261110-135700/env.json`)과 서버 값이 같다.

| 항목 | 값 |
|---|---|
| SQL Server | 17.0.5035.5, CU10, Enterprise Developer Edition (64-bit), Linux 컨테이너 1개(Docker Desktop, WSL2) |
| 서버 설정 | nested triggers 1, disallow results from triggers 0, 세션 옵션 5432 |
| 컨테이너 메모리 | 물리 4,194,304 KB(커밋 목표 3,353,048 KB), CPU 16 |
| DB | 호환성 수준 170, 정렬 규칙 SQL_Latin1_General_CP1_CI_AS, RECURSIVE_TRIGGERS OFF(CL08b 전용 DB만 ON), RCSI·스냅샷 격리 OFF |
| 클라이언트 | Windows 10.0.19045, .NET 10.0.12, 논리 프로세서 16(같은 PC) |
| 실행 사용자 | `dbr_preview_user` |

## 3. 정답과 오라클

- **1차 정답:** CascadeLab 손 정답(변경 키 집합, 간선, 기대 오류)이다. 셀 값은 오라클 결과를 사람이 검토해 확정한다.
- **2차 정답(오라클):** 수집 장치가 없는 전용 원본 사본에서 같은 문장을 실행하고 롤백한 최종 차이다. 실행 전후 행별 SHA2_256 해시로 바뀐 행을 찾고 그 행만 셀 단위로 비교한다(`eval/DbRipple.Eval/HashSnapshotComparer.cs`, `OracleRunner.cs`).
- **독립성:** 평가 하네스 핵심(`eval/DbRipple.Eval`)은 제품 컴포넌트를 참조하지 않는다. 제품 최종 차이 계산기(C12), [적용]의 IDENTITY 짝 매핑(C15)과 코드를 공유하지 않는다.
- **원문 배치 경로:** 오라클은 문장별 실행과 함께 제품 분석기를 거치지 않은 원문 배치도 실행해 대조한다(`raw_batch_match`).
- **거부 사례(v0.3):** `expected.status = rejected`인 사례(CL25a~d)는 오라클을 실행하지 않는다. 거부된 문장은 원본에서 실행하면 안 되는 것들이라(CL25b의 남는 COMMIT은 오라클 트랜잭션을 확정해 시드를 오염시킨다) 오라클에 돌리지 않는다.
- **평가 훅:** 제품 실행기가 마지막 문장과 REVERT 뒤, ROLLBACK 전에 하네스 콜백을 같은 연결로 부른다. 하네스는 자기 비교기로 샌드박스 순 변경을 기록한다. 이 값이 E_obs·E_cap의 기준이다.
- **사례 사이 초기화:** 사례마다 오라클 시작 상태가 시드 디지스트와 같은지 확인한다. 롤백 뒤에도 행이 남는 사례(`leavesResidue`, CL12a)는 맨 뒤에 돈다.
- **정답 정정:** 손 정답과 오라클이 다르면 `differences.txt`에 후보로 남기고, 원인(문서 해석 오류인지 정답 오류인지)을 적은 뒤 고친다. 지금까지의 정정은 `firings[].pairing` 6건이다(CL07·CL08a·CL08b, 정답 오류, M1·M2 영향 없음, [cascadelab/README.md](../cascadelab/README.md) "정정"). 2026-11-12 실행의 `differences.txt`에는 손 정답 대 오라클 차이와 원문 배치 대 문장별 차이가 0건이다(남은 줄은 M3 예측에만 있는 ambiguous 후보 간선과 CL12a의 예상된 남은 행 기록, §6.1).

## 4. 지표 정의 (평가 하네스 코드 기준)

열 명세는 `eval/DbRipple.Eval/MetricsCsv.cs` 주석이 기준이다. 아래는 그 요약이다. CSV 공통 규칙: UTF-8(BOM 없음), RFC 4180, 소수 4자리, 분모가 0인 비율은 빈 칸.

| 지표 | 파일 | 단위와 계산 |
|---|---|---|
| M1 일치율 | `m1.csv` | 셀 사실 (테이블, 키, 열, 값). precision·recall·F1과 행 완전 일치율. 비결정 셀은 tp·fp·fn에서 빼고 존재·형식 일치율로 따로 센다. 비교는 셋이다: `total` = 미리보기 대 오라클(E_total), `obs` = 평가 훅 대 오라클(E_obs, 관찰자 효과), `cap` = 미리보기 재구성 대 평가 훅(E_cap, 수집·재구성 오류). `residual_fill_rate` = 잔차·재조회로만 설명돼 E_cap 예측에서 뺀 셀 비율. `identity_rows`·`identity_paired` = 정답 쪽 새 IDENTITY 행 수와 짝지은 수(3주차 추가, `IdentityPairing.cs`) |
| M2 탐지 | `m2.csv` | (테이블, 키)당 한 번. 원인(DIRECT, FK_CASCADE, FK_SET_NULL, FK_SET_DEFAULT, TRIGGER, INSTEAD_OF, SAME_TABLE_TRIGGER, ONE_OF, PROC_DIRECT)별 재현율. `PROC_DIRECT` 버킷은 v0.3(EXEC)에서 더했다. 정답은 손 정답 changes ∪ residualCommitted, 예측은 NetChanges ∪ ResidualCommitted |
| M3 귀속 정확도 | `m3.csv` | 간선 키 (문장, 부모, 자식, 종류, 이름). 확신도(certain, inferred, ambiguous, unexplained, all)별 precision = 맞힌 예측 간선 ÷ 예측 간선, recall = 찾은 정답 간선 ÷ 정답 간선(확신도 무관). ambiguous 후보 포함률 = 정답 간선이 든 후보 집합 ÷ 후보 집합((문장, 자식, 종류)로 묶음). certain 과신 = 정답에 없는 certain 간선 + 정답 확신도가 더 약한 certain 간선(목표 0) |
| M4 순서 안정성 | `m4.csv` | 같은 사례를 k회 반복. 형제 순서를 뺀 정규 그래프 서명이 한 가지면 안정. observed_only 변동률 = (k − 가장 흔한 observed_only 서명 수) ÷ k. 파이프라인 예외 반복은 분모에서 빼고 `error_runs`로 따로 센다. 거부는 결정적이라 유효 반복이다 |
| M5 응답 시간 | `m5.csv` | CL20·CL20b, N = 직접 대상 행 수. 워밍업 1회 뒤 5회(기본값), 단계별 p50·p95(선형 보간). 단계: analyze, execute, read_log, reread, net_diff, resolve, serialize, total, oracle_execute. 수집 오버헤드 = execute p50 ÷ oracle_execute p50. 브라우저 첫 그래프·첫 그리드 열은 빈 칸이고 SP9 값을 손으로 옮긴다 |

스위트 집계(`metrics.csv`)는 마이크로 평균이다. 사례 상태 일치(`status_match_rate`)도 함께 낸다.

**v0.3(2026-11-13)에서 바뀐 계산 범위:**

- **거부 사례는 M1·M2·M3와 대조군(B0·B2) 행에서 뺀다.** 오라클을 실행하지 않기 때문이다. `cases.csv`에는 `status_expected = rejected`와 미리보기 상태(`status_actual`)만 남고, (규칙 ID, 문장 번호) 비교는 `differences.txt`와 실행 폴더의 `rejects.json`에 쓴다. 규칙 ID가 `*`로 끝나면 접두 패턴이다.
- **`returnValue`·`isToolFault` 비교는 지표가 아니다.** 손 정답의 기대값과 제품 `statements[]`의 값을 비교해 `differences.txt`에 보고만 한다(`eval/DbRipple.Eval/V03Checks.cs`).
- **`database`가 `CascadeLab`·`CascadeLab_RT`가 아닌 사례(CL25d, `CascadeLab_Reach`)는 하네스가 건너뛴다.** 분석 API 회귀(`scripts/home-test.ps1` N1 단계)로 확인한다.

**알려진 정의 차이(2026-11-11 검토, 결과를 읽을 때 함께 볼 것):**

1. B0의 M3 예측 간선은 손 정답 DIRECT 간선 그대로라 B0 precision은 **구성상 1.0**이다. 비교할 값은 recall이다.
2. B0 필터는 테이블 단위라, 직접 대상 테이블에서 같은 테이블 트리거가 바꾼 행도 B0이 찾은 것으로 센다(OUTPUT 기반 도구보다 후하게 잰다).
3. 직접 대상은 문장 정규식(스키마로 한정한 대상)과 손 정답 DIRECT 행 테이블의 합집합이다. `DELETE TOP (n) FROM s.t`는 정규식이 못 잡아 손 정답 DIRECT 행에만 기댄다.
4. M5 오버헤드의 분자(샌드박스 execute)는 실행 봉투 비용(BEGIN TRAN, 제어 UPDATE, 경계 읽기, EXECUTE AS/REVERT)을 포함하고, 분모(오라클 execute)는 문장 실행만 잰다. 작은 N에서는 비율이 순수 수집 비용보다 크게 나온다.
5. M4는 예외 반복을 안정성 분모에서 뺀다. 예외 반복이 많으면 `M4.error_run_rate`를 함께 봐야 한다.
6. (2026-11-12 결과를 읽으며 더함) B2의 M1·M2 예측은 오라클 순 변경 그대로라 M1·M2는 **구성상 1.0**이다. B0·B2의 사례 상태도 오라클 상태라 상태 일치는 구성상 1.0이다.
7. (같음) ambiguous 간선은 후보마다 예측 간선 하나로 세므로, 후보가 k개인 묶음의 precision 상한은 1/k다. ambiguous는 후보 포함률로 읽는다.

## 5. 대조군

근거: `eval/DbRipple.Eval/Baselines.cs`. 대조군 행은 제품과 같은 CSV에 `system` 열 `B0`·`B1`·`B2`로 쓴다. 상태는 모두 오라클 상태다. 평가 훅이 없으므로 M1은 `total`만 채운다.

| ID | 정의 | 기대(추정) |
|---|---|---|
| B0 | 직접 대상만 본다(OUTPUT 기반 도구 모사). 오라클 순 변경을 문장의 직접 대상 테이블로 거른 것을 예측으로 쓴다. EXEC 문장의 직접 대상은 프로시저의 정적 쓰기 대상(손 정답 `PROC_DIRECT` 행의 테이블)으로 본다. M3 예측 간선은 손 정답 DIRECT 간선(certain)뿐이고 `PROC_DIRECT` 간선은 넣지 않는다 | 연쇄·트리거 재현율(M2)이 낮을 것이다 |
| B2 | 사본 + 최종 차이(Data Compare 자동화판). 오라클 순 변경을 그대로 예측으로 쓴다. M3 예측 간선은 0개다 | M1·M2는 제품과 비슷하고 M3 recall은 0이다. 작업량 `manual_select_tables`(직접 대상이 아닌데 바뀐 테이블 수)를 함께 본다 |
| B1 | FK 메타데이터 정적 예측(`FkStaticBaseline.cs`, 순수 함수 `Predict`). 문장의 직접 대상과 그 연산(INSERT·UPDATE·DELETE, 문장 원문)에서 시작해 DB의 활성(`is_disabled = 0`) FK 참조 동작만 따라 고정점까지 넓힌다. 부모 DELETE + CASCADE → 자식 DELETE, 부모 DELETE + SET NULL·SET DEFAULT → 자식 UPDATE, 부모 UPDATE + ON UPDATE CASCADE·SET NULL·SET DEFAULT → 자식 UPDATE이고 NO ACTION·INSERT는 넓히지 않는다. 열과 트리거는 보지 않는다. EXEC 문장은 B0처럼 손 정답 `PROC_DIRECT` 행의 테이블을 직접 대상으로 보고 연산은 그 행의 변경 종류로 정한다. 예측 변경 = 오라클 순 변경을 (직접 대상 ∪ FK로 닿은 테이블)로 거른 것. M3 예측 간선 = B0의 DIRECT 간선 + FK 간선(parent·child는 묶음 노드, name = FK 이름, certain, 같은 (테이블, 연산)은 `#k` 없이 한 노드). FK 목록은 CLI가 사례 DB의 `sys.foreign_keys`에서 DB마다 한 번 읽는다. 작업량 `manual_select_tables` = 예측 범위(직접 대상 ∪ FK로 닿은 테이블) 밖인데 바뀐 테이블 수(B0·B2는 직접 대상 밖). 실행: `--systems preview,B0,B1,B2` | FK 연쇄(M2 `FK_CASCADE`·`FK_SET_NULL`·`FK_SET_DEFAULT`)는 맞히고 트리거 변경과 그 뒤의 연쇄는 놓칠 것이다. 열을 안 보므로 키 열이 아닌 UPDATE에도 ON UPDATE CASCADE를 따라가고(거짓 양성), 프로시저가 자식을 직접 지운 경우(CL22)에도 FK 간선을 낸다. 손 정답이 같은 묶음을 `#1`·`#2`로 나눈 사례(CL09)는 간선 키가 어긋난다. **DB 연결 없이 짠 코드라 첫 실행 전까지 실행 결과는 없다** |

## 6. 결과

### 6.1 CascadeLab CL01~CL20 (2026-11-12)

**실행 `eval-20261112-143835`, 2026-11-12 14:38~14:49(650초), 사례 30개 × 반복 20회 × 시스템 3개(preview, B0, B2).** 출처: `logs/eval/eval-20261112-143835/`의 `metrics.csv`, `m1.csv`~`m4.csv`, `cases.csv`, `differences.txt`, 자동 생성 표 `report.md`. 명령: `. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -c Release -- cascadelab --systems preview,B0,B2 --repeat 20 --out logs\eval`(종료 코드 0, 미리보기 예외 0, 평가 훅 누락 0).

**반복 20회의 사례별 지표는 모두 같았다.** `m1.csv`·`m2.csv`·`m3.csv`에서 반복 열을 뺀 행이 (시스템, 사례, 비교·원인·확신도) 묶음마다 한 가지뿐이다(271·721·451묶음). 그래서 아래 스위트 수치의 분자·분모는 한 번 실행한 값의 20배다. "1회분"은 그 값을 20으로 나눈 것이다.

**사례 단위:** 상태 일치 preview·B0·B2 모두 600/600(B0·B2의 상태는 구성상 오라클 상태라 1.0이 당연하다, §5). 오라클 대 손 정답 키 일치 600/600, 원문 배치 대 문장별 일치 600/600, 비교기 문제 0건.

#### M1 일치율(셀 단위, 마이크로 평균)

| system | 비교 | 사례 수 | precision | recall | F1 | 행 완전 일치 | 비결정 셀 존재·형식 | 잔차로 메운 셀 | IDENTITY 짝 |
|---|---|---|---|---|---|---|---|---|---|
| preview | total(E_total) | 30 | 1.0000 (3520/3520) | 1.0000 (3520/3520) | 1.0000 | 1.0000 (2340/2340) | 1.0000 (320/320) | 0.0057 (20/3520) | 1.0000 (140/140) |
| preview | obs(E_obs) | 27 | 1.0000 (3480/3480) | 1.0000 (3480/3480) | 1.0000 | 1.0000 (2320/2320) | 1.0000 (300/300) | (집계 없음) | 1.0000 (120/120) |
| preview | cap(E_cap) | 27 | 1.0000 (3460/3460) | 0.9943 (3460/3480) | 0.9971 | 0.9914 (2300/2320) | 1.0000 (300/300) | 0.0057 (20/3480) | 1.0000 (120/120) |
| B0 | total | 30 | 1.0000 (1340/1340) | 0.3807 (1340/3520) | 0.5514 | 0.3761 (880/2340) | 0.6250 (200/320) | — | 0.4286 (60/140) |
| B2 | total | 30 | 1.0000 (3520/3520) | 1.0000 (3520/3520) | 1.0000 | 1.0000 (2340/2340) | 1.0000 (320/320) | — | 1.0000 (140/140) |

- 1회분: 정답 셀 176개, 행 117개, 비결정 셀 16개, 새 IDENTITY 행 7개(CL16 4, CL12c 2, CL12a 1).
- obs·cap의 사례 수가 27인 것은 평가 훅 값이 없는 실패·차단 사례 3개(CL03b 547, CL18 547, CL12a ROLLBACK 트리거 3609) 때문이다.
- cap recall 0.9943은 CL19 `text` 열 셀 1개(1회분)다. 감사 기록에 값이 없어 제품이 재조회로 채운 셀이라, 하네스가 정의대로 E_cap 예측에서 뺐다(`eval/DbRipple.Eval/Reconstruction.cs`, §4 M1 `residual_fill_rate`). 손 정답도 이 변경을 잔차(RESIDUAL 간선)로 적었다. total·obs는 1.0이다.

사례별(반복 1회분, 20회 모두 같음):

| 사례 | 상태 | 정답 셀 | preview total tp/fp/fn | E_cap | IDENTITY 짝 | B0 셀 recall | B2 셀 recall |
|---|---|---|---|---|---|---|---|
| CL01 | ok | 9 | 9/0/0 | 9/9 | — | 1/9 | 9/9 |
| CL02 | ok | 3 | 3/0/0 | 3/3 | — | 1/3 | 3/3 |
| CL03 | ok | 3 | 3/0/0 | 3/3 | — | 1/3 | 3/3 |
| CL03b | failed(547) | 0 | 0/0/0 | — | — | — | — |
| CL04 | ok | 9 | 9/0/0 | 9/9 | — | 6/9 | 9/9 |
| CL04b | ok | 4 | 4/0/0 | 4/4 | — | 3/4 | 4/4 |
| CL05 | ok | 7 | 7/0/0 | 7/7 | — | 1/7 | 7/7 |
| CL06 | ok | 7 | 7/0/0 | 7/7 | — | 1/7 | 7/7 |
| CL07 | ok | 8 | 8/0/0 | 8/8 | — | 1/8 | 8/8 |
| CL08a | ok | 2 | 2/0/0 | 2/2 | — | 2/2 | 2/2 |
| CL08b | ok | 2 | 2/0/0 | 2/2 | — | 2/2 | 2/2 |
| CL09 | ok | 6 | 6/0/0 | 6/6 | — | 1/6 | 6/6 |
| CL10 | ok | 13 | 13/0/0 | 13/13 | — | 1/13 | 13/13 |
| CL11 | ok | 5 | 5/0/0 | 5/5 | — | 3/5 | 5/5 |
| CL11b | ok | 5 | 5/0/0 | 5/5 | — | 1/5 | 5/5 |
| CL12a | trigger_blocked(3609) | 2 | 2/0/0 | — | 1/1 | 0/2 | 2/2 |
| CL12b | ok | 3 | 3/0/0 | 3/3 | — | 1/3 | 3/3 |
| CL12c | ok | 5 | 5/0/0 | 5/5 | 2/2 | 1/5 | 5/5 |
| CL13 | ok | 18 | 18/0/0 | 18/18 | — | 1/18 | 18/18 |
| CL14 | ok | 6 | 6/0/0 | 6/6 | — | 6/6 | 6/6 |
| CL14b | ok | 1 | 1/0/0 | 1/1 | — | 1/1 | 1/1 |
| CL15 | ok | 6 | 6/0/0 | 6/6 | — | 6/6 | 6/6 |
| CL15b | ok | 8 | 8/0/0 | 8/8 | — | 8/8 | 8/8 |
| CL16 | ok | 14 | 14/0/0 | 14/14 | 4/4 | 6/14 | 14/14 |
| CL16b | ok | 1 | 1/0/0 | 1/1 | — | 1/1 | 1/1 |
| CL17 | ok | 1 | 1/0/0 | 1/1 | — | 1/1 | 1/1 |
| CL18 | failed(547) | 0 | 0/0/0 | — | — | — | — |
| CL19 | ok | 5 | 5/0/0 | 4/5(재조회 1) | — | 5/5 | 5/5 |
| CL20 | ok | 18 | 18/0/0 | 18/18 | — | 2/18 | 18/18 |
| CL20b | ok | 5 | 5/0/0 | 5/5 | — | 3/5 | 5/5 |

CL12a의 정답 셀 2개는 ROLLBACK 뒤 커밋된 `ErrLog` 행(residualCommitted)이다.

#### M2 탐지((테이블, 키)당 한 번, 원인별 재현율)

| 원인 | preview | B0 | B2 | 해당 사례 수 |
|---|---|---|---|---|
| DIRECT | 1.0000 (800/800) | 1.0000 (800/800) | 1.0000 (800/800) | 25 |
| FK_CASCADE | 1.0000 (740/740) | 0.0000 (0/740) | 1.0000 (740/740) | 9 |
| FK_SET_NULL | 1.0000 (80/80) | 0.0000 (0/80) | 1.0000 (80/80) | 2 |
| FK_SET_DEFAULT | 1.0000 (40/40) | 0.0000 (0/40) | 1.0000 (40/40) | 1 |
| TRIGGER | 1.0000 (580/580) | 0.0690 (40/580) | 1.0000 (580/580) | 13 |
| INSTEAD_OF | 1.0000 (100/100) | 0.4000 (40/100) | 1.0000 (100/100) | 2 |
| SAME_TABLE_TRIGGER | — (0/0) | — (0/0) | — (0/0) | 0 |
| ONE_OF | — (0/0) | — (0/0) | — (0/0) | 0 |
| 전체(ALL) | 1.0000 (2300/2300) | 0.3652 (840/2300) | 1.0000 (2300/2300) | 28 |

- 1회분: 정답 (테이블, 키) 115개. 원인별 행은 한 키가 원인 여럿에 들 수 있어 합이 ALL과 같지 않다.
- B0이 찾은 TRIGGER 2개(CL08a·CL08b)와 INSTEAD_OF 2개(CL11·CL11b)는 직접 대상 테이블에서 트리거가 바꾼 행이다. B0 필터가 테이블 단위라 찾은 것으로 셌다(§4 정의 차이 2). OUTPUT 기반 도구라면 못 찾을 수 있다.
- 손 정답에 SAME_TABLE_TRIGGER·ONE_OF 원인이 붙은 키가 없어 두 줄은 분모가 0이다.

#### M3 귀속 정확도(간선 키, 확신도별)

| system | 확신도 | precision | recall | ambiguous 후보 포함 | certain 과신 |
|---|---|---|---|---|---|
| preview | certain | 1.0000 (940/940) | 1.0000 (940/940) | | 0 / 940 |
| preview | inferred | 1.0000 (1020/1020) | 1.0000 (1020/1020) | | |
| preview | ambiguous | 0.3636 (80/220) | 1.0000 (80/80) | 1.0000 (80/80) | |
| preview | unexplained | — (0/0) | — (0/0) | | |
| preview | all | 0.9358 (2040/2180) | 1.0000 (2040/2040) | 1.0000 (80/80) | |
| B0 | certain | 1.0000 (560/560) | 0.5957 (560/940) | | 0 / 560 |
| B0 | all | 1.0000 (560/560) | 0.2745 (560/2040) | — (0/0) | |
| B2 | all | — (0/0) | 0.0000 (0/2040) | — (0/0) | |

- 1회분: 정답 간선 102개(certain 47, inferred 51, ambiguous 4), 제품 예측 간선 109개. 찾은 정답 간선 102개 모두 예측 확신도가 정답 확신도와 같았다(`confidence_match` 102/102). FIRES 간선 observed 불일치 0, certain 과신 0(정답에 없는 certain 0, 정답보다 센 certain 0).
- **ambiguous precision 0.3636은 정의에서 나온 값이다.** 제품은 ambiguous 간선을 후보 집합으로 내고 하네스는 후보 하나하나를 예측 간선으로 센다. 손 정답은 후보 가운데 실제로 쓴 트리거 하나만 간선으로 적는다. 그래서 후보가 k개인 묶음의 precision은 1/k다. CL10(후보 2개 묶음 1개)과 CL13(후보 3개 묶음 3개)의 후보 11개 중 4개가 정답이고, 정답이 든 후보 집합은 4/4다(1회분). ambiguous는 후보 포함률로 읽는다. all precision 0.9358도 이 7개 때문이다.
- B0의 precision 1.0은 **구성상** 값이다(예측 간선이 손 정답 DIRECT 간선 그대로, §4 정의 차이 1). 비교할 값은 recall 0.2745다. B2는 간선을 내지 않아 recall 0이다.
- 대조군 작업량(`M3.manual_select_tables`): B0·B2 모두 사례·반복 평균 1.2333(740/600)이다. 직접 대상이 아닌데 바뀐 테이블을 사용자가 직접 찾아 SELECT해야 하는 수다.

#### M4 순서 안정성(제품, 20회)

| 범위 | 값 |
|---|---|
| 정규 그래프(형제 순서 제외) 서명이 한 가지인 사례 | 1.0000 (30/30) |
| observed_only 변동률 | 0.0000 (0/600) |
| 예외 반복 | 0.0000 (0/600) |

- 사례마다 유효 반복 20, 상태 가짓수 1, 정규 그래프 가짓수 1이다(`m4.csv`).
- observed_only 간선이 있는 사례는 CL09(1), CL10(2), CL13(9), CL19(1) 넷뿐이다. 나머지 26개 사례는 변동률이 구조적으로 0이라, M4가 실제로 가려낸 범위는 이 네 사례의 간선 13개다(§8).

#### 대조군 요약

| 지표 | preview | B0(직접 대상만) | B2(사본 + 최종 차이) |
|---|---|---|---|
| M1 셀 recall(total) | 1.0000 | 0.3807 | 1.0000 |
| M2 ALL 재현율 | 1.0000 | 0.3652 | 1.0000 |
| M2 FK 연쇄(CASCADE·SET NULL·SET DEFAULT 합) | 1.0000 (860/860) | 0.0000 (0/860) | 1.0000 (860/860) |
| M3 all recall | 1.0000 | 0.2745 | 0.0000 |
| M3 certain 과신 | 0 | 0(구성상) | 해당 없음(예측 없음) |
| 직접 찾을 테이블(사례 평균) | 0(정의상) | 1.2333 | 1.2333 |

B2는 무엇이 바뀌었는지(M1·M2)는 제품과 같지만 왜 바뀌었는지(M3)는 0이다. B0은 연쇄 변경을 하나도 못 찾고 트리거 변경은 직접 대상 테이블의 것만 찾는다. 세 지표를 함께 채운 것은 제품뿐이다. 다만 합성 사례 30개, 같은 오라클 데이터에서 나온 비교다(§8).

#### 사례별 불일치와 원인

1.0이 아닌 제품 수치와 `differences.txt` 줄을 모두 사례별로 따졌다. 제품 결함, 하네스 결함, 손 정답 오류는 0건이고 하네스를 고치지 않았다.

| 사례 | 나타난 곳 | 내용(1회분) | 원인 갈래 | 근거 |
|---|---|---|---|---|
| CL10 | M3 ambiguous precision, `differences.txt` 1줄(간선 차이는 반복 1에서만 적는다) | `trg_Region_del_dyn → RegionLog INSERT` 후보 간선 1개가 예측에만 있다 | 지표 정의(결함 아님) | 손 정답 간선 basis가 후보 2개(trg_Region_del_log, trg_Region_del_dyn)를 적고 실제 하나만 간선으로 둔다. 정답 후보는 포함됨 |
| CL13 | 같음, `differences.txt` 6줄 | `trg_T_1·2·3 → Log INSERT#2~#4` 후보 간선 6개가 예측에만 있다 | 지표 정의(결함 아님) | 손 정답이 후보 3개를 ambiguous로 적고 실제 하나씩만 간선으로 둔다. 후보 집합 3/3 포함 |
| CL19 | M1 cap recall·행 완전 일치 | `text` 열 셀 1개가 E_cap 예측에서 빠짐 | 지표 정의(재조회 셀 제외, 결함 아님) | `Reconstruction.cs` 재조회 열 제외 규칙. total·obs는 1.0, 손 정답도 잔차로 적음 |
| CL12a | `differences.txt` 39줄 | 반복마다 오라클 롤백 뒤 `ErrLog` 행이 남고(20줄), r2~r20의 오라클 시작 상태가 시드와 다르다(19줄) | 사례 성질(leavesResidue) + 하네스 한계 | 손 정답 observedOnly OO3가 예고한 동작이다. 20회 모두 상태·키·M1~M3 값이 같아 지표 영향은 없었다. 다만 하네스는 같은 사례의 반복 사이에 원본을 되돌리지 않는다(§8) |
| B0·B2 | M1·M2·M3 | 1.0 미만 | 대조군 정의 | §5, §4 정의 차이 1~3 |

### 6.2 M5 응답 시간

**실행 `m5-20261112-145013`, 2026-11-12 14:50~14:57(438초), N = 1, 100, 1천, 1만, 5만, 사례 CL20(DELETE)·CL20b(UPDATE), 워밍업 1회 뒤 5회, 제품 모드(평가 훅 없음).** 출처: `logs/eval-m5/m5-20261112-145013/m5.csv`, 표본별 값은 실행 콘솔 기록(이 문서에는 옮기지 않음). 명령: `. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -c Release -- m5 --sizes 1,100,1000,10000,50000 --out logs\eval-m5`(종료 코드 0, 예외 0, `differences.txt` 0바이트). N은 `cl20.usp_Generate @parents = N`의 직접 대상 행 수다.

단위 ms, p50·p95는 워밍업을 뺀 5회의 선형 보간 백분위수다(5회라 p95는 4·5번째 값 사이).

| 사례 | 연산 | N | 상태 | 행 이미지 | 순 변경 | total p50 | total p95 | execute p50 | reread p50 | resolve p50 | 오라클 execute p50 | 오버헤드(execute ÷ 오라클) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CL20 | DELETE | 1 | ok | 5 | 5 | 18.0 | 22.6 | 8.0 | 4.0 | 1.0 | 1.0 | 8.0000 |
| CL20 | DELETE | 100 | ok | 500 | 500 | 75.0 | 98.4 | 33.0 | 12.0 | 3.0 | 5.0 | 6.6000 |
| CL20 | DELETE | 1,000 | ok | 5,000 | 5,000 | 885.0 | 915.2 | 141.0 | 589.0 | 63.0 | 66.0 | 2.1364 |
| CL20 | DELETE | 10,000 | ok | 50,000 | 50,000 | 6,096.0 | 7,576.6 | 666.0 | 3,986.0 | 995.0 | 238.0 | 2.7983 |
| CL20 | DELETE | 50,000 | truncated(KEYS) | 250,000 | 250,000 | 43,364.0 | 46,642.4 | 3,372.0 | 21,050.0 | 16,029.0 | 1,184.0 | 2.8480 |
| CL20b | UPDATE | 1 | ok | 4 | 2 | 19.0 | 23.6 | 11.0 | 2.0 | 2.0 | 1.0 | 11.0000 |
| CL20b | UPDATE | 100 | ok | 300 | 150 | 36.0 | 41.6 | 26.0 | 3.0 | 2.0 | 2.0 | 13.0000 |
| CL20b | UPDATE | 1,000 | ok | 3,000 | 1,500 | 112.0 | 132.0 | 62.0 | 11.0 | 20.0 | 6.0 | 10.3333 |
| CL20b | UPDATE | 10,000 | ok | 30,000 | 15,000 | 886.0 | 1,023.6 | 325.0 | 121.0 | 263.0 | 42.0 | 7.7381 |
| CL20b | UPDATE | 50,000 | truncated(KEYS) | 150,000 | 75,000 | 4,747.0 | 5,507.8 | 1,458.0 | 633.0 | 1,497.0 | 183.0 | 7.9672 |

**10만 행(따로 실행) `m5-20261112-145801`, 2026-11-12 14:58~15:11(788초), 같은 설정.** 출처: `logs/eval-m5/m5-20261112-145801/m5.csv`. 명령: 위 명령에서 `--sizes 100000`(종료 코드 0, 예외 0, `differences.txt` 0바이트). 5만 행 DELETE가 표본당 약 45초여서 10만 행은 표본당 약 2분, 전체 15~20분으로 보고 따로 돌렸다. 실행 중 컨테이너는 계속 Up이었고, 20초마다 본 메모리 사용량이 5GiB를 넘은 적이 없다(Docker 한도 6GiB, SQL Server 커밋 목표 약 3.3GB).

| 사례 | 연산 | N | 상태 | 행 이미지 | 순 변경 | total p50 | total p95 | execute p50 | reread p50 | resolve p50 | 오라클 execute p50 | 오버헤드 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CL20 | DELETE | 100,000 | truncated(KEYS) | 500,000 | 500,000 | 105,171.0 | 112,221.8 | 5,717.0 | 38,895.0 | 55,710.0 | 2,135.0 | 2.6778 |
| CL20b | UPDATE | 100,000 | truncated(KEYS) | 300,000 | 150,000 | 9,774.0 | 10,080.2 | 2,662.0 | 1,359.0 | 2,930.0 | 319.0 | 8.3448 |

두 실행을 합친 표는 `logs/eval/eval-20261112-143835/report.md` "M5 응답 시간"에 있다(`report --m5`에 두 폴더를 쉼표로 넘김).

읽은 것:

- **1만 행까지:** DELETE 6.1초, UPDATE 0.9초(p50)다. 1천 행 이하는 둘 다 1초 안이다.
- **5만·10만 행:** 두 사례 모두 행 이미지 상한을 넘어 KEYS 모드(`truncated`)가 됐다. DELETE는 43.4초(5만)·105.2초(10만)로 대화형으로 쓰기 어렵다. UPDATE는 4.7초·9.8초다.
- **DELETE의 병목은 재조회(reread)와 귀속(resolve)이다.** 1만 행에서 total 6.1초 중 reread 4.0초·resolve 1.0초, 5만 행에서 43.4초 중 reread 21.1초·resolve 16.0초다. resolve는 1만→5만(5배)에서 약 16배, 5만→10만(2배)에서 약 3.5배(55.7초)라 선형보다 빨리 는다. 10만 행 DELETE는 total 105.2초 중 resolve 55.7초·reread 38.9초다. execute(샌드박스 실행)는 1만 행 0.7초, 5만 행 3.4초로 작다. UPDATE는 같은 행 수에서 reread가 작다(5만 행 0.6초). 원인 분석은 하지 않았다(측정만, 제품 코드는 읽기 전용).
- **수집 오버헤드:** 1천 행 이상에서 DELETE 2.1~2.8배, UPDATE 7.7~10.3배(10만 행 8.3배)다. 1~100행의 6.6~13배는 실행 봉투 비용(§4 정의 차이 4)이 문장 실행(1~5ms)보다 커서 생긴 값이다.
- **브라우저 첫 그래프·첫 그리드(SP9):** 측정 전이다. `m5.csv`의 두 열은 비어 있다.

### 6.3 측정 전

| 항목 | 상태 | 명령(저장소 루트, `eval/DbRipple.Eval.Cli/Program.cs` 옵션) |
|---|---|---|
| M5 브라우저 첫 그래프·첫 그리드 렌더(SP9) | 측정 전(참고값 1회, 브라우저 창이 보여야 함) | 웹 화면 `/sp9`. 기록은 [spikes/SP9.md](spikes/SP9.md) |
| SP8 AW DM1·DM2·DM3 실제 전파 | **확인함(2026-11-12)**: DM1 주문 43697, DM2 상세 52, DM3 직원 1. DM2의 SalesYTD는 회계 기간 불일치로 늘 0행 | [spikes/SP8.md](spikes/SP8.md), `spikes/logs/sp8-*.log`(AW_Origin 읽기 전용) |
| [적용] 검증 게이트(AW DM1~DM3, DM7) | **확인함(2026-11-12, 전용 원본 사본 `dbrt_e2e_origin`)**: DM1·DM2 `committed`(원본에서 실제 반영을 독립 SQL로 확인), DM3 `precondition_failed`(미리보기 `trigger_blocked`), DM7 `drift_detected`(원본 값 커밋 안 됨). CSRF 403, 동시 요청 409 LOCKED도 확인 | `logs/e2e/host-e2e-2026-11-12.log`, `logs/e2e/sql-*.log` |
| 누적 구성(초기화 없이 연속 실행, IDENTITY 벌어짐) | 측정 전 | 설계 §10.3 "구성". 하네스 옵션 없음(방법 미정) |
| CL21~CL25(EXEC·거부 사례) 평가 실행 | 측정 전(2026-11-13 작성). 정답 형식 v0.3 | 집에서 `.\scripts\home-test.ps1 -IncludeExec`(아래) |
| 2026-11-13에 더한 기능(도달 폐포 거부, EXEC 미리보기, KEYSET_HASH, Split)의 DB 확인 | 측정 전. 정적 검사와 단위 테스트만 했다 | [home-test-plan.md](home-test-plan.md), [spikes/README.md](spikes/README.md) SP11~SP21 |

집에서 평가를 다시 돌릴 때는 `scripts\home-test.ps1`을 쓴다. Eval 단계의 기본 사례는 CL01~CL20(30개)이고(EXEC·거부 사례 제외), `-IncludeExec`를 주면 CL21~CL25를 더한다(그때는 사례 집합이 달라 이전 실행과의 지표 비교는 참고용이다).

```powershell
.\scripts\home-test.ps1 -Skip Tests,N1                  # 평가만, 기본 CL01~CL20
.\scripts\home-test.ps1 -Skip Tests,N1 -IncludeExec      # CL21~CL25 포함
```

이번에 쓴 명령(다시 돌릴 때):

```powershell
. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -c Release -- cascadelab --systems preview,B0,B2 --repeat 20 --out logs\eval
. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -c Release -- m5 --sizes 1,100,1000,10000,50000 --out logs\eval-m5
. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -c Release -- m5 --sizes 100000 --out logs\eval-m5
dotnet run --project eval\DbRipple.Eval.Cli -c Release -- report --run logs\eval\<실행 id> --m5 logs\eval-m5\<실행 id>,logs\eval-m5\<10만 행 실행 id>
```

### 6.4 이전 실행과 스파이크

**CascadeLab CL01~CL08b, 2026-11-10, 실행 `eval-20261110-135700`, 반복 1회, 제품(preview)만.** 출처: `logs/week2/eval/eval-20261110-135700/metrics.csv`, `cases.csv`. 2주차 하네스(answer-format v0.1, IDENTITY 짝 매핑·KeyFolding 추가 전)로 쟀다. M1 total·obs·cap 모두 precision·recall 1.0000(54/54), M2 ALL 45/45, 상태 일치 11/11, `differences.txt` 0바이트였다. 그 뒤 손 정답이 v0.2로 바뀌어 같은 사례의 셀·키 수는 2026-11-12 실행(§6.1)과 다르지만, 두 실행 모두 1.0이다.

**SP7, 열린 트랜잭션 안 최종 차이(2026-11-10, AW 71개 테이블 760,598행).** 출처: [spikes/SP7.md](spikes/SP7.md), `logs/week2/sp7-aw-netdiff.log`.

| 항목 | 첫 실행 | C12 수정 뒤 다시 실행 |
|---|---|---|
| 독립 확인 | 10/10 일치 | 10/10 일치 |
| 열린 트랜잭션 안 | p50 1,553ms, p95 2,028ms | p50 1,652ms, p95 3,697ms |
| 롤백 뒤(트랜잭션 없음, 실패 경로) | p50 1,447ms, p95 1,575ms | p50 1,875ms, p95 2,129ms |

5회 측정이라 p95는 최댓값이다. 이 시간은 [적용]의 드리프트 잠금 유지 시간과 실패 경로 검사 시간의 근사로 쓴다(전 테이블 비교 한 번 기준).

## 7. 확인한 사실과 추정

스파이크 결과는 [spikes/README.md](spikes/README.md) 표에 모았다. 요약:

- **확인(DB 실측):** SP0 에디션·스냅샷, SP1 전제 H1~H5 성립(H3은 3회 관측), SP2 관찰자 효과 점검 (a)(c)(d)(e) 성립((b)는 하네스의 E_obs로 대신 잰다), SP3 자기 테이블 재갱신이 감사 묶음으로 잡힘, SP5 기준선 시간, SP6 형식 변환(규칙 수정), SP7 열린 트랜잭션 안 최종 차이, SP10 트랜잭션 안 EXECUTE AS/REVERT.
- **추정으로 남긴 것:** H2·H3·H5는 관측이지 문서 보장이 아니어서 트리거 귀속 상한 `inferred`와 형제·2차 체인 순서 `observed_only`를 유지한다(설계 §7.1).
- **확인(2026-11-12):** SP8 AW DM1·DM2·DM3 실제 전파([spikes/SP8.md](spikes/SP8.md)).
- **측정 전:** SP9(측정 화면 준비, 참고값 1회, 브라우저 창이 보이는 상태에서 다시 재야 함).
- **준비됨, 미실행(2026-11-13):** SP11(트랜잭션 제어 프로시저), SP12(깊이 신호), SP13(`EXEC @rc` 래퍼), SP14(실행 사용자 권한), SP16(`@@TRANCOUNT` 분기), SP17(Split 끝까지), SP18(스냅샷 실패 오류 번호), SP20(고아 dbo), SP21(KEYSET_HASH). 결과 칸이 모두 비어 있다.
- **정답 정정:** §3 마지막 항목.

## 8. 타당성 위협

1. **정의 차이:** §4 "알려진 정의 차이" 일곱 항목. 특히 B0의 M3 precision 1.0과 B2의 M1·M2 1.0은 구성의 결과이고, M5 오버헤드는 실행 봉투 비용을 포함한다.
2. **KeyFolding 근사:** 오라클 키 비교를 DB 정렬 규칙에 맞추는 규칙(`eval/DbRipple.Eval/KeyFolding.cs`)은 근사다. 뒤 공백 무시, `_CI`면 대문자, `_AI`면 악센트 제거만 하고 폭·가나 구분(`_WS`·`_KS`)은 다루지 않는다. 그런 정렬 규칙의 키가 있으면 M1·M2 키 비교가 틀릴 수 있다. CascadeLab·AW의 정렬 규칙은 `SQL_Latin1_General_CP1_CI_AS`다(§2.3).
3. **IDENTITY 순서 가정:** 하네스와 [적용] 모두 비결정 열을 뺀 값이 같은 새 IDENTITY 행 여러 개를 IDENTITY 값 오름차순으로 짝짓는다(같은 문장이 같은 순서로 IDENTITY를 매긴다는 가정). 두 구현은 코드를 공유하지 않지만 같은 가정에 기대므로, 가정이 깨지면 함께 틀릴 수 있다.
4. **롤백 뒤 남은 변경 검사의 오염:** [적용]이 실패했을 때의 "롤백 뒤 남은 변경" 검사는 잠금이 풀린 원본을 트랜잭션 없이 원본 스냅샷과 비교한다. 그 사이 다른 세션이 원본에 커밋한 변경이 섞여 트리거가 남긴 변경처럼 보일 수 있다(추정, 3주차 독립 검토, 실측 전). 하네스는 전용 DB를 쓰므로 지표에는 영향이 적지만, [적용] 결과의 `residualCommitted`를 읽을 때는 이 한계를 함께 본다.
5. **단일 PC 측정:** 모든 측정은 PC 한 대의 Docker Desktop(WSL2) Linux 컨테이너 하나(메모리 4GB)에서 한다. 샌드박스·스냅샷·오라클 사본이 같은 인스턴스에 있어 자원을 나눠 쓴다. 시간 값(M5, SP7)은 이 구성에서만 뜻이 있고, 다른 하드웨어나 분리 배치에서는 달라진다. Microsoft 지원 문구는 "Linux 호스트"라 이 구성은 개발·평가용이다(설계 §3.2, A17).
6. **합성 정답 DB:** CascadeLab은 패턴마다 작은 시드다. 실제 업무 DB의 트리거 본문 복잡도와 데이터 분포를 대표하지 않는다. AW에는 정답 집합이 없어 시연으로만 쓴다.
7. **손 정답 작성자와 구현자가 같은 설계를 읽었다:** 문서 해석 오류가 정답과 제품에 함께 들어갈 수 있다. 오라클(코드 독립), 원문 배치 경로, 비교기 자체 검증(`eval/DbRipple.Eval.Tests/ComparerSelfTests.cs`)으로 줄인다.
8. **반복 20회는 독립 표본 20개가 아니다(2026-11-12 실행에서 새로 봄):** 20회의 사례별 지표가 모두 같았다(§6.1). 스위트 분모(셀 3520 등)는 같은 사실 176개를 20번 센 것이라 신뢰구간처럼 읽으면 안 된다. 또 한 프로세스가 한 컨테이너에서 사례를 차례로 돌린 결과라, 동시 부하·서버 재시작·실행 계획 변화 아래의 안정성은 재지 않았다.
9. **M4가 가려낸 범위가 좁다(새로 봄):** observed_only 간선이 있는 사례는 30개 중 4개(CL09·CL10·CL13·CL19), 간선 13개뿐이다. 나머지 사례의 변동률 0은 측정 결과가 아니라 구조상 0이다. "순서 안정 30/30"은 이 범위 안의 결과다.
10. **B2의 M1·M2 1.0은 구성상 값이다(새로 봄):** B2 예측은 오라클 순 변경 그대로라(§5), 같은 오라클과 비교한 M1·M2가 1.0인 것은 당연하다. B0·B2의 상태 일치 1.0도 상태를 오라클에서 가져온 결과다. 대조군에서 뜻이 있는 비교는 B0의 M1·M2 recall, B0·B2의 M3 recall과 작업량이다.
11. **ambiguous precision은 후보 수에 묶인다(새로 봄):** ambiguous 간선의 precision은 후보 수 k에 대해 1/k가 상한이다(§6.1 M3). 확신도 all의 precision 0.9358도 이 후보 간선 때문이다. ambiguous 귀속의 품질은 후보 포함률(1.0000, 4/4묶음)로 읽는다. 후보 포함률의 분모는 1회분 4묶음(CL10 1, CL13 3)뿐이다.
12. **leavesResidue 사례의 반복(새로 봄):** 하네스는 사례 사이 초기화를 디지스트로 확인만 하고, 같은 사례의 반복 사이에 원본을 되돌리지 않는다. CL12a는 반복마다 `ErrLog` 행이 원본에 남아 r2~r20의 오라클 시작 상태가 시드와 달랐다(`differences.txt` 19줄). 이번에는 20회 모두 상태·키·M1~M3 값이 같아 영향이 없었지만, 남은 행이 다음 반복 결과를 바꾸는 사례가 생기면 반복 지표가 오염된다. 미리보기 쪽 샌드박스는 이 남은 행을 모른 채(기준선은 실행 시작 때 한 번) 돌기 때문에 미리보기와 오라클의 시작 상태도 r2부터 다르다.
13. **E_cap의 재조회 셀 제외:** CL19 `text` 열처럼 감사 기록에 값이 없는 셀은 제품이 재조회로 채우고, 하네스는 그 셀을 E_cap 예측에서 뺀다. E_cap 0.9943은 수집 오류가 아니라 감사 기록이 담지 못하는 열의 비율로 읽는다.
14. **M5 total에 들지 않는 시간(새로 봄):** M5 total은 제품 `timingsMs` 7단계의 합이다. 샌드박스 잠금 획득과 오염 시 재구축, 기준선 문맥 읽기, 결과 저장(`PreviewStore`), HTTP 전송, 브라우저 렌더는 들지 않는다(`src/DbRipple.Preview/PreviewPipeline.cs`). 사용자가 겪는 시간은 이보다 길다. 표본은 크기마다 5회라 p95는 4·5번째 값 사이 보간이고, 10만 행은 따로 실행했다(같은 설정, 다른 전용 DB).
15. **M5 규모 사례가 둘뿐이다:** CL20(3단 연쇄 DELETE)·CL20b(UPDATE) 두 틀만 쟀다. 5만 행부터 KEYS 모드라 행 값 화면이 없는 상태의 시간이다. 귀속(resolve)이 선형보다 빨리 느는 것(§6.2)이 다른 그래프 모양에서도 같은지는 재지 않았다.
16. **EXEC 정답이 추정에 기댄다(2026-11-13):** CL21~CL25의 손 정답은 SP11~SP16으로 확인할 트랜잭션·깊이·권한 동작의 추정(봉투 안 BEGIN/COMMIT은 중첩이라 확정하지 않는다, CATCH ROLLBACK 뒤에는 266, 프로시저 안 DML의 감사 트리거 깊이는 1)에 기댄다. DB로 확인하기 전까지는 정답이 틀릴 수 있고, 7번(정답 작성자와 구현자가 같은 설계를 읽음)의 위험이 EXEC에서 더 크다.
17. **거부 사례는 지표에 들어가지 않는다(2026-11-13):** CL25a~d는 M1~M3·대조군에서 빠진다. 거부 규칙의 정확도는 지표가 아니라 (규칙 ID, 문장 번호) 일치 여부로만 본다(`rejects.json`, `differences.txt`).

## 9. 재현 방법

준비는 루트 [README.md](../README.md) "실행 방법" 1~3과 같다(컨테이너, `.env`, AdventureWorks 복원). 평가 명령은 §6.3에 있다(EXEC 사례 포함 실행은 `scripts\home-test.ps1 -IncludeExec`). 하네스는 공유 DB(CascadeLab*, AW_*)를 쓰지 않고 `dbrt_eval_*` 전용 DB를 만들고 끝에 지운다(`--keep-dbs`로 남길 수 있다). 결과 폴더에는 CSV, `env.json`, `differences.txt`, 미리보기 결과 JSON(`records/`)이 남는다.
