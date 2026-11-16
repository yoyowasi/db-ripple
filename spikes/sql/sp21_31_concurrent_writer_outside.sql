/* SP21 C 보조(2026-11-16 실행 중 추가) — 방해 세션이 미리보기 밖 테이블(sp21.Y)의 행만 잠그는 경우.
   sp21_30은 X를 UPDATE하므로 원본 트리거(trg_X_upd)가 Y도 잠그고, [적용]은 첫 단계(드리프트 확인, X 행 읽기)에서 1222를 만난다.
   이 스크립트는 Y만 잠가 [적용]이 드리프트 확인을 지나 KEYSET_HASH 실행 전 상태 읽기(모든 테이블 해시)에서 1222를 만나게 한다(두 단계의 오류 처리 비교).
   실행(저장소 루트, 별도 터미널, sa): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp21_31_concurrent_writer_outside.sql -Database CascadeLab -Log spikes\logs\sp21-31-writer-outside.log
   약 40초 잠근 뒤 ROLLBACK한다(데이터 변경 없음). 사전: sp21_21(sp21 스키마). */
USE CascadeLab;
GO
BEGIN TRANSACTION;
UPDATE sp21.Y SET v = v WHERE id = 1;   -- 값은 그대로, Y 행 잠금(X)만
SELECT 'locked_at' AS what, CONVERT(varchar(30), SYSDATETIME(), 126) AS t;
WAITFOR DELAY '00:00:40';
ROLLBACK TRANSACTION;
SELECT 'released_at' AS what, CONVERT(varchar(30), SYSDATETIME(), 126) AS t;
GO
