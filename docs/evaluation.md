# 평가 보고서

> 상태: 틀(2026-11-11). **측정한 결과는 2주차 CascadeLab CL01~CL08b의 M1·M2와 스파이크(SP0~SP7, SP10)뿐이다.** 나머지 칸은 "측정 전"으로 두고 채울 명령을 적었다. 수치는 CSV와 로그에서만 옮기고 손으로 만든 값은 넣지 않는다(설계 §10.6).
> 설계: [architecture.md](architecture.md) v1.5 §10 · 결정: [week3-plan.md](week3-plan.md) · 지표 열 정의: `eval/DbRipple.Eval/MetricsCsv.cs` 주석 · 자동 생성 보고서: `report` 명령이 실행 폴더에 쓰는 `report.md`(`eval/DbRipple.Eval/ReportBuilder.cs`)

이 문서는 사람이 읽는 보고서다. 실행마다 생기는 `report.md`는 CSV 표를 그대로 옮긴 부록이고, 이 문서의 §6 결과 표는 그 `report.md`와 CSV에서 옮겨 채운다.

## 1. 주장과 지표

| 주장 | 지표 | 상태 |
|---|---|---|
| 미리보기가 실제 실행과 같다 | M1 일치율 | CL01~CL08b 측정함(§6.1), CL09~CL20 측정 전 |
| 연쇄·트리거 변경까지 찾는다 | M2 탐지 | CL01~CL08b 측정함(§6.1), CL09~CL20 측정 전 |
| 원인과 순서를 맞게 귀속한다(신규성의 근거) | M3 귀속 정확도, M4 순서 안정성 | 측정 전 |
| 쓸 만한 시간 안에 끝난다 | M5 응답 시간 | 측정 전(SP7만 측정함) |

신규성 주장은 M3·M4에 둔다. 가장 강한 반론인 "사본 + 최종 차이"(B2)를 같은 사례·같은 지표로 비교한다(§5).

## 2. 데이터셋

### 2.1 CascadeLab (합성 정답 DB, 저장소에 포함)

공개 샘플에 없는 연쇄 패턴을 담은 합성 DB다. 사례마다 손 정답(변경 키 집합, 간선, 기대 오류)을 JSON으로 썼다. 형식은 answer-format v0.2([cascadelab/answer-format.md](../cascadelab/answer-format.md))다. 사례 파일은 변형을 포함해 30개다(`cascadelab/cases/`). 사례별 문장과 기대값은 [cascadelab/README.md](../cascadelab/README.md) "사례 요약"에 있다.

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
| CL09 | CL09 | 트리거 DELETE가 2차 CASCADE로 같은 자식에 다시 닿음(H4·H5) | 실행 전 |
| CL10 | CL10 | 3단 CASCADE + SET NULL + 동적 SQL 트리거(DM4) | 실행 전 |
| CL11 | CL11, CL11b | INSTEAD OF INSERT·UPDATE | 실행 전 |
| CL12 | CL12a, CL12b, CL12c | ROLLBACK 트리거와 롤백 뒤 남은 변경, `ROWCOUNT_BIG()` 0행 가드, @@IDENTITY를 읽는 트리거 | 실행 전 |
| CL13 | CL13 | AFTER 트리거 3개, 사용자 Last, 감사 순서 NONE | 실행 전 |
| CL14 | CL14, CL14b | 힙(PK 없음, 중복 행) | 실행 전 |
| CL15 | CL15, CL15b | PK를 바꾸는 다중 행 UPDATE(UNPAIRED, BY_UNIQUE) | 실행 전 |
| CL16 | CL16, CL16b | IDENTITY·SEQUENCE·SYSDATETIME·NEWID, 새 IDENTITY를 참조하는 자식 | 실행 전 |
| CL17 | CL17 | 앞 문장 결과에 의존하는 다중 문장 | 실행 전 |
| CL18 | CL18 | NO ACTION 위반(547)과 not_run | 실행 전 |
| CL19 | CL19 | xml·hierarchyid·geography·text 열 | 실행 전 |
| CL20 | CL20, CL20b | 행 수 생성기(`cl20.usp_Generate @parents = N`), M5 DELETE·UPDATE 틀 | 실행 전 |

CL01~CL08b의 대조 기록은 [cascadelab/verification/report-2026-11-10.md](../cascadelab/verification/report-2026-11-10.md)다. CL09~CL20은 정적 검토만 했다.

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

2주차 실행의 `env.json`(`logs/week2/eval/eval-20261110-135700/env.json`) 기준이다.

| 항목 | 값 |
|---|---|
| SQL Server | 17.0.5035.5, CU10, Enterprise Developer Edition (64-bit), Linux 컨테이너 1개 |
| 서버 설정 | nested triggers 1, disallow results from triggers 0, 세션 옵션 5432 |
| 컨테이너 메모리 | 물리 4,194,304 KB, CPU 16 |
| DB | 호환성 수준 170, 정렬 규칙 SQL_Latin1_General_CP1_CI_AS, RECURSIVE_TRIGGERS OFF(CL08b만 ON) |
| 실행 사용자 | `dbr_preview_user` |

## 3. 정답과 오라클

- **1차 정답:** CascadeLab 손 정답(변경 키 집합, 간선, 기대 오류)이다. 셀 값은 오라클 결과를 사람이 검토해 확정한다.
- **2차 정답(오라클):** 수집 장치가 없는 전용 원본 사본에서 같은 문장을 실행하고 롤백한 최종 차이다. 실행 전후 행별 SHA2_256 해시로 바뀐 행을 찾고 그 행만 셀 단위로 비교한다(`eval/DbRipple.Eval/HashSnapshotComparer.cs`, `OracleRunner.cs`).
- **독립성:** 평가 하네스 핵심(`eval/DbRipple.Eval`)은 제품 컴포넌트를 참조하지 않는다. 제품 최종 차이 계산기(C12), [적용]의 IDENTITY 짝 매핑(C15)과 코드를 공유하지 않는다.
- **원문 배치 경로:** 오라클은 문장별 실행과 함께 제품 분석기를 거치지 않은 원문 배치도 실행해 대조한다(`raw_batch_match`).
- **평가 훅:** 제품 실행기가 마지막 문장과 REVERT 뒤, ROLLBACK 전에 하네스 콜백을 같은 연결로 부른다. 하네스는 자기 비교기로 샌드박스 순 변경을 기록한다. 이 값이 E_obs·E_cap의 기준이다.
- **사례 사이 초기화:** 사례마다 오라클 시작 상태가 시드 디지스트와 같은지 확인한다. 롤백 뒤에도 행이 남는 사례(`leavesResidue`, CL12a)는 맨 뒤에 돈다.
- **정답 정정:** 손 정답과 오라클이 다르면 `differences.txt`에 후보로 남기고, 원인(문서 해석 오류인지 정답 오류인지)을 적은 뒤 고친다. 지금까지의 정정은 `firings[].pairing` 6건이다(CL07·CL08a·CL08b, 정답 오류, M1·M2 영향 없음, [cascadelab/README.md](../cascadelab/README.md) "정정").

## 4. 지표 정의 (평가 하네스 코드 기준)

열 명세는 `eval/DbRipple.Eval/MetricsCsv.cs` 주석이 기준이다. 아래는 그 요약이다. CSV 공통 규칙: UTF-8(BOM 없음), RFC 4180, 소수 4자리, 분모가 0인 비율은 빈 칸.

| 지표 | 파일 | 단위와 계산 |
|---|---|---|
| M1 일치율 | `m1.csv` | 셀 사실 (테이블, 키, 열, 값). precision·recall·F1과 행 완전 일치율. 비결정 셀은 tp·fp·fn에서 빼고 존재·형식 일치율로 따로 센다. 비교는 셋이다: `total` = 미리보기 대 오라클(E_total), `obs` = 평가 훅 대 오라클(E_obs, 관찰자 효과), `cap` = 미리보기 재구성 대 평가 훅(E_cap, 수집·재구성 오류). `residual_fill_rate` = 잔차·재조회로만 설명돼 E_cap 예측에서 뺀 셀 비율. `identity_rows`·`identity_paired` = 정답 쪽 새 IDENTITY 행 수와 짝지은 수(3주차 추가, `IdentityPairing.cs`) |
| M2 탐지 | `m2.csv` | (테이블, 키)당 한 번. 원인(DIRECT, FK_CASCADE, FK_SET_NULL, FK_SET_DEFAULT, TRIGGER, INSTEAD_OF, SAME_TABLE_TRIGGER, ONE_OF)별 재현율. 정답은 손 정답 changes ∪ residualCommitted, 예측은 NetChanges ∪ ResidualCommitted |
| M3 귀속 정확도 | `m3.csv` | 간선 키 (문장, 부모, 자식, 종류, 이름). 확신도(certain, inferred, ambiguous, unexplained, all)별 precision = 맞힌 예측 간선 ÷ 예측 간선, recall = 찾은 정답 간선 ÷ 정답 간선(확신도 무관). ambiguous 후보 포함률 = 정답 간선이 든 후보 집합 ÷ 후보 집합((문장, 자식, 종류)로 묶음). certain 과신 = 정답에 없는 certain 간선 + 정답 확신도가 더 약한 certain 간선(목표 0) |
| M4 순서 안정성 | `m4.csv` | 같은 사례를 k회 반복. 형제 순서를 뺀 정규 그래프 서명이 한 가지면 안정. observed_only 변동률 = (k − 가장 흔한 observed_only 서명 수) ÷ k. 파이프라인 예외 반복은 분모에서 빼고 `error_runs`로 따로 센다. 거부는 결정적이라 유효 반복이다 |
| M5 응답 시간 | `m5.csv` | CL20·CL20b, N = 직접 대상 행 수. 워밍업 1회 뒤 5회(기본값), 단계별 p50·p95(선형 보간). 단계: analyze, execute, read_log, reread, net_diff, resolve, serialize, total, oracle_execute. 수집 오버헤드 = execute p50 ÷ oracle_execute p50. 브라우저 첫 그래프·첫 그리드 열은 빈 칸이고 SP9 값을 손으로 옮긴다 |

스위트 집계(`metrics.csv`)는 마이크로 평균이다. 사례 상태 일치(`status_match_rate`)도 함께 낸다.

**알려진 정의 차이(2026-11-11 검토, 결과를 읽을 때 함께 볼 것):**

1. B0의 M3 예측 간선은 손 정답 DIRECT 간선 그대로라 B0 precision은 **구성상 1.0**이다. 비교할 값은 recall이다.
2. B0 필터는 테이블 단위라, 직접 대상 테이블에서 같은 테이블 트리거가 바꾼 행도 B0이 찾은 것으로 센다(OUTPUT 기반 도구보다 후하게 잰다).
3. 직접 대상은 문장 정규식(스키마로 한정한 대상)과 손 정답 DIRECT 행 테이블의 합집합이다. `DELETE TOP (n) FROM s.t`는 정규식이 못 잡아 손 정답 DIRECT 행에만 기댄다.
4. M5 오버헤드의 분자(샌드박스 execute)는 실행 봉투 비용(BEGIN TRAN, 제어 UPDATE, 경계 읽기, EXECUTE AS/REVERT)을 포함하고, 분모(오라클 execute)는 문장 실행만 잰다. 작은 N에서는 비율이 순수 수집 비용보다 크게 나온다.
5. M4는 예외 반복을 안정성 분모에서 뺀다. 예외 반복이 많으면 `M4.error_run_rate`를 함께 봐야 한다.

## 5. 대조군

근거: `eval/DbRipple.Eval/Baselines.cs`. 대조군 행은 제품과 같은 CSV에 `system` 열 `B0`·`B2`로 쓴다. 상태는 둘 다 오라클 상태다. 평가 훅이 없으므로 M1은 `total`만 채운다.

| ID | 정의 | 기대(추정) |
|---|---|---|
| B0 | 직접 대상만 본다(OUTPUT 기반 도구 모사). 오라클 순 변경을 문장의 직접 대상 테이블로 거른 것을 예측으로 쓴다. M3 예측 간선은 손 정답 DIRECT 간선(certain)뿐이다 | 연쇄·트리거 재현율(M2)이 낮을 것이다 |
| B2 | 사본 + 최종 차이(Data Compare 자동화판). 오라클 순 변경을 그대로 예측으로 쓴다. M3 예측 간선은 0개다 | M1·M2는 제품과 비슷하고 M3 recall은 0이다. 작업량 `manual_select_tables`(직접 대상이 아닌데 바뀐 테이블 수)를 함께 본다 |
| B1 | FK 메타데이터 정적 예측 | 구현하지 않았다(자를 목록 1순위) |

## 6. 결과

### 6.1 측정한 것

**CascadeLab CL01~CL08b, 2026-11-10, 실행 `eval-20261110-135700`, 반복 1회, 제품(preview)만.** 출처: `logs/week2/eval/eval-20261110-135700/metrics.csv`, `cases.csv`. 이 실행은 2주차 하네스(answer-format v0.1 사례, IDENTITY 짝 매핑·KeyFolding 추가 전)로 쟀다. 이 사례들에는 새 IDENTITY 행과 대소문자만 바꾸는 키가 없다.

M1 일치율(셀 단위):

| 비교 | 사례 수 | precision | recall | F1 | 행 완전 일치 | 비결정 셀 존재·형식 | 잔차로 메운 셀 |
|---|---|---|---|---|---|---|---|
| total(E_total) | 11 | 1.0000 (54/54) | 1.0000 (54/54) | 1.0000 | 1.0000 (45/45) | — (0/0) | 0.0000 (0/54) |
| obs(E_obs) | 10 | 1.0000 (54/54) | 1.0000 (54/54) | 1.0000 | 1.0000 (45/45) | — (0/0) | (집계 없음) |
| cap(E_cap) | 10 | 1.0000 (54/54) | 1.0000 (54/54) | 1.0000 | 1.0000 (45/45) | — (0/0) | 0.0000 (0/54) |

- obs·cap의 사례 수가 10인 것은 실패 사례 CL03b(547, 변경 0)에 평가 훅 값이 없기 때문이다.
- 이 사례들에는 비결정 셀이 없어 존재·형식 일치율은 분모가 0이다.

M2 탐지((테이블, 키)당 한 번):

| 원인 | 찾음 / 정답 | 재현율 | 해당 사례 수 |
|---|---|---|---|
| DIRECT | 14/14 | 1.0000 | 10 |
| FK_CASCADE | 20/20 | 1.0000 | 5 |
| FK_SET_NULL | 2/2 | 1.0000 | 1 |
| FK_SET_DEFAULT | 2/2 | 1.0000 | 1 |
| TRIGGER | 9/9 | 1.0000 | 4 |
| SAME_TABLE_TRIGGER | 0/0 | — | 0 |
| 전체(ALL) | 45/45 | 1.0000 | 10 |

원인별 행은 한 (테이블, 키)가 원인 여럿에 들 수 있어 합이 전체와 같지 않다.

사례 단위: 상태 일치 11/11, 오라클 대 손 정답 키 일치 11/11, 원문 배치 일치 11/11, `differences.txt` 0바이트.

**참고(평가 하네스 밖):** 2주차 회귀 테스트 픽스처에서 CL01~CL08의 M3 키와 확신도가 손 정답 간선 40/40과 같았고 certain 과신은 0이었다([week2-plan.md](week2-plan.md) §0). 하네스의 M3(`m3.csv`)로 잰 값은 아니다.

**SP7, 열린 트랜잭션 안 최종 차이(2026-11-10, AW 71개 테이블 760,598행).** 출처: [spikes/SP7.md](spikes/SP7.md), `logs/week2/sp7-aw-netdiff.log`.

| 항목 | 첫 실행 | C12 수정 뒤 다시 실행 |
|---|---|---|
| 독립 확인 | 10/10 일치 | 10/10 일치 |
| 열린 트랜잭션 안 | p50 1,553ms, p95 2,028ms | p50 1,652ms, p95 3,697ms |
| 롤백 뒤(트랜잭션 없음, 실패 경로) | p50 1,447ms, p95 1,575ms | p50 1,875ms, p95 2,129ms |

5회 측정이라 p95는 최댓값이다. 이 시간은 [적용]의 드리프트 잠금 유지 시간과 실패 경로 검사 시간의 근사로 쓴다(전 테이블 비교 한 번 기준).

### 6.2 측정 전

| 항목 | 채울 표 | 명령(저장소 루트, `eval/DbRipple.Eval.Cli/Program.cs` 옵션) |
|---|---|---|
| M1·M2 CL01~CL20(제품, B0, B2) | M1·M2 표에 system 열을 더한다 | `. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -- cascadelab --systems preview,B0,B2 --repeat 20 --out logs\week4\eval` |
| M1 IDENTITY 짝(CL16) | `M1.identity_pair_rate` | 위와 같음 |
| M3 확신도별 정확도, certain 과신, ambiguous 후보 포함률, B2 작업량 | 측정 전 | 위와 같음(`m3.csv`, `metrics.csv`) |
| M4 순서 안정성(20회) | 측정 전 | 위와 같음(`m4.csv`) |
| M5 응답 시간(N = 1, 100, 1천, 1만, 5만, 10만) | 측정 전 | `. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -- m5 --sizes 1,100,1000,10000,50000,100000 --out logs\week4\m5` |
| 보고서 표 생성 | 측정 전 | `dotnet run --project eval\DbRipple.Eval.Cli -- report --run logs\week4\eval\<실행 id> --m5 logs\week4\m5\<실행 id>` |
| M5 브라우저 첫 그래프·첫 그리드 렌더(SP9) | 측정 전(참고값 1회) | 웹 화면 `/sp9`. 기록은 [spikes/SP9.md](spikes/SP9.md) |
| SP8 AW DM1·DM2·DM3 실제 전파 | 측정 전 | `.\scripts\run-sql.ps1 spikes\sql\sp8-dm2-pick-order.sql -Database AW_Origin -Log spikes\logs\sp8-dm2.log` 등(읽기 전용) |
| [적용] 검증 게이트(AW DM1~DM3 committed, DM7 drift_detected) | 측정 전 | Host 실행 뒤 `scripts\demo\demo.http`, DM7은 `scripts\demo\dm7-drift.sql` 뒤 [적용] |
| 누적 구성(초기화 없이 연속 실행, IDENTITY 벌어짐) | 측정 전 | 설계 §10.3 "구성". 하네스 옵션 없음(방법 미정) |

M5에서 10만 행이 컨테이너 메모리 4GB로 너무 느리면 자를 목록 3번(5만 행까지)을 쓴다(week3-plan.md §8 위험 9).

## 7. 확인한 사실과 추정

스파이크 결과는 [spikes/README.md](spikes/README.md) 표에 모았다. 요약:

- **확인(DB 실측):** SP0 에디션·스냅샷, SP1 전제 H1~H5 성립(H3은 3회 관측), SP2 관찰자 효과 점검 (a)(c)(d)(e) 성립((b)는 하네스의 E_obs로 대신 잰다), SP3 자기 테이블 재갱신이 감사 묶음으로 잡힘, SP5 기준선 시간, SP6 형식 변환(규칙 수정), SP7 열린 트랜잭션 안 최종 차이, SP10 트랜잭션 안 EXECUTE AS/REVERT.
- **추정으로 남긴 것:** H2·H3·H5는 관측이지 문서 보장이 아니어서 트리거 귀속 상한 `inferred`와 형제·2차 체인 순서 `observed_only`를 유지한다(설계 §7.1).
- **측정 전:** SP8(스크립트 준비), SP9(측정 화면 준비, 참고값 1회).
- **정답 정정:** §3 마지막 항목.

## 8. 타당성 위협

1. **정의 차이:** §4 "알려진 정의 차이" 다섯 항목. 특히 B0의 M3 precision 1.0은 구성의 결과이고, M5 오버헤드는 실행 봉투 비용을 포함한다.
2. **KeyFolding 근사:** 오라클 키 비교를 DB 정렬 규칙에 맞추는 규칙(`eval/DbRipple.Eval/KeyFolding.cs`)은 근사다. 뒤 공백 무시, `_CI`면 대문자, `_AI`면 악센트 제거만 하고 폭·가나 구분(`_WS`·`_KS`)은 다루지 않는다. 그런 정렬 규칙의 키가 있으면 M1·M2 키 비교가 틀릴 수 있다. CascadeLab·AW의 정렬 규칙은 `SQL_Latin1_General_CP1_CI_AS`다(§2.3).
3. **IDENTITY 순서 가정:** 하네스와 [적용] 모두 비결정 열을 뺀 값이 같은 새 IDENTITY 행 여러 개를 IDENTITY 값 오름차순으로 짝짓는다(같은 문장이 같은 순서로 IDENTITY를 매긴다는 가정). 두 구현은 코드를 공유하지 않지만 같은 가정에 기대므로, 가정이 깨지면 함께 틀릴 수 있다.
4. **롤백 뒤 남은 변경 검사의 오염:** [적용]이 실패했을 때의 "롤백 뒤 남은 변경" 검사는 잠금이 풀린 원본을 트랜잭션 없이 원본 스냅샷과 비교한다. 그 사이 다른 세션이 원본에 커밋한 변경이 섞여 트리거가 남긴 변경처럼 보일 수 있다(추정, 3주차 독립 검토, 실측 전). 하네스는 전용 DB를 쓰므로 지표에는 영향이 적지만, [적용] 결과의 `residualCommitted`를 읽을 때는 이 한계를 함께 본다.
5. **단일 PC 측정:** 모든 측정은 PC 한 대의 Docker Desktop(WSL2) Linux 컨테이너 하나(메모리 4GB)에서 한다. 샌드박스·스냅샷·오라클 사본이 같은 인스턴스에 있어 자원을 나눠 쓴다. 시간 값(M5, SP7)은 이 구성에서만 뜻이 있고, 다른 하드웨어나 분리 배치에서는 달라진다. Microsoft 지원 문구는 "Linux 호스트"라 이 구성은 개발·평가용이다(설계 §3.2, A17).
6. **합성 정답 DB:** CascadeLab은 패턴마다 작은 시드다. 실제 업무 DB의 트리거 본문 복잡도와 데이터 분포를 대표하지 않는다. AW에는 정답 집합이 없어 시연으로만 쓴다.
7. **손 정답 작성자와 구현자가 같은 설계를 읽었다:** 문서 해석 오류가 정답과 제품에 함께 들어갈 수 있다. 오라클(코드 독립), 원문 배치 경로, 비교기 자체 검증(`eval/DbRipple.Eval.Tests/ComparerSelfTests.cs`)으로 줄인다.
8. **반복 수:** 2주차 결과는 반복 1회다. 순서 안정성(M4)은 아직 재지 않았다.

## 9. 재현 방법

준비는 루트 [README.md](../README.md) "실행 방법" 1~3과 같다(컨테이너, `.env`, AdventureWorks 복원). 평가 명령은 §6.2 표에 있다. 하네스는 공유 DB(CascadeLab*, AW_*)를 쓰지 않고 `dbrt_eval_*` 전용 DB를 만들고 끝에 지운다(`--keep-dbs`로 남길 수 있다). 결과 폴더에는 CSV, `env.json`, `differences.txt`, 미리보기 결과 JSON(`records/`)이 남는다.
