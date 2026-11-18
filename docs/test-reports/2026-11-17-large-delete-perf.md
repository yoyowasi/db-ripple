# 2026-11-17 큰 DELETE 성능: 원인 귀속(resolve) 초선형 원인 수정, 재조회(reread) 분석

브랜치 `feat/large-delete-perf`(main 953fd09 기준). DB는 돌리지 않았다(Docker·DB 실행 금지). 아래 시간은 모두 DB 없는 합성 측정이고, M5 실측(DB)은 Codex 클라우드에서 따로 잰다.

## 결론

- **resolve 초선형 원인 = FK 행 단위 부모 찾기의 O(부모 행 × 자식 행) 이중 반복**이었다. 키 색인으로 바꿔 O(부모 + 자식)이 됐다. 합성 측정(Resolve만, 부하가 낮던 2회차)에서 부모 10만(행 변경 50만) 41.9초 → 1.4초, 5만 10.9초 → 0.65초, 1만 0.58초 → 0.21초(각 3회 중앙값)다.
- **결과는 같다.** 변경 전 코드로 만든 Resolve 결과 지문 404개(CL20 4개 + FK 무작위 400개)가 변경 뒤에도 모두 같고, 기존 Attribution 테스트 156개도 그대로 통과한다.
- **reread는 고치지 않았다(제안만).** 클라이언트 코드는 선형이고, M5 실측도 키당 약 80µs로 거의 선형이다. 다만 DELETE의 키당 비용이 UPDATE(CL20b)의 약 9배라 DB 쪽(지워진 행 키 조회 등)이 의심된다. DB 없이 확인할 수 없어 실험 절차와 개선안만 적는다.

## 1. resolve 원인

PreviewPipeline의 `resolve` 시간 = C11 `ChangeCollector.Collect` + C13 `CausalResolver.Resolve`(PreviewPipeline.cs 76~86줄).

- **원인(코드):** `src/DbRipple.Attribution/FkJudge.cs`(953fd09) 208~219줄. `MatchRow`가 FK로 일치한 자식 행마다 부모 묶음의 행 목록 `ps.Rows` 전체를 훑어 before 키가 같은 부모 행(`parentRowIds`)을 찾았다. `MatchRow`는 55줄에서 (FK, 이벤트, 부모 묶음) 쌍마다 자식 행 전부에 대해 불린다.
- **복잡도:** 쌍마다 O(P × C) (P = 부모 묶음 행 수, C = 일치한 자식 행 수). CL20 N = 10만이면 P = 10만, C = 20만으로 문자열 비교 약 2×10^10번(5만: 5×10^9, 1만: 2×10^8). 1만→5만에서 25배, 5만→10만에서 4배인 항이 선형 항과 섞여 M5에서 본 16배·3.5배가 된다.
- **고친 뒤:** 부모 묶음을 만들 때(`ParentSets`) before 키 → 그 키의 부모 행 목록(묶음 안 순서 유지, after 키 포함) 사전을 만들고, 자식 행은 자기 키의 목록만 본다. 쌍마다 O(P + C + Σk)(k = 같은 before 키의 부모 행 수, PK·유일 키 참조면 1).

고친 뒤 Resolve는 1만→5만 3.2배, 5만→10만 2.2배로 선형에 가깝다(4절). 계측(임시 Stopwatch, 커밋 안 함)으로 본 5만 행 Resolve 약 1초의 구성은 잔차 조립(`ResidualBuilder.Build`, 순 변경 25만 건) 0.7~1.0초, 문장 판정(FK 포함) 0.15~0.2초, 나머지 0.2초 이하였다.

## 2. 바꾼 것

| 파일 | 내용 |
|---|---|
| `src/DbRipple.Attribution/FkJudge.cs` | `ParentSet`의 `Before` 집합·`Rows` 목록을 `ByBefore`(before 키 → 부모 행 목록) 사전 하나로 바꿈. `MatchRow`는 키 존재 확인과 부모 행 찾기에 이 사전을 쓴다. 비교는 이전과 같은 Ordinal, 목록 순서는 부모 묶음 행 순서, UPDATE 이벤트의 UNPAIRED·이미지 없음 제외와 CASCADE after 일치 조건은 그대로 |
| `tests/DbRipple.Attribution.Tests/LargeDeleteData.cs` | CL20 픽스처를 부모 N행으로 키운 C13 입력(실제 카탈로그 픽스처 + C6 정적 분석) |
| `tests/DbRipple.Attribution.Tests/ResolveEquivalenceTests.cs` | 결과 동일성 회귀(아래 3절) |
| `tests/DbRipple.Attribution.Tests/LargeDeletePerfTests.cs` | 합성 측정. `DBR_PERF=1`일 때만 돈다(CI 영향 없음) |
| `tests/fixtures/attribution/resolve-golden.txt` | 변경 전 코드로 만든 결과 지문 404개 |

동작(귀속 결과, 확신도, 순서, 행 이미지 상한·KEYS 전환)은 바꾸지 않았다. 근사는 없다.

## 3. 결과 동일성

- **지문 비교:** `Resolution_matches_pre_change_fingerprints`가 입력 404개마다 Resolve 결과 전체(모델·그래프를 ContractJson으로 직렬화)의 SHA-256을 지문 파일과 비교한다. 지문 파일은 **변경 전 코드(커밋 941402c, 953fd09의 src 그대로)**에서 `DBR_WRITE_FIXTURES=1`로 만들었고, 변경 뒤(3b28eab) 404개 모두 같다.
  - 입력: CL20 N = 1, 2, 3, 257 + 고정 시드 무작위 400개. 무작위 입력은 부모 키 중복, UNPAIRED, 이미지 없음(KEYS 흉내), 불완전 묶음(RowCount > 행 수), FK 동작 CASCADE·SET NULL·SET DEFAULT(기본값 상수·함수·없음), 두 번째 FK, 깊이가 다른 부모 후보(H2), 쓰기 경합 트리거, 동적·미상 트리거, 트리거 연결 누락·ambiguous 상한, 묶음 없는 최종 차이(잔차)를 섞는다.
  - `Fuzz_covers_fk_paths`가 무작위 입력이 바꾼 경로를 실제로 밟는지 확인한다: FK 판정 252건, 부모 행 1개인 자식 행 139건, 2개 이상 20건, FK 종류 셋과 certain·inferred·불일치 행이 모두 나온다.
- **명시 테스트:** `Update_event_parent_row_ids_keep_order_and_filters`(UPDATE CASCADE·SET NULL에서 부모 행 순서와 UNPAIRED·after 불일치 제외), `Cl20_scaled_parent_row_ids_are_exact`(1천 부모: 자식 2천 행 모두 부모 행 1개, FK_CASCADE certain), `Resolve_is_deterministic_on_large_input`. 셋 다(그리고 `Fuzz_covers_fk_paths`도) 변경 전 코드에서 통과했다.
- **기존 테스트:** CascadeLab 픽스처 회귀(CL01~CL20b) 등 기존 Attribution 테스트 전부 통과(아래 5절).

## 4. 합성 측정

**환경:** Windows 10 19045, Intel Core Ultra 7 255H(논리 16), RAM 15.4GB(측정 중 여유 1~3GB), .NET 10.0.12, Release, 워크스테이션 GC. 같은 PC에서 다른 에이전트의 빌드·테스트가 함께 돌았다(아래 CPU 부하는 각 실행 직전 Win32_Processor.LoadPercentage 한 번 읽은 값).

**방법:** `LargeDeletePerfTests.Cl20_resolve_scaling`(입력 만들기는 재지 않고 `CausalResolver.Resolve`만 Stopwatch로 잰다). 크기마다 새 테스트 프로세스, 워밍업 1회 뒤 3회, 중앙값. 변경 전(941402c 빌드)과 뒤(3b28eab 빌드)의 Release 출력을 따로 두고 크기마다 전 → 후를 번갈아 돌렸다. 같은 순서로 두 번(1회차 14:16~14:22, 2회차 14:22~14:27) 했다. 명령(크기마다): `DBR_PERF=1 DBR_PERF_SIZES=<N> DBR_PERF_REPEAT=3 dotnet test <빌드 출력>\DbRipple.Attribution.Tests.dll --filter FullyQualifiedName~LargeDeletePerfTests --logger "console;verbosity=detailed"`.

입력: CL20 부모 N, 자식 2N, ChildLog 2N(행 변경 5N, 최종 차이 5N). N은 M5의 N과 같다.

**2회차(부하 낮음, 판정용):**

| N | 행 변경 | 전 표본(ms) | 전 중앙값 | 후 표본(ms) | 후 중앙값 | 배수 | CPU 부하(전/후) |
|---|---|---|---|---|---|---|---|
| 1만 | 5만 | 656 / 527 / 583 | 583 | 272 / 206 / 192 | 206 | 2.8배 | 8% / 9% |
| 5만 | 25만 | 10,200 / 10,929 / 11,184 | 10,929 | 692 / 615 / 652 | 652 | 16.8배 | 35% / 15% |
| 10만 | 50만 | 41,599 / 41,868 / 50,221 | 41,868 | 1,335 / 1,485 / 1,416 | 1,416 | 29.6배 | 5% / 38% |

**1회차(부하 높음, 참고):** 1만 1,233 → 422ms(CPU 96% / 100%), 5만 10,957 → 673ms(96% / 29%), 10만 48,027 → 1,976ms(20% / 36%). 표본: 1만 전 1,212/1,233/1,276·후 792/422/319, 5만 전 10,957/11,016/10,229·후 673/802/654, 10만 전 65,408/46,414/48,027·후 1,976/1,556/2,737.

- **늘어나는 모양:** 전은 1만→5만 18.7배, 5만→10만 3.8배(M5 실측 16배·3.5배와 같은 모양). 후는 3.2배, 2.2배로 선형에 가깝다.
- **메모리:** Resolve 한 번의 할당은 전후가 거의 같다(10만: 1,498MB → 1,506MB, 5만: 744 → 747MB, 1만: 149 → 150MB). 색인 사전이 늘린 할당은 1% 안이다. 측정 프로세스 최대 작업 집합은 10만에서 1.3~1.7GB(입력 포함)다.
- **불확실성:** 표본 3개, 같은 PC 동시 부하. 1회차 1만은 CPU 96~100%에서 재서 2회차보다 2배쯤 느리다. 배수의 크기 순서(1만 약 3배, 5만 약 17배, 10만 약 30배)는 두 회차가 같다.

**resolve 단계 전체(Collect + Resolve) 참고 측정:** M5의 `resolve` 열과 같은 범위를 보려고, CL20 픽스처 raw.json(헤더·행 이미지 JSON·재조회 행·최종 차이)을 N으로 키워 `ChangeCollector.Collect` → `CausalResolver.Resolve`를 한 프로세스에서 잰 임시 콘솔 프로그램이다(저장소에 넣지 않음. C11은 다른 담당이고 테스트 프로젝트 참조를 늘리지 않으려고). 변경 전 Attribution(953fd09 FkJudge)과 뒤를 따로 빌드해 크기마다 전 → 후로 돌렸다(14:27~14:34, 워밍업 1회 뒤 3회, 표본별 합의 중앙값).

| N | 전 Collect | 전 Resolve | 전 합 | 후 Collect | 후 Resolve | 후 합 | CPU 부하(전/후) |
|---|---|---|---|---|---|---|---|
| 1만 | 385 | 545 | 890ms | 486 | 226 | 712ms | 51% / 15% |
| 5만 | 1,353 | 11,393 | 12,746ms | 1,270 | 920 | 2,210ms | 21% / 23% |
| 10만 | 5,802 | 57,623 | 63,871ms | 6,283 | 5,090 | 11,373ms | 32% / 26% |

- 전 10만 resolve 합 63.9초는 M5 실측 resolve(51~56초)와 같은 크기라, 합성 입력이 실제 병목을 재현한다고 본다.
- 후에는 Collect(C11, 바꾸지 않음)가 resolve 단계의 절반 이상이다. Collect의 5만→10만 배수는 이 실행에서 4.3~4.9배, 같은 날 앞선 실행(후 코드)에서 2.1배(2.2 → 4.7초)로 표본마다 달라 선형인지 가르지 못했다(10만에서 메모리 압박이 겹침). C11 담당이 같은 방식으로 볼 항목이다.
- 이 실행의 후 10만 Resolve(3.9~5.5초)는 Resolve만 잰 표(두 회차 표본 1.3~2.7초)보다 크다. 같은 프로세스에 Collect 결과가 함께 있어 최대 작업 집합이 1.9GB(여유 메모리 2.3GB)까지 올라 GC 부담이 컸던 것으로 보인다(추정).

## 5. 검증

| 검사 | 명령 | 결과 |
|---|---|---|
| 빌드 | `dotnet build DbRipple.slnx -warnaserror` | 경고 0, 오류 0 |
| 테스트 | `dotnet test DbRipple.slnx` | 실패 0. Attribution 162 통과(기존 156 + 새 테스트 5 + 측정 1, 측정은 `DBR_PERF` 없으면 바로 끝남). DB 테스트는 연결 환경 변수가 없어 건너뜀(Integration 4, Execution 17 등) |
| 동일성(변경 전 코드) | 941402c에서 `DBR_WRITE_FIXTURES=1 dotnet test tests/DbRipple.Attribution.Tests --filter ResolveEquivalenceTests` | 지문 404개 생성, 명시 테스트 통과 |

증거 범위: 정적·단위(메모리 안 합성 입력). DB·브라우저·운영은 확인하지 않았다.

## 6. reread 분석과 제안

**코드(읽은 범위):** `src/DbRipple.Execution/PreviewExecutor.cs` 486~514줄 `RereadAsync`, `RereadSql.cs`, 소비 쪽 `ChangeCollector.cs` 469~508줄 `ApplyRereads`.

- 재조회 키 = FULL 헤더의 행 이미지 keyJson 전부(테이블마다 중복 제거). CL20 N이면 Parent N + Child 2N + ChildLog 2N = 5N개(첫 문장은 상한을 넘어도 문장이 끝난 뒤에 KEYS로 바뀌므로 이미지가 모두 FULL이다, PreviewExecutor 182~187줄). 키 1천 개마다 왕복 1번(10만이면 500번).
- 서버 쪽 SQL: `OPENJSON(@keys)` LEFT JOIN 대상 테이블 `ON r.키 = JSON_VALUE(k.value, '$."키"')`, 찾은 행만 `FOR JSON` 부분 질의.
- 클라이언트 쪽: Where·GroupBy·Distinct·Chunk·string.Join·청크별 사전으로 모두 선형이다. **초선형 구조는 코드에 없다.**

**실측 다시 읽기(evaluation.md §6.2, 2026-11-16 재측정):**

| 사례 | N | 재조회 키 | reread p50 | 키당 |
|---|---|---|---|---|
| CL20 DELETE | 1천 | 5천 | 589ms | 118µs |
| CL20 DELETE | 1만 | 5만 | 3,986ms | 80µs |
| CL20 DELETE | 5만 | 25만 | 21,050ms | 84µs |
| CL20 DELETE | 10만 | 50만 | 38,895ms(11-16 재측정 37.8초) | 78µs |
| CL20b UPDATE | 5만 | 7.5만(Child N + Parent N/2) | 633ms | 8.4µs |
| CL20b UPDATE | 10만 | 15만 | 1,359ms | 9.1µs |

- DELETE reread는 키 수에 거의 선형이다(키당 78~84µs). 11-16 보고서의 "5만→10만 2.5배"는 부하 겹침 값(46.3초)이고, 부하 없는 재측정 37.8초 ÷ DM6 5만 약 18초 ≈ 2.1배다.
- 그런데 **DELETE 키당 비용이 UPDATE의 약 9배**다. UPDATE는 행이 남아 있어 FOR JSON까지 하는데도 싸다. 왕복 수 차이로는 설명되지 않는다(청크당 DELETE 약 78ms, UPDATE 약 9ms). 그래서 DB 쪽 비용으로 본다. 가설(확인 안 됨): (가) 같은 트랜잭션에서 지운 행(유령 레코드)의 키 조회가 비싸다, (나) 트랜잭션 안에서 0행에서 20만 행이 된 ChildLog 또는 전부 지워진 Parent·Child의 통계 때문에 계획이 탐색(seek)이 아니다, (다) 그 밖의 서버 비용. 키당 비용이 N과 무관하게 일정하므로 청크마다 테이블 전체를 훑는 계획은 아닐 가능성이 높다(그랬다면 N²로 늘었을 것).

**DB에서 가를 실험(Codex 클라우드, 샌드박스 DB에서 한 연결):** `BEGIN TRAN; DELETE FROM cl20.Parent WHERE id <= 100000;` 뒤 `SET STATISTICS IO, TIME ON; SET STATISTICS XML ON;`으로 `RereadSql.For`가 만드는 SQL을 테이블마다(Parent·Child·ChildLog) 키 1천 개 청크로 한 번씩 실행하고 `ROLLBACK`. 볼 것: 테이블별 경과 시간·논리 읽기·실제 계획(Clustered Index Seek인지), 같은 테이블에서 청크 크기 1천·5천·1만의 시간.

**개선 후보(구현 안 함):**

1. **지워진 키만 나온 키는 재조회하지 않기(효과 클 가능성, 결정 필요). → 2026-11-18 구현함(6.1).** `ApplyRereads`는 재조회 행이 없으면(`RowJson` null) 건너뛰고(477줄), 있으면 그 키 그룹의 마지막 행 변경에 after가 있을 때만 쓴다(490·499줄). 그룹 키는 `AfterKeyJson ?? KeyJson`이고 after가 있는 행 상태의 그룹 키는 INSERTED 이미지의 keyJson이다(ChangeCollector 311·328줄). 그래서 어느 FULL 헤더의 INSERTED 이미지에도 나오지 않은 키(DELETE 전용 키)는 재조회 결과가 C11 결과를 바꿀 수 없다(코드 읽기로 낸 결론, 실행 확인 안 함). CL20이면 재조회 키가 5N → 2N(ChildLog만)으로 줄고, 비싼 쪽으로 의심되는 지워진 행 조회가 모두 빠진다. 다만 `RawCapture.Rereads`(C10 계약 출력)가 줄고, 설계 문서 C10의 "수집된 키를 재조회" 범위가 바뀌며, 판단 근거가 C11(다른 담당) 내부 규칙에 기대므로 설계 결정과 C11 담당 확인이 먼저다.
2. **키를 임시 테이블로 한 번에 보내고 테이블마다 질의 1번.** SqlBulkCopy로 `#keys(ord, keyJson)`에 넣고 한 번 조인하면 OPENJSON 고정 추정(50행) 대신 실제 행 수로 계획을 세운다. SQL이 바뀌므로 결과 문자열이 같은지 DB 테스트로 확인해야 한다.
3. **청크 크기 1천 → 5천~1만.** 왕복 수가 1/5~1/10. 실험의 청크별 시간이 왕복 비용 위주일 때만 의미가 있다(위 실측으로는 왕복 비용이 작아 보인다).

reread 코드는 바꾸지 않았다(DB로 효과를 잴 수 없고, 1안은 계약·설계 범위 변경).

### 6.1 1안 구현함(2026-11-18, 브랜치 `perf/reread-skip-deleted`, main 2e96564 기준)

클라우드 실측(2026-11-17)에서 지운 행 키 재조회가 빈 Parent·Child에 키마다 Clustered Index Scan을 반복한다고 확인돼 1안을 구현했다. 2·3안은 하지 않았다. 설계 반영: `docs/architecture.md` §5.2 7, D3 대조 1.

**규칙(`RereadSql.Plan`):** 후보는 전과 같다(FULL 묶음 행 이미지 keyJson, 키 있는 테이블, 테이블마다 서수 중복 제거). 그 가운데 같은 테이블(object_id)의 INSERTED 이미지(FULL·KEYS 묶음 모두)에 나온 키만 읽는다. 서수로 먼저 찾고, 못 찾으면 접어서(후행 공백 제거·대문자·숫자는 decimal 정규형, Attribution `ValueComparer`의 대소문자 무시 쪽과 같은 규칙) 다시 본다. 생략한 키 수는 `RawCapture.RereadSkippedKeys`(추가 필드, 0이면 JSON에서 빠짐)에 남긴다. 남은 키의 SQL·청크(1천)는 그대로다.

**출력이 바뀌지 않는 근거(코드, main 2e96564 줄 번호):**

- 재조회 행을 읽는 곳은 `ChangeCollector.Collect` 91줄 → `ApplyRereads`뿐이다. `PreviewPipeline.cs` 119줄은 시간만 옮기고, `PreviewRecord`(124~130줄)·API·평가 하네스(`eval/`)·web은 `Rereads`를 읽지 않는다. 테스트에서는 `CascadeLabTests.cs` 91줄(CL08 `cl08.Acct {"id":1}`, UPDATE 키라 남음)과 픽스처 30개 `raw.json`이 쓴다.
- `ApplyRereads` 469~472줄: 묶음 키 = `AfterKeyJson ?? KeyJson`(서수 사전). 477줄: 재조회 행이 null이거나 그 (테이블, 키) 묶음이 없으면 건너뜀. 490줄(순서 확정): 마지막 행 변경에 after가 없으면 건너뜀. 499~500줄(순서 미정): after 있는 대표가 없으면 건너뜀. 둘 다 `Replace`·차이 추가 전에 끝난다.
- after가 있는 행 변경은 `ToRow`(311~313·328줄)에서 `d.After`(INSERTED 이미지)가 있고 그 rowJson이 있을 때만 생기며, 그 행의 `AfterKeyJson`은 INSERTED 이미지의 keyJson이다. 키 없는 테이블(keyJson null)은 470줄에서 빠지고 재조회도 안 한다. 그래서 어떤 INSERTED 이미지에도 서수로 나오지 않은 키의 묶음에는 after가 있는 행 변경이 없고, 그 키의 재조회 값(행이 있든 없든)은 C11 출력을 바꿀 수 없다. 재조회 행끼리는 (테이블, 키)가 서로 달라 서로 영향을 주지 않는다.
- 오케스트레이터가 짚은 경로: DELETE 뒤 감사된 재INSERT(다른 문장·트리거) → INSERTED가 있어 남김. PK를 바꾼 UPDATE → 새 키는 INSERTED라 남기고, 옛 키(SINGLE_ROW·BY_UNIQUE는 새 키 묶음에 들어감, UNPAIRED 옛 키 행은 after 없음)만 생략. 감사가 놓친 재삽입 → 전에도 재조회 값이 위 477·490·499줄에서 버려졌고 최종 차이 잔차로 잡혔다(바뀌지 않음). 대소문자·후행 공백만 다른 키 → 접기로 남김(서수 규칙보다 넓음). KEYS 묶음 INSERTED에만 나온 키 → 남김(넓음). KEYS 묶음 키는 전처럼 후보가 아니다. 행 이미지 상한·KEYS 전환(`PreviewExecutor` 실행 반복)은 건드리지 않았다.

**검증(DB 없음):**

| 검사 | 명령 | 결과 |
|---|---|---|
| 빌드 | `dotnet build DbRipple.slnx -warnaserror` | 경고 0, 오류 0 |
| 테스트 | `dotnet test DbRipple.slnx` | 실패 0. Execution 112 통과(새 42: 합성 9, 픽스처 이론 30, CL20 키 수 2, JSON 1), Attribution 162, Api 133 등. DB 테스트는 연결 환경 변수가 없어 건너뜀 |
| 동일성(합성) | `RereadPlanTests.cs` `CollectorTests.Reread_*` | DELETE만, DELETE 뒤 재INSERT(다른 문장·트리거), 트리거 연쇄 DELETE, PK 변경(SINGLE_ROW·BY_UNIQUE·UNPAIRED·맞바꾸기), 대소문자·후행 공백, 테이블별, KEYS 모드, 순서 미정(Q14), 키 없는 테이블. DB 상태 3가지(행 없음, 모든 키에 감사 after와 다른 행 = 감사 밖 재삽입 가정, 마지막 after)마다 전체 재조회와 생략 재조회의 `Collect` JSON이 같다 |
| 동일성(실DB 픽스처) | `RereadPlanFixtureTests` | CascadeLab 픽스처 30개: 픽스처 `rereads` = 기준 구현(바꾸기 전 키 고르기). 위 DB 상태 3가지마다 전체 재조회와 생략 재조회의 `Collect`가 같고, 기록된 실제 재조회 값에서 생략한 키를 뺀 입력의 `Collect`가 `collect.json`과 같다 |
| 키 수 | 같은 파일 | CL20 픽스처(N = 2) 10 → 4(생략 6), CL20 모양 N = 1천 5천 → 2천(생략 3천) |
| 검사의 힘 | 규칙을 일부러 틀리게 바꿔 실행(커밋 안 함) | 픽스처 CL08a·CL08b·CL10·CL12b·CL20b와 합성 6개가 "Collect 결과가 다름"으로 실패 |
| 계획 비용 | 임시 측정(커밋 안 함), Release, N = 10만 CL20 모양 | `Plan` 약 95ms(바꾸기 전 키 고르기 128~228ms). 서수로 먼저 찾아 CL20·CL20b는 접기를 하지 않는다 |

증거 범위: 정적·단위(픽스처는 실DB에서 만든 원자료). DB 실행·M5·평가는 하지 않았다.

**Codex 클라우드 DB 재측정(요청):** 이 브랜치에서 아래를 이전과 같은 방식으로 돌린다.

> 2026-11-18 실행 결과와 원자료는 [재조회 최적화 클라우드 검증](2026-11-18-cloud-reread-validation.md)에 있다.

```
pwsh -NoProfile -File scripts/home-test.ps1 -Date cloud-20261118   # CI와 같은 기본(빌드·DB 테스트·30사례×3회 평가·N1), summary PASS
. ./scripts/env.ps1
dotnet run --project eval/DbRipple.Eval.Cli -c Release -- m5 --cases CL20,CL20b --sizes 10000,50000,100000 --repeat 5 --warmup 1 --out logs/eval-m5
DBR_WRITE_FIXTURES=1 dotnet test tests/DbRipple.Execution.Tests --filter "FullyQualifiedName~CascadeLabTests|FullyQualifiedName~Week3EngineTests"
git diff --stat -- tests/fixtures/capture
```

기대: (1) CL20 재조회 키 = ChildLog 2N(1만/5만/10만에서 2만/10만/20만, 전 5N). M5 CSV에는 키 수 열이 없으므로 키 수는 (5)의 CL20 픽스처(N = 2)로 본다. (2) CL20 reread p50이 기준값 4,693/22,905/47,784ms보다 크게 준다. 남는 것은 행이 있는 ChildLog Seek이고 CL20b(행이 남는 UPDATE, 10만 15만 키 957ms)의 키당 비용을 대면 10만에서 1~3초 안팎(추정). total p50도 그만큼 준다. (3) CL20b reread·execute, CL20 execute·resolve는 같은 수준. (4) 기본 평가 M1~M4와 기준선 공통 지표가 같다(CI 기본 평가와 같은 기준). (5) 픽스처를 다시 쓰면 `raw.json`의 `rereads`가 줄고 `rereadSkippedKeys`가 생긴다(CL20: `rereads` 4개 모두 `cl20.ChildLog`, `rereadSkippedKeys` 6). `collect.json`은 바뀌지 않아야 한다. 바뀐 파일이 있으면 main(2e96564)에서 같은 명령을 돌려 환경 차이(captureSeq·object_id 등)인지 먼저 가른다. 이 픽스처 변경은 커밋하지 않는다(생략 전 `rereads`가 동일성 테스트의 기준이다).

## 7. Codex 클라우드 DB 재측정

> 2026-11-17 후속 실측 완료: [클라우드 DB·성능 검증](2026-11-17-cloud-db-and-perf.md). M5 6조합×5회와 확장 37사례×20회를 실행했다. CL20 resolve p50은 1만/5만/10만에서 627/3,242/7,413ms였다. reread 추가 실험에서는 DELETE 후 빈 Parent·Child의 반복 Scan을 확인했다. 아래는 당시 재측정 요청과 비교 기준이다.

브랜치 `feat/large-delete-perf`를 받아, 이전 측정과 같은 방식(워밍업 1회 + 5회, Release, 제품 모드)으로 잰다. 연결 환경 변수는 `scripts/env.ps1`이 만드는 값(`DBRIPPLE_SQL_CONNECTION` 등)과 같게 둔다.

```
. ./scripts/env.ps1
dotnet run --project eval/DbRipple.Eval.Cli -c Release -- m5 --cases CL20,CL20b --sizes 10000,50000,100000 --repeat 5 --warmup 1 --out logs/eval-m5
```

결과 동일성까지 DB로 보려면 같은 브랜치에서 평가 묶음도 돌린다(M1~M4가 이전과 같아야 한다).

```
dotnet run --project eval/DbRipple.Eval.Cli -c Release -- cascadelab --systems preview,B0,B2 --repeat 20 --out logs/eval
```

**비교 기준값(변경 전, CL20 DELETE p50):**

| N | total | resolve | reread | 출처 |
|---|---|---|---|---|
| 1만 | 6,096ms | 995ms | 3,986ms | `m5-20261112-145013` |
| 5만 | 43,364ms | 16,029ms | 21,050ms | 같음 |
| 10만 | 105,171ms | 55,710ms | 38,895ms | `m5-20261112-145801` |
| 10만(부하 없는 재측정) | 99.0초 | 51.1초 | 37.8초 | `m5-20261116-101532` |

대조군 CL20b UPDATE 10만: total 9,774ms, resolve 2,930ms(11-12), total 7.3초(11-16). CL20b는 FK 판정을 타지 않아(ON UPDATE 동작 없음) 바뀌면 안 된다.

**판정:** (1) CL20 resolve가 선형에 가깝게 는다(5만→10만 약 2배, 1만→5만 약 5~6배 이하). (2) resolve p50이 기준값보다 작다. 합성 측정에서 남은 resolve는 C11 Collect가 Resolve보다 크다(4절 참고 측정). (3) reread·execute·CL20b는 기준값과 같은 수준(바꾸지 않음). (4) 예외 0, `differences.txt` 0바이트, cascadelab M1~M4가 이전 실행과 같다.

## 미확인

- DB 실측(M5)의 resolve·total 개선 폭. 합성 입력은 실제 C11 출력과 모양이 같지만(픽스처 collect.json을 키운 것) 실제 측정의 GC·메모리 압박·동시 부하는 다를 수 있다.
- reread 느린 원인(DB 쪽 가설). 6절 실험으로 가른다.
- C11 Collect(다른 담당)의 큰 입력 비용. 4절 참고 측정만 했다.
- 잔차 조립(`ResidualBuilder.Build`)의 작은 개선(닿은 행이 하나면 LINQ 정렬 생략, 재조회 차이가 없으면 키 문자열 생략)을 시험했으나 5만에서 잔차 조립 시간 중앙값 810 → 680ms(표본 716~980 대 438~779, 3회씩)로 표본 폭이 겹쳐 효과를 가르지 못해 넣지 않았다.
