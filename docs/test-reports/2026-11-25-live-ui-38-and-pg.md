# 2026-11-25 PR #38 및 PostgreSQL 실DB 화면 검증

**SQL Server에서 #38의 A1~A3을 확인했고, A4를 재현할 AdventureWorks 데이터는 없었다. PostgreSQL B1~B4는 홈으로 돌아온 뒤 모두 통과했다.** PostgreSQL 첫 접속이 SQL Server 설정 화면으로 이동하는 문제를 새로 관측했다.

## 환경과 절차

- 대상은 `main`의 `6440bb3c028344f176bd3ba17c63b292a3bf5d14`다. .NET SDK 10.0.404로 게시한 Release Host를 실행 파일 모드에서 `--no-browser`로 띄웠다. Chromium 156.0.8078.79(1440×960, 움직임 허용, 한국 시각)으로 일회용 주소에서 접속했다. 두 엔진은 메모리 한도 8 GiB에서 순차 실행했다. [직전 화면 검증 절차](2026-11-24-redesign-r3-live-ui.md)
- SQL Server 2025 CU10 Enterprise Developer(17.0.5035.5)를 최초 기동했다. 공식 AdventureWorks2025 백업을 `AW_Origin`으로 복원하고 실행 사용자를 준비했다. 백업 SHA-256은 11-24와 같았다. 준비 스크립트에서 임시 복사 SQL 읽기 권한 문제가 한 번 발생해 준비 단계만 기본 `umask 022`로 재실행했다. 화면에서 만든 첫 기준선은 지문 일치, 수집 71개, 제외 0개였다. [상태 화면](data/2026-11-25-live-ui-38-and-pg/sql-first-ready.png)
- PostgreSQL 18.7(Debian 18.7-1.pgdg13+5)은 SQL Server를 멈춘 뒤 실행했다. 비슈퍼유저 관리 역할에 LOGIN·CREATEDB·CREATEROLE을 부여하고 그 역할이 소유한 `shop`에 Sp22 상수를 넣었다. 열린 원본 연결은 0개였다. 화면에서 만든 첫 기준선은 지문 일치, 수집 7개, 제외 0개였다. [PG 설정과 API 기록](data/2026-11-25-live-ui-38-and-pg/pg-observations.json)
- **정적**으로 #38 변경 파일과 PostgreSQL 분석·행 비교·적용 제한·오류 경로를 읽었다. **API**에서는 브라우저의 요청·응답과 상태를 기록했다. **브라우저**에서는 실제 동작, 문구, 접근성 속성, 프레임, 두 테마를 관측했다. **DB**에서는 원본과 샌드박스의 행을 별도 SQL로 대조했다.

## A. PR #38 SQL Server 화면

| 항목 | 판정 | 실측 |
|---|---|---|
| A1 · 기준선 뒤 분석 | **통과** | 최초 자동 분석은 409 `NO_BASELINE`이었다. 편집기를 그대로 두고 상단에서 새로 고치자 성공한 분석이 오류를 지웠다. DM1 적용 후 자동 갱신에서도 같은 DELETE를 다시 분석했다. [최초 오류](data/2026-11-25-live-ui-38-and-pg/sql-first-analysis-error.png) · [갱신 코드](../../web/src/stores/preview.ts#L137) |
| A2 · 내 작업 및 다른 작업 | **통과** | DM1·DM2 미리보기 중에는 `· 미리보기 실행 중`, DM1 적용 중에는 `· 적용 중`이었다. 잠금 상세는 `이 창의 …`, 새로 고침은 `aria-disabled="true"`와 `실행 중인 미리보기·적용이 끝나면 켜집니다`였다. 해당 관측에 다른 작업 알림은 없었다. 완료 후 `· 잠금 없음`과 활성 버튼으로 돌아왔다. 별도 SQL 트랜잭션으로 샌드박스 행을 잠가 두 번째 창의 미리보기를 대기시키자 첫 창에 `잠금: 다른 작업 진행 중`과 제목 `다른 미리보기·적용이 끝나면 켜집니다`가 표시됐다. [내 미리보기](data/2026-11-25-live-ui-38-and-pg/sql-own-preview.png) · [다른 작업 및 DM7](data/2026-11-25-live-ui-38-and-pg/sql-other-window-lock.png) · [상태 코드](../../web/src/components/SandboxBar.vue#L34) |
| A3 · 큰 합계 카운트업 | **통과** | DM1·DM2·DM3에서 표시된 값을 프레임마다 수집했다(고유 프레임 시각 66·68·47개). `-`로 시작한 값은 각각 0개였고 최종값은 4·4·0이었다. [프레임 기록](data/2026-11-25-live-ui-38-and-pg/sql-observations.json) · [카운트업 코드](../../web/src/components/ApplyPanel.vue#L212) |
| A4 · 설명 안 됨 막대 | **미확인(재현 데이터 없음)** | AdventureWorks 시나리오에서 순변경이 있는 `UNEXPLAINED`가 나오지 않았다. 정적 검토에서 `설명 안 됨`으로 시작하는 원인 글자를 빗금 스타일로 분류하는 경로를 확인했다. [분류 코드](../../web/src/components/compare/TableTree.vue#L74) |

A1에서 Chromium이 상태 API 응답을 받은 시각과 분석 요청을 보낸 시각을 대조했다. 단위는 KST다.

| 구간 | 관측 |
|---|---|
| 첫 기준선 전 | 16:32:17.683 분석 요청 → 16:32:17.912 HTTP 409 |
| 첫 기준선 생성 후 | 16:32:32.544 `ready` 수신 → 16:32:32.549 같은 SQL 분석 요청 → 16:32:32.589 HTTP 200 → 16:32:32.593 오류 문구 지워짐 |
| DM1 적용으로 자동 갱신 | 16:32:47.547 새 기준선 `ready` 수신 → 16:32:47.552 같은 DELETE 분석 요청 → 16:32:47.554 HTTP 200 |

[SQL Server API·브라우저 관측값](data/2026-11-25-live-ui-38-and-pg/sql-observations.json)

| 시나리오 | 판정 | 화면·API·DB 대조 |
|---|---|---|
| DM1 · 주문 43697 삭제 | **통과** | 3개 테이블 삭제 4행, `committed`, `SNAPSHOT_NETDIFF` 일치. 미리보기 뒤 헤더·상세·사유 연결 1·1·2행 유지, 적용·갱신 뒤 양쪽 0행. |
| DM2 · 주문 상세 52번 | **통과** | 수정 3행·추가 1행, `committed`, `SNAPSHOT_NETDIFF` 일치. 수량 1 → 2, 소계 419.4589 → 838.9178, 개정번호 10 → 11, 고객 해시 변경과 거래 이력 1행 증가 확인. 갱신 뒤 양쪽 값 일치. |
| DM3 · 직원 1번 삭제 | **통과** | `trigger_blocked`, SQL Server 오류 3609, 순변경 0행. 적용 비활성, 취소 204. 양쪽 DB에 직원 유지. |
| DM7 · 상품 680 가격 변경 | **통과** | 원본 가격을 1431.50 → 1431.51로 바꾼 뒤 적용하니 `drift_detected`와 `ProductID=680`의 `ListPrice` 차이가 표시됐다. 적용 시도 후 1431.51 유지, 외부 변경 복구 및 갱신 뒤 양쪽 1431.50. |

[SQL Server 직접 조회](data/2026-11-25-live-ui-38-and-pg/sql-database.json)

## B. PostgreSQL 화면

| 항목 | 판정 | 실측 |
|---|---|---|
| B1 · (a) 부모 1 삭제 | **통과** | parent 직접, child·grandchild FK 연쇄, note FK SET NULL을 모두 `확실`로 표시했다. 삭제 1·2·3행, note 수정 2행과 FK 부모 링크가 API·화면에서 일치했다. [밝게](data/2026-11-25-live-ui-38-and-pg/pg-a-light.png) · [어둡게](data/2026-11-25-live-ui-38-and-pg/pg-a-dark.png) |
| B1 · (b) 부모 2 삭제 | **통과** | parent·child 각각 삭제 1행, note 수정 1행. note는 FK SET NULL `확실`이고 행 비교는 `parent_id` 2 → `NULL`이었다. grandchild의 별도 0행 발동은 순변경 집계에 들지 않았다. [행 비교](data/2026-11-25-live-ui-38-and-pg/pg-b-note-null.png) · [NULL 표기](../../web/src/components/compare/RowCompare.vue#L81) |
| B1 · (c) 주문 상세 70 수정 | **통과** | order_lines 직접, orders·history 트리거 `추론`이었다. orders는 같은 행에 두 번 발동했고 순변경은 1행이었다. 최종 결과는 order_lines·orders 수정 1행씩, history 추가 1행이었다. 화면 비교에서 total 20.00 → 30.00, rev 0 → 1을 확인했다. 11-18의 rev 1 → 2와 시작값이 다른 이유는 이번 Sp22 상수가 시드 뒤 트리거를 만들기 때문이다. [밝게](data/2026-11-25-live-ui-38-and-pg/pg-c-light.png) · [어둡게](data/2026-11-25-live-ui-38-and-pg/pg-c-dark.png) · [Sp22 시드](../../tests/DbRipple.Postgres.Tests/PgIntegrationTests.cs#L17) |
| B1 · (d) 두 거부 | **통과** | `TRUNCATE`와 `set_config()` 미리보기가 각각 422였다. 거부 칸에 `REJECT_TRUNCATE`·`REJECT_ADMIN_COMMAND`와 구체적인 거부 사유가 표시됐다. [TRUNCATE](data/2026-11-25-live-ui-38-and-pg/pg-reject-truncate.png) · [함수 거부](data/2026-11-25-live-ui-38-and-pg/pg-reject-admin.png) · [규칙 표시](../../web/src/components/DiagnosticList.vue#L44) |
| B2 · 적용 제한·지문·상태·합계 | **통과** | (a)~(c)의 [원본에 적용]은 비활성이었고 이유는 “PostgreSQL 최소판은 [적용]을 지원하지 않습니다. 미리보기만 됩니다.” 하나였다. (c)의 적용 API 별도 호출은 501이었다. 지문 버전·정렬 규칙은 첫 미리보기 전에는 비어 있었고 첫 미리보기 뒤 `PostgreSQL 18.7 (Debian 18.7-1.pgdg13+5) · en_US.utf8`로 표시됐다. 세 미리보기 모두 내 작업 표시와 비활성 버튼, 끝난 뒤 잠금 해제를 확인했다. 큰 합계 최종값 8·3·3, 프레임 시각 70·65·65개, 음수 표기 0개였다. [관측값](data/2026-11-25-live-ui-38-and-pg/pg-observations.json) · [적용 제한 코드](../../web/src/lib/applyGate.ts#L51) |
| B3 · 원본 접속 중 새로 고침 | **통과** | psql 연결 1개를 유지한 상태에서 새로 고침하니 상태가 오류로 바뀌고 실패 이유에 원본 연결을 닫은 뒤 TEMPLATE 복제를 다시 시도하라는 안내가 나왔다. 55006은 Host에서 안내 문구로 변환한다. 연결 수가 0이 된 뒤 새로 고치자 새 기준 시각과 함께 준비됨으로 돌아왔다. [실패 이유 화면](data/2026-11-25-live-ui-38-and-pg/pg-origin-in-use.png) · [오류 변환 코드](../../src/DbRipple.Postgres/PgSandboxManager.cs#L144) |
| B4 · 두 테마·브라우저 오류 | **통과** | (a)·(c) 각각 밝은·어두운 화면을 캡처했다. PostgreSQL에서 Chromium 리소스 오류 5건은 의도한 409 2건(첫 방문과 홈 복귀 분석), 422 2건, 501 1건에 대응했다. 그 밖의 콘솔 오류·페이지 예외·CSP 위반·예상 밖 HTTP 오류는 각각 0건이었다. SQL Server는 최초 409와 대응 리소스 오류 각 1건 외에 같은 항목이 각각 0건이었다. |

(a)~(c) 취소는 모두 204였다. 각 미리보기와 두 거부 후에 `shop`과 샌드박스의 7개 업무 테이블을 id 순으로 조회했다. 값이 최초 조회와 일치했고 샌드박스 감사 헤더·행 이미지는 0행이었다. B3에서 기준선을 다시 만든 뒤에도 업무 행이 일치했다. [PostgreSQL 직접 조회](data/2026-11-25-live-ui-38-and-pg/pg-database.json) · [11-18 API 기준](2026-11-18-pg-minimal-cloud.md#host-api-미리보기)

## 결함과 확인 범위

- **#38에서 고친 동작.** 11-24에 남았던 기준선 전 분석 오류와 짧은 미리보기의 상단 잠금 표시, 큰 합계의 `-0`을 A1~A3에서 다시 확인했다. 리디자인 전부터 있던 문제는 앞의 두 건이다. A4는 실제 순변경 데이터가 없어 코드 경로만 확인했다.
- **이번에 드러난 PostgreSQL 첫 실행 화면 문제.** 환경 변수로 PostgreSQL을 지정하고 SQL Server 설정이 없는 상태에서 게시 Host의 실행 주소를 연다. 기대값은 미리보기 홈에서 PostgreSQL 기준선을 만드는 것이다. 실제로는 PostgreSQL 상태 API가 동작해도 `/api/settings`의 `configured: false`에 따라 SQL Server 설정으로 자동 이동한다. 상단 로고를 눌러 홈으로 돌아가면 PostgreSQL 기준선을 만들 수 있었다. 설정 서비스는 SQL Server 환경 변수와 저장 프로필을 기준으로 응답하며, 화면은 DB 엔진을 확인하지 않고 이 값으로 이동한다. 두 경로는 #38 전 커밋에도 있었다. [첫 실행 캡처](data/2026-11-25-live-ui-38-and-pg/pg-first-launch-settings.png) · [홈 이동 코드](../../web/src/App.vue#L16) · [설정 서비스](../../src/DbRipple.Preview/Settings/ProfileSettingsService.cs#L100) · [PG 엔진 기동](../../src/DbRipple.Host/Program.cs#L135)

## 자동 테스트 범위

- 로컬 `npm run build`의 타입 검사와 운영 빌드, .NET Release Host 게시가 성공했다. 로컬 `dotnet test DbRipple.slnx -c Release -m:2`는 1,460건 중 1,387건 통과, 실패 0건, 73건 건너뜀이었다(DB 연결 변수 없이 실행).
- `6440bb3`의 GitHub Actions CI에서 .NET, 웹, PowerShell 5.1, SQL Server 2025, PostgreSQL 16·17·18의 7개 잡이 모두 성공했다. [해당 커밋 CI](https://github.com/yoyowasi/db-ripple/actions/runs/45564010877)
