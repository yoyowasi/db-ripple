# 스파이크 기록 (설계 §13)

스파이크마다 `SPn.md`에 목적, 방법(실행한 SQL·명령), 결과(확인한 사실과 추정), 증거 범위, 설계 반영을 적는다. 실행 스크립트는 `spikes/` 아래에 있다.

## 상태 (2026-11-13 기준: SP0~SP10은 2026-11-11 표 그대로, SP11~SP21 행 추가)

| ID | 확인할 것 | 상태 | 증거 범위 | 기록 |
|---|---|---|---|---|
| SP0 | 컨테이너 에디션, 파일별 스냅샷 생성 | 통과(2026-11-10) | DB 실측(컨테이너) | [SP0.md](SP0.md) |
| SP1 | 전제 H1~H5 | H1~H5 모두 성립(2026-11-10). H3은 3회 관측 | DB 실측(컨테이너) | [SP1.md](SP1.md) |
| SP2 | 관찰자 효과 (a)(c)(d)(e) | 성립(2026-11-10). (d) 연쇄 0행은 미발동. pubs (a)는 미설치로 미확인, (b)는 2주차 | DB 실측(컨테이너) + 드라이버 | [SP2.md](SP2.md) |
| SP3 | 자기 테이블 재갱신과 잔차 | 통과(2026-11-10): RECURSIVE_TRIGGERS OFF에서도 감사가 중첩 자기 갱신을 깊이+1 묶음으로 잡음(CAPTURED) | DB 실측(컨테이너) | [SP3.md](SP3.md) |
| SP4 | ScriptDom 파서 클래스, GO, 쓰기 대상 | 실행함(라이브러리) | 정적: 라이브러리 실행, DB 미사용 | [SP4.md](SP4.md) |
| SP5 | FILELISTONLY, 전체 텍스트, 백업·복원·스냅샷 시간 | 통과(2026-11-10) | DB 실측(컨테이너) | [SP5.md](SP5.md) |
| SP6 | FOR JSON 형식 변환 | 일부 깨짐(2026-11-10): float 정밀도, 공간 Z/M, money=숫자 → §6.1 규칙 수정, C9 반영 | DB 실측(컨테이너) | [SP6.md](SP6.md) |
| SP10 | 트랜잭션 안 EXECUTE AS/REVERT, 소유권 체인 | 성립(2026-11-10). doomed면 ROLLBACK 뒤 REVERT | DB 실측(컨테이너) | [SP10.md](SP10.md) |
| SP7 | 열린 트랜잭션 안 최종 차이(정확성, AW 전 테이블 시간) | 통과(2026-11-10, 2회): 독립 확인 10/10, p50 1.55~1.65초·p95 2.03~3.70초 | DB 실측(컨테이너) | [SP7.md](SP7.md) |
| SP8 | AW DM1·DM2·DM3 실제 전파, 데모 대본 확정 | 확정(2026-11-12): DM1 주문 43697, DM2 상세 52, DM3 직원 1. DM2의 SalesYTD는 회계 기간 불일치로 늘 0행 | DB 실측(AW_Origin 읽기 전용 조회 + 전용 사본에서 Host 끝까지) | [SP8.md](SP8.md) |
| SP9 | 브라우저 렌더 시간(그래프 노드 50·200·500, 그리드 1만·5만 행) | 측정 전(참고값 1회) | 브라우저(측정 화면 `/sp9`) | [SP9.md](SP9.md) |
| SP11 | 트랜잭션 제어 프로시저와 실행 봉투(균형 BEGIN/COMMIT, 남는 COMMIT, CATCH ROLLBACK 뒤 266, SAVE TRAN) | 준비됨, 미실행(2026-11-13) | 정적 검토만(ScriptDom TSql170 파싱 오류 0) | [SP11.md](SP11.md) |
| SP12 | 깊이 신호(`TRIGGER_NESTLEVEL()`과 `@@NESTLEVEL`, 프로시저 중첩 안 트리거 깊이) | 준비됨, 미실행(2026-11-13) | 정적 검토만 | [SP12.md](SP12.md) |
| SP13 | `EXEC @rc` 래퍼의 반환값·행 수·결과 집합 수 | 준비됨, 미실행(2026-11-13) | 정적 검토(SQL 파싱 오류 0), PowerShell 클라이언트는 구문 검사만 | [SP13.md](SP13.md) |
| SP14 | 실행 사용자 `dbr_preview_user`와 프로시저(EXECUTE 권한, 동적 SQL 229, 3부분 이름 916) | 준비됨, 미실행(2026-11-13) | 정적 검토만 | [SP14.md](SP14.md) |
| SP16 | `@@TRANCOUNT` 분기 프로시저: 봉투 안 대 자동 커밋 단독 | 준비됨, 미실행(2026-11-13) | 정적 검토만 | [SP16.md](SP16.md) |
| SP17 | Split 끝까지: 원본 서버(2022, 제한 로그인) → 로컬 Docker 샌드박스(2025) | 준비됨, 미실행(2026-11-13) | 정적 검토만(SQL 파서, PowerShell 5.1 파서, compose) | [SP17.md](SP17.md) |
| SP18 | 원본 스냅샷 실패의 오류 번호(권한, 경로, 이름 충돌) | 준비됨, 미실행(2026-11-13) | 정적 검토만 | [SP18.md](SP18.md) |
| SP20 | 고아 dbo와 EXECUTE AS OWNER(Split 복원 뒤 소유자 정리) | 준비됨, 미실행(2026-11-13) | 정적 검토만 | [SP20.md](SP20.md) |
| SP21 | KEYSET_HASH(약한 검증)의 검출력과 비용 | 준비됨, 미실행(2026-11-13) | 정적 검토만 | [SP21.md](SP21.md) |

SP11~SP21은 결과 칸이 모두 비어 있다. SP15·SP19는 기록 문서가 없다(SP19는 큰 DB 시간 측정 후보로 [../home-test-plan.md](../home-test-plan.md) 4.4에만 적혀 있다). 집에서 돌리는 순서는 [../home-test-plan.md](../home-test-plan.md)다.

2026-11-06에는 작업 PC에 Docker Desktop·WSL이 없어 SP4만 실행했다. 2026-11-10에 Docker를 설치하고 나머지를 실측했다. 준비 절차는 [../dev-setup.md](../dev-setup.md)에 있다. 설계 반영은 [../architecture.md](../architecture.md) v1.3에서 "v1.3"으로 표시한 곳이다.

## 월요일 실행 순서

1. 컨테이너 준비: `dev-setup.md` 절차(설치 → `.env` → `scripts/db-up.ps1`).
2. SP0: 에디션과 스냅샷. 실패하면 2022-latest Developer로 바꾸고 D1을 고친다.
3. AdventureWorks 복원(`scripts/fetch-adventureworks.ps1`, `scripts/restore-aw-origin.ps1`) 뒤 SP5.
4. SP6, SP1, SP3, SP10, SP2 순서로 실행하고 결과를 각 `SPn.md`에 채운다. 세부 순서는 각 `SPn.md`에 있다. `sp2_10`·`sp3_10`·`sp10_10`은 `-- PART 2` 주석 앞뒤 사이에 `common_audit_store.sql`·`common_hand_audit.sql`을 설치해야 하므로 통째로 실행하지 않는다.
   - 2026-11-10: SP3 AW 판(`sp3_20_aw.sql`)과 SP10 AW 판(`sp10_20_aw.sql`)을 추가했다. 스크립트는 `run-sql.ps1`(sqlcmd `-I`, QUOTED_IDENTIFIER ON)으로 실행한다.
5. 깨진 전제는 설계 §7.1 "깨지면" 규칙대로 `docs/architecture.md`에 반영하고 v1.3을 확정한다.
