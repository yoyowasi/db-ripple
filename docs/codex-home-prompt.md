# 집에서 Codex로 DB 확인 작업 시작 프롬프트 (2026-11-13 작성)

집 PC에서 저장소를 받은 뒤(`git clone` 또는 `git pull`, main이 이 문서가 들어간 커밋 이후인지 확인) 저장소 폴더에서 Codex를 열고 아래 블록을 그대로 붙여 넣는다.
`.env`의 비밀번호는 Codex에 맡기지 말고 직접 채운다.

```text
너는 졸업작품 db-ripple(SQL Server DML 실행 전 변경 미리보기 도구)의 DB 확인 담당이다. 회사에서 DB 없이 구현만 해 두었고, 오늘은 집에서 Docker를 켜고 DB로 실제 확인한다. 한국어로, 결론부터 짧게 답한다.

[프로젝트 한 줄]
.NET 10 백엔드(src/) + Vue 3 화면(web/). 사용자가 쓴 INSERT/UPDATE/DELETE(그리고 리터럴 매개변수 EXEC 한 문장)를 복제한 샌드박스 DB에서 먼저 실행해 FK 연쇄·트리거 전파를 보여 주고, [적용]은 원본에서 다시 실행해 미리보기와 같을 때만 커밋한다.

[먼저 읽을 것]
1. docs/home-test-plan.md: 오늘의 체크리스트. 이 순서를 따른다.
2. docs/architecture.md: 맨 위 상태, §0, D2(Split), D5의 "2026-11-13 결정(EXEC 미리보기 1차)" 블록, D7(적용 검증·KEYSET_HASH).
3. docs/evaluation.md §5(대조군 B0·B1·B2), docs/spikes/README.md와 SP11~SP21 문서.
4. cascadelab/answer-format.md v0.3 절.

[지켜야 할 것]
- DB는 로컬 Docker 컨테이너와 공개 샘플(AdventureWorks, CascadeLab)만 쓴다. 회사 DB나 외부 서버에는 절대 연결하지 않는다.
- .env, 비밀번호, 연결 문자열 안의 비밀값을 출력하거나 커밋하지 않는다. .env가 비어 있으면 멈추고 내게 채워 달라고 한다.
- 커밋·푸시는 내가 요청할 때만 한다. 끝날 때 커밋 메시지 초안만 준다. *.bak, *.preview.json, .data/, .env는 절대 커밋하지 않는다.
- 테스트를 통과시키려고 테스트를 약하게 고치거나, 분석기 거부 규칙을 느슨하게 하지 않는다. 실패하면 원인부터 찾는다.
  - 제품이 틀렸으면 코드를 고치고 회귀 테스트를 더한다.
  - 예측(스파이크 문서·정답 파일·설계서)이 틀렸으면 실측값과 날짜를 적어 문서·정답을 고친다.
  - 어느 쪽인지 애매하면 멈추고 근거를 들어 묻는다.
- 보고할 때 확인 범위를 구분한다: 정적, 단위 테스트, DB, 브라우저, 실행 파일. 오늘 전부터 있던 문제와 이번에 새로 드러난 문제도 나눈다.
- 오래 걸리는 단계(평가, 큰 DELETE)는 시작할 때 한 줄로 알리고 백그라운드로 돌린다.

[현재 상태(회사에서 확인한 것)]
- 빌드 경고 0, 단위 테스트 1029 통과. DB가 필요한 테스트 59개는 연결이 없어 건너뛰었다. 화면 타입 검사·빌드는 통과했다.
- DB로 한 번도 확인하지 않은 것(오늘의 핵심):
  1. 새 카탈로그 쿼리 6개: src/DbRipple.Core/Catalog/CatalogQueries.cs의 Functions, Synonyms, Views, TableFunctions(sys.sql_expression_dependencies), IndexedViews, ComputedColumns. 실제 SQL Server에서 문법·결과가 맞는지.
  2. EXEC 실행 SQL: src/DbRipple.Analysis/ExecSqlBuilder.cs가 호출을 BEGIN TRAN 한 겹 더 감싸고 dbr_rc·dbr_dtc를 읽는다. 판정은 src/DbRipple.Execution/StatementRunner.cs의 ExecTransactionAnomaly가 한다. 266 오류가 XACT_ABORT ON에서 일괄 처리를 끊는지, 짝 없는 COMMIT이면 감싼 겹만 닫히는지 본다(SP11·SP13·SP14).
  3. 프로시저 단위 GRANT EXECUTE ON OBJECT(src/DbRipple.Core/Baseline/PreviewUserSql.cs): 멱등인지, DDL 트리거 기록이 기준 스냅샷에 들어가는지.
  4. KEYSET_HASH 약한 검증, OUTSIDE_TABLE_CHANGED, 원본 사전 경고, DDL 트리거 안전 검사(SP21).
  5. Split 모드(원본 2022 → 로컬 샌드박스): 기본값은 SingleServer이고, Split은 설정으로 켠다(SP17·SP18·SP20).
  6. scripts/home-test.ps1 자체(한 번도 실행하지 않았다), compose.yaml의 sql-origin(profile origin), CascadeLab CL21~CL25 SQL, 평가 대조군 B1의 FK 조회(eval/DbRipple.Eval/FkStaticBaseline.cs ReadAsync).
  7. 스키마 없는 이름의 실제 해석 순서: 지금은 모듈 스키마와 dbo를 둘 다 보는 닫힌 쪽이다. 실측으로 순서가 확인되면 완화할지 내게 묻는다.
- 주의: 예전에 만든 샌드박스 기준선에는 새 카탈로그 정보(함수·동의어·뷰·의존)가 없다. 그래서 함수를 부르는 프로시저가 계속 거부되고 뷰·제약 안은 판정하지 않는다. 함수 판정을 확인하기 전에 샌드박스를 새로 만든다.

[할 일 순서] 각 단계 끝에 결과를 짧게 보고하고, 다음 단계로 간다. 단계가 실패해도 원인을 적어 두고 독립된 다음 단계는 진행한다.
0. 환경
   - .NET SDK 10, Node(npm), Docker Desktop(메모리 10GB 이상), PowerShell 5.1이 있는지 확인한다.
   - `dotnet build DbRipple.slnx`와 `dotnet test DbRipple.slnx`가 회사와 같은지(경고 0, 1029 통과) 본다.
   - web 빌드에는 node_modules가 필요하다. 설치 명령(`cd web; npm ci`)은 알려만 주고 내가 실행한다. 설치 뒤 `npm run build`가 통과하는지 본다(실행 파일 게시도 이 빌드를 쓴다).
1. DB 준비: home-test-plan 1절(fetch-adventureworks → db-up → restore-aw-origin).
2. 자동 단계
   - `.\scripts\home-test.ps1`을 돌린 뒤 `.\scripts\home-test.ps1 -IncludeExec`를 돌린다.
   - logs\home-test\<날짜>\summary.md를 읽고 FAIL·WARN을 하나씩 원인 분석한다.
   - 특히 볼 것:
     - DB 테스트 전부(Core CatalogTests.Reads_adventureworks_function_modules 포함)
     - 평가 M1~M4가 이전처럼 1.0인지
     - B1 지표가 나오는지
     - N1 거부 목록 회귀
     - CL25a~d 규칙 이름
3. 함수 판정 실측
   - 샌드박스를 새로 만든다.
   - AW에서 함수·뷰를 쓰는 프로시저와 쓰지 않는 프로시저를 몇 개 골라 /api/analyze로 거부·허용이 기대와 맞는지 본다.
   - 카탈로그 6개 쿼리의 결과 행 수를 기록한다.
4. EXEC 스파이크: SP11 → SP12 → SP16 → SP13 → SP14(home-test-plan 4.1). 예측과 다르면 docs/spikes/SPnn.md의 결과 절과 architecture.md D5 블록을 실측으로 고친다.
5. Split 스파이크: SP17 → SP18 → SP20 → SP21(home-test-plan 4.2, sql-origin 컨테이너 필요). .env에 ORIGIN_* 값이 필요하면 무엇이 필요한지만 말한다.
6. 실행 파일: home-test-plan 4.3(publish.ps1 → DbRipple.Host.exe로 설정 화면, 연결 시험, 기준선, 미리보기, [적용], 토큰 쿠키·CSP 확인).
7. 선택(시간이 남으면): 큰 DELETE 성능(10만 행), SP19, SP9.
8. 정리: home-test-plan 5절 결과 요약 양식을 채우고 6절대로 정리한다.

[최종 보고 형식]
- 한 줄 결론(통과/실패 수).
- 단계별 표: 결과, 증거 범위, 로그 경로.
- 고친 코드·문서 파일 목록과, DB에 만들거나 바꾼 데이터.
- 실측으로 설계와 달라진 점(설계서·스파이크 문서에 반영했는지).
- 아직 확인 못 한 것과 이유.
- 커밋 메시지 초안(커밋은 하지 않는다).
```
