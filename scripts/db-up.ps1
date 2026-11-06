# SQL Server 컨테이너 기동 후 healthy까지 대기. 사용: .\scripts\db-up.ps1 [-TimeoutSec 180]
param([int]$TimeoutSec = 180)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Test-Path '.env')) { throw '.env가 없습니다. .env.example을 복사해 비밀번호를 채우세요.' }
docker compose up -d
if ($LASTEXITCODE -ne 0) { throw 'docker compose up 실패' }
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
