# 3·4주차 계획 (2026-11-11)

설계 [architecture.md](architecture.md) v1.4 §11.3의 3·4주차 범위에 [week2-plan.md](week2-plan.md) §0의 인계 9개를 더한 계획이다.

**사용자 지시:** PC 자원이 부족하니 이번 라운드는 코드 위주로 구현한다. 테스트 실행, DB·Docker 사용, `npm install`은 하지 않는다. 빌드는 자기 프로젝트만 `-m:1`로 한다.

## 결과 (2026-11-11, 코드 위주 라운드, 미커밋)

**코드는 다 썼고, 실행 확인은 하나도 하지 않았다.** C# 쪽은 솔루션 전체가 경고·오류 0으로 컴파일된다(Host·Eval.Cli 포함, `-p:ApplyIntegration=true`의 Apply DB 테스트 포함). 테스트·DB·평가·Host 실행과 `npm install`은 하지 않았다(사용자 지시).

| 단위 | 담당 | 결과(정적) |
|---|---|---|
| C15 적용 | apply-1 | ApplyService(§5.3 12단계), ApplyMatcher(D7 6규칙), 드리프트·KEYSET·원본 스냅샷. 테스트는 작성만 |
| 플랫폼 | platform-1 | /catalog/schema, /apply, 예외 → 409 reason·503·500, Host DI·appsettings·.http |
| 엔진 | engine-1 | StatementRunner(AD6), 프로시저 펼치기(깊이 4), 3609 두 경우, 인계 7, 미리보기 행 수를 `ROWCOUNT_BIG`으로 통일, PairingOverrides 삭제 |
| 평가 | eval-2 | M1 IDENTITY 짝, M3, M4(예외 반복 제외), M5, B0·B2, report.md, CLI `cascadelab --systems`·`m5`·`report` |
| CascadeLab | lab-1 | CL09~CL20 사례 19개, answer-format v0.2, pairing 정정 6건, SP8 조회, 데모 대본, 원본 실행 사용자 단계(Q30) |
| 프런트 | web-1 | web/ Vue 3 앱(C1~C4, /sp9). 설치·컴파일 안 함 |

**이번 라운드에서 정한 것:** Q30(원본 실행 사용자는 `restore-aw-origin.ps1`에서 만든다), 프로시저 펼치기 깊이 4(AW uspLogError → uspPrintError), 미리보기와 [적용]의 행 수를 같은 `ROWCOUNT_BIG` 방식으로(D7 1), `StatementRunOutcome.TriggerRollbackConfirmed` 계약 추가.

**독립 정적 검토(reviewer-2):** 치명 0. 중간 3건(COMMIT 예외를 "롤백함"으로 단정, StatementRunner REVERT 대체 경로의 TransactionOpen, M4가 예외 반복을 안정으로 셈)과 낮음 5건(웹 sqlHash, 확인 단계 포커스, 적용 중 미리보기, KEYSET 조용한 강등, 설정 오류 400)을 고쳤다. 남긴 것:
- 값이 같은 새 IDENTITY 행 여러 개는 늘 IDENTITY_UNPAIRED로 막힌다(안전 쪽 과보수).
- [적용]의 롤백 뒤 남은 변경 검사가 그 사이 다른 세션 커밋에 오염될 수 있다(추정).
- 로컬 API(127.0.0.1:5080)의 POST에 CSRF 방어가 없다. /apply는 미리보기 id가 필요하지만 /sandbox/refresh는 아니다. 보안 검토 대상.
- `web/`에 package-lock.json이 없다(첫 `npm install` 때 생긴다).

**테스트 라운드 우선순위:** §7 명령에 더해,
1. 프런트 설치·타입 검사(패키지 버전 일부는 기억으로 고정: @vue-flow/core 1.48.0, @dagrejs/dagre 3.1.0, ag-grid 36.0.0, @codemirror/view 6.36.8).
2. Execution: `Runner_matches_preview_executor_on_q27_cases`, ROWCOUNT_BIG 실측(INSTEAD OF CL11·CL11b, 문장 앞 `;`), 3609 두 경우.
3. (2026-11-12: 공유 CascadeLab 재설치는 필요 없었다. 테스트가 dbrt_ DB에 cl*.sql을 직접 시드한다.) Analysis → Execution(`DBR_WRITE_FIXTURES=1`) → Attribution 순서로 픽스처와 회귀. CL19의 `hierarchyid::Parse`·`geography::Point` 수락 여부, 손 정답 확신도 두 곳(CL09 Q→K#2, CL10 Counter).
4. Apply 단위·DB 테스트(`-p:ApplyIntegration=true`), 권한·스냅샷 불가 오류 번호 실측, AW DM1~DM3 [적용] committed와 DM7 drift_detected.
5. Host DI 실제 기동, /apply 빈 본문, OpenAPI와 손 TS 타입 대조.
6. 평가: `cascadelab --systems preview,B0,B2 --repeat 20`, `m5`, `report`.

## 0. 이번 라운드 완료 기준

- 맡은 범위의 코드가 있고, 자기 프로젝트가 `dotnet build <proj> -m:1 -nologo`에서 경고 0·오류 0이다.
- 테스트는 작성하되 실행하지 않는다. DB 테스트는 `[SqlFact]`로 쓴다.
- 프런트는 `web/`에 소스와 `package.json`(버전 고정)만 둔다. 설치·빌드·타입 검사는 하지 않는다.
- Host(`src/DbRipple.Host`)와 Eval.Cli는 모든 구현자의 코드를 함께 컴파일하므로 병렬 단계에서는 빌드하지 않는다. 통합 때 오케스트레이터가 빌드한다.

## 1. 작업 단위와 소유자

같은 파일은 한 명만 고친다. 계약(Contracts), TestSupport, slnx, docs, Integration.Tests는 오케스트레이터 몫이다.

| 단위 | 소유자 | 범위(설계 절) | 소유 파일·폴더 |
|---|---|---|---|
| C15 적용 | apply-1 | §5.3, D7, D8(잘린 결과는 적용 차단), E2, E13 | `src/DbRipple.Apply/**`, `tests/DbRipple.Apply.Tests/**` |
| 플랫폼 | platform-1 | §6.4(/catalog/schema Q23, /apply Q24), C5, C8, C14, 인계 7(API 쪽) | `src/DbRipple.Preview/**`, `src/DbRipple.Api/**`, `src/DbRipple.Host/**`, `tests/DbRipple.Api.Tests/**` |
| 엔진 | engine-1 | C6, C7(Core는 추가만), C10, C11, C12, C13, AD6 StatementRunner, 인계 1·3·5·6·7(실행기 쪽)·8(테스트 쪽), E22 | `src/DbRipple.Core/**`, Analysis·Execution·Diff·Attribution과 각 `*.Tests`, `tests/DbRipple.Core.Tests/**` |
| 평가 | eval-2 | §10.3~10.6, M1(IDENTITY 짝)·M3·M4·M5, B0·B2(B1은 선택), 인계 2·4·6(오라클 쪽), 보고서 생성 | `eval/**` |
| CascadeLab | lab-1 | §10.2 CL09~CL20, 손 정답, 정정(인계 8), SP8 스크립트, DM1~DM7 대본, 원본 실행 사용자 준비(Q30) | `cascadelab/**`, `spikes/sql/sp8-*.sql`, `scripts/demo/**`, `scripts/restore-aw-origin.ps1` |
| 프런트 | web-1 | C1~C4, SP9, D6 프런트 스택 | `web/**` |

### apply-1 (C15)

- `ApplyService`가 §5.3 절차를 그대로 구현한다(클래스 주석에 12단계).
  1. 선행 조건(ApplyPrecondition)을 확인한다.
  2. 문장 원문을 얻으려고 `IBatchAnalyzer`로 다시 분석한다.
  3. 원본 스냅샷을 만든다(Core `BaselineSql.DropSnapshot`·`CreateSnapshot`·`SnapshotPath`, 원본 `sys.database_files` 논리 이름). 실패하면 KEYSET으로 낮춘다.
  4. 전용 연결에서 `IStatementRunner.PrepareAsync`를 부르고 `BEGIN TRAN`을 연다.
  5. 드리프트를 검사한다: 미리보기 NetChanges의 before 행을 UPDLOCK, HOLDLOCK으로 읽어 기준선 값과 비교한다.
  6. 문장마다 `RunAsync`로 실행한다.
  7. `INetDiffer`로 원본 대 원본 스냅샷의 최종 차이를 구한다(전체 사용자 테이블에서 감사 저장소 제외).
  8. `ApplyMatcher`로 D7 규칙을 판정하고 COMMIT 또는 ROLLBACK 한다.
  9. finally에서 남은 트랜잭션을 ROLLBACK하고 원본 스냅샷을 지운다.
  10. 잠금을 푼 뒤 커밋이면 미리보기를 지우고 `StartRefresh()`를 부른다.
- `ApplyMatcher`(D7 6규칙): 새 IDENTITY 행은 비결정 열을 뺀 값으로 짝을 짓고, 그 IDENTITY를 참조하는 자식 FK 값은 짝 매핑으로 치환한다(`BaselineContext.Catalog.ForeignKeys`). Eval과 코드를 공유하지 않는다(§10.3).
- 트리거 거부나 오류가 나면 원본 스냅샷과 비교해 `ResidualCommitted`를 채운다. 이 비교가 예외로 끝나면 `ResidualCheckFailed = true`.
- 테스트(작성만):
  - Matcher 단위: CL16형 IDENTITY와 자식 FK, 비결정 열, 미리보기에 없는 테이블, netSummary 차이
  - 서비스 흐름 단위(가짜 runner·differ·store·sandbox): 선행 조건 5종, drift 시 ROLLBACK, mismatch 시 ROLLBACK, error 시 residual 검사, 스냅샷 실패 시 KEYSET, 예외가 나도 finally에서 ROLLBACK, 커밋 뒤 새로 고침
  - `[SqlFact]`(dbrt_apl_ CascadeLab 기준선): CL01 committed, 원본 행을 바꾼 뒤 drift_detected, 원본에 행을 끼워 넣은 뒤 rolled_back_mismatch, CL12 error와 롤백 뒤 남은 변경, ForceKeyset 경로

### platform-1 (Preview·Api·Host)

- **/catalog/schema:** Preview에 `CatalogSchemaProvider : ICatalogSchemaProvider`.
  - 입력은 SandboxManager 문맥과 샌드박스 `sys.columns`의 max_length·precision·scale 자체 조회다. `SqlTypes.Format`으로 sqlType을 만들고 기준선(BaselineAt)마다 캐시한다.
  - 매니페스트의 covered, excluded reason, volatile, unobservable을 옮기고, COMPUTED는 `IsComputed`로 옮긴다. 감사 저장소(audit.*)는 뺀다.
- **/apply:** 501을 실제 엔드포인트로 바꾼다. 본문은 선택(`ApplyRequest`, 빈 본문 허용). 응답: 200 ApplyResult(상태 5종 모두), 404(`null`), 409(`PreviewConflictException`), 503(`SandboxUnavailableException`).
- **인계 7, ApiExceptionHandler:** `PreviewConflictException` → 409(ProblemDetails 확장 `reason`), `SandboxUnavailableException` → 503, `ArgumentException` → 400, `NotImplementedException` → 501, 그 밖의 `InvalidOperationException` → 500.
- SandboxManager·PreviewPipeline의 409 경로(잠금, building, 기준선 없음, 지문 불일치, 계속 오염)를 `PreviewConflictException(reason)`으로 바꾼다. Api.Tests의 `Assert.Throws<InvalidOperationException>`(정확한 형식 비교)도 함께 고친다.
- **Pipeline:** `ResidualCheckFailed`와 Message를 PreviewResult로 옮기고, 실패면 MarkDirty를 부른다. `SqlHash`·`Summarize`는 `ContractFormulas`에 위임한다.
- **Host:** Apply 프로젝트 참조. DI에 `IStatementRunner`(Execution `StatementRunner`), `IApplyService`(`ApplyService`), `ICatalogSchemaProvider`, `ApplySettings`. appsettings `DbRipple` 절에 `ApplyCommandTimeoutSeconds`, `ApplyToolTimeoutSeconds`, `OriginSnapshotSuffix`, `ForceKeyset` 추가. `.http`에 catalog·apply 예시.
- **OpenAPI:** 새 TypedResults가 문서에 나오게 한다(테스트 라운드에서 openapi-typescript 타입과 손 TS를 대조).

### engine-1 (Core·Analysis·Execution·Diff·Attribution)

- **AD6:** Execution에 `StatementRunner : IStatementRunner`(매개변수 없는 생성자). `PreviewExecutor.EnvelopeSql`·`RequiredOptions`·`ForbiddenOptions`와 `ErrorRules.Classify`를 재사용한다. 순서: EXECUTE AS → 실행 → @@TRANCOUNT·XACT_STATE·CURRENT_TRANSACTION_ID 확인 → doomed면 ROLLBACK → REVERT. 연결이 끊기면 `SandboxUnavailableException`. PreviewExecutor가 이 클래스를 쓰도록 바꾸는 일은 테스트 라운드 뒤로 미룬다.
- **인계 1(프로시저 펼치기):**
  - Core: `CatalogQueries`·`CatalogReader`에 프로시저 모듈(`sys.sql_modules` 중 프로시저)을 추가한다. DbCatalog 끝에 기본값 null인 매개변수로 붙여 비파괴로 한다.
  - Analysis TriggerAnalyzer: 프로시저를 한 단계 펼쳐 쓰기 대상을 WriteTargets에 합치고, `ExpandedProcedureCalls`와 `UnresolvedProcedureCalls`에 나눠 담는다.
  - Attribution: WRITES 하향은 진짜 미상(`WriteTargetsUnknown`이거나, `UnresolvedProcedureCalls`가 있고 그 테이블이 WriteTargets에 없을 때)에만 한다. FK 경합은 미상을 유지한다.
  - 호환(Q38): 두 목록이 모두 비고 ProcedureCalls가 있으면 전부 미상으로 본다.
- **인계 3:** 거부 유지(Q34). 코드를 바꾸지 않는다.
- **인계 5:** 3609 남은 두 경우(연쇄 대상의 ROLLBACK 트리거가 CATCH에서 자동 커밋 로그를 쓰는 경우, 트리거가 COMMIT 뒤 RAISERROR를 내는 경우)를 ErrorRules·PreviewExecutor에서 다루고 테스트를 쓴다.
- **인계 6:** rowsAffected는 Q33 기본안(같은 명령 끝에 `SELECT ROWCOUNT_BIG();`)으로 StatementRunner에 구현한다. 대소문자만 바꾸는 키 UPDATE는 DB 정렬 규칙(UPDATED) 유지(Q35).
- **인계 7(실행기 쪽):** 실패 경로의 `DiffAsync` 예외를 잡아 `ResidualCheckFailed = true`, Message, `SandboxDirty = true`로 결과에 담는다.
- **인계 8:** lab-1이 정정을 반영하면 `tests/DbRipple.Execution.Tests/CascadeLabTests.cs`의 `PairingOverrides`를 지운다.
- **E22:** 비활성 FK와 비활성 트리거가 후보에서 빠지는지 Attribution 테스트를 쓴다.
- CL09~CL20 회귀 테스트를 Analysis(수락·거부), Execution(firings), Attribution(M3 간선)에 쓴다(실행하지 않음).

### eval-2 (eval/**)

- **M1:** IDENTITY 짝 매핑(CL16). Apply와 독립 구현.
- **M3:** 간선 키 기준 정밀도·재현율을 확신도별로. ambiguous는 정답이 후보에 든 비율, certain 과신 오류 수.
- **M4:** k회 반복해 형제 순서를 뺀 정규 그래프가 같은지, observed_only 변동률.
- **M5:** CL20 N 계열, 단계 시간은 timingsMs. 수집 오버헤드 = 샌드박스 execute ÷ 오라클 execute. 브라우저 렌더 열은 비워 두고 SP9 측정값으로 채운다(Q42).
- **대조군:** B0은 정답을 직접 대상 테이블로 거른 뒤 M2를 계산한다. B2는 오라클 차이를 예측으로 쓴다(M1은 오라클과 같고 M3 간선 0). "직접 찾아 SELECT해야 할 테이블 수"를 함께 낸다. B1은 선택(자를 목록 1순위).
- **인계 2:** 키 없는 테이블 ROW 키에서 계산 열과 관측 불가 열을 빼는 규칙을 훅과 E_cap 재구성 양쪽에서 같게 한다.
- **인계 4:** RAISERROR만 하는 트리거와 컴파일 오류 사례에서 오라클 after는 롤백 뒤 상태로 둔다.
- **인계 6(오라클 쪽):** 키 동등 비교를 DB 정렬 규칙에 맞추고, 다른 결과가 나오면 정정 기록을 남긴다.
- **CSV:** m3.csv, m4.csv, m5.csv 추가. 대조군 행은 system 열에 B0·B2. 열 명세는 MetricsCsv.cs 주석에.
- **CLI:** `cascadelab`에 `--systems preview,B0,B2`, 새 명령 `m5 --sizes …`, `report --run <dir>`(CSV만 읽어 실행 폴더에 `report.md`).

### lab-1 (cascadelab·SP8·데모·원본 준비)

- `cascadelab/sql/cl09.sql` ~ `cl20.sql`과 `cases/CL09…CL20*.json`. 패턴은 §10.2 표대로. CL12는 ROLLBACK, 0행 가드, @@IDENTITY. CL16은 IDENTITY, SEQUENCE, GETDATE, NEWID, 새 IDENTITY를 참조하는 자식.
- **제약:** TestSupport `CascadeLabSeed`가 `sql/cl*.sql`을 모든 CascadeLab 기준선에 싣는다. 시드는 작게, 자기 스키마(clNN) 밖은 건드리지 않고, 서버 설정은 바꾸지 않는다. CL20 대량 데이터는 `cl20.sql` 안의 프로시저 `cl20.usp_Generate @parents int`로만 만든다(Q39).
- **answer-format v0.2:** IDENTITY 행 표기 `{"match": {비결정 아닌 열: 값}, "identity": ["열"]}`와 비결정 셀 표기. eval-2와 먼저 합의한다.
- **인계 8:** CL07·CL08a·CL08b에 `corrections[]`를 더해 pairing 6건을 고치고 README "정정"을 갱신한 뒤 engine-1에 알린다.
- **SP8:** `spikes/sql/sp8-*.sql`로 AW DM1·DM2·DM3 전파를 관측하는 읽기 전용 조회. DM2는 SalesYTD가 실제로 바뀌는 주문을 고르는 조회 포함.
- **DM1~DM7 대본:** `scripts/demo/demo.http`, `scripts/demo/dm7-drift.sql`(로컬 AW_Origin 공개 샘플 사본의 한 행을 바꾸는 데모 전용). DM5(pubs)는 안내문만.
- **원본 실행 사용자(Q30):** `scripts/restore-aw-origin.ps1`에 복원 뒤 `dbr_preview_user`(WITHOUT LOGIN, 기본 스키마 dbo, 사용자 스키마 SELECT·INSERT·UPDATE·DELETE만)를 멱등으로 만드는 단계를 넣는다.

### web-1 (web/**)

- **스택:** Vue 3, Vite, TypeScript, Pinia. 편집기 CodeMirror 6 + `@codemirror/lang-sql`(MSSQL, onMounted에서 직접 마운트). 그래프 `@vue-flow/core` 1.48.x + `@dagrejs/dagre` 3.1.x. 그리드 AG Grid Community 36.x(Client-Side, 큰 묶음은 Infinite 모델 + 서버 페이지).
- `package.json` 버전 고정. Vite proxy `/api` → `http://127.0.0.1:5080`(CORS 없음). 타입은 §5를 손으로 옮겨 `web/src/api/types.ts`.
- **C1 편집기:** 스키마 자동완성(/catalog/schema 캐시, baselineAt이 키), Diagnostic 줄 표시.
- **C2 그래프:** 노드 6종·간선 7종, 확신도별 선 모양, `observed: false`는 점선, observed_only는 "관측 순서, 보장 안 됨", group 노드와 zeroRows·partial·auditNotLast 플래그(Q15).
- **C3 그리드:** event·net 보기 전환, 바뀐 셀 강조, volatile "예상값", derived "파생", MULTISET은 "제거된 행 n개, 추가된 행 m개".
- **C4 패널:** 문장별 결과, 테이블별 건수, 거부·경고, 지문, 수집 범위, 롤백 뒤 남은 변경, "잔여 확인 실패" 배너. [적용]은 status ok·fingerprintMatch·30분 안일 때만 켜고, 꺼지면 이유를 보인다. ApplyResult(drift·mismatch·KEYSET 한계)와 [취소].
- **E23·E24:** 0행 묶음 기본 숨김, event·net 보기 전환.
- **SP9:** 개발용 경로 `/sp9`에 합성 노드 50·200·500개와 그리드 1만·5만 행, 측정 버튼과 시간 표시.

## 2. 아키텍처 결정(추가)

- **AD6 실행 봉투 공유:** `IStatementRunner`(Contracts)는 Execution이 구현하고 C10·C15가 함께 쓴다(D7 "같은 실행 봉투"). 이번 라운드에는 새 클래스만 만들고 PreviewExecutor는 그대로 둔다.
- **AD7 Apply 참조:** Apply는 Contracts만 참조하고 Host가 조립한다. Apply와 Eval의 IDENTITY 짝 매핑은 서로 독립 구현이다.
- **AD8 스키마 공급:** /catalog/schema는 Preview 공급자가 만든다. sqlType은 Core를 바꾸지 않고 별도 조회와 `SqlTypes.Format`으로 만든다. 행 DTO에는 형식을 싣지 않고 프런트가 스키마로 찾는다.
- **AD9 예외 형식:** 409는 `PreviewConflictException`, 503은 `SandboxUnavailableException`, 그 밖의 `InvalidOperationException`은 500.
- **AD10 단일 잠금:** 미리보기와 적용은 같은 잠금을 쓴다. 새로 고침은 잠금을 푼 뒤 시작한다.
- **AD11 CL20 데이터:** 대량 데이터는 시드 프로시저로만 만들어 다른 테스트 기준선이 무거워지지 않게 한다.

## 3. 이번 라운드 계약 추가(src/DbRipple.Contracts)

- `Apply.cs`: ApplyResult와 원소들, IApplyService(§5.3, §6.4, D7, Q24·Q28·Q29)
- `StatementRunner.cs`: IStatementRunner(AD6)
- `CatalogSchema.cs`: CatalogSchema, ICatalogSchemaProvider, SqlTypes(Q23·Q25)
- `Errors.cs`: PreviewConflictException, SandboxUnavailableException, ContractFormulas(인계 7, Q32)
- 기존 형식에 추가: RawCapture·PreviewResult의 `residualCheckFailed`·`residualCheckMessage`(Q31), TriggerStaticInfo의 `expandedProcedureCalls`·`unresolvedProcedureCalls`(인계 1)
- TestSupport `TestDb.Components`에 `"apl"` 추가(오케스트레이터, 2026-11-11)

## 4. 동료끼리 주고받을 것

- engine-1 → apply-1, platform-1: `StatementRunner` 클래스 이름과 생성자, 3609 보정 규칙. 그 전까지 apply-1은 가짜 runner로 테스트를 쓴다.
- apply-1 ↔ platform-1: ApplyService 생성자는 골격에서 확정했다: `(connectionString, ApplySettings, IPreviewStore, ISandboxManager, IBatchAnalyzer, IStatementRunner, INetDiffer, TimeProvider)`. ApplySettings 설정 키를 맞춘다.
- engine-1 ↔ platform-1: 예외 형식. 실행기의 연결 끊김은 `SandboxUnavailableException`.
- platform-1 → web-1: §5와 달라지는 점, 409 `reason` 값.
- lab-1 → engine-1, eval-2: CL09~CL20 목록과 기대값, answer-format v0.2, `cl20.usp_Generate` 사용법. pairing 정정이 끝나면 engine-1에 알린다.

## 5. API 요청·응답 JSON 모양(프런트 손 TS용)

ContractJson 기준이다: camelCase, 열거형은 설계 문자열, null 속성도 포함한다.

```ts
type Iso = string;                                   // DateTimeOffset ISO 8601
type Values = Record<string, string | null>;         // §6.1: 값은 모두 문자열, NULL은 null
type KeyKind = 'PK' | 'UNIQUE' | 'ROW';
type Op = 'INSERT' | 'UPDATE' | 'DELETE';
type Confidence = 'certain' | 'inferred' | 'ambiguous' | 'unexplained';
type OrderGuarantee = 'guaranteed' | 'observed_only';
type Evidence = 'depth' | 'postorderBracket' | 'staticWriteSet' | 'fkKeyMatch' | 'catalogInsteadOf' | 'reread' | 'netDiff';
type VolatileReason = 'IDENTITY' | 'SEQUENCE_DEFAULT' | 'NONDETERMINISTIC_DEFAULT' | 'STATEMENT_FUNCTION' | 'TRIGGER_GETDATE' | 'ROWVERSION';
interface SourceSpan { startLine: number; startColumn: number; endLine: number; endColumn: number }
interface RowKey { kind: KeyKind; columns: string[]; values: (string | null)[] }
type ConflictReason = 'LOCKED' | 'BUILDING' | 'NO_BASELINE' | 'FINGERPRINT_MISMATCH' | 'DIRTY';
interface RejectItem { statementIndex: number | null; ruleId: string; span: SourceSpan }
interface ProblemDetails {             // 400·404·409·422·500·501·503
  type?: string; title?: string; status: number; detail?: string; instance?: string; traceId?: string;
  errors?: Record<string, string[]>;   // 400 ValidationProblem (예 errors.sql, errors["options.imageCap"])
  rejects?: RejectItem[];              // 422, type = "https://example.local/problems/rejected"
  reason?: ConflictReason;             // 409 (3주차 추가)
}

// GET /api/sandbox → 200
interface SandboxStatus { state: 'building' | 'ready' | 'dirty' | 'error'; baselineAt: Iso | null; fingerprintMatch: boolean; locked: boolean;
  manifest: { coveredTables: number; excludedTables: number; auditNotLast: number } | null }
// POST /api/sandbox/refresh → 202, 본문 없음(Location: /api/sandbox)

// GET /api/catalog/schema → 200 | 409
interface CatalogSchema { database: string; baselineAt: Iso; manifestVersion: string; tables: SchemaTable[] }
interface SchemaTable { schema: string; name: string; table: string; keyKind: KeyKind; keyColumns: string[];
  covered: boolean; excludedReason: string | null; columns: SchemaColumn[] }
interface SchemaColumn { name: string; sqlType: string /* "decimal(19,4)" 또는 "decimal" */; isNullable: boolean;
  isIdentity: boolean; isComputed: boolean; isRowVersion: boolean; volatile: VolatileReason | null; unobservable: boolean }

// POST /api/analyze  body { sql: string } → 200 | 400 | 409
interface Diagnostic { statementIndex: number | null; span: SourceSpan; severity: 'error' | 'warning'; ruleId: string; message: string; docRef: string | null }
interface AnalyzedStatement { index: number; batch: number; position: number; kind: Op; target: string; span: SourceSpan; sql: string; nondeterministicColumns: string[] }
interface AnalysisResult { statements: AnalyzedStatement[]; rejects: Diagnostic[]; warnings: Diagnostic[]; diagnostics: Diagnostic[]; isAccepted: boolean }

// POST /api/previews  body { sql: string; options?: { imageCap: number | null } | null } → 200 PreviewResult | 400 | 409 | 422 | 500 | 503
// GET  /api/previews/{id} → 200 PreviewResult | 404
interface Fingerprint { productVersion: string; edition: string; compatibilityLevel: number; isRecursiveTriggersOn: boolean;
  nestedTriggers: number; disallowResultsFromTriggers: number; sessionOptions: number; collation: string }
interface StatementError { errorNumber: number; message: string; procedure: string | null; isToolFault: boolean }
interface StatementResult { index: number; batch: number; position: number; kind: Op; target: string; span: SourceSpan;
  status: 'ok' | 'failed' | 'trigger_blocked' | 'not_run'; rowsAffected: number | null; captureSeqRange: [number, number] | null; error: StatementError | null }
interface ForeignKeyCause { name: string; parent: string; child: string; action: 'CASCADE' | 'SET_NULL' | 'SET_DEFAULT'; event: 'DELETE' | 'UPDATE'; matchedRows: number; unmatchedRows: number }
interface Cause { kind: 'DIRECT' | 'FK_CASCADE' | 'FK_SET_NULL' | 'FK_SET_DEFAULT' | 'TRIGGER' | 'INSTEAD_OF' | 'UNEXPLAINED';
  confidence: Confidence; parentFiringId: string | null; foreignKey: ForeignKeyCause | null; triggerCandidates: string[]; evidence: Evidence[] }
interface Firing { id: string; captureSeq: number; statementIndex: number; depth: number; table: string; operation: Op; rowCount: number;
  imageMode: 'FULL' | 'KEYS'; auditOrder: 'LAST' | 'NONE'; cause: Cause; orderGuarantee: OrderGuarantee }
interface Residual { id: string; table: string; key: RowKey; columns: string[]; detectedBy: 'REREAD' | 'NET_DIFF'; candidates: string[];
  category: 'EXCLUDED_TABLE' | 'UNOBSERVABLE_COLUMN' | 'SAME_TABLE_TRIGGER' | 'UNKNOWN'; confidence: Confidence; rowChangeId: string | null }
interface NetChange { table: string; key: RowKey; change: 'INSERTED' | 'DELETED' | 'UPDATED' | 'MULTISET_REMOVED' | 'MULTISET_ADDED';
  multiplicity: number; before: Values | null; after: Values | null; changedColumns: string[];
  explainedBy: { firingIds: string[]; residualId: string | null } | null }
interface NetSummaryRow { table: string; inserted: number; updated: number; deleted: number; multisetRemoved: number; multisetAdded: number }
type GraphNode =
  | { type: 'statement'; id: string; statementIndex: number; label: string; rowsAffected: number | null; status: StatementResult['status'] }
  | { type: 'firing'; id: string; firingId: string; table: string; operation: Op; rowCount: number; depth: number; captureSeq: number }
  | { type: 'trigger'; id: string; trigger: string; parentFiringId: string | null; statementIndex: number; observed: boolean }
  | { type: 'residual'; id: string; residualId: string; table: string; category: Residual['category'] }
  | { type: 'error'; id: string; errorNumber: number; message: string; isToolFault: boolean }
  | { type: 'group'; id: string; members: string[]; rowCount: number };
interface GraphEdge { id: string; source: string; target: string; kind: 'DIRECT' | 'FK' | 'FIRES' | 'REPLACES' | 'WRITES' | 'RESIDUAL' | 'BLOCKED';
  confidence: Confidence; orderGuarantee: OrderGuarantee; evidence: Evidence[];
  fk: string | null; action: ForeignKeyCause['action'] | null; matchedRows: number | null }
interface PreviewResult {
  previewId: string; status: 'ok' | 'failed' | 'trigger_blocked' | 'truncated'; createdAt: Iso; sqlHash: string;
  baseline: { originDb: string; sandboxDb: string; snapshotDb: string; backupFinishedAt: Iso; fingerprint: Fingerprint };
  statements: StatementResult[]; firings: Firing[]; residuals: Residual[]; residualCommitted: NetChange[];
  residualCheckFailed: boolean; residualCheckMessage: string | null;      // 3주차 추가(인계 7)
  netSummary: NetSummaryRow[]; graph: { nodes: GraphNode[]; edges: GraphEdge[] }; warnings: Diagnostic[];
  capture: { imageCount: number; cap: number; truncated: boolean; manifestVersion: string };
  timingsMs: { analyze: number; execute: number; readLog: number; reread: number; netDiff: number; resolve: number; serialize: number } }

// GET /api/previews/{id}/rows?view=event|net&firingId=&offset=0&limit=100 (limit 1..1000) → 200 | 400 | 404
interface RowChange { id: string; firingId: string; operation: Op; key: RowKey; before: Values | null; after: Values | null;
  changedColumns: string[]; volatile: { column: string; reason: VolatileReason }[]; derived: string[];
  pairing: 'BY_PK' | 'BY_UNIQUE' | 'SINGLE_ROW' | 'UNPAIRED'; finalAfterSource: 'AUDIT' | 'REREAD'; parentRowIds: string[] }
interface RowPage<T> { total: number; view: 'event' | 'net'; offset: number; limit: number; rows: T[] }   // event → RowChange, net → NetChange

// POST /api/previews/{id}/apply  body(선택) { sqlHash: string | null } → 200 ApplyResult | 404 | 409 | 500 | 503
type ApplyPrecondition = 'PREVIEW_NOT_OK' | 'SQL_HASH_MISMATCH' | 'PREVIEW_EXPIRED' | 'FINGERPRINT_MISMATCH' | 'EXECUTE_AS_USER_MISSING';
interface ApplyResult {
  previewId: string; status: 'committed' | 'rolled_back_mismatch' | 'drift_detected' | 'precondition_failed' | 'error';
  finishedAt: Iso; preconditionFailures: ApplyPrecondition[];
  verification: {
    mode: 'SNAPSHOT_NETDIFF' | 'KEYSET';
    driftRows: { table: string; key: RowKey; kind: 'CHANGED' | 'MISSING' | 'MULTISET_COUNT'; columns: string[];
      expected: Values | null; actual: Values | null; expectedMultiplicity: number | null; actualMultiplicity: number | null }[];
    statementRowCounts: { index: number; preview: number | null; actual: number | null }[];
    netSummaryMatch: boolean | null; actualNetSummary: NetSummaryRow[];
    mismatches: { kind: 'STATEMENT_ROW_COUNT' | 'NET_SUMMARY' | 'ROW_VALUE' | 'ROW_MISSING' | 'ROW_UNEXPECTED' | 'IDENTITY_UNPAIRED'
                    | 'UNEXPECTED_TABLE' | 'VOLATILE_FORMAT' | 'KEYSET_TABLE_COUNT';
      statementIndex: number | null; table: string | null; key: RowKey | null; column: string | null;
      expected: string | null; actual: string | null; message: string }[];
    volatileIgnored: string[];              // "스키마.테이블.열"
    identityPairs: { table: string; previewKey: RowKey; actualKey: RowKey }[];
    residualCommitted: NetChange[]; residualCheckFailed: boolean;
    tableCounts: { table: string; before: number; after: number; expectedDelta: number }[];
    limitations: string[] };
  statements: StatementResult[]; message: string | null; sandboxRefreshStarted: boolean;
  timingsMs: { snapshot: number; drift: number; execute: number; netDiff: number; commit: number } }

// POST /api/previews/{id}/cancel → 204 | 404
// GET  /api/previews/{id}/export → 200 application/json 파일 { result: PreviewResult; rowChanges: RowChange[]; netChanges: NetChange[]; sql: string } | 404
```

## 6. §9 경계 사례 담당

- E1·E3·E4 비결정·계산 열: 그리드 "예상값"·"파생"은 web-1, 적용 대조에서 존재·형식만 비교는 apply-1, M1은 eval-2, 사례 CL16은 lab-1
- E2 IDENTITY 짝 매핑: apply-1과 eval-2가 독립 구현, 사례 CL16
- E5 상한: 적용 차단(PREVIEW_NOT_OK)은 apply-1, M5·DM6은 eval-2와 lab-1
- E6 힙 다중집합: 사례 CL14, 그리드 문구는 web-1
- E7 PK가 바뀌는 다중 행 UPDATE: 사례 CL15
- E13 트리거 거부: 사례 CL12, [적용] 비활성은 web-1, 원본 롤백 뒤 남은 변경은 apply-1
- E15 감사 순서 NONE: 사례 CL13
- E16 형식 규칙: 사례 CL19
- E22 비활성 FK·트리거: engine-1 테스트
- E23·E24 0행 숨김·보기 전환: web-1
- E25 @@IDENTITY를 읽는 트리거: 사례 CL12
- E20 타임아웃·연결 끊김: AD9의 503
- E17 temporal·메모리 최적화: AW·CascadeLab에 없어 MVP 사례 없음

## 7. 나중 테스트 라운드로 미루는 것과 실행 명령

```powershell
# 0) 전체 빌드(직렬)
dotnet build DbRipple.slnx -m:1 -nologo
# 1) 프로젝트별 테스트(DB 포함). 한 번에 하나씩 돌려 자원을 아낀다
. .\scripts\env.ps1; dotnet test tests\DbRipple.Apply.Tests
. .\scripts\env.ps1; dotnet test tests\DbRipple.Api.Tests
. .\scripts\env.ps1; dotnet test tests\DbRipple.Analysis.Tests   # Execution·Diff·Attribution·Core·Eval.Tests도 같은 방식
. .\scripts\env.ps1; dotnet test DbRipple.slnx                   # 마지막 전체 회귀
# 2) Host + SP8(AW DM1~DM3 실측, 대본 확정)
. .\scripts\env.ps1; dotnet run --project src\DbRipple.Host       # 그다음 scripts\demo\demo.http의 DM1~DM3 요청
.\scripts\run-sql.ps1 spikes\sql\sp8-dm2-pick-order.sql -Database AW_Origin -Log spikes\logs\sp8-dm2.log   # 읽기 전용 조회만
# 3) 프런트(사용자가 설치)
cd web; npm install; npx vue-tsc --noEmit; npx vite build; npm run dev   # 브라우저: http://localhost:5273, /sp9 = SP9
# 4) 평가(옵션 이름은 eval-2 구현 기준)
. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -- cascadelab --systems preview,B0,B2 --repeat 20 --out logs\week4\eval
. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -- m5 --sizes 1,100,1000,10000,50000,100000 --out logs\week4\m5
dotnet run --project eval\DbRipple.Eval.Cli -- report --run logs\week4\eval\<run-id>
```

- 브라우저 확인: DM1~DM3가 화면에서 끝까지 되는지, DM7(`scripts\demo\dm7-drift.sql` 실행 뒤 [적용])에서 drift_detected가 나오는지 본다. 이것이 3주차 완료 기준이다.
- AW 원본에 실행 사용자를 만드는 복원 스크립트 단계(Q30)를 실행한다(`restore-aw-origin.ps1` 다시 실행 또는 그 단계만).
- openapi-typescript로 만든 타입과 `web/src/api/types.ts`를 대조한다.
- PreviewExecutor가 StatementRunner를 쓰도록 바꾸는 리팩터링은 회귀 테스트를 통과한 뒤에 한다.

## 8. 위험

1. 테스트를 실행하지 않아 회귀가 숨어 있을 수 있다. 특히 StatementRunner와 PreviewExecutor의 규칙이 어긋날 수 있다. 테스트 라운드 1순위는 Execution 회귀와 Runner·Executor 결과 대조다.
2. Core 추가(DbCatalog 매개변수)는 모든 프로젝트에 번진다. 끝에 기본값을 둔 매개변수로만 추가한다.
3. 예외 형식을 바꾸면 `Assert.Throws<InvalidOperationException>`(정확한 형식 비교)를 쓰는 테스트가 깨진다. platform-1과 engine-1이 자기 테스트를 함께 고친다.
4. 새 `cl*.sql`은 모든 CascadeLab 기준선에 실린다. 시드가 커지거나 다른 스키마를 건드리면 모든 테스트가 느려지거나 흔들린다(AD11).
5. [적용]은 원본에 COMMIT한다. DB 테스트는 자기 dbrt_apl_ 기준선에서만 한다. 공유 AW_Origin에는 데모 DM7과 사용자 시연 때만 쓴다.
6. 드리프트 잠금(UPDLOCK, HOLDLOCK)은 최종 차이가 끝날 때까지 유지된다. AW 전체 기준 약 1.6초(SP7 p50)다.
7. 손으로 쓴 TS 타입이 서버와 어긋날 수 있다. §5를 유일한 기준으로 쓰고, 테스트 라운드에서 OpenAPI 생성 타입과 대조한다.
8. KEYSET은 AW에서 자연스럽게 일어나지 않는다(스냅샷이 됨). `ForceKeyset`으로만 확인한다.
9. 컨테이너 메모리 4GB에서 M5 10만 행은 너무 느릴 수 있다. 그러면 자를 목록 3번(5만 행까지)을 쓴다.
10. Host와 Eval.Cli는 통합 전까지 컴파일을 확인할 수 없다. DI 조립 오류는 통합 때 드러난다.

## 9. 열린 질문과 이번 라운드 기본값

Q28부터 이어 번호를 붙였다. 앞 번호는 week2-plan.md §9에 있다.

| 번호 | 질문 | 기본값 |
|---|---|---|
| Q24 | ApplyResult 원소 형식, HTTP | §5 모양. 상태 5종 모두 200, 본문 선택 `{sqlHash}`, 없거나 만료면 404 |
| Q28 | 선행 조건 코드 | PREVIEW_NOT_OK(truncated 포함), SQL_HASH_MISMATCH, PREVIEW_EXPIRED, FINGERPRINT_MISMATCH, EXECUTE_AS_USER_MISSING |
| Q29 | IDENTITY 짝을 결과에 싣기 | 싣는다(`verification.identityPairs`) |
| Q30 | 원본에 `dbr_preview_user`가 없음 | 오케스트레이터 결정(2026-11-11): 원본 준비 단계(`restore-aw-origin.ps1`)에서 멱등으로 만든다. 실제 운영 DB라면 사용자가 미리 만들어 두는 설정 단계에 해당한다. [적용] 중에는 원본에 DDL을 하지 않고, 없으면 EXECUTE_AS_USER_MISSING으로 막는다. AW의 DDL 트리거가 원본 dbo.DatabaseLog에 행을 남기지만 기준선은 원본 백업에서 만들므로 미리보기에는 영향이 없다 |
| Q31 | 잔여 확인 실패 표시 | bool과 메시지, SandboxDirty = true. 예외를 던지지 않고 결과로 돌려준다 |
| Q32 | 409 사유 코드와 예외 형식 | `ConflictReason` 5종을 ProblemDetails `reason`에 싣는다. 503은 연결 끊김 |
| Q33 | NOCOUNT OFF 트리거로 rowsAffected 과대 집계 | StatementRunner가 같은 명령 끝에 `SELECT ROWCOUNT_BIG();`를 붙여 그 값을 쓴다. 테스트 라운드에서 실측 |
| Q34 | 인계 3(트리거의 SAVE TRAN·균형 잡힌 BEGIN…COMMIT) | 거부 유지(D5 "판정할 수 없으면 닫힌 쪽") |
| Q35 | 인계 6(대소문자만 바꾸는 키 UPDATE) | 제품은 DB 정렬 규칙대로 UPDATED. 오라클을 정렬 규칙에 맞추고 정정 기록을 남긴다 |
| Q36 | 커밋 뒤 미리보기 | 저장소에서 지우고 새로 고침을 시작한다. 그 밖의 상태는 남겨 둔다 |
| Q37 | sqlType 출처 | Preview 자체 조회와 `SqlTypes.Format`. 실패하면 형식 이름만 둔다 |
| Q38 | 인계 1 호환(펼치기 전 분석기 결과) | 두 목록이 비고 ProcedureCalls가 있으면 전부 미상으로 본다 |
| Q39 | CL20 대량 데이터 생성 위치 | 시드 안 프로시저 `cl20.usp_Generate @parents`, 기본 시드는 소량 |
| Q40 | 손 정답의 IDENTITY 행 표기 | answer-format v0.2 `{match, identity}`. lab-1과 eval-2가 정한다 |
| Q41 | 프런트 타입 | 이번 라운드는 손으로 쓰고, 테스트 라운드에서 openapi-typescript로 대조한다 |
| Q42 | M5 브라우저 렌더 시간 | SP9 화면의 측정값을 수동으로 기록하고, m5.csv 브라우저 열은 빈 칸 |

설계 문서에 반영할 것(4주차 끝): AD6~AD11, Q24·Q28~Q42 결정, §4 C15 시그니처(`ApplyAsync(previewId, request)`), §6.4 ApplyResult 확장 필드, catalog/schema 형식.
