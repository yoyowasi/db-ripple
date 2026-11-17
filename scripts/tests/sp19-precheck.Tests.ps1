# Linux precheck regression: actual policy, simulated native SQL/Docker/df boundaries.
param([string]$ScriptPath = (Join-Path $PSScriptRoot '../../spikes/sql/sp19_00_run.ps1'))
$ErrorActionPreference='Stop'
$source=[IO.File]::ReadAllText([IO.Path]::GetFullPath($ScriptPath))
$prefix=$source.Substring(0,$source.IndexOf('# ---- 1. 원본 준비'))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($prefix,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'SP19 precheck parse failed' }
$node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Read-Precheck'},$true)
$replacement=@'
function Read-Precheck([string]$service) { return @{major='17';version='17.0.5035.5';committed_target_mb='1500';sp19_db=@();login_dbr_origin_user='1';db_CascadeLab='1'} }
'@
$prefix=$prefix.Remove($node.Extent.StartOffset,$node.Extent.EndOffset-$node.Extent.StartOffset).Insert($node.Extent.StartOffset,$replacement)
$boundary=@'
function docker {
    $global:LASTEXITCODE=0
    $a=@($args)
    if($a[0] -eq 'inspect' -and $a[2] -like '*State.Running*') { if($env:SP19_CASE -eq 'stopped'){return 'false'};return 'true' }
    if($a[0] -eq 'inspect') {
        if($a[-1] -eq 'dbripple-sql') {if($env:SP19_CASE -eq 'wrong-bind'){return '/var/opt/mssql/import=/wrong/path;'};return "/var/opt/mssql/import=$importDir;"}
        return "/var/opt/mssql/backup=$backupDir;"
    }
    if($a[0] -eq 'info'){return '/sp19-missing-docker-root'}
    if($a[0] -eq 'exec' -and $a[2] -eq 'df') {
        if($env:SP19_CASE -eq 'df-fail') {$global:LASTEXITCODE=1;return}
        if($env:SP19_CASE -eq 'df-empty') {return 'Filesystem 1024-blocks Used Available Capacity Mounted'}
        $kb=5*1048576
        if($env:SP19_CASE -eq 'combined-low'){$kb=3*1048576}
        if($env:SP19_CASE -in 'same-low','separate-docker-low'){$kb=2*1048576}
        $fs=if($env:SP19_CASE -like 'separate-*'){'dockerfs'}else{'fixturefs'}
        return @('Filesystem 1024-blocks Used Available Capacity Mounted',"$fs 10485760 1 $kb 20% /var/opt/mssql")
    }
    if($a[0] -eq 'exec' -and $a[2] -eq 'test') {if($env:SP19_CASE -eq 'import-unreadable'){$global:LASTEXITCODE=1};return}
    if($a[0] -eq 'exec' -and $a[2] -eq 'bash') {
        if($env:SP19_CASE -eq 'backup-read-denied'){return}
        if($env:SP19_CASE -eq 'backup-unwritable'){$global:LASTEXITCODE=1;return}
        $name=($a[-1] -split '/')[-1]
        [IO.File]::WriteAllText((Join-Path $backupDir $name),'x');return
    }
    if($a[0] -eq 'exec' -and $a[2] -eq 'rm'){Remove-Item (Join-Path $backupDir ($a[-1] -split '/')[-1]) -ErrorAction SilentlyContinue;return}
    throw 'Unexpected Docker boundary'
}
function df {
    $global:LASTEXITCODE=0
    $kb=5*1048576
        if($env:SP19_CASE -eq 'combined-low'){$kb=3*1048576}
    if($env:SP19_CASE -in 'same-low','separate-repo-low'){$kb=1*1048576}
    @('Filesystem 1024-blocks Used Available Capacity Mounted',"fixturefs 10485760 1 $kb 20% /")
}
function Get-Content {
    if($args[0] -eq '/proc/meminfo') {
        if($env:SP19_CASE -eq 'memory-low'){return 'MemAvailable: 1048576 kB'}
        return 'MemAvailable: 2097152 kB'
    }
    Microsoft.PowerShell.Management\Get-Content @args
}
'@
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('sp19-precheck-tests-'+[guid]::NewGuid().ToString('N'))
$cases=@(
 @('same-ok','',@()),@('same-low','디스크 부족',@()),@('combined-low','디스크 부족',@()),
 @('separate-ok','',@()),@('separate-repo-low','저장소 디스크 부족',@()),@('separate-docker-low','Docker 디스크 부족',@()),
 @('df-fail','디스크 여유 확인 실패',@()),@('df-empty','디스크 여유 확인 실패',@()),@('memory-low','메모리 부족',@()),@('wrong-bind','바인드 원본',@()),
 @('stopped','떠 있지 않음',@()),@('import-unreadable','파일을 읽지 못함',@()),@('backup-unwritable','쓰지 못함',@()),
 @('backup-read-denied','.bak을 읽지 못함',@()),
 @('unknown-size','알 수 없는 크기',@('-Sizes','unknown')),
 @('port-busy','이미 쓰이고 있음',@())
)
$saved=@{};foreach($k in 'MSSQL_SA_PASSWORD','ORIGIN_SQL_SERVER','ORIGIN_SQL_USER','ORIGIN_SQL_PASSWORD','SP19_CASE','SP19_ORIGIN_CONTAINER','SP19_SANDBOX_CONTAINER'){$saved[$k]=[Environment]::GetEnvironmentVariable($k)}
$cwd=Get-Location
try {
 foreach($case in $cases) {
  $name=$case[0];$fixture=Join-Path $scratch $name
  New-Item -ItemType Directory -Force (Join-Path $fixture 'spikes/sql'),(Join-Path $fixture 'scripts') | Out-Null
  $file=Join-Path $fixture 'spikes/sql/sp19_00_run.ps1'
  # Native SQL/Docker/df boundaries only; retain original bind/port/resource policy.
  $copy=$prefix.Replace("# ---- 0. 사전 확인 ----",$boundary+"`n# ---- 0. 사전 확인 ----")
  [IO.File]::WriteAllText($file,$copy,[Text.UTF8Encoding]::new($true))
  [IO.File]::WriteAllText((Join-Path $fixture 'scripts/env.ps1'),@'
$env:MSSQL_SA_PASSWORD='fixture-only';$env:ORIGIN_SQL_SERVER='127.0.0.1,1';$env:ORIGIN_SQL_USER='fixture';$env:ORIGIN_SQL_PASSWORD='fixture-only'
'@)
  $env:SP19_CASE=$name
  $listener=$null;$port=0
  if($name -eq 'port-busy'){$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port}
  $err=''
  $invoke = @{ PrecheckOnly = $true; Port = $port }; if ($name -eq 'unknown-size') { $invoke['Sizes'] = @('unknown') }
  try { & $file @invoke *> (Join-Path $fixture 'console.txt') } catch { $err=$_.Exception.Message } finally {if($listener){$listener.Stop()}}
  if(($case[1] -eq '' -and $err) -or ($case[1] -ne '' -and $err -notlike ('*'+$case[1]+'*'))){throw "FAIL $name expected=$($case[1]) actual=$err"}
  if ($case[1] -eq '' -and [IO.File]::ReadAllText((Join-Path $fixture 'console.txt')) -notlike '*사전 확인만 하고 끝냄*') { throw "FAIL $name precheck did not finish" }
  "PASS $name"
 }
 "Precheck safety scenarios: $($cases.Count)/$($cases.Count) PASS (native boundaries simulated; no live SQL)"
} finally {
 Set-Location $cwd
 foreach($k in $saved.Keys){[Environment]::SetEnvironmentVariable($k,$saved[$k])}
 if(Test-Path $scratch){Remove-Item $scratch -Recurse -Force}
}
