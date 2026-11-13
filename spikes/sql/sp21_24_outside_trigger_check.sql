/* SP21 — KEYSET_HASH 검출력(2) 확인: [적용] 시도 뒤 원본의 sp21 값을 본다(읽기만).
   기대: 약한 검증이 OUTSIDE_TABLE_CHANGED로 막고 롤백했다면 X·Y 모두 v = 0 그대로(+ 이전 시도가 있었다면 그 값). 막지 못하고 커밋됐다면 X.v 1행 +1, Y.id=1의 v +1.
   실행(저장소 루트, sa): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp21_24_outside_trigger_check.sql -Database CascadeLab -Log spikes\logs\sp21-24-check.log */
USE CascadeLab;
GO
SELECT 'X' AS t, id, v FROM sp21.X UNION ALL SELECT 'Y', id, v FROM sp21.Y UNION ALL SELECT 'Z', id, v FROM sp21.Z ORDER BY 1, 2;
SELECT 'trigger_exists' AS what, COUNT(*) AS n FROM sys.triggers WHERE name = N'trg_X_upd';
GO
