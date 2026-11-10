# CascadeLab 손 정답 형식 (v0.1)

사례 하나 = `cases/CLnn-<짧은이름>.json` 파일 하나. 각 사례는 시드 직후 상태(`sql/00`→`sql/clNN.sql` 실행 직후)에서 시작한다.

## 최상위

| 필드 | 뜻 |
|---|---|
| `id` | 사례 ID (`CL01`, `CL08a` …) |
| `pattern` | 설계 §10.2 패턴 ID |
| `database` | 원본 역할 DB (`CascadeLab`, `CascadeLab_RT`) |
| `schemaScript` | 스키마·시드 스크립트 경로(`cascadelab/` 기준) |
| `settings` | 정답이 기대는 설정. `RECURSIVE_TRIGGERS`, `nestedTriggers`(서버 옵션) |
| `statements[]` | `{index, sql}`. 도구가 하나씩 실행하는 사용자 문장 |
| `expected` | 아래 |
| `rationale[]` | `{claim, basis}`. basis는 설계 문서 근거(A번호·§) |

## expected

| 필드 | 뜻 |
|---|---|
| `status` | `ok` / `failed` / `trigger_blocked` (§6.2) |
| `errorNumber` | 기대 오류 번호, 없으면 `null` |
| `statementStatus[]` | `{index, status}` (`ok`/`failed`/`trigger_blocked`/`not_run`) |
| `changes[]` | 테이블별 **순 변경**(NetChange 기준, PK 조인). `{table, keyColumns, inserted[], updated[], deleted[], basis}` |
| `firings[]` | 기대 변경 묶음(노드). `{node, table, operation, rowCount, causeKind, depth, pairing?}` |
| `edges[]` | 기대 간선. M3 간선 키 + 기대 확신도 + 근거 |
| `observedOnly[]` | 문서 규칙만으로 정할 수 없는 부분. `{id, what, alternatives?, basis, confirmed?}`. 실측으로 값이 정해지면 `confirmed: {date, value, note, evidence}`를 붙인다(2026-11-10 추가) |
| `corrections[]` | 정답 정정 기록(설계 §10.3). `{date, item, before, after, evidence, cause}`. 대안을 하나로 확정해 `observedOnly`에서 뺀 경우에도 남긴다(2026-11-10 추가) |

### changes 행

- `{ "key": {"id": 10}, "causes": ["FK_CASCADE"] }`
- updated는 `changedColumns`(값 비교 기준, A12)를 반드시 쓴다.
- 셀 값은 쓰지 않는다(오라클로 확정, §10.2·§10.3). 단 값 자체가 판정 기준인 열(SET NULL·SET DEFAULT 결과, UPDATE CASCADE의 새 FK 값, 재귀 횟수 카운터)은 `after`에 그 열만 쓴다.
- `parentKey`(선택): 부모 짝이 확정돼 행 단위 부모가 정해지는 경우(§6.2 `parentRowIds`)의 부모 행 키(after 기준).
- PK가 바뀐 UPDATE는 PK 조인 기준이라 옛 키는 `deleted`, 새 키는 `inserted`에 둔다(NetChange 규칙). 묶음 쪽 짝짓기는 `firings[].pairing`으로 따로 적는다.
- `causes` 값: `DIRECT`, `FK_CASCADE`, `FK_SET_NULL`, `FK_SET_DEFAULT`, `TRIGGER`, `SAME_TABLE_TRIGGER`(잔차) — §6.2 cause.kind, §10.4 M2. 문서만으로 정해지지 않으면 `{"oneOf": [...], "ref": "OO1"}`.
- 빈 배열은 "그 종류의 변경이 없어야 한다"는 뜻이다. `changes`에 없는 CascadeLab 테이블은 변경 0이어야 한다(같은 패턴 스키마 기준).

### 노드 참조 (edges의 parent·child)

| 표기 | 노드 |
|---|---|
| `S` | 문장 노드(문장 번호는 `statementIndex`) |
| `F:<schema.table>:<OP>` | 변경 묶음(INSERT/UPDATE/DELETE) |
| `T:<schema.trigger>` | 사용자 트리거 1회 실행 |
| `R:<schema.table>` | 잔차 노드 |
| `E:<오류번호>` | 오류 노드 |

한 문장에서 같은 묶음·트리거 실행이 여러 번 나오면 `#k`(인과 순서, 1부터)를 붙인다. 예: `F:cl08.Acct:UPDATE#2`.

### edges 항목

`{statementIndex, parent, child, kind, name, confidence, basis}` 와 FK 간선의 `action`·`event`.

- M3 간선 키 = (`statementIndex`, `parent`, `child`, `kind`, `name`) (§10.4).
- `kind`: `DIRECT`, `FK`, `FIRES`, `WRITES`, `RESIDUAL`, `BLOCKED` (§6.3).
- `name`: FK 간선은 FK 이름, FIRES·WRITES·RESIDUAL은 트리거 이름, DIRECT·BLOCKED는 `null`.
- `confidence`: 제품 규칙(§7.2·§7.3)이 내야 할 등급. DIRECT `certain`, 조건을 만족한 FK `certain`, 트리거 간선 최대 `inferred`.
- `observed: false`: 변경을 남기지 않은 트리거 실행(카탈로그 추론 점선, §6.3).

## 채점 메모

- 키 집합은 순서 없는 집합으로 비교한다. 형제 묶음·체인 순서와 `captureSeq`는 정답에 넣지 않는다(A2, §7.2 7단계).
- `depth`는 A7(트리거 스택 수)로 정해지는 값만 확정값으로 쓰고, FK 연쇄 묶음의 깊이처럼 H2 전제에 기대는 값은 `observedOnly`에도 적는다.
- 손 정답과 오라클이 다르면 사례 파일을 고치지 말고 정정 기록을 남긴다(§10.3).
