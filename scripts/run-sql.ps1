# spikes\sql의 .sql 파일을 컨테이너 안 sqlcmd로 실행한다. 사용: .\scripts\run-sql.ps1 spikes\sql\sp0-edition-snapshot.sql
# 비밀번호는 컨테이너 환경 변수를 쓴다(호스트 명령줄·출력에 노출 없음). 시간 측정은 -Measure.
param([Parameter(Mandatory)][string]$File, [switch]$Measure)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Test-Path $File)) { throw "$File 없음" }
$sqlcmd = '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b -W'
$run = { Get-Content -Raw -Encoding UTF8 $File | docker compose exec -T sql bash -c $sqlcmd }
if ($Measure) { $t = Measure-Command { & $run | Out-Host }; Write-Host ("총 소요: {0:N1}s" -f $t.TotalSeconds) } else { & $run }
if ($LASTEXITCODE -ne 0) { throw "sqlcmd 실패(exit $LASTEXITCODE)" }
