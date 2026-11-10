# SP1 — 전제 H1~H5 (트리거 깊이·순서)

- **목적:** §7.1 H1~H5가 SQL Server 2025에서 성립하는지 감사 헤더의 `nestLevel`·`captureSeq`로 확인한다.
- **결과:** H1~H5 모두 **성립**(2026-11-10 실측). '깨지면' 규칙을 적용할 전제는 없다. 다만 H3은 3회 관측값이고 문서가 보장하는 순서는 아니다.
- **증거 범위:** DB 실측(컨테이너 dbripple-sql, 2026-11-10). SQL Server 2025 RTM-CU10(17.0.5035.5) Enterprise Developer Edition, Linux. SpikeLab_SP1 미니 스키마(RECURSIVE_TRIGGERS OFF, nested triggers 1, 호환성 170)와 AW_SpikeA(AW 2025 복사본, RECURSIVE_TRIGGERS OFF)의 SalesOrderHeader 1건 DELETE. 실험은 모두 ROLLBACK했고 SpikeLab_SP1은 끝에 지웠다.

## 방법

| 순서 | 스크립트 | DB | 로그(`spikes/logs/`) |
|---|---|---|---|
| 1 | `spikes/sql/sp1_00_create_db.sql` | master (SpikeLab_SP1 생성, RECURSIVE_TRIGGERS OFF) | `sp1-00-create-db.log` |
| 2 | `spikes/sql/common_audit_store.sql` | SpikeLab_SP1 | `sp1-01-audit-store.log` |
| 3 | `spikes/sql/common_hand_audit.sql` | SpikeLab_SP1 (손 감사 트리거 I/U/D 본문 + 순서 뷰) | `sp1-02-hand-audit.log` |
| 4 | `spikes/sql/sp1_10_schema.sql` | SpikeLab_SP1: CL06·CL07·CL09·CL13 미니 스키마, 사용자 트리거, 감사 트리거 | `sp1-10-schema.log` |
| 5 | `spikes/sql/sp1_20_run.sql` | SpikeLab_SP1: 실험 4개. 각 실험은 BEGIN TRAN → 경계 → DML → 헤더 수집 → ROLLBACK → 판정 표. CL13은 스크립트 안에서 3회 반복 | `sp1-20-run.log` (QI OFF 1차: `sp1-qioff-20-run.log`) |
| 6 | `spikes/sql/sp1_30_aw_soh_delete.sql` | AW_SpikeA. 감사 저장소·손 감사 트리거 4개를 트랜잭션 안에서 만들고 ROLLBACK | `sp1-30-aw.log` (1차 실패: `sp1-30-aw-try1.log`) |
| 7 | `spikes/sql/sp1_99_drop.sql` | master | `sp1-99-drop.log` |

- 실행: `.\scripts\run-sql.ps1 <파일> -Database <DB> -Log <로그>`(sqlcmd `-b -W`, 열 구분자는 공백, 09:11 재실행부터 `-I`). 의도한 오류를 관찰하는 단계가 없어 `-ContinueOnError`는 쓰지 않았다.
- 로그 첫 줄은 모두 `exit=0`이다. 예외는 1차 실패 로그 `sp1-30-aw-try1.log`(`exit=1`)뿐이다.
- 판정 표의 `verdict`가 `HOLDS`/`BROKEN`을 바로 낸다.

**고친 것(2026-11-10)**

- `sp1_30`: 대상 DB를 `AW_Sandbox`에서 `AW_SpikeA`로 바꿨다. AW_Sandbox는 backend-3 전용이다.
- `sp1_30`: 첫 배치에 `SET QUOTED_IDENTIFIER ON`을 넣었다.
  - 원인: sqlcmd 세션 기본값이 OFF다(@@OPTIONS 5688, `logs/week1/env-evidence.log`에 2026-11-10 기록. `-I`면 5944, SqlClient는 5432). SOH에는 계산 열 인덱스가 있어서 1차 실행의 DELETE가 1934로 실패했다(`sp1-30-aw-try1.log` 29~30행).
  - 처리: 1차 실행의 트랜잭션은 XACT_ABORT로 롤백됐다. AW_SpikeA에 감사 스키마·트리거가 남지 않았음을 확인한 뒤 다시 실행했다.
- `sp1_20`·`sp1_30`: 한글 라벨 리터럴(`'참고'` 등)에 `N`을 붙였다. 서버 정렬(SQL_Latin1_General_CP1_CI_AS)에서는 varchar 리터럴의 한글이 `?`로 바뀐다. 판정식은 바꾸지 않았다.
- QI ON 재실행(09:11): run-sql.ps1에 `-I`(QUOTED_IDENTIFIER ON)가 들어간 뒤 1~7단계를 처음부터 다시 돌렸다.
  - 이유: 1차(08:58)의 미니 트리거·프로시저·뷰는 OFF로 저장됐다.
  - 확인 쿼리를 `sp1_10`·`sp1_30` 끝에 넣었다. `sys.sql_modules`에서 `uses_quoted_identifier = 0`인 모듈은 0행이다(`sp1-10-schema.log` 58~64행: 25개 중 0, `sp1-30-aw.log` 29~33행: 56개 중 0).
  - 결과: 판정과 값은 OFF 1차(`sp1-qioff-20-run.log`)와 첫 줄만 빼고 같다.
  - 아래 줄 번호는 재실행 로그 기준이다.

## 가설별 예상값과 판정 기준

| 실험 | 문장 | 예상(성립) | 깨짐으로 보는 것 |
|---|---|---|---|
| CL06 | `DELETE cl06.P WHERE Id=1` (C, G CASCADE, C의 사용자 트리거가 Log INSERT) | **H2:** P·C·G 헤더 `nestLevel=1`, Log `nestLevel=2`. **H1:** seq(Log) < seq(C). 참고: G<C<P면 "연쇄의 반대 순서" 관찰 | 연쇄 대상(C, G)의 깊이 ≠ 1이거나 Log ≠ 2 → H2 깨짐. Log가 C 감사 뒤 → H1 깨짐 |
| CL07 | `INSERT cl07.A VALUES (1),(2)` (A→B,D, B→C) | **H2:** A=1, B=2, D=2, C=3. **H1:** seq(C)<seq(B)<seq(A), seq(D)<seq(A), C는 B와 D 사이에 오지 않는다 | 자식 감사가 부모 감사 뒤에 오거나 C가 B 묶음 밖(B 뒤) → H1 깨짐 |
| CL09 | `DELETE cl09.P WHERE Id=1` (1차 K{40,42}·M{20}, trg_M_D가 Q{30} 삭제 → 2차 K{41}) | **H4:** K 묶음 2개, 같은 키가 두 묶음에 없음. **H5:** Q와 K{41}이 `nestLevel=2`이고 seq < seq(M). **H2:** 1차 K, M, P는 1 | K{41}·Q가 M 감사 뒤(특히 P 뒤 = 문서 문구대로 "모든 1차 트리거 뒤") → H5 깨짐. 같은 K 키 중복 → H4 깨짐 |
| CL13 | `DELETE cl13.T WHERE Id=1`, 3회 반복 (T: 사용자 First·중간·Last, 감사 NONE, TC CASCADE+트리거) | **H3:** 매 회차 TC 그룹(trg_TC_1 Log + TC 감사)과 T 그룹(trg_T_1~3 Log + T 감사)이 섞이지 않음. T 감사 `auditOrder=NONE`. **H1:** TC 감사(Last)가 trg_TC_1 Log 뒤 | 그룹이 섞이면 H3 깨짐. 참고 칸: T 감사가 trg_T_3 Log보다 먼저인 회차 수(>0이면 NONE 표시가 필요한 근거) |
| AW | `DELETE Sales.SalesOrderHeader` 1건(상세·사유 있는 첫 주문) | **H2:** D 헤더(SOH, SOD, SOHSR) 모두 `nestLevel=1`. **H1:** iduSalesOrderDetail이 일으킨 SOH UPDATE 헤더(깊이 2, 0행 예상)가 SOH D 감사보다 먼저 | 위와 같음. 참고: SOH U 0행 발동이 기록되는지(A3), 첫 결과 집합으로 FK가 실제 CASCADE인지 먼저 본다 |

## 결과

### 판정

로그 줄 번호는 `spikes/logs/sp1-20-run.log`(미니)와 `spikes/logs/sp1-30-aw.log`(AW) 기준이다.

| 전제 | 판정 | 근거 |
|---|---|---|
| H1 | **성립** | CL06 Log(seq 2) < C 감사(seq 3): 6~7, 12행 `HOLDS`. CL07 C(5) < B(6) < D(7) < A(8): 17~20, 24~25행. CL09 K{41}(10)·Q(11) < M 감사(12): 30~32행. CL13 trg_TC_1 Log < TC 감사, 3회 모두: 42~43·48~49·54~55, 63행. AW iduSalesOrderDetail이 일으킨 SOH U(seq 2) < SOD 감사(seq 3) < SOH 감사(seq 4): AW 43~45, 49행 |
| H2 | **성립** | 문장 대상과 그 FK 연쇄 대상은 깊이 1이다. CL06 G·C·P(5, 7, 8행), CL09 K{40,42}·M·P(29, 32, 33행), AW SOHSR·SOD·SOH D(AW 42, 44, 45, 48행). 트리거 안 DML은 1 크다. CL06 Log 2(6행), CL07 B·D 2, C 3(17~19, 23행), AW SOH U 2(AW 43행). 트리거 안 DML이 일으킨 FK 연쇄 K{41}도 그 DML 대상 Q와 같은 깊이 2다(30~31, 37행) |
| H3 | **성립**(관측 3/3회) | CL13 3회 모두 TC 묶음(seq 14~15, 20~21, 26~27)이 끝난 뒤 T 묶음(16~19, 22~25, 28~31)이 왔다: 42~59행, 62행 `HOLDS`. 보조로 CL06 C 묶음(6~7행), CL09 M 묶음(30~32행), AW SOD 묶음(AW 43~44행)도 다른 테이블 묶음과 섞이지 않았다 |
| H4 | **성립** | CL09 K 묶음은 2개({42,40} 29행, {41} 30행)이고 같은 키가 없다: 36행 `HOLDS`. 단서도 관측했다. 같은 문장 안에서 K에 두 루트(P의 1차 체인, Q의 2차 체인)로 닿았다 |
| H5 | **성립** | CL09 2차 체인 Q(seq 11)·K{41}(seq 10)이 `nestLevel=2`이고 M 감사(seq 12)보다 먼저다: 30~32행, 37행 `HOLDS`. 2차 체인은 trg_M_D 안 DELETE가 끝나기 전에 처리됐다(39행 `INSIDE`). 문서 문구("모든 1차 체인의 트리거가 발동한 뒤")를 지연 처리로 읽으면 관측과 맞지 않는다 |

### 확인한 사실(추가 관측)

- **A1 "연쇄의 반대 순서":** 세 경우 모두 연쇄 대상의 트리거가 먼저 돌았고, 직접 대상의 트리거가 마지막이었다. 보장 문구가 아니므로 관측값으로만 둔다.
  - CL06: G(1) < C(3) < P(4), 13행 `OBSERVED`.
  - CL13: 3회 모두 TC → T.
  - AW: SOHSR(1) → SOD(3) → SOH(4), AW 42~45행.
- **체인·형제 순서(관측값):** CL09는 K 체인(9) → M 체인(10~12) → P(13), AW는 SOHSR → SOD였다. CL07 형제는 B → D로 트리거 본문 순서와 같았다(26행).
- **CL13 NONE:** T 감사(NONE)는 3회 모두 trg_T_1(First) 바로 뒤, trg_T_2·trg_T_3 앞에서 돌았다(44~47·50~53·56~59행, 65행 `3`). 그래서 trg_T_2·trg_T_3의 Log 묶음(깊이 2)은 T 감사(깊이 1) **뒤**에 기록되고, 그 문장 안에서 그 뒤에 오는 깊이 1 묶음은 없다.
- **A3(0행 발동):** 트리거 안 UPDATE가 0행이어도 헤더가 남는다. SOH U, `nestLevel=2`, inserted 0·deleted 0(AW 43, 50행).
  - 연쇄로 발동한 iduSalesOrderDetail은 첫머리의 `IF @Count = 0 RETURN`을 지나갔다. 즉 연쇄로 발동한 트리거의 시작 @@ROWCOUNT는 0이 아니다(정확한 값은 미확인).
- **`tranCount`:** 깊이와 상관없이 2였다(바깥 BEGIN TRAN 1 + 트리거 1, 깊이 1~3, 5~33행).
- **CREATE TRIGGER 롤백:** 트랜잭션 안에서 만든 감사 저장소와 트리거는 ROLLBACK으로 사라졌다(AW 53행: `@@TRANCOUNT` 0, 트리거 OBJECT_ID NULL). §2 A16의 미확인 항목이다.
- **DDL 트리거 기록:** AW의 DDL 트리거 ddlDatabaseTriggerLog는 `sp_settriggerorder`도 ALTER_TRIGGER로 기록한다(AW 25~28행). 이번 실행의 기록 행은 함께 롤백됐다.
- **FK 메타데이터:** 실험 전에 SOD·SOHSR FK가 CASCADE이고 활성임을 확인했다(AW 5~6행). AW_SpikeA의 RECURSIVE_TRIGGERS는 OFF였다(AW 15행).

### 추정

- H5는 문장 단위 의미에서 나온다고 본다. 트리거 본문의 DML 문장이 끝날 때 그 문장의 연쇄와 AFTER 트리거도 모두 끝난다. 그래서 1차 체인 순서가 이번(K → M)과 달라도 결과가 같을 것으로 추정한다(미확인).

## 판정식의 한계 (2026-11-10 독립 검토 반영)

결론은 출력된 묶음 행으로 유지되지만, 판정식만으로는 다음을 가르지 못한다.

- H4: DELETE에서는 같은 키가 두 번 지워질 수 없어 "키 중복 없음" 검사가 늘 참이다. 오류 1785는 시험하지 않았다. H4는 사실상 문서 규칙에 기댄다.
- H5 참고 판정 `INSIDE`(sp1_20_run.sql:91)는 "P 감사보다 뒤가 아님"만 잰다. "trg_M_D 안의 DELETE가 끝나기 전"이라는 근거는 nestLevel 2(로그 30~31행)와 seq < M 감사(32행)이고, DELETE 종료 시점 자체는 재지 않았다.
- CL13 H1(Last)과 AW H1 판정은 NOT EXISTS라 중첩 묶음이 아예 없어도 HOLDS가 나온다. 실제 로그에는 중첩 묶음이 있어 결론은 유지된다.
- AW H2 판정은 D 묶음 개수를 보지 않는다.
- H3의 3회는 한 배치 안 WHILE 반복이다. 관측 구성은 부모·자식(CL13)과 형제 1개(CL09, K에는 사용자 트리거 없음)뿐이다. 같은 깊이의 두 테이블이 각각 사용자 트리거를 가진 형제 구성은 2주차 CascadeLab 사례로 더한다.

## 설계 반영 규칙(§7.1 '깨지면')

| 전제 | 깨지면 |
|---|---|
| H1 | 그 테이블의 트리거 문맥 자식은 `ambiguous` 이하. |
| H2 | FK 판정은 깊이와 상관없이 키 일치로 한다. 같은 문맥에 그 테이블을 쓰는 트리거가 있으면 `inferred`로 낮춘다. |
| H3 | 괄호 추론을 쓰지 않고 정적 쓰기 대상으로만 후보를 정한다(최대 `ambiguous`). |
| H4 | 문서 규칙(오류 1785). 2차 체인은 다른 루트로 같은 테이블에 닿을 수 있다 → CL09 결과로 루트 후보 수를 확인. |
| H5 | 2차 연쇄 묶음은 FK 키 일치로만 부모를 찾고, 확신도 `inferred` 이하, 순서 `observed_only`. |

## 설계 반영 제안(2026-11-10 결과 기준)

깨진 전제가 없으므로 위 '깨지면' 규칙은 적용하지 않는다. 오케스트레이터가 반영할 후보는 아래와 같다.

1. **§7.1·§13:** H1~H5 상태를 "SP1 확인(2026-11-10, 2025 CU10)"으로 바꾼다. H3은 관측값(3회)이므로 괄호 추론의 확신도 상한(`inferred`)은 그대로 둔다.
2. **§7.2 7단계:** H5가 확인됐으므로 "트리거 안 DML이 일으킨 2차 체인은 그 트리거 안에서 처리된다"를 인과 단계에 표시할 수 있다. `observed_only`를 풀지는 오케스트레이터가 정한다. 관측한 1차 체인 순서는 1가지뿐이다.
3. **§7.2 2단계 보완(CL13 관측):** 현재 규칙은 NONE 묶음 F의 자식이 "다음 형제에게 잘못 붙는" 경우만 다룬다.
   - 빠진 경우: 감사가 NONE인 테이블에서는 사용자 트리거의 중첩 묶음이 그 테이블 감사 묶음 뒤에 기록될 수 있다. 뒤에 형제가 없으면 이 묶음은 문장 끝까지 미배정으로 남고, 4단계에서 부모가 없어 `unexplained`가 된다.
   - 제안: 문장 끝까지 미배정으로 남은 깊이 d+1 묶음은 같은 문맥의 NONE 묶음을 부모 후보로 두고 `ambiguous`로 귀속한다(후보 트리거는 정적 쓰기 대상으로 표시).
   - CL13 정답 대조에 이 경우를 넣는다.
4. **§2 A16:** "트랜잭션 안의 CREATE TRIGGER는 ROLLBACK으로 되돌려진다(SP1 실측)"를 덧붙일 수 있다.
5. **실행 연결:** 계산 열 인덱스가 있는 테이블(AW SOH)은 QUOTED_IDENTIFIER OFF 세션에서 DML이 1934로 실패한다.
   - 실행기·[적용] 연결은 SqlClient 기본값(ON)을 유지하고, 환경 지문 sessionOptions에 QI 비트(256)가 있는지 확인한다.
   - 스파이크용 run-sql.ps1은 2026-11-10에 `-I`로 고쳐졌다. 다른 sqlcmd 경로를 쓰면 `SET QUOTED_IDENTIFIER ON`을 스크립트 맨 앞에 둔다.

## 미확인

- CL13 순서는 같은 세션·같은 계획에서 3회 반복한 값이다(일반화하지 않는다). NONE 감사가 First 바로 뒤에 온 이유는 모른다.
- H5는 1차 체인 순서 1가지(K → M)만 관측했다.
- 연쇄 0행 발동(SP2(d)), MERGE·INSTEAD OF, Windows 판은 이 스파이크 범위 밖이다.

## 주의

- AW 스크립트는 트랜잭션 동안 Sales 세 테이블에 스키마 잠금(Sch-M)을 잡는다. 샌드박스 전용.
- 손 감사 트리거(미니)는 `dbo.spike_InstallAudit`가 고정 본문으로 만든다. 본문 모양은 backend-2 생성기와 같다(다른 점: tableObjectId를 `OBJECT_ID(N'[s].[t]')`로 계산). 미니 트리거를 QUOTED_IDENTIFIER OFF로 만든 1차와 ON으로 만든 재실행의 결과가 같았다.
- CL13 순서는 보장되지 않는 영역이라 3회 반복값만 기록한다(일반화하지 않는다).
