/* SP21 — 동시 세션 쓰기: [적용]이 도는 동안 다른 세션이 같은 행에 쓰면 어떻게 실패하는가(롤백 쪽 실패).
   이 스크립트는 "방해 세션"이다. 한 터미널에서 먼저 실행하고(약 90초 동안 sp21.X의 id = 1 행을 잠근다), 그동안 Host에서 `UPDATE sp21.X SET v = v + 1 WHERE id = 1;`을 [적용]한다.
   기대(추정): 도구의 원본 연결이 LOCK_TIMEOUT(제품 값 DbRipple:OriginLockTimeoutMs, 기본 10000ms) 뒤 오류 1222로 실패하고 [적용]은 롤백된다(변경 없음). 정확한 오류 번호·화면 문구를 docs/spikes/SP21.md에 적는다.
   실행(저장소 루트, 별도 터미널): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp21_30_concurrent_writer.sql -Database CascadeLab -Log spikes\logs\sp21-30-writer.log
   사전: sp21_21_outside_trigger_setup.sql(sp21 스키마). 방해 세션은 끝에서 ROLLBACK한다(데이터 변경 없음). 실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE CascadeLab;
GO
BEGIN TRANSACTION;
UPDATE sp21.X SET v = v WHERE id = 1;   -- 값은 그대로, 행 잠금(X)만 잡는다
SELECT 'locked_at' AS what, CONVERT(varchar(30), SYSDATETIME(), 126) AS t;
WAITFOR DELAY '00:01:30';
ROLLBACK TRANSACTION;
SELECT 'released_at' AS what, CONVERT(varchar(30), SYSDATETIME(), 126) AS t;
GO
