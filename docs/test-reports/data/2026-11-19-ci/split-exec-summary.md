# Split 모드 EXEC [적용] 자동 확인

전체: **PASS** (시나리오 8개 중 기대와 다름 0개)

- 입력: `EXEC cl21.usp_Outer @id = 1, @delta = 5;`
- commit: 108d9d878bd435f263dcf8d1cf6812fc2d249fdf
- os: Ubuntu 26.04.1 LTS
- pwsh: 7.6.10
- originConnection: 127.0.0.1,14334, 로그인 dbr_origin_user
- dotnet: 11.0.100
- originVersion: Microsoft SQL Server 2022 (RTM-CU28) (KB5125472) - 16.0.4325.4 (X64)   Nov  4 2026 14:09:11   Copyright (C) 2022 Microsoft Corporation  Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>
- originServer: 9e495d9b5c5f
- originCollation: SQL_Latin1_General_CP1_CI_AS
- sandboxVersion: Microsoft SQL Server 2025 (RTM-CU10) (KB5125471) - 17.0.5035.5 (X64)   Oct 15 2026 19:55:26   Copyright (C) 2025 Microsoft Corporation  Enterprise Developer Edition (64-bit) on Linux (Ubuntu 24.04.5 LTS) <X64>
- sandboxServer: 4f66944a4aac
- sandboxCollation: SQL_Latin1_General_CP1_CI_AS
- 기준선(첫 구축): state ready, mode split, fingerprintMatch True, 경고 3, 불일치 0, 9초
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

## 응답 메시지와 참고 값
- S0: 정의 해시(sa): cl21.usp_Inner=30161F1A92F8, cl21.usp_Outer=113D46437081
- S1a message: 원본 스냅샷을 만들 수 없어(원본 로그인에 CREATE DATABASE 권한이 없음(데이터베이스 스냅샷 생성 불가), 원본 로그인에 원본 DB의 CONTROL 권한이 없음(GRANT CONTROL·db_owner·DB 소유자 중 하나 필요, 스냅샷 생성 불가)) KEYSET_HASH(건수·해시 비교, 해시 충돌이나 미리보기 테이블의 키 밖 UPDATE를 놓칠 수 있음)로만 검증할 수 있음. 동의하면 allowKeysetVerification=true로 다시 요청할 것.
- S1a: 미리보기 pv_20261119_297b590f ok, 반환 값 0, 1685ms
- S1a: [적용] 57ms
- S1b message: 원본에서 프로시저 정의를 읽지 못해 기준선과 같은지 확인할 수 없음: cl21.usp_Outer, cl21.usp_Inner(없거나 암호화됐거나 도구 로그인에 VIEW DEFINITION 권한이 없음). 원본 로그인(본인)에게 프로시저 EXECUTE 권한이 없음: cl21.usp_Outer. 도구는 원본에 GRANT하지 않으므로 권한을 받은 뒤 다시 적용할 것.
- S1b: [적용] 61ms
- S2 message: 원본에서 프로시저 정의를 읽지 못해 기준선과 같은지 확인할 수 없음: cl21.usp_Outer, cl21.usp_Inner(없거나 암호화됐거나 도구 로그인에 VIEW DEFINITION 권한이 없음).
- S2: 미리보기 pv_20261119_1c88135e ok, 반환 값 0, 141ms
- S2: [적용] 23ms
- S3 message: 원본 스냅샷을 만들 수 없어(원본 로그인에 CREATE DATABASE 권한이 없음(데이터베이스 스냅샷 생성 불가), 원본 로그인에 원본 DB의 CONTROL 권한이 없음(GRANT CONTROL·db_owner·DB 소유자 중 하나 필요, 스냅샷 생성 불가)) KEYSET_HASH(건수·해시 비교, 해시 충돌이나 미리보기 테이블의 키 밖 UPDATE를 놓칠 수 있음)로만 검증할 수 있음. 동의하면 allowKeysetVerification=true로 다시 요청할 것.
- S3: 미리보기 pv_20261119_feaa7776 ok, 반환 값 0, 143ms
- S3: [적용] 15ms
- S4 message: 기준선 이후 원본에서 프로시저 정의가 바뀜: cl21.usp_Outer. 바뀐 본문은 거부 규칙 검사를 거치지 않았으므로 실행하지 않음(샌드박스를 새로 고친 뒤 다시 미리보기 할 것).
- S4: 미리보기 pv_20261119_9ebe65e4 ok, 반환 값 0, 157ms
- S4: usp_Outer 정의 해시 113D464370818A2B99EC95662697245182101120C66A1374CBB46F45B1BA629E → 22C05E887B61F7A58078EF6CE17B0C8F886CD3922760AD804F2265AD34F2E7DA
- S4: [적용] 26ms
- S4: 새로 고침: HTTP 202, state ready, 6초
- S5 message: 원본이 바뀌어 샌드박스 기준선이 오래됨(자동 재구축 안 함, 새로 고침 필요).
- S5: 미리보기 pv_20261119_2d502ff2 ok, 반환 값 0, 732ms
- S5: [적용] 222ms
- S5: statementRowCounts: #1 preview= actual=
- S5: tableCounts: cl21.Log 0->1(기대 1)
- S5: limitations 9건(s5-apply.json), sandboxRefreshStarted False
- S5: ddl_ 지문 9줄 비교, 다른 줄 0개
- S5: 적용 뒤 /api/sandbox: state ready, baselineStale True
- S6 message: Not Found
- S6: [적용] 10ms

## 증거 파일(결과 폴더 기준)
- S0: s0-origin.log
- S1a: s1-preview.json, s1a-apply.json, s1-origin-before.log, s1a-origin-after.log
- S1b: s1b-apply.json, s1b-origin-before.log, s1b-origin-after.log
- S2: s2-grant.log, s2-preview.json, s2-apply.json, s2-origin-before.log, s2-origin-after.log
- S3: s3-grant.log, s3-preview.json, s3-apply.json, s3-origin-before.log, s3-origin-after.log
- S4: s4-preview.json, s4-alter.log, s4-apply.json, s4-origin-before.log, s4-origin-after.log, baseline-2-sandbox.json
- S5: s5-fingerprint-before.log, s5-preview.json, s5-apply.json, s5-origin-before.log, s5-origin-after.log, s5-fingerprint-after.log
- S6: s6-apply.json, s6-origin-before.log, s6-origin-after.log
- 공통: progress.log, host.out.log, host.err.log, setup-*.log, baseline-*.json, folders-after-baseline.log, summary.json
