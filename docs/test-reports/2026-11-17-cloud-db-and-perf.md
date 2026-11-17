# 2026-11-17 클라우드 DB 검증과 큰 DELETE 재측정

대상 애플리케이션 커밋은 main `87df6298242839d3838cca7d0032152ee2ed3097`이다. 검증 브랜치의 앞선 커밋 `38f93ef`는 문서만 추가했다. 실제 실행일은 한국 시각 2026-11-17이다. 새로 게시된 환경에서 이전 네트워크 차단이 해소돼 SQL Server를 실행했다. 애플리케이션 소스·테스트·의존성 선언·잠금 파일은 변경하지 않았다.

## 환경과 준비

- Debian 13 x64, .NET SDK 10.0.404 / 런타임 10.0.15, PowerShell 7.5.5, Node 24.22.1, Docker 29.9.2.
- 클라이언트 cgroup CPU quota 4 CPU, 메모리 한도 32GiB. SQL Server가 보고한 메모리는 4GiB다. 자세한 설정은 [M5 환경 기록](data/2026-11-17-cloud/m5-env.json)에 있다.
- 공식 `mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-24.04`, digest `sha256:245f6b1c8903edae52cb20fa31419cefcde1a49938bb8c3447099011fc871ca3`, SQL `17.0.5035.5`, Enterprise Developer. 저장소 compose 그대로 루프백 14333에 기동했다.
- 공식 AdventureWorks 백업 50,229,248 bytes, SHA-256 `FA6A2A5D431AD88123F89B36B1F2C7E42CA4BDF6B293269A44DF80F6DE3738A5`를 재확인했다. 무작위 로컬 테스트 비밀번호는 ignored `.env`에만 두고 0600을 유지했다. 외부·회사 DB는 사용하지 않았다.
- 호스트 기본 `umask 077` 때문에 복사한 임시 SQL이 0600이 되어 컨테이너의 mssql 사용자가 읽지 못했다. SQL 실행 단계에서 `umask 022`를 사용해 해결했다. 비밀번호를 담은 기존 `.env`의 권한은 바꾸지 않았다. 저장소 스크립트 수정이나 검증 우회는 없다.

## 실제 검증 결과

| 검사 | 결과 | 증거 |
|---|---|---|
| 전체 빌드 | 경고 0, 오류 0 | `logs/home-test/cloud-20261117/dotnet-build.log` |
| 전체 DB 테스트, SP7 활성화 | **1,342 통과, 실패·건너뜀 0**, TRX 11개 실제 outcome 집계 | 같은 폴더 `trx/`, `sp7-aw-netdiff.log` |
| ApplyIntegration 별도 실행 | **163 통과, 실패·건너뜀 0** | `/workspace/.setup/cloud-db/apply-trx/` |
| 기본 평가 | 30사례 × 3회, 상태 불일치 0, 기준선 공통 지표 **103개 동일**, 신규 34개 | [비교 CSV](data/2026-11-17-cloud/baseline-comparison.csv), [지표](data/2026-11-17-cloud/baseline-metrics.csv) |
| 기본 N1 | AW 4/4, CascadeLab 29/29, RT 1/1, Reach 1/1 일치 | `logs/home-test/cloud-20261117/n1/` |
| EXEC 포함 평가 | 37사례 × 3회, 종료 0, 상태 불일치·예상 밖 거부 0 | [지표](data/2026-11-17-cloud/exec-metrics.csv), [비교](data/2026-11-17-cloud/exec-comparison.csv) |
| EXEC 포함 N1 | AW 4/4, CascadeLab 36/36, RT 1/1, Reach 1/1 일치 | `logs/home-test/cloud-20261117-exec/n1/` |
| 문서 명령의 20회 평가 | 37사례 × 20회, 종료 0, CSV 2,100행 전부 상태 일치 | [실행 CSV](data/2026-11-17-cloud/eval20-cases.csv), [지표](data/2026-11-17-cloud/eval20-metrics.csv) |
| Linux PowerShell 실행·정리 회귀 | 24/24 통과 | `/workspace/.setup/home-test-regression.log` |
| HTTP 기능 검사 | 기준선 ready·지문 일치, 미리보기 ok, 취소 204, 적용 committed | `/workspace/.setup/api-smoke/` |

EXEC 평가의 전체 요약 `WARN`은 사례 집합이 30→37로 달라서 생긴다. 비교 지표 차이 17개를 같은 범위의 회귀라고 해석하지 않는다. 제품 M1 precision/recall, M2 ALL recall, M4 canonical stable rate는 1.0000, M3 certain 과신은 0/171이다. 기본 30사례의 회귀 여부는 별도 기본 평가로 확인했다.

20회 평가는 문서의 `cascadelab --systems preview,B0,B2 --repeat 20` 명령 그대로 실행했다. 현재 이 명령은 EXEC·거부 사례까지 포함한다. 제품 740행, B0·B2 각 680행이며, 거부 3사례×20회에는 오라클이 없으므로 해당 비교 칸은 빈 값이다. 나머지 2,040행은 오라클 대 손 정답 키·원문 배치 비교가 모두 true다. 제품 M1 precision/recall 3,840/3,840, M2 ALL recall 2,520/2,520, M3 certain 과신 0/1,140, M4 정규 서명 안정 37/37이다. M4 오류 실행은 0/740이다. [거부 규칙](data/2026-11-17-cloud/eval20-rejects.json)도 기대와 일치한다. 확장 37사례의 집계와 기존 30사례 집계를 직접 비교하지 않는다.

평가 `differences.txt`는 기본 12줄, EXEC 19줄이다. 기존 CL10·CL13 M3 예측 간선 기록, CL12a·CL24의 명시된 잔류행, CL25d 하네스 제외 안내다. CL25d는 Reach N1에서 검사했다. M5의 빈 differences와 이 진단 기록을 혼동하지 않는다.

20회 실행의 [진단 기록](data/2026-11-17-cloud/eval20-differences.txt)은 87줄(CL10 1, CL13 6, CL12a 39, CL24 40, CL25d 안내 1)이다. 반복에 따른 알려진 잔류행 관측을 포함하며, 상태 불일치나 비교기 오류를 숨기기 위해 파일을 비우지 않았다.

첫 로컬 실행기에서 `pwsh -File ... -Skip Build,Tests`의 배열이 단일 문자열로 전달돼 EXEC 실행이 시작 전에 실패했다. 아래 `pwsh -Command` 명령으로 수정해 재실행했고 위 결과는 성공한 재실행의 로그·TRX에서 읽었다. 초기 실패 로그도 보존했다.

## M5: 제품 모드, 워밍업 1회 + 측정 5회

다른 DB 검증 작업과 겹치지 않게 실행했다. 크기별 새 원본·샌드박스·스냅샷을 만들었으며 CL20 DELETE와 CL20b UPDATE 모두 측정했다. [집계 CSV](data/2026-11-17-cloud/m5.csv)는 러너가 기록한 값 그대로다. 6개 조합마다 유효 표본 5개, 총 측정 30개와 워밍업 6개가 완료됐다. 예외 0, M5 `differences.txt` 0바이트다.

단위: ms, 워밍업 제외 p50. total은 각 실행의 단계 합에서 구한 중앙값이므로 표의 단계별 중앙값을 더한 값과 다를 수 있다.

| 사례 | N | execute | reread | resolve(C11+C13) | total |
|---|---:|---:|---:|---:|---:|
| CL20 DELETE | 10,000 | 766 | 4,693 | 627 | 6,471 |
| CL20 DELETE | 50,000 | 3,231 | 22,905 | 3,242 | 31,401 |
| CL20 DELETE | 100,000 | 6,103 | 47,784 | 7,413 | 65,510 |
| CL20b UPDATE | 10,000 | 266 | 91 | 281 | 826 |
| CL20b UPDATE | 50,000 | 1,343 | 467 | 1,424 | 4,441 |
| CL20b UPDATE | 100,000 | 2,594 | 957 | 3,160 | 8,464 |

- CL20 resolve는 1만→5만 **5.17배**, 5만→10만 **2.29배**다. 이전 문서의 초선형 증가보다 선형에 가까운 모양이다. C13만 잰 합성 측정과 달리 이 열은 C11 Collect도 포함한다.
- 문서의 변경 전 CL20 10만 resolve 51.1~55.7초보다 이번 7.413초가 작다. 다만 Windows와 Linux, CPU·메모리·런타임이 달라 동일 머신 전후 실험의 개선 배수로 주장하지 않는다. 같은 이유로 CL20b 10만 8.464초를 이전 7.3~9.8초와 엄밀한 동등성 판정으로 쓰지 않는다.
- CL20 10만 total p50은 65.51초이며 reread p50은 47.784초다(약 73% 규모). 다음 병목은 재조회다.
- N=5만·10만의 `truncated`는 제품의 기본 이미지 상한으로 인한 상태다. 실패나 건너뜀이 아니다. 이미지 수는 CL20 5N, CL20b 3N이며 큰 입력에서 적용 가능한 미리보기라는 뜻도 아니다.

## reread: 빈 테이블의 Scan 반복 확인

기존 성능 보고서 §6의 실험을 별도 임시 CascadeLab 샌드박스에서 실행했다. 부모 10만·자식 20만을 생성하고 기준선을 만든 뒤 **한 연결·한 트랜잭션에서 DELETE → 실제 `RereadSql.For` 질의 → ROLLBACK**했다. 각 테이블에서 키 1천·5천·1만 청크를 한 번씩 실행해 총 9개 질의의 행 수를 확인했다. Parent·Child는 모두 NULL, ChildLog는 모두 행 JSON이었으며 롤백 후 부모 10만 행 복귀를 확인했다. 임시 DB는 정리했다.

| 테이블 | 1천 키 실제 계획 | 인덱스 연산 실행 수 | 인덱스 연산 논리 읽기 | 반환된 현재 행 |
|---|---|---:|---:|---:|
| Parent | Clustered Index Scan | 1,000 | 14,001 | 0 |
| Child | Clustered Index Scan | 1,000 | 13,001 | 0 |
| ChildLog | Clustered Index Seek | 1,000 | 1,929 | 1,000 |

Parent·Child 계획의 `TableCardinality`는 0, 추정 행 수는 1이었다. 실제 계획에서 Scan이 키마다 재실행됐다. Parent 논리 읽기는 청크 1천/5천/1만에서 14,001/70,001/140,001, Child는 13,001/65,001/130,001이다. 기존 문서의 “scan 아닐 가능성” 추정은 이 실험에선 맞지 않았다. 빈 테이블 Scan 비용이 키 수에 비례하므로 선형 증가와 Scan은 양립한다. 고스트 레코드 자체의 내부 비용까지 분리해 입증한 것은 아니다.

ChildLog의 위 숫자는 인덱스 연산만의 읽기다. JSON 생성 등까지 포함한 STATISTICS IO 테이블 합은 2,071회다. [실제 계획 카운터](data/2026-11-17-cloud/reread-plan-summary.csv)와 [전체 IO·시간 기록](data/2026-11-17-cloud/reread-results.json)을 구분해서 읽는다. 실제 생성 SQL도 [Parent](data/2026-11-17-cloud/reread-Parent.sql)·[Child](data/2026-11-17-cloud/reread-Child.sql)·[ChildLog](data/2026-11-17-cloud/reread-ChildLog.sql)로 보관했다.

이 추가 진단은 20회 정확성 평가와 함께 실행했으므로 그 경과 시간을 단독 성능 비교로 쓰지 않는다. 청크 확대만으로는 위 반복 Scan을 없애지 못한다. 키 수 감소나 Seek 유도 등은 결과 동일성과 계약을 별도 검증할 후속 변경이다. 이번 PR에서는 reread 코드를 변경하지 않았다.

## API와 남긴 상태

AW 주문 43659의 DELETE를 미리보기했을 때 원본 주문이 1행 그대로임을 SQL로 확인했다. 취소(204) 뒤에도 1행이었다. 재미리보기 후 적용은 `SNAPSHOT_NETDIFF`, 직접 영향 행 수 preview=actual=1, 순 변경 일치, mismatches=[]로 커밋했다. 응답의 실제 순 변경은 주문 헤더 1행·상세 12행 삭제였고, 원본 주문이 0행임을 별도 SQL로 확인했다.

이후 공식 백업으로 AW_Origin을 다시 복원하고 실행 사용자를 준비했다. 주문 43659는 다시 1행이고 샌드박스도 다시 ready·지문 일치 상태다. 로컬 SQL 컨테이너와 개발 API는 후속 사용을 위해 유지했다. Windows 실행 파일 UX·Split·수동 SP11~21 전체는 이번 실행 범위가 아니다. 기존 PR CI에서는 .NET·web·Windows PowerShell 5.1·SQL Server DB 네 검사가 통과했다. 최종 문서 커밋으로 시작하는 새 CI 상태는 PR에서 별도로 확인한다.

## 재현 명령과 증거

기존 격리 checkout을 사용한다. 새 Git worktree를 만들 필요가 없다. 설치·캐시는 `/workspace/.tools`, `/workspace/.cache`이며 활성화는 `source /workspace/.setup/env.sh`다. .NET의 시스템 뮤텍스가 명령 샌드박스에서 거부되면 이 환경에서 검증한 승인된 escalated 실행을 사용한다.

```bash
cd /workspace/db-ripple
source /workspace/.setup/env.sh
umask 022  # 컨테이너에 복사하는 비밀값 없는 임시 SQL을 읽을 수 있게 함
pwsh -NoProfile -File scripts/db-up.ps1
pwsh -NoProfile -File scripts/restore-aw-origin.ps1
DBRIPPLE_RUN_SP7=1 pwsh -NoProfile -File scripts/home-test.ps1 -Date cloud-20261117
pwsh -NoProfile -Command '& ./scripts/home-test.ps1 -IncludeExec -Skip Build,Tests -Date cloud-20261117-exec'
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet test tests/DbRipple.Apply.Tests -p:ApplyIntegration=true --logger trx --results-directory /workspace/.setup/cloud-db/apply-trx'
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet run --project eval/DbRipple.Eval.Cli -c Release -- m5 --cases CL20,CL20b --sizes 10000,50000,100000 --repeat 5 --warmup 1 --out logs/home-test/cloud-20261117/m5 --run-id cloud-20261117-m5'
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet run --project eval/DbRipple.Eval.Cli -c Release -- cascadelab --systems preview,B0,B2 --repeat 20 --out logs/home-test/cloud-20261117/eval20 --run-id cloud-20261117-eval20'
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet test tests/DbRipple.Apply.Tests -p:ApplyIntegration=true --logger trx'
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet run --project eval/DbRipple.Eval.Cli -c Release -- m5 --cases CL20,CL20b --sizes 10000,50000,100000 --repeat 5 --warmup 1 --out logs/home-test/cloud-20261117/m5 --run-id cloud-20261117-m5'
pwsh -NoProfile -Command '. ./scripts/env.ps1; dotnet run --project eval/DbRipple.Eval.Cli -c Release -- cascadelab --systems preview,B0,B2 --repeat 20 --out logs/home-test/cloud-20261117/eval20 --run-id cloud-20261117-eval20'
```

재실행할 때는 새로운 `-Date` / `--run-id`를 사용해 이전 로그와 섞지 않는다. 기존 `.env`는 보존하며, 새 로컬 테스트 비밀번호가 필요하면 CI의 무작위 생성 절차를 따른다.

원본 로그·TRX는 ignored `logs/home-test/cloud-20261117*` 및 `/workspace/.setup/cloud-db/`에 보관했다. SP7가 갱신한 추적 로그는 실제 결과를 ignored 폴더에 복사한 뒤 원본으로 복구했다. 비밀값·백업·미리보기 원문·빌드 출력은 커밋하지 않는다.
