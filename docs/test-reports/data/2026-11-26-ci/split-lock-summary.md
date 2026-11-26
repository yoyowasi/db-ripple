# Split 모드 EXEC [적용] 자동 확인

전체: **PASS** (시나리오 17개 중 기대와 다름 0개)

- 입력: `EXEC cl21.usp_Outer @id = 1, @delta = 5;`
- commit: 560b5d511adf0529c61a6cc0d1352e342c588312
- os: Ubuntu 26.04.1 LTS
- pwsh: 7.6.10
- originConnection: 127.0.0.1,14334, 로그인 dbr_origin_user
- dotnet: 11.0.100
- originVersion: Microsoft SQL Server 2022 (RTM-CU28) (KB5125472) - 16.0.4325.4 (X64)   Nov  4 2026 14:09:11   Copyright (C) 2022 Microsoft Corporation  Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>
- originServer: ca65cd672bec
- originCollation: SQL_Latin1_General_CP1_CI_AS
- sandboxVersion: Microsoft SQL Server 2025 (RTM-CU10) (KB5125471) - 17.0.5035.5 (X64)   Oct 15 2026 19:55:26   Copyright (C) 2025 Microsoft Corporation  Enterprise Developer Edition (64-bit) on Linux (Ubuntu 24.04.5 LTS) <X64>
- sandboxServer: 6f33663575e3
- sandboxCollation: SQL_Latin1_General_CP1_CI_AS
- 기준선(첫 구축): state ready, mode split, fingerprintMatch True, 경고 3, 불일치 0, 6초
  - 경고: ProductVersion: 원본 16.0.4325.4, 샌드박스 17.0.5035.5
  - 경고: Edition: 원본 Developer Edition (64-bit), 샌드박스 Enterprise Developer Edition (64-bit)
  - 경고: 주버전: 원본 16, 샌드박스 17

| 시나리오 | 요청 | 기대 | HTTP | status | preconditionFailures | verification.mode | mismatches | 원본 전(sa) | 원본 후(sa) | 판정 |
|---|---|---|---|---|---|---|---|---|---|---|
| S0 시작 상태 | 기준선 뒤 sa로 원본 cl21 값·dbr_origin_user의 cl21 권한 읽기 | 원본 Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0, cl21 권한 없음 |  |  |  |  | [] | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | PASS |
| S1a EXECUTE 없음, 동의 없음 | 권한: 역할 3개뿐. 새 미리보기 → [적용] {sqlHash} | HTTP 200, precondition_failed, [WEAK_VERIFICATION_NOT_ACCEPTED](검증 모드 결정이 먼저이고 실패가 있으면 프로시저 검사를 건너뜀), KEYSET_HASH, 원본 불변 | 200 | precondition_failed | WEAK_VERIFICATION_NOT_ACCEPTED | KEYSET_HASH | [] | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | PASS |
| S1b EXECUTE 없음, 동의 있음 | 같은 미리보기(막히면 소비되지 않음) → [적용] {sqlHash, allowKeysetVerification:true} | HTTP 200, precondition_failed, [PROC_DEFINITION_CHANGED, PROC_EXECUTE_PERMISSION_MISSING](권한이 없어 정의도 안 보임), KEYSET_HASH, 원본 불변 | 200 | precondition_failed | PROC_DEFINITION_CHANGED, PROC_EXECUTE_PERMISSION_MISSING | KEYSET_HASH | [] | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | PASS |
| S2 EXECUTE만(VIEW DEFINITION 없음) | sa: GRANT EXECUTE usp_Outer·usp_Inner. 새 미리보기 → [적용] {sqlHash, allowKeysetVerification:true} | HTTP 200, precondition_failed, [PROC_DEFINITION_CHANGED](정의를 못 읽음), KEYSET_HASH, 원본 불변 | 200 | precondition_failed | PROC_DEFINITION_CHANGED | KEYSET_HASH | [] | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | PASS |
| S3 VIEW DEFINITION까지, 동의 없음 | sa: GRANT VIEW DEFINITION ON SCHEMA::cl21. 새 미리보기 → [적용] {sqlHash} | HTTP 200, precondition_failed, [WEAK_VERIFICATION_NOT_ACCEPTED](원본 로그인에 CREATE DATABASE 없음), KEYSET_HASH, 원본 불변 | 200 | precondition_failed | WEAK_VERIFICATION_NOT_ACCEPTED | KEYSET_HASH | [] | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | PASS |
| S4 미리보기 뒤 원본 usp_Outer ALTER | 새 미리보기 → sa: usp_Outer 본문 앞에 주석 한 줄을 더해 ALTER → [적용] {sqlHash, allowKeysetVerification:true} → 기준선 새로 고침 | HTTP 200, precondition_failed, [PROC_DEFINITION_CHANGED], KEYSET_HASH, 원본 값 불변, 새로 고침 ready | 200 | precondition_failed | PROC_DEFINITION_CHANGED | KEYSET_HASH | [] | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | PASS |
| S5 정상 경로(동의 있음) | 원본 지문(전) → 새 미리보기 → [적용] {sqlHash, allowKeysetVerification:true} → 원본 지문(후) | HTTP 200, committed, KEYSET_HASH, mismatches [], 반환 값 0, 원본 Parent 1=15·Touch 1=1·Log outer 1행·Spare 그대로, ddl_ 지문 전후 같음 | 200 | committed |  | KEYSET_HASH | [] | Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0 | Parent 1=15,2=20; Touch 1=1,2=0; Log 1:outer; Spare 100=0 | PASS |
| S6 같은 미리보기로 [적용] 다시 | S5 미리보기 id로 [적용] {sqlHash, allowKeysetVerification:true} 한 번 더 | HTTP 404(커밋한 미리보기는 재적용 차단·저장소에서 삭제), 원본이 두 번 바뀌지 않음 | 404 |  |  |  | [] | Parent 1=15,2=20; Touch 1=1,2=0; Log 1:outer; Spare 100=0 | Parent 1=15,2=20; Touch 1=1,2=0; Log 1:outer; Spare 100=0 | PASS |
| S7 원본 로그인 sysadmin, 동의 없음 | S6 뒤 샌드박스가 쉬면 Host 정지 → sa: ALTER SERVER ROLE sysadmin ADD MEMBER dbr_origin_user → Host 다시 시작 → 기준선 새로 고침 → 원본 지문(전) → 새 미리보기 → [적용] {sqlHash} → 원본 지문(후) | HTTP 200, committed, SNAPSHOT_NETDIFF, mismatches [], 반환 값 0, 원본 Parent 1 +5·Touch 1 +1·Log outer 1행 추가·나머지 그대로, ddl_ 지문 전후 같음, 적용 뒤 원본에 CascadeLab 스냅샷 없음, 끝나면 sysadmin 회수 | 200 | committed |  | SNAPSHOT_NETDIFF | [] | Parent 1=15,2=20; Touch 1=1,2=0; Log 1:outer; Spare 100=0 | Parent 1=20,2=20; Touch 1=2,2=0; Log 1:outer,2:outer; Spare 100=0 | PASS |
| S8a 서명된 프로시저: 최상위 프로시저 서명 | sa: cl21.usp_Outer에 인증서 서명(ADD SIGNATURE) → 기준선 새로 고침 → 새 미리보기 → [적용] {sqlHash, allowKeysetVerification:true} | 미리보기 ok, HTTP 200, precondition_failed, [PROC_SIGNED_NOT_SUPPORTED]만, message에 '모듈 서명(ADD SIGNATURE)이 있는 프로시저: cl21.usp_Outer.', 원본 불변 | 200 | precondition_failed | PROC_SIGNED_NOT_SUPPORTED | KEYSET_HASH | [] | Parent 1=20,2=20; Touch 1=2,2=0; Log 1:outer,2:outer; Spare 100=0 | Parent 1=20,2=20; Touch 1=2,2=0; Log 1:outer,2:outer; Spare 100=0 | PASS |
| S8b 서명된 프로시저: 중첩 프로시저만 서명 | sa: cl21.usp_Inner에 인증서 서명(ADD SIGNATURE) → 기준선 새로 고침 → 새 미리보기 → [적용] {sqlHash, allowKeysetVerification:true} | 미리보기 ok, HTTP 200, precondition_failed, [PROC_SIGNED_NOT_SUPPORTED]만, message에 '모듈 서명(ADD SIGNATURE)이 있는 프로시저: cl21.usp_Inner.', 원본 불변 | 200 | precondition_failed | PROC_SIGNED_NOT_SUPPORTED | KEYSET_HASH | [] | Parent 1=20,2=20; Touch 1=2,2=0; Log 1:outer,2:outer; Spare 100=0 | Parent 1=20,2=20; Touch 1=2,2=0; Log 1:outer,2:outer; Spare 100=0 | PASS |
| L-KEYSET-3 잠금 시간: KEYSET_HASH, cl20 부모 3 | sa: EXEC cl20.usp_Generate @parents = 3 → (기준선 새로 고침 → 새 미리보기 → [적용] {allowKeysetVerification:true}) × 3 | 매회 HTTP 200, committed, verification.mode KEYSET_HASH | 200 | committed |  | KEYSET_HASH | [] |  |  | PASS |
| L-KEYSET-100000 잠금 시간: KEYSET_HASH, cl20 부모 100000 | sa: EXEC cl20.usp_Generate @parents = 100000 → (기준선 새로 고침 → 새 미리보기 → [적용] {allowKeysetVerification:true}) × 3 | 매회 HTTP 200, committed, verification.mode KEYSET_HASH | 200 | committed |  | KEYSET_HASH | [] |  |  | PASS |
| L-KEYSET-1000000 잠금 시간: KEYSET_HASH, cl20 부모 1000000 | sa: EXEC cl20.usp_Generate @parents = 1000000 → (기준선 새로 고침 → 새 미리보기 → [적용] {allowKeysetVerification:true}) × 3 | 매회 HTTP 200, committed, verification.mode KEYSET_HASH | 200 | committed |  | KEYSET_HASH | [] |  |  | PASS |
| L-SNAPSHOT-3 잠금 시간: SNAPSHOT_NETDIFF, cl20 부모 3 | sa: EXEC cl20.usp_Generate @parents = 3 → (기준선 새로 고침 → 새 미리보기 → [적용] {동의 없음}) × 3 | 매회 HTTP 200, committed, verification.mode SNAPSHOT_NETDIFF | 200 | committed |  | SNAPSHOT_NETDIFF | [] |  |  | PASS |
| L-SNAPSHOT-100000 잠금 시간: SNAPSHOT_NETDIFF, cl20 부모 100000 | sa: EXEC cl20.usp_Generate @parents = 100000 → (기준선 새로 고침 → 새 미리보기 → [적용] {동의 없음}) × 3 | 매회 HTTP 200, committed, verification.mode SNAPSHOT_NETDIFF | 200 | committed |  | SNAPSHOT_NETDIFF | [] |  |  | PASS |
| L-SNAPSHOT-1000000 잠금 시간: SNAPSHOT_NETDIFF, cl20 부모 1000000 | sa: EXEC cl20.usp_Generate @parents = 1000000 → (기준선 새로 고침 → 새 미리보기 → [적용] {동의 없음}) × 3 | 매회 HTTP 200, committed, verification.mode SNAPSHOT_NETDIFF | 200 | committed |  | SNAPSHOT_NETDIFF | [] |  |  | PASS |

## 원본 크기별 [적용] 잠금 시간(측정 L)

잠금 = timingsMs의 drift + execute + netDiff + commit(BEGIN TRAN부터 COMMIT까지). snapshot은 BEGIN TRAN 전. 값은 반복 회차의 중앙값(ms), 잠금 최대는 회차 중 가장 큰 값. 회차별 값은 lock.csv.

| 검증 방식 | cl20 부모 | 사용자 테이블 행 | 데이터 MB | 회차 | 잠금 p50 | 잠금 최대 | drift p50 | execute p50 | netDiff p50 | commit p50 | snapshot p50 | [적용] HTTP p50 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| KEYSET_HASH | 100000 | 300151 | 18.1 | 3 | 100 | 110 | 51 | 2 | 46 | 1 | 0 | 152 |
| KEYSET_HASH | 1000000 | 3000154 | 94.2 | 3 | 863 | 870 | 434 | 2 | 419 | 1 | 0 | 919 |
| KEYSET_HASH | 3 | 157 | 9.4 | 3 | 18 | 40 | 11 | 2 | 4 | 1 | 0 | 71 |
| SNAPSHOT_NETDIFF | 100000 | 300160 | 18.3 | 3 | 542 | 627 | 0 | 3 | 537 | 1 | 76 | 695 |
| SNAPSHOT_NETDIFF | 1000000 | 3000163 | 94.8 | 3 | 2906 | 3293 | 1 | 3 | 2900 | 1 | 79 | 3069 |
| SNAPSHOT_NETDIFF | 3 | 166 | 78.0 | 3 | 313 | 372 | 1 | 3 | 308 | 1 | 81 | 466 |

## 응답 메시지와 참고 값
- S0: 정의 해시(sa): cl21.usp_Inner=30161F1A92F8, cl21.usp_Outer=113D46437081
- S1a message: 원본 스냅샷을 만들 수 없어(원본 로그인에 CREATE DATABASE 권한이 없음(데이터베이스 스냅샷 생성 불가), 원본 로그인에 원본 DB의 CONTROL 권한이 없음(GRANT CONTROL·db_owner·DB 소유자 중 하나 필요, 스냅샷 생성 불가)) KEYSET_HASH(건수·해시 비교, 해시 충돌이나 미리보기 테이블의 키 밖 UPDATE를 놓칠 수 있음)로만 검증할 수 있음. 동의하면 allowKeysetVerification=true로 다시 요청할 것.
- S1a: 미리보기 pv_20261126_2fe430ea ok, 반환 값 0, 886ms
- S1a: [적용] 67ms
- S1b message: 원본에서 프로시저 정의를 읽지 못해 기준선과 같은지 확인할 수 없음: cl21.usp_Outer, cl21.usp_Inner(없거나 암호화됐거나 도구 로그인에 VIEW DEFINITION 권한이 없음). 원본 로그인(본인)에게 프로시저 EXECUTE 권한이 없음: cl21.usp_Outer. 도구는 원본에 GRANT하지 않으므로 권한을 받은 뒤 다시 적용할 것.
- S1b: [적용] 71ms
- S2 message: 원본에서 프로시저 정의를 읽지 못해 기준선과 같은지 확인할 수 없음: cl21.usp_Outer, cl21.usp_Inner(없거나 암호화됐거나 도구 로그인에 VIEW DEFINITION 권한이 없음).
- S2: 미리보기 pv_20261126_e8d35dde ok, 반환 값 0, 174ms
- S2: [적용] 29ms
- S3 message: 원본 스냅샷을 만들 수 없어(원본 로그인에 CREATE DATABASE 권한이 없음(데이터베이스 스냅샷 생성 불가), 원본 로그인에 원본 DB의 CONTROL 권한이 없음(GRANT CONTROL·db_owner·DB 소유자 중 하나 필요, 스냅샷 생성 불가)) KEYSET_HASH(건수·해시 비교, 해시 충돌이나 미리보기 테이블의 키 밖 UPDATE를 놓칠 수 있음)로만 검증할 수 있음. 동의하면 allowKeysetVerification=true로 다시 요청할 것.
- S3: 미리보기 pv_20261126_79fcb666 ok, 반환 값 0, 174ms
- S3: [적용] 18ms
- S4 message: 기준선 이후 원본에서 프로시저 정의가 바뀜: cl21.usp_Outer. 바뀐 본문은 거부 규칙 검사를 거치지 않았으므로 실행하지 않음(샌드박스를 새로 고친 뒤 다시 미리보기 할 것).
- S4: 미리보기 pv_20261126_2bde4654 ok, 반환 값 0, 186ms
- S4: usp_Outer 정의 해시 113D464370818A2B99EC95662697245182101120C66A1374CBB46F45B1BA629E → 22C05E887B61F7A58078EF6CE17B0C8F886CD3922760AD804F2265AD34F2E7DA
- S4: [적용] 34ms
- S4: 새로 고침: HTTP 202, state ready, 6초
- S5 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- S5: 미리보기 pv_20261126_e8bb174a ok, 반환 값 0, 531ms
- S5: [적용] 280ms
- S5: statementRowCounts: #1 preview= actual=
- S5: tableCounts: cl21.Log 0->1(기대 1)
- S5: limitations 9건(s5-apply.json), sandboxRefreshStarted False
- S5: ddl_ 지문 9줄 비교, 다른 줄 0개
- S5: 적용 뒤 /api/sandbox: state ready, baselineStale True
- S6 message: Not Found
- S6: [적용] 11ms
- S7 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- S7: Host 정지 전 /api/sandbox: state ready, locked False
- S7: 기준선 새로 고침: HTTP 202, state ready, 6초
- S7: 미리보기 pv_20261126_0c3b4435 ok, 반환 값 0, 871ms
- S7: [적용] 715ms
- S7: statementRowCounts: #1 preview= actual=
- S7: tableCounts: 
- S7: limitations 4건(s7-apply.json), sandboxRefreshStarted False
- S7: 끝난 뒤 dbr_origin_user sysadmin 회수
- S8a message: 모듈 서명(ADD SIGNATURE)이 있는 프로시저: cl21.usp_Outer. 서명은 실행 사용자의 DML 전용 권한 경계를 넘을 수 있어 원본에서 실행하지 않음.
- S8a: 기준선 새로 고침: HTTP 202, state ready, 6초
- S8a: 미리보기 pv_20261126_a690615a ok, 반환 값 0, 565ms
- S8a: [적용] 52ms
- S8a: verification.mode KEYSET_HASH(단언 안 함)
- S8b message: 모듈 서명(ADD SIGNATURE)이 있는 프로시저: cl21.usp_Inner. 서명은 실행 사용자의 DML 전용 권한 경계를 넘을 수 있어 원본에서 실행하지 않음.
- S8b: 기준선 새로 고침: HTTP 202, state ready, 6초
- S8b: 미리보기 pv_20261126_9606563b ok, 반환 값 0, 507ms
- S8b: [적용] 34ms
- S8b: verification.mode KEYSET_HASH(단언 안 함)
- L-KEYSET-3 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- L-KEYSET-3: 원본 CascadeLab 사용자 테이블 157 행, 데이터 파일 사용 9.4 MB
- L-KEYSET-3: 미리보기 pv_20261126_19a5da33 ok, 반환 값 0, 519ms
- L-KEYSET-3: [적용] 85ms
- L-KEYSET-3: 미리보기 pv_20261126_e650943f ok, 반환 값 0, 598ms
- L-KEYSET-3: [적용] 71ms
- L-KEYSET-3: 미리보기 pv_20261126_0c5aa182 ok, 반환 값 0, 539ms
- L-KEYSET-3: [적용] 59ms
- L-KEYSET-3: 잠금 p50 18ms(회차별 40/18/15), drift p50 11, netDiff p50 4, snapshot p50 0ms
- L-KEYSET-100000 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- L-KEYSET-100000: 원본 CascadeLab 사용자 테이블 300151 행, 데이터 파일 사용 18.1 MB
- L-KEYSET-100000: 미리보기 pv_20261126_27f1d202 ok, 반환 값 0, 526ms
- L-KEYSET-100000: [적용] 159ms
- L-KEYSET-100000: 미리보기 pv_20261126_b288c316 ok, 반환 값 0, 537ms
- L-KEYSET-100000: [적용] 152ms
- L-KEYSET-100000: 미리보기 pv_20261126_52c9c0cd ok, 반환 값 0, 499ms
- L-KEYSET-100000: [적용] 150ms
- L-KEYSET-100000: 잠금 p50 100ms(회차별 110/100/99), drift p50 51, netDiff p50 46, snapshot p50 0ms
- L-KEYSET-1000000 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- L-KEYSET-1000000: 원본 CascadeLab 사용자 테이블 3000154 행, 데이터 파일 사용 94.2 MB
- L-KEYSET-1000000: 미리보기 pv_20261126_66953bdf ok, 반환 값 0, 542ms
- L-KEYSET-1000000: [적용] 902ms
- L-KEYSET-1000000: 미리보기 pv_20261126_39436879 ok, 반환 값 0, 513ms
- L-KEYSET-1000000: [적용] 923ms
- L-KEYSET-1000000: 미리보기 pv_20261126_28a2aad7 ok, 반환 값 0, 506ms
- L-KEYSET-1000000: [적용] 919ms
- L-KEYSET-1000000: 잠금 p50 863ms(회차별 854/870/863), drift p50 434, netDiff p50 419, snapshot p50 0ms
- L-SNAPSHOT-3 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- L-SNAPSHOT-3: 원본 CascadeLab 사용자 테이블 166 행, 데이터 파일 사용 78.0 MB
- L-SNAPSHOT-3: 미리보기 pv_20261126_6f025184 ok, 반환 값 0, 871ms
- L-SNAPSHOT-3: [적용] 605ms
- L-SNAPSHOT-3: 미리보기 pv_20261126_403655b3 ok, 반환 값 0, 535ms
- L-SNAPSHOT-3: [적용] 464ms
- L-SNAPSHOT-3: 미리보기 pv_20261126_52f36c41 ok, 반환 값 0, 511ms
- L-SNAPSHOT-3: [적용] 466ms
- L-SNAPSHOT-3: 잠금 p50 313ms(회차별 372/308/313), drift p50 1, netDiff p50 308, snapshot p50 81ms
- L-SNAPSHOT-100000 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- L-SNAPSHOT-100000: 원본 CascadeLab 사용자 테이블 300160 행, 데이터 파일 사용 18.3 MB
- L-SNAPSHOT-100000: 미리보기 pv_20261126_361c7ecf ok, 반환 값 0, 525ms
- L-SNAPSHOT-100000: [적용] 776ms
- L-SNAPSHOT-100000: 미리보기 pv_20261126_dead712e ok, 반환 값 0, 530ms
- L-SNAPSHOT-100000: [적용] 691ms
- L-SNAPSHOT-100000: 미리보기 pv_20261126_40c3011e ok, 반환 값 0, 580ms
- L-SNAPSHOT-100000: [적용] 695ms
- L-SNAPSHOT-100000: 잠금 p50 542ms(회차별 627/521/542), drift p50 0, netDiff p50 537, snapshot p50 76ms
- L-SNAPSHOT-1000000 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- L-SNAPSHOT-1000000: 원본 CascadeLab 사용자 테이블 3000163 행, 데이터 파일 사용 94.8 MB
- L-SNAPSHOT-1000000: 미리보기 pv_20261126_58ed999a ok, 반환 값 0, 512ms
- L-SNAPSHOT-1000000: [적용] 3469ms
- L-SNAPSHOT-1000000: 미리보기 pv_20261126_5bc05d71 ok, 반환 값 0, 510ms
- L-SNAPSHOT-1000000: [적용] 3069ms
- L-SNAPSHOT-1000000: 미리보기 pv_20261126_57ddca55 ok, 반환 값 0, 537ms
- L-SNAPSHOT-1000000: [적용] 3036ms
- L-SNAPSHOT-1000000: 잠금 p50 2906ms(회차별 3293/2906/2877), drift p50 1, netDiff p50 2900, snapshot p50 79ms

## 증거 파일(결과 폴더 기준)
- S0: s0-origin.log
- S1a: s1-preview.json, s1a-apply.json, s1-origin-before.log, s1a-origin-after.log
- S1b: s1b-apply.json, s1b-origin-before.log, s1b-origin-after.log
- S2: s2-grant.log, s2-preview.json, s2-apply.json, s2-origin-before.log, s2-origin-after.log
- S3: s3-grant.log, s3-preview.json, s3-apply.json, s3-origin-before.log, s3-origin-after.log
- S4: s4-preview.json, s4-alter.log, s4-apply.json, s4-origin-before.log, s4-origin-after.log, baseline-2-sandbox.json
- S5: s5-fingerprint-before.log, s5-preview.json, s5-apply.json, s5-origin-before.log, s5-origin-after.log, s5-fingerprint-after.log
- S6: s6-apply.json, s6-origin-before.log, s6-origin-after.log
- S7: s7-grant-sysadmin.log, baseline-s7-sandbox.json, s7-fingerprint-before.log, s7-preview.json, s7-apply.json, s7-origin-before.log, s7-origin-after.log, s7-fingerprint-after.log, s7-snapshots-after.log, s7-revoke-sysadmin.log
- S8a: s8a-sign.log, baseline-s8a-sandbox.json, s8a-preview.json, s8a-apply.json, s8a-origin-before.log, s8a-origin-after.log
- S8b: s8b-sign.log, baseline-s8b-sandbox.json, s8b-preview.json, s8b-apply.json, s8b-origin-before.log, s8b-origin-after.log
- L-KEYSET-3: l-keyset-3-generate.log, l-keyset-3-r1-preview.json, l-keyset-3-r1-apply.json, l-keyset-3-r2-preview.json, l-keyset-3-r2-apply.json, l-keyset-3-r3-preview.json, l-keyset-3-r3-apply.json
- L-KEYSET-100000: l-keyset-100000-generate.log, l-keyset-100000-r1-preview.json, l-keyset-100000-r1-apply.json, l-keyset-100000-r2-preview.json, l-keyset-100000-r2-apply.json, l-keyset-100000-r3-preview.json, l-keyset-100000-r3-apply.json
- L-KEYSET-1000000: l-keyset-1000000-generate.log, l-keyset-1000000-r1-preview.json, l-keyset-1000000-r1-apply.json, l-keyset-1000000-r2-preview.json, l-keyset-1000000-r2-apply.json, l-keyset-1000000-r3-preview.json, l-keyset-1000000-r3-apply.json
- L-SNAPSHOT-3: l-snapshot-3-generate.log, l-snapshot-3-r1-preview.json, l-snapshot-3-r1-apply.json, l-snapshot-3-r2-preview.json, l-snapshot-3-r2-apply.json, l-snapshot-3-r3-preview.json, l-snapshot-3-r3-apply.json
- L-SNAPSHOT-100000: l-snapshot-100000-generate.log, l-snapshot-100000-r1-preview.json, l-snapshot-100000-r1-apply.json, l-snapshot-100000-r2-preview.json, l-snapshot-100000-r2-apply.json, l-snapshot-100000-r3-preview.json, l-snapshot-100000-r3-apply.json
- L-SNAPSHOT-1000000: l-snapshot-1000000-generate.log, l-snapshot-1000000-r1-preview.json, l-snapshot-1000000-r1-apply.json, l-snapshot-1000000-r2-preview.json, l-snapshot-1000000-r2-apply.json, l-snapshot-1000000-r3-preview.json, l-snapshot-1000000-r3-apply.json
- 공통: progress.log, host.out.log, host.err.log, setup-*.log, baseline-*.json, folders-after-baseline.log, summary.json, lock.csv
