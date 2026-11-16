/* SP18 보조(2026-11-16 실행 중 추가) — 제품 수준 재현 준비: 제한 로그인에 CREATE ANY DATABASE만 임시로 준다(db_owner·DB 소유자 아님).
   이 상태에서 [적용]의 검증 모드 결정(ApplySteps.cs OriginSnapshot.CapabilitySql)은 "스냅샷 가능"으로 보지만, 실제 스냅샷은 1817로 실패한다(sp18_10 s9).
   실행(저장소 루트, sa, 원본 모사 컨테이너): .\spikes\sql\sp_container-run.ps1 spikes\sql\sp18_30_repro_grant.sql -Log spikes\logs\sp18-30-grant.log
   끝나면 반드시: .\spikes\sql\sp_container-run.ps1 spikes\sql\sp18_39_repro_revoke.sql -Log spikes\logs\sp18-39-revoke.log */
SET NOCOUNT ON;
USE master;
GO
GRANT CREATE ANY DATABASE TO [dbr_origin_user];
GO
EXECUTE AS LOGIN = N'dbr_origin_user';
SELECT 'capability_as_limited_login' AS what,
       CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE') = 1 OR HAS_PERMS_BY_NAME(N'master', N'DATABASE', N'CREATE DATABASE') = 1 THEN 1 ELSE 0 END AS can_create_database,
       IS_ROLEMEMBER(N'db_owner') AS db_owner_in_master;
REVERT;
GO
SELECT 'cascadelab_db_owner_member' AS what, COUNT(*) AS n
FROM CascadeLab.sys.database_role_members AS m
INNER JOIN CascadeLab.sys.database_principals AS r ON r.principal_id = m.role_principal_id
INNER JOIN CascadeLab.sys.database_principals AS u ON u.principal_id = m.member_principal_id
WHERE r.name = N'db_owner' AND u.name = N'dbr_origin_user';
GO
