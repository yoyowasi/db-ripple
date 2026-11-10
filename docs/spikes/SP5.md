# SP5 — AdventureWorks 백업 구조와 시간 측정

**결과: 통과 (2026-11-10, DB 실측).** FILESTREAM·메모리 최적화 파일 그룹이 없고, 백업·복원·스냅샷 생성·되돌리기가 모두 1초 안에 끝난다. 스냅샷이 있으면 RESTORE는 오류 5094로 거부된다. 전체 텍스트 카탈로그가 있지만 컨테이너에 전체 텍스트 검색 구성 요소가 없어 `CONTAINS` 질의만 실패하고 DML에는 영향이 없다.

## 목적
AW_Origin 기준선 구축(설계 §5.1)의 위험(R4 등)을 실측으로 닫는다: 백업에 FILESTREAM·메모리 최적화·전체 텍스트가 있는지, 복원·스냅샷 시간, 스냅샷이 있을 때 RESTORE 거부 여부.

## 방법 (`.\scripts\run-sql.ps1 <파일> -Log spikes\logs\<파일>.log`, 선행: fetch-adventureworks, restore-aw-origin)
| 파일 | 내용 |
|---|---|
| `sp5-1-inspect-backup.sql` | RESTORE HEADERONLY, FILELISTONLY (Type D/L/S/F) |
| `sp5-2-post-restore.sql` | 복원 뒤 `sys.filegroups`(FG/FX/FD), `sys.fulltext_catalogs`, `sys.fulltext_indexes`, 파일 경로 |
| `sp5-3-timing.sql` (`-Measure`) | BACKUP COPY_ONLY, RESTORE WITH MOVE, 스냅샷 생성, 스냅샷 되돌리기 시간(ms) |
| `sp5-4-restore-with-snapshot.sql` (`-ContinueOnError`) | 스냅샷이 있는 DB에 RESTORE 시도, TRY/CATCH로 오류 번호 기록 |
| `sp5-5-restore-with-snapshot-raw.sql` (`-ContinueOnError`, 2026-11-10 추가) | sp5-4의 CATCH는 마지막 오류(3013)만 잡아서, TRY 없이 원래 오류를 보고 스냅샷 삭제 뒤 같은 RESTORE가 되는지 확인 |
| `sp5-6-fulltext.sql` (`-ContinueOnError`, 2026-11-10 추가) | `IsFullTextInstalled`, 전체 텍스트 인덱스가 걸린 테이블의 UPDATE·DELETE(롤백), `CONTAINS` 질의 |

실행 세션 설정: 이 스파이크는 `run-sql.ps1`에 `sqlcmd -I`를 넣기 전(QUOTED_IDENTIFIER OFF)에 실행했다. 백업·복원·스냅샷과 계산 열 인덱스가 없는 Production.ProductReview의 DML은 이 설정의 영향을 받지 않으므로 다시 실행하지 않았다.

진행 중 고친 점: 첫 실행에서 sp5-3·sp5-4의 BACKUP이 OS 오류 5(접근 거부)로 실패했다. `restore-aw-origin.ps1`이 `/var/opt/mssql/backup` 폴더를 root 소유로 만들고 `.bak` 파일만 mssql로 바꿨기 때문이다. 폴더를 `chown -R mssql:root`로 바꾸고 스크립트도 고친 뒤 다시 실행했다. 첫 실행의 sp5-4 `RESTORE_REJECTED 3013`은 백업 파일이 없어서 난 것이라 결과에서 뺐다.

## 판정 기준
- FILESTREAM(Type S 또는 FD)·메모리 최적화(FX): 있으면 컨테이너 복원 가능 여부와 스냅샷 제외 문제를 기록하고 설계에 반영. 없으면 "해당 없음".
- 전체 텍스트: 카탈로그·인덱스 수 기록. 복원 후 정상 열림 여부 확인.
- 시간: 각 단계 ms 기록. 설계 §13 SP5의 통과 기준은 "FILESTREAM이 없다. 시간을 기록한다."이고 수치 합격선은 없다. 측정값은 D2 복구 방식과 M4 반복 횟수(§10.4) 결정에 쓴다.
- 스냅샷 존재 시 RESTORE: `RESTORE_REJECTED`면 오류 번호·메시지를 기록하고 구축 흐름에서 스냅샷 삭제를 선행하도록 한다. `RESTORE_ALLOWED`면 그 사실을 기록.

## 결과

| 항목 | 결과 | 로그 |
|---|---|---|
| HEADERONLY | AdventureWorks2025 전체 백업(2025-11-14), CompatibilityLevel 170, DatabaseVersion 998, Collation `SQL_Latin1_General_CP1_CI_AS`, RecoveryModel SIMPLE, 압축(MS_XPRESS) | sp5-1 |
| FILELISTONLY (파일 유형) | `AdventureWorks`(D, FileId 1, PRIMARY), `AdventureWorks_log`(L, FileId 2). S·F 없음 | sp5-1 |
| 파일 그룹 형식 | PRIMARY(FG)만. 메모리 최적화·FILESTREAM 파일 그룹 0개 | sp5-2 |
| 전체 텍스트 | 카탈로그 1개 `AW2025FullTextCatalog`, 인덱스 3개(Production.Document, HumanResources.JobCandidate, Production.ProductReview, 변경 추적 AUTO). `IsFullTextInstalled = 0`. 그 테이블의 UPDATE·DELETE는 정상(각 1행, 롤백 후 @@TRANCOUNT 0). `CONTAINS`는 오류 7609 | sp5-2, sp5-6 |
| BACKUP COPY_ONLY 시간 | 265ms (25,554페이지) | sp5-3 |
| RESTORE WITH MOVE 시간 | 611ms | sp5-3 |
| 스냅샷 생성 시간 | 362ms | sp5-3 |
| 스냅샷 되돌리기 시간 | 411ms | sp5-3 |
| 스냅샷 존재 시 RESTORE (오류 번호) | 거부: 오류 5094 "The operation cannot be performed on a database with database snapshots or active DBCC replicas." 뒤에 3013. 스냅샷을 지운 뒤 같은 RESTORE … REPLACE는 성공 | sp5-4, sp5-5 |

참고: `restore-aw-origin.ps1`(CLI `restore`)로 AW_Origin을 복원할 때 RESTORE 자체는 1,412ms였다(`spikes/logs/aw-origin-restore.log`, `dotnet run` 빌드 포함 전체 9초).

**확인한 사실**
- 공식 AdventureWorks2025.bak에는 FILESTREAM·메모리 최적화가 없다. 설계 R4(FILESTREAM으로 스냅샷 불가)는 AW에 해당하지 않는다.
- 스냅샷이 있으면 원본 DB RESTORE는 5094로 거부된다(A14 확인). C8의 "기존 스냅샷 삭제 → 백업 → 복원" 순서가 필요하다.
- 기준선 구축 단계는 AW 규모에서 각각 1초 안이다.

**추정**
- 컨테이너에 전체 텍스트 검색(`mssql-server-fts`)이 없어도 DML 미리보기에는 영향이 없다. 다만 사용자 트리거나 계산 식이 전체 텍스트 질의를 쓰면 실패할 수 있다. AW 트리거 10개의 본문에는 `CONTAINS`·`FREETEXT`가 없다고 보지만 C7 카탈로그로 다시 확인할 일이다.
- 스냅샷 되돌리기는 전체 텍스트 카탈로그를 지운다(A14). 평가 회차 초기화를 되돌리기로 하면 전체 텍스트 메타데이터가 사라질 수 있으나, 이 컨테이너에서는 전체 텍스트를 쓰지 않으므로 영향이 없다고 본다.

## 증거 범위
DB 실측(컨테이너 dbripple-sql, 2026-11-10). 로그 `spikes/logs/sp5-1-inspect-backup.log` ~ `sp5-6-fulltext.log`, `aw-origin-restore.log`.

## 설계 반영
- D2: 평가 회차 초기화는 스냅샷 되돌리기(약 0.4초)와 RESTORE(약 0.6초) 모두 빠르다. 다만 1회 측정이고 되돌리기 전 변경은 1,000행 하나뿐이었다. 이 값만으로 보면 M4 반복 20회는 초기화 시간에 크게 제약받지 않는다(사례별 변경량이 크면 다시 잰다).
- §5.1: 1단계(스냅샷 먼저 삭제) 근거에 오류 5094를 적는다.
- §2 A14: AW 전체 텍스트 카탈로그와 컨테이너의 전체 텍스트 미설치(`IsFullTextInstalled = 0`, `CONTAINS`는 7609)를 적는다.
