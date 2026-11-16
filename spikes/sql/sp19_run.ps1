# SP19 — SQL 파일을 컨테이너 안 sqlcmd로 실행한다(sp_container-run.ps1과 같은 방식 + sqlcmd 변수 -Vars, 컨테이너 이름으로 실행).
# Windows PowerShell 5.1과 Linux PowerShell 7(Codex 클라우드) 둘 다에서 돈다: docker compose 서비스가 아니라 컨테이너 이름(docker cp·docker exec)을 쓴다.
#   그래서 compose로 띄운 컨테이너든 docker run으로 띄운 컨테이너든(클라우드 경량 이미지) 이름만 맞으면 된다.
# 사용(저장소 루트): ./spikes/sql/sp19_run.ps1 <파일> [-Service sql-origin|sql] [-Container 이름] [-Database master]
#                     [-Vars @{ DbName = 'SP19_0G3'; Scale = '0.35' }] [-Log spikes/logs/x.log] [-ContinueOnError] [-PasswordFromEnv ORIGIN_SQL_PASSWORD]
#   -Service sql-origin: 원본 모사(기본 컨테이너 dbripple-sql-origin, 환경 변수 SP19_ORIGIN_CONTAINER로 바꿈). -Service sql: 샌드박스(dbripple-sql, SP19_SANDBOX_CONTAINER).
#   -Vars: sqlcmd -v 이름=값. 값은 영문자·숫자·밑줄·점·하이픈만 허용(따옴표 없이 넘겨도 안전하게, 셸·SQL 주입 방지).
#   -PasswordFromEnv: SQL 파일 안의 {{PASSWORD}}를 그 환경 변수 값으로 바꾼다(제한 로그인 만들기용). 값은 화면·로그에 내지 않는다.
# 비밀번호(sa)는 컨테이너 환경 변수 MSSQL_SA_PASSWORD를 쓴다(호스트 명령줄·출력에 노출 없음). sqlcmd -I(QUOTED_IDENTIFIER ON), 종료 코드로 판정.
param(
    [Parameter(Mandatory)][string]$File,
    [string]$Database = 'master',
    [ValidateSet('sql-origin', 'sql')][string]$Service = 'sql-origin',
    [string]$Container,
    [hashtable]$Vars = @{},
    [string]$Log,
    [switch]$ContinueOnError,
    [string]$PasswordFromEnv
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
# 경로 구분자는 /로 통일한다(Windows의 .NET·PowerShell도 /를 받는다. Linux에서 \는 파일 이름 글자다)
$File = $File.Replace('\', '/')
if ($Log) { $Log = $Log.Replace('\', '/') }
if (-not (Test-Path $File)) { throw "$File 없음" }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "DB 이름 형식 오류: $Database" }
if (-not $Container) {
    $Container = if ($Service -eq 'sql') { if ($env:SP19_SANDBOX_CONTAINER) { $env:SP19_SANDBOX_CONTAINER } else { 'dbripple-sql' } }
                 else { if ($env:SP19_ORIGIN_CONTAINER) { $env:SP19_ORIGIN_CONTAINER } else { 'dbripple-sql-origin' } }
}
if ($Container -notmatch '^[A-Za-z0-9][A-Za-z0-9_.\-]*$') { throw "컨테이너 이름 형식 오류: $Container" }

$vArgs = ''
foreach ($k in $Vars.Keys) {
    $v = [string]$Vars[$k]
    if ($k -notmatch '^[A-Za-z][A-Za-z0-9_]*$' -or $v -notmatch '^[A-Za-z0-9_.\-]+$') { throw "sqlcmd 변수 형식 오류: $k" }
    $vArgs += " -v $k=$v"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$text = [System.IO.File]::ReadAllText((Resolve-Path $File).Path, $utf8)
if ($PasswordFromEnv) {
    $pw = [Environment]::GetEnvironmentVariable($PasswordFromEnv, 'Process')
    if ([string]::IsNullOrEmpty($pw)) { throw "환경 변수 $PasswordFromEnv 가 비어 있습니다(scripts/env.ps1로 .env를 읽었는지 확인)." }
    $text = $text.Replace('{{PASSWORD}}', $pw.Replace("'", "''"))
    $pw = $null
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dbr-' + [guid]::NewGuid().ToString('N') + '.sql')
$remote = '/tmp/' + [IO.Path]::GetFileName($tmp)
[IO.File]::WriteAllText($tmp, $text, $utf8)
$text = $null
$stopFlag = if ($ContinueOnError) { '' } else { '-b ' }
# 2022 이미지는 mssql-tools18 또는 mssql-tools 중 있는 쪽을 쓴다
$sqlcmd = 'S=/opt/mssql-tools18/bin/sqlcmd; [ -x $S ] || S=/opt/mssql-tools/bin/sqlcmd; $S -C -I -S localhost -U sa -P "$MSSQL_SA_PASSWORD" ' + $stopFlag + '-W -f 65001 -d ' + $Database + $vArgs + ' -i ' + $remote
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$ErrorActionPreference = 'Continue'   # 네이티브 stderr를 오류로 바꾸지 않게 하고 종료 코드로 판정
$code = 1
$out = @()
try {
    docker cp $tmp ($Container + ':' + $remote) 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "컨테이너 $Container 로 SQL 파일 복사 실패(컨테이너가 떠 있는지 확인)" }
    $out = @(docker exec $Container bash -c $sqlcmd | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
} finally {
    docker exec $Container rm -f $remote 2>$null | Out-Null
    Remove-Item $tmp -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Stop'
}
$sw.Stop()
$out | Out-Host
if ($Log) {
    $logPath = Join-Path $root $Log
    New-Item -ItemType Directory -Force (Split-Path -Parent $logPath) | Out-Null
    $varText = ($Vars.Keys | Sort-Object | ForEach-Object { "$_=$($Vars[$_])" }) -join ','
    $header = "# $File container=$Container db=$Database vars=$varText at=$(Get-Date -Format o) elapsedSec=$([math]::Round($sw.Elapsed.TotalSeconds, 2)) exit=$code"
    [System.IO.File]::WriteAllLines($logPath, [string[]](@($header) + $out), $utf8)
}
if ($code -ne 0) { throw "sqlcmd 실패(exit $code)" }
exit 0
