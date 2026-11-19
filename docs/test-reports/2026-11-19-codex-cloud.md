# 2026-11-19 클라우드 DB 검증

PostgreSQL 미리보기 측정 15회에서 연쇄 삭제 건수와 원인 귀속이 기대와 일치했다. 부모 10만 행의 API 응답 중앙값은 26.871초였다. SQL Server용 이미지 다운로드가 차단돼 Split EXEC [적용]과 B1 포함 20회 평가는 미실행이다.

## 환경

Debian 13 x64, Linux 6.18.57, .NET SDK 10.0.404, PowerShell 7.5.5, Docker 29.9.2(VFS)에서 `main`의 `f48207e0ec82a1ae8dcc0658dc7d142225fadfa6`를 검증했다. `nproc`은 3, cgroup CPU 할당량은 2개다. `free -m`은 총 9,968 MiB, cgroup 메모리 한도는 8 GiB였다. 시작 시 작업 파일시스템 가용 공간은 60 GiB였다. `/workspace/.setup/env.sh`를 사용했고 빌드는 기본 권한으로 성공했다. [환경 로그](data/2026-11-19-codex-cloud/environment.log)

## B. 대조군 B1 포함 평가 20회

**미실행(클라우드 한정).** 평가 CLI의 Release 빌드는 경고·오류 없이 성공했다. `docker compose pull sql`은 SQL Server 2025 CU10 이미지 설정 파일을 받다가 `Forbidden`으로 종료했다. 평가 CLI에는 DB 연결이 필요하므로 아래 평가 명령은 시작하지 못했다. 이번 실행의 `cases.csv`, `metrics.csv`, `rejects.json`, `differences.txt`는 없다. 이전 3회 B1 결과는 [평가 §6.1](../evaluation.md#대조군-b12026-11-16-로컬-3회)에 있다. [CLI 빌드](data/2026-11-19-codex-cloud/eval-build.log) · [Docker 로그](data/2026-11-19-codex-cloud/sql2025-docker-pull.log)

이미지를 받을 수 있는 환경에서는 저장소 루트에서 아래 명령을 실행한다. 실행 범위는 §6.3의 명령과 같다(`--cases` 미지정).

```bash
source /workspace/.setup/env.sh
umask 022
docker compose pull sql
pwsh -NoProfile -File scripts/db-up.ps1
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet run --project eval/DbRipple.Eval.Cli -c Release -- cascadelab --systems preview,B0,B1,B2 --repeat 20 --out logs/home-test/cloud-20261119-b/eval --run-id cloud-20261119-b1; exit $LASTEXITCODE'
docker compose stop sql
docker rm dbripple-sql
```

## A. Split 모드 EXEC [적용]

**미실행(클라우드 한정).** 원본용 `2022-latest`와 샌드박스용 `2025-CU10-ubuntu-24.04` manifest는 조회됐다. 두 이미지 모두 설정 파일의 GET이 `westus.data.mcr.microsoft.com`으로 HTTP 307을 반환했다. 해당 호스트는 실행 중인 환경의 허용 목록에 없었고, SQL Server 2025 파일 요청에서 프록시 CONNECT 403을 확인했다. 허용된 Central US 호스트를 통한 설정 파일 요청에는 저장소가 HTTP 403 `AuthenticationFailed`를 반환했다. Microsoft APT 패키지 다운로드도 프록시 CONNECT 403으로 끝났다. [레지스트리 로그](data/2026-11-19-codex-cloud/sql-registry.log) · [네트워크 정책](data/2026-11-19-codex-cloud/network-policy.json)

SQL Server 컨테이너가 시작되지 않아 기준선 생성과 EXEC 미리보기·적용 요청은 보내지 못했다. Split에서의 EXECUTE 권한 및 정의 검사, 반환값·결과 집합 수 대조, 약한 검증 동의, 적용 전후 원본 DDL 및 행 상태는 미확인이다. 기존 SP17 결과는 DML [적용]의 증거다. [SP17 절차와 DML 실측](../spikes/SP17.md) · [EXEC 적용 계약](../architecture.md#d7-적용--같은-실행-봉투로-재실행--커밋-전-검증-게이트)

실행 가능한 환경에서는 SP17 절차로 만든 제한 로그인에 `cl21.usp_Outer`의 EXECUTE와 `cl21.usp_Outer`·`cl21.usp_Inner`의 VIEW DEFINITION을 부여한 뒤 원본 지문을 채집한다. `EXEC cl21.usp_Outer @id = 1, @delta = 5;`의 미리보기로 검증 동의 전후의 응답을 비교하고, 커밋 시 `cl21.Parent`, `cl21.Touch`, `cl21.Log`에 정답의 순 변경이 남는지 원본 SQL로 조회한다. 권한·정의 거부는 전용 사본에서 미리보기 이후 최상위 EXECUTE를 회수하거나 안쪽 정의를 바꿔 검사한다. [CL21 기대값](../../cascadelab/cases/CL21-proc-no-branch.json)

## C. PostgreSQL 18 FK 연쇄 DELETE

PostgreSQL 18.7에서 `perf.parent → perf.child → perf.grandchild`를 `ON DELETE CASCADE`로 연결했다. FK 참조 열에 인덱스를 두고 부모마다 자식 2행, 자식마다 손자 1행을 넣었다. 크기별로 새 원본과 샌드박스를 만들고, 개발 모드 Host에서 옵션 없는 `DELETE FROM perf.parent WHERE id >= 1;` 미리보기를 순차 요청했다. 워밍업 1회는 집계에서 제외하고 5회를 측정했다. [픽스처 SQL](../../spikes/sql/pg_cascade_measure.sql) · [이미지와 버전](data/2026-11-19-codex-cloud/pg-version.log)

```bash
source /workspace/.setup/env.sh
umask 022
docker pull postgres:18
dotnet build src/DbRipple.Host -m:2
mkdir -p logs/home-test/cloud-20261119-c
bash scripts/measure-pg-cascade.sh logs/home-test/cloud-20261119-c/run
```

스크립트는 크기별 새로 고침이 `ready`가 된 뒤 미리보기를 호출한다. 각 미리보기 응답을 저장한 뒤 취소하고, 크기별 원본·샌드박스의 SQL 조회 결과를 대조한 뒤 종료 시 PostgreSQL 컨테이너를 정지·제거한다. 재실행에는 새 로그 디렉터리를 지정한다. [측정 스크립트](../../scripts/measure-pg-cascade.sh)

### 결과(DB·API, 클라우드 한정)

워밍업을 제외한 시간의 단위는 ms다. API는 로컬 HTTP 요청 왕복 시간이고, 나머지 시간 열은 Host의 `timingsMs` 중앙값이다. 최근접 순위로 계산한 p95는 각 크기에서 측정 표본 5개의 최댓값이다. [집계 CSV](data/2026-11-19-codex-cloud/pg-summary.csv) · [개별 표본](data/2026-11-19-codex-cloud/pg-samples.csv)

| 부모 행 | API 상태 | 이미지 수 | execute p50 | readLog p50 | netDiff p50 | resolve p50 | serialize p50 | API p50 | API p95 |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 10,000 | `ok` | 50,000 | 942 | 104 | 242 | 817 | 341 | 2,890.444 | 3,154.466 |
| 50,000 | `truncated` | 250,000 | 4,669 | 426 | 1,207 | 4,662 | 1,168 | 13,280.981 | 62,894.736 |
| 100,000 | `truncated` | 500,000 | 9,170 | 871 | 2,403 | 8,163 | 3,156 | 26,871.266 | 33,345.506 |

- 워밍업을 포함한 18회가 HTTP 200이었다. 부모는 `DIRECT`, 자식과 손자는 `FK_CASCADE`였고 모두 `certain`이었다. 자식·손자의 부모 링크가 FK 경로와 일치했고 미매칭 행은 0이었다. 부모 10만 행에서 순 변경은 부모 10만·자식 20만·손자 20만 행 삭제였다. [미리보기 응답 JSONL](data/2026-11-19-codex-cloud/pg-previews.jsonl)
- 부모 5만·10만 행에서는 기본 이미지 상한 10만 개를 넘어 `truncated`였다. 단일 DELETE에서 모든 발동 묶음은 `FULL`이었고 `reread`는 0 ms였다. 실행기는 문장 뒤에 이미지 수를 검사하고 후속 문장용 모드를 `KEYS`로 바꾼다(정적 확인). 이번 입력으로는 후속 문장의 KEYS 재조회 경로가 실행되지 않았다. [개별 표본](data/2026-11-19-codex-cloud/pg-samples.csv) · [상한 처리 코드](../../src/DbRipple.Postgres/PgPreviewExecutor.cs)
- 각 크기의 기준선은 `ready`였고 감사 대상은 3개 테이블이었다. 각 크기의 측정 전후 원본·샌드박스에서 전체 업무 행 수와 정렬한 열 값의 MD5가 일치했다. 샌드박스의 감사 헤더·행 이미지 테이블은 측정 후 각각 0행이었다. 측정 스크립트는 종료 코드 0을 기록하고 컨테이너를 제거했다. [기준선 JSON](data/2026-11-19-codex-cloud/pg-sandbox.json) · [DB 확인 CSV](data/2026-11-19-codex-cloud/pg-db-checks.csv) · [정리 로그](data/2026-11-19-codex-cloud/pg-cleanup.log)
- 5만 행의 측정 2회차는 HTTP 62.895초였고, Host에서 `execute` 37.835초와 `readLog` 7.935초를 기록했다. 같은 구간의 Host에 Kestrel heartbeat 지연 경고가 있었지만 원인은 확인하지 못했다. 10만 행 구간에서 cgroup 메모리 사용량이 8 GiB 한도에 닿았고 당시 OOM kill은 0건이었다. 느린 표본도 집계에 포함했다. [개별 표본](data/2026-11-19-codex-cloud/pg-samples.csv) · [Host 경고](data/2026-11-19-codex-cloud/pg-host-heartbeat.log) · [메모리 로그](data/2026-11-19-codex-cloud/pg-memory.log)
