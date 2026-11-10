# SP0 — 에디션과 스냅샷 가능 여부

**결과: 통과 (2026-11-10, DB 실측).** SQL Server 2025 RTM-CU10 Linux 컨테이너가 `MSSQL_PID=EnterpriseDeveloper`와 `Developer` 모두 Enterprise Developer Edition으로 뜨고, 파일별 경로를 지정한 데이터베이스 스냅샷이 만들어진다. D1(2025 유지)의 근거다.

## 목적
컨테이너의 `EnterpriseDeveloper`(및 `Developer`)에서 데이터베이스 스냅샷이 만들어지는지 확인한다(설계 A14~A17, R4).

## 방법
1. `.\scripts\db-up.ps1` 후 `.\scripts\run-sql.ps1 spikes\sql\sp0-edition-snapshot.sql -Log spikes\logs\sp0-enterprisedeveloper.log`
   - SERVERPROPERTY(Edition, EngineEdition, ProductVersion, ProductLevel, ProductUpdateLevel), @@VERSION 기록
   - 작은 DB 생성 → `sys.database_files`의 데이터 파일 논리 이름마다 경로를 지정해 스냅샷 생성 → `sys.databases.source_database_id` 확인 → 삭제
2. `Developer` 값은 기본 컨테이너를 건드리지 않도록 볼륨 없는 임시 컨테이너(`dbr-sp0-dev`, `MSSQL_PID=Developer`, 같은 이미지)를 띄워 같은 스크립트를 실행하고 지웠다(`spikes\logs\sp0-developer.log`). 초안의 "`.env`를 바꿔 기본 컨테이너 재생성" 방법은 쓰지 않았다. 독립 검토에서 첫 로그의 머리줄이 손으로 쓴 것이라 PID를 로그로 가를 수 없다는 지적을 받아, 10:10에 다시 실행해 `docker inspect`의 이미지와 `MSSQL_PID=Developer`를 로그 머리줄에 남겼다(결과 동일).
3. 2022 대체안은 필요 없어 실행하지 않았다.

실행 세션은 `sqlcmd -I` 도입 전(QUOTED_IDENTIFIER OFF)이었지만, 에디션 조회와 DB·스냅샷 생성은 이 설정과 무관하다.

실행 환경: Docker Desktop 29.9.2(WSL2, VM 메모리 약 7.5GiB), 이미지 `mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-24.04`(다이제스트 `sha256:245f6b1c8903edae52cb20fa31419cefcde1a49938bb8c3447099011fc871ca3`). 버전·다이제스트·기본 컨테이너의 `MSSQL_PID=EnterpriseDeveloper`는 `logs/week1/env-evidence.log`에 남겼다.

## 판정 기준
- 통과: `SNAPSHOT_OK`, 스냅샷 행의 `source_database_id`가 원본 DB id와 같음, Edition 문자열이 Developer 계열.
- 실패: `SNAPSHOT_FAIL`과 오류 번호 → 2022 대체안으로 전환하고 설계 §2 A14~A17을 갱신.

## 결과

| 구성 | Edition (EngineEdition) | ProductVersion / Level / Update | 스냅샷 | 로그 |
|---|---|---|---|---|
| 2025-CU10 + EnterpriseDeveloper | Enterprise Developer Edition (64-bit) (3) | 17.0.5035.5 / RTM / CU10 | `SNAPSHOT_OK`, 스냅샷 `source_database_id = 5` = 원본 | `spikes/logs/sp0-enterprisedeveloper.log` |
| 2025-CU10 + Developer | Enterprise Developer Edition (64-bit) (3) | 17.0.5035.5 / RTM / CU10 | `SNAPSHOT_OK`, `source_database_id = 5` | `spikes/logs/sp0-developer.log` |
| 2022-latest + Developer | 실행 안 함(필요 없음) | — | — | — |

**확인한 사실**
- `@@VERSION`: `Microsoft SQL Server 2025 (RTM-CU10) (KB5125471) - 17.0.5035.5 (X64) … Enterprise Developer Edition (64-bit) on Linux (Ubuntu 24.04.5 LTS)`.
- `MSSQL_PID=Developer`도 Enterprise Developer Edition이 된다. 설계 A17의 "PID 표의 EnterpriseDeveloper와 예시의 Developer가 다르다"는 불일치는 이 이미지에서 같은 결과로 확인됐다.
- 스냅샷은 데이터 파일 논리 이름마다 `/var/opt/mssql/data/…ss` 경로를 지정해 named volume(ext4) 위에 만들어진다.

**추정**
- 스냅샷이 named volume에서 되는 것은 확인했지만, Windows 바인드 마운트에서 안 되는지는 시험하지 않았다(구성에서 쓰지 않음).

## 증거 범위
DB 실측(컨테이너 dbripple-sql, 2026-11-10). 로그 두 개.

## 설계 반영
- D1: SQL Server 2025 EnterpriseDeveloper(CU10 태그 고정)로 확정. 2022 대체는 필요 없다.
- A17: `Developer`와 `EnterpriseDeveloper` 모두 Enterprise Developer Edition으로 뜬다는 실측을 덧붙인다.
