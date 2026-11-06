# CascadeLab — 합성 정답 DB (CL01~CL08)

설계 §10.2의 CascadeLab 패턴 가운데 CL01~CL08의 스키마·시드와 손 정답(1차 정답, §10.3)이다.
**아직 DB에서 실행하지 않았다.** 정답은 설계 문서 규칙(§2 A번호, §6~§7)과 시드를 손으로 센 결과다. 실행 대조는 월요일에 한다.

## 구성

| 경로 | 내용 |
|---|---|
| `answer-format.md` | 정답 JSON 형식 (먼저 읽을 것) |
| `sql/00-create-databases.sql` | `CascadeLab`(RECURSIVE_TRIGGERS OFF), `CascadeLab_RT`(ON) 생성 + 설정 확인 쿼리 |
| `sql/cl01.sql` ~ `sql/cl08.sql` | 패턴별 스키마 `cl01`~`cl08`, 테이블·트리거·시드 |
| `cases/CLnn-*.json` | 사례별 문장·기대값·근거 |

## 실행 순서

1. `sql/00-create-databases.sql` (기존 두 DB를 지우고 다시 만든다)
2. `sql/cl01.sql` … `sql/cl08.sql` (순서 무관. `cl08.sql`은 두 DB 모두에 만든다)
3. 사례마다 시드 직후 상태에서 `statements`를 실행한다. 사례 사이에는 초기화한다(§10.3 구성).

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

## 문서만으로 정할 수 없어 observed_only로 둔 것

- 형제 체인·묶음 사이 순서와 `captureSeq` (A1 "연쇄의 반대 순서" 정의 없음, A2) — CL01·CL05·CL06·CL07
- FK 연쇄 묶음의 `depth` (H2 전제, A7에 연쇄 시 값 없음) — CL01~CL06
- CL04 부모 옛 키-새 키 짝과 `parentRowIds` (A12) — 정답은 `UNPAIRED`
- CL03b 오류 번호 547 (FK 충돌 오류로 판단, 이 경로를 밝힌 인용 문서 없음)
- CL08a 트리거 안 같은 테이블 UPDATE가 감사 트리거를 발동하는지: a) WRITES 간선 / b) SAME_TABLE_TRIGGER 잔차 (A8 문구는 a, 설계 E14는 b). 순 변경은 어느 쪽이든 같다.

## 정정

손 정답과 오라클이 다르면 사례 파일을 바로 고치지 말고, 원인(문서 해석 오류인지 정답 오류인지)을 기록한 뒤 고친다(§10.3).
