# 2026-11-15 클라우드 DB 검증

실제 실행 시각은 2026-11-14 UTC(한국 시각 11월 15일)다. 새 클라우드 안의 공개 AdventureWorks/CascadeLab만 사용했다. 외부 운영 DB와 로컬 PC에는 연결하지 않았다. 원본 로그·TRX·API 응답은 ignored `logs/home-test/cloud/`에 보관한다. 비밀번호는 무작위 생성한 ignored `.env`에서만 읽었고 출력·커밋하지 않았다.

## 환경과 공간 제한 우회

- .NET SDK 10.0.402, PowerShell 7.5.4, Docker 29.9.2.
- SQL Server 2025 CU10 `17.0.5035.5`, Enterprise Developer, 루프백 `127.0.0.1:14333`.
- 공식 `AdventureWorks2025.bak` 50,229,248 bytes, SHA256 `FA6A2A5D431AD88123F89B36B1F2C7E42CA4BDF6B293269A44DF80F6DE3738A5`. `AW_Origin` 복원과 DML 전용 `dbr_preview_user` 준비를 완료했다.
- 기본 Docker vfs가 7.9GB 디스크를 채워 컨테이너 생성이 ENOSPC로 실패했다. `docker save`도 daemon 임시파일 공간 때문에 실패했다. 공식 MCR 이미지의 Ubuntu·의존성 레이어만 단일 경량 이미지 `dbripple-sql-thin:cloud`로 만들고 동일 이미지의 SQL 바이너리를 메모리 디스크에서 `/opt/mssql`에 읽기 전용 bind mount해 실제 SQL을 기동했다. DB 데이터는 새 Docker named volume이다. 이미지의 시작 스크립트와 동일하게 `EnterpriseDeveloper` 설정을 런타임 `Developer` 값으로 변환했다.
- SDK와 SQL 바이너리의 메모리 디스크 경로는 escalated 실행 환경에서만 보인다. 후속 실행도 같은 환경이 필요하며 메모리 디스크는 영구 보관이 아니다.

## 검증 결과

| 검증 | 결과 | 원본 증거 |
|---|---|---|
| 신규 EXEC 첫 DELETE 실제 DB 회귀 | all/none/partial/branch 4 통과, 실패·건너뜀 0. 최종 gate 빌드에서도 전체 Integration 17/17에 포함 | `entry-tests.log`, `entry-trx/`, `db-trx/` |
| 전체 실제 DB 테스트 + SP7 opt-in | 11프로젝트 1,191 통과, 실패·건너뜀 0, 명령 종료 0 | `db-all.log`, `db-trx/` |
| SP7 독립 실DB 비교 | Diff.Tests 8/8 통과에 포함, 모든 독립 확인 일치 | `sp7-aw-netdiff.log`, `db-trx/` |
| 최종 기본 평가 | 30사례 × 3회, 제품+B0/B1/B2, 종료 0; 공통 103지표 동일, B1 33지표 신규 | `baseline-final/summary.md`, `baseline-final/eval-vs-baseline.csv` |
| EXEC 포함 최종 평가 | 37사례 × 3회, 제품+B0/B1/B2, 종료 0; 상태·오라클 손정답 키·raw batch 불일치 없음 | `exec/summary.md`, `exec/eval/eval-quick/` |
| EXEC 포함 최종 N1 API | AW 4/4, CascadeLab 36/36, RT 1/1, Reach 1/1 일치 | `n1-complete/summary.md`, `n1-complete/n1/*/verdict.txt` |

기본 평가 제품 지표는 M1 precision/recall 1.0000/1.0000, M2 ALL recall 1.0000, M3 certain 과신 0/141, M4 canonical stable rate 1.0000이다. `differences.txt` 12줄은 CL10·CL13의 기존 M3 예측 간선 차이와 CL12a의 명시된 잔류행 관측이다. 상태 불일치와 예상 밖 거부는 없다. 최종 기본 평가는 마지막 orphan-image 거부 gate 이후 Release 빌드로 재실행했다. gate 전 초기 기본 실행은 `baseline/`에 별도 보관하며, 최종 실행과 136개 전체 지표 값이 동일하다.

## 재현 명령

PowerShell에서 `. ./scripts/env.ps1`로 이번 `.env`의 루프백 연결을 만들었다. 연결 문자열은 표시하지 않았다.

```powershell
dotnet test tests/DbRipple.Integration.Tests --no-build --filter FullyQualifiedName~ExecEntryDeleteIntegrationTests --logger trx --results-directory logs/home-test/cloud/entry-trx
$env:DBRIPPLE_RUN_SP7 = '1'
dotnet test DbRipple.slnx --no-build -m:1 --logger trx --results-directory logs/home-test/cloud/db-trx
& ./scripts/home-test.ps1 -Skip Docker,Build,Tests,N1 -Date cloud/baseline-final -EvalRepeat 3
& ./scripts/home-test.ps1 -Skip Docker,Build,Tests,N1 -Date cloud/exec -EvalRepeat 3 -IncludeExec
& ./scripts/home-test.ps1 -Skip Docker,Build,Tests,Eval -Date cloud/n1-complete -IncludeExec
```

`Docker` 단계는 커스텀 경량 컨테이너를 원본 대용량 이미지로 재생성하지 않도록 건너뛰었다. Env 단계에서 실제 SQL 연결을 확인했다. 빌드·테스트는 별도 명령으로 실행하며, N1/eval 요약에서 건너뛴 단계를 실측으로 세지 않았다. SP7가 쓰는 추적 중인 기존 `logs/week2/sp7-aw-netdiff.log`는 실제 출력만 ignored 폴더에 복사하고 HEAD 원본으로 복구했다.

## 확인한 실행 환경 문제

N1 첫 실행은 PowerShell Linux의 `Start-Process -WindowStyle Hidden` 미지원으로 네 대상 모두 실패했다. Windows에서만 옵션을 넣도록 수정한 다음에는 cleanup의 Windows 전용 `taskkill` 미존재로 실패했고 해당 실행이 생성한 Host 두 프로세스만 정리했다. 초기 실패는 `n1-initial*`, `n1-final*` 원본 로그에 남겨 후속 성공과 구분한다.

## 최종 gate와 CL22 확인

전체 DB 실행 중 프로젝트별 testhost가 생성된다. 최종 빌드 뒤 시작한 Execution 결과 103개 안에서 `mode: orphanImage, proven: False` 회귀가 실제 Passed이고, Integration 결과 17개 안에서 신규 네 변형이 모두 Passed임을 TRX 이름과 outcome으로 확인했다. SP7도 실제 실행되어 DB 관련 건너뜀이 없다.

최종 EXEC 평가 `records/CL22-r1.json`부터 `r3.json`까지 직접 확인했다. 매회 ChildA 2행이 `PROC_DIRECT/certain`이며 `procedureEntryDelete` 실행 증거를 갖는다. ChildB 1행은 `FK_CASCADE/certain`이고 `fkKeyMatch` 증거를 갖는다. ChildA가 FK로 잘못 귀속되던 문제는 실제 DB에서 재현되지 않았다.

EXEC 제품 지표는 M1 precision/recall 1.0000/1.0000, M2 ALL recall 1.0000, M3 certain 과신 0/171, M4 canonical stable rate 1.0000이다. 스크립트의 전체 WARN은 이전 30사례 기준선과 37사례 집합을 비교한 결과다. 지표차 17개는 B0 10개·제품 6개·B2 1개이며 집계 대상·분모가 다르므로 같은 범위 회귀로 해석하지 않는다. 제품 차이는 CAP recall/f1/row_exact/residual 비율과 all-confidence M3 precision이다. 기존 30사례의 동등성은 별도 기본 평가로 판단한다.

EXEC `differences.txt` 19줄은 기존 CL10·CL13 M3 예측 차이 7줄, CL12a 명시된 잔류행 5줄, CL24 명시된 잔류행 6줄, CL25d를 evaluator가 지원하지 않아 제외한다는 안내 1줄이다. CL25d의 두 문장 거부는 Reach N1에서 확인했다. 새 예상 밖 실패나 상태 불일치는 없다.

N1 최종 실행은 Host 시작·정리까지 정상 종료했다. 종료 후 루프백 5081 포트를 다시 bind할 수 있어 임시 Host가 남지 않았음을 확인했다. 이번 `.env`의 생성 비밀번호가 cloud 원본 로그에 포함되어 있는지 검사한 결과 0건이었다.

최종 gate 이후 기본 30사례 × 3회 재평가도 종료 0으로 완료했다. 이전 기준선 공통 103지표 동일·신규 B1 33지표이며, 초기 클라우드 기본 실행과 최종 실행의 전체 136지표 값이 동일하다. 추가 반복은 수행하지 않았다. 공개 원본·N1 샌드박스와 컨테이너는 후속 확인용으로 유지했고, 평가·테스트의 `dbrt_` 사본은 실행이 정리했다. Split 원본 서버·SP11~21 수동 실험·브라우저 UX·대량 M5는 이 검증 범위에 포함하지 않았다.
