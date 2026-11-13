/* SP21 — KEYSET_HASH 검출력(2) 준비: 원본 모사 컨테이너의 CascadeLab에 sp21 스키마(X 대상, Y 미리보기 밖, Z 무관)를 만든다. 트리거는 아직 없다.
   실행(저장소 루트, sa): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp21_21_outside_trigger_setup.sql -Database CascadeLab -Log spikes\logs\sp21-21-setup.log
   이어서 docs/spikes/SP21.md 2단계: Host에서 기준선 새로 고침 → 미리보기 `UPDATE sp21.X SET v = v + 1 WHERE id = 1;` → (이 폴더) sp21_22로 원본에 트리거 추가 → [적용].
   만든 스키마는 sp21_23으로 지운다. 이 스키마는 원본 모사 컨테이너에만 있다(저장소의 CascadeLab 정답·cl*.sql에는 없다).
   dbr_origin_user가 있으면 sp21 스키마 DML 권한이 db_datareader·db_datawriter로 이미 있다. 실행하지 않은 상태에서 쓴 스크립트다(정적 검토만). */
USE CascadeLab;
GO
IF SCHEMA_ID(N'sp21') IS NOT NULL THROW 50000, N'sp21 스키마가 이미 있다: sp21_23_outside_trigger_drop.sql로 먼저 지운다', 1;
GO
CREATE SCHEMA sp21;
GO
CREATE TABLE sp21.X (id int NOT NULL CONSTRAINT PK_sp21_X PRIMARY KEY, v int NOT NULL);
CREATE TABLE sp21.Y (id int NOT NULL CONSTRAINT PK_sp21_Y PRIMARY KEY, v int NOT NULL);
CREATE TABLE sp21.Z (id int NOT NULL CONSTRAINT PK_sp21_Z PRIMARY KEY, v int NOT NULL);
GO
INSERT sp21.X (id, v) VALUES (1, 0), (2, 0);
INSERT sp21.Y (id, v) VALUES (1, 0), (2, 0);
INSERT sp21.Z (id, v) VALUES (1, 0);
GO
SELECT 'X' AS t, id, v FROM sp21.X UNION ALL SELECT 'Y', id, v FROM sp21.Y UNION ALL SELECT 'Z', id, v FROM sp21.Z ORDER BY 1, 2;
GO
