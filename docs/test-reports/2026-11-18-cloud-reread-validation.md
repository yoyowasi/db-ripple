# 2026-11-18 재조회 최적화 클라우드 검증

PR #19가 병합된 `main` (`f0f6a7f28ac141a85806b6cdc742d330219143da`)을 실제 SQL Server에서 검증했다. 기본 DB 검사와 픽스처 비교는 Codex 클라우드에서, M5 재측정은 같은 커밋을 체크아웃한 GitHub Actions에서 실행했다. [큰 DELETE 성능 보고서 6.1절](2026-11-17-large-delete-perf.md)

## 실행 환경

- Debian 13 x64, .NET SDK 10.0.404, PowerShell 7.5.5, Docker 29.9.2. 클라우드 한도는 CPU 2개와 메모리 8 GiB였다. SQL Server 설정은 메모리 4 GiB다.
- SQL Server 2025 CU10 Enterprise Developer `17.0.5035.5`. 공식 이미지 `mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-24.04`의 digest `sha256:245f6b1c8903edae52cb20fa31419cefcde1a49938bb8c3447099011fc871ca3`에서 가져온 파일을 사용했다. Docker의 VFS 드라이버가 이미지 레이어를 복제하면서 디스크가 찼다. 이 환경에서는 이미지의 파일을 단일 레이어로 가져오고 `/opt/mssql`을 공유 메모리의 읽기 전용 마운트로 연결했다.
- Microsoft의 AdventureWorks2025 백업을 로컬 SQL Server에 복원했다. 백업 SHA-256은 `fa6a2a5d431ad88123f89b36b1f2c7e42ca4bdf6b293269a44df80f6de3738a5`다.
- 첫 `home-test`는 디스크 부족 오류가 기록된 뒤 중단했다. SQL 런타임과 검증 도구를 공유 메모리에 두어 공간을 확보하고, 중단된 실행이 남긴 임시 테스트 DB를 지운 뒤 새 실행 ID로 처음부터 다시 실행했다.

## 기본 DB 검증

`DBRIPPLE_RUN_SP7=1`을 설정한 `home-test.ps1` 실행 `cloud-20261118-verified`에서 빌드 경고·오류 0개, 테스트 1,384개 통과, 실패·건너뜀 0개를 확인했다. 건수는 11개 TRX의 개별 결과를 합산했다. [테스트 집계](data/2026-11-18-cloud-reread/test-results.csv)

CascadeLab 30사례를 `preview`, `B0`, `B1`, `B2`로 3회씩 실행했다. 360개 실행의 기대 상태가 모두 일치했고, 기준선과 공통인 지표 103개는 값이 같았다. 새 지표 34개는 `NEW`로 기록됐다. 제품의 M1 정밀도·재현율과 M2 전체 재현율은 각각 1.0000, M3 `certain` 과신은 0/141, M4 정규 서명 안정성은 1.0000이었다. [지표 비교](data/2026-11-18-cloud-reread/eval-vs-baseline.csv)

N1 분석기 회귀 검사도 AW 4/4, CascadeLab 29/29, CascadeLab_RT 1/1, CascadeLab_Reach 1/1에서 기대와 일치했다. Docker, Env, Build, Tests, Eval, N1이 모두 `PASS`를 기록해 `summary.json`의 전체 결과가 `PASS`였다.

## M5 재측정

Codex 클라우드에서는 두 사례의 1만·5만 행 측정을 마쳤다. 10만 행 구간의 SQL Server 메모리 할당 대기(`RESOURCE_SEMAPHORE`)가 반복돼 해당 실행을 중단하고, 같은 커밋과 명령으로 GitHub Actions에서 세 크기를 처음부터 실행했다. [GitHub Actions 실행 기록](https://github.com/yoyowasi/db-ripple/actions/runs/44211851823)

GitHub Actions 러너는 Ubuntu 26.04.1 LTS, CPU 4개, 메모리 약 15.3 GiB였다. 실행 `actions-20261118-m5`에서 .NET 10.0.15와 SQL Server 2025 CU10(메모리 4 GiB)을 사용했다. 제품 모드와 기본 행 이미지 상한에서 조합별 워밍업 1회와 측정 5회를 완료했다. 30개 측정 표본은 모두 유효했고 예외 0건, `differences.txt` 0바이트였다. 5만·10만 행의 `truncated` 상태와 이미지·발동·순 변경 건수는 11월 17일과 일치했다. [실행 환경](data/2026-11-18-cloud-reread/m5-env.json) · [개별 표본](data/2026-11-18-cloud-reread/m5-samples.csv)

11월 17일 Codex 클라우드와 이번 GitHub Actions의 중앙값을 나란히 적었다. 단위는 ms이며 각 칸은 `11월 17일 → 11월 18일`이다. 실행 환경 차이도 포함된 비교다. [11월 17일 CSV](data/2026-11-17-cloud/m5.csv) · [11월 18일 CSV](data/2026-11-18-cloud-reread/m5.csv)

| 사례 | N | execute p50 | reread p50 | resolve p50 | total p50 |
|---|---:|---:|---:|---:|---:|
| CL20 DELETE | 10,000 | 766 → 669 | 4,693 → 120 | 627 → 666 | 6,471 → 1,858 |
| CL20 DELETE | 50,000 | 3,231 → 3,007 | 22,905 → 698 | 3,242 → 3,172 | 31,401 → 8,564 |
| CL20 DELETE | 100,000 | 6,103 → 5,899 | 47,784 → 1,461 | 7,413 → 6,688 | 65,510 → 17,784 |
| CL20b UPDATE | 10,000 | 266 → 241 | 91 → 85 | 281 → 284 | 826 → 804 |
| CL20b UPDATE | 50,000 | 1,343 → 1,282 | 467 → 402 | 1,424 → 1,454 | 4,441 → 4,008 |
| CL20b UPDATE | 100,000 | 2,594 → 2,236 | 957 → 913 | 3,160 → 2,872 | 8,464 → 7,827 |

CL20 DELETE 10만 행에서 재조회 중앙값은 47.784초에서 1.461초로 줄었다. 이번 실행의 같은 사례에서 재조회 p95는 1.534초, 전체 p95는 18.238초였다. 재조회 대상 키의 변화는 아래 픽스처에서 확인했다.

## 픽스처 비교

변경 전 `2e96564`와 변경 후 `f0f6a7f`에서 같은 SQL Server로 CascadeLab 픽스처 30개를 다시 생성했다. `CascadeLabTests`와 `Week3EngineTests`는 양쪽에서 각각 40개가 통과했다. 변경 후 재생성한 픽스처로 재조회 동일성 및 CL20 키 수 검사 31개도 통과했다.

- 13개 사례에서 재조회 키가 줄었다. CL20의 부모 2행에서는 10개 중 6개를 생략했고, 남은 4개는 모두 `cl20.ChildLog`였다. CL20b는 5개로 같았다. [사례별 키 수](data/2026-11-18-cloud-reread/fixture-reread-counts.csv)
- `collect.json`은 28개가 바이트 단위로 일치했다. CL16·CL16b의 차이는 `SYSDATETIME()`, `GETDATE()`, `NEWID()`가 만든 `created_at`, `touched_at`, `added_at`, `row_guid` 값이었다. 두 사례에서 해당 값만 정규화한 뒤 전체 수집 결과가 일치했다. [CL16 SQL](../../cascadelab/sql/cl16.sql) · [CL16b 사례](../../cascadelab/cases/CL16b-statement-function.json)

## 재현 명령

로컬 SQL Server와 `AW_Origin`을 준비한 뒤 저장소 루트에서 실행한다. 픽스처는 재조회 변경 전 `2e96564`와 병합 후 `f0f6a7f`에서 각각 생성한다. 두 체크아웃에서 `DbRipple.Diff`의 Debug 빌드가 필요하다.

```bash
DBRIPPLE_RUN_SP7=1 pwsh -NoProfile -File scripts/home-test.ps1 -Date cloud-20261118-verified
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet run --project eval/DbRipple.Eval.Cli -c Release -- m5 --cases CL20,CL20b --sizes 10000,50000,100000 --repeat 5 --warmup 1 --out logs/eval-m5 --run-id actions-20261118-m5; exit $LASTEXITCODE'
dotnet build src/DbRipple.Diff
DBR_WRITE_FIXTURES=1 pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet test tests/DbRipple.Execution.Tests --filter "FullyQualifiedName~CascadeLabTests|FullyQualifiedName~Week3EngineTests"; exit $LASTEXITCODE'
git diff --stat -- tests/fixtures/capture
```
