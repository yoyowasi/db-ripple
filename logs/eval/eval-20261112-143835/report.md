# 평가 보고서 — eval-20261112-143835, m5-20261112-145013, m5-20261112-145801

이 보고서의 수치는 아래 CSV에서만 옮겼다(손으로 쓴 수치 없음, 설계 §10.6). 열 정의는 `eval/DbRipple.Eval/MetricsCsv.cs` 주석에 있다.

- `cases.csv`: 1800행
- `metrics.csv`: 103행
- `m4.csv`: 30행
- `m5.csv`: 12행 (`../../eval-m5/m5-20261112-145013, ../../eval-m5/m5-20261112-145801`)

## 지표 정의

- M1은 (테이블, 키, 열) 셀 단위다. 비결정 셀은 존재·형식만 보고, IDENTITY는 짝 매핑 뒤 비교한다. M2는 (테이블, 키)당 한 번 센다.
- M3는 간선 키(문장, 부모, 자식, 종류, 이름) 단위이고 확신도별로 나눈다. certain 과신은 정답에 없는 certain 간선과 정답 확신도가 더 약한 certain 간선의 합이다.
- B0의 M3 예측 간선은 손 정답 DIRECT 간선 그대로라 B0 precision은 구성상 1.0이다(비교할 값은 recall).
- B0 필터는 테이블 단위라 직접 대상 테이블에서 같은 테이블 트리거가 바꾼 행도 B0 탐지로 센다(OUTPUT 기반 도구보다 후하게 잰다).
- 직접 대상은 문장 정규식(스키마로 한정한 대상)과 손 정답 DIRECT 행 테이블의 합집합이다. `DELETE TOP (n) FROM s.t`는 정규식이 못 잡아 손 정답 DIRECT 행에만 기댄다.
- M4는 예외(error) 반복을 안정성 분모에서 빼고 따로 센다. 거부(rejected)는 결정적이라 그대로 센다.
- M5 오버헤드의 분자(샌드박스 execute)는 실행 봉투 비용(BEGIN TRAN, 제어 UPDATE, 경계 읽기, EXECUTE AS/REVERT)을 포함하고, 분모(오라클 execute)는 문장 실행만 잰다.

## 사례 요약

행 = (사례, 반복). 오라클 대 손 정답이 다르면 정답 정정 기록 후보다(differences.txt).

| system | 사례 | 행 | 상태 일치 | 오라클 대 손 정답 일치 | 원문 배치 일치 |
|---|---|---|---|---|---|
| B0 | 30 | 600 | 600/600 | 600/600 | 600/600 |
| B2 | 30 | 600 | 600/600 | 600/600 | 600/600 |
| preview | 30 | 600 | 600/600 | 600/600 | 600/600 |

## M1 일치율(셀 단위, 마이크로 평균)

comparison: total = 미리보기 대 오라클(E_total), obs = 평가 훅 대 오라클(E_obs), cap = 미리보기 재구성 대 평가 훅(E_cap).

| system | comparison | precision | recall | f1 | 행 완전 일치 | 비결정 셀 존재·형식 | 잔차로 메운 셀 | IDENTITY 짝 |
|---|---|---|---|---|---|---|---|---|
| B0 | total | 1.0000 (1340/1340) | 0.3807 (1340/3520) | 0.5514 (2680/4860) | 0.3761 (880/2340) | 0.6250 (200/320) |  | 0.4286 (60/140) |
| B2 | total | 1.0000 (3520/3520) | 1.0000 (3520/3520) | 1.0000 (7040/7040) | 1.0000 (2340/2340) | 1.0000 (320/320) |  | 1.0000 (140/140) |
| preview | cap | 1.0000 (3460/3460) | 0.9943 (3460/3480) | 0.9971 (6920/6940) | 0.9914 (2300/2320) | 1.0000 (300/300) | 0.0057 (20/3480) | 1.0000 (120/120) |
| preview | obs | 1.0000 (3480/3480) | 1.0000 (3480/3480) | 1.0000 (6960/6960) | 1.0000 (2320/2320) | 1.0000 (300/300) |  | 1.0000 (120/120) |
| preview | total | 1.0000 (3520/3520) | 1.0000 (3520/3520) | 1.0000 (7040/7040) | 1.0000 (2340/2340) | 1.0000 (320/320) | 0.0057 (20/3520) | 1.0000 (140/140) |

## M2 탐지(원인별 재현율, (테이블, 키)당 한 번)

| system | cause | recall |
|---|---|---|
| B0 | DIRECT | 1.0000 (800/800) |
| B0 | FK_CASCADE | 0.0000 (0/740) |
| B0 | FK_SET_NULL | 0.0000 (0/80) |
| B0 | FK_SET_DEFAULT | 0.0000 (0/40) |
| B0 | TRIGGER | 0.0690 (40/580) |
| B0 | INSTEAD_OF | 0.4000 (40/100) |
| B0 | SAME_TABLE_TRIGGER | — (0/0) |
| B0 | ONE_OF | — (0/0) |
| B0 | ALL | 0.3652 (840/2300) |
| B2 | DIRECT | 1.0000 (800/800) |
| B2 | FK_CASCADE | 1.0000 (740/740) |
| B2 | FK_SET_NULL | 1.0000 (80/80) |
| B2 | FK_SET_DEFAULT | 1.0000 (40/40) |
| B2 | TRIGGER | 1.0000 (580/580) |
| B2 | INSTEAD_OF | 1.0000 (100/100) |
| B2 | SAME_TABLE_TRIGGER | — (0/0) |
| B2 | ONE_OF | — (0/0) |
| B2 | ALL | 1.0000 (2300/2300) |
| preview | DIRECT | 1.0000 (800/800) |
| preview | FK_CASCADE | 1.0000 (740/740) |
| preview | FK_SET_NULL | 1.0000 (80/80) |
| preview | FK_SET_DEFAULT | 1.0000 (40/40) |
| preview | TRIGGER | 1.0000 (580/580) |
| preview | INSTEAD_OF | 1.0000 (100/100) |
| preview | SAME_TABLE_TRIGGER | — (0/0) |
| preview | ONE_OF | — (0/0) |
| preview | ALL | 1.0000 (2300/2300) |

## M3 귀속 정확도(간선 키, 확신도별)

ambiguous 후보 포함률은 정답 간선이 후보 집합에 든 비율, certain 과신은 목표 0. 작업량은 대조군의 직접 찾아 SELECT할 테이블 수(사례·반복 평균).

| system | 확신도 | precision | recall | ambiguous 후보 포함 | certain 과신 | 작업량(테이블) |
|---|---|---|---|---|---|---|
| B0 | certain | 1.0000 (560/560) | 0.5957 (560/940) |  | 0 / 560 |  |
| B0 | inferred | — (0/0) | 0.0000 (0/1020) |  |  |  |
| B0 | ambiguous | — (0/0) | 0.0000 (0/80) | — (0/0) |  |  |
| B0 | unexplained | — (0/0) | — (0/0) |  |  |  |
| B0 | all | 1.0000 (560/560) | 0.2745 (560/2040) |  |  | 1.2333 (740/600) |
| B2 | certain | — (0/0) | 0.0000 (0/940) |  | 0 / 0 |  |
| B2 | inferred | — (0/0) | 0.0000 (0/1020) |  |  |  |
| B2 | ambiguous | — (0/0) | 0.0000 (0/80) | — (0/0) |  |  |
| B2 | unexplained | — (0/0) | — (0/0) |  |  |  |
| B2 | all | — (0/0) | 0.0000 (0/2040) |  |  | 1.2333 (740/600) |
| preview | certain | 1.0000 (940/940) | 1.0000 (940/940) |  | 0 / 940 |  |
| preview | inferred | 1.0000 (1020/1020) | 1.0000 (1020/1020) |  |  |  |
| preview | ambiguous | 0.3636 (80/220) | 1.0000 (80/80) | 1.0000 (80/80) |  |  |
| preview | unexplained | — (0/0) | — (0/0) |  |  |  |
| preview | all | 0.9358 (2040/2180) | 1.0000 (2040/2040) |  |  |  |

## M4 순서 안정성(스위트)

| system | 범위 | 정규 그래프 안정 사례 | observed_only 변동률 | 예외 반복 |
|---|---|---|---|---|
| preview | case | 1.0000 (30/30) |  |  |
| preview | repeat |  | 0.0000 (0/600) | 0.0000 (0/600) |

## 사례 상태 일치

| system | 범위 | 상태 일치 |
|---|---|---|
| B0 | case | 1.0000 (600/600) |
| B2 | case | 1.0000 (600/600) |
| preview | case | 1.0000 (600/600) |

## M4 사례별

canonical = 형제 순서를 뺀 정규 그래프, observed_only 변동률 = (k − 가장 흔한 서명 수) ÷ k, k = 예외를 뺀 유효 반복.

| system | case | 반복 | 예외 | 상태 가짓수 | 정규 그래프 가짓수 | 안정 | observed_only 간선 | observed_only 변동률 |
|---|---|---|---|---|---|---|---|---|
| preview | CL01 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL02 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL03 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL03b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL04 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL04b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL05 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL06 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL07 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL08a | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL08b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL09 | 20 | 0 | 1 | 1 | true | 1 | 0.0000 |
| preview | CL10 | 20 | 0 | 1 | 1 | true | 2 | 0.0000 |
| preview | CL11 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL11b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL12a | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL12b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL12c | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL13 | 20 | 0 | 1 | 1 | true | 9 | 0.0000 |
| preview | CL14 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL14b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL15 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL15b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL16 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL16b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL17 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL18 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL19 | 20 | 0 | 1 | 1 | true | 1 | 0.0000 |
| preview | CL20 | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |
| preview | CL20b | 20 | 0 | 1 | 1 | true | 0 | 0.0000 |

## M5 응답 시간(ms, 워밍업 제외 p50·p95)

수집 오버헤드 = execute p50 ÷ 오라클 execute p50. 브라우저 열은 SP9 측정값을 손으로 기록한다(Q42, 비어 있으면 —).

| case | 연산 | N | 표본 | 상태 | KEYS(상한 초과) | total p50 | total p95 | execute p50 | oracle p50 | 오버헤드 | 첫 그래프 | 첫 그리드 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CL20 | DELETE | 1 | 5 | ok | false | 18.0 | 22.6 | 8.0 | 1.0 | 8.0000 | — | — |
| CL20 | DELETE | 100 | 5 | ok | false | 75.0 | 98.4 | 33.0 | 5.0 | 6.6000 | — | — |
| CL20 | DELETE | 1000 | 5 | ok | false | 885.0 | 915.2 | 141.0 | 66.0 | 2.1364 | — | — |
| CL20 | DELETE | 10000 | 5 | ok | false | 6096.0 | 7576.6 | 666.0 | 238.0 | 2.7983 | — | — |
| CL20 | DELETE | 50000 | 5 | truncated | true | 43364.0 | 46642.4 | 3372.0 | 1184.0 | 2.8480 | — | — |
| CL20 | DELETE | 100000 | 5 | truncated | true | 105171.0 | 112221.8 | 5717.0 | 2135.0 | 2.6778 | — | — |
| CL20b | UPDATE | 1 | 5 | ok | false | 19.0 | 23.6 | 11.0 | 1.0 | 11.0000 | — | — |
| CL20b | UPDATE | 100 | 5 | ok | false | 36.0 | 41.6 | 26.0 | 2.0 | 13.0000 | — | — |
| CL20b | UPDATE | 1000 | 5 | ok | false | 112.0 | 132.0 | 62.0 | 6.0 | 10.3333 | — | — |
| CL20b | UPDATE | 10000 | 5 | ok | false | 886.0 | 1023.6 | 325.0 | 42.0 | 7.7381 | — | — |
| CL20b | UPDATE | 50000 | 5 | truncated | true | 4747.0 | 5507.8 | 1458.0 | 183.0 | 7.9672 | — | — |
| CL20b | UPDATE | 100000 | 5 | truncated | true | 9774.0 | 10080.2 | 2662.0 | 319.0 | 8.3448 | — | — |

## 사례별 상태(preview)

| case | 반복 | 기대 | 실제 | 일치 | 오류 기대 | 오류 실제 | 오라클 대 손 정답 | 원문 배치 |
|---|---|---|---|---|---|---|---|---|
| CL01 | 1 | ok | ok | true | — | — | true | true |
| CL01 | 2 | ok | ok | true | — | — | true | true |
| CL01 | 3 | ok | ok | true | — | — | true | true |
| CL01 | 4 | ok | ok | true | — | — | true | true |
| CL01 | 5 | ok | ok | true | — | — | true | true |
| CL01 | 6 | ok | ok | true | — | — | true | true |
| CL01 | 7 | ok | ok | true | — | — | true | true |
| CL01 | 8 | ok | ok | true | — | — | true | true |
| CL01 | 9 | ok | ok | true | — | — | true | true |
| CL01 | 10 | ok | ok | true | — | — | true | true |
| CL01 | 11 | ok | ok | true | — | — | true | true |
| CL01 | 12 | ok | ok | true | — | — | true | true |
| CL01 | 13 | ok | ok | true | — | — | true | true |
| CL01 | 14 | ok | ok | true | — | — | true | true |
| CL01 | 15 | ok | ok | true | — | — | true | true |
| CL01 | 16 | ok | ok | true | — | — | true | true |
| CL01 | 17 | ok | ok | true | — | — | true | true |
| CL01 | 18 | ok | ok | true | — | — | true | true |
| CL01 | 19 | ok | ok | true | — | — | true | true |
| CL01 | 20 | ok | ok | true | — | — | true | true |
| CL02 | 1 | ok | ok | true | — | — | true | true |
| CL02 | 2 | ok | ok | true | — | — | true | true |
| CL02 | 3 | ok | ok | true | — | — | true | true |
| CL02 | 4 | ok | ok | true | — | — | true | true |
| CL02 | 5 | ok | ok | true | — | — | true | true |
| CL02 | 6 | ok | ok | true | — | — | true | true |
| CL02 | 7 | ok | ok | true | — | — | true | true |
| CL02 | 8 | ok | ok | true | — | — | true | true |
| CL02 | 9 | ok | ok | true | — | — | true | true |
| CL02 | 10 | ok | ok | true | — | — | true | true |
| CL02 | 11 | ok | ok | true | — | — | true | true |
| CL02 | 12 | ok | ok | true | — | — | true | true |
| CL02 | 13 | ok | ok | true | — | — | true | true |
| CL02 | 14 | ok | ok | true | — | — | true | true |
| CL02 | 15 | ok | ok | true | — | — | true | true |
| CL02 | 16 | ok | ok | true | — | — | true | true |
| CL02 | 17 | ok | ok | true | — | — | true | true |
| CL02 | 18 | ok | ok | true | — | — | true | true |
| CL02 | 19 | ok | ok | true | — | — | true | true |
| CL02 | 20 | ok | ok | true | — | — | true | true |
| CL03 | 1 | ok | ok | true | — | — | true | true |
| CL03 | 2 | ok | ok | true | — | — | true | true |
| CL03 | 3 | ok | ok | true | — | — | true | true |
| CL03 | 4 | ok | ok | true | — | — | true | true |
| CL03 | 5 | ok | ok | true | — | — | true | true |
| CL03 | 6 | ok | ok | true | — | — | true | true |
| CL03 | 7 | ok | ok | true | — | — | true | true |
| CL03 | 8 | ok | ok | true | — | — | true | true |
| CL03 | 9 | ok | ok | true | — | — | true | true |
| CL03 | 10 | ok | ok | true | — | — | true | true |
| CL03 | 11 | ok | ok | true | — | — | true | true |
| CL03 | 12 | ok | ok | true | — | — | true | true |
| CL03 | 13 | ok | ok | true | — | — | true | true |
| CL03 | 14 | ok | ok | true | — | — | true | true |
| CL03 | 15 | ok | ok | true | — | — | true | true |
| CL03 | 16 | ok | ok | true | — | — | true | true |
| CL03 | 17 | ok | ok | true | — | — | true | true |
| CL03 | 18 | ok | ok | true | — | — | true | true |
| CL03 | 19 | ok | ok | true | — | — | true | true |
| CL03 | 20 | ok | ok | true | — | — | true | true |
| CL03b | 1 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 2 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 3 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 4 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 5 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 6 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 7 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 8 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 9 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 10 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 11 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 12 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 13 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 14 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 15 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 16 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 17 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 18 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 19 | failed | failed | true | 547 | 547 | true | true |
| CL03b | 20 | failed | failed | true | 547 | 547 | true | true |
| CL04 | 1 | ok | ok | true | — | — | true | true |
| CL04 | 2 | ok | ok | true | — | — | true | true |
| CL04 | 3 | ok | ok | true | — | — | true | true |
| CL04 | 4 | ok | ok | true | — | — | true | true |
| CL04 | 5 | ok | ok | true | — | — | true | true |
| CL04 | 6 | ok | ok | true | — | — | true | true |
| CL04 | 7 | ok | ok | true | — | — | true | true |
| CL04 | 8 | ok | ok | true | — | — | true | true |
| CL04 | 9 | ok | ok | true | — | — | true | true |
| CL04 | 10 | ok | ok | true | — | — | true | true |
| CL04 | 11 | ok | ok | true | — | — | true | true |
| CL04 | 12 | ok | ok | true | — | — | true | true |
| CL04 | 13 | ok | ok | true | — | — | true | true |
| CL04 | 14 | ok | ok | true | — | — | true | true |
| CL04 | 15 | ok | ok | true | — | — | true | true |
| CL04 | 16 | ok | ok | true | — | — | true | true |
| CL04 | 17 | ok | ok | true | — | — | true | true |
| CL04 | 18 | ok | ok | true | — | — | true | true |
| CL04 | 19 | ok | ok | true | — | — | true | true |
| CL04 | 20 | ok | ok | true | — | — | true | true |
| CL04b | 1 | ok | ok | true | — | — | true | true |
| CL04b | 2 | ok | ok | true | — | — | true | true |
| CL04b | 3 | ok | ok | true | — | — | true | true |
| CL04b | 4 | ok | ok | true | — | — | true | true |
| CL04b | 5 | ok | ok | true | — | — | true | true |
| CL04b | 6 | ok | ok | true | — | — | true | true |
| CL04b | 7 | ok | ok | true | — | — | true | true |
| CL04b | 8 | ok | ok | true | — | — | true | true |
| CL04b | 9 | ok | ok | true | — | — | true | true |
| CL04b | 10 | ok | ok | true | — | — | true | true |
| CL04b | 11 | ok | ok | true | — | — | true | true |
| CL04b | 12 | ok | ok | true | — | — | true | true |
| CL04b | 13 | ok | ok | true | — | — | true | true |
| CL04b | 14 | ok | ok | true | — | — | true | true |
| CL04b | 15 | ok | ok | true | — | — | true | true |
| CL04b | 16 | ok | ok | true | — | — | true | true |
| CL04b | 17 | ok | ok | true | — | — | true | true |
| CL04b | 18 | ok | ok | true | — | — | true | true |
| CL04b | 19 | ok | ok | true | — | — | true | true |
| CL04b | 20 | ok | ok | true | — | — | true | true |
| CL05 | 1 | ok | ok | true | — | — | true | true |
| CL05 | 2 | ok | ok | true | — | — | true | true |
| CL05 | 3 | ok | ok | true | — | — | true | true |
| CL05 | 4 | ok | ok | true | — | — | true | true |
| CL05 | 5 | ok | ok | true | — | — | true | true |
| CL05 | 6 | ok | ok | true | — | — | true | true |
| CL05 | 7 | ok | ok | true | — | — | true | true |
| CL05 | 8 | ok | ok | true | — | — | true | true |
| CL05 | 9 | ok | ok | true | — | — | true | true |
| CL05 | 10 | ok | ok | true | — | — | true | true |
| CL05 | 11 | ok | ok | true | — | — | true | true |
| CL05 | 12 | ok | ok | true | — | — | true | true |
| CL05 | 13 | ok | ok | true | — | — | true | true |
| CL05 | 14 | ok | ok | true | — | — | true | true |
| CL05 | 15 | ok | ok | true | — | — | true | true |
| CL05 | 16 | ok | ok | true | — | — | true | true |
| CL05 | 17 | ok | ok | true | — | — | true | true |
| CL05 | 18 | ok | ok | true | — | — | true | true |
| CL05 | 19 | ok | ok | true | — | — | true | true |
| CL05 | 20 | ok | ok | true | — | — | true | true |
| CL06 | 1 | ok | ok | true | — | — | true | true |
| CL06 | 2 | ok | ok | true | — | — | true | true |
| CL06 | 3 | ok | ok | true | — | — | true | true |
| CL06 | 4 | ok | ok | true | — | — | true | true |
| CL06 | 5 | ok | ok | true | — | — | true | true |
| CL06 | 6 | ok | ok | true | — | — | true | true |
| CL06 | 7 | ok | ok | true | — | — | true | true |
| CL06 | 8 | ok | ok | true | — | — | true | true |
| CL06 | 9 | ok | ok | true | — | — | true | true |
| CL06 | 10 | ok | ok | true | — | — | true | true |
| CL06 | 11 | ok | ok | true | — | — | true | true |
| CL06 | 12 | ok | ok | true | — | — | true | true |
| CL06 | 13 | ok | ok | true | — | — | true | true |
| CL06 | 14 | ok | ok | true | — | — | true | true |
| CL06 | 15 | ok | ok | true | — | — | true | true |
| CL06 | 16 | ok | ok | true | — | — | true | true |
| CL06 | 17 | ok | ok | true | — | — | true | true |
| CL06 | 18 | ok | ok | true | — | — | true | true |
| CL06 | 19 | ok | ok | true | — | — | true | true |
| CL06 | 20 | ok | ok | true | — | — | true | true |
| CL07 | 1 | ok | ok | true | — | — | true | true |
| CL07 | 2 | ok | ok | true | — | — | true | true |
| CL07 | 3 | ok | ok | true | — | — | true | true |
| CL07 | 4 | ok | ok | true | — | — | true | true |
| CL07 | 5 | ok | ok | true | — | — | true | true |
| CL07 | 6 | ok | ok | true | — | — | true | true |
| CL07 | 7 | ok | ok | true | — | — | true | true |
| CL07 | 8 | ok | ok | true | — | — | true | true |
| CL07 | 9 | ok | ok | true | — | — | true | true |
| CL07 | 10 | ok | ok | true | — | — | true | true |
| CL07 | 11 | ok | ok | true | — | — | true | true |
| CL07 | 12 | ok | ok | true | — | — | true | true |
| CL07 | 13 | ok | ok | true | — | — | true | true |
| CL07 | 14 | ok | ok | true | — | — | true | true |
| CL07 | 15 | ok | ok | true | — | — | true | true |
| CL07 | 16 | ok | ok | true | — | — | true | true |
| CL07 | 17 | ok | ok | true | — | — | true | true |
| CL07 | 18 | ok | ok | true | — | — | true | true |
| CL07 | 19 | ok | ok | true | — | — | true | true |
| CL07 | 20 | ok | ok | true | — | — | true | true |
| CL08a | 1 | ok | ok | true | — | — | true | true |
| CL08a | 2 | ok | ok | true | — | — | true | true |
| CL08a | 3 | ok | ok | true | — | — | true | true |
| CL08a | 4 | ok | ok | true | — | — | true | true |
| CL08a | 5 | ok | ok | true | — | — | true | true |
| CL08a | 6 | ok | ok | true | — | — | true | true |
| CL08a | 7 | ok | ok | true | — | — | true | true |
| CL08a | 8 | ok | ok | true | — | — | true | true |
| CL08a | 9 | ok | ok | true | — | — | true | true |
| CL08a | 10 | ok | ok | true | — | — | true | true |
| CL08a | 11 | ok | ok | true | — | — | true | true |
| CL08a | 12 | ok | ok | true | — | — | true | true |
| CL08a | 13 | ok | ok | true | — | — | true | true |
| CL08a | 14 | ok | ok | true | — | — | true | true |
| CL08a | 15 | ok | ok | true | — | — | true | true |
| CL08a | 16 | ok | ok | true | — | — | true | true |
| CL08a | 17 | ok | ok | true | — | — | true | true |
| CL08a | 18 | ok | ok | true | — | — | true | true |
| CL08a | 19 | ok | ok | true | — | — | true | true |
| CL08a | 20 | ok | ok | true | — | — | true | true |
| CL09 | 1 | ok | ok | true | — | — | true | true |
| CL09 | 2 | ok | ok | true | — | — | true | true |
| CL09 | 3 | ok | ok | true | — | — | true | true |
| CL09 | 4 | ok | ok | true | — | — | true | true |
| CL09 | 5 | ok | ok | true | — | — | true | true |
| CL09 | 6 | ok | ok | true | — | — | true | true |
| CL09 | 7 | ok | ok | true | — | — | true | true |
| CL09 | 8 | ok | ok | true | — | — | true | true |
| CL09 | 9 | ok | ok | true | — | — | true | true |
| CL09 | 10 | ok | ok | true | — | — | true | true |
| CL09 | 11 | ok | ok | true | — | — | true | true |
| CL09 | 12 | ok | ok | true | — | — | true | true |
| CL09 | 13 | ok | ok | true | — | — | true | true |
| CL09 | 14 | ok | ok | true | — | — | true | true |
| CL09 | 15 | ok | ok | true | — | — | true | true |
| CL09 | 16 | ok | ok | true | — | — | true | true |
| CL09 | 17 | ok | ok | true | — | — | true | true |
| CL09 | 18 | ok | ok | true | — | — | true | true |
| CL09 | 19 | ok | ok | true | — | — | true | true |
| CL09 | 20 | ok | ok | true | — | — | true | true |
| CL10 | 1 | ok | ok | true | — | — | true | true |
| CL10 | 2 | ok | ok | true | — | — | true | true |
| CL10 | 3 | ok | ok | true | — | — | true | true |
| CL10 | 4 | ok | ok | true | — | — | true | true |
| CL10 | 5 | ok | ok | true | — | — | true | true |
| CL10 | 6 | ok | ok | true | — | — | true | true |
| CL10 | 7 | ok | ok | true | — | — | true | true |
| CL10 | 8 | ok | ok | true | — | — | true | true |
| CL10 | 9 | ok | ok | true | — | — | true | true |
| CL10 | 10 | ok | ok | true | — | — | true | true |
| CL10 | 11 | ok | ok | true | — | — | true | true |
| CL10 | 12 | ok | ok | true | — | — | true | true |
| CL10 | 13 | ok | ok | true | — | — | true | true |
| CL10 | 14 | ok | ok | true | — | — | true | true |
| CL10 | 15 | ok | ok | true | — | — | true | true |
| CL10 | 16 | ok | ok | true | — | — | true | true |
| CL10 | 17 | ok | ok | true | — | — | true | true |
| CL10 | 18 | ok | ok | true | — | — | true | true |
| CL10 | 19 | ok | ok | true | — | — | true | true |
| CL10 | 20 | ok | ok | true | — | — | true | true |
| CL11 | 1 | ok | ok | true | — | — | true | true |
| CL11 | 2 | ok | ok | true | — | — | true | true |
| CL11 | 3 | ok | ok | true | — | — | true | true |
| CL11 | 4 | ok | ok | true | — | — | true | true |
| CL11 | 5 | ok | ok | true | — | — | true | true |
| CL11 | 6 | ok | ok | true | — | — | true | true |
| CL11 | 7 | ok | ok | true | — | — | true | true |
| CL11 | 8 | ok | ok | true | — | — | true | true |
| CL11 | 9 | ok | ok | true | — | — | true | true |
| CL11 | 10 | ok | ok | true | — | — | true | true |
| CL11 | 11 | ok | ok | true | — | — | true | true |
| CL11 | 12 | ok | ok | true | — | — | true | true |
| CL11 | 13 | ok | ok | true | — | — | true | true |
| CL11 | 14 | ok | ok | true | — | — | true | true |
| CL11 | 15 | ok | ok | true | — | — | true | true |
| CL11 | 16 | ok | ok | true | — | — | true | true |
| CL11 | 17 | ok | ok | true | — | — | true | true |
| CL11 | 18 | ok | ok | true | — | — | true | true |
| CL11 | 19 | ok | ok | true | — | — | true | true |
| CL11 | 20 | ok | ok | true | — | — | true | true |
| CL11b | 1 | ok | ok | true | — | — | true | true |
| CL11b | 2 | ok | ok | true | — | — | true | true |
| CL11b | 3 | ok | ok | true | — | — | true | true |
| CL11b | 4 | ok | ok | true | — | — | true | true |
| CL11b | 5 | ok | ok | true | — | — | true | true |
| CL11b | 6 | ok | ok | true | — | — | true | true |
| CL11b | 7 | ok | ok | true | — | — | true | true |
| CL11b | 8 | ok | ok | true | — | — | true | true |
| CL11b | 9 | ok | ok | true | — | — | true | true |
| CL11b | 10 | ok | ok | true | — | — | true | true |
| CL11b | 11 | ok | ok | true | — | — | true | true |
| CL11b | 12 | ok | ok | true | — | — | true | true |
| CL11b | 13 | ok | ok | true | — | — | true | true |
| CL11b | 14 | ok | ok | true | — | — | true | true |
| CL11b | 15 | ok | ok | true | — | — | true | true |
| CL11b | 16 | ok | ok | true | — | — | true | true |
| CL11b | 17 | ok | ok | true | — | — | true | true |
| CL11b | 18 | ok | ok | true | — | — | true | true |
| CL11b | 19 | ok | ok | true | — | — | true | true |
| CL11b | 20 | ok | ok | true | — | — | true | true |
| CL12b | 1 | ok | ok | true | — | — | true | true |
| CL12b | 2 | ok | ok | true | — | — | true | true |
| CL12b | 3 | ok | ok | true | — | — | true | true |
| CL12b | 4 | ok | ok | true | — | — | true | true |
| CL12b | 5 | ok | ok | true | — | — | true | true |
| CL12b | 6 | ok | ok | true | — | — | true | true |
| CL12b | 7 | ok | ok | true | — | — | true | true |
| CL12b | 8 | ok | ok | true | — | — | true | true |
| CL12b | 9 | ok | ok | true | — | — | true | true |
| CL12b | 10 | ok | ok | true | — | — | true | true |
| CL12b | 11 | ok | ok | true | — | — | true | true |
| CL12b | 12 | ok | ok | true | — | — | true | true |
| CL12b | 13 | ok | ok | true | — | — | true | true |
| CL12b | 14 | ok | ok | true | — | — | true | true |
| CL12b | 15 | ok | ok | true | — | — | true | true |
| CL12b | 16 | ok | ok | true | — | — | true | true |
| CL12b | 17 | ok | ok | true | — | — | true | true |
| CL12b | 18 | ok | ok | true | — | — | true | true |
| CL12b | 19 | ok | ok | true | — | — | true | true |
| CL12b | 20 | ok | ok | true | — | — | true | true |
| CL12c | 1 | ok | ok | true | — | — | true | true |
| CL12c | 2 | ok | ok | true | — | — | true | true |
| CL12c | 3 | ok | ok | true | — | — | true | true |
| CL12c | 4 | ok | ok | true | — | — | true | true |
| CL12c | 5 | ok | ok | true | — | — | true | true |
| CL12c | 6 | ok | ok | true | — | — | true | true |
| CL12c | 7 | ok | ok | true | — | — | true | true |
| CL12c | 8 | ok | ok | true | — | — | true | true |
| CL12c | 9 | ok | ok | true | — | — | true | true |
| CL12c | 10 | ok | ok | true | — | — | true | true |
| CL12c | 11 | ok | ok | true | — | — | true | true |
| CL12c | 12 | ok | ok | true | — | — | true | true |
| CL12c | 13 | ok | ok | true | — | — | true | true |
| CL12c | 14 | ok | ok | true | — | — | true | true |
| CL12c | 15 | ok | ok | true | — | — | true | true |
| CL12c | 16 | ok | ok | true | — | — | true | true |
| CL12c | 17 | ok | ok | true | — | — | true | true |
| CL12c | 18 | ok | ok | true | — | — | true | true |
| CL12c | 19 | ok | ok | true | — | — | true | true |
| CL12c | 20 | ok | ok | true | — | — | true | true |
| CL13 | 1 | ok | ok | true | — | — | true | true |
| CL13 | 2 | ok | ok | true | — | — | true | true |
| CL13 | 3 | ok | ok | true | — | — | true | true |
| CL13 | 4 | ok | ok | true | — | — | true | true |
| CL13 | 5 | ok | ok | true | — | — | true | true |
| CL13 | 6 | ok | ok | true | — | — | true | true |
| CL13 | 7 | ok | ok | true | — | — | true | true |
| CL13 | 8 | ok | ok | true | — | — | true | true |
| CL13 | 9 | ok | ok | true | — | — | true | true |
| CL13 | 10 | ok | ok | true | — | — | true | true |
| CL13 | 11 | ok | ok | true | — | — | true | true |
| CL13 | 12 | ok | ok | true | — | — | true | true |
| CL13 | 13 | ok | ok | true | — | — | true | true |
| CL13 | 14 | ok | ok | true | — | — | true | true |
| CL13 | 15 | ok | ok | true | — | — | true | true |
| CL13 | 16 | ok | ok | true | — | — | true | true |
| CL13 | 17 | ok | ok | true | — | — | true | true |
| CL13 | 18 | ok | ok | true | — | — | true | true |
| CL13 | 19 | ok | ok | true | — | — | true | true |
| CL13 | 20 | ok | ok | true | — | — | true | true |
| CL14 | 1 | ok | ok | true | — | — | true | true |
| CL14 | 2 | ok | ok | true | — | — | true | true |
| CL14 | 3 | ok | ok | true | — | — | true | true |
| CL14 | 4 | ok | ok | true | — | — | true | true |
| CL14 | 5 | ok | ok | true | — | — | true | true |
| CL14 | 6 | ok | ok | true | — | — | true | true |
| CL14 | 7 | ok | ok | true | — | — | true | true |
| CL14 | 8 | ok | ok | true | — | — | true | true |
| CL14 | 9 | ok | ok | true | — | — | true | true |
| CL14 | 10 | ok | ok | true | — | — | true | true |
| CL14 | 11 | ok | ok | true | — | — | true | true |
| CL14 | 12 | ok | ok | true | — | — | true | true |
| CL14 | 13 | ok | ok | true | — | — | true | true |
| CL14 | 14 | ok | ok | true | — | — | true | true |
| CL14 | 15 | ok | ok | true | — | — | true | true |
| CL14 | 16 | ok | ok | true | — | — | true | true |
| CL14 | 17 | ok | ok | true | — | — | true | true |
| CL14 | 18 | ok | ok | true | — | — | true | true |
| CL14 | 19 | ok | ok | true | — | — | true | true |
| CL14 | 20 | ok | ok | true | — | — | true | true |
| CL14b | 1 | ok | ok | true | — | — | true | true |
| CL14b | 2 | ok | ok | true | — | — | true | true |
| CL14b | 3 | ok | ok | true | — | — | true | true |
| CL14b | 4 | ok | ok | true | — | — | true | true |
| CL14b | 5 | ok | ok | true | — | — | true | true |
| CL14b | 6 | ok | ok | true | — | — | true | true |
| CL14b | 7 | ok | ok | true | — | — | true | true |
| CL14b | 8 | ok | ok | true | — | — | true | true |
| CL14b | 9 | ok | ok | true | — | — | true | true |
| CL14b | 10 | ok | ok | true | — | — | true | true |
| CL14b | 11 | ok | ok | true | — | — | true | true |
| CL14b | 12 | ok | ok | true | — | — | true | true |
| CL14b | 13 | ok | ok | true | — | — | true | true |
| CL14b | 14 | ok | ok | true | — | — | true | true |
| CL14b | 15 | ok | ok | true | — | — | true | true |
| CL14b | 16 | ok | ok | true | — | — | true | true |
| CL14b | 17 | ok | ok | true | — | — | true | true |
| CL14b | 18 | ok | ok | true | — | — | true | true |
| CL14b | 19 | ok | ok | true | — | — | true | true |
| CL14b | 20 | ok | ok | true | — | — | true | true |
| CL15 | 1 | ok | ok | true | — | — | true | true |
| CL15 | 2 | ok | ok | true | — | — | true | true |
| CL15 | 3 | ok | ok | true | — | — | true | true |
| CL15 | 4 | ok | ok | true | — | — | true | true |
| CL15 | 5 | ok | ok | true | — | — | true | true |
| CL15 | 6 | ok | ok | true | — | — | true | true |
| CL15 | 7 | ok | ok | true | — | — | true | true |
| CL15 | 8 | ok | ok | true | — | — | true | true |
| CL15 | 9 | ok | ok | true | — | — | true | true |
| CL15 | 10 | ok | ok | true | — | — | true | true |
| CL15 | 11 | ok | ok | true | — | — | true | true |
| CL15 | 12 | ok | ok | true | — | — | true | true |
| CL15 | 13 | ok | ok | true | — | — | true | true |
| CL15 | 14 | ok | ok | true | — | — | true | true |
| CL15 | 15 | ok | ok | true | — | — | true | true |
| CL15 | 16 | ok | ok | true | — | — | true | true |
| CL15 | 17 | ok | ok | true | — | — | true | true |
| CL15 | 18 | ok | ok | true | — | — | true | true |
| CL15 | 19 | ok | ok | true | — | — | true | true |
| CL15 | 20 | ok | ok | true | — | — | true | true |
| CL15b | 1 | ok | ok | true | — | — | true | true |
| CL15b | 2 | ok | ok | true | — | — | true | true |
| CL15b | 3 | ok | ok | true | — | — | true | true |
| CL15b | 4 | ok | ok | true | — | — | true | true |
| CL15b | 5 | ok | ok | true | — | — | true | true |
| CL15b | 6 | ok | ok | true | — | — | true | true |
| CL15b | 7 | ok | ok | true | — | — | true | true |
| CL15b | 8 | ok | ok | true | — | — | true | true |
| CL15b | 9 | ok | ok | true | — | — | true | true |
| CL15b | 10 | ok | ok | true | — | — | true | true |
| CL15b | 11 | ok | ok | true | — | — | true | true |
| CL15b | 12 | ok | ok | true | — | — | true | true |
| CL15b | 13 | ok | ok | true | — | — | true | true |
| CL15b | 14 | ok | ok | true | — | — | true | true |
| CL15b | 15 | ok | ok | true | — | — | true | true |
| CL15b | 16 | ok | ok | true | — | — | true | true |
| CL15b | 17 | ok | ok | true | — | — | true | true |
| CL15b | 18 | ok | ok | true | — | — | true | true |
| CL15b | 19 | ok | ok | true | — | — | true | true |
| CL15b | 20 | ok | ok | true | — | — | true | true |
| CL16 | 1 | ok | ok | true | — | — | true | true |
| CL16 | 2 | ok | ok | true | — | — | true | true |
| CL16 | 3 | ok | ok | true | — | — | true | true |
| CL16 | 4 | ok | ok | true | — | — | true | true |
| CL16 | 5 | ok | ok | true | — | — | true | true |
| CL16 | 6 | ok | ok | true | — | — | true | true |
| CL16 | 7 | ok | ok | true | — | — | true | true |
| CL16 | 8 | ok | ok | true | — | — | true | true |
| CL16 | 9 | ok | ok | true | — | — | true | true |
| CL16 | 10 | ok | ok | true | — | — | true | true |
| CL16 | 11 | ok | ok | true | — | — | true | true |
| CL16 | 12 | ok | ok | true | — | — | true | true |
| CL16 | 13 | ok | ok | true | — | — | true | true |
| CL16 | 14 | ok | ok | true | — | — | true | true |
| CL16 | 15 | ok | ok | true | — | — | true | true |
| CL16 | 16 | ok | ok | true | — | — | true | true |
| CL16 | 17 | ok | ok | true | — | — | true | true |
| CL16 | 18 | ok | ok | true | — | — | true | true |
| CL16 | 19 | ok | ok | true | — | — | true | true |
| CL16 | 20 | ok | ok | true | — | — | true | true |
| CL16b | 1 | ok | ok | true | — | — | true | true |
| CL16b | 2 | ok | ok | true | — | — | true | true |
| CL16b | 3 | ok | ok | true | — | — | true | true |
| CL16b | 4 | ok | ok | true | — | — | true | true |
| CL16b | 5 | ok | ok | true | — | — | true | true |
| CL16b | 6 | ok | ok | true | — | — | true | true |
| CL16b | 7 | ok | ok | true | — | — | true | true |
| CL16b | 8 | ok | ok | true | — | — | true | true |
| CL16b | 9 | ok | ok | true | — | — | true | true |
| CL16b | 10 | ok | ok | true | — | — | true | true |
| CL16b | 11 | ok | ok | true | — | — | true | true |
| CL16b | 12 | ok | ok | true | — | — | true | true |
| CL16b | 13 | ok | ok | true | — | — | true | true |
| CL16b | 14 | ok | ok | true | — | — | true | true |
| CL16b | 15 | ok | ok | true | — | — | true | true |
| CL16b | 16 | ok | ok | true | — | — | true | true |
| CL16b | 17 | ok | ok | true | — | — | true | true |
| CL16b | 18 | ok | ok | true | — | — | true | true |
| CL16b | 19 | ok | ok | true | — | — | true | true |
| CL16b | 20 | ok | ok | true | — | — | true | true |
| CL17 | 1 | ok | ok | true | — | — | true | true |
| CL17 | 2 | ok | ok | true | — | — | true | true |
| CL17 | 3 | ok | ok | true | — | — | true | true |
| CL17 | 4 | ok | ok | true | — | — | true | true |
| CL17 | 5 | ok | ok | true | — | — | true | true |
| CL17 | 6 | ok | ok | true | — | — | true | true |
| CL17 | 7 | ok | ok | true | — | — | true | true |
| CL17 | 8 | ok | ok | true | — | — | true | true |
| CL17 | 9 | ok | ok | true | — | — | true | true |
| CL17 | 10 | ok | ok | true | — | — | true | true |
| CL17 | 11 | ok | ok | true | — | — | true | true |
| CL17 | 12 | ok | ok | true | — | — | true | true |
| CL17 | 13 | ok | ok | true | — | — | true | true |
| CL17 | 14 | ok | ok | true | — | — | true | true |
| CL17 | 15 | ok | ok | true | — | — | true | true |
| CL17 | 16 | ok | ok | true | — | — | true | true |
| CL17 | 17 | ok | ok | true | — | — | true | true |
| CL17 | 18 | ok | ok | true | — | — | true | true |
| CL17 | 19 | ok | ok | true | — | — | true | true |
| CL17 | 20 | ok | ok | true | — | — | true | true |
| CL18 | 1 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 2 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 3 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 4 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 5 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 6 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 7 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 8 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 9 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 10 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 11 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 12 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 13 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 14 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 15 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 16 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 17 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 18 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 19 | failed | failed | true | 547 | 547 | true | true |
| CL18 | 20 | failed | failed | true | 547 | 547 | true | true |
| CL19 | 1 | ok | ok | true | — | — | true | true |
| CL19 | 2 | ok | ok | true | — | — | true | true |
| CL19 | 3 | ok | ok | true | — | — | true | true |
| CL19 | 4 | ok | ok | true | — | — | true | true |
| CL19 | 5 | ok | ok | true | — | — | true | true |
| CL19 | 6 | ok | ok | true | — | — | true | true |
| CL19 | 7 | ok | ok | true | — | — | true | true |
| CL19 | 8 | ok | ok | true | — | — | true | true |
| CL19 | 9 | ok | ok | true | — | — | true | true |
| CL19 | 10 | ok | ok | true | — | — | true | true |
| CL19 | 11 | ok | ok | true | — | — | true | true |
| CL19 | 12 | ok | ok | true | — | — | true | true |
| CL19 | 13 | ok | ok | true | — | — | true | true |
| CL19 | 14 | ok | ok | true | — | — | true | true |
| CL19 | 15 | ok | ok | true | — | — | true | true |
| CL19 | 16 | ok | ok | true | — | — | true | true |
| CL19 | 17 | ok | ok | true | — | — | true | true |
| CL19 | 18 | ok | ok | true | — | — | true | true |
| CL19 | 19 | ok | ok | true | — | — | true | true |
| CL19 | 20 | ok | ok | true | — | — | true | true |
| CL20 | 1 | ok | ok | true | — | — | true | true |
| CL20 | 2 | ok | ok | true | — | — | true | true |
| CL20 | 3 | ok | ok | true | — | — | true | true |
| CL20 | 4 | ok | ok | true | — | — | true | true |
| CL20 | 5 | ok | ok | true | — | — | true | true |
| CL20 | 6 | ok | ok | true | — | — | true | true |
| CL20 | 7 | ok | ok | true | — | — | true | true |
| CL20 | 8 | ok | ok | true | — | — | true | true |
| CL20 | 9 | ok | ok | true | — | — | true | true |
| CL20 | 10 | ok | ok | true | — | — | true | true |
| CL20 | 11 | ok | ok | true | — | — | true | true |
| CL20 | 12 | ok | ok | true | — | — | true | true |
| CL20 | 13 | ok | ok | true | — | — | true | true |
| CL20 | 14 | ok | ok | true | — | — | true | true |
| CL20 | 15 | ok | ok | true | — | — | true | true |
| CL20 | 16 | ok | ok | true | — | — | true | true |
| CL20 | 17 | ok | ok | true | — | — | true | true |
| CL20 | 18 | ok | ok | true | — | — | true | true |
| CL20 | 19 | ok | ok | true | — | — | true | true |
| CL20 | 20 | ok | ok | true | — | — | true | true |
| CL20b | 1 | ok | ok | true | — | — | true | true |
| CL20b | 2 | ok | ok | true | — | — | true | true |
| CL20b | 3 | ok | ok | true | — | — | true | true |
| CL20b | 4 | ok | ok | true | — | — | true | true |
| CL20b | 5 | ok | ok | true | — | — | true | true |
| CL20b | 6 | ok | ok | true | — | — | true | true |
| CL20b | 7 | ok | ok | true | — | — | true | true |
| CL20b | 8 | ok | ok | true | — | — | true | true |
| CL20b | 9 | ok | ok | true | — | — | true | true |
| CL20b | 10 | ok | ok | true | — | — | true | true |
| CL20b | 11 | ok | ok | true | — | — | true | true |
| CL20b | 12 | ok | ok | true | — | — | true | true |
| CL20b | 13 | ok | ok | true | — | — | true | true |
| CL20b | 14 | ok | ok | true | — | — | true | true |
| CL20b | 15 | ok | ok | true | — | — | true | true |
| CL20b | 16 | ok | ok | true | — | — | true | true |
| CL20b | 17 | ok | ok | true | — | — | true | true |
| CL20b | 18 | ok | ok | true | — | — | true | true |
| CL20b | 19 | ok | ok | true | — | — | true | true |
| CL20b | 20 | ok | ok | true | — | — | true | true |
| CL12a | 1 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 2 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 3 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 4 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 5 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 6 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 7 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 8 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 9 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 10 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 11 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 12 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 13 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 14 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 15 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 16 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 17 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 18 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 19 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL12a | 20 | trigger_blocked | trigger_blocked | true | 3609 | 3609 | true | true |
| CL08b | 1 | ok | ok | true | — | — | true | true |
| CL08b | 2 | ok | ok | true | — | — | true | true |
| CL08b | 3 | ok | ok | true | — | — | true | true |
| CL08b | 4 | ok | ok | true | — | — | true | true |
| CL08b | 5 | ok | ok | true | — | — | true | true |
| CL08b | 6 | ok | ok | true | — | — | true | true |
| CL08b | 7 | ok | ok | true | — | — | true | true |
| CL08b | 8 | ok | ok | true | — | — | true | true |
| CL08b | 9 | ok | ok | true | — | — | true | true |
| CL08b | 10 | ok | ok | true | — | — | true | true |
| CL08b | 11 | ok | ok | true | — | — | true | true |
| CL08b | 12 | ok | ok | true | — | — | true | true |
| CL08b | 13 | ok | ok | true | — | — | true | true |
| CL08b | 14 | ok | ok | true | — | — | true | true |
| CL08b | 15 | ok | ok | true | — | — | true | true |
| CL08b | 16 | ok | ok | true | — | — | true | true |
| CL08b | 17 | ok | ok | true | — | — | true | true |
| CL08b | 18 | ok | ok | true | — | — | true | true |
| CL08b | 19 | ok | ok | true | — | — | true | true |
| CL08b | 20 | ok | ok | true | — | — | true | true |

