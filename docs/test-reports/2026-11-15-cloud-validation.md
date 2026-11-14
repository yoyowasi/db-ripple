# 2026-11-15 클라우드 검증

기준은 원격 `codex/validate-home-tests`의 `42e13e8280643851b419687dcec04fd347b010a2`다. 새 클라우드 작업 폴더에 clone했고 원격 HEAD와 일치를 확인했다. 이전 로컬 실행 결과와 아래 새 실행을 구분한다. PR #1은 Draft로 유지하며 merge·배포하지 않는다.

## 수정과 독립 리뷰

- `CollectResult.ProcedureEntryDeletes`는 내부 실행 증거이므로 JSON에서 제외했다. 기존 정답 fixture는 변경하지 않았다. 수정 전 동일 직렬화 회귀 11/11 실패를 실제 재현했다.
- 내부 증거를 JSON으로 구성하던 신규 Attribution 테스트는 실제 pipeline처럼 메모리 객체로 구성했다. 검증 assertion은 유지했다.
- 독립 리뷰에서 DELETE 헤더에 INSERTED 이미지를 섞어도 직접 귀속 증거가 생기는 무결성 검증 누락을 발견했다. 회귀를 먼저 추가해 1실패/17통과를 확인하고, DELETE/INSERT 연산과 모순되는 이미지 count는 fallback하도록 보강했다. 리뷰어가 수정 diff를 재확인했다.
- 추가 독립 리뷰에서 헤더가 누락되고 이미지가 남은 부분 수집도 증거를 승인함을 확인했다. orphan 이미지 회귀의 수정 전 1실패/18통과를 재현한 뒤 모든 이미지의 헤더 연결을 검사하도록 보강했고, 리뷰어가 재확인했다.
- Linux N1의 실제 실행에서 `Start-Process -WindowStyle` 미지원 예외를 재현했다. Windows에서만 해당 옵션을 전달하도록 수정했으며 스크립트 회귀 23/23을 다시 확인했다.
- Linux 전체 실행에서 Windows 경로 구분자를 고정한 Split 테스트와 importer의 플랫폼별 파일명 허용 차이가 드러났다. 호스트 경로 기대값은 `Path.Combine`으로 구성하고 importer는 양쪽 구분자·점 디렉터리를 거부한다. Unix 상대 경로 회귀도 추가했다.
- 별도 리뷰어는 strict 직선형 계획, 활성 사용자 트리거·하위 FK 제외, 최초 raw 0행 헤더 소비, 특정 FiringId 한 번만 사용, 구형 manifest·버전·coverage·경계·depth·실패 fallback을 검토했다. 추가 미해결 지적은 없다. 중첩 팀은 사용하지 않았다.

## 실행 증거

.NET SDK 10.0.402, Linux, Node/npm 및 PowerShell 7.5.4에서 실행했다. 원본 로그/TRX는 ignored `logs/home-test/cloud/` 또는 `/tmp/db-ripple-*.log`에 보관하며 커밋하지 않는다.

| 명령/범위 | 새 클라우드 결과 |
|---|---|
| `dotnet build DbRipple.slnx --no-restore -m:1 -nologo` | 종료 0, 경고 0·오류 0 |
| `dotnet test DbRipple.slnx --no-restore -m:1 --logger trx` (DB 연결 없음) | 종료 0, TRX 합계 **1064 통과·0 실패·63 건너뜀** |
| Execution 전체 | 68 통과·17 DB 건너뜀; 기존 JSON fixture 11건 포함 |
| Attribution 전체 | 156 통과 |
| Core 전체 | 117 통과·6 DB 건너뜀 |
| `npm ci` 및 `npm run build` | 종료 0, 타입 검사·Vite build 통과 |
| `pwsh -NoProfile -File scripts/tests/home-test.Tests.ps1` | 23/23 통과 |

웹의 AG Grid 1,178.19 kB chunk 경고는 남아 있다. PowerShell 5.1은 Linux에서 실행하지 않았다. 전체 단위의 63 건너뜀은 실제 DB 검증을 대신하지 않는다.

## DB 검증 진행 상태

Docker 29.9.2는 응답했지만 7.9GB root overlay에서 SQL 이미지의 vfs 복제로 ENOSPC가 발생해 컨테이너 생성이 실패했다. 전체 NuGet 복원도 한 번 공간 부족으로 중단됐다. 작업에서 설치한 SDK를 여유 있는 메모리 파일시스템으로 옮기고 unused SQL 이미지를 제거해 공간을 확보한 뒤 위 전체 빌드·테스트는 완료했다. 공개 SQL 이미지 레이어를 이용한 작은 컨테이너 구성으로 SQL Server 2025 CU10 `17.0.5035.5`를 기동했다. 공식 AdventureWorks 백업을 복원하고 미리보기 사용자 최소 권한도 준비했다. **신규 실제 DB 4사례는 4/4 통과**했다. 전체 DB(SP7)/기본·EXEC 평가/N1은 진행 중이며 결과는 별도 DB 검증 보고서에 추가한다.

## 사용량 제한과 재개

사용자는 5시간 사용량 제한이 해제되면 계속 작업하도록 요청했다. 현재 도구 목록에는 클라우드 작업을 사용량 해제 이벤트에 자동 재개하는 전용 기능이 확인되지 않았다. 일반 예약 automation 도구의 존재만으로 이 런타임의 제한 해제 감지·동일 실행 재개를 보장할 수 없다. 확인되지 않은 자동 재개나 로컬 PC 타이머를 약속하지 않는다. 제한이 발생하면 코드·검증·미완료 항목을 같은 브랜치/PR에 보존한다. 현재 이 실행에서 사용량 제한 오류는 발생하지 않았다.
