# SP19 — Split 모드 Host를 개발 모드(--dev, 브라우저를 열지 않음)로 띄우거나(-Action Start) 끈다(-Action Stop).
# Windows PowerShell 5.1과 Linux PowerShell 7 둘 다: 창 숨김(-WindowStyle)은 Windows에서만 넘기고, 끄기는 Windows taskkill /T, Linux Process.Kill($true)(scripts/home-test.ps1과 같은 방식).
# 사용(저장소 루트, 먼저 dotnet build src/DbRipple.Host):
#   ./spikes/sql/sp19_25_host.ps1 -Action Start -Label 0g3 -OriginDb SP19_0G3     # 뜰 때까지(GET /api/sandbox 응답) 기다린 뒤 돌아온다
#   ./spikes/sql/sp19_25_host.ps1 -Action Stop                                       # PID 파일의 Host를 끄고 포트가 비었는지 확인
#   샌드박스 DB는 SP19_<Label>_Sandbox, 스냅샷은 SP19_<Label>_Sandbox_base(기존 CascadeLab_Sandbox·AW_Sandbox를 덮지 않게 SP19 전용 이름).
#   매니페스트·미리보기 저장소는 .data/sp19-host/<Label>/(기본값이면 AW 매니페스트를 덮는다, SP17과 같은 이유). PID는 .data/sp19-host/host.pid.
#   콘솔 출력은 <LogDir>/host-<Label>.out.log·.err.log(연결 문자열은 Host가 출력하지 않는다).
param(
    [ValidateSet('Start', 'Stop')][string]$Action = 'Start',
    [ValidatePattern('^[a-z0-9]+$')][string]$Label,
    [ValidatePattern('^[A-Za-z0-9_]+$')][string]$OriginDb,
    [string]$LogDir = ('logs/home-test/' + (Get-Date -Format 'yyyy-MM-dd') + '/sp19'),
    [int]$Port = 5080,
    [int]$StartTimeoutSec = 120
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$isWin = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$pidFile = Join-Path (Join-Path (Join-Path $root '.data') 'sp19-host') 'host.pid'

function Test-PortFree([int]$p) {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $p)
    try { $l.Start(); return $true } catch { return $false } finally { try { $l.Stop() } catch { } }
}
function Stop-Tree([System.Diagnostics.Process]$proc) {
    if ($null -eq $proc -or $proc.HasExited) { return }
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        if ($isWin) { & taskkill /PID $proc.Id /T /F 2>$null | Out-Null }
        else { $proc.Kill($true); [void]$proc.WaitForExit(10000) }
    } finally { $ErrorActionPreference = $saved }
}

if ($Action -eq 'Stop') {
    if (Test-Path $pidFile) {
        $id = [int](Get-Content $pidFile -Raw)
        $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
        Stop-Tree $proc
        Remove-Item $pidFile -Force
    }
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-PortFree $Port) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    $free = Test-PortFree $Port
    Write-Host ("Host 종료, 포트 {0} 비었음: {1}" -f $Port, $free)
    if (-not $free) { throw "포트 $Port 가 아직 쓰이고 있음(다른 프로세스일 수 있음, 직접 확인)" }
    exit 0
}

if (-not $Label -or -not $OriginDb) { throw '-Action Start에는 -Label과 -OriginDb가 필요함' }
if (Test-Path $pidFile) { throw "이미 띄운 Host가 있음(PID 파일 $pidFile). 먼저 -Action Stop" }
if (-not (Test-PortFree $Port)) { throw "포트 $Port 가 이미 쓰이고 있음" }
. (Join-Path (Join-Path $root 'scripts') 'env.ps1') -Split
$tag = $Label.ToUpperInvariant()
$hostDir = Join-Path (Join-Path (Join-Path $root '.data') 'sp19-host') $Label
$logPath = Join-Path $root $LogDir.Replace('\', '/')
New-Item -ItemType Directory -Force $hostDir, $logPath | Out-Null
$backupDir = (Resolve-Path (Join-Path (Join-Path $root '.data') 'origin-backup')).Path
$overrides = [ordered]@{
    'DbRipple__Urls'                = ('http://127.0.0.1:' + $Port)
    'DbRipple__Mode'                = 'Split'
    'DbRipple__OriginDb'            = $OriginDb
    'DbRipple__SandboxDb'           = ('SP19_' + $tag + '_Sandbox')
    'DbRipple__SnapshotDb'          = ('SP19_' + $tag + '_Sandbox_base')
    'DbRipple__Backup__ServerPath'  = '/var/opt/mssql/backup'
    'DbRipple__Backup__SharePath'   = $backupDir
    'DbRipple__ManifestPath'        = (Join-Path $hostDir 'manifest.json')
    'DbRipple__PreviewStoreDir'     = (Join-Path $hostDir 'previews')
}
$saved = @{}
foreach ($k in $overrides.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process'); [Environment]::SetEnvironmentVariable($k, $overrides[$k], 'Process') }
try {
    $out = Join-Path $logPath ('host-' + $Label + '.out.log')
    $windowOptions = @{}
    if ($isWin) { $windowOptions['WindowStyle'] = 'Hidden' }
    $project = Join-Path (Join-Path $root 'src') 'DbRipple.Host'
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', ('"' + $project + '"'), '--no-build', '--no-launch-profile', '--', '--dev') `
        -PassThru @windowOptions -RedirectStandardOutput $out -RedirectStandardError ($out + '.err')
    [IO.File]::WriteAllText($pidFile, [string]$proc.Id)
} finally {
    foreach ($k in $overrides.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
}
$deadline = (Get-Date).AddSeconds($StartTimeoutSec)
$up = $false
while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
    try { $null = Invoke-RestMethod -Uri ('http://127.0.0.1:' + $Port + '/api/sandbox') -Headers @{ 'X-DbRipple-Request' = '1' } -TimeoutSec 30; $up = $true; break }
    catch { Start-Sleep -Seconds 2 }
}
if (-not $up) {
    Stop-Tree $proc
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    throw "Host가 뜨지 않음(로그 $out)"
}
Write-Host ("Host 시작: label={0} origin={1} sandbox=SP19_{2}_Sandbox pid={3} 로그 {4}" -f $Label, $OriginDb, $tag, $proc.Id, $out)
exit 0
