# 2주차 계획 (2026-11-10)

설계 [architecture.md](architecture.md) v1.3 §11.3의 2주차 범위다. 분석기(C6), 실행기(C10), 수집기(C11), 최종 차이(C12), 원인 귀속·그래프(C13), 저장소(C14), API(C5), 평가 하네스 골격(C16)을 만들고, C12 첫판 직후 SP7을 한다. CascadeLab으로 매일 회귀한다.

**완료 기준:** CL01~CL08의 M1·M2가 CSV로 나온다.

## 0. 결과 (2026-11-10, 미커밋)

**완료 기준 충족.** CL01~CL08b 11건의 M1·M2 CSV가 `logs/week2/eval/eval-20261110-135700/`에 있다.
- M1: total·obs·cap 모두 tp 54, fp 0, fn 0(precision·recall 1.0000), 행 완전 일치 45/45, 잔차로 메운 셀 0/54, E_obs 0
- M2: DIRECT 14/14, FK_CASCADE 20/20, FK_SET_NULL 2/2, FK_SET_DEFAULT 2/2, TRIGGER 9/9, ALL 45/45
- 상태 일치 11/11, 오라클 대 손 정답 키 일치 11/11, 원문 배치 일치 11/11, differences.txt 0바이트
- M3(합성·실측 픽스처): 손 정답 간선 40/40, certain 과신 0

| 검사 | 결과 | 증거 범위 |
|---|---|---|
| `dotnet build DbRipple.slnx` | 경고 0, 오류 0 | 정적 |
| `. .\scripts\env.ps1; dotnet test DbRipple.slnx` | 484 통과, 1 건너뜀(SP7, 따로 실행) | 단위 + 로컬 컨테이너 DB |
| SP7 | 통과(독립 확인 10/10, AW 71개 테이블 p50 1.55~1.65초) | DB |
| Host HTTP(AW DM1, 거부 422, 행 페이지, 취소) | 기대대로 | API, 로컬 AW |
| 독립 검토 2회 | 1회차 높음 2·중간 3 → 2회차에 해소. 새 중간 2건은 3주차로 | 정적 + DB 재현 |

**3주차로 넘기는 것**
1. 프로시저를 부르는 트리거를 미상으로 둔 보수 처리 때문에 AW 트리거 귀속이 ambiguous로 내려간다(정밀도 퇴행, 검토 2회차). FK 경합에서는 미상을 유지하되, WRITES 하향은 진짜 미상(Q6)에만 한다. 정적 쓰기 대상에 그 테이블이 있으면 inferred를 유지한다. 프로시저 본문을 `sys.sql_modules`에서 한 단계 펼친다(uspLogError는 dbo.ErrorLog에만 쓴다).
2. 키 없는 테이블의 ROW 키 규칙을 평가 훅과 E_cap 재구성 사이에 맞춘다(계산 열, 관측 불가 열). 지금은 힙 변경 사례가 없어 드러나지 않는다.
3. 직접 대상 트리거의 SAVE TRAN·균형 잡힌 BEGIN…COMMIT도 거부한다(과잉 거부, 안전 쪽). COMMIT만 거부하고 나머지는 경고로 낮출지 정한다.
4. 오라클 봉투: RAISERROR만 하는 트리거·컴파일 오류는 XACT_ABORT ON에서도 트랜잭션을 살려 둔다. 실패 사례의 오라클 after를 롤백 뒤 상태로 정한다.
5. 3609 판정 남은 경우: 연쇄 대상 테이블의 ROLLBACK 트리거가 CATCH에서 자동 커밋 로그를 쓰면 failed로 나온다. 트리거가 COMMIT 뒤 RAISERROR를 내면 trigger_blocked가 남는다(잔차·dirty는 잡힘).
6. 대소문자 무시 키를 대소문자만 바꾸는 UPDATE(제품 UPDATED 대 오라클 DELETED+INSERTED), NOCOUNT OFF에서 rowsAffected 과대 집계.
7. 실패 경로 DiffAsync 예외(8115)를 결과로 돌려줄 계약, ApiExceptionHandler가 실행기 InvalidOperationException까지 409로 내는 문제(기대 500·503).
8. 손 정답 pairing 6건 정정과 `CascadeLabTests.PairingOverrides` 삭제([../cascadelab/README.md](../cascadelab/README.md) "정정").
9. 평가 범위 확대: CL09~CL20(CL16 IDENTITY 짝 매핑, 비결정 셀, 힙·KEYS·여러 문장 실패 사례), AW 사례. 3주차 본 범위: SP8·SP9, 프런트(C1~C4), /catalog/schema(Q23), /apply(C15, Q24).

## 1. 프로젝트 배치와 소유자

| 프로젝트 | 담당 | 소유 | 참조 |
|---|---|---|---|
| src/DbRipple.Contracts | 공용 계약 | 오케스트레이터(고정) | Core |
| tests/DbRipple.TestSupport, TestSupport.Tests | DB 격리·시드·계약 JSON 검사 | 오케스트레이터(고정) | Core, Contracts |
| src/DbRipple.Analysis + tests/DbRipple.Analysis.Tests | C6 배치 분석, 트리거 정적 분석 | 구현자 A | Contracts |
| src/DbRipple.Execution + tests/DbRipple.Execution.Tests | C10, C11 | 구현자 B | Contracts |
| src/DbRipple.Diff + tests/DbRipple.Diff.Tests | C12, SP7 | 구현자 C | Contracts |
| src/DbRipple.Attribution + tests/DbRipple.Attribution.Tests | C13 | 구현자 D | Contracts |
| src/DbRipple.Preview, src/DbRipple.Api, src/DbRipple.Host + tests/DbRipple.Api.Tests | 파이프라인, C8 실행 시점 측면, C14, C5, 조립 | 구현자 E | Preview·Api는 Contracts, Host는 전부 |
| eval/DbRipple.Eval, Eval.Tests, Eval.Cli | C16 | 구현자 F | Eval은 Contracts, Cli는 전부 |

**편집 규칙**
- 각 구현자는 자기 src 프로젝트와 자기 테스트 프로젝트만 고친다.
- Contracts·TestSupport·slnx·Core·docs는 고치지 않는다. 계약 변경이 필요하면 오케스트레이터에 요청한다. 변경은 추가만 한다.
- C10·C11 담당(B)은 CascadeLab RawCapture·CollectResult 픽스처를 `tests/fixtures/capture/<사례>/` 아래 JSON(ContractJson)으로 낸다. C13 담당(D)은 읽기만 한다.

**아키텍처 결정**
- **AD1** 컴포넌트 프로젝트는 Contracts만 참조한다. C10은 C12를 `INetDiffer` 생성자 주입으로 받는다. 구현체 조립은 Host와 Eval.Cli 두 곳에서만 한다.
- **AD2** Api는 Microsoft.AspNetCore.App 프레임워크 참조를 가진 클래스 라이브러리이고, 실행 파일은 Host다. Api 테스트는 TestServer와 가짜 서비스로 돌린다.
- **AD3** 파이프라인(§5.2 조립)과 C14 저장소는 ASP.NET에 의존하지 않는 Preview 프로젝트에 둔다. 평가 CLI도 이것을 쓴다.
- **AD4** §7.2 1·2단계(문장 나누기, 괄호)는 C11이 맡는다. 6단계의 "전위 순서 마지막 묶음"을 정하려면 이 트리가 필요하기 때문이다. C13은 `CollectResult.ContextLinks`를 받아 3~7단계를 한다.
- **AD5** 평가 하네스 핵심(Eval)은 제품 컴포넌트를 참조하지 않는다(§10.3). Eval.Tests가 어셈블리 참조로 이를 검사한다.

## 2. 계약 요약 (src/DbRipple.Contracts)

**공통 규칙**
- 직렬화 설정은 `ContractJson`이다: camelCase, 설계 값 그대로의 열거형 문자열, null 포함.
- 테이블·트리거 이름은 "스키마.이름" 표시형이다. 감사 헤더는 대괄호 인용형이므로 C11이 tableObjectId로 바꾼다.
- 값은 §6.1 문자열 사전이다.
- Core 타입(DbCatalog, CaptureManifest, Fingerprint, TableInfo, KeyKind)을 재사용한다.

**인터페이스(§4 대응)**
- `IBatchAnalyzer.Analyze(sql, BaselineContext)` → `AnalysisResult{Statements, Rejects, Warnings, Diagnostics, IsAccepted}`
- `ITriggerAnalyzer.Analyze(DbCatalog)` → `TriggerStaticInfo[]`
- `IPreviewExecutor.ExecuteAsync(analysis, baseline, PreviewOptions, IEvaluationHook?, ct)` → `RawCapture`. 생성자는 (연결 문자열, ExecutionSettings, INetDiffer).
- `IChangeCollector.Collect(raw, analysis, baseline)` → `CollectResult{Firings, RowChanges, ContextLinks, RereadDifferences}`
- `INetDiffer.DiffAsync(SqlSession, liveDb, snapshotDb, IReadOnlyList<TableInfo>, ct)` → `NetChange[]`. SqlSession은 연결과 트랜잭션을 함께 담는다.
- `ICausalResolver.Resolve(ResolveInput)` → `Resolution{CausalModel, GraphDto}`
- `IPreviewStore`: Save, Get, PageEvents, PageNet, Export, Remove
- `ISandboxManager`: GetStatus, GetContext, Refresh, StartRefresh, AcquirePreviewLock, MarkDirty
- `IPreviewPipeline.RunAsync(sql, options, hook, ct)` → `PreviewOutcome`
- `IEvaluationHook.OnBeforeRollbackAsync(EvaluationHookContext, ct)`

**2주차 인계 규칙**
- C10: 문장 뒤 XACT_STATE가 −1(doomed)이면 ROLLBACK 뒤 REVERT.
- isToolFault: 오류 프로시저 이름의 마지막 부분이 `dbr_audit_`로 시작하는지로 판정(`ErrorClassification.IsToolFault`).
- C11: 최종 after는 전위 순서의 마지막 묶음.

**API(§6.4 경로)**
- 거부: 422 ProblemDetails + `rejects[]`
- 409: 준비 안 됨, 잠금, 지문 불일치
- 501: 아직 구현 안 됨(`/catalog/schema`, `/apply`는 3주차)
- 400: 입력 검증

## 3. DB 격리 규칙

- 컨테이너는 dbripple-sql 1개(127.0.0.1:14333)다. 연결 문자열은 `. .\scripts\env.ps1`로 프로세스 안에서만 만들고 출력하지 않는다. 테스트는 `. .\scripts\env.ps1; dotnet test tests\<자기 테스트>`로 돌린다.
- 테스트 DB 이름은 `dbrt_<코드>_<태그>_<8자리 16진>`이다. 코드는 ana(A), exe(B), dif(C), att(D), api(E), eval(F), sup(지원)이다. `TestDb.NewName`으로 만들고, `TestDb.DropAsync`는 dbrt_ 접두가 아닌 DB를 거부한다.
- 기준선이 필요하면 자기 전용으로 만든다.
  - `TestBaseline.BuildCascadeLabAsync(code[, RecursiveTriggers])`: 약 7초.
  - `TestBaseline.BuildAdventureWorksAsync(code)`: AW_Origin을 거치지 않고 백업에서 복원한다. 디스크를 수백 MB 쓰므로 B와 C만 쓴다.
- 공유 DB
  - AW_Origin은 쓰기 금지다.
  - 병렬 단계 동안 AW_Sandbox·AW_Sandbox_base, CascadeLab·CascadeLab_RT와 그 샌드박스·스냅샷, AW_Spike*에는 DML을 하지 않는다(롤백하더라도). AW 기준선 재구축도 금지다.
- 서버 설정(sp_configure)은 바꾸지 않는다. 사용자와 RECURSIVE_TRIGGERS는 자기 DB 안에서만 만든다.
- 남은 DB는 `TestDb.DropOrphansAsync(code)`로 손으로만 치운다.
- 같은 체크아웃에서 동시에 빌드하므로 각자 자기 테스트 프로젝트만 빌드·테스트한다. Host와 Eval.Cli는 다른 구현자의 코드를 함께 컴파일하므로 병렬 단계에서는 빌드하지 않는다. `dotnet build DbRipple.slnx`와 `dotnet clean`은 오케스트레이터만 한다.

## 4. 컴포넌트별 완료 기준과 테스트

**C6 (A)**
- D5 거부 범주마다 규칙 ID, 위치, 근거 docRef를 내고 경고 4종을 낸다.
- SP4 동작을 재현한다(GO, 주석·문자열 안 GO, `GO 2` 거부). 문장 원문을 정확히 잘라낸다.
- 트리거 분석: AW 트리거 10개의 쓰기 대상이 SP4 결과와 같고, CascadeLab 트리거도 맞는다.
- 테스트: 단위 테스트 + SqlFact(dbrt_ana_ CascadeLab 기준선 카탈로그). CL01~CL08 문장이 모두 수락된다.

**C10 (B)**
- 실행 봉투: 풀링 끔, XACT_ABORT ON, @@OPTIONS 점검.
- 경계 0, 문장마다 EXECUTE AS → 실행 → XACT_STATE 확인 → (doomed면 ROLLBACK) → REVERT.
- failed·trigger_blocked·not_run 판정, isToolFault 판정. 상한을 넘으면 KEYS, truncated.
- 재조회는 Core AuditValueExpressions로 같은 형식을 만든다. 최종 차이는 INetDiffer로(평가 모드는 전체 테이블). 훅을 부르고 finally에서 ROLLBACK한다.
- 실패 뒤에는 롤백 후 전체 테이블을 비교하고(ResidualCommitted) SandboxDirty를 표시한다.
- 테스트(dbrt_exe_ CascadeLab):
  - CL01~CL08 헤더가 `cascadelab/verification/logs/audit-2026-11-10.json`과 같다.
  - 상한 1로 truncated가 된다.
  - NO ACTION 위반이면 failed + not_run이다.
  - R18 재현(계산 열 오버플로)이면 isToolFault = true이고 doomed 처리가 된다.
  - 실행 뒤 샌드박스 대 스냅샷 차이가 0이다.

**C11 (B)**
- 문장 나누기, 괄호(NONE 보완 포함), 짝짓기 4종, 값 비교로 바뀐 열 계산, volatile·derived 처리, §6.1 숫자 원문 보존, KEYS 모드.
- 전위 순서 최종 after(CL08a·CL08b), RereadDifferences.
- 테스트: CL01~CL08의 firings가 손 정답 expected.firings(테이블·연산·rowCount·depth·pairing)와 같다.

**C12 (C)**
- PK, UNIQUE, 다중집합으로 비교한다. §6.1 정규화는 C11과 같은 Core 식을 쓴다. 결과 순서는 결정적이다.
- 열린 트랜잭션에서 동작하고, 트랜잭션 없이도(실패 뒤 검사) 동작한다.
- 테스트(dbrt_dif_): 형식별 테이블, 힙 중복, PK 변경. CL01~CL08 순 변경이 `cascadelab/verification/logs/oracle-2026-11-10.json`과 같다. 끝으로 SP7을 한다.

**C13 (D)**
- §7.2 3~7단계: FK 판정과 certain 조건, 트리거 후보, INSTEAD OF 가상 노드, 잔차 범주 순서, ExplainedBy·ParentRowIds.
- §6.3 그래프: WRITES 확신도가 FIRES를 넘지 않음, 노드 200개 초과 시 group으로 접기.
- 테스트: CL01~CL08 expected.edges의 M3 키(문장, 부모, 자식, 종류, 이름)와 확신도가 같고, certain 과신 오류가 0이다.

**파이프라인·C8·C14·C5 (E)**
- 파이프라인 순서(§5.2), 거부면 저장하지 않음, 오염이면 재구축, 잠금(409), sqlHash, netSummary, timings.
- SandboxManager: manifest.json, 카탈로그, 트리거 분석, 지문, 상태.
- 저장소: 활성 1건, 30분 만료, 파일 보관, 페이지·firingId 필터, 내보내기, 삭제.
- 테스트: 가짜 서비스 + TestServer, SqlFact로 dbrt_api_ CascadeLab 기준선에서 SandboxManager.

**C16 (F)**
- 비교기 자체 검증(NULL↔NULL, 힙 중복, xml, hierarchyid).
- 오라클: 전용 원본에서 CL01~CL08 결과가 oracle-2026-11-10.json과 같다. 원문 배치 경로도 확인한다.
- 훅(감사 스키마 제외), M1·M2 계산 단위 테스트, CSV, 독립성 테스트.
- CLI `cascadelab`로 cases·m1·m2·metrics.csv와 env.json을 낸다.

## 5. M1·M2 CSV 열 명세 (eval/DbRipple.Eval/MetricsCsv.cs)

**공통 형식:** UTF-8(BOM 없음), RFC 4180, 소수는 InvariantCulture 소수점 4자리, 분모가 0인 비율은 빈 칸. `system`은 preview, B0, B2다.

- **cases.csv**(실행·시스템·사례·반복당 1행): run_id, system, case_id, pattern, database, repeat, preview_id, status_expected, status_actual, status_match, error_expected, error_actual, statements_total, statements_status_match, oracle_vs_hand_keys_match, raw_batch_match, analyze_ms, execute_ms, read_log_ms, reread_ms, net_diff_ms, resolve_ms, serialize_ms, oracle_execute_ms, record_path
- **m1.csv**(comparison당 1행: total = 미리보기 대 오라클, obs = 훅 대 오라클, cap = 미리보기 대 훅): run_id, system, case_id, repeat, comparison, truth_cells, predicted_cells, tp, fp, fn, precision, recall, f1, rows_union, rows_exact, row_exact_rate, volatile_cells, volatile_present_format_ok, residual_filled_cells, residual_fill_rate
- **m2.csv**(cause당 1행: DIRECT, FK_CASCADE, FK_SET_NULL, FK_SET_DEFAULT, TRIGGER, SAME_TABLE_TRIGGER, ONE_OF): run_id, system, case_id, repeat, cause, truth_rows, found_rows, recall, found_by_firing, found_by_residual_only, missed_keys
- **metrics.csv**(스위트 마이크로 평균): run_id, system, metric, scope, numerator, denominator, value, cases. 실행 환경은 env.json에 따로 남긴다.

**M1 셀 정의**
- 셀 사실은 (테이블, 키, 열, 값)이다. INSERTED는 모든 열, UPDATED는 바뀐 열, DELETED는 열 "*"·값 "DELETED" 사실 1개로 센다.
- 비결정 셀은 tp·fp·fn에서 빼고 존재·형식만 따로 센다. IDENTITY 짝 매핑은 CL16이 있는 3주차에 한다.
- M1은 (테이블, 키, 열)당, M2는 (테이블, 키)당 한 번만 센다.
- **예측 셀의 출처(2026-11-10 결정):**
  - total(E_total)은 PreviewRecord.NetChanges(사용자가 보는 최종 차이)를 쓴다.
  - cap(E_cap)은 RowChanges만으로 재구성한 순 변경을 쓴다. 테이블·키마다 처음 before와 최종 after를 쓰고, 최종 after는 전위 순서 마지막 묶음이다.
  - 잔차로만 설명된 셀은 cap 예측에 넣지 않는다. 대신 residual_filled_cells·residual_fill_rate로 따로 센다. 그래야 E_cap이 차이 엔진끼리의 비교가 아니라 수집·재구성 오류를 잰다(§10.4).
  - 재구성 접기는 Eval 안에 독립 구현한다.
- 손 정답의 트리거 실행 이름은 같은 이름의 `#2`가 없으면 `#1`을 떼고 비교한다(CL08a 표기).

**M2 판정:** found는 제품 NetChange에 같은 (테이블, 키, 변경 종류)가 있는 경우다. 손 정답의 causes로 묶어 세고, oneOf인 행은 ONE_OF로 센다.

## 6. SP7 (§13)

- **담당과 시점:** 구현자 C, C12 첫판 직후. 통합 전에 결과가 있어야 한다.
- **확인할 것:** REVERT 뒤 같은 연결·트랜잭션에서 미커밋 변경을 스냅샷과 비교했을 때 정확한지, AW 전 테이블 기준 시간(목표: 수 초).
- **환경:** `TestBaseline.BuildAdventureWorksAsync("dif")`로 만든 전용 기준선.
- **방법:**
  - 전용 DB 안에 DML 권한만 가진 사용자를 만든다.
  - BEGIN TRAN 뒤 EXECUTE AS로 DM1(SOH 1건 DELETE)·DM2(SOD OrderQty UPDATE)를 실행하고 REVERT한다.
  - 전 사용자 테이블(감사 제외)에 NetDiffer를 돌리고, 정확성은 독립 확인(SOH 삭제에 연쇄된 SOD·SOHSR 행 수, 트리거 대상 행 키)으로 판정한다.
  - 워밍업 1회 뒤 5회의 p50·p95를 잰다. 실패 경로(트랜잭션 없이 롤백 뒤 전체 비교)의 시간도 잰다.
  - 끝나면 ROLLBACK하고 샌드박스 대 스냅샷 차이가 0인지 확인한다.
- **기록:** `logs/week2/sp7-*.log`. SP7.md는 오케스트레이터가 쓴다.
- **실패하면:** §13대로 재조회와 KEYSET 비교로 낮추고, C10의 최종 차이 범위를 줄인다.

## 7. 통합 순서

- **D0(완료):** 계약과 골격.
- **D1:** 각자 핵심 로직과 단위 테스트(가짜 구현 사용). C6은 트리거 분석을 먼저 낸다. C12 첫판.
- **D2:** SP7. C10이 CL01~CL08 픽스처를 `tests/fixtures/capture`에 낸다. C11·C13이 그 픽스처로 회귀한다. E는 SandboxManager와 저장소를 마친다.
- **D3:** 통합. Host·Eval.Cli를 진짜 구현으로 조립하고, 파이프라인 CL01을 끝까지 돌린 뒤 CL01~CL08로 넓힌다.
- **D4:** 평가 CLI로 CL01~CL08 M1·M2 CSV를 낸다. 손 정답과 다르면 정정 기록을 남기고 결함을 고친다.
- **D5:** 매일 회귀, 여유분, 3주차 인계.

## 8. 위험

1. 계약 변경이 번진다 → 계약은 고정하고, 추가만 오케스트레이터 경유로 한다.
2. C11(감사 JSON)과 C12(최종 차이)의 §6.1 정규화가 어긋나면 거짓 잔차가 생긴다 → 둘 다 Core AuditValueExpressions를 쓰고, 통합 때 같은 행 대조 테스트를 둔다.
3. 괄호 판정이 C11·C13에 중복될 수 있다 → C13은 ContextLinks만 쓴다.
4. 같은 체크아웃의 동시 빌드에서 파일 잠금(MSB3026/3027)이 날 수 있다 → 자기 프로젝트만 빌드한다.
5. 컨테이너 자원(메모리 4GB, AW 전용 기준선 여러 벌) → AW는 B·C만 쓴다.
6. XACT_ABORT ON일 때의 doomed·REVERT 동작은 SP10(XACT_ABORT 미지정)과 다를 수 있다 → C10 테스트로 실측한다.
7. SqlError.Procedure 형식은 실측하지 않았다 → C10이 실측한다.
8. 하네스와 제품이 TestSupport의 시드 실행기를 함께 쓴다(비교·지표는 독립) → 오라클 결과를 1주차 oracle-2026-11-10.json과 대조한다.
9. 통합일에 일정이 몰릴 수 있다 → D2까지 픽스처 기반 회귀를 마친다.

## 9. 열린 질문과 결정

계약 주석의 Qn과 같은 번호다. "결정"은 2026-11-10 오케스트레이터가 정한 2주차 기본값이고, 설계 문서 반영은 2주차 끝에 한다.

| 번호 | 질문 | 결정 |
|---|---|---|
| Q1 | §4 시그니처 확장(Analyze에 baseline, Collect에 analysis·baseline, Execute에 options·hook) | 채택. 설계 §4에 반영 대기 |
| Q2 | §7.2 1·2단계를 C11로(AD4) | 채택. 설계 §4 C11·C13에 반영 대기 |
| Q3 | 진단 규칙 ID 전체 목록 | C6이 정해 보고하면 오케스트레이터가 계약 상수로 올린다 |
| Q4 | 스키마를 생략한 이름의 해석 | 실행 사용자의 기본 스키마로 해석하고, 2주차 실행 사용자는 기본 스키마가 dbo다(Q12) |
| Q5 | 빈 스크립트·주석뿐인 스크립트 | 거부(IsAccepted = false)와 진단 1개 |
| Q6 | 암호화·파싱 실패 트리거의 쓰기 대상 | 알 수 없음으로 두고, 그 트리거가 낸 후보는 ambiguous 이하로 둔다 |
| Q7 | COMPUTED·SEQUENCE 사유 | 현재 계약대로(COMPUTED → derived, SEQUENCE_DEFAULT 미사용) |
| Q8 | Residual.detectedBy 철자 | "NET_DIFF" |
| Q9 | 오류·group 노드 id | err-n, g-n |
| Q10 | Statement.error 필드 | errorNumber, message, procedure, isToolFault |
| Q11 | RowChange 세부(PK가 바뀐 UPDATE의 key 등) | C11 담당이 정하고 계약 주석과 테스트로 남긴다 |
| Q12 | 실행 사용자 | `dbr_preview_user`(WITHOUT LOGIN, 기본 스키마 dbo, DML 권한만)로 EXECUTE AS한다. null(가장 안 함)은 테스트에서만 허용한다. **수정(2026-11-10, exec-1 보고):** AW의 DDL 트리거 `ddlDatabaseTriggerLog`가 CREATE USER·GRANT에도 발동해 `dbo.DatabaseLog`에 행을 자동 커밋한다(SP10 로그 `sp10-20-aw-part1.log:6`). 그래서 사용자는 기준선 구축 중 C9 설치(5단계) 전에 만든다. Core BaselineBuilder에 이 단계를 넣는 일은 통합 때 오케스트레이터가 한다. 실행기는 멱등 검사만 하고, 만들게 되면 SandboxDirty를 알린다 |
| Q13 | 실패로 트랜잭션이 끝났을 때 평가 훅 | 부르지 않는다 |
| Q14 | 형제 순서를 정할 수 없을 때 RereadDifference | C11 담당이 정한다 |
| Q15 | partial·auditNotLast 계산 위치 | 현재대로(firingId로 계산) |
| Q16 | netSummary 다중집합 건수 | multiplicity 합 |
| Q17 | sqlHash 입력 | UTF-8, 정규화 없음 |
| Q18 | warnings 원소 형식 | Diagnostic |
| Q19 | 내보내기 형식 | PreviewRecord(원문 SQL 포함) |
| Q20 | SQL 최대 길이 | 1,000,000자, 넘으면 400 |
| Q21 | 행 페이지 최대 limit | 1000 |
| Q22 | ProblemDetails type URI | 설계 예시 그대로 |
| Q23 | GET /api/catalog/schema | 3주차(프런트와 함께). 2주차는 501 |
| Q24 | ApplyResult 원소 형식 | 3주차(C15). 2주차는 501 |
| Q25 | sqlType 열 메타데이터 위치 | 3주차(프런트와 함께) |
| Q26 | captureSeqRange | 그 문장 묶음의 최소·최대, 없으면 null |
| Q27 | trigger_blocked와 failed의 규칙 | C10 담당이 실측해 정하고 테스트로 남긴다 |

설계 문서에 반영할 것(2주차 끝): Q1·Q2·Q7·Q12 결정, §4 C14 목록에 Remove, §4 C8에 StartRefresh(202).
