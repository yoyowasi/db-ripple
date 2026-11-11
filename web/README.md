# db-ripple 웹 화면

Vue 3 + Vite + TypeScript + Pinia. 설계: [docs/architecture.md](../docs/architecture.md) §4 C1~C4, §6.3, D6. API 모양: [docs/week3-plan.md](../docs/week3-plan.md) §5.

> 3주차 1라운드에서 코드만 썼다. 설치·타입 검사·빌드·실행은 아직 한 번도 하지 않았다.

## 설치와 실행

API 서버(`src/DbRipple.Host`, `http://127.0.0.1:5080`)를 먼저 켠다([루트 README](../README.md) 실행 방법). 그다음:

```powershell
cd web
npm install
npx vue-tsc --noEmit      # 타입 검사
npx vite build            # 빌드(dist/)
npm run dev               # 개발 서버 http://localhost:5173
```

- 브라우저는 같은 출처의 `/api`만 부른다. Vite 개발 서버가 `/api`를 `http://127.0.0.1:5080`으로 넘기므로 CORS 설정이 없다(`vite.config.ts`).
- `/sp9`는 개발 서버에서만 열린다(운영 빌드에서 열려면 `VITE_ENABLE_SP9=true`).
- `package.json`의 버전은 모두 고정이다. 설치 때 존재하지 않는 버전이라는 오류가 나면 `npm view <패키지> versions`로 확인해 가까운 패치로 바꾼다.

## 화면 구성(`/`)

| 영역 | 컴포넌트 | 내용 |
|---|---|---|
| 위쪽 띠 | `SandboxBar` | 샌드박스 상태(building·ready·dirty·error), 기준 시각, 환경 지문, 잠금, [샌드박스 새로 고침] |
| C1 편집기 | `SqlEditor` | CodeMirror 6 MSSQL 방언. `/api/catalog/schema`로 스키마·열 자동완성(기준선 `baselineAt`별 캐시). 입력을 멈추면 `/api/analyze`로 분석해 거부·경고 위치를 줄 표시로 보인다. Ctrl+Enter = 미리보기 |
| C2 그래프 | `PropagationGraph`, `FlowNode`, `RippleEdge` | 노드 6종·간선 7종, dagre 왼쪽 → 오른쪽 배치. 선 모양 = 확신도, 색·라벨 = 종류, 흐린 점선·점선 테두리 = `observed:false`, 라벨 "관측 순서, 보장 안 됨". `0행`·`일부만 수집`·`감사 순서 불확실` 표시. 0행 묶음은 기본 숨김(E23) |
| C3 그리드 | `DiffGrid` | AG Grid. 발동 보기(event)·최종 차이 보기(net) 전환(E24), 묶음 선택 목록, 서버 페이지(offset·limit). 바뀐 셀 강조("이전 → 이후"), "예상값"·"파생" 뱃지, 키 없는 테이블은 "제거된 행 n개, 추가된 행 m개"(E6) |
| C4 요약 | `PreviewSummary` | 문장별 결과, 테이블별 건수, 경고, 롤백 뒤 남은 변경, 환경 지문, 수집 범위, "잔여 확인 실패" 배너 |
| C4 적용 | `ApplyPanel`, `ApplyResultView` | [적용]은 status ok · 환경 지문 일치 · 30분 안 · SQL 불변일 때만 켜지고 꺼지면 이유를 모두 보인다. 누르면 한 번 더 확인한다. [취소]. ApplyResult(드리프트·불일치·IDENTITY 짝·KEYSET 한계·잔여 확인 실패) |

오류는 ProblemDetails를 `ProblemBanner`로 보인다. 409는 `reason` 5종(LOCKED·BUILDING·NO_BASELINE·FINGERPRINT_MISMATCH·DIRTY)별 문장과 [새로 고침] 단추를, 422는 거부 규칙 ID와 위치를, 503은 연결 끊김 문장을 보인다.

## SP9 측정 화면(`/sp9`)

서버 없이 합성 데이터로 잰다. 그래프 노드 50·200·500개(배치+렌더), 그리드 1만·5만 행(첫 렌더, 끝까지 스크롤, 정렬). 값은 m5.csv의 브라우저 열에 수동으로 옮긴다(Q42). 같은 크기를 3회 이상 재서 중앙값을 쓴다.

## 구조

```
src/
  api/types.ts         week3-plan.md §5를 손으로 옮긴 타입(유일한 기준은 §5)
  api/client.ts        fetch 래퍼, ApiError, 오류 문장
  stores/              Pinia: sandbox(상태·스키마 캐시), preview(편집·분석·미리보기·적용)
  composables/         useRowPage(서버 페이지), useNow
  lib/                 순수 함수: applyGate, graphModel·graphLayout·graphStyle, gridRows, editorSupport, format, sp9Data
  components/          위 표의 화면 조각
  views/               HomeView(/), Sp9View(/sp9)
```
