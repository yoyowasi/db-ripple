# 클라우드 인계: 테스트·PR 검증과 CL22 귀속 확장

사용자는 로컬 PC를 끄기 위해 2026-11-15 클라우드에서 작업을 계속하도록 요청했다. 이 커밋은 **WIP checkpoint**이며 완성·전체 통과를 의미하지 않는다. 브랜치 `codex/validate-home-tests`, 원격 `https://github.com/yoyowasi/db-ripple.git`, 기준 main `b1a6fabbbd9caa61e62c852b941b22054364b607`.

## 사용자 승인과 경계

- 테스트 후 PR 생성·검증, 로컬 Docker 공개 샘플 DB 신규 구축, npm ci 실행 승인.
- CL22의 귀속 불일치를 단순히 기록하는 대신 **분석 계획·수집 정보를 확장하여 해결**하도록 승인.
- 클라우드에서 이어서 수정·검증하고 같은 브랜치/PR을 갱신한다. merge·배포는 요청하지 않았다.
- 회사 DB·외부 운영 DB 금지. DB는 실행 환경 안의 공개 AdventureWorks/CascadeLab만 사용한다. 로컬 PC의 SQL/비밀번호/경로는 인계되지 않는다.
- `.env`, 비밀번호, `.data/`, 백업, `*.preview.json`, `logs/home-test/` 원본 로그는 커밋하지 않는다.
- 테스트 약화·거부 규칙 완화·무근거 certainty로 통과시키지 않는다.

## 이미 검증한 내용

[로컬 검증 기록](2026-11-14-local-validation.md)을 읽는다. 기본 30사례 × 3회 평가는 이전 공통 103지표가 동일했고 B1 33지표를 새로 얻었다. N1은 EXEC 포함 AW 4/4, CascadeLab 36/36, RT 1/1, Reach 1/1 일치. PowerShell 5.1/7 스크립트 회귀는 각각 23/23. npm ci와 web 빌드 통과. 이 결과는 **CL22 신규 확장 전** 증거이며 확장 전체의 최종 통과를 대신하지 않는다.

## CL22 변경과 남은 실패

설계와 계획: `docs/superpowers/specs/2026-11-14-exec-entry-delete.md`, `docs/superpowers/plans/2026-11-14-exec-entry-delete.md`.

- 새 `EntryDeletePlanner`: 엄격한 직선형 본문, 스키마 명시 DELETE, 첫 대상의 사용자 트리거·하위 FK action 없음만 계획에 기록.
- manifest의 `RecordsZeroRowDirectDelete`, C11의 특정 첫 FiringId 증거, C13의 `procedureEntryDelete` 증거를 연결하는 코드가 작성되었다.
- 0행 raw 헤더를 반드시 소비하며 뒤 같은 테이블의 cascade에 근거를 재사용하면 안 된다. 구형 plan/manifest·불완전 수집·실행 실패는 fallback.
- root 신규 실제 DB 회귀 `ExecEntryDeleteIntegrationTests`는 변경 전 all/partial 2실패(기대 ProcDirect Certain, 실제 FkCascade Inferred), none/branch 2통과를 재현했다. **변경 후 실제 DB 4사례는 아직 미실행**.
- 구현 담당은 사용량 제한으로 중단되어 완성 보고나 최종 독립 리뷰를 받지 못했다. 부분 구현을 검토하고 마무리해야 한다.

인계 직전 현재 소스의 `dotnet build DbRipple.slnx --no-restore -m:1 -nologo`는 **경고 0·오류 0**으로 통과했다.

DB 환경변수를 제거한 `dotnet test DbRipple.slnx --no-build -m:1 --logger trx` 결과는 **1051 통과·11 실패·63 건너뜀**이다. 11개는 모두 `DbRipple.Execution.Tests.FixtureTests.Collect_of_raw_fixture_equals_collect_fixture` 직렬화 비교 실패다. 새 `CollectResult.ProcedureEntryDeletes` 빈 목록이 기존 fixture JSON에 없는 `procedureEntryDeletes`로 직렬화된다. 이 목록은 내부 증거이므로 JSON 경계에서 제외하는 등 기존 fixture 호환성을 유지하는 방향으로 해결한다. 정답 fixture를 일괄 재생성하여 숨기지 않는다.

로컬 증거는 `logs/home-test/2026-11-14/entry-delete/handoff-build.log`, `handoff-unit.log`, `handoff-trx/`에만 있으며 클라우드에는 없다. 위 수치는 로컬 TRX의 실제 UnitTestResult outcome을 root가 집계했다.

## 이어서 할 일

1. 이 브랜치를 checkout하고 프로젝트 지침을 읽는다. 가능한 경우 독립 리뷰어를 사용하되 중첩 팀은 만들지 않는다.
2. 직렬화 11실패를 재현·해결하고 신규 분석/수집/귀속 단위 테스트, 전체 빌드·테스트, web 빌드를 실행한다.
3. 첫 헤더의 신뢰성·manifest capability·raw 버전/경계/이미지 완전성·raw trigger 정확 식별·0행/부분/조건부 fallback을 독립 리뷰한다. 타입·실행 순서만 보고 certainty를 부여하지 않는다.
4. 클라우드에 Docker가 있으면 새 로컬 SQL Server와 공개 DB를 준비해 Integration 4사례, 전체 DB 테스트(선택 SP7은 `DBRIPPLE_RUN_SP7=1`), 기본/EXEC evaluator와 N1을 재실행한다. `.env`는 무작위 테스트 비밀번호로 새로 만들고 출력하지 않는다.
5. Docker/SQL을 실행할 수 없으면 그 제한을 정확히 기록하고 가능한 단위·정적·빌드·리뷰는 완료한다. 로컬 PC SQL에 연결하려고 하지 않는다.
6. 검증 보고서와 plan 체크박스를 실제 결과에 맞게 갱신하고 같은 PR에 커밋·푸시한다. PR이 없으면 이 브랜치로 Draft PR을 만든다. 미해결 실패가 있으면 draft를 유지하며 통과했다고 하지 않는다. merge하지 않는다.

Linux 클라우드에서는 경로 구분자·PowerShell 설치·.NET 10 SDK 지원을 확인한다. `web`은 `npm ci` 후 `npm run build`; 개발 의존성 minimatch 관련 moderate advisory는 기존 보고서에 기록되어 있으며 자동 audit fix는 하지 않았다.

## 클라우드 후속 검증 (2026-11-15)

이 문서 위쪽의 11실패/WIP 수치는 **인계 당시 이력**이다. 클라우드에서 최소 수정·독립 리뷰를 진행했고, 현재 실제 결과는 [클라우드 코드 검증](2026-11-15-cloud-validation.md)과 [클라우드 DB 검증](2026-11-15-cloud-db-validation.md)을 따른다. 내부 증거 JSON 노출·모순 이미지·헤더 없는 이미지의 gate 및 Linux 테스트 환경 문제를 수정했다. 신규 실제DB4사례와 전체DB(SP7)1191건이 통과했다. 비밀값과 원본로그는 커밋하지 않았다.
