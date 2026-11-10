# SQL Server 컨테이너 기동 후 healthy까지 대기. 사용: .\scripts\db-up.ps1 [-TimeoutSec 180]
param([int]$TimeoutSec = 180)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Test-Path '.env')) { throw '.env가 없습니다. .env.example을 복사해 비밀번호를 채우세요.' }
# PowerShell 5.1은 네이티브 명령의 stderr(진행 메시지)를 오류로 바꿀 수 있어, 이 구간은 종료 코드로만 판정한다
$ErrorActionPreference = 'Continue'
docker compose up -d
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ne 0) { throw 'docker compose up 실패' }
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    $id = (docker compose ps -q sql)
    if ($id) {
        $status = (docker inspect -f '{{.State.Health.Status}}' $id)
        Write-Host "health: $status"
        if ($status -eq 'healthy') { exit 0 }
    }
    Start-Sleep -Seconds 5
}
docker compose logs --tail 30 sql
throw "healthy 대기 시간 초과($TimeoutSec s)"
