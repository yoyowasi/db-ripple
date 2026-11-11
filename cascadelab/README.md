# CascadeLab — 합성 정답 DB (CL01~CL20)

설계 §10.2의 CascadeLab 패턴 CL01~CL20의 스키마·시드와 손 정답(1차 정답, §10.3)이다.
정답은 설계 문서 규칙(§2 A번호, §6~§7)과 시드를 손으로 센 결과다.
- CL01~CL08: 2026-11-10 실DB 대조 11/11 일치([verification/report-2026-11-10.md](verification/report-2026-11-10.md)).
- CL09~CL20: 2026-11-11 작성, **아직 DB에서 실행하지 않았다**(정적 검토만). 실행 대조는 테스트 라운드에서 한다.
- `verification/CascadeLab.Verify`는 answer-format v0.1 필드만 읽는다(v0.2의 `match`·`identity`·`identityRefs`·`residualCommitted` 등은 모른다). CL09 이후 사례의 오라클 대조는 평가 하네스(`eval/DbRipple.Eval.Cli -- cascadelab`)로 한다.

## 구성

| 경로 | 내용 |
|---|---|
| `answer-format.md` | 정답 JSON 형식 (먼저 읽을 것) |
| `sql/00-create-databases.sql` | `CascadeLab`(RECURSIVE_TRIGGERS OFF), `CascadeLab_RT`(ON) 생성 + 설정 확인 쿼리 |
| `sql/cl01.sql` ~ `sql/cl20.sql` | 패턴별 스키마 `cl01`~`cl20`, 테이블·트리거·시드. 각 파일은 자기 스키마만 만든다 |
| `cases/CLnn-*.json` | 사례별 문장·기대값·근거(형식 v0.2) |

## 실행 순서

1. `sql/00-create-databases.sql` (기존 두 DB를 지우고 다시 만든다)
2. `sql/cl01.sql` … `sql/cl20.sql` (순서 무관. `cl08.sql`만 두 DB 모두에 만들고, 나머지는 `CascadeLab`에만 만든다)
3. 사례마다 시드 직후 상태에서 `statements`를 실행한다. 사례 사이에는 초기화한다(§10.3 구성).

- TestSupport `CascadeLabSeed`가 `sql/cl*.sql`을 모든 CascadeLab 테스트 기준선에 싣는다. 그래서 시드는 작게 두고, 스크립트는 자기 스키마 밖을 건드리지 않으며 서버 설정을 바꾸지 않는다(week3-plan 위험 4, AD11).
- CL20 대량 데이터는 `EXEC cl20.usp_Generate @parents = N;`으로만 만든다(Q39). 멱등이고(기존 cl20 행을 지우고 다시 만든다) 시드 기본값은 3이다. **원본 역할 DB에서 기준선(백업)을 만들기 전에** 부른다. 감사 트리거가 깔린 샌드박스에서 부르면 Parent DELETE가 수집된다.

- 순수 T-SQL + `GO`만 쓴다. sqlcmd `:setvar`·`$(var)`는 쓰지 않는다(ScriptDom 문법 점검 대상).
- 서버 옵션 `nested triggers = 1`(기본값)을 전제로 한다. 스크립트는 서버 설정을 바꾸지 않고 확인 쿼리만 둔다.
- 샌드박스·스냅샷(`CascadeLab_Sandbox`, `CascadeLab_Sandbox_base`)은 제품 C8이 만든다.

## 왜 DB가 둘인가

`RECURSIVE_TRIGGERS`는 DB 단위 옵션이고 실제 값은 원본 DB를 따른다(A8). 제품은 원본과 샌드박스의 설정을 비교해 다르면 막는다(환경 지문). 그래서 ON이 필요한 CL08b만 `CascadeLab_RT`에 두고, 나머지는 모두 OFF인 `CascadeLab`에 둔다.

## 사례 요약

| 사례 | 문장 | 기대 상태 | 변경 테이블 | 변경 행 | 간선 |
|---|---|---|---|---|---|
| CL01 | `DELETE cl01.A id=1` (CASCADE 4단) | ok | 4 | 9 | 4 |
| CL02 | `DELETE cl02.Dept id=1` (SET NULL) | ok | 2 | 3 | 2 |
| CL03 | `DELETE cl03.Category id=1` (SET DEFAULT) | ok | 2 | 3 | 2 |
| CL03b | `DELETE cl03.Grp id=1` (기본값이 부모에 없음) | failed, 547 | 0 | 0 | 1 (BLOCKED) |
| CL04 | `UPDATE cl04.P SET id=id+100 WHERE id IN (1,2)` | ok | 2 | 7 (P 삭제 2·삽입 2, Ch 갱신 3) | 2 |
| CL04b | `UPDATE cl04.P SET id=300 WHERE id=3` (단일 행, 짝 확정) | ok | 2 | 3 | 2 |
| CL05 | `DELETE cl05.P id=1` (체인 2개) | ok | 5 | 7 | 5 |
| CL06 | `DELETE cl06.Ord id=1` (연쇄 대상 트리거) | ok | 3 | 5 | 4 |
| CL07 | `UPDATE cl07.A id=1` (트리거 3단 사슬) | ok | 4 | 6 | 7 |
| CL08a | `UPDATE cl08.Acct id=1` (OFF) | ok | 1 | 1 | 2 확정 + 1 관측 대기 |
| CL08b | `UPDATE cl08.Acct id=1` (ON) | ok | 1 | 1 | 8 |
| CL09 | `DELETE cl09.P id=1` (트리거 DELETE가 2차 CASCADE로 K에 다시 닿음) | ok | 4 | 6 | 6 |
| CL10 | `DELETE cl10.Region id=1` (3단 CASCADE + SET NULL + 동적 SQL 트리거, DM4) | ok, 경고 WARN_TRIGGER_DYNAMIC_SQL | 7 | 12 | 9 |
| CL11 | `INSERT cl11.Item id=3` (INSTEAD OF INSERT) | ok | 2 | 2 | 3 (REPLACES 1) |
| CL11b | `UPDATE cl11.Item id IN (1,2)` (INSTEAD OF UPDATE, 1행 거름) | ok | 2 | 3 | 3 (REPLACES 1) |
| CL12a | `UPDATE cl12.Ord id=1` → qty < 0, 트리거 ROLLBACK | trigger_blocked, 3609 | 0 (+ 롤백 뒤 남은 ErrLog 1) | 0 (+1) | 1 (BLOCKED) |
| CL12b | 0행 UPDATE + 1행 UPDATE (ROWCOUNT_BIG() 0행 가드) | ok | 2 | 2 | 5 |
| CL12c | `INSERT cl12.Doc` (@@IDENTITY를 읽는 트리거) | ok | 3 | 3 | 4 |
| CL13 | `DELETE cl13.T id=1` (AFTER 트리거 3개, 사용자 Last, 감사 NONE) | ok, 경고 WARN_AUDIT_NOT_LAST | 3 | 8 | 10 |
| CL14 | `UPDATE cl14.Tag name='a'` (힙, 중복 행) | ok | 1 | 다중집합 −2/+2 | 1 |
| CL14b | `DELETE TOP (1) cl14.Tag name='a'` | ok, 경고 WARN_TOP_WITHOUT_ORDER | 1 | 다중집합 −1 | 1 |
| CL15 | `UPDATE cl15.Seat SET id=id+100` (2행, 다른 유일 키 없음) | ok, UNPAIRED | 1 | 4 (삭제 2·삽입 2) | 1 |
| CL15b | `UPDATE cl15.Emp SET id=id+100` (2행, badge UNIQUE) | ok, BY_UNIQUE | 1 | 4 | 1 |
| CL16 | `INSERT cl16.Ord` 2행 (IDENTITY·SEQUENCE·SYSDATETIME·NEWID, 새 IDENTITY 참조 자식) | ok | 2 | 4 | 3 |
| CL16b | `UPDATE cl16.Ord SET touched_at = SYSDATETIME()` | ok, 경고 WARN_NONDETERMINISTIC_FUNCTION | 1 | 1 | 1 |
| CL17 | 문장 3개(INSERT → 의존 UPDATE → DELETE) | ok | 1 | 1 | 3 |
| CL18 | 문장 3개(ok → NO ACTION 위반 → not_run) | failed, 547 | 0 | 0 | 1 (BLOCKED) |
| CL19 | `UPDATE cl19.Doc` xml·hierarchyid·geography·text | ok | 1 | 1 | 2 (RESIDUAL 1) |
| CL20 | `DELETE cl20.Parent id<=2` (시드 @parents=3, M5 DELETE 틀) | ok | 3 | 10 | 4 |
| CL20b | `UPDATE cl20.Child id<=3` (M5 UPDATE 틀) | ok | 2 | 5 | 3 |

CL20·CL20b의 `scaling`(형식 v0.2)에 M5용 `generate`·`statement` 틀과 N별 건수 식이 있다. 데모 DM4는 CL10, DM6은 CL20 틀(N = 5만)이다.

## 문서만으로 정할 수 없어 observed_only로 둔 것

- 형제 체인·묶음 사이 순서와 `captureSeq` (A1 "연쇄의 반대 순서" 정의 없음, A2) — CL01·CL05·CL06·CL07
- FK 연쇄 묶음의 `depth` (H2 전제, A7에 연쇄 시 값 없음) — CL01~CL06
- CL04 부모 옛 키-새 키 짝과 `parentRowIds` (A12) — 정답은 `UNPAIRED`
- CL03b 오류 번호 547 (FK 충돌 오류로 판단, 이 경로를 밝힌 인용 문서 없음)
- CL08a 트리거 안 같은 테이블 UPDATE가 감사 트리거를 발동하는지: a) WRITES 간선 / b) SAME_TABLE_TRIGGER 잔차 (A8 문구는 a, 설계 E14는 b). 순 변경은 어느 쪽이든 같다. (2026-11-10 실측으로 a 확정)
- CL09 K 묶음이 1차·2차 두 개로 나뉘는지(H4·H5, SP1 관측), 2차 체인 FK 간선 확신도(certain/inferred, §7.2 3단계 괄호 해석)
- CL10 동적 SQL 트리거 하나뿐인 후보의 확신도(inferred/ambiguous, §7.2 4단계와 E8 문구)
- CL11·CL11b INSTEAD OF 안 DML 묶음 depth 2(A7)
- CL12a 오류 3609(2차 출처 + 이 저장소 AW 재현)와 firings가 비는지, CL12b 트리거 첫 문장의 ROWCOUNT_BIG() 값
- CL13 감사(NONE) 묶음의 위치(SP1 관측: trg_T_1 바로 뒤)
- CL18 NO ACTION 경로의 547(CL03b는 실측, 이 경로는 미실측)
- CL19 text 열이 UNOBSERVABLE_COLUMN 잔차로 나오는 경로

## 정정

손 정답과 오라클이 다르면 사례 파일을 바로 고치지 말고, 원인(문서 해석 오류인지 정답 오류인지)을 기록한 뒤 고친다(§10.3).

### 2026-11-10: firings[].pairing 6건 (2주차 통합, 2026-11-11 사례 파일 반영)

| 사례 | 묶음 | 손 정답 | 정정 |
|---|---|---|---|
| CL07 | `F:cl07.D:UPDATE` | SINGLE_ROW | BY_PK |
| CL08a | `F:cl08.Acct:UPDATE#1` | SINGLE_ROW | BY_PK |
| CL08b | `F:cl08.Acct:UPDATE#1`~`#4` | SINGLE_ROW | BY_PK |

- **원인:** 정답 오류. 모두 한 행이고 PK가 바뀌지 않은 UPDATE다. 수집기 규칙(설계 §4 v1.4 C11, Q11)은 키가 그대로면 행 수와 상관없이 BY_PK이고, SINGLE_ROW는 키가 바뀌었거나 키 없는 테이블의 한 행 묶음에만 쓴다. 손 정답 안에서도 같은 모양인 CL07 `F:cl07.A:UPDATE`와 CL04b `F:cl04.Ch:UPDATE`는 BY_PK로 적혀 있어 엇갈린다. CL04b `F:cl04.P:UPDATE`(PK 3→300, 한 행)의 SINGLE_ROW는 규칙과 맞으므로 그대로 둔다.
- **영향:** M1·M2와 순 변경에는 영향이 없다(pairing은 묶음 표시 항목).
- **증거:** 실DB 수집기 출력(Execution.Tests CascadeLab 회귀, `logs/week2/eval/eval-20261110-132555/records/`의 rowChanges[].pairing).
- **반영(2026-11-11, lab-1):** CL07·CL08a·CL08b의 `firings[].pairing` 6건을 BY_PK로 고치고 각 파일 `corrections[]`에 기록했다(날짜 2026-11-11, before SINGLE_ROW, after BY_PK, 증거·원인 위와 같음).
- **남은 일:** engine-1이 `tests/DbRipple.Execution.Tests/CascadeLabTests.cs`의 `PairingOverrides`를 지운다(알림 보냄). 실행 확인은 테스트 라운드.
