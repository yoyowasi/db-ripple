# 2026-11-24 리디자인 r3 AdventureWorks 실DB 화면 검증

**DM1·DM2·DM3·DM7 모두 통과했다(4/4). A~H는 7개 통과, 1개 실패(A)다.** 미리보기 순변경과 적용 결과가 원본 DB 조회값과 일치했다. 짧은 미리보기의 잠금 표시가 누락됐고(리디자인 전부터 있던 문제), 카운트업 중에는 `-0`이 잠깐 표시됐다(리디자인으로 생긴 문제). 처음에는 행 수 막대 색 2건 때문에 E도 실패로 적었으나, 검증 요청서의 기대값이 코드의 의도와 달랐던 것이라 통과로 정정했다([정정](#결함이-아닌-것-정정)).

## 환경과 절차

- `main`의 `aba4409`(PR #36)를 검사했다. .NET SDK 10.0.404로 게시한 Release Host를 실행 파일 모드에서 `--no-browser`로 열고, Chromium 156.0.8078.79에서 일회용 주소로 접속했다. [11-23 검증 절차](2026-11-23-adventureworks-live-ui.md)
- 공식 SQL Server 2025 CU10 Enterprise Developer 이미지(`17.0.5035.5`)를 받았다. 최초 기동은 `AppLoader`의 LSA 오류 `0xc0070102`로 종료됐고, 같은 컨테이너를 재시작해 연결을 확인했다. 원본과 샌드박스는 메모리 한도 8GiB의 클라우드에서 이 인스턴스 한 대를 공유했다.
- 공식 AdventureWorks2025 백업(SHA-256 `fa6a2a5d431ad88123f89b36b1f2c7e42ca4bdf6b293269a44df80f6de3738a5`)을 `AW_Origin`으로 복원하고 저장소의 실행 사용자 준비 SQL을 적용했다. 화면의 [샌드박스 새로 고침]으로 첫 기준선을 만들었다. API는 `ready`, 지문 일치, 수집 71개, 제외 0개를 반환했다. [상태 상세](data/2026-11-24-redesign-r3-live-ui/a-ready.png)
- **정적** 검토에서 6개 화면 컴포넌트와 집계·상태 갱신 경로를 읽었다. **API**는 Chromium이 받은 미리보기·적용 응답과 별도로 읽은 샌드박스 상태로 확인했다. **브라우저**에서 문구·접근성 속성·그래프·콘솔을 확인했다. **DB**는 SQL Server에서 원본과 샌드박스를 각각 조회했다. [데모 SQL](../../scripts/demo/demo.http) · [API·브라우저 관측값](data/2026-11-24-redesign-r3-live-ui/api-ui.json)

## 시나리오별 결과

| 시나리오 | 화면과 API 결과 | DB 직접 조회 |
|---|---|---|
| **DM1 · 주문 43697 삭제 — 통과** | 큰 합계 4, ‘행 삭제 · 3개 테이블’. 확인 상자는 ‘원본에서 4행이 삭제됩니다.’와 ‘원본에서 4행 삭제’를 표시했다. API는 미리보기·실제 모두 삭제 4행, `committed`, `SNAPSHOT_NETDIFF`, 불일치 0건을 반환했다. [확인 상자](data/2026-11-24-redesign-r3-live-ui/dm1-confirm.png) · [적용](data/2026-11-24-redesign-r3-live-ui/dm1-apply.png) | 헤더 1행, 상세 1행, 사유 연결 2행은 미리보기 뒤 양쪽 DB에 남았다. 적용 뒤 원본에서 모두 삭제됐고 자동 기준선 갱신 뒤 샌드박스도 0행이었다. [조회 결과](data/2026-11-24-redesign-r3-live-ui/dm1-db.json) |
| **DM2 · 주문 상세 52번 수량 변경 — 통과** | 큰 합계 4, ‘행 변경 · 4개 테이블’, ‘수정 3 · 추가 1’. 확인 상자는 ‘원본에서 4행이 바뀝니다.’와 ‘원본에 4행 변경 적용’을 표시했다. API는 미리보기·실제 모두 수정 3행, 추가 1행, `committed`, `SNAPSHOT_NETDIFF`, 불일치 0건을 반환했다. [확인 상자](data/2026-11-24-redesign-r3-live-ui/dm2-confirm.png) · [적용](data/2026-11-24-redesign-r3-live-ui/dm2-apply.png) | 수량 `1 → 2`, 주문 소계 `419.4589 → 838.9178`, 개정번호 `10 → 11`을 확인했다. 고객 483의 `Demographics` 해시가 바뀌었고 거래 이력이 1행 늘었다. 미리보기 뒤 양쪽 조회값이 초기값과 같았고 자동 기준선 갱신 뒤 샌드박스가 원본과 일치했다. [조회 결과](data/2026-11-24-redesign-r3-live-ui/dm2-db.json) |
| **DM3 · 직원 1번 삭제 — 통과** | `dEmployee` 트리거가 삭제를 거부했다. API `trigger_blocked`, 오류 3609, 순변경 0행과 화면 표시가 일치했다. 적용 단추는 `aria-disabled="true"`였고 적용 요청은 0건, 미리보기 취소는 204였다. [거부 화면](data/2026-11-24-redesign-r3-live-ui/dm3-preview.png) | 양쪽 DB 모두 직원 1번이 남아 있었다. [조회 결과](data/2026-11-24-redesign-r3-live-ui/dm3-db.json) |
| **DM7 · 상품 680번 가격 드리프트 — 통과** | 가격 `+1.00` 미리보기 뒤 원본을 `+0.01` 했다. 적용은 `SNAPSHOT_NETDIFF`에서 `drift_detected`였다. 화면과 API 모두 차이 행 1건 `Production.Product`, `ProductID=680`, `ListPrice`를 표시했다. [미리보기](data/2026-11-24-redesign-r3-live-ui/dm7-preview.png) · [드리프트](data/2026-11-24-redesign-r3-live-ui/dm7-drift.png) | 원본 가격은 외부 변경 뒤 `1431.51`, 적용 시도 뒤에도 `1431.51`이었다. 스크립트 2부로 `1431.50`을 복원하고 상단 [샌드박스 새로 고침]을 눌렀다. API 기준 시각이 바뀌고 양쪽 가격이 `1431.50`으로 일치했다. [드리프트 SQL](../../scripts/demo/dm7-drift.sql) · [조회 결과](data/2026-11-24-redesign-r3-live-ui/dm7-db.json) · [새 기준선](data/2026-11-24-redesign-r3-live-ui/dm7-refreshed.png) |

## 리디자인 A~H 판정

| 항목 | 판정 | 실측 |
|---|---|---|
| **A · 상단 상태** | **실패** | 준비 상태의 초록 점, ‘· 잠금 없음’, 상세의 한 서버 모드·원본·기준 시각·지문·잠금 정보를 확인했다. 일반 실행에서 DM1·DM2 적용 중 잠금 표시가 보였다. DM1·DM2·DM3·DM7 미리보기와 DM7 적용 중에는 API의 `locked: true`를 화면이 놓쳤다. 보조 실행에서 SQL 잠금을 한 상태로 작업을 시작하니 미리보기·적용 중에 ‘잠금: 다른 작업 진행 중’, `aria-disabled="true`, ‘다른 미리보기·적용이 끝나면 켜집니다’ 제목이 나타났다. 종료 뒤 단추가 다시 켜졌다. [상태 상세](data/2026-11-24-redesign-r3-live-ui/a-ready.png) · [미리보기 잠금](data/2026-11-24-redesign-r3-live-ui/a-locked-preview.png) · [적용 잠금](data/2026-11-24-redesign-r3-live-ui/a-locked-apply.png) |
| **B · 전파 단계 칩** | **통과** | DM1은 `DELETE → 직접 1행 → FK 연쇄 3행 → 트리거 0행 쓰기 2건`, DM2는 `UPDATE → 직접 1행 → 트리거 5행`과 0행 쓰기 6건이었다. API에서 `firings[].rowCount`를 원인별로 더해 대조했다. DM2는 발동 6행, 순변경 4행이었다. |
| **C · 큰 합계** | **통과** | 카운트업 종료값 DM1·DM2·DM3·DM7의 `4·4·0·1`이 API `netSummary` 합과 일치했다. DM2의 `prefers-reduced-motion: reduce`에서는 첫 표시부터 4였고 중간값이 없었다. 일반 모드에서 관측된 `-0`은 아래에 따로 기록했다. [동작 줄이기](data/2026-11-24-redesign-r3-live-ui/dm2-reduced.png) |
| **D · 확인 상자** | **통과** | 빈 값과 `AW_Originn`에서 `aria-disabled="true"`였다. 오타 상태로 클릭과 Enter를 시도해도 적용 요청이 없었다. `aw_origin`과 `AW_Origin`에서 단추가 켜졌다. DM1·DM2의 문구 및 건수와 실제 적용 결과가 일치했다. |
| **E · 원인 트리 행 수 막대** | **통과**(정정) | DM1은 50%·50%·100%, DM2는 4개 모두 100%였다. 보조 미리보기의 44행·1행에서 렌더링 종료 뒤 100%(72px)·4%(2.875px/72px)였다. DM1·DM2의 단일 원인 색은 직접 빨강, FK 파랑, 트리거 황색이었다. 복수 원인 막대는 원인 칸 글자의 첫 항목 색을, ‘설명 안 됨’은 빨강 빗금을 썼다. 처음에는 요청서 기대값과 달라 실패로 적었으나 둘 다 코드의 의도와 같다([정정](#결함이-아닌-것-정정)). [최소 너비](data/2026-11-24-redesign-r3-live-ui/e-minimum.png) |
| **F · 그래프** | **통과** | 초기 등장 지연이 노드별로 달랐고 연출 종료 뒤 기본 노드 DM1 5개, DM2 11개가 모두 보였다. 0행 묶음을 켜면 API의 전체 노드 9개·17개가 각각 화면에 들어왔다. 확대 120%, 축소 69%에서 좁은 배치, 맞춤 100%를 확인했다. ‘읽는 법 ?’은 Esc와 바깥 클릭으로 각각 닫혔다. [DM1 범례](data/2026-11-24-redesign-r3-live-ui/dm1-graph.png) · [DM1 전체](data/2026-11-24-redesign-r3-live-ui/dm1-graph-all.png) · [DM2 범례](data/2026-11-24-redesign-r3-live-ui/dm2-graph.png) · [DM2 전체](data/2026-11-24-redesign-r3-live-ui/dm2-graph-all.png) |
| **G · 두 테마** | **통과** | 테마 단추로 바꿔 네 화면을 캡처했다. [DM1 밝게](data/2026-11-24-redesign-r3-live-ui/dm1-light.png) · [DM1 어둡게](data/2026-11-24-redesign-r3-live-ui/dm1-dark.png) · [DM2 밝게](data/2026-11-24-redesign-r3-live-ui/dm2-light.png) · [DM2 어둡게](data/2026-11-24-redesign-r3-live-ui/dm2-dark.png) |
| **H · 브라우저 오류** | **통과** | 첫 기준선 전 자동 분석 `POST /api/analyze` 409 1회와 이에 대응하는 Chromium 리소스 메시지 1건을 기록했다. 네 시나리오의 그 밖의 콘솔 오류, 페이지 예외, CSP 위반, 예상 밖 HTTP 오류는 각 0건이었다. |

## 발견한 결함

### 리디자인으로 생긴 결함

1. **일반 모드의 큰 합계에 `-0`이 잠깐 나온다.** 움직임을 허용한 Chromium에서 DM1·DM2·DM3 미리보기를 실행하면 첫 프레임들이 `0 → -0`을 거쳤다. 기대값은 0부터 최종값까지 음수 표시 없는 증가다. DM7 재현에서 관측한 DOM 프레임을 고정해 캡처했고 실제 최종값 1도 확인했다. 시작 시각보다 이른 프레임 타임스탬프에서 진행률이 음수가 될 수 있다. 계산상 합계가 크면(예: 5만 행) `-0`이 아니라 수천 단위 음수가 한 프레임 보일 수 있다(관측하지 않은 추정). [관측 프레임](data/2026-11-24-redesign-r3-live-ui/c-countup-observed-frame.png) · [ApplyPanel.vue:209](../../web/src/components/ApplyPanel.vue#L209)

### 결함이 아닌 것 (정정)

처음 보고에서 리디자인 결함으로 적었던 두 건이다. 관측값은 맞지만, 검증 요청서가 코드의 의도와 다른 기대값을 주어서 생긴 오판이라 2026-11-24에 정정했다.

1. **복수 원인 막대의 색.** DM2 SQL에 이어 `UPDATE Sales.SalesOrderHeader SET Comment = N'db-ripple r3 validation' WHERE SalesOrderID = 43663;`을 두 번째 문장으로 미리 보면 헤더 막대가 빨강 `#c4402e`이다. 요청서는 '첫 원인'을 문장 순서로 읽히게 적어 황색을 기대했다. 원인 칸 글자는 원인 종류 순서(직접 → FK → 트리거 → 설명 안 됨)로 `직접 · 트리거 1건`처럼 나오고, 막대는 그 글자의 첫 항목 색을 쓴다. 글자와 색이 맞으므로 의도대로다. [캡처](data/2026-11-24-redesign-r3-live-ui/e-first-cause.png) · [TableTree.vue:73](../../web/src/components/compare/TableTree.vue#L73) · [summary.ts:167](../../web/src/lib/summary.ts#L167)
2. **‘설명 안 됨’ 막대의 색.** 직접 막대와 같은 빨강(밝은 테마 `#c4402e`, 어두운 테마 `#ef7b69`)이지만 빗금 무늬로 구분된다. 요청서의 '별도 색' 기대는 CSS를 확인하지 않고 적은 것이었다. 다만 이 정정을 검토하다가 정적으로 결함 하나를 찾았다. 같은 테이블에 '설명 안 됨' 발동이 2건 이상이면 원인 칸 글자가 `설명 안 됨 2건`이 되어 [TableTree.vue:73](../../web/src/components/compare/TableTree.vue#L73)의 일치 비교에 걸리지 않고, 빗금 없이 직접과 같은 빨강 단색이 된다(이번 데이터에는 `UNEXPLAINED`가 없어 관측하지 못함). 이 비교 캡처는 같은 Chromium 페이지에 두 막대 스타일을 따로 렌더링한 것이다. [밝은 캡처](data/2026-11-24-redesign-r3-live-ui/e-bar-colors-light.png) · [어두운 캡처](data/2026-11-24-redesign-r3-live-ui/e-bar-colors-dark.png) · [TableTree.vue:243](../../web/src/components/compare/TableTree.vue#L243)

### 리디자인 전부터 있던 결함

1. **짧은 잠금이 상단바에 반영되지 않는다.** DM1·DM2·DM3·DM7을 미리 보면서 상태 API를 별도로 읽으면 잠금 `true`가 관측되지만 화면에는 잠금 표시가 없었다. 기대값은 실제 잠금 동안 경고가 붙고 새로 고침이 비활성화되는 것이다. 화면은 평상시 15초마다 상태를 읽고 작업 직후 다시 읽는다. 두 경로가 `1aa86a2`에도 있었다. 일반 미리보기에서 API로 관측한 잠금 구간은 약 0.27~3.66초였다. [API·브라우저 관측값](data/2026-11-24-redesign-r3-live-ui/api-ui.json) · [sandbox.ts:72](../../web/src/stores/sandbox.ts#L72) · [preview.ts:226](../../web/src/stores/preview.ts#L226) · [preview.ts:279](../../web/src/stores/preview.ts#L279)
2. **첫 기준선이 준비돼도 이전 분석 오류가 남는다.** 기준선 없이 화면을 열어 자동 분석 409를 받은 뒤 새로 고침한다. 기대값은 준비 상태가 된 뒤 ‘기준선이 없습니다’ 안내가 사라지는 것이다. 실제로는 상단이 준비됨인데 편집기 오류가 남았고, SQL을 바꿔 다시 분석하자 사라졌다. 리디자인 전 `1aa86a2` 웹 빌드에서도 같은 현상을 재현했다. [리디자인 전 캡처](data/2026-11-24-redesign-r3-live-ui/pre-r3-stale-analysis.png) · [r3 캡처](data/2026-11-24-redesign-r3-live-ui/a-ready.png) · [HomeView.vue:294](../../web/src/views/HomeView.vue#L294) · [preview.ts:99](../../web/src/stores/preview.ts#L99)

## 확인하지 못한 항목

- 공개 복원본의 네 시나리오와 보조 SQL에서 순변경 행의 원인이 `UNEXPLAINED`인 응답은 관측하지 못했다. 해당 막대의 색 선언과 Chromium 렌더링을 확인했고, 실제 API 응답에서 그 막대가 선택되는 경로는 미확인이다.

## 자동 테스트 범위

- 로컬 `npm run build`와 .NET Release Host 게시가 성공했다. 기본 .NET 테스트(`dotnet test DbRipple.slnx --no-build --no-restore -m:2`)는 1,460건 중 1,387건 통과, 실패 0건, DB 연결이 필요한 73건 건너뜀이다.
- `aba4409`의 GitHub Actions CI는 .NET, 웹 빌드, PowerShell 5.1, SQL Server, PostgreSQL 16·17·18까지 7개 잡이 모두 성공했다. [해당 커밋 CI](https://github.com/yoyowasi/db-ripple/actions/runs/45137177765)

## 재검증

같은 날 15:58 KST에 새 SQL Server 2025 CU10 인스턴스에서 같은 공식 백업을 복원하고 첫 기준선을 생성했다. `aba4409`의 웹을 다시 빌드해 Release Host 실행 파일 모드와 Chromium으로 실행했다. 기준선은 지문 일치와 71개 수집 테이블을 반환했다. DM7의 첫 자동 확인에서는 새로 고침 직전에 받은 상태를 읽었다. 기준 시각이 바뀐 뒤 조회하도록 대기를 조정해 DM7을 다시 실행했다.

- **시나리오 4개 통과.** DM1은 3개 테이블에서 삭제 4행, DM2는 수정 3행·추가 1행이었다. DM3은 `trigger_blocked`·3609·순변경 0행과 직원 유지, DM7은 `drift_detected`와 `ProductID=680`·`ListPrice`를 재확인했다. 상단에서 기준선을 갱신한 뒤 양쪽의 가격은 `1431.50`이었다. [DM1 DB](data/2026-11-24-redesign-r3-live-ui/rerun/dm1-db.json) · [DM2 DB](data/2026-11-24-redesign-r3-live-ui/rerun/dm2-db.json) · [DM3 DB](data/2026-11-24-redesign-r3-live-ui/rerun/dm3-db.json) · [DM7 DB](data/2026-11-24-redesign-r3-live-ui/rerun/dm7-db.json)
- **A~H 6개 통과, 2개 실패(A·E)로 판정했다. E는 위 정정에 따라 통과로 본다(7개 통과, 1개 실패).** 일반 실행의 상단 잠금 표시는 미리보기 중 네 시나리오 모두에서 보이지 않았다. 적용 중 DM1·DM2에서 보였다. 별도 SQL 트랜잭션으로 잠금을 유지한 보조 실행에서는 화면의 잠금 문구, 새로 고침의 비활성화와 제목, 잠금 해제 뒤 활성화를 다시 확인했다. 리디자인 전 같은 보조 미리보기는 API에서 잠금이 약 768ms 관측됐고 상단 표시가 나오지 않았다. [미리보기 잠금](data/2026-11-24-redesign-r3-live-ui/rerun/a-locked-preview.png) · [적용 잠금](data/2026-11-24-redesign-r3-live-ui/rerun/a-locked-apply.png)
- **색상·카운트업 재현.** 복수 원인 막대의 빨강, 두 테마에서 ‘설명 안 됨’ 줄무늬의 빨강(둘 다 의도대로, 위 정정 참고), DM1·DM2·DM3의 화면 숫자 `-0`(결함)을 재확인했다. 100%·4% 너비와 최종 합계 및 동작 줄이기는 다시 통과했다. [첫 원인 색](data/2026-11-24-redesign-r3-live-ui/rerun/e-first-cause.png) · [밝은 색 비교](data/2026-11-24-redesign-r3-live-ui/rerun/e-bar-colors-light.png) · [어두운 색 비교](data/2026-11-24-redesign-r3-live-ui/rerun/e-bar-colors-dark.png) · [관측 프레임](data/2026-11-24-redesign-r3-live-ui/rerun/c-countup-observed-frame.png) · [DM1 밝게](data/2026-11-24-redesign-r3-live-ui/rerun/dm1-light.png) · [DM1 어둡게](data/2026-11-24-redesign-r3-live-ui/rerun/dm1-dark.png) · [DM2 밝게](data/2026-11-24-redesign-r3-live-ui/rerun/dm2-light.png) · [DM2 어둡게](data/2026-11-24-redesign-r3-live-ui/rerun/dm2-dark.png)
- **오류 및 미확인.** 최초 분석 409 1회와 대응하는 Chromium 리소스 메시지를 제외하고 콘솔 오류·페이지 예외·CSP 위반·예상 밖 HTTP 오류는 0건이었다. 첫 기준선이 준비된 뒤에도 남는 분석 오류는 리디자인 전 빌드에서 다시 재현했다. 실제 복원본 시나리오에서 `UNEXPLAINED` 응답이 없어서 해당 막대의 API 연동은 미확인이다. [리디자인 전 캡처](data/2026-11-24-redesign-r3-live-ui/rerun/pre-r3-stale-analysis.png) · [API·브라우저 관측값](data/2026-11-24-redesign-r3-live-ui/rerun/api-ui.json)
- **자동 테스트.** 로컬 웹 빌드와 Release Host 게시 성공. .NET 기본 테스트 1,387건 통과, 실패 0건, DB 연결 변수가 필요한 73건 건너뜀.
