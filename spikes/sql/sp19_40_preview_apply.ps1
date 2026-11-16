# SP19 Q2·Q3 — 제품 경로로 미리보기(+ 선택: [적용] 약한 검증 동의)를 회차마다 재고, 미리보기 직후 샌드박스 상태(ready/dirty)를 본다.
# Windows PowerShell 5.1과 Linux PowerShell 7 둘 다(경로는 Join-Path·/, 호스트 부하는 Windows CIM 또는 Linux /proc/loadavg).
# 사전: sp19_25_host.ps1 -Action Start로 같은 Label의 Host가 떠 있고 기준선이 ready.
# 사용(저장소 루트):
#   ./spikes/sql/sp19_40_preview_apply.ps1 -Label 0g3 -Shape order1 -SqlTemplate 'UPDATE dbo.SalesOrder SET Status = (Status + 1) % 5 WHERE OrderId = {id};' -Ids 101,102,103 [-CsvTag cloud]
#   [적용]까지: -Apply (각 id마다 새 미리보기 → apply(allowKeysetVerification = true)). [적용]은 원본을 바꾼다(SP19 전용 DB에만 쓴다). id마다 다른 행을 써야 드리프트가 안 난다.
# 결과: spikes/logs/sp19-40-<Label>[-<CsvTag>].csv(회차별 요약), <LogDir>/<Label>/q2-<Shape>-<id>-preview.json·q3-<Shape>-<id>-apply.json(응답 원본, 행 이미지가 있어 커밋하지 않는 폴더).
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]+$')][string]$Label,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]+$')][string]$Shape,
    [Parameter(Mandatory)][string]$SqlTemplate,
    [Parameter(Mandatory)][int[]]$Ids,
    [switch]$Apply,
    [string]$BaseUrl = 'http://127.0.0.1:5080',
    [string]$LogDir = ('logs/home-test/' + (Get-Date -Format 'yyyy-MM-dd') + '/sp19'),
    [ValidatePattern('^[a-z0-9]*$')][string]$CsvTag = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$utf8 = New-Object System.Text.UTF8Encoding($false)
$isWin = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$outDir = Join-Path (Join-Path $root $LogDir.Replace('\', '/')) $Label
New-Item -ItemType Directory -Force $outDir | Out-Null
$csv = Join-Path (Join-Path (Join-Path $root 'spikes') 'logs') ('sp19-40-' + $Label + $(if ($CsvTag) { '-' + $CsvTag } else { '' }) + '.csv')
# 호스트 부하(다른 작업과 겹쳤는지 표시용): Windows는 CPU %, Linux는 1분 load average/CPU 수. 열 이름 cpu_pct는 2026-11-16 로컬 CSV와 맞추려고 그대로 둔다
function Get-HostLoad {
    try {
        if ($isWin) { return [string](Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average }
        return ('load' + (Get-Content '/proc/loadavg' -Raw).Split(' ')[0] + '/' + [Environment]::ProcessorCount)
    } catch { return 'unknown' }
}
$h = @{ 'X-DbRipple-Request' = '1' }
function Post-Json([string]$path, $body) {
    $json = $body | ConvertTo-Json -Depth 5
    $bytes = $utf8.GetBytes($json)
    return Invoke-RestMethod -Method Post -Uri ($BaseUrl + $path) -Headers $h -ContentType 'application/json; charset=utf-8' -Body $bytes
}
foreach ($id in $Ids) {
    $sql = $SqlTemplate.Replace('{id}', [string]$id)
    $before = (Invoke-RestMethod -Uri ($BaseUrl + '/api/sandbox') -Headers $h).state
    $cpu = Get-HostLoad
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Post-Json '/api/previews' @{ sql = $sql }
    $sw.Stop()
    $previewMs = [math]::Round($sw.Elapsed.TotalMilliseconds)
    $after = (Invoke-RestMethod -Uri ($BaseUrl + '/api/sandbox') -Headers $h).state
    [IO.File]::WriteAllText((Join-Path $outDir ("q2-$Shape-$id-preview.json")), ($p | ConvertTo-Json -Depth 8), $utf8)
    $t = $p.timingsMs
    $net = (@($p.netSummary) | ForEach-Object { '{0}:i{1}u{2}d{3}' -f $_.table, $_.inserted, $_.updated, $_.deleted }) -join ' '
    $row = [ordered]@{
        label = $Label; shape = $Shape; id = $id; cpu_pct = $cpu; state_before = $before; preview_ms = $previewMs; preview_status = $p.status
        analyze = $t.analyze; execute = $t.execute; readLog = $t.readLog; reread = $t.reread; netDiff = $t.netDiff; resolve = $t.resolve; serialize = $t.serialize
        state_after = $after; net = $net
        apply_ms = ''; apply_status = ''; mode = ''; a_snapshot = ''; a_drift = ''; a_execute = ''; a_netDiff = ''; a_commit = ''; mismatches = ''; message = ''
    }
    if ($Apply -and $p.status -eq 'ok') {
        $sw2 = [Diagnostics.Stopwatch]::StartNew()
        try {
            $a = Post-Json ('/api/previews/' + $p.previewId + '/apply') @{ sqlHash = $p.sqlHash; allowKeysetVerification = $true }
            $sw2.Stop()
            [IO.File]::WriteAllText((Join-Path $outDir ("q3-$Shape-$id-apply.json")), ($a | ConvertTo-Json -Depth 8), $utf8)
            $row.apply_ms = [math]::Round($sw2.Elapsed.TotalMilliseconds); $row.apply_status = $a.status; $row.mode = $a.verification.mode
            $row.a_snapshot = $a.timingsMs.snapshot; $row.a_drift = $a.timingsMs.drift; $row.a_execute = $a.timingsMs.execute; $row.a_netDiff = $a.timingsMs.netDiff; $row.a_commit = $a.timingsMs.commit
            $row.mismatches = @($a.verification.mismatches).Count
            $row.message = ([string]$a.message).Replace(',', ';').Replace("`n", ' ')
        } catch {
            $sw2.Stop()
            $row.apply_ms = [math]::Round($sw2.Elapsed.TotalMilliseconds); $row.apply_status = 'http_error'
            $row.message = ($_.Exception.Message + ' ' + $_.ErrorDetails.Message).Replace(',', ';').Replace("`n", ' ')
        }
    }
    if (-not (Test-Path $csv)) { [IO.File]::WriteAllText($csv, (($row.Keys -join ',') + "`n"), $utf8) }
    [IO.File]::AppendAllText($csv, ((@($row.Values) | ForEach-Object { "$_" }) -join ',') + "`n", $utf8)
    ($row.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ' | Out-Host
    Start-Sleep -Seconds 2
}
