# 2026-11-23 AdventureWorks 원인 트리 화면 실DB 검증

**DM1~DM3과 DM7이 모두 통과했다(4/4).** Chromium에서 미리보기와 적용을 조작하고, 원본·샌드박스 DB를 SQL로 직접 조회해 결과를 대조했다.

## 환경과 절차

- 대상은 `main`의 `1aa86a2`다. .NET 10 Release Host가 `npm run build`로 만든 웹을 제공했고, Chromium 156에서 실행 파일 모드의 일회용 주소로 열었다.
- 격리된 SQL Server 2025 CU10 컨테이너(`17.0.5035.5`)에 공식 AdventureWorks2025 백업을 `AW_Origin`으로 복원했다. 원본과 샌드박스는 한 인스턴스를 썼다. 화면에서 기준선을 생성한 뒤 `ready`, 환경 지문 일치, 감사 대상 71개 테이블을 확인했다.
- [데모 대본](../../scripts/demo/demo.http)의 SQL을 브라우저 편집기에 입력했다. 적용할 때는 확인 상자에서 정확한 원본 DB 이름 `AW_Origin`을 입력해야 확정 버튼이 켜지는 것을 확인했다.

## 시나리오별 결과

| 시나리오 | 화면과 API 결과 | DB 직접 조회 |
|---|---|---|
| **DM1 · 주문 43697 삭제** | 원인 트리에 FK 연쇄 2개와 `iduSalesOrderDetail`이 표시됐다. 적용 결과는 `committed`였고, 미리보기와 실제 결과 모두 3개 테이블에서 4행 삭제였다. [적용 화면](data/2026-11-23-adventureworks-live-ui/dm1-apply.png) | 주문 헤더 1행·상세 1행·사유 연결 2행은 미리보기 후 남아 있었고, 적용 후 모두 0행이었다. [조회 결과](data/2026-11-23-adventureworks-live-ui/dm1-db.json) |
| **DM2 · 주문 상세 52번 수량 변경** | 원인 트리와 행 비교에서 트리거 전파와 전후 값을 확인했다. 주문 헤더의 깊이 3 재갱신과 SalesPerson·SalesTerritory의 0행 쓰기가 관측됐다. 적용 결과는 `committed`였고, 미리보기와 실제 결과 모두 수정 3행·추가 1행이었다. [미리보기 화면](data/2026-11-23-adventureworks-live-ui/dm2-preview.png) | 수량 `1 → 2`, 주문 소계 `419.4589 → 838.9178`, 개정번호 `10 → 11`을 확인했다. 고객의 `Demographics`가 바뀌었고 해당 거래 이력이 1행 추가됐다. [조회 결과](data/2026-11-23-adventureworks-live-ui/dm2-db.json) |
| **DM3 · 직원 1번 삭제** | `dEmployee` 트리거가 삭제를 거부했다. `trigger_blocked`와 SQL Server 오류 `3609`, 순변경 0건이 표시됐다. 적용 버튼이 비활성화됐고, 미리보기 취소는 HTTP 204였다. [거부 화면](data/2026-11-23-adventureworks-live-ui/dm3-preview.png) | 원본과 샌드박스 모두 직원 1번이 남아 있었다. [조회 결과](data/2026-11-23-adventureworks-live-ui/dm3-db.json) |
| **DM7 · 상품 680번 가격 드리프트** | 가격 `+1.00`을 미리 본 뒤 [드리프트 스크립트](../../scripts/demo/dm7-drift.sql)로 원본 가격을 `+0.01` 했다. 화면에서 적용을 확정하자 `drift_detected`가 표시됐고, 차이가 난 행과 열은 `Production.Product`의 `ProductID=680`, `ListPrice`였다. [드리프트 화면](data/2026-11-23-adventureworks-live-ui/dm7-drift.png) | 가격은 `1431.50 → 1431.51`로 바뀌었고, 적용을 시도한 뒤에도 `1431.51`이었다. `1431.50`으로 복구하고 화면에서 다시 기준선을 만든 뒤 원본·샌드박스 가격 일치를 확인했다. [조회 결과](data/2026-11-23-adventureworks-live-ui/dm7-db.json) |

DM1과 DM2는 `SNAPSHOT_NETDIFF`로 건수 일치를 확인한 뒤 커밋됐고, 자동 기준선 갱신 뒤 샌드박스의 조회값도 원본과 일치했다. DM7도 `SNAPSHOT_NETDIFF` 경로에서 원본 드리프트 검사로 중단됐다.

첫 기준선 생성 전에 자동 분석 요청이 HTTP 409를 한 번 반환했다. 기준선을 생성한 뒤 테스트한 동작에는 예상 밖의 HTTP 오류, 페이지 예외, CSP 위반이 없었다. 네 시나리오 판정이 끝난 뒤 Playwright가 브라우저를 닫는 과정에서 `Target closed` 경고를 출력했다. 실행 종료 코드는 0이었다.

## 자동 테스트 범위

- 로컬 기본 .NET 테스트(`dotnet test DbRipple.slnx --no-build --no-restore -m:2`)는 1,460건 중 1,387건이 통과했고 실패는 0건이었다. DB 연결 변수가 없어 SQL Server·PostgreSQL 통합 테스트 73건이 건너뛰어졌다.
- `1aa86a2`의 GitHub Actions CI는 .NET, 웹 빌드, PowerShell 5.1, SQL Server 및 PostgreSQL 16·17·18의 잡 7개가 모두 성공했다. [해당 커밋 CI](https://github.com/yoyowasi/db-ripple/actions/runs/44484031553)
