# .bak을 컨테이너 백업 폴더로 복사하고 CLI(dbripple restore)로 복원한 뒤, 원본 실행 사용자 dbr_preview_user를 멱등으로 맞춘다.
# 사용: .\scripts\restore-aw-origin.ps1 [-BakName AdventureWorks2025.bak] [-DbName AW_Origin] [-PreviewUserOnly]
#   -PreviewUserOnly: 복원은 건너뛰고 실행 사용자 단계만 한다(이미 복원한 원본에 Q30 단계만 더할 때).
# 복원 SQL(WITH MOVE)은 C8과 같은 코드(BaselineSql.Restore)로 만든다. 연결 문자열은 env.ps1이 .env로 만들고 출력하지 않는다.
# 실행 사용자(Q30, 오케스트레이터 결정 2026-11-11): [적용]은 원본에서 EXECUTE AS USER = 'dbr_preview_user'로 실행하고, 원본에 DDL을 하지 않는다.
#   그래서 원본 준비 단계인 여기서 만든다. 문장은 src/DbRipple.Core/Baseline/PreviewUserSql.cs EnsureBatch와 같다
#   (WITHOUT LOGIN, 기본 스키마 dbo, 사용자 테이블 스키마마다 SELECT·INSERT·UPDATE·DELETE만, audit 스키마 권한 없음, 역할 소속이면 50000).
#   스키마 목록은 고정 목록 대신 사용자 테이블이 있는 스키마(sys.tables is_ms_shipped = 0, audit 제외)로 정한다.
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
SELECT COUNT(*) AS non_schema_permissions
FROM sys.database_permissions AS p
WHERE p.grantee_principal_id = USER_ID(N'dbr_preview_user') AND p.class <> 3 AND p.permission_name <> N'CONNECT';
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
