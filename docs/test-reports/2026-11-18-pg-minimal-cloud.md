# 2026-11-18 PostgreSQL 최소판 클라우드 검증

`feat/postgres-preview-minimal`의 시작 커밋 `3f00826`, 기준 `main`의 `95c700f`, 수정 후 작업 트리를 비교했다. 환경은 Debian 13 x64, .NET SDK 10.0.404, Node 24.22.1, Docker 29.9.2, PostgreSQL 18.7이다. .NET·npm 경로와 캐시는 [기존 환경 준비 절차](2026-11-17-cloud-onboarding.md)대로 설정했다.

## 실행 명령

```bash
source /workspace/.setup/env.sh
TEST_OUT=$(mktemp -d)
dotnet restore DbRipple.slnx
dotnet build DbRipple.slnx --no-restore -warnaserror -m:4
dotnet test DbRipple.slnx --no-build -m:4 --logger trx --results-directory "$TEST_OUT"
dotnet test tests/DbRipple.Postgres.Tests --no-build
dotnet test tests/DbRipple.Analysis.Tests --no-build --filter FullyQualifiedName~BatchAnalyzerTests
(cd web && npm ci && npm run build)
```

별도 디렉터리에 `git archive origin/main`으로 기준 커밋을 풀어 `dotnet test DbRipple.slnx -m:4 --logger trx`와 웹 빌드를 실행했다. 테스트 두 트리 모두 SQL Server 연결 환경 변수 없이 실행했다. TRX에서 테스트 이름별 결과를 비교했다.

격리된 컨테이너에는 비밀번호를 프로세스 환경으로 전달했다. SP22 종료 후 API용 `sp22`에는 업무 테이블 7개와 업무 트리거 2개를 남기고, 실측용 감사 테이블과 트리거 함수를 제거했다. Host가 새 샌드박스에 제품의 감사 트리거를 설치하게 했다.

```bash
docker pull postgres:18
docker run --detach --name dbripple-pg-sp22 --publish 127.0.0.1:55432:5432 \
  --env POSTGRES_PASSWORD --env POSTGRES_DB=sp22 postgres:18
docker exec -i dbripple-pg-sp22 psql -X -U postgres -d sp22 -v ON_ERROR_STOP=1 -f - \
  < spikes/sql/sp22_10_pg_capture.sql
docker exec -i dbripple-pg-sp22 psql -X -U postgres -d sp22 -v ON_ERROR_STOP=1 <<'SQL'
DROP FUNCTION s.dbr_audit_ins(), s.dbr_audit_upd(), s.dbr_audit_del() CASCADE;
DROP TABLE s.dbr_audit;
SQL
```

`DBRIPPLE_PG_CONNECTION`을 프로세스에 주입한 상태에서 Debug 빌드의 Host를 실행했다. 중첩 보강 시험은 [SP22의 추가 트리거](../spikes/SP22.md#사용자-트리거-안의-fk와-0행-묶음)를 설치한 별도 원본 `sp22_nested`와 포트 15081을 사용했다.

```bash
cd src/DbRipple.Host
DBRIPPLE_ENGINE=postgres DbRipple__Pg__OriginDb=sp22 DbRipple__Urls=http://127.0.0.1:15080 \
  dotnet bin/Debug/net10.0/DbRipple.Host.dll --dev --no-browser
```

새로 고침 요청을 보내고 상태를 폴링했다. 각 미리보기 요청의 `sql` 값은 아래 결과 표에 있다. 주문 UPDATE가 활성 미리보기일 때 해당 응답의 `previewId`를 `PREVIEW_ID`로 사용해 [적용]을 호출했다.

```bash
BASE=http://127.0.0.1:15080
curl -sS -i -X POST -H 'X-DbRipple-Request: 1' -H "Origin: $BASE" "$BASE/api/sandbox/refresh"
curl -sS "$BASE/api/sandbox"
curl -sS -i -H 'X-DbRipple-Request: 1' -H "Origin: $BASE" -H 'Content-Type: application/json' \
  --data '{"sql":"DELETE FROM s.parent WHERE id = 1"}' "$BASE/api/previews"
curl -sS -i -H 'X-DbRipple-Request: 1' -H "Origin: $BASE" -H 'Content-Type: application/json' \
  --data '{}' "$BASE/api/previews/$PREVIEW_ID/apply"
```

## 검증 결과

| 증거 범위 | 검사 | 결과 |
|---|---|---|
| 정적 | .NET 복원·빌드 | 초기·최종 빌드 경고 0, 오류 0 |
| 정적 | `npm ci && npm run build` | 성공. Vite 500 kB 초과 경고는 `main`에서도 발생 |
| 정적 | ScriptDom T-SQL 검사 | 시작 커밋에서 SP22 psql 구문으로 1건 실패. SP22 파일만 PostgreSQL 실행으로 검증하고 최종 ScriptDom 29건 통과 |
| 단위 | 전체 .NET 솔루션 | `main` 1,254 통과·66 건너뜀·실패 0. 시작 브랜치 1,300 통과·66 건너뜀·1 실패. 수정 후 1,299 통과·66 건너뜀·실패 0. `main`과 공통인 1,320건의 결과가 모두 일치 |
| 단위 | `DbRipple.Postgres.Tests` | 최종 45건 통과. 기존 깊이 보정의 가정을 재현하던 테스트 3개를 실측 헤더로 원인 귀속을 검사하는 1개로 교체. 새 테스트는 기존 보정을 거치면 중첩 DELETE의 `TRIGGER` 귀속을 찾지 못해 실패 |
| 단위 | `BatchAnalyzerTests` | 103건 통과. 경고 규칙 ID 15개, 중복 없음 |
| DB | SP22 H1~H7, A~E | FK는 같은 깊이에서 부모 헤더 뒤에 기록되고 사용자 트리거 사슬은 깊이 3·3·2·1 순으로 기록. RI 문장은 부모 행별로 실행되고 감사 묶음은 테이블별로 합쳐짐. [SP22 상세 결과](../spikes/SP22.md) |
| DB·API | TEMPLATE, 새로 고침 | 활성 원본 psql 세션이 있으면 DB에서 `55006`. 같은 조건의 Host 요청은 202 뒤 `error`. 두 확인에서 원본 세션이 같은 PID로 계속 응답. 자체 psql을 정상 종료한 뒤 새로 고침은 202 뒤 `ready`, `engine=postgres`, 감사 대상 7개 |

## Host API 미리보기

표의 테이블 이름은 모두 `s` 스키마다. 성공한 미리보기는 HTTP 200, `status=ok`, `WARN_PG_MINIMAL_SCOPE`를 반환했다.

| 요청 SQL 또는 경로 | 확인한 원인·응답 | `netSummary` |
|---|---|---|
| (a) `DELETE FROM s.parent WHERE id = 1` | parent `DIRECT`, child·grandchild `FK_CASCADE`, note `FK_SET_NULL`. 모두 깊이 1, `certain`. 손자의 부모 링크는 child | parent 삭제 1, child 삭제 2, grandchild 삭제 3, note 수정 2 |
| (b) `DELETE FROM s.parent WHERE id = 2` | note `FK_SET_NULL`(`certain`), FK 부모 링크는 parent. 행 API에서 `parent_id`가 `"2"` → `null` | parent·child 각각 삭제 1, note 수정 1 |
| (c) `UPDATE s.order_lines SET qty = qty + 1 WHERE id = 70` | order_lines `DIRECT`. orders와 history는 `TRIGGER`(`inferred`), 두 사용자 트리거의 사슬과 부모 링크 일치. 행 API에서 orders의 total `20.00` → `30.00`, rev `1` → `2` | order_lines 수정 1, orders 수정 1, history 삽입 1 |
| (d) `TRUNCATE s.parent` | HTTP 422, 문장 1의 `REJECT_TRUNCATE` | — |
| (d) `UPDATE s.parent SET name = set_config('application_name', 'sp22', true) WHERE id = 1` | HTTP 422, 문장 1의 `REJECT_ADMIN_COMMAND` | — |
| `POST /api/previews/{id}/apply` | HTTP 501, (c)의 유효한 미리보기 ID 사용 | — |
| 0행 연쇄와 0행 직접 DELETE | parent 3 삭제 시 child·note 0행 헤더, parent 99 삭제 시 `DIRECT` 0행 헤더. 행 키로 귀속할 근거가 없는 빈 FK 묶음은 `UNEXPLAINED` | parent 3 삭제 1, parent 99 변경 없음 |
| `sp22_nested`에서 parent 3 삭제 | 바깥 parent `DIRECT`, 안쪽 parent `TRIGGER`. child·note는 안쪽 parent에 연결된 깊이 2 FK 묶음. 깊이 2 grandchild 0행 헤더도 보존 | parent 삭제 2, child 삭제 1, note 수정 1 |

두 원본과 두 샌드박스의 업무 행을 각 테이블의 id 순으로 읽어 API 시험 전후 값이 같음을 확인했다. 두 샌드박스의 `dbr_audit.header`와 `dbr_audit.row_image`는 시험 뒤 모두 0행이었다. PostgreSQL 시퀀스는 트랜잭션이 롤백돼도 소모될 수 있다.

## 문제와 남은 확인

- `main`에서 이미 발생한 환경 메시지는 VSTest 임시 아티팩트 경로 안내와 Vite 청크 크기 경고다. 수정 후에도 같은 메시지가 나왔다.
- 시작 브랜치에서 새로 드러난 문제는 PostgreSQL 스크립트의 T-SQL 검사 실패와 잘못된 깊이 보정·중첩 0행 누락이다. 변경 전 Host에서도 child·note가 깊이 1로 내려가고 안쪽 parent가 `UNEXPLAINED`로 남는 현상을 재현했다. 최종 Host에서는 실측 깊이·순번을 보존했다. 이전 코드로 만든 샌드박스에는 새로 고침으로 감사 함수를 다시 설치해야 한다.
- 기존 새로 고침 상태 API는 실패 세부 내용을 응답에 싣지 않는다. 원본 접속 충돌 시 API에서 `error`, PostgreSQL에서 `55006`을 각각 확인했다.
- 실제 SQL Server 연결이 필요한 테스트 66건은 건너뛰었다. 브라우저 상호작용, PostgreSQL 비슈퍼유저 관리 계정, 동일 사용자 트리거의 실제 재귀 발동, 이미지 상한 이후 KEYS 재조회, 대용량 성능, 18.7 외 버전은 이번 실행에서 확인하지 않았다.
