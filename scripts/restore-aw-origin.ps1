# .bak을 컨테이너 백업 폴더로 복사하고 CLI(dbripple restore)로 복원한다. 사용: .\scripts\restore-aw-origin.ps1 [-BakName AdventureWorks2025.bak] [-DbName AW_Origin]
# 복원 SQL(WITH MOVE)은 C8과 같은 코드(BaselineSql.Restore)로 만든다. 연결 문자열은 env.ps1이 .env로 만들고 출력하지 않는다.
param([string]$BakName = 'AdventureWorks2025.bak', [string]$DbName = 'AW_Origin')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
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
exit 0
