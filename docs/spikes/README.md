# 스파이크 기록 (설계 §13)

스파이크마다 `SPn.md`에 목적, 방법(실행한 SQL·명령), 결과(확인한 사실과 추정), 증거 범위, 설계 반영을 적는다. 실행 스크립트는 `spikes/` 아래에 있다.

## 상태 (2026-11-06 기준)

| ID | 확인할 것 | 상태 | 증거 범위 | 기록 |
|---|---|---|---|---|
| SP0 | 컨테이너 에디션, 파일별 스냅샷 생성 | 스크립트만 준비, 미실행 | — | [SP0.md](SP0.md) |
| SP1 | 전제 H1~H5 | 스크립트만 준비, 미실행 | — | [SP1.md](SP1.md) |
| SP2 | 관찰자 효과 (a)(c)(d)(e) | 스크립트만 준비, 미실행 | — | [SP2.md](SP2.md) |
| SP3 | 자기 테이블 재갱신과 잔차 | 스크립트만 준비, 미실행 | — | [SP3.md](SP3.md) |
| SP4 | ScriptDom 파서 클래스, GO, 쓰기 대상 | 실행함(라이브러리) | 정적: 라이브러리 실행, DB 미사용 | [SP4.md](SP4.md) |
| SP5 | FILELISTONLY, 전체 텍스트, 백업·복원·스냅샷 시간 | 스크립트만 준비, 미실행 | — | [SP5.md](SP5.md) |
| SP6 | FOR JSON 형식 변환 | 스크립트만 준비, 미실행 | — | [SP6.md](SP6.md) |
| SP10 | 트랜잭션 안 EXECUTE AS/REVERT, 소유권 체인 | 스크립트만 준비, 미실행 | — | [SP10.md](SP10.md) |
| SP7~SP9 | 2·3주차 | 범위 밖 | — | — |

미실행 사유: 2026-11-06 작업 PC에 Docker Desktop·WSL이 설치돼 있지 않았다(관리자 권한 없음). 준비 절차는 [../dev-setup.md](../dev-setup.md)에 있다.

## 월요일 실행 순서

1. 컨테이너 준비: `dev-setup.md` 절차(설치 → `.env` → `scripts/db-up.ps1`).
2. SP0: 에디션과 스냅샷. 실패하면 2022-latest Developer로 바꾸고 D1을 고친다.
3. AdventureWorks 복원(`scripts/fetch-adventureworks.ps1`, `scripts/restore-aw-origin.ps1`) 뒤 SP5.
4. SP6, SP1, SP3, SP10, SP2 순서로 실행하고 결과를 각 `SPn.md`에 채운다. 세부 순서는 각 `SPn.md`에 있다. `sp2_10`·`sp3_10`·`sp10_10`은 `-- PART 2` 주석 앞뒤 사이에 `common_audit_store.sql`·`common_hand_audit.sql`을 설치해야 하므로 통째로 실행하지 않는다.
   - 아직 없는 것: SP3의 AW_Sandbox 판(uSalesOrderHeader RevisionNumber), SP10의 AW 판(DML 전용 사용자와 dbo 결과 비교).
5. 깨진 전제는 설계 §7.1 "깨지면" 규칙대로 `docs/architecture.md`에 반영하고 v1.3을 확정한다.
