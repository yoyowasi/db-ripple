# PostgreSQL 미리보기 최소판

사용자가 2026-11-18 PostgreSQL 미리보기 최소판을 요청했다. 이 문서에는 같은 날 실행한 [SP22 실측](../../spikes/SP22.md)을 반영했다.

## 범위

- **한다:** PostgreSQL 18 한 서버(원본과 샌드박스가 같은 서버)에서 INSERT·UPDATE·DELETE 미리보기. 원인 트리(직접·FK 연쇄·트리거), 행 비교, 문장별 결과.
- **안 한다:** [원본에 적용], 프로시저·함수 호출(`CALL`, `DO`), Split(원격 원본), 설정 화면에서 PostgreSQL 연결 저장(환경 변수로만), 롤백 뒤 남은 변경 검사.
- 화면은 지금 것을 그대로 쓴다. 엔진이 PostgreSQL이면 [원본에 적용]을 끄고 이유를 보이며, 편집기 방언을 PostgreSQL로 바꾼다.

## 경계(코드 조사 2026-11-18)

미리보기 흐름(`PreviewPipeline`), 변경 묶기(`ChangeCollector`), 원인 귀속(`CausalResolver`), 저장소·API·화면은 DB와 무관하다. PostgreSQL 구현은 새 프로젝트 `src/DbRipple.Postgres`(Npgsql)에 두고 아래 자리에 꽂는다.

| 자리 | SQL Server | PostgreSQL 최소판 |
|---|---|---|
| `ISandboxManager` | BACKUP/RESTORE, DB 스냅샷 | `CREATE DATABASE 샌드박스 TEMPLATE 원본`, 감사 설치, 카탈로그·지문·매니페스트 |
| `IBatchAnalyzer` | ScriptDom | 문장 나누기(따옴표·`$태그$`·주석 인식) + 첫 낱말로 허용/거부 + 대상 테이블 |
| `ITriggerAnalyzer` 결과 | ScriptDom 정적 분석 | 트리거 함수 본문(`prosrc`)에서 INSERT INTO·UPDATE·DELETE FROM 대상을 찾는 보수적 분석, `EXECUTE`가 있으면 동적 SQL |
| `IPreviewExecutor` | T-SQL 실행 봉투 | Npgsql 트랜잭션 봉투, 감사 읽기, 감사 이미지로 최종 차이, ROLLBACK |
| `IColumnTypeSource` | sys.columns | `format_type` |
| `IApplyService` | ApplyService | 지원 안 함(501) |
| 조립 | `CreateEngine` | 엔진 설정이 `Postgres`면 위 구현으로 조립 |

`IBatchAnalyzer`는 지금 DI 싱글턴이라 `/api/analyze`가 늘 SQL Server 분석기를 쓴다. 엔진 묶음(`Engine`)에 분석기를 넣고 DI는 엔진에서 꺼내게 바꾼다.

## 결정

1. **샌드박스 복제는 TEMPLATE.** 원본에 다른 연결이 있으면 PostgreSQL이 복제를 거절한다(SQLSTATE 55006). 도구는 원본 세션을 유지한다. 새로 고침 요청은 202를 반환하고, 복제가 실패하면 상태 API는 `error`를 반환한다. SQLSTATE 55006은 psql에서 확인했다. 기준 시각은 복제가 끝난 시각이다. 기준 비교용 스냅샷 DB는 만들지 않는다(PostgreSQL은 DB 사이 조회가 안 된다). 새로 고침은 `DROP DATABASE 샌드박스 WITH (FORCE)`(PostgreSQL 13 이상) 뒤 다시 복제한다. 지우는 것은 **도구가 만든 DB뿐**이다: 복제 직후 DB 주석(`COMMENT ON DATABASE`)으로 소유 표시를 남기고, 같은 이름의 DB가 이미 있는데 그 주석이 없으면 지우지 않고 오류로 끝낸다. 시스템 DB 이름(`postgres`, `template0/1`)과 원본과 같은 이름은 받지 않는다. 매니페스트·기준 시각·원본 이름은 샌드박스의 `dbr_audit.meta`에 둔다(Host를 다시 켜면 거기서 읽는다).
2. **감사 수집은 문장 단위 AFTER 트리거 + 전이 테이블.** 테이블마다 INSERT·UPDATE·DELETE 트리거 3개(`zzz_dbr_audit_i|u|d`, `REFERENCING ... TABLE`). PostgreSQL은 같은 종류 트리거를 이름순으로 부르므로 `zzz_` 접두로 마지막에 돈다. 이름이 더 뒤인 사용자 트리거가 있으면 매니페스트에 NONE으로 적는다. 감사 저장소는 샌드박스의 `dbr_audit` 스키마(순번 시퀀스, 헤더, 행 이미지, meta). 감사 함수는 `SECURITY DEFINER`(미리보기 역할은 감사 스키마에 권한이 없다). 깊이는 `pg_trigger_depth()`. 키는 수집기와 같은 규칙(`TableKey.Choose`: PK → NOT NULL UNIQUE 인덱스 ID 순 → ROW). 파티션 테이블·파티션은 최소판에서 제외(매니페스트 excluded). 직접 문장과 FK 연쇄 모두 0행 묶음의 헤더를 남긴다. SP22에서 빈 FK 묶음의 깊이는 그 연쇄를 시작한 문장과 같았다. 중첩된 0행 문장도 문장 단위 사용자 트리거의 쓰기 문맥이 될 수 있으므로 깊이와 관계없이 보존한다.
3. **실행 봉투.** 전용 연결(풀링 끔) `BEGIN` → 문장마다 `SET LOCAL ROLE dbr_preview` → 실행·행 수 → `RESET ROLE` → 경계(그 트랜잭션 안 헤더 순번 최댓값) → 감사 읽기 → 최종 차이 → `ROLLBACK`. 오류가 나면 PostgreSQL 트랜잭션은 중단되므로 그 문장 뒤는 not_run, 수집·최종 차이는 비운다. PostgreSQL 트리거 함수는 COMMIT할 수 없어 롤백 뒤 남은 변경 검사는 하지 않는다(시퀀스 값 소비는 남는다). 오류 문맥에 사용자 PL/pgSQL 함수가 있으면 `trigger_blocked`, `dbr_audit.*` 함수면 도구 오류(`failed`), 그 밖은 `failed`. SQLSTATE는 메시지 앞에 붙이고 오류 번호는 0. **주의:** 연결 사용자가 superuser면 `SET ROLE`은 보안 경계가 아니다(`set_config('role', …)`로 되돌릴 수 있음). 그래서 문장 안 `set_config` 등을 거부하지만, 사용자 함수 안까지는 막지 못한다. 연결 사용자는 superuser가 아닌 계정(CREATEDB·CREATEROLE)을 권장한다.
4. **최종 차이는 감사 이미지로(순수 계산 `ImageNetDiff`).** 묶음마다 DELETED 이미지를 −1, INSERTED 이미지를 +1로 (키, 행 문자열)마다 더한다. 전이 테이블은 그 문장이 지우고 넣은 행 그대로라 **더하는 순서와 상관없이 "최종 − 처음"이 남는다**(트리거 안에서 같은 행을 다시 고쳐 기록 순서가 실제 순서와 달라도 맞음). 키마다 남은 −1·+1이 하나씩이면 UPDATED, 하나만이면 DELETED/INSERTED, 키 없는 테이블은 다중집합. 이미지 상한을 넘어 KEYS로 바뀐 테이블은 이 방법을 못 쓰므로, before는 트랜잭션 밖 새 연결(확정된 기준선), after는 트랜잭션 안에서 감사에 나온 키만 다시 읽는다(키 없는 테이블은 전체 다중집합 비교, 느린 길). 감사 밖 테이블·롤백 뒤 남은 변경은 보지 못한다. 이 한계는 분석 경고(`WARN_PG_MINIMAL_SCOPE`)로 매 미리보기에 남긴다.
5. **FK 연쇄의 깊이와 순서.** PostgreSQL 18.7에서 RI 트리거의 자식 DML은 부모 행마다 실행됐고, SP22의 전이 테이블은 같은 연쇄의 변경을 자식 테이블별 한 묶음으로 모았다. FK 감사 헤더는 연쇄를 시작한 문장과 같은 깊이에서 그 문장 뒤에 기록됐다. 사용자 트리거가 명시적으로 실행한 문장의 감사 헤더는 한 단계 더 깊고 SP22의 트리거 사슬에서는 후위였다. 실행기는 관측 깊이와 순번을 그대로 전달한다. 기존 원인 판정(FkJudge)은 같은 문장 안의 FK 키와 깊이로 연쇄를 귀속하며, 수집기의 후위 괄호는 사용자 트리거 문맥에 쓴다. 이 순서는 [SP22](../../spikes/SP22.md)의 관측 범위에 해당한다.
6. **값 문자열.** 감사는 `to_jsonb` 뒤 `->>`로 열마다 문자열(NULL은 JSON null)을 담는다. 계약 규칙(모든 값은 문자열, NULL은 null)과 같고, 같은 함수라 같은 값은 같은 문자열이다. 숫자는 jsonb 원문, 불리언 `true`/`false`, 시각은 ISO(`T` 구분).
7. **문장 분석(`PgSqlLexer` + `PgBatchAnalyzer`).** 문장 나누기는 `'…'`(E'…' 포함), `"…"`, `$태그$…$태그$`, `--`, 중첩 `/* */`를 인식하고 닫히지 않으면 `REJECT_PARSE_ERROR`. 허용: `INSERT INTO`, `UPDATE [ONLY]`, `DELETE FROM [ONLY]`. 스키마를 생략한 대상은 `public`에서만 찾는다(샌드박스는 원본 DB의 search_path 설정을 물려받지 않음). 거부 규칙 ID는 기존 것을 다시 쓴다: `REJECT_TRUNCATE`, `REJECT_MERGE`, `REJECT_EXEC`(CALL·DO·EXECUTE), `REJECT_TRANSACTION_CONTROL`, `REJECT_SET_OPTION`(SET·RESET), `REJECT_DDL`, `REJECT_BULK_INSERT`(COPY), `REJECT_SELECT_INTO`, `REJECT_ADMIN_COMMAND`(VACUUM 등 + `set_config`·백엔드 종료·`pg_sleep`·advisory lock·`query_to_xml` 류처럼 SQL 문자열을 실행하는 함수), `REJECT_EXTERNAL_ROWSET`(`dblink*`·파일 읽기·`lo_import/export`), `REJECT_NOT_ALLOWED`(SELECT·WITH 등), `REJECT_TARGET_NOT_USER_TABLE`(없음·시스템·파티션), `REJECT_CAPTURE_STORE`(dbr_audit). 도달하는 트리거(직접 대상 + FK 연쇄 + 트리거 쓰기 대상)에 외부 부작용(`pg_notify`·dblink·COPY·신뢰하지 않는 언어 등)이 있으면 `REJECT_TRIGGER_EXTERNAL_EFFECT`, 동적·모름은 `WARN_TRIGGER_DYNAMIC_SQL`, 감사 순서 NONE은 `WARN_AUDIT_NOT_LAST`. 새 규칙은 경고 하나 `WARN_PG_MINIMAL_SCOPE`(스크립트마다 한 번).
8. **트리거 분석은 보수적으로(`PgTriggerAnalyzer`).** 주석·문자열을 지운 함수 본문에서 INSERT INTO·UPDATE … SET·DELETE FROM 대상을 찾는다. 동적 SQL(`EXECUTE`), plpgsql·sql 밖의 언어, 이름을 풀 수 없는 대상이 있으면 `WriteTargetsUnknown`. 신뢰하지 않는 언어(plpython3u 등)는 외부 부작용으로 둔다. 트리거 원인은 지금처럼 `inferred`.
9. **지문.** 한 서버라 원본·샌드박스 지문은 늘 같다(`FingerprintMatch = true`). 값: 서버 버전, "PostgreSQL", 주버전, DB 정렬(나머지 SQL Server 항목은 고정값).
10. **객체 ID.** PostgreSQL oid(uint32)를 int로 넣는다. 2^31을 넘으면 기준선 구축을 실패로 끝낸다(사용자 객체 oid는 보통 16384부터라 실제로는 거의 없음).
11. **엔진 선택.** `DbRipple:Engine` = `SqlServer`(기본) | `Postgres`, 환경 변수 `DBRIPPLE_ENGINE`이 우선. PostgreSQL 서버 연결은 `DBRIPPLE_PG_CONNECTION`(관리용 DB `postgres`에 붙는 Npgsql 연결 문자열). 원본·샌드박스 DB 이름은 SQL Server 기본값(AW_Origin)과 섞이지 않게 `DbRipple:Pg:OriginDb`(필수, 환경 변수 `DbRipple__Pg__OriginDb`)·`DbRipple:Pg:SandboxDb`(기본 `원본_dbr_sandbox`), 소문자·숫자·밑줄만. 선택: `Pg:PreviewRole`(기본 `dbr_preview`), `Pg:CommandTimeoutSeconds`(기본 60). 설정 화면에서 SQL Server 연결을 저장하면 그 묶음으로 바뀐다.
12. **API·화면.** `SandboxStatus.engine`(PostgreSQL이면 `"postgres"`, SQL Server는 필드 없음 — 기존 JSON 모양 유지)을 더한다. 문장 분석기는 엔진 묶음(`Engine.Analyzer`)에서 꺼낸다. [적용] API는 501. 화면은 엔진이 postgres면 편집기 문법을 PostgreSQL(기본 스키마 public)로, [원본에 적용]은 "PostgreSQL 최소판은 [적용]을 지원하지 않습니다" 한 줄로 끈다. 2026-11-18 추가: `SandboxStatus.lastError`(error 상태일 때만, 비밀번호 가림)로 새로 고침 실패 이유(예 원본 연결 때문의 55006)를 보내고, 상단 샌드박스 상태 칩을 열면 "실패 이유"로 보인다(SQL Server도 같음).

## 구현 파일

- `src/DbRipple.Postgres/`: `PgOptions`(설정·식별자), `PgCatalogReader`, `PgCapture`(감사 설치 SQL·매니페스트), `PgTriggerAnalyzer`, `PgSqlLexer`, `PgBatchAnalyzer`, `ImageNetDiff`, `PgPreviewExecutor`, `PgSandboxManager`, `PgStubs`(형식 재료·[적용] 501).
- Host: `Engine`에 `Analyzer`, `CreatePgEngine`, `IBatchAnalyzer`는 현재 묶음에서. Contracts: `SandboxStatus.Engine`, `AnalysisRules.WarnPgMinimalScope`(경고 목록 테스트 14 → 15).
- web: `types.ts` engine, `SqlEditor` 문법, `applyGate`·`gateItems`.
- 테스트: `tests/DbRipple.Postgres.Tests/PgPureTests.cs`(DB 없음).

## 검증

1. [SP22](../../spikes/SP22.md)에서 H1~H7, 시나리오 A~E, 원본 연결이 있는 TEMPLATE 복제를 실측했다.
2. `dotnet build`, 기존 단위 테스트(회귀 없음), 새 단위 테스트(문장 나누기·분류, 감사 이미지 최종 차이 — DB 없음).
3. [클라우드 검증 보고서](../../test-reports/2026-11-18-pg-minimal-cloud.md)에 빌드·단위 테스트·PostgreSQL Host API 결과와 미검증 범위를 기록한다.
4. 화면은 정적 빌드로 확인했다. 브라우저에서의 동작 확인은 남아 있다.
5. 2026-11-19 통합 테스트 `tests/DbRipple.Postgres.Tests/PgIntegrationTests.cs`(`PgFact`, `DBRIPPLE_PG_TEST_CONNECTION`이 없으면 건너뜀)와 CI `pg` 잡(PostgreSQL 16·17·18 공식 이미지, superuser 비밀번호는 실행마다 무작위, 테스트 계정은 `LOGIN NOSUPERUSER CREATEDB CREATEROLE`)을 더했다. 실제 DB로 새로 고침(ready·감사 대상 수·Host 재시작·두 번째 새로 고침), 문장이 미리보기 역할로 실행됨(`SET LOCAL ROLE`: 열 기본값 `current_user`가 미리보기 역할, `session_user`가 연결 계정), 원본 연결이 있을 때 `55006`과 원본 세션 유지, FK 연쇄(원인·깊이·부모 먼저·0행 헤더·롤백 뒤 행과 감사 0행), 같은 트리거의 실제 재귀와 SP22 D 사슬, 이미지 상한 뒤 KEYS 재조회(키 있는 테이블·키 없는 테이블), 자식 2만 행 연쇄(시간은 테스트 출력)를 확인한다. pg 잡은 건너뜀(전체 − 통과 − 실패)이 0이고 `PgIntegrationTests`가 이름으로 6건 이상 통과해야 하며, 테스트 뒤 `dbr_it_` 접두 DB·역할이 남지 않았는지 superuser로 확인한다. [클라우드 검증 보고서](../../test-reports/2026-11-18-pg-minimal-cloud.md) "문제와 남은 확인" 마지막 줄 가운데 비슈퍼유저 관리 계정, 동일 사용자 트리거의 실제 재귀 발동, 이미지 상한 이후 KEYS 재조회, 18.7 외 버전(16·17)은 CI pg 잡이 이 항목을 확인한다. 브라우저 확인과 대용량(수십만 행 이상)은 범위 밖으로 남는다.
6. 이 테스트를 쓰며 결정 9의 고정값 하나를 고쳤다: PostgreSQL은 트리거가 자기 테이블을 고치면 같은 트리거가 다시 돈다(SQL Server `RECURSIVE_TRIGGERS OFF` 같은 설정이 없음). 지문의 재귀 트리거 값을 꺼짐으로 두면 원인 판정이 그 트리거가 다시 발동해 쓴 묶음(시험에서는 깊이 3)을 `UNEXPLAINED`로 남겨 켜짐으로 바꿨다(`PgSandboxManager.FingerprintOf`, 순수 테스트 `PgRecursionPureTests`). 그래프는 열 목록(`UPDATE OF`)·`WHEN` 조건을 모르므로 같은 테이블 트리거를 "발동했을 것"으로 하나 더 그릴 수 있다.
