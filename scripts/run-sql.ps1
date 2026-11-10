# .sql 파일을 컨테이너 안 sqlcmd로 실행한다. 비밀번호는 컨테이너 환경 변수를 쓴다(호스트 명령줄·출력에 노출 없음).
# 사용: .\scripts\run-sql.ps1 spikes\sql\sp0-edition-snapshot.sql [-Database SpikeLab_SP1] [-Part 1|2] [-Log spikes\logs\sp0.log] [-ContinueOnError] [-Measure]
#   -Part: '-- PART 2' 줄을 경계로 앞(1) 또는 뒤(2)만 실행한다.
#   -ContinueOnError: sqlcmd -b를 빼서 오류가 나도 다음 배치를 계속 실행한다(의도한 오류를 관찰하는 스크립트용).
param(
    [Parameter(Mandatory)][string]$File,
    [string]$Database = 'master',
    [ValidateSet(0, 1, 2)][int]$Part = 0,
    [string]$Log,
    [switch]$ContinueOnError,
    [switch]$Measure
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Test-Path $File)) { throw "$File 없음" }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "DB 이름 형식 오류: $Database" }

$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$text = [System.IO.File]::ReadAllText((Resolve-Path $File), $utf8)
if ($Part -ne 0) {
    $m = [regex]::Match($text, '(?m)^-- PART 2.*$')
    if (-not $m.Success) { throw "'-- PART 2' 표시가 없습니다: $File" }
    $text = if ($Part -eq 1) { $text.Substring(0, $m.Index) } else { $text.Substring($m.Index) }
}

# PowerShell 5.1은 네이티브 명령 표준 입력에 UTF-8 BOM을 붙이므로, 표준 입력 대신 임시 파일을 복사해 -i로 실행한다
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("dbr-" + [guid]::NewGuid().ToString('N') + '.sql')
$remote = '/tmp/' + [IO.Path]::GetFileName($tmp)
[IO.File]::WriteAllText($tmp, $text, $utf8)
$stopFlag = if ($ContinueOnError) { '' } else { '-b ' }
# -I: QUOTED_IDENTIFIER ON(sqlcmd 기본은 OFF). OFF면 계산 열 인덱스가 있는 테이블의 DML이 1934로 실패하고, 만든 모듈도 OFF로 저장된다(제품 SqlClient는 ON)
$sqlcmd = '/opt/mssql-tools18/bin/sqlcmd -C -I -S localhost -U sa -P "$MSSQL_SA_PASSWORD" ' + $stopFlag + '-W -f 65001 -d ' + $Database + ' -i ' + $remote
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$ErrorActionPreference = 'Continue'  # 네이티브 stderr를 오류로 바꾸지 않게 하고 종료 코드로 판정
docker compose cp $tmp "sql:$remote" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { Remove-Item $tmp; throw '컨테이너로 SQL 파일 복사 실패' }
$out = @(docker compose exec -T sql bash -c $sqlcmd | ForEach-Object { "$_" })
$code = $LASTEXITCODE
docker compose exec -T sql rm -f $remote 2>$null | Out-Null
$ErrorActionPreference = 'Stop'
Remove-Item $tmp
$sw.Stop()
$out | Out-Host
if ($Log) {
    $logPath = Join-Path $root $Log
    New-Item -ItemType Directory -Force (Split-Path -Parent $logPath) | Out-Null
    $header = "# $File db=$Database part=$Part at=$(Get-Date -Format o) elapsedSec=$([math]::Round($sw.Elapsed.TotalSeconds, 2)) exit=$code"
    [System.IO.File]::WriteAllLines($logPath, [string[]](@($header) + $out), $utf8)
}
if ($Measure) { Write-Host ("총 소요: {0:N2}s" -f $sw.Elapsed.TotalSeconds) }
if ($code -ne 0) { throw "sqlcmd 실패(exit $code)" }
exit 0
