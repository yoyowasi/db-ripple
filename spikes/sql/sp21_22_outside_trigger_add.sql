/* SP21 — KEYSET_HASH 검출력(2): 원본에만 트리거를 추가한다. X가 UPDATE되면 Y의 한 행도 UPDATE한다(행 수 그대로, 값만 +1).
   Host의 기준선(샌드박스)은 이 트리거를 모른다 → 미리보기는 X만 보이고, 원본에서 [적용]하면 Y도 바뀐다 → 약한 검증(KEYSET_HASH)이 Y를 OUTSIDE_TABLE_CHANGED로 막아야 한다.
   실행(저장소 루트, sa): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp21_22_outside_trigger_add.sql -Database CascadeLab -Log spikes\logs\sp21-22-add.log
   주의: 이 DDL은 원본 모사 컨테이너에 하는 것이다(운영·회사 서버에 하지 않는다). 기준선을 새로 만들면 샌드박스에도 트리거가 생겨 시나리오가 달라진다 → 미리보기 뒤에 추가한다.
   실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE CascadeLab;
GO
CREATE TRIGGER sp21.trg_X_upd ON sp21.X AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE sp21.Y SET v = v + 1 WHERE id = 1;
END;
GO
SELECT 'trigger_added' AS what, name, OBJECT_SCHEMA_NAME(parent_id) AS schema_name, OBJECT_NAME(parent_id) AS table_name FROM sys.triggers WHERE name = N'trg_X_upd';
GO
