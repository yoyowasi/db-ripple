/* SP21 — KEYSET_HASH 검출력(2) 정리: 원본 모사 컨테이너의 CascadeLab에서 sp21 스키마(트리거·테이블)를 지운다. 없으면 건너뛴다.
   실행(저장소 루트, sa): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp21_23_outside_trigger_drop.sql -Database CascadeLab -Log spikes\logs\sp21-23-drop.log
   그 뒤 Host의 기준선을 새로 고친다(샌드박스에서도 sp21이 사라지도록). 실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE CascadeLab;
GO
IF OBJECT_ID(N'sp21.trg_X_upd', N'TR') IS NOT NULL DROP TRIGGER sp21.trg_X_upd;
IF OBJECT_ID(N'sp21.Z', N'U') IS NOT NULL DROP TABLE sp21.Z;
IF OBJECT_ID(N'sp21.Y', N'U') IS NOT NULL DROP TABLE sp21.Y;
IF OBJECT_ID(N'sp21.X', N'U') IS NOT NULL DROP TABLE sp21.X;
GO
IF SCHEMA_ID(N'sp21') IS NOT NULL DROP SCHEMA sp21;
GO
SELECT 'sp21_schema_left' AS what, COUNT(*) AS n FROM sys.schemas WHERE name = N'sp21';
GO
