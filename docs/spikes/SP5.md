# SP5 — AdventureWorks 백업 구조와 시간 측정

**결과: 미실행(2026-11-06 Docker 미설치)**

## 목적
AW_Origin 기준선 구축(설계 §5.1)의 위험(R8 등)을 실측으로 닫는다: 백업에 FILESTREAM·메모리 최적화·풀텍스트가 있는지, 복원·스냅샷 시간, 스냅샷이 있을 때 RESTORE 거부 여부.

## 방법 (`.\scripts\run-sql.ps1 <파일>`, 선행: fetch, restore-aw-origin)
| 파일 | 내용 |
|---|---|
| `sp5-1-inspect-backup.sql` | RESTORE HEADERONLY, FILELISTONLY (Type D/L/S/F) |
| `sp5-2-post-restore.sql` | 복원 뒤 `sys.filegroups`(FG/FX/FD), `sys.fulltext_catalogs`, `sys.fulltext_indexes` |
| `sp5-3-timing.sql` (`-Measure`) | BACKUP COPY_ONLY, RESTORE WITH MOVE, 스냅샷 생성, 스냅샷 되돌리기 시간(ms) |
| `sp5-4-restore-with-snapshot.sql` | 스냅샷이 있는 DB에 RESTORE 시도, TRY/CATCH로 오류 번호 기록 |

## 판정 기준
- FILESTREAM(Type S 또는 FD)·메모리 최적화(FX): 있으면 컨테이너 복원 가능 여부와 스냅샷 제외 문제를 기록하고 설계에 반영. 없으면 "해당 없음".
- 풀텍스트: 카탈로그·인덱스 수 기록. 복원 후 정상 열림 여부 확인.
- 시간: 각 단계 ms 기록. 설계 §13 SP5의 통과 기준은 "FILESTREAM이 없다. 시간을 기록한다."이고 수치 합격선은 없다. 측정값은 D2 복구 방식과 M4 반복 횟수(§10.4) 결정에 쓴다.
- 스냅샷 존재 시 RESTORE: `RESTORE_REJECTED`면 오류 번호·메시지를 기록하고 구축 흐름에서 스냅샷 삭제를 선행하도록 한다. `RESTORE_ALLOWED`면 그 사실을 기록.

## 결과 칸
| 항목 | 결과 |
|---|---|
| HEADERONLY / FILELISTONLY (파일 유형) | 미실행(2026-11-06 Docker 미설치) |
| 파일 그룹 형식 (FG/FX/FD) | 미실행(2026-11-06 Docker 미설치) |
| 풀텍스트 카탈로그·인덱스 | 미실행(2026-11-06 Docker 미설치) |
| BACKUP COPY_ONLY 시간 | 미실행(2026-11-06 Docker 미설치) |
| RESTORE WITH MOVE 시간 | 미실행(2026-11-06 Docker 미설치) |
| 스냅샷 생성 시간 | 미실행(2026-11-06 Docker 미설치) |
| 스냅샷 되돌리기 시간 | 미실행(2026-11-06 Docker 미설치) |
| 스냅샷 존재 시 RESTORE (오류 번호) | 미실행(2026-11-06 Docker 미설치) |
