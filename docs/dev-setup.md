# 개발 환경 준비 (SQL Server 컨테이너)

> 2026-11-06 기준 작성. 이 PC에는 Docker·WSL이 없어 아래 절차는 **아직 한 번도 실행하지 않았다**(정적 작성). 월요일 설치 후 실행하며 틀린 곳을 고친다.

## 고정한 값

| 항목 | 값 | 근거 |
|---|---|---|
| 이미지 | `mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-24.04` | 태그 목록 조회(`https://mcr.microsoft.com/v2/mssql/server/tags/list`)에서 2025 최신 CU 선택. 조회 2026-11-06 11:10 KST (태그 목록만 조회, 이미지 미다운로드). `2025-latest`는 움직이므로 쓰지 않음 |
| 에디션 | `MSSQL_PID=EnterpriseDeveloper` | SP0에서 스냅샷 확인. 실패하면 `Developer`, 그래도 안 되면 2022-latest (SP0.md) |
| 메모리 | 컨테이너 한도 6g, `MSSQL_MEMORY_LIMIT_MB=4096` | 4GB 이상 요구 충족 |
| 포트 | `127.0.0.1:14333 -> 1433` | 호스트 포트는 `.env`의 `MSSQL_HOST_PORT` |
| 저장소 | named volume `mssql-data` -> `/var/opt/mssql` | 데이터 `/var/opt/mssql/data`, 백업 `/var/opt/mssql/backup`. Windows 바인드 마운트는 스파스 파일(스냅샷)·O_DIRECT 문제 가능(추정, 미확인)이라 피함 |
| AdventureWorks | `AdventureWorks2025.bak`, 50,229,248 bytes (약 47.9 MiB) | GitHub releases URL에 HEAD 요청, 200 확인 (2026-11-06). 다운로드는 안 함 |

AW .bak URL: `https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorks2025.bak`

## 월요일 절차

1. 관리자 PowerShell: `wsl --install --no-distribution` 후 재부팅.
2. Docker Desktop 설치: `winget install -e --id Docker.DockerDesktop` 또는 공식 설치 파일. Settings에서 WSL2 백엔드 사용 확인 (`docker info`에 `OSType: linux`, `wsl -l -v`에 `docker-desktop` VERSION 2).
   - 라이선스: 직원 250명 이상 또는 매출 1천만 달러 이상 기업은 Docker Desktop이 유료 구독 대상. 대안은 WSL2 배포판 안에 Docker Engine 설치.
3. `.env` 작성: `Copy-Item .env.example .env` 후 `MSSQL_SA_PASSWORD`를 강한 값으로 교체. (파일을 열어 직접 편집, 값을 화면에 출력하지 않음)
4. `.\scripts\db-up.ps1` — compose up, healthy까지 대기.
5. `.\scripts\fetch-adventureworks.ps1` — `.data\backup\`에 .bak 다운로드, 크기·SHA256 출력. 크기가 위 값과 같은지 확인.
6. `.\scripts\restore-aw-origin.ps1` — docker cp, FILELISTONLY, `AW_Origin`으로 RESTORE WITH MOVE.
7. SP0: `.\scripts\run-sql.ps1 spikes\sql\sp0-edition-snapshot.sql` (docs/spikes/SP0.md).
8. SP5: `sp5-1` ~ `sp5-4`를 순서대로 `run-sql.ps1`로 실행 (`sp5-3`은 `-Measure`) (docs/spikes/SP5.md).

## 확인해야 할 것

- sqlcmd 경로: 이미지마다 다를 수 있다. `docker compose exec sql ls /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd`로 확인하고, 없는 쪽이면 compose.yaml healthcheck와 scripts의 경로를 바꾼다. 2025 이미지는 tools18에 `-C`(서버 인증서 신뢰)가 필요하다고 알려져 있으나 미확인.
- 비밀번호는 컨테이너 환경 변수 `$MSSQL_SA_PASSWORD`로만 쓴다. 호스트 명령줄에 넣지 않는다.
- `docker compose exec -T sql bash -c ... < 표준입력`으로 SQL을 넘기는 방식(run-sql.ps1, restore-aw-origin.ps1)은 sqlcmd가 stdin을 읽는다는 가정이다. 안 되면 `docker cp` 후 `sqlcmd -i`로 바꾼다.
- 초기화: `docker compose down -v`는 volume(AW_Origin 포함)을 지운다. 지우기 전 확인.

## backend-2와의 합의 사항

기준선 구축 코드는 백업 경로 `/var/opt/mssql/backup`, 데이터 경로 `/var/opt/mssql/data`를 그대로 쓴다.
