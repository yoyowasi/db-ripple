# .bak을 컨테이너로 복사하고 AW_Origin으로 복원한다. 사용: .\scripts\restore-aw-origin.ps1 [-BakName AdventureWorks2025.bak]
# sqlcmd는 컨테이너 안에서 실행하며 비밀번호는 컨테이너 환경 변수를 쓴다(호스트 명령줄에 노출 없음).
param([string]$BakName = 'AdventureWorks2025.bak', [string]$DbName = 'AW_Origin')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$local = Join-Path $root ".data\backup\$BakName"
if (-not (Test-Path $local)) { throw "$local 없음. fetch-adventureworks.ps1을 먼저 실행하세요." }
$sqlcmd = '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b'
$cid = (docker compose ps -q sql)
if (-not $cid) { throw '컨테이너가 없습니다. db-up.ps1을 먼저 실행하세요.' }

docker compose exec -T -u root sql mkdir -p /var/opt/mssql/backup
docker cp $local "${cid}:/var/opt/mssql/backup/$BakName"
docker compose exec -T -u root sql chown -R mssql:root /var/opt/mssql/backup
if ($LASTEXITCODE -ne 0) { throw 'bak 복사 실패' }
$bak = "/var/opt/mssql/backup/$BakName"

# 1) FILELISTONLY: 구분자 | 로 받아 LogicalName|PhysicalName|Type 파싱
$q = "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK=N'$bak'"
$lines = docker compose exec -T sql bash -c "$sqlcmd -h -1 -W -s '|' -Q `"$q`""
if ($LASTEXITCODE -ne 0) { throw 'FILELISTONLY 실패' }
$files = @()
foreach ($l in $lines) {
    $p = "$l".Split('|')
    if ($p.Count -ge 3 -and $p[2].Trim() -match '^[DLSFC]$') {
        $files += [pscustomobject]@{ Logical = $p[0].Trim(); Physical = $p[1].Trim(); Type = $p[2].Trim() }
    }
}
if ($files.Count -eq 0) { throw 'FILELISTONLY 결과를 해석하지 못했습니다.' }
$files | Format-Table | Out-String | Write-Host

# 2) WITH MOVE: 파일명은 DB명_논리이름 + 원래 확장자(FILESTREAM(S)는 확장자 없는 폴더)
$moves = foreach ($f in $files) {
    $ext = [IO.Path]::GetExtension($f.Physical)
    if ($f.Type -eq 'S') { $ext = '' }
    $name = "${DbName}_$($f.Logical)$ext"
    "MOVE N'$($f.Logical)' TO N'/var/opt/mssql/data/$name'"
}
$sql = @"
RESTORE DATABASE [$DbName] FROM DISK = N'$bak'
WITH $($moves -join ",`n  "), STATS = 10, REPLACE;
GO
SELECT name, state_desc, recovery_model_desc, compatibility_level FROM sys.databases WHERE name = N'$DbName';
GO
"@
$sql | docker compose exec -T sql bash -c $sqlcmd
if ($LASTEXITCODE -ne 0) { throw 'RESTORE 실패' }
Write-Host "$DbName 복원 완료"
