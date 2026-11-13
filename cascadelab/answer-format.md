# CascadeLab 손 정답 형식 (v0.3)

사례 하나 = `cases/CLnn-<짧은이름>.json` 파일 하나. 각 사례는 시드 직후 상태(`sql/00`→`sql/clNN.sql` 실행 직후)에서 시작한다.

v0.2(2026-11-11, lab-1·eval-2 합의)는 v0.1에 필드를 더하기만 한다. v0.1 파일은 그대로 v0.2 파일이다. 추가한 것은 [v0.2 추가](#v02-추가) 절에 모았다.

v0.3(2026-11-13, EXEC 미리보기용)도 더하기만 한다. v0.2 파일은 그대로 v0.3 파일이다. 추가한 것은 [v0.3 추가](#v03-추가) 절에 모았다. 새 이름은 공용 계약(EXEC)을 따르고, ruleId는 통합 때 확정한다.

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

## v0.2 추가

### 최상위

| 필드 | 뜻 |
|---|---|
| `leavesResidue` | `true`면 트리거 ROLLBACK 뒤 트랜잭션 밖에서 확정된 변경이 원본(오라클)에 남는다(CL12a). 오라클의 "롤백 뒤 시드와 같음" 확인이 다르게 나오는 것이 정상이다. 없으면 `false` |
| `scaling` | 행 수 생성기 사례(CL20)만. `{statement, generate, counts}`. `statement`·`generate`는 `{N}`을 넣을 SQL 틀이다. 예 `"generate": "EXEC cl20.usp_Generate @parents = {N};"`. `counts`는 테이블별 건수 식(설명용 자유 형식) |

### expected

| 필드 | 뜻 |
|---|---|
| `warnings` | (선택) 분석기가 내야 할 경고 ruleId 집합. 예 `["WARN_TRIGGER_DYNAMIC_SQL"]` |
| `residualCommitted` | `changes`와 같은 모양. 트리거 ROLLBACK 뒤 트랜잭션 밖에서 확정된 변경(A22, E13, §6.2 `residualCommitted`). 이때 `changes`는 트랜잭션 안 순 변경이라 비어 있다. 오라클 대조는 `changes ∪ residualCommitted`로 한다(오라클은 첫 실패 문장에서 ROLLBACK한 뒤 after를 읽는다) |

### firings 항목

- `statementIndex`: 문장 번호. 없으면 1.
- `auditOrder`: 감사 트리거가 Last가 아닌 묶음만 `"NONE"`으로 쓴다(E15). 없으면 `LAST`.

### edges 항목

- `kind`에 `REPLACES`(§6.3)를 더한다. parent `S`, child `T:<schema.trigger>`(INSTEAD OF 실행), name = 트리거 이름.
- `ambiguous` 간선은 실제로 쓴 트리거의 간선 하나만 적고 `confidence: "ambiguous"`로 둔다. 하네스는 제품 후보 집합에 정답이 들었는지 센다(M3).

### changes 행

`causes` 값에 `INSTEAD_OF`(§6.2 cause.kind, CL11)를 더한다.

**IDENTITY 행**(키에 IDENTITY 열이 있어 값이 실행마다 달라지는 inserted 행)은 `key` 대신 아래를 쓴다.

```json
{ "ref": "o1", "match": { "customer": "c1" }, "identity": ["id"],
  "volatile": ["invoice_no", "created_at", "row_guid"], "causes": ["DIRECT"] }
```

- `ref`: 사례 안에서 고유한 라벨. 다른 행의 `identityRefs`가 가리킨다.
- `match`: 비결정 아닌 열 값. 그 테이블의 INSERTED 행 하나를 가리킬 만큼만 쓴다. IDENTITY 열, 비결정 열, 새 IDENTITY를 참조하는 열은 넣지 않는다.
- `identity`: 그 행의 IDENTITY 열 이름.
- 하네스 판정: 같은 테이블 오라클 INSERTED 행 가운데 after가 `match`를 모두 만족하는 행과 짝짓는다. 여럿이면 손 정답 배열 순서와 IDENTITY 오름차순으로 차례대로 짝짓는다.

**새 IDENTITY를 참조하는 열**은 `identityRefs`에 쓴다. 새 IDENTITY를 참조하는 열이 있으면 반드시 쓴다.

```json
"identityRefs": { "ord_id": { "table": "cl16.Ord", "ref": "o1" } }
```

- 하네스는 그 열의 오라클 값이 짝지은 부모 행(`ref`)의 IDENTITY 값과 같은지 본다(D7 "자식 FK는 짝 매핑으로 치환").
- FK가 아닌 열에도 쓴다. 예: CL12c의 `seen_identity`(트리거가 읽은 `@@IDENTITY`).
- 행 자신의 키가 결정적이면 `key`를 그대로 쓰고 `identityRefs`만 붙인다. 그 행이 IDENTITY 행이면 `match`·`identity`와 함께 쓴다.

**비결정 셀**은 `volatile`에 열 이름만 쓴다(GETDATE·SYSDATETIME·NEWID·SEQUENCE 기본값·rowversion, IDENTITY가 아닌 열).

- 값은 쓰지 않는다. 하네스는 오라클 after에 그 열이 있고 NULL이 아닌지만 본다. 그래서 NOT NULL로 끝나는 열만 쓴다. 같은 열을 `after`에 쓰지 않는다.
- updated 행에도 쓴다. 그 열이 바뀌면 `changedColumns`에도 넣는다(값만 비교하지 않는다).

**키 없는 테이블**(E6, CL14)은 changes 항목에 `"keyKind": "ROW"`, `"keyColumns": []`를 쓰고, `inserted`·`updated`·`deleted`를 빈 배열로 둔 뒤 아래 두 배열을 쓴다.

```json
"multisetRemoved": [ { "row": { "name": "a", "qty": 1 }, "multiplicity": 2, "causes": ["DIRECT"] } ],
"multisetAdded":   [ { "row": { "name": "a", "qty": 11 }, "multiplicity": 2, "causes": ["DIRECT"] } ]
```

- `row`는 계산 열을 뺀 모든 열이다(제품 C12·하네스 비교기와 같은 규칙). 비교는 (`row`, `multiplicity`) 다중집합이다.

### 노드 참조

v0.1 그대로다. 같은 테이블·연산 묶음이 한 문장에 여럿이면 `#k`는 인과 순서다. 순서를 문서로 정할 수 없으면 사례 파일의 `observedOnly`에 무엇을 기준으로 번호를 붙였는지 적는다.

## v0.3 추가

EXEC(저장 프로시저 호출) 사례와 거부 사례용이다. 공용 계약(2026-11-13)의 이름을 그대로 쓴다. 하네스는 제품 계약의 새 enum에 기대지 않고 이 필드를 문자열로 읽는다(`eval/DbRipple.Eval/HandCase.cs`).

### 최상위

| 필드 | 뜻 |
|---|---|
| `statements[].returnValue` | (선택) EXEC 문장의 기대 반환값(정수). 문장이 실패하거나 반환값이 없으면 `null`. 하네스는 제품 `StatementResult.returnValue`와 비교해 `differences.txt`에 남긴다(지표 아님) |

### expected

| 필드 | 뜻 |
|---|---|
| `status` | `rejected`를 더한다(분석 단계 거부, 422). 이때 `statementStatus`는 `[]`, `changes`·`firings`·`edges`는 `[]`다 |
| `statementStatus[].isToolFault` | (선택) 실패 문장의 기대 분류. 사용자 프로시저 때문에 실패하면 `false`(CL24). 하네스는 제품 `statements[].error.isToolFault`와 비교해 `differences.txt`에 남긴다 |
| `rejects[]` | `[{ "ruleId", "statementIndex" }]`. 분석기가 내야 할 거부 집합. 거부 사례의 정답이다. 아래 규칙 참고 |

**rejects 규칙**

- 비교는 (`ruleId`, `statementIndex`) 집합이다. 문장 번호는 `statements[].index`와 같은 전역 번호다.
- `ruleId`가 `*`로 끝나면 접두 패턴이다(예 `REJECT_PROC_*`). 규칙 이름이 아직 정해지지 않은 새 사례는 자리표시 패턴에 `"ruleIdPending": true`와 `"ruleIdHint"`(예상 이름, 설명)를 붙이고, 이름이 정해지면 실제 이름으로 바꾸고 `ruleIdPending`을 지운다. CL25a~d는 2026-11-13 통합 때 `AnalysisRules`의 실제 이름으로 확정했다(지금 자리표시를 쓰는 사례는 없다).
- 거부 사례는 오라클을 실행하지 않고 M1·M2·M3에 넣지 않는다. 대조군(B0·B2) 행도 만들지 않는다. 거부는 결정적이라 `cases.csv`에는 `status_expected = rejected`와 미리보기 상태(`status_actual = rejected`)만 남고, (ruleId, 문장 번호) 차이는 `differences.txt`와 실행 폴더의 `rejects.json`에 쓴다.
- 오라클을 실행하지 않는 이유: 거부된 문장은 원본에서 실행하면 안 되는 것들이다(CL25b의 남는 COMMIT은 오라클 트랜잭션을 확정해 시드를 오염시킨다).
- `database`가 `CascadeLab`·`CascadeLab_RT`가 아닌 사례(CL25d, `CascadeLab_Reach`)는 하네스가 건너뛴다(알림 한 줄). 분석 API 회귀(`scripts/home-test.ps1` N1 단계)로 확인한다.

### changes 행

`causes` 값에 `PROC_DIRECT`(§6.2 cause.kind 새 값)를 더한다. EXEC 문장 창 안, 깊이 1, FK로 설명되지 않는 쓰기다. 안쪽 프로시저가 쓴 행도 `PROC_DIRECT`다. 프로시저가 쓴 테이블의 FK 연쇄·트리거 쓰기는 기존 원인(`FK_CASCADE`, `TRIGGER` …)을 그대로 쓴다. M2는 `PROC_DIRECT` 버킷을 따로 센다(`HandCase.Causes`의 아홉 번째 값).

프로시저가 먼저 지운 FK 자식(CL22 ChildA)은 `PROC_DIRECT`다. `FK_CASCADE`로 적지 않는다(부모 DELETE 때 FK가 지울 행이 없었다).

### 노드 참조

| 표기 | 노드 |
|---|---|
| `P:<스키마.프로시저>` | 프로시저 호출 노드(그래프 노드 type `procedureCall`). 예 `P:cl21.usp_Outer` |

- `P:` 노드는 EXEC 문장의 프로시저다. 안쪽에서 호출된 프로시저는 노드로 만들지 않는다(제품은 `expandedCalls[]`로만 알린다).
- EXEC 문장에는 `S` 노드가 없다. 제품은 문장 노드 자리(id `st-n`)에 `procedureCall` 노드를 두므로 `P:`가 그 문장의 뿌리이고 `S` → `P:` 간선도 없다. 실패한 EXEC의 오류 노드 `E:<번호>`도 부모가 `P:`다(2026-11-13 통합 확정).

### edges 항목

- `kind`에 `PROC_DIRECT`를 더한다. parent `P:<스키마.프로시저>`, child 변경 묶음 `F:…`, name = EXEC 문장에 쓴 프로시저 이름(`cl21.usp_Outer`), confidence `certain`(§6.3, 공용 계약).
- 안쪽 프로시저가 쓴 묶음의 name도 EXEC 문장에 쓴 프로시저다(제품은 묶음별 안쪽 프로시저를 가리지 않는다).
- FK 확신도(결정): 프로시저의 정적 쓰기 대상에 자식 테이블이 있으면 그 FK 간선은 `inferred`로 낮출 수 있다. 정답은 실제 원인을 적는다. 프로시저가 직접 지운 자식(CL22 ChildA)에는 FK 간선을 두지 않고, 프로시저가 건드리지 않은 자식(CL22 ChildB)은 `certain` FK 간선이다.
- 실패 문장의 오류 노드(`E:<번호>`)는 CL12a와 같이 `S` → `E:`의 `BLOCKED` 간선으로 적는다(통합 때 확정).

### 사례

CL21~CL25(a~d)와 스키마(`sql/cl21.sql`~`sql/cl25.sql`, `sql/extra/cl25d.sql`)는 [README.md](README.md)의 사례 요약에 있다.

