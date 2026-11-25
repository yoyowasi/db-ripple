# 2026-11-25 이미지 상한 변경 뒤 M5 재측정

SQL Server와 PostgreSQL 18의 모든 표본에서 FULL 이미지가 10만 개 이하였다. SQL Server CL20의 total p50은 1만·5만·10만 행에서 2.564·10.614·16.609초였다. PostgreSQL 부모 10만 행은 API p50 16.651초, 순 삭제 50만 행이었다. PostgreSQL의 5만·10만 행에서 FK 연결은 유지됐고 자식·손자 귀속의 신뢰도는 `certain`에서 `inferred`로 낮아졌다.

## 환경과 명령

측정 대상은 `main`의 `882801242c1355a502a4af3476c9c1e67a8f27ff`다. GitHub Actions의 별도 `ubuntu-latest` 잡 두 개에서 기본 FULL 이미지 상한 100,000개, 크기별 워밍업 1회와 측정 5회로 실행했다. [실행 기록](https://github.com/yoyowasi/db-ripple/actions/runs/45514031243)

| 항목 | 이전 환경 | 11-25 환경 |
|---|---|---|
| SQL Server | 11-18 Actions Ubuntu 26.04.1 LTS, vCPU 4개, 15,700 MiB, .NET 런타임 10.0.15 | Ubuntu 26.04.1 LTS, Intel Xeon Platinum 8573C vCPU 4개, 15,982 MiB, .NET 런타임 10.0.15 |
| SQL Server 컨테이너 | 2025 CU10(17.0.5035.5), 4 vCPU, 메모리 4 GiB, SQL Server 커밋 목표 3,468,488 KiB | 같은 버전·vCPU·메모리, 커밋 목표 3,436,848 KiB |
| PostgreSQL | 11-19 Codex 클라우드 Debian 13, CPU 할당 2개, 메모리 한도 8 GiB, PostgreSQL 18.7(Debian 패키지 +2) | Ubuntu 26.04.1 LTS, Intel Xeon 6975P-C vCPU 4개, 15,700 MiB, PostgreSQL 18.7(Debian 패키지 +5) |

두 잡에서 `setup-dotnet`에 `10.0.x`를 지정했다. 러너의 `dotnet --version`은 둘 다 11.0.100을 기록했고, SQL Server 평가 CLI가 기록한 런타임은 10.0.15였다.

[SQL Server 러너](data/2026-11-25-m5-after-image-cap/actions-env.txt) · [SQL Server 환경 JSON](data/2026-11-25-m5-after-image-cap/env.json) · [PostgreSQL 러너](data/2026-11-25-m5-after-image-cap/pg-actions-env.txt) · [PostgreSQL 버전](data/2026-11-25-m5-after-image-cap/pg-version.txt) · [이미지 다이제스트](data/2026-11-25-m5-after-image-cap/pg-image.txt)

```powershell
pwsh -NoProfile -File scripts/db-up.ps1 -TimeoutSec 240
. ./scripts/env.ps1; dotnet run --project eval/DbRipple.Eval.Cli -c Release -- m5 --cases CL20,CL20b --sizes 1,100,1000,10000,50000,100000 --repeat 5 --warmup 1 --out logs/eval-m5 --run-id actions-20261125-m5
```

```bash
dotnet build src/DbRipple.Host -m:2
mkdir -p logs
bash scripts/measure-pg-cascade.sh logs/pg-cascade-20261125
```

## SQL Server M5

측정 단계가 종료 코드 0으로 끝났다. `m5.csv`는 12행이고 각 행의 `samples`와 `ok_samples`는 5였다. `differences.txt`는 0바이트였다. 실행 로그에서 워밍업 12개와 측정 60개를 추출해 total p50·p95 및 FULL 이미지 최댓값이 CSV와 일치하는 것을 확인했다. [잡 로그](https://github.com/yoyowasi/db-ripple/actions/runs/45514031243/job/138868719515) · [differences.txt](data/2026-11-25-m5-after-image-cap/differences.txt)

### 1만·5만·10만 행

각 칸은 11-18 → 11-25이며 단위는 ms다. total은 제품 내부 7단계의 합이고, p95는 워밍업을 제외한 5회에서 선형 보간한다. [11-18 기준 CSV](data/2026-11-18-cloud-reread/m5.csv) · [11-25 CSV](data/2026-11-25-m5-after-image-cap/m5.csv) · [11-25 표본](data/2026-11-25-m5-after-image-cap/m5-samples.csv)

| 사례 | 직접 대상 행 | execute p50 | reread p50 | resolve p50 | total p50 | total p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| CL20 | 10,000 | 669 → 1,117 | 120 → 158 | 666 → 711 | 1,858 → 2,564 | 2,003.6 → 2,725.4 |
| CL20 | 50,000 | 3,007 → 4,260 | 698 → 908 | 3,172 → 3,248 | 8,564 → 10,614 | 8,950 → 10,973.6 |
| CL20 | 100,000 | 5,899 → 6,742 | 1,461 → 25 | 6,688 → 5,873 | 17,784 → 16,609 | 18,238.4 → 16,959.2 |
| CL20b | 10,000 | 241 → 356 | 85 → 111 | 284 → 317 | 804 → 1,068 | 825.6 → 1,224 |
| CL20b | 50,000 | 1,282 → 1,394 | 402 → 170 | 1,454 → 1,266 | 4,008 → 3,843 | 4,452.6 → 4,019 |
| CL20b | 100,000 | 2,236 → 2,599 | 913 → 366 | 2,872 → 2,440 | 7,827 → 7,388 | 8,641.2 → 7,861 |

`image_count`는 `rowJson`이 있는 FULL 이미지의 누적 수이며 CSV에는 측정 5회 중 최댓값을 남긴다. 상태와 건수도 11-18 → 11-25로 적었다.

| 사례 | 직접 대상 행 | 상태 | FULL 이미지 수 | 순 변경 행 |
| --- | ---: | --- | ---: | ---: |
| CL20 | 50,000 | `truncated` → `truncated` | 250,000 → 100,000 | 250,000 → 250,000 |
| CL20 | 100,000 | `truncated` → `truncated` | 500,000 → 0 | 500,000 → 500,000 |
| CL20b | 50,000 | `truncated` → `truncated` | 150,000 → 50,000 | 75,000 → 75,000 |
| CL20b | 100,000 | `truncated` → `truncated` | 300,000 → 100,000 | 150,000 → 150,000 |

- 네 대형 조합 모두 `truncated`였고 순 변경 건수는 유지됐다. CL20 10만 행에서 FULL 이미지가 0개인 것은 첫 발동 묶음이 남은 예산을 넘으면 그 발동부터 KEYS에 머무르는 설계와 일치한다. CSV에는 개별 발동의 순서가 들어 있지 않다. [설계 §5.2](../architecture.md#52-미리보기)
- CL20 1만·5만 행은 execute가 각각 0.448·1.253초 늘어 total 증가에서 가장 큰 단계였다. 10만 행은 execute가 0.843초, netDiff가 0.441초 늘었다. 더 큰 폭으로 줄어든 단계는 reread 1.436초, resolve 0.815초였다. 10만 행에서 FULL 이미지가 0개라 FULL 후보를 읽는 재조회 비용이 줄어든 동작과 맞는다. 단계별 p50의 합과 표본별 total의 p50은 서로 다를 수 있다.
- CL20b 5만·10만 행에서 execute는 각각 0.112·0.363초 늘었고 reread는 0.232·0.547초, resolve는 0.188·0.432초 줄었다. 두 크기의 total도 줄었다. 1만 행에서는 FULL 수 3만 개가 유지됐고 execute를 포함한 네 단계 중앙값이 모두 늘었다.
- 두 실행의 서버 버전·컨테이너 메모리·vCPU 수는 같다. 러너 총 메모리와 SQL Server 커밋 목표가 조금 달랐고 11-18 CPU 모델은 남아 있지 않다. 감사 수집이 없는 오라클 execute p50도 6개 조합 모두 증가했다. 예를 들어 CL20 5만 행은 0.994초에서 1.399초였다. 따라서 execute 및 전체 시간의 차이에서 #31과 러너 차이의 기여도를 따로 정량화할 수 없다.

### 1·100·1천 행

각 칸은 11-12 → 11-25이며 단위는 ms다. 11-12는 Windows 10의 Docker Desktop(WSL2, 논리 CPU 16개)에서 실행했으므로 참고값이다. [11-12 평가 §6.2](../evaluation.md#62-m5-응답-시간)

| 사례 | 직접 대상 행 | execute p50 | reread p50 | resolve p50 | total p50 | total p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| CL20 | 1 | 8 → 5 | 4 → 0 | 1 → 1 | 18 → 8 | 22.6 → 21.8 |
| CL20 | 100 | 33 → 27 | 12 → 2 | 3 → 5 | 75 → 50 | 98.4 → 280.8 |
| CL20 | 1,000 | 141 → 167 | 589 → 16 | 63 → 48 | 885 → 280 | 915.2 → 307.2 |
| CL20b | 1 | 11 → 5 | 2 → 1 | 2 → 1 | 19 → 8 | 23.6 → 8.8 |
| CL20b | 100 | 26 → 19 | 3 → 2 | 2 → 2 | 36 → 26 | 41.6 → 27 |
| CL20b | 1,000 | 62 → 61 | 11 → 11 | 20 → 13 | 112 → 116 | 132 → 155.4 |

## PostgreSQL 18 FK 연쇄 DELETE

부모마다 자식 2행, 자식마다 손자 1행을 넣었다. 새 기준선을 만든 뒤 `DELETE FROM perf.parent WHERE id >= 1;`를 옵션 없이 요청했다. 각 칸은 11-19 → 11-25이며 시간 단위는 ms다. API는 로컬 HTTP 왕복 시간이고 p95는 측정 5회의 최댓값이다. [11-19 기준 보고서](2026-11-19-codex-cloud.md#c-postgresql-18-fk-연쇄-delete) · [11-25 집계](data/2026-11-25-m5-after-image-cap/pg-summary.csv)

| 부모 행 | 상태 | FULL 이미지 수 | API p50 | API p95 |
| ---: | --- | ---: | ---: | ---: |
| 10,000 | `ok` → `ok` | 50,000 → 50,000 | 2,890.444 → 1,305.359 | 3,154.466 → 1,878.778 |
| 50,000 | `truncated` → `truncated` | 250,000 → 50,000 | 13,280.981 → 8,145.070 | 62,894.736 → 8,226.890 |
| 100,000 | `truncated` → `truncated` | 500,000 → 100,000 | 26,871.266 → 16,650.617 | 33,345.506 → 17,081.113 |

단계별 p50이며 Host total은 표본마다 `timingsMs` 7단계를 합한 뒤 중앙값을 구했다.

| 부모 행 | execute | readLog | reread | netDiff | resolve | serialize | Host total |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 | 942 → 573 | 104 → 31 | 0 → 0 | 242 → 139 | 817 → 387 | 341 → 95 | 2,517 → 1,173 |
| 50,000 | 4,669 → 2,057 | 426 → 160 | 0 → 0 | 1,207 → 3,211 | 4,662 → 1,753 | 1,168 → 426 | 12,119 → 7,605 |
| 100,000 | 9,170 → 4,159 | 871 → 359 | 0 → 0 | 2,403 → 6,788 | 8,163 → 3,484 | 3,156 → 809 | 23,859 → 15,710 |

- 5만·10만 부모 행에서 execute·resolve는 줄고 netDiff는 늘었다. `reread`는 모두 0 ms였다. PostgreSQL 실행기는 KEYS 묶음의 순 변경을 확인하는 재조회를 netDiff 안에서 잰다. 10만 행은 execute와 resolve가 각각 5.011초, 4.679초 줄었고 netDiff는 4.385초 늘었다. [실행기](../../src/DbRipple.Postgres/PgPreviewExecutor.cs)
- 워밍업 포함 18회가 HTTP 200이었다. 순 삭제는 부모·자식·손자 기준으로 1만·2만·2만, 5만·10만·10만, 10만·20만·20만 행으로 11-19와 같았다. FK 링크와 롤백도 일치했다. 세 크기 모두 기준선 `ready`, 원본·샌드박스 행 수와 MD5 일치, 감사 테이블 잔류 0행이었다. [표본](data/2026-11-25-m5-after-image-cap/pg-samples.csv) · [DB 확인](data/2026-11-25-m5-after-image-cap/pg-db-checks.csv) · [로그 요약](data/2026-11-25-m5-after-image-cap/pg-log-summary.md)
- 1만 행은 11-19와 같이 부모 `DIRECT`, 자식·손자 `FK_CASCADE`가 모두 `certain`이었다. 5만·10만 행은 부모가 FULL, 자식·손자가 KEYS였다. 부모는 `certain`을 유지했고 자식·손자는 각각 6회 모두 `inferred`였다. 두 FK의 경로는 맞았다. 행별 FK 값 확인에 필요한 자식 이미지가 빠져 `matchedRows`·`unmatchedRows`가 모두 0이었다. 11-19와 같은 행 단위 확실성은 1만 행에서 재현됐다. [FK 귀속 코드](../../src/DbRipple.Attribution/FkJudge.cs)
- FULL 수 감소는 #31의 동작과 일치한다. 지연 시간 비교에는 클라우드에서 Actions로 바뀐 자원과 PostgreSQL 이미지 빌드, #33의 미리보기 연결 변경도 함께 반영된다. 11-19 5만 행 p95 62.895초에는 당시 느린 표본 1개가 포함돼 있었다. [11-19 환경 및 이상치](2026-11-19-codex-cloud.md#c-postgresql-18-fk-연쇄-delete)

## 측정하지 못한 것

SQL Server M5는 제품 내부 시간만 잰다. SQL Server HTTP 왕복, 브라우저 렌더, 사용자 DB, 동시 요청과 반복 실행 간 러너 편차는 이번 범위에 없었다. 같은 러너에서 #31 전후 커밋만 바꾼 대조 실험도 하지 않았다.
