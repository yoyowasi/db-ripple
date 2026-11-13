# 집에서 DB 테스트 계획 (순서대로 따라 하는 체크리스트)

**상태: 준비됨, 전부 미실행(2026-11-13).** 오늘 구현했지만 DB 없이는 확인하지 못한 것을 집에서 Docker를 켜고 한 번에 확인하기 위한 문서다. 스크립트·SQL은 정적 검토만 했다(PowerShell 5.1 파서 오류 0, SQL ScriptDom TSql170 파싱 오류 0, compose YAML 구문). 아래 "결과" 칸은 모두 비어 있고, 실행하지 않은 것은 실행하지 않았다고 적는다.

**2026-11-13 갱신:** EXEC 미리보기 구현이 합쳐졌다(미리보기 전용, [적용]은 막힘). 그래서 자동 단계를 한 번 더 `-IncludeExec`로 돌린다(2절 "실행 순서"). CL25의 규칙 이름은 확정됐다. 오늘 확인한 범위는 정적 검사와 단위 테스트뿐이다(빌드 경고 0, 테스트 910 통과, DB 테스트 건너뜀).

**2026-11-13 저녁 추가(DB 미확인):** 카탈로그가 사용자 함수·동의어·뷰·테이블 계산 열/제약의 함수 의존을 새로 읽는다(EXEC 본문의 함수 판정용). **예전에 만든 기준선(매니페스트)에는 이 정보가 없어서**, 함수를 부르는 프로시저는 계속 거부되고 뷰·제약 안은 판정하지 않는다. 집에서는 샌드박스를 **새로 만든 뒤** 확인한다(설정 화면의 [샌드박스 새로 고침] 또는 기준선 다시 만들기). 자동 단계의 Tests에 AW 함수 목록을 읽는 DB 테스트(`CatalogTests.Reads_adventureworks_function_modules`)가 들어 있다. 평가 단계는 대조군 B1(FK 메타데이터 정적 예측)도 돌린다(이전 결과에 없는 시스템이라 비교표에는 NEW로만 남는다). 단위 테스트는 1017 통과.

결과는 `logs\home-test\<날짜>\`에 모은다(자동 단계가 만든다. 수동 단계의 로그는 `sp11\`, `sp17\` 같은 하위 폴더에 옮겨 둔다). 공개 샘플 DB(AdventureWorks, CascadeLab)만 쓴다. 비밀값(`.env`의 비밀번호·연결 문자열)은 어떤 기록에도 적지 않는다.

## 0. 무엇을 확인하나 (배경 항목 ↔ 단계)

| 확인할 것 | 무엇을 확인하나 | 단계 |
|---|---|---|
| N1 도달 폐포 거부 | CascadeLab 30사례·AW 시연 SQL의 거부 목록이 바뀌지 않았는지(특히 동적 SQL 트리거 CL10) | 2절 N1, 3.1 CL21~25 |
| 환경 함수를 비결정 열로 분류 | 평가 M1~M4가 이전처럼 1.0인지 | 2절 Eval |
| EXEC 미리보기(분석·평가) | CL21~CL25(EXEC·거부 사례)가 기대대로 분석·실행되는지. `PROC_DIRECT` 귀속, 반환값, 롤백 분류, 거부 규칙 이름 | 2절 `-IncludeExec`, 3.1 |
| 기존 DB 테스트·SingleServer | DB 테스트(약 58개)가 통과하는지 | 2절 Tests |
| Split 모드 | 2022 원본 → 로컬 2025 샌드박스가 끝까지 도는지, 원본 DDL 0, 권한 오류 | 4.2 SP17 |
| 원본 스냅샷 실패 | 실패 사유별 오류 번호 | 4.2 SP18 |
| 고아 dbo | 다른 로그인 소유 DB 복원 뒤 EXECUTE AS OWNER | 4.2 SP20 |
| 약한 검증(KEYSET_HASH) | 검출력·동시 쓰기·트랜잭션 안 시간 | 4.2 SP21 |
| EXEC 실행 봉투 | 트랜잭션 제어 프로시저 | 4.1 SP11 |
| EXEC 깊이 신호 | 프로시저 중첩과 트리거 깊이 | 4.1 SP12 |
| EXEC 반환값·결과 집합 | `EXEC @rc` 래퍼 | 4.1 SP13 |
| 실행 사용자 권한 | `dbr_preview_user` + EXECUTE, 동적 SQL 229, 3부분 이름 916 | 4.1 SP14 |
| `@@TRANCOUNT` 분기 | 미리보기 대 실제 최종 차이 | 4.1 SP16 |
| 실행 파일 | 게시본 실제 SQL Server 연결·화면 | 4.3 |
| 큰 DELETE 성능·SP19 | 선택 | 4.4 |

## 1. 준비

- [ ] Docker Desktop 실행. 컨테이너 둘(샌드박스 6GB, 원본 모사 3GB 상한)을 쓰므로 Docker Desktop 메모리를 10GB 이상으로.
- [ ] 저장소 최신(`git pull`, main이 2026-11-13 마지막 커밋 이후인지 확인. Codex로 진행하면 `docs/codex-home-prompt.md`의 프롬프트를 붙여 넣는다).
- [ ] `.env` 존재, `MSSQL_SA_PASSWORD` 채움(`.env.example` 참고). 비밀번호에 `$`를 쓰지 않는다.
- [ ] AdventureWorks 준비(N1 AW, SP14 AW 판, SP21 D에 필요):
  ```powershell
  .\scripts\fetch-adventureworks.ps1          # .data\backup\AdventureWorks2025.bak (이미 있으면 생략)
  .\scripts\db-up.ps1                          # 샌드박스 컨테이너 기동·healthy 대기
  .\scripts\restore-aw-origin.ps1              # AW_Origin 복원 + dbr_preview_user
  ```
- [ ] 결과 폴더: `$d = Get-Date -Format yyyy-MM-dd; New-Item -ItemType Directory -Force logs\home-test\$d`
- 통과 기준: `docker compose ps`에 `sql`이 healthy, `.\scripts\run-sql.ps1`로 AW_Origin 조회 가능.
- 실패 시: `docs/dev-setup.md`, `docker compose logs --tail 30 sql`.

## 2. 자동 단계: `scripts\home-test.ps1`

```powershell
.\scripts\home-test.ps1                    # 전체(약 20~40분)
.\scripts\home-test.ps1 -Skip Eval,N1      # 단계 건너뛰기: Docker, Env, Build, Tests, Eval, N1
.\scripts\home-test.ps1 -EvalRepeat 20     # 평가 반복 20회(이전 실행과 같은 조건)
.\scripts\home-test.ps1 -N1Targets AW      # N1 대상만: AW, CascadeLab, CascadeLab_RT, CascadeLab_Reach
.\scripts\home-test.ps1 -IncludeExec       # EXEC 포함(합쳐졌음): Eval에 CL21~CL25, N1에 EXEC 문장과 expected.rejects 전부
```

실패해도 다음 단계로 가며 요약에 표시한다. 종료 코드 1 = FAIL이 하나 이상.

| 단계 | 하는 일 | 통과 기준 | 실패 시 볼 곳 |
|---|---|---|---|
| Docker | Docker·컨테이너 확인(필요하면 `db-up.ps1`), AW_Origin 유무 | 컨테이너 healthy | `summary.md`, `docker compose logs sql` |
| Env | `env.ps1`(SingleServer)로 연결 설정·연결 시험 | 연결 확인 | `.env` |
| Build | `dotnet build DbRipple.slnx` | 오류 0 | `dotnet-build.log` |
| Tests | `dotnet test DbRipple.slnx`(DB 테스트 포함), TRX로 집계 | 실패 0, 건너뜀이 0 근처(DB 테스트가 돎) | `failed-tests.txt`, `dotnet-test.log`, `trx\` |
| Eval | CascadeLab 빠른 평가, 이전 실행(`logs\eval\eval-20261112-143835`)과 비교. 기본은 CL01~CL20(30사례, EXEC·거부 사례 제외), `-IncludeExec`면 CL21~CL25를 더하고 이때 결과는 WARN(사례 집합이 달라 지표 비교는 참고용) | 미리보기 거부 사례 0, 이전과 다른 지표 0(M1 total 1.0000, M2 ALL 1.0000, M3 certain 과신 0, M4 정규 서명 안정 1.0000) | `eval\eval-quick\`(`cases.csv`, `differences.txt`), `eval-vs-baseline.csv`의 DIFF 줄 |
| N1 | 임시 Host(127.0.0.1:5081, `--dev` 개발 모드)로 `/api/analyze`: AW DM1·DM2·DM3·DM7, CascadeLab 사례 문장, CL25d. 결과 JSON 저장 후 기대 거부와 비교. 기본은 첫 EXEC 문장 앞까지만, `-IncludeExec`면 EXEC 문장까지 전부 분석하고 `expected.rejects`를 규칙 ID까지 비교 | 모든 대상 "일치"(AW·CascadeLab 일반 사례는 거부 0. 기본 실행의 CL25d는 문장 1만 `REJECT_TRIGGER_EXTERNAL_EFFECT`, `-IncludeExec`에서는 CL25a~c와 CL25d 문장 1·2가 아래 3.1의 규칙으로 거부) | `n1\<대상>\verdict.txt`, `<사례>.json`, `host.out.log` |

기록할 것: `summary.md`(자동), 이전과 다른 지표가 있으면 그 줄과 원인. 확인하지 못한 단계는 SKIP으로 남는다.

N1에서 거부가 새로 생기면 두 가지를 구분해 적는다: 이번 변경(도달 폐포, 환경 함수 분류)으로 새로 생긴 거부인지, 그 전부터 있던 거부인지. CL10(동적 SQL 트리거)은 "모든 테이블에 닿는다"로 넓어져 DB 안 다른 트리거가 판정 대상이 되므로, CascadeLab에 외부 부작용·트랜잭션 제어 트리거가 하나라도 있으면 CL10이 거부로 바뀐다(그래서 CL25d는 별도 DB `CascadeLab_Reach`에 둔다).

**실행 순서(EXEC가 합쳐진 뒤):**
1. `.\scripts\home-test.ps1` — 기본 실행. EXEC·거부 사례를 뺀 CL01~CL20이 이전 결과(M1~M4 1.0)와 같은지 본다.
2. `.\scripts\home-test.ps1 -IncludeExec -Skip Tests -Date <오늘날짜>-exec` — EXEC 포함 실행. 테스트는 1에서 돌렸으므로 건너뛴다. 날짜 이름을 바꾸는 까닭: Eval은 같은 날짜 폴더의 `eval\eval-quick`을 지우고 다시 만들고, N1도 `n1\` 결과를 덮어쓰기 때문이다.

선택 반복(결과 보강): `-EvalRepeat 20`(이전과 같은 20회), M5: `. .\scripts\env.ps1; dotnet run --project eval\DbRipple.Eval.Cli -c Release -- m5 --out logs\home-test\<날짜>\eval-m5`(큰 N은 오래 걸린다).

## 3. 수동 단계 전에 확인

### 3.1 CL21~CL25 스키마를 DB에 처음 실행

자동 N1 단계가 CascadeLab DB를 새로 만들 때 `cl01~cl25.sql`을 실행한다. 오류가 있으면 `logs\home-test\cl-cl2x.log`에서 확인한다.
- 무엇을 확인하나: 오늘 작성한 CL21~CL25 스키마(CL21~CL25는 한 번도 DB에서 실행한 적이 없다)가 SQL Server 2025에서 만들어지는지. 특히 `cl25.usp_CrossDb`(없는 3부분 이름의 지연 해석)와 `cl24`의 CHECK.
- 통과 기준: `cl-cl21.log`~`cl-cl25.log` 첫 줄이 `exit=0`.
- 이어서 `-IncludeExec`를 돌린다(EXEC 지원이 합쳐졌다). CL25 규칙 이름은 확정됐다: CL25a `REJECT_PROC_DYNAMIC_SQL`, CL25b `REJECT_PROC_UNBALANCED_COMMIT`, CL25c `REJECT_PROC_EXTERNAL_EFFECT`(tempdb 대신 master 3부분 이름), CL25d 문장 1·2 모두 `REJECT_TRIGGER_EXTERNAL_EFFECT`(`CascadeLab_Reach`). 확인할 것: CL21~CL24는 거부 0이고, Eval에서 CL21~CL24의 `returnValue`·`isToolFault`·`PROC_DIRECT` 간선이 손 정답과 같은지(`differences.txt`, 불일치는 정답 오류인지 제품 결함인지 구분해 적는다). CL25a~c는 Eval에서 오라클 없이 규칙 ID만 비교하고(`rejects.json`), CL25d는 Eval이 건너뛰므로 N1로만 확인한다.

## 4. 수동 단계 (순서)

각 스파이크는 `docs/spikes/SPnn.md`의 "실행 순서"를 따른다. 로그는 `spikes\logs\`에 생기므로 끝나면 `logs\home-test\<날짜>\spNN\`로 복사한다.

### 4.1 EXEC 스파이크(샌드박스 컨테이너만 필요)

| 순서 | 스파이크 | 명령(요약) | 통과 기준 | 실패 시 |
|---|---|---|---|---|
| 1 | SP11 트랜잭션 제어 프로시저·봉투 | `run-sql.ps1 spikes\sql\sp11_10_run.sql -Part 1/2` | 15 시나리오 기록, 판정 H1~H6 PASS 또는 실측으로 정정(남는 COMMIT은 봉투 1겹에서 변경이 롤백 뒤 남음, CATCH ROLLBACK은 266) | SP11.md "실패 시 볼 곳" |
| 2 | SP12 깊이 신호 | `sp12_10_run.sql` | H1~H4(트리거 깊이는 프로시저 중첩 무관, `@@NESTLEVEL − TRIGGER_NESTLEVEL()` = 프로시저 중첩 수) | `nested triggers` 서버 옵션 |
| 3 | SP16 `@@TRANCOUNT` 분기 | `sp16_10_run.sql` | `p_own_tran_if_none` SAME, `p_branch_data`·`p_requires_tran` DIFF | SP16.md |
| 4 | SP13 `EXEC @rc` 래퍼 | `sp13_10_setup.sql` → `. .\scripts\env.ps1; .\spikes\sql\sp13_20_client.ps1` | 36행 기록, 반환값·결과 집합 수 질문 Q1~Q4 답 확정 | 연결 환경 변수 |
| 5 | SP14 `dbr_preview_user` | `restore-aw-origin.ps1 -DbName AW_SpikeB` → `sp14_10_aw.sql` → `sp14_20_mini.sql` | AW H1~H3, 미니 H1~H5 | SP14.md(모듈 정의 확인) |

끝에 정리: `.\scripts\run-sql.ps1 spikes\sql\sp_exec_99_drop.sql`.
- 무엇을 확인하나: EXEC 미리보기의 전제(봉투 안 프로시저의 트랜잭션 동작, 깊이, 반환값, 권한, 미리보기와 실제의 차이).
- 결과가 가정과 다르면 고칠 곳: architecture.md D5 "EXEC 미리보기" 결정의 "SP 뒤 재검토" 세 가지다. SP12는 프로시저 안 DML의 감사 트리거 깊이를 1로 보는 `PROC_DIRECT` 가정, SP14는 266과 XACT_ABORT ON에서의 프로시저 ROLLBACK 판정, SP16은 `@@TRANCOUNT` 분기 거부다. 고치기 전에 CL21~CL24 손 정답이 이 가정에 기대는지 같이 본다.
- 기록할 것: 각 SPnn.md "결과" 표, 판정 표 PASS/FAIL, 추정과 다른 값.

### 4.2 Split 스파이크(원본 모사 컨테이너 필요)

순서와 명령 전체는 `docs/spikes/SP17.md`. 요약: `.env`에 `ORIGIN_SQL_*` 추가 → `docker compose --profile origin`으로 `sql-origin`(2022, 127.0.0.1:14334) 기동 → 원본에 CascadeLab 생성 → 제한 로그인 `dbr_origin_user`(db_backupoperator + DML만) 생성 → 지문(전) → 권한 시험 → Split Host → 기준선 → 미리보기 → [적용] → 지문(후).

| 순서 | 스파이크 | 통과 기준(요약) | 기록할 것 |
|---|---|---|---|
| 6 | SP17 Split 끝까지 | 기준선 ready(mode Split), 미리보기 ok, [적용] 동의 없이 막힘·동의 후 결과, **원본 `ddl_` 줄 전후 동일**, RESTORE·DDL은 권한 오류 | SP17.md 결과 표, 지문 전·후 로그, `fingerprintWarnings` |
| 7 | SP18 원본 스냅샷 실패 오류 번호 | s3 성공, 나머지 번호 기록(예측 262·5133·1801·911) | SP18.md 표 |
| 8 | SP20 고아 dbo·EXECUTE AS OWNER | 조치 뒤 모듈 오류 없음(H1), 조치 전 소유자·오류 번호 기록 | SP20.md 표 |
| 9 | SP21 KEYSET_HASH | A: H1~H3, B: `OUTSIDE_TABLE_CHANGED`로 막고 원본 불변, C: 동시 쓰기 시 롤백 쪽 실패, D: 해시 시간 | SP21.md 표 |

- 무엇을 확인하나: SP17 = Split 경로 전체와 원본 최소 권한, SP18 = 스냅샷 불가 사유 분류, SP20 = 복원 뒤 소유자 정리, SP21 = 스냅샷 없이 하는 검증의 한계.
- 정리: `docker compose --profile origin stop sql-origin`, SP21 `sp21_23_outside_trigger_drop.sql`, SP20 `sp20_99_drop.sql`(양쪽).

### 4.3 실행 파일(게시본)

```powershell
.\scripts\publish.ps1                       # web\node_modules가 없으면 직접 npm 설치 후 다시 실행(스크립트는 설치하지 않는다)
cd artifacts\db-ripple                      # README-실행.txt, .env.example, compose.yaml 포함
mkdir $env:LOCALAPPDATA\db-ripple\import -Force
# .env를 만들고(MSSQL_SA_PASSWORD) 이 폴더의 compose.yaml로 샌드박스를 띄운다: docker compose up -d
.\DbRipple.Host.exe
```
확인 항목(브라우저가 자동으로 열린다):
- [ ] 첫 실행 설정 화면이 뜬다(연결 설정 없음).
- [ ] 원본·샌드박스 연결을 입력하고 [연결 시험]이 성공한다(SingleServer 시험이면 둘 다 127.0.0.1,14333, sa). 설정 화면의 연결 모드 기본 선택은 "분리(split)"이므로 한 서버로 시험할 때는 "한 서버"를 고른다.
- [ ] 저장 뒤 기준선 구축(상태가 building → ready), 기준 시각이 보인다.
- [ ] 미리보기(예: AW DM1)가 그래프·변경 행으로 렌더된다. 브라우저 콘솔에 CSP 위반 없음(개발자 도구).
- [ ] [적용]: DB 이름 입력 + 약한 검증 동의(해당 경우)를 거쳐 실행된다.
- [ ] 일회용 토큰: 실행 주소(`?launch=…`)를 새 탭에 다시 붙여 넣으면 거부된다. 쿠키 없이 API 직접 호출이 거부된다.
- [ ] 요청 키: 개발자 도구에서 세션 쿠키 이름이 `dbr_session_<포트>`이다. 쿠키만 보내고 헤더 `X-DbRipple-Request`를 빼거나 값을 틀리게 한 `/api` 호출(GET 포함)은 401이다. `/openapi`는 404다(개발 모드에서만 열린다).
- 무엇을 확인하나: 게시본이 실제 SQL Server와 붙어 화면까지 도는지, 보안(쿠키·CSP)이 동작하는지.
- 통과 기준: 위 7항목. 기록할 것: 항목별 O/X, 화면 캡처(비밀값 가림), Host 콘솔 로그(연결 문자열 없음 확인).
- 실패 시: Host 콘솔 마지막 오류, `%LOCALAPPDATA%\db-ripple\profile.json` 존재(비밀번호는 `profile.secret`), `README-실행.txt`.

### 4.4 선택

- 큰 DELETE 성능: `scripts\demo\dm6-generate.sql`로 cl20을 5만~10만 행으로 키운 뒤 DM6(`demo.http`)·M5 `--sizes 100000`(이전 105초). 개선 여부 기록.
- SP19(큰 DB 시간)·SP9(브라우저 렌더: 창이 보이는 상태에서 크기마다 3회): 각 SP 문서 방법을 따른다.

## 5. 결과 요약 양식

| 단계 | 결과(PASS/FAIL/WARN/미실행) | 증거 범위 | 메모(새로 생긴 문제 / 전부터 있던 문제) |
|---|---|---|---|
| Docker·Env·Build·Tests | | DB 실측 | |
| Eval M1~M4 | | DB 실측 | |
| N1 AW / CascadeLab / RT / Reach | | API+DB | |
| EXEC 평가·분석(`-IncludeExec`) | | DB 실측 + API | |
| SP11·SP12·SP16·SP13·SP14 | | DB 실측 | |
| SP17·SP18·SP20·SP21 | | DB 실측(컨테이너 2대) | |
| 실행 파일 | | 브라우저+DB | |
| 큰 DELETE·SP19·SP9(선택) | | | |

실패한 항목은 같은 방법을 되풀이하지 말고 실패한 지점부터 다시 본다. 스크립트 실패와 제품 결함은 구분해 적는다(환경 실패: 컨테이너·권한·경로, 제품 결함: 판정 FAIL이 설계 가정을 깨는 경우).

## 6. 정리

```powershell
$env:COMPOSE_PROFILES = 'origin'; docker compose stop sql-origin    # 원본 모사 컨테이너
docker compose stop                                                  # 샌드박스
```
남은 임시물: `dbrt_eval_*` DB(평가가 지움), `AW_SpikeB`(남김), `.data\origin-backup\*.bak`·`.data\import\*.bak`(직접 삭제), 호스트 `logs\home-test\`(커밋하지 않는다).
