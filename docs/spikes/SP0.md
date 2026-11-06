# SP0 — 에디션과 스냅샷 가능 여부

**결과: 미실행(2026-11-06 Docker 미설치)**

## 목적
컨테이너의 `EnterpriseDeveloper`(및 `Developer`)에서 데이터베이스 스냅샷이 만들어지는지 확인한다(설계 A14~A17, R4).

## 방법
1. `.\scripts\db-up.ps1` 후 `.\scripts\run-sql.ps1 spikes\sql\sp0-edition-snapshot.sql`
   - SERVERPROPERTY(Edition, EngineEdition, ProductVersion, ProductLevel, ProductUpdateLevel), @@VERSION 기록
   - 작은 DB 생성 -> `sys.database_files`의 데이터 파일 논리 이름마다 경로를 지정해 스냅샷 생성 -> `sys.databases.source_database_id` 확인 -> 삭제
2. `.env`의 `MSSQL_PID=Developer`로 바꾸고 `docker compose up -d --force-recreate` 후 1을 다시 실행(데이터는 volume에 유지됨).
3. 둘 다 실패하면 `.env`의 `MSSQL_IMAGE_TAG=2022-latest`, `MSSQL_PID=Developer`로 바꿔 재생성하고 1을 반복. volume의 DB는 상위 버전이 만든 파일이므로 `docker compose down -v`로 초기화 후 restore 다시 수행.

## 판정 기준
- 통과: `SNAPSHOT_OK`, 스냅샷 행의 `source_database_id`가 원본 DB id와 같음, Edition 문자열이 Developer 계열.
- 실패: `SNAPSHOT_FAIL`과 오류 번호 -> 위 3단계 대체안으로 전환하고 설계 문서 §2 A14~A17을 갱신.

## 결과 칸
| 구성 | Edition | ProductVersion/Update | 스냅샷 | 오류 번호 |
|---|---|---|---|---|
| 2025-CU10 + EnterpriseDeveloper | 미실행(2026-11-06 Docker 미설치) | | | |
| 2025-CU10 + Developer | 미실행(2026-11-06 Docker 미설치) | | | |
| 2022-latest + Developer | 미실행(필요 시) | | | |
