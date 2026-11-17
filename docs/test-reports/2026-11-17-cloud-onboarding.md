# 2026-11-17 클라우드 환경 검증 (DB 연결 전)

대상: main `87df6298242839d3838cca7d0032152ee2ed3097`. Linux x64 클라우드에서 실행했다. **DB 검증과 PR 생성은 네트워크 정책 때문에 보류 중이다.** 기존 Windows/DB 성능 결과를 이 환경에서 재현했다고 주장하지 않는다.

## 환경과 검증 결과

- .NET SDK 10.0.404 / 런타임 10.0.15, PowerShell 7.5.5, Node 24.22.1, Docker 29.9.2.
- SDK는 Microsoft 릴리스 메타데이터의 SHA-512, PowerShell 패키지는 Microsoft 패키지 목록의 SHA-256으로 검증했다. 설치 위치는 체크아웃 밖 `/workspace/.tools`, 캐시는 `/workspace/.cache`다.
- 읽기 전용 홈 디렉터리 때문에 npm/PowerShell 기본 캐시 경로가 실패했다. 캐시와 XDG 경로를 `/workspace`로 지정한 뒤 성공했다.
- .NET의 `NuGet-Migrations` 시스템 뮤텍스 생성은 명령 샌드박스에서 실패했다. 동일 명령을 승인된 `require_escalated` 실행으로 복원·빌드·테스트했다. 저장소 코드나 보안 검증을 바꾸지 않았다.

| 검사 | 결과 | 증거 범위 |
|---|---|---|
| `dotnet restore DbRipple.slnx` | 통과 | 의존성 복원 |
| `dotnet build DbRipple.slnx --no-restore -warnaserror -m:4` | 경고 0, 오류 0 | 전체 솔루션 빌드 |
| `dotnet test DbRipple.slnx --no-build -m:4 --logger trx` | 전체 1,278개: 통과 1,212, 건너뜀 66, 실패 0 | DB 연결 변수 없는 테스트; TRX의 실제 결과 집계 |
| `pwsh -NoProfile -File scripts/tests/sp19.Tests.ps1` | PASS 6건 | DB 없는 스크립트 회귀 |
| `pwsh -NoProfile -File scripts/tests/sp19-precheck.Tests.ps1` | 16/16 PASS | 네이티브 경계 모사; 실제 SQL 아님 |
| `cd web; npm ci; npm run build` | 통과 | 타입 검사와 Vite 프로덕션 빌드; 기존 chunk 크기 경고 있음 |
| API 개발 서버 | `/api/settings` HTTP 200, `configured=false` | 기동·초기 설정 응답 |
| Vite 개발 서버 | HTML 진입점 확인, `/api/settings` 프록시 HTTP 200 | 웹 기동·API 프록시; 브라우저 상호작용 아님 |
| `/api/sandbox` | HTTP 503 | 연결 미설정 상태의 응답 확인; DB 성공 아님 |
| AdventureWorks 다운로드 | 50,229,248 bytes, 문서 SHA-256과 일치 | 공식 샘플 백업 무결성 |
| 재사용 설치 스크립트 재실행 | 통과 | 체크섬 재검증·복원·빌드·npm ci·웹 빌드 반복 가능 |

기본 테스트의 `LargeDeletePerfTests`는 `DBR_PERF`가 없으면 조기 반환하므로 위 통과 개수를 성능 측정 증거로 쓰지 않는다. 아래에서는 별도 활성화했다.

## 큰 DELETE resolve 합성 재측정

DB 없는 `CausalResolver.Resolve`만 측정했다. Release, 크기별 워밍업 1회 + 측정 3회, `DBR_PERF=1`, `DBR_PERF_SIZES=10000,50000,100000`, `DBR_PERF_REPEAT=3`.

```bash
source /workspace/.setup/env.sh
DBR_PERF=1 DBR_PERF_SIZES=10000,50000,100000 DBR_PERF_REPEAT=3 \
  dotnet test tests/DbRipple.Attribution.Tests -c Release \
  --filter FullyQualifiedName~LargeDeletePerfTests --logger 'console;verbosity=detailed'
```

| 부모 행 수 | 변경 행 수 | 표본(ms) | 중앙값(ms) |
|---|---|---|---|
| 10,000 | 50,000 | 368 / 202 / 192 | 202 |
| 50,000 | 250,000 | 1,013 / 875 / 960 | 960 |
| 100,000 | 500,000 | 1,878 / 2,061 / 1,909 | 1,909 |

측정 테스트 1개 통과. 5만→10만 중앙값은 약 1.99배다. 다른 머신의 변경 전 값과 직접적인 개선 배수를 비교하지 않는다. C11 Collect, SQL 실행·재조회, M5 전체 시간은 이 측정에 포함되지 않는다.

## 남은 작업과 재현 설정

1. SQL Server 2025 CU10 이미지 manifest는 조회됐지만 blob 요청이 `centralus.data.mcr.microsoft.com`으로 리디렉션되며 프록시에서 Forbidden이 발생했다. 컨테이너를 시작하지 못해 DB 테스트·기본/EXEC 평가·M5 DB 측정은 미실행이다.
2. Git HTTPS 읽기(`git ls-remote origin HEAD`)는 정상이다. GitHub API는 `api.github.com` CONNECT에서 403이므로 PR 생성 여부와 API 인증은 검증하지 못했다. CLI의 토큰 진단만으로 자격 증명 부재라고 판정하지 않는다.
3. 두 도메인을 기존 패키지 관리자 프리셋을 보존한 환경 설정 초안에 추가했다. `install_script`와 `start_skill`도 저장했다. 초안 저장은 현재 네트워크 적용이나 환경 게시가 아니다. 환경 설정에서 저장 후 연결을 재확인하고, DB 검증이 끝난 뒤 환경을 게시해야 한다.
4. 네트워크 적용 후 `.github/workflows/ci.yml`의 임시 로컬 SQL 자격 증명 절차와 `docs/home-test-plan.md`대로 DB를 준비하고 `home-test.ps1`, `-IncludeExec`, ApplyIntegration을 실행한다. 기존 `.env`가 있으면 보존한다. 공개 샘플 외 원본에 연결하지 않는다.
5. `2026-11-17-large-delete-perf.md` §7의 M5(워밍업 1회+5회)와 CascadeLab(preview/B0/B2 20회), differences·지표 검증은 별도로 필요하다. Windows 실행 파일·PowerShell 5.1·Split·브라우저도 이번 실행에서는 검증하지 않았다.

원본 로그는 현재 환경 `/workspace/.setup/`의 `build.log`, `tests.log`, `trx/`, `resolve-perf.log`, `sp19-tests.log`, `sp19-precheck-tests.log`, `web-build.log`, `install-verify.log`, `host.log`, `vite.log`에 있다. 백업과 생성 출력·캐시는 커밋하지 않는다. 애플리케이션 소스·테스트·의존성 선언·잠금 파일은 변경하지 않았다.
