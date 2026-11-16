# SP19 전체 실행(가벼운 기본판, Codex 클라우드 Linux PowerShell 7과 Windows PowerShell 5.1 둘 다).
#   사전 확인(컨테이너·바인드 폴더 쓰기·포트·디스크·메모리·주버전) → 빌드 → XE 켜기 → 크기마다 [원본 DB 준비 → Host → Q1 기준선 → Q2·Q3 미리보기·[적용]
#   → Host 끔 → Q2 스냅샷 되돌리기 → Q3 해시 → 그 크기 DB 정리] → XE·새 .bak 정리 → 요약(summary.md).
# 기본 크기는 cl(CascadeLab)과 0g3(합성 약 0.3GB)뿐이다. 1g·aw는 -Sizes로 켠다. 2g·5g는 -AllowLarge까지 줘야 돈다(로컬 RAM·디스크 여유가 있을 때만).
#   aw는 원본 모사가 주버전 17(2025) 이상일 때만 된다(2022에는 AdventureWorks2025.bak 복원 불가, 3169). 아니면 이유를 적고 건너뛴다.
# 사용(저장소 루트):
#   pwsh -NoProfile -File spikes/sql/sp19_00_run.ps1 -PrecheckOnly          # 사전 확인만
#   pwsh -NoProfile -File spikes/sql/sp19_00_run.ps1                        # 기본: cl, 0g3, 3회
#   pwsh -NoProfile -File spikes/sql/sp19_00_run.ps1 -Sizes cl,0g3,1g       # 1GB 추가(디스크 여유 확인 뒤)
#   pwsh -NoProfile -File spikes/sql/sp19_00_run.ps1 -Sizes 2g,5g -AllowLarge -Tag local   # 선택 단계(큰 DB)
# 결과: spikes/logs/sp19-30-q1-<크기>-<Tag>.csv, sp19-40-<크기>-<Tag>.csv, sp19-<Tag>-*.log(커밋 대상, 비밀값·PC 이름 없음)
#       logs/home-test/<날짜>/sp19-<Tag>/(run.log, summary.md, 회차별 XE 명령 목록, API 응답 JSON, Host 콘솔. 커밋하지 않음)
param(
    [string[]]$Sizes = @('cl', '0g3'),
    [switch]$AllowLarge,
    [ValidateRange(1, 10)][int]$Runs = 3,
    [ValidatePattern('^[a-z0-9]+$')][string]$Tag = 'cloud',
    [string]$OriginContainer = 'dbripple-sql-origin',
    [string]$SandboxContainer = 'dbripple-sql',
    [int]$Port = 5080,
    [double]$DiskMarginGb = 1.5,
    [double]$MinMemAvailableGb = 1.5,
    [switch]$SkipBuild,
    [switch]$KeepDbs,
    [switch]$PrecheckOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$isWin = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$utf8 = New-Object System.Text.UTF8Encoding($false)
$sqlDir = Join-Path $root 'spikes'; $sqlDir = Join-Path $sqlDir 'sql'
$dataDir = Join-Path $root '.data'
$backupDir = Join-Path $dataDir 'origin-backup'
$importDir = Join-Path $dataDir 'import'
$logDir = 'logs/home-test/' + (Get-Date -Format 'yyyy-MM-dd') + '/sp19-' + $Tag
$logAbs = Join-Path $root $logDir
New-Item -ItemType Directory -Force $logAbs, $backupDir, $importDir | Out-Null
$runLog = Join-Path $logAbs 'run.log'
$env:SP19_ORIGIN_CONTAINER = $OriginContainer
$env:SP19_SANDBOX_CONTAINER = $SandboxContainer

function Say([string]$m) {
    $line = (Get-Date -Format 'HH:mm:ss') + ' ' + $m
    Write-Host $line
    [IO.File]::AppendAllText($runLog, $line + "`n", $utf8)
}
function Script([string]$name) { return Join-Path $sqlDir $name }
function SqlFile([string]$name) { return 'spikes/sql/' + $name }
function Run-Sql([string]$file, [string]$service, [string]$log, [hashtable]$vars = @{}, [string]$database = 'master', [switch]$ContinueOnError, [string]$passwordFromEnv) {
    $a = @{ File = (SqlFile $file); Service = $service; Database = $database; Vars = $vars; Log = $log }
    if ($ContinueOnError) { $a['ContinueOnError'] = $true }
    if ($passwordFromEnv) { $a['PasswordFromEnv'] = $passwordFromEnv }
    & (Script 'sp19_run.ps1') @a | Out-Null
}
function Read-Precheck([string]$service) {
    $log = 'spikes/logs/sp19-' + $Tag + '-01-precheck-' + $(if ($service -eq 'sql') { 'sandbox' } else { 'origin' }) + '.log'
    Run-Sql 'sp19_01_precheck.sql' $service $log
    $r = @{ sp19_db = @() }
    foreach ($l in [IO.File]::ReadAllLines((Join-Path $root $log), $utf8)) {
        if ($l -match '^precheck (\S+) ?(.*)$') {
            if ($Matches[1] -eq 'sp19_db') { $r.sp19_db += $Matches[2].Trim() } else { $r[$Matches[1]] = $Matches[2].Trim() }
        }
    }
    return $r
}
function Median([object[]]$xs) {
    $v = @($xs | Where-Object { "$_" -ne '' } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($v.Count -eq 0) { return '' }
    if ($v.Count % 2 -eq 1) { return $v[[int][math]::Floor($v.Count / 2)] }
    return ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2
}
# sqlcmd -W 결과 표에서 머리글이 $header로 시작하는 표의 행(공백으로 나눈 칸 배열)들
function Read-Table([string]$path, [string]$header) {
    $rows = @()
    if (-not (Test-Path $path)) { return , $rows }
    $lines = [IO.File]::ReadAllLines($path, $utf8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i].StartsWith($header)) {
            for ($j = $i + 2; $j -lt $lines.Length -and $lines[$j] -match '^\d'; $j++) { $rows += , ($lines[$j] -split '\s+') }
            break
        }
    }
    return , $rows   # 행이 하나여도 배열의 배열로 돌려준다(풀리지 않게)
}
function Get-FreeGb([string]$path, [string]$container = '') {
    # Linux: df -Pk(사용 가능 KB, 파일 시스템 이름) / Windows: 드라이브 여유
    if ($isWin) {
        $d = Get-PSDrive -Name ((Resolve-Path $path).Path.Substring(0, 1))
        return @{ Gb = [math]::Round($d.Free / 1GB, 2); Fs = $d.Name }
    }
    $o = if ($container) { @(& docker exec $container df -Pk $path 2>$null) } else { @(& df -Pk $path 2>$null) }
    if ($LASTEXITCODE -ne 0 -or $o.Count -lt 2) { throw "디스크 여유 확인 실패: $container $path" }
    $f = ($o[-1] -split '\s+')
    return @{ Gb = [math]::Round([double]$f[3] / 1MB, 2); Fs = $f[0] }
}

# 크기 표. DockerGb = 컨테이너 볼륨(원본 DB + 샌드박스 DB, 생성 중 로그 512MB 포함), RepoGb = 바인드 폴더(.data)의 .bak(원본 백업 + 가져오기 사본). 2026-11-16 로컬 실측에서 잡은 값 + 여유.
$sizeTable = [ordered]@{
    'cl'  = @{ Origin = 'CascadeLab'; Kind = 'cl'; DockerGb = 0.3; RepoGb = 0.05 }
    '0g3' = @{ Origin = 'SP19_0G3'; Kind = 'syn'; Scale = '0.35'; DataMB = '436'; LogMB = '32'; LedgerRows = 525000; DockerGb = 1.4; RepoGb = 0.2 }
    '1g'  = @{ Origin = 'SP19_1G'; Kind = 'syn'; Scale = '1.6'; DataMB = '0'; LogMB = '128'; LedgerRows = 2400000; DockerGb = 3.2; RepoGb = 0.8 }
    'aw'  = @{ Origin = 'SP19_AW'; Kind = 'aw'; DockerGb = 0.9; RepoGb = 0.2 }
    '2g'  = @{ Origin = 'SP19_2G'; Kind = 'syn'; Scale = '3.0'; DataMB = '2104'; LogMB = '200'; LedgerRows = 4500000; DockerGb = 5.0; RepoGb = 1.5; Large = $true }
    '5g'  = @{ Origin = 'SP19_5G'; Kind = 'syn'; Scale = '7.6'; DataMB = '5232'; LogMB = '500'; LedgerRows = 11400000; DockerGb = 11.5; RepoGb = 3.7; Large = $true }
}
$Sizes = @($Sizes | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
foreach ($s in $Sizes) {
    if (-not $sizeTable.Contains($s)) { throw "알 수 없는 크기 $s (cl, 0g3, 1g, aw, 2g, 5g)" }
    if ($sizeTable[$s].Large -and -not $AllowLarge) { throw "$s 는 선택 단계다. 로컬 RAM·디스크 여유를 확인한 뒤 -AllowLarge로 켠다(클라우드 기본 실행에서는 쓰지 않는다)" }
}
Say ("SP19 시작: 크기 " + ($Sizes -join ',') + ", 회차 $Runs, 태그 $Tag, 로그 $logDir")

# ---- 0. 사전 확인 ----
. (Join-Path (Join-Path $root 'scripts') 'env.ps1')
foreach ($k in 'MSSQL_SA_PASSWORD', 'ORIGIN_SQL_SERVER', 'ORIGIN_SQL_USER', 'ORIGIN_SQL_PASSWORD') {
    if (-not [Environment]::GetEnvironmentVariable($k, 'Process')) { throw ".env에 $k 가 없음(docs/spikes/SP19.md 'Codex 클라우드에서 돌리기' 1단계)" }
}
$saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
foreach ($c in $OriginContainer, $SandboxContainer) {
    $running = (& docker inspect -f '{{.State.Running}}' $c 2>$null)
    if ("$running".Trim() -ne 'true') { $ErrorActionPreference = $saved; throw "컨테이너 $c 가 떠 있지 않음" }
}
$sbMounts = (& docker inspect -f '{{range .Mounts}}{{.Destination}}={{.Source}};{{end}}' $SandboxContainer 2>$null) -join ''
$orMounts = (& docker inspect -f '{{range .Mounts}}{{.Destination}}={{.Source}};{{end}}' $OriginContainer 2>$null) -join ''
$ErrorActionPreference = $saved
if ($sbMounts -notmatch '/var/opt/mssql/import=') { throw "샌드박스 $SandboxContainer 에 가져오기 폴더 바인드(/var/opt/mssql/import ← .data/import)가 없음" }
if ($orMounts -notmatch '/var/opt/mssql/backup=') { throw "원본 $OriginContainer 에 백업 폴더 바인드(/var/opt/mssql/backup ← .data/origin-backup)가 없음" }
if (-not $isWin) {
    $impSrc = [regex]::Match($sbMounts, '/var/opt/mssql/import=([^;]*)').Groups[1].Value
    $bakSrc = [regex]::Match($orMounts, '/var/opt/mssql/backup=([^;]*)').Groups[1].Value
    if ((Resolve-Path $importDir).Path.TrimEnd('/') -ne $impSrc.TrimEnd('/')) { throw "샌드박스 가져오기 바인드 원본($impSrc)이 이 저장소의 .data/import가 아님" }
    if ((Resolve-Path $backupDir).Path.TrimEnd('/') -ne $bakSrc.TrimEnd('/')) { throw "원본 백업 바인드 원본($bakSrc)이 이 저장소의 .data/origin-backup이 아님" }
}
# 바인드 폴더 쓰기·읽기(Linux에서 컨테이너 mssql 사용자(uid 10001)와 이 셸 사용자가 서로 읽고 쓸 수 있어야 한다)
$probe = '.sp19-probe-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
[IO.File]::WriteAllText((Join-Path $importDir $probe), 'x')
$ErrorActionPreference = 'Continue'
& docker exec $SandboxContainer test -r ('/var/opt/mssql/import/' + $probe) 2>$null; $impOk = ($LASTEXITCODE -eq 0)
& docker exec $OriginContainer bash -c ('echo x > /var/opt/mssql/backup/' + $probe) 2>$null; $bakWrite = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $saved
Remove-Item (Join-Path $importDir $probe) -Force -ErrorAction SilentlyContinue
$bakRead = $false
if ($bakWrite) {
    try { $null = [IO.File]::ReadAllText((Join-Path $backupDir $probe)); $bakRead = $true } catch { }
    $ErrorActionPreference = 'Continue'; & docker exec $OriginContainer rm -f ('/var/opt/mssql/backup/' + $probe) 2>$null | Out-Null; $ErrorActionPreference = $saved
}
if (-not $impOk) { throw '샌드박스가 .data/import의 파일을 읽지 못함(폴더 권한: chmod 777 .data/import)' }
if (-not $bakWrite) { throw '원본이 .data/origin-backup에 쓰지 못함(폴더 권한: chmod 777 .data/origin-backup)' }
if (-not $bakRead) { throw '이 셸 사용자가 원본이 쓴 .bak을 읽지 못함(root로 실행하거나 setfacl -d -m o::r .data/origin-backup)' }
foreach ($p in @($Port)) {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $p)
    try { $l.Start() } catch { throw "Host 포트 $p 가 이미 쓰이고 있음" } finally { try { $l.Stop() } catch { } }
}
$pre = @{ origin = (Read-Precheck 'sql-origin'); sandbox = (Read-Precheck 'sql') }
$originMajor = [int]$pre.origin['major']; $sandboxMajor = [int]$pre.sandbox['major']
Say ("원본 주버전 $originMajor ($($pre.origin['version'])), 샌드박스 $sandboxMajor ($($pre.sandbox['version'])), 원본 메모리 목표 $($pre.origin['committed_target_mb'])MB, 샌드박스 $($pre.sandbox['committed_target_mb'])MB")
if ($originMajor -eq $sandboxMajor) { Say '주의: 원본과 샌드박스 주버전이 같다 → 16→17 업그레이드 복원 경로는 이번 측정에 없다(결과에 적는다)' }
if ($pre.sandbox.sp19_db.Count -gt 0) { Say ('주의: 샌드박스에 SP19 DB가 이미 있음(덮어쓴다): ' + ($pre.sandbox.sp19_db -join ',')) }
if ($Sizes -contains 'aw' -and $originMajor -lt 17) {
    Say "AW 건너뜀: 원본 주버전 $originMajor < 17이라 AdventureWorks2025.bak을 복원할 수 없음(Msg 3169, spikes/logs/sp19-05-aw-on-origin.log). 원본을 2025 이미지로 띄우면 된다"
    $Sizes = @($Sizes | Where-Object { $_ -ne 'aw' })
}
if ($Sizes -contains 'aw' -and -not (Test-Path (Join-Path (Join-Path $dataDir 'backup') 'AdventureWorks2025.bak'))) {
    Say 'AW 건너뜀: .data/backup/AdventureWorks2025.bak 없음(scripts/fetch-adventureworks.ps1)'
    $Sizes = @($Sizes | Where-Object { $_ -ne 'aw' })
}
if ($Sizes.Count -eq 0) { throw '잴 크기가 남지 않음' }
# 디스크: 크기마다 DB를 지우고 다음으로 가므로 필요한 양은 가장 큰 크기 하나 + 여유
$needDocker = ($Sizes | ForEach-Object { $sizeTable[$_].DockerGb } | Measure-Object -Maximum).Maximum
$needRepo = ($Sizes | ForEach-Object { $sizeTable[$_].RepoGb } | Measure-Object -Maximum).Maximum
$repoFree = Get-FreeGb $root
$dockerRoot = ''
$ErrorActionPreference = 'Continue'; $dockerRoot = ("" + (& docker info -f '{{.DockerRootDir}}' 2>$null)).Trim(); $ErrorActionPreference = $saved
$dockerFree = $null
if (-not $isWin) {
    if ($dockerRoot -and (Test-Path $dockerRoot -ErrorAction SilentlyContinue)) {
        $dockerFree = Get-FreeGb $dockerRoot
    } else {
        # rootless DockerRootDir는 호스트 사용자에게 접근 불가할 수 있다.
        # 경고만 내고 건너뛰지 않고 실제 SQL 데이터 볼륨에서 같은 기준을 검사한다.
        $dockerFree = Get-FreeGb '/var/opt/mssql' $SandboxContainer
        Say 'Docker 루트 접근 불가: 샌드박스 /var/opt/mssql 볼륨에서 디스크 여유 확인'
    }
}
if ($null -ne $repoFree) {
    if ($null -ne $dockerFree -and $dockerFree.Fs -eq $repoFree.Fs) {
        $need = $needDocker + $needRepo + $DiskMarginGb
        Say ("디스크(같은 파일 시스템 $($repoFree.Fs)): 여유 $($repoFree.Gb)GB, 필요 약 ${need}GB")
        if ($repoFree.Gb -lt $need) { throw "디스크 부족: 여유 $($repoFree.Gb)GB < 필요 ${need}GB(크기를 줄이거나 공간을 비운다)" }
    } else {
        $needR = $needRepo + $DiskMarginGb
        Say ("디스크(저장소): 여유 $($repoFree.Gb)GB, 필요 약 ${needR}GB")
        if ($repoFree.Gb -lt $needR) { throw "저장소 디스크 부족: 여유 $($repoFree.Gb)GB < 필요 ${needR}GB" }
        if ($null -ne $dockerFree) {
            $needD = $needDocker + $DiskMarginGb
            Say ("디스크(Docker 루트 $dockerRoot): 여유 $($dockerFree.Gb)GB, 필요 약 ${needD}GB")
            if ($dockerFree.Gb -lt $needD) { throw "Docker 디스크 부족: 여유 $($dockerFree.Gb)GB < 필요 ${needD}GB" }
        } else { Say "주의: Docker 데이터 루트 여유를 확인하지 못함(Windows Docker Desktop 또는 접근 불가). 컨테이너 볼륨에 약 ${needDocker}GB 필요" }
    }
}
if (-not $isWin -and (Test-Path '/proc/meminfo')) {
    $avail = [double](([regex]::Match((Get-Content '/proc/meminfo' -Raw), 'MemAvailable:\s+(\d+)')).Groups[1].Value) / 1MB
    Say ("메모리: MemAvailable " + [math]::Round($avail, 2) + 'GB(두 SQL 컨테이너는 이미 떠 있음, Host·빌드에 약 1GB 더 씀)')
    if ($avail -lt $MinMemAvailableGb) { throw "메모리 부족: MemAvailable $([math]::Round($avail, 2))GB < $MinMemAvailableGb GB" }
}
if ($PrecheckOnly) { Say '사전 확인만 하고 끝냄(-PrecheckOnly)'; exit 0 }

# ---- 1. 원본 준비: CascadeLab(없으면 SP17 3단계와 같은 파일로 만든다), 제한 로그인(없으면 sp17_10) ----
$createdCl = $false
if (($Sizes -contains 'cl' -or $pre.origin['login_dbr_origin_user'] -ne '1') -and $pre.origin['db_CascadeLab'] -ne '1') {
    Say '원본에 CascadeLab 만들기(cascadelab/sql/00 + cl*.sql)'
    $createdCl = $true
    $clDir = Join-Path (Join-Path $root 'cascadelab') 'sql'
    $files = @('00-create-databases.sql') + @(Get-ChildItem $clDir -Filter 'cl*.sql' | Sort-Object Name | ForEach-Object Name)
    foreach ($f in $files) {
        & (Script 'sp19_run.ps1') -File ('cascadelab/sql/' + $f) -Service 'sql-origin' -Log ($logDir + '/origin-cascadelab-' + [IO.Path]::GetFileNameWithoutExtension($f) + '.log') | Out-Null
    }
}
if ($pre.origin['login_dbr_origin_user'] -ne '1' -or $createdCl) {
    # sp17_10은 멱등이다: 로그인이 있으면 비밀번호만 다시 맞추고, CascadeLab에 사용자·역할(db_backupoperator·datareader·datawriter)을 둔다
    Say '원본에 제한 로그인 dbr_origin_user와 CascadeLab 사용자 만들기(sp17_10, 비밀번호는 .env ORIGIN_SQL_PASSWORD, 출력 안 함)'
    & (Script 'sp19_run.ps1') -File 'spikes/sql/sp17_10_create_limited_login.sql' -Service 'sql-origin' -PasswordFromEnv 'ORIGIN_SQL_PASSWORD' -Log ('spikes/logs/sp19-' + $Tag + '-02-login.log') | Out-Null
}

# ---- 2. 빌드, XE ----
if (-not $SkipBuild) {
    Say 'dotnet build src/DbRipple.Host'
    $ErrorActionPreference = 'Continue'
    $b = @(& dotnet build (Join-Path (Join-Path $root 'src') 'DbRipple.Host') -nologo -v q 2>&1 | ForEach-Object { "$_" })
    $bc = $LASTEXITCODE
    $ErrorActionPreference = $saved
    [IO.File]::WriteAllLines((Join-Path $logAbs 'dotnet-build.log'), [string[]]$b, $utf8)
    if ($bc -ne 0) { throw "dotnet build 실패(exit $bc, $logDir/dotnet-build.log)" }
}
Run-Sql 'sp19_20_xe_start.sql' 'sql-origin' ('spikes/logs/sp19-' + $Tag + '-20-xe-origin.log')
Run-Sql 'sp19_20_xe_start.sql' 'sql' ('spikes/logs/sp19-' + $Tag + '-20-xe-sandbox.log')
$bakAtStart = @(Get-ChildItem $backupDir -File | ForEach-Object Name)
$failures = New-Object System.Collections.Generic.List[string]

# ---- 3. 크기마다 ----
$ids = @(1..$Runs | ForEach-Object { 100 + $_ })
foreach ($size in $Sizes) {
    $cfg = $sizeTable[$size]
    $originDb = $cfg.Origin
    $sandboxDb = 'SP19_' + $size.ToUpperInvariant() + '_Sandbox'
    Say "== $size ($originDb → $sandboxDb) 시작"
    try {
        # 3a. 원본 DB
        $now = Read-Precheck 'sql-origin'
        if ($cfg.Kind -eq 'syn' -and $now.sp19_db -notcontains $originDb) {
            Say "합성 DB $originDb 만들기(S $($cfg.Scale))"
            Run-Sql 'sp19_10_gen_db.sql' 'sql-origin' ('spikes/logs/sp19-' + $Tag + '-10-gen-' + $size + '.log') @{ DbName = $originDb; Scale = $cfg.Scale; LogMB = $cfg.LogMB; DataMB = $cfg.DataMB }
        } elseif ($cfg.Kind -eq 'aw' -and $now.sp19_db -notcontains $originDb) {
            Say 'AW를 원본에 SP19_AW로 복원'
            $awCopy = Join-Path $backupDir 'sp19_aw2025.bak'
            Copy-Item (Join-Path (Join-Path $dataDir 'backup') 'AdventureWorks2025.bak') $awCopy -Force
            try { Run-Sql 'sp19_05_aw_on_origin.sql' 'sql-origin' ('spikes/logs/sp19-' + $Tag + '-05-aw-on-origin.log') }
            finally { Remove-Item $awCopy -Force -ErrorAction SilentlyContinue }
        } elseif ($cfg.Kind -ne 'cl') { Say "$originDb 가 이미 있어 재사용(앞선 [적용]으로 몇 행이 바뀌었을 수 있음)" }
        if ($cfg.Kind -ne 'cl') { Run-Sql 'sp19_15_limited_login.sql' 'sql-origin' ('spikes/logs/sp19-' + $Tag + '-15-login-' + $size + '.log') @{ DbName = $originDb } }

        # 3b. Host → Q1 → Q2·Q3(API)
        & (Script 'sp19_25_host.ps1') -Action Start -Label $size -OriginDb $originDb -LogDir $logDir -Port $Port | Out-Host
        try {
            Say "Q1 기준선 $Runs 회"
            & (Script 'sp19_30_measure.ps1') -Label $size -OriginDb $originDb -Runs $Runs -LogDir $logDir -CsvTag $Tag -BaseUrl ('http://127.0.0.1:' + $Port) | Out-Null
            $shapes = @()
            if ($cfg.Kind -eq 'cl') { $shapes += , @('cl02dept', 'UPDATE cl02.Dept SET name = name WHERE id = 1;', $false) }   # CascadeLab 원본은 바꾸지 않는다([적용] 없음)
            elseif ($cfg.Kind -eq 'syn') {
                $shapes += , @('order1', 'UPDATE dbo.SalesOrder SET Status = (Status + 1) % 5 WHERE OrderId = {id};', $true)
                $shapes += , @('ledger1', 'UPDATE dbo.StockLedger SET Qty = Qty + 1 WHERE LedgerId = {id};', $false)
            } else { $shapes += , @('product1', 'UPDATE Production.Product SET SafetyStockLevel = SafetyStockLevel + 1 WHERE ProductID = {id};', $true) }
            foreach ($sh in $shapes) {
                $shIds = if ($cfg.Kind -eq 'aw') { @(1..$Runs) } else { $ids }
                Say ("Q2" + $(if ($sh[2]) { '·Q3 [적용]' } else { '' }) + " $($sh[0]) $Runs 회")
                try {
                    $a = @{ Label = $size; Shape = $sh[0]; SqlTemplate = $sh[1]; Ids = $shIds; LogDir = $logDir; CsvTag = $Tag; BaseUrl = ('http://127.0.0.1:' + $Port) }
                    if ($sh[2]) { $a['Apply'] = $true }
                    & (Script 'sp19_40_preview_apply.ps1') @a | Out-Null
                } catch { $failures.Add("$size $($sh[0]): $($_.Exception.Message)"); Say "실패(계속): $size $($sh[0]) $($_.Exception.Message)" }
            }
        } finally {
            & (Script 'sp19_25_host.ps1') -Action Stop -Port $Port | Out-Host
        }

        # 3c. Q2 스냅샷 되돌리기(Host를 끈 뒤), Q3 해시(원본, 읽기만)
        $rv = switch ($cfg.Kind) {
            'cl' { @{ Table = 'cl02.Dept'; Key = 'id'; Col = 'name'; Big = 0 } }
            'syn' { @{ Table = 'dbo.StockLedger'; Key = 'LedgerId'; Col = 'RefNo'; Big = [int]($cfg.LedgerRows / 10) } }
            'aw' { @{ Table = 'Person.Address'; Key = 'AddressID'; Col = 'AddressLine1'; Big = 1961 } }
        }
        Say ("Q2 스냅샷 되돌리기(1,000행 $Runs 회" + $(if ($rv.Big -gt 0) { ", 큰 테이블 10% $($rv.Big)행 1회)" } else { ')' }))
        $rvVars = @{ SandboxDb = $sandboxDb; SnapshotDb = ($sandboxDb + '_base'); Table = $rv.Table; Key = $rv.Key; Col = $rv.Col; Rows = '1000'; Rounds = [string]$Runs }
        Run-Sql 'sp19_45_revert.sql' 'sql' ('spikes/logs/sp19-' + $Tag + '-45-revert-' + $size + '-1k.log') $rvVars
        if ($rv.Big -gt 0) {
            $rvVars['Rows'] = [string]$rv.Big; $rvVars['Rounds'] = '1'
            Run-Sql 'sp19_45_revert.sql' 'sql' ('spikes/logs/sp19-' + $Tag + '-45-revert-' + $size + '-10pct.log') $rvVars
        }
        Say 'Q3 해시(제품 모양, 제한 로그인, LOCK_TIMEOUT 10000, 3회 + 테이블별)'
        Run-Sql 'sp19_50_hash_time.sql' 'sql-origin' ('spikes/logs/sp19-' + $Tag + '-50-hash-' + $size + '.log') @{} $originDb
    } catch {
        $failures.Add("$size : $($_.Exception.Message)")
        Say "실패: $size $($_.Exception.Message)"
    } finally {
        if (-not $KeepDbs) {
            Say ("$size 정리: 샌드박스 $sandboxDb(+스냅샷)" + $(if ($cfg.Kind -ne 'cl') { ", 원본 $originDb" } else { '' }))
            try {
                Run-Sql 'sp19_99_drop.sql' 'sql' ('spikes/logs/sp19-' + $Tag + '-99-drop-' + $size + '-sandbox.log') @{ DbName = $sandboxDb }
                if ($cfg.Kind -ne 'cl') { Run-Sql 'sp19_99_drop.sql' 'sql-origin' ('spikes/logs/sp19-' + $Tag + '-99-drop-' + $size + '-origin.log') @{ DbName = $originDb } }
            } catch { $failures.Add("$size 정리: $($_.Exception.Message)"); Say "정리 실패: $($_.Exception.Message)" }
            Remove-Item (Join-Path (Join-Path $dataDir 'sp19-host') $size) -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---- 4. XE·새 .bak 정리 ----
Say 'XE 세션·이벤트 파일, 이번 실행이 만든 .bak 정리'
foreach ($svc in 'sql-origin', 'sql') {
    try { Run-Sql 'sp19_29_xe_drop.sql' $svc ('spikes/logs/sp19-' + $Tag + '-29-xe-drop-' + $(if ($svc -eq 'sql') { 'sandbox' } else { 'origin' }) + '.log') } catch { $failures.Add("XE 정리 $svc") }
}
$ErrorActionPreference = 'Continue'
foreach ($c in $OriginContainer, $SandboxContainer) { & docker exec $c bash -c 'rm -f /var/opt/mssql/log/sp19_trace*.xel' 2>$null | Out-Null }
$ErrorActionPreference = $saved
foreach ($f in @(Get-ChildItem $backupDir -File | Where-Object { $bakAtStart -notcontains $_.Name })) {
    try { Remove-Item $f.FullName -Force } catch { $failures.Add('.bak 삭제 실패(권한)') }
}
$importLeft = @(Get-ChildItem $importDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'dbr_*' }).Count

# ---- 5. 요약 ----
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# SP19 요약 ($Tag, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))")
[void]$md.AppendLine('')
[void]$md.AppendLine("환경: 원본 주버전 $originMajor($($pre.origin['version'])), 샌드박스 $sandboxMajor($($pre.sandbox['version'])), 회차 $Runs, OS " + $(if ($isWin) { 'Windows' } else { 'Linux' }) + ", PowerShell $($PSVersionTable.PSVersion)")
[void]$md.AppendLine('')
[void]$md.AppendLine('Q1 기준선 중앙값(ms, 전체 회차 / 2회차 이후):')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 크기 | 백업 원본→압축 MB | total | pre | backup | copy | restore(cmd) | owner | install(배치) | snapshot | context |')
[void]$md.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|')
$logsDir = Join-Path (Join-Path $root 'spikes') 'logs'
foreach ($size in $Sizes) {
    $p = Join-Path $logsDir ('sp19-30-q1-' + $size + '-' + $Tag + '.csv')
    if (-not (Test-Path $p)) { [void]$md.AppendLine("| $size | (없음) |||||||||"); continue }
    $rows = @(Import-Csv $p)
    $warm = @($rows | Where-Object { $_.first_run -eq '0' })
    function Cell([string]$col) { '{0} / {1}' -f (Median @($rows | ForEach-Object { $_.$col })), (Median @($warm | ForEach-Object { $_.$col })) }
    [void]$md.AppendLine(('| {0} | {1}→{2} | {3} | {4} | {5} | {6} | {7} ({8}) | {9} | {10} ({11}) | {12} | {13} |' -f $size, $rows[0].backup_mb, $rows[0].compressed_mb,
        (Cell 'total_client_ms'), (Cell 'pre_ms'), (Cell 'backup_ms'), (Cell 'copy_ms'), (Cell 'restore_ms'), (Cell 'restore_cmd_ms'), (Cell 'owner_ms'), (Cell 'install_ms'), $rows[0].install_batches, (Cell 'snapshot_ms'), (Cell 'context_ms')))
}
[void]$md.AppendLine('')
[void]$md.AppendLine('Q2 미리보기·Q3 [적용] 중앙값(ms):')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 크기 | 모양 | 미리보기 API | netDiff | 미리보기 뒤 상태 | [적용] 상태 | 적용 API | drift(전 해시 포함) | netDiff(후 해시) | 잠금 보유(drift+execute+netDiff+commit) |')
[void]$md.AppendLine('|---|---|---|---|---|---|---|---|---|---|')
foreach ($size in $Sizes) {
    $p = Join-Path $logsDir ('sp19-40-' + $size + '-' + $Tag + '.csv')
    if (-not (Test-Path $p)) { continue }
    $rows = @(Import-Csv $p)
    foreach ($g in ($rows | Group-Object shape)) {
        $r = @($g.Group)
        $lock = @($r | Where-Object { $_.a_drift -ne '' } | ForEach-Object { [double]$_.a_drift + [double]$_.a_execute + [double]$_.a_netDiff + [double]$_.a_commit })
        [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |' -f $size, $g.Name, (Median @($r | ForEach-Object preview_ms)), (Median @($r | ForEach-Object netDiff)),
            (($r | ForEach-Object state_after | Sort-Object -Unique) -join '/'), (($r | ForEach-Object apply_status | Where-Object { $_ } | Sort-Object -Unique) -join '/'),
            (Median @($r | ForEach-Object apply_ms)), (Median @($r | ForEach-Object a_drift)), (Median @($r | ForEach-Object a_netDiff)), (Median $lock)))
    }
}
[void]$md.AppendLine('')
[void]$md.AppendLine('Q2 스냅샷 되돌리기, Q3 해시(ms):')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 크기 | 되돌리기 1,000행(회차별) | 되돌리기 10% | 되돌린 뒤 차이 0 | 해시 한 번(1~3회, 1회=차가운 캐시) | 해시 오류 |')
[void]$md.AppendLine('|---|---|---|---|---|---|')
foreach ($size in $Sizes) {
    $r1 = Read-Table (Join-Path $logsDir ('sp19-' + $Tag + '-45-revert-' + $size + '-1k.log')) 'round rows_ change_ms'
    $r2 = Read-Table (Join-Path $logsDir ('sp19-' + $Tag + '-45-revert-' + $size + '-10pct.log')) 'round rows_ change_ms'
    $hs = Read-Table (Join-Path $logsDir ('sp19-' + $Tag + '-50-hash-' + $size + '.log')) 'round round_ms'
    $hc = Read-Table (Join-Path $logsDir ('sp19-' + $Tag + '-50-hash-' + $size + '.log')) 'round chunk tables_'
    $diff0 = (@($r1 + $r2 | Where-Object { $_[9] -ne '0' }).Count -eq 0)
    $herr = @($hc | Where-Object { $_[5] -ne 'NULL' } | ForEach-Object { $_[5] }) -join ','
    [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $size, (($r1 | ForEach-Object { $_[7] }) -join ' / '), (($r2 | ForEach-Object { $_[7] }) -join ' / '),
        $(if ($r1.Count) { $diff0 } else { '' }), (($hs | ForEach-Object { $_[1] }) -join ' / '), $(if ($herr) { $herr } else { '없음' })))
}
[void]$md.AppendLine('')
[void]$md.AppendLine('실패·건너뜀: ' + $(if ($failures.Count) { $failures -join ' | ' } else { '없음' }) + ". 가져오기 폴더에 남은 dbr_*.bak: $importLeft")
[IO.File]::WriteAllText((Join-Path $logAbs 'summary.md'), $md.ToString(), $utf8)
Write-Host $md.ToString()
Say ("끝: 요약 $logDir/summary.md, 실패 $($failures.Count)건")
if ($failures.Count -gt 0) { exit 1 }
exit 0
