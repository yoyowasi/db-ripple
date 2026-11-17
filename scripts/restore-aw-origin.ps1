# .bak을 컨테이너 백업 폴더로 복사하고 CLI(dbripple restore)로 복원한 뒤, 원본 실행 사용자 dbr_preview_user를 멱등으로 맞춘다.
# 사용: .\scripts\restore-aw-origin.ps1 [-BakName AdventureWorks2025.bak] [-DbName AW_Origin] [-PreviewUserOnly]
#   -PreviewUserOnly: 복원은 건너뛰고 실행 사용자 단계만 한다(이미 복원한 원본에 Q30 단계만 더할 때).
# 복원 SQL(WITH MOVE)은 C8과 같은 코드(BaselineSql.Restore)로 만든다. 연결 문자열은 env.ps1이 .env로 만들고 출력하지 않는다.
# 실행 사용자(Q30, 오케스트레이터 결정 2026-11-11): [적용]은 원본에서 EXECUTE AS USER = 'dbr_preview_user'로 실행하고, 원본에 DDL을 하지 않는다.
#   그래서 원본 준비 단계인 여기서 만든다. 문장은 src/DbRipple.Core/Baseline/PreviewUserSql.cs EnsureBatch와 같다
#   (WITHOUT LOGIN, 기본 스키마 dbo, 사용자 테이블 스키마마다 SELECT·INSERT·UPDATE·DELETE만, audit 스키마 권한 없음, 역할 소속이면 50000).
#   스키마 목록은 고정 목록 대신 사용자 테이블이 있는 스키마(sys.tables is_ms_shipped = 0, audit 제외)로 정한다.
#   EXEC [적용](2026-11-17): PreviewUserSql과 같은 규칙으로 프로시저마다 EXECUTE를 준다(EXECUTE AS CALLER인 T-SQL 사용자 프로시저,
#   본문을 읽을 수 있는 것, audit 스키마 제외, 이미 있으면 건너뜀). 스키마 단위 EXECUTE는 함수까지 열리므로 주지 않는다.
#   PreviewUserSql과 달리 모듈 서명(ADD SIGNATURE)이 있는 프로시저는 뺀다(서명이 DML 전용 권한 경계를 넘을 수 있음, 2026-11-17).
#   [적용]은 원본에 GRANT하지 않고, 이 권한이 없으면 PROC_EXECUTE_PERMISSION_MISSING으로 막는다.
#   AW의 DDL 트리거 ddlDatabaseTriggerLog가 CREATE USER·GRANT를 원본 dbo.DatabaseLog에 기록한다. 기준선은 원본 백업에서 만들므로
#   이 단계 뒤에 기준선을 새로 고친다(POST /api/sandbox/refresh). 이미 있으면 DDL을 하지 않아 DatabaseLog 행도 늘지 않는다.
param([string]$BakName = 'AdventureWorks2025.bak', [string]$DbName = 'AW_Origin', [switch]$PreviewUserOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($DbName -notmatch '^[A-Za-z0-9_]+$') { throw "DB 이름 형식 오류: $DbName" }

function Set-PreviewUser([string]$Database) {
    $sql = @'
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ddl bit = 0;
IF USER_ID(N'dbr_preview_user') IS NULL
BEGIN
    CREATE USER [dbr_preview_user] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo];
    SET @ddl = 1;
END;
IF EXISTS (SELECT 1 FROM sys.database_role_members WHERE member_principal_id = USER_ID(N'dbr_preview_user'))
    THROW 50000, N'dbr_preview_user is a member of a database role (DML-only assumption broken).', 1;
DECLARE @grants nvarchar(max);
SELECT @grants = STRING_AGG(CAST(N'GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::' + QUOTENAME(s.name) + N' TO [dbr_preview_user];' AS nvarchar(max)), NCHAR(10))
                 WITHIN GROUP (ORDER BY s.name)
FROM sys.schemas AS s
WHERE s.name <> N'audit'
  AND EXISTS (SELECT 1 FROM sys.tables AS t WHERE t.schema_id = s.schema_id AND t.is_ms_shipped = 0)
  AND (SELECT COUNT(*) FROM sys.database_permissions AS p
       WHERE p.grantee_principal_id = USER_ID(N'dbr_preview_user') AND p.class = 3 AND p.major_id = s.schema_id
         AND p.state = 'G' AND p.permission_name IN (N'SELECT', N'INSERT', N'UPDATE', N'DELETE')) < 4;
IF @grants IS NOT NULL
BEGIN
    EXEC sys.sp_executesql @grants;
    SET @ddl = 1;
END;
-- EXEC apply: EXECUTE per procedure (same rule as PreviewUserSql.EnsureBatch; never schema-level EXECUTE, which would also open functions)
DECLARE @exec_grants nvarchar(max);
SELECT @exec_grants = STRING_AGG(CAST(N'GRANT EXECUTE ON OBJECT::' + QUOTENAME(SCHEMA_NAME(p.schema_id)) + N'.' + QUOTENAME(p.name) + N' TO [dbr_preview_user];' AS nvarchar(max)), NCHAR(10))
                      WITHIN GROUP (ORDER BY SCHEMA_NAME(p.schema_id), p.name)
FROM sys.procedures AS p
INNER JOIN sys.sql_modules AS m ON m.object_id = p.object_id
WHERE p.is_ms_shipped = 0 AND p.type = 'P' AND m.definition IS NOT NULL AND m.execute_as_principal_id IS NULL
  AND SCHEMA_NAME(p.schema_id) <> N'audit'
  -- signed modules (ADD SIGNATURE) can carry certificate-user permissions beyond schema DML: never grant (apply blocks them: PROC_SIGNED_NOT_SUPPORTED)
  AND NOT EXISTS (SELECT 1 FROM sys.crypt_properties AS c WHERE c.class = 1 AND c.major_id = p.object_id)
  AND NOT EXISTS (SELECT 1 FROM sys.database_permissions AS d
                  WHERE d.grantee_principal_id = USER_ID(N'dbr_preview_user') AND d.class = 1 AND d.major_id = p.object_id
                    AND d.minor_id = 0 AND d.permission_name = N'EXECUTE' AND d.state = 'G');
IF @exec_grants IS NOT NULL
BEGIN
    EXEC sys.sp_executesql @exec_grants;
    SET @ddl = 1;
END;
-- verification: user, schema grants (expected: every user-table schema except audit has DELETE,INSERT,SELECT,UPDATE), no role membership
SELECT @ddl AS ddl_this_run;
SELECT dp.name, dp.type_desc, dp.authentication_type_desc, dp.default_schema_name
FROM sys.database_principals AS dp
WHERE dp.name = N'dbr_preview_user';
SELECT s.name AS schema_name,
       STRING_AGG(CAST(p.permission_name AS nvarchar(max)), N',') WITHIN GROUP (ORDER BY p.permission_name) AS granted
FROM sys.database_permissions AS p
INNER JOIN sys.schemas AS s ON s.schema_id = p.major_id
WHERE p.grantee_principal_id = USER_ID(N'dbr_preview_user') AND p.class = 3 AND p.state = 'G'
GROUP BY s.name
ORDER BY s.name;
-- procedure EXECUTE grants (expected: one per unsigned CALLER T-SQL user procedure outside audit)
SELECT COUNT(*) AS procedure_execute_grants
FROM sys.database_permissions AS p
WHERE p.grantee_principal_id = USER_ID(N'dbr_preview_user') AND p.class = 1 AND p.minor_id = 0 AND p.permission_name = N'EXECUTE' AND p.state = 'G';
-- anything else besides CONNECT, schema DML and procedure EXECUTE (expected: 0)
SELECT COUNT(*) AS other_permissions
FROM sys.database_permissions AS p
WHERE p.grantee_principal_id = USER_ID(N'dbr_preview_user') AND p.class <> 3 AND p.permission_name <> N'CONNECT'
  AND NOT (p.class = 1 AND p.minor_id = 0 AND p.permission_name = N'EXECUTE' AND p.state = 'G'
           AND EXISTS (SELECT 1 FROM sys.procedures AS x WHERE x.object_id = p.major_id));
'@
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('dbr-preview-user-' + [guid]::NewGuid().ToString('N') + '.sql')
    [IO.File]::WriteAllText($tmp, $sql, (New-Object System.Text.UTF8Encoding($false)))
    try { & (Join-Path $PSScriptRoot 'run-sql.ps1') $tmp -Database $Database }
    finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

if ($PreviewUserOnly) {
    Set-PreviewUser $DbName
    exit 0
}

$local = Join-Path $root ".data\backup\$BakName"
if (-not (Test-Path $local)) { throw "$local 없음. fetch-adventureworks.ps1을 먼저 실행하세요." }
. (Join-Path $PSScriptRoot 'env.ps1')
$remote = "/var/opt/mssql/backup/$BakName"

$ErrorActionPreference = 'Continue'  # 네이티브 stderr(진행 메시지)를 오류로 바꾸지 않고 종료 코드로 판정
docker compose exec -T -u root sql mkdir -p /var/opt/mssql/backup 2>$null | Out-Null
$mk = $LASTEXITCODE
docker compose cp $local "sql:$remote" 2>$null | Out-Null
$cp = $LASTEXITCODE
docker compose exec -T -u root sql chown -R mssql:root /var/opt/mssql/backup 2>$null | Out-Null  # 폴더째 바꿔야 BACKUP TO DISK가 쓸 수 있다
$ch = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($mk -ne 0 -or $cp -ne 0 -or $ch -ne 0) { throw "bak 준비 실패(mkdir=$mk, cp=$cp, chown=$ch)" }

dotnet run --project (Join-Path $root 'src\DbRipple.Cli') -- restore --backup $remote --database $DbName
if ($LASTEXITCODE -ne 0) { throw "복원 실패(exit $LASTEXITCODE)" }

# 원본 실행 사용자(Q30). 실패하면 throw로 멈춘다(run-sql.ps1이 sqlcmd 종료 코드로 판정).
Set-PreviewUser $DbName
exit 0
