# SQL 파일을 컨테이너 안 sqlcmd로 실행한다(run-sql.ps1과 같은 방식, 서비스 선택 + 비밀번호 치환 추가).
# 사용(저장소 루트): .\spikes\sql\sp_container-run.ps1 <파일> [-Database master] [-Service sql-origin|sql] [-Log spikes\logs\x.log] [-ContinueOnError] [-PasswordFromEnv ORIGIN_SQL_PASSWORD]
#   -Service sql-origin: SP17 등의 "원본 서버" 모사 컨테이너(SQL Server 2022, compose profile origin). 기본값.
#   -Service sql      : 샌드박스 컨테이너(SQL Server 2025). run-sql.ps1과 같다.
#   -PasswordFromEnv : SQL 파일 안의 {{PASSWORD}} 토큰을 이 환경 변수 값으로 바꾼다(제한 로그인 만들기용). 값은 화면·로그에 출력하지 않는다.
#                      환경 변수는 먼저 `. .\scripts\env.ps1`로 .env에서 읽어 둔다(ORIGIN_SQL_PASSWORD). 값이 없으면 중단한다.
# 비밀번호(sa)는 컨테이너 환경 변수를 쓴다(호스트 명령줄·출력에 노출 없음). 토큰을 바꾼 임시 SQL 파일은 컨테이너로 복사해 실행한 뒤 호스트·컨테이너에서 지운다.
# sqlcmd -I: QUOTED_IDENTIFIER ON(제품 SqlClient와 같게). 실행은 종료 코드로 판정한다.
param(
    [Parameter(Mandatory)][string]$File,
    [string]$Database = 'master',
    [ValidateSet('sql-origin', 'sql')][string]$Service = 'sql-origin',
    [string]$Log,
    [switch]$ContinueOnError,
    [string]$PasswordFromEnv
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
if (-not (Test-Path $File)) { throw "$File 없음" }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "DB 이름 형식 오류: $Database" }
$env:COMPOSE_PROFILES = 'origin'   # profile이 붙은 sql-origin 서비스를 compose 명령이 찾게 한다

$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$text = [System.IO.File]::ReadAllText((Resolve-Path $File), $utf8)
if ($PasswordFromEnv) {
    $pw = [Environment]::GetEnvironmentVariable($PasswordFromEnv, 'Process')
    if ([string]::IsNullOrEmpty($pw)) { throw "환경 변수 $PasswordFromEnv 가 비어 있습니다(. .\scripts\env.ps1 로 .env를 읽었는지, .env에 값이 있는지 확인)." }
    $text = $text.Replace('{{PASSWORD}}', $pw.Replace("'", "''"))
}

# PowerShell 5.1은 네이티브 명령 표준 입력에 UTF-8 BOM을 붙이므로, 임시 파일을 복사해 -i로 실행한다(run-sql.ps1과 같음)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dbr-' + [guid]::NewGuid().ToString('N') + '.sql')
$remote = '/tmp/' + [IO.Path]::GetFileName($tmp)
[IO.File]::WriteAllText($tmp, $text, $utf8)
$stopFlag = if ($ContinueOnError) { '' } else { '-b ' }
# 2022 이미지는 mssql-tools18 또는 mssql-tools 중 있는 쪽을 쓴다
$sqlcmd = 'S=/opt/mssql-tools18/bin/sqlcmd; [ -x $S ] || S=/opt/mssql-tools/bin/sqlcmd; $S -C -I -S localhost -U sa -P "$MSSQL_SA_PASSWORD" ' + $stopFlag + '-W -f 65001 -d ' + $Database + ' -i ' + $remote
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$ErrorActionPreference = 'Continue'  # 네이티브 stderr를 오류로 바꾸지 않게 하고 종료 코드로 판정
$code = 1
try {
    docker compose cp $tmp "${Service}:$remote" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '컨테이너로 SQL 파일 복사 실패(컨테이너가 떠 있는지 확인)' }
    $out = @(docker compose exec -T $Service bash -c $sqlcmd | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
} finally {
    docker compose exec -T $Service rm -f $remote 2>$null | Out-Null
    Remove-Item $tmp -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Stop'
}
$sw.Stop()
$out | Out-Host
if ($Log) {
    $logPath = Join-Path $root $Log
    New-Item -ItemType Directory -Force (Split-Path -Parent $logPath) | Out-Null
    $header = "# $File service=$Service db=$Database at=$(Get-Date -Format o) elapsedSec=$([math]::Round($sw.Elapsed.TotalSeconds, 2)) exit=$code"
    [System.IO.File]::WriteAllLines($logPath, [string[]](@($header) + $out), $utf8)
}
if ($code -ne 0) { throw "sqlcmd 실패(exit $code)" }
exit 0
