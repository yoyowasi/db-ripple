# SP19 Q1 — Split 기준선을 제품 경로(POST /api/sandbox/refresh → GET /api/sandbox가 ready)로 회차마다 재고, 단계별 시간은 두 서버의 확장 이벤트(sp19_trace)에서 읽는다.
# Windows PowerShell 5.1과 Linux PowerShell 7 둘 다(경로는 Join-Path·/, 호스트 부하는 Windows CIM 또는 Linux /proc/loadavg).
# 사전: 두 컨테이너에 sp19_20_xe_start.sql, Host는 sp19_25_host.ps1 -Action Start(같은 Label·OriginDb)로 떠 있어야 한다.
# 사용(저장소 루트): ./spikes/sql/sp19_30_measure.ps1 -Label 0g3 -OriginDb SP19_0G3 -Runs 3 [-CsvTag cloud]
# 단계 정의(서버 시간. 두 컨테이너는 같은 Docker 호스트 커널 시계):
#   pre      = 첫 Host 명령 시작 → 원본 BACKUP 시작(인스턴스 확인·표식·원본 사전 검사·지문·샌드박스 사전 검사)
#   backup   = 원본 BACKUP DATABASE … COPY_ONLY, COMPRESSION 명령 시간
#   copy     = 원본 BACKUP 끝 → 샌드박스 다음 명령 시작(Host의 FileBackupImporter 복사: 원본 백업 폴더 → 가져오기 폴더)
#   restore  = 그 샌드박스 명령(기준 스냅샷 삭제) 시작 → RESTORE DATABASE 끝(HEADERONLY·FILELISTONLY·SINGLE_USER 포함), restore_cmd = RESTORE 명령만
#   owner    = RESTORE 끝 → AfterRestoreLocal(ALTER AUTHORIZATION·서버 옵션) 끝(TRUSTWORTHY OFF 포함)
#   install  = owner 끝 → 스냅샷 명령 시작(카탈로그·행 수·실행 사용자·감사 트리거·샌드박스 지문)
#   snapshot = CREATE DATABASE … AS SNAPSHOT OF 명령 시간
#   context  = 스냅샷 끝 → 마지막 Host 명령 끝(매니페스트 쓰기 뒤 ReadContextAsync: 카탈로그·지문·원본 확인)
#   total    = 클라이언트 POST → 폴링에서 ready를 본 시각(폴링 간격 -PollMs 포함)
# 새로 생긴 원본 백업 파일(.data/origin-backup)은 크기를 적고 지운다(-KeepBackups면 남김). 기존 파일은 건드리지 않는다.
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]+$')][string]$Label,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_]+$')][string]$OriginDb,
    [int]$Runs = 3,
    [int]$FirstRun = 1,
    [string]$BaseUrl = 'http://127.0.0.1:5080',
    [string]$LogDir = ('logs/home-test/' + (Get-Date -Format 'yyyy-MM-dd') + '/sp19'),
    [ValidatePattern('^[a-z0-9]*$')][string]$CsvTag = '',
    [int]$PollMs = 250,
    [int]$MaxWaitSec = 3600,
    [switch]$KeepBackups
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
. (Join-Path (Join-Path $root 'scripts') 'env.ps1')
$utf8 = New-Object System.Text.UTF8Encoding($false)
$isWin = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$tag = $Label.ToUpperInvariant()
$sandboxDb = 'SP19_' + $tag + '_Sandbox'
$outDir = Join-Path (Join-Path $root $LogDir.Replace('\', '/')) $Label
New-Item -ItemType Directory -Force $outDir | Out-Null
$csvName = 'sp19-30-q1-' + $Label + $(if ($CsvTag) { '-' + $CsvTag } else { '' }) + '.csv'
$csv = Join-Path (Join-Path (Join-Path $root 'spikes') 'logs') $csvName
$backupDir = Join-Path (Join-Path $root '.data') 'origin-backup'
$importDir = Join-Path (Join-Path $root '.data') 'import'
# 제품 RunNames.MachineTag와 같은 규칙(백업 파일 이름 dbr_<태그>_…을 로그에서 dbr_<pc>_로 가린다)
$machineTag = ([regex]::Replace([Environment]::MachineName, '[^A-Za-z0-9]', '_')).Trim('_').ToLowerInvariant()
if ($machineTag.Length -gt 32) { $machineTag = $machineTag.Substring(0, 32) }
if ($machineTag.Length -eq 0) { $machineTag = 'pc' }

function Open-Sql([string]$port) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = '127.0.0.1,' + $port
    $b['User ID'] = 'sa'
    $b['Password'] = $env:MSSQL_SA_PASSWORD
    $b['Encrypt'] = $true
    $b['TrustServerCertificate'] = $true
    $b['Application Name'] = 'sp19-measure'   # XE 필터(db-ripple)에서 빠진다
    $b['Pooling'] = $false
    $b['Initial Catalog'] = 'master'
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $c.Open()
    return $c
}
function Invoke-Query($conn, [string]$sql, [object]$since = $null, [string]$db = $null) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 600
    if ($null -ne $since) { $p = $cmd.Parameters.Add('@since', [System.Data.SqlDbType]::DateTime2); $p.Value = $since }
    if ($db) { $p2 = $cmd.Parameters.Add('@db', [System.Data.SqlDbType]::NVarChar, 128); $p2.Value = $db }
    $da = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $t = New-Object System.Data.DataTable
    [void]$da.Fill($t)
    return , $t
}
function Ms($a, $b) { if ($null -eq $a -or $null -eq $b) { return '' } return [math]::Round(($b - $a).TotalMilliseconds) }
function Redact([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return $s }
    return $s.Replace('dbr_' + $machineTag + '_', 'dbr_<pc>_')
}
# 호스트 부하(다른 무거운 작업과 겹쳤는지 표시용, runs.log에만): Windows는 CPU %, Linux는 1분 load average/CPU 수
function Get-HostLoad {
    try {
        if ($isWin) { return ('cpu_pct=' + (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average) }
        $la = (Get-Content '/proc/loadavg' -Raw).Split(' ')[0]
        return ('loadavg1=' + $la + '/' + [Environment]::ProcessorCount)
    } catch { return 'load=unknown' }
}

$xeSql = @'
SELECT f.timestamp_utc AS ts,
       x.ed.value('(/event/@name)[1]', 'nvarchar(60)') AS name,
       x.ed.value('(/event/data[@name="duration"]/value)[1]', 'bigint') AS dur_us,
       x.ed.value('(/event/data[@name="logical_reads"]/value)[1]', 'bigint') AS reads,
       x.ed.value('(/event/data[@name="writes"]/value)[1]', 'bigint') AS writes,
       x.ed.value('(/event/action[@name="database_name"]/value)[1]', 'nvarchar(128)') AS db,
       x.ed.value('(/event/action[@name="session_id"]/value)[1]', 'int') AS sid,
       LEFT(COALESCE(x.ed.value('(/event/data[@name="batch_text"]/value)[1]', 'nvarchar(max)'),
                     x.ed.value('(/event/data[@name="statement"]/value)[1]', 'nvarchar(max)')), 400) AS txt
FROM sys.fn_xe_file_target_read_file(N'/var/opt/mssql/log/sp19_trace*.xel', NULL, NULL, NULL) AS f
CROSS APPLY (SELECT CAST(f.event_data AS xml) AS ed) AS x
WHERE DATEADD(millisecond, 0, f.timestamp_utc) >= @since   -- 2022 CU28: timestamp_utc 직접 비교는 0행(2025는 정상). DATEADD로 감싸면 된다(2026-11-16 실측)
ORDER BY f.timestamp_utc;
'@
$sandboxPort = if ($env:MSSQL_HOST_PORT) { $env:MSSQL_HOST_PORT } else { '14333' }
$originPort = if ($env:ORIGIN_HOST_PORT) { $env:ORIGIN_HOST_PORT } else { '14334' }
$h = @{ 'X-DbRipple-Request' = '1' }
$null = Invoke-RestMethod -Uri ($BaseUrl + '/api/sandbox') -Headers $h   # 첫 읽기(기존 기준선 로드)는 측정 밖에서

for ($run = $FirstRun; $run -lt $FirstRun + $Runs; $run++) {
    $co = Open-Sql $originPort
    $cs = Open-Sql $sandboxPort
    $bakBefore = @(Get-ChildItem $backupDir -File | ForEach-Object Name)
    $sinceO = [datetime](Invoke-Query $co 'SELECT DATEADD(millisecond, -200, SYSUTCDATETIME()) AS t;').Rows[0].t
    $sinceS = [datetime](Invoke-Query $cs 'SELECT DATEADD(millisecond, -200, SYSUTCDATETIME()) AS t;').Rows[0].t
    $before = Invoke-RestMethod -Uri ($BaseUrl + '/api/sandbox') -Headers $h
    $load = Get-HostLoad
    $startedLocal = Get-Date
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-RestMethod -Method Post -Uri ($BaseUrl + '/api/sandbox/refresh') -Headers $h
    $states = New-Object System.Collections.Generic.List[string]
    $sawBuilding = $false
    do {
        Start-Sleep -Milliseconds $PollMs
        $s = Invoke-RestMethod -Uri ($BaseUrl + '/api/sandbox') -Headers $h
        if ($states.Count -eq 0 -or $states[$states.Count - 1] -ne $s.state) { $states.Add($s.state) }
        if ($s.state -eq 'building') { $sawBuilding = $true }
        # 새로 고침은 비동기(202): 옛 기준선의 ready를 새것으로 착각하지 않게 building을 봤거나 baselineAt이 바뀐 뒤에만 끝으로 본다(home-test.ps1과 같은 규칙)
        $changed = ([string]$s.baselineAt -ne [string]$before.baselineAt)
        $done = ($s.state -in 'ready', 'error', 'dirty') -and ($sawBuilding -or $changed -or $sw.Elapsed.TotalSeconds -gt 30)
    } until ($done -or $sw.Elapsed.TotalSeconds -gt $MaxWaitSec)
    $sw.Stop()
    $totalMs = [math]::Round($sw.Elapsed.TotalMilliseconds)
    [IO.File]::WriteAllText((Join-Path $outDir ("run$run-status.json")), ($s | ConvertTo-Json -Depth 6), $utf8)
    Start-Sleep -Seconds 2   # XE 전달 지연(MAX_DISPATCH_LATENCY 1초)

    $ev = New-Object System.Collections.Generic.List[object]
    foreach ($srv in @(@{ n = 'O'; c = $co; since = $sinceO }, @{ n = 'S'; c = $cs; since = $sinceS })) {
        $t = Invoke-Query $srv.c $xeSql $srv.since
        foreach ($r in $t.Rows) {
            $end = [datetime]$r.ts
            $dur = [double]$r.dur_us / 1000.0
            $txt = ([string]$r.txt -replace '\s+', ' ').Trim()
            $ev.Add([pscustomobject]@{ Srv = $srv.n; Start = $end.AddMilliseconds(-$dur); End = $end; DurMs = [math]::Round($dur, 1); Db = [string]$r.db; Sid = $r.sid; Reads = $r.reads; Writes = $r.writes; Text = $txt })
        }
    }
    $ev = @($ev | Sort-Object Start)
    $lines = @('srv,start_utc,end_utc,dur_ms,db,sid,reads,writes,text')
    foreach ($e in $ev) {
        $t120 = Redact $(if ($e.Text.Length -gt 120) { $e.Text.Substring(0, 120) } else { $e.Text })
        $lines += ('{0},{1:HH:mm:ss.fff},{2:HH:mm:ss.fff},{3},{4},{5},{6},{7},"{8}"' -f $e.Srv, $e.Start, $e.End, $e.DurMs, $e.Db, $e.Sid, $e.Reads, $e.Writes, $t120.Replace('"', "'"))
    }
    [IO.File]::WriteAllLines((Join-Path $outDir ("run$run-events.csv")), [string[]]$lines, $utf8)

    $bk = $ev | Where-Object { $_.Srv -eq 'O' -and $_.Text -like 'BACKUP DATABASE*' } | Select-Object -Last 1
    $rs = $ev | Where-Object { $_.Srv -eq 'S' -and $_.Text -like 'RESTORE DATABASE*FROM DISK*' } | Select-Object -Last 1
    $afterBk = if ($bk) { $ev | Where-Object { $_.Srv -eq 'S' -and $_.Start -ge $bk.End } | Select-Object -First 1 } else { $null }
    $own = $ev | Where-Object { $_.Srv -eq 'S' -and $_.Text -like 'DECLARE @sa sysname*' } | Select-Object -Last 1
    $snap = $ev | Where-Object { $_.Srv -eq 'S' -and $_.Text -like 'CREATE DATABASE*SNAPSHOT OF*' } | Select-Object -Last 1
    $first = if ($ev.Count -gt 0) { $ev[0] } else { $null }
    $last = if ($ev.Count -gt 0) { $ev | Sort-Object End | Select-Object -Last 1 } else { $null }
    $installBatches = if ($own -and $snap) { @($ev | Where-Object { $_.Srv -eq 'S' -and $_.Start -ge $own.End -and $_.End -le $snap.Start }).Count } else { '' }

    $bs = (Invoke-Query $co 'SELECT TOP (1) CAST(backup_size / 1048576.0 AS decimal(12, 1)) AS mb, CAST(compressed_backup_size / 1048576.0 AS decimal(12, 1)) AS cmb FROM msdb.dbo.backupset WHERE database_name = @db ORDER BY backup_set_id DESC;' $null $OriginDb).Rows
    $files = Invoke-Query $cs 'SELECT d.name, mf.type_desc, mf.size / 128 AS size_mb FROM sys.master_files AS mf JOIN sys.databases AS d ON d.database_id = mf.database_id WHERE d.name LIKE @db ORDER BY d.name, mf.file_id;' $null ($sandboxDb + '%')
    $fileLines = @($files.Rows | ForEach-Object { '{0} {1} size {2}MB' -f $_.name, $_.type_desc, $_.size_mb })

    $newBaks = @(Get-ChildItem $backupDir -File | Where-Object { $bakBefore -notcontains $_.Name })
    $bakInfo = @($newBaks | ForEach-Object { '{0} {1:N0}B' -f (Redact $_.Name), $_.Length })
    $importLeft = @(Get-ChildItem $importDir -File -ErrorAction SilentlyContinue).Count
    if (-not $KeepBackups) {
        foreach ($f in $newBaks) {
            try { Remove-Item $f.FullName -Force } catch { Write-Warning ("새 백업 파일을 지우지 못함(권한 확인, 컨테이너 mssql 사용자 소유일 수 있음): " + (Redact $f.Name)) }
        }
    }
    $stats = (docker stats --no-stream --format '{{.Name}} {{.MemUsage}}' 2>$null) -join '; '

    $row = [ordered]@{
        label = $Label; run = $run; first_run = [int]($run -eq 1); started_local = $startedLocal.ToString('HH:mm:ss')
        total_client_ms = $totalMs; pre_ms = (Ms $first.Start $bk.Start); backup_ms = $(if ($bk) { [math]::Round($bk.DurMs) } else { '' })
        copy_ms = (Ms $bk.End $afterBk.Start); restore_ms = (Ms $afterBk.Start $rs.End); restore_cmd_ms = $(if ($rs) { [math]::Round($rs.DurMs) } else { '' })
        owner_ms = (Ms $rs.End $own.End); install_ms = (Ms $own.End $snap.Start); install_batches = $installBatches
        snapshot_ms = $(if ($snap) { [math]::Round($snap.DurMs) } else { '' }); context_ms = (Ms $snap.End $last.End); xe_span_ms = (Ms $first.Start $last.End)
        backup_mb = $(if ($bs.Count) { $bs[0].mb } else { '' }); compressed_mb = $(if ($bs.Count) { $bs[0].cmb } else { '' })
        state = $s.state; states = ($states -join '>'); fingerprint_match = $s.fingerprintMatch; mismatches = @($s.fingerprintMismatches).Count; import_left = $importLeft
    }
    if (-not (Test-Path $csv)) { [IO.File]::WriteAllText($csv, (($row.Keys -join ',') + "`n"), $utf8) }
    [IO.File]::AppendAllText($csv, ((@($row.Values) | ForEach-Object { "$_" }) -join ',') + "`n", $utf8)
    $kv = ($row.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
    $detail = @("# run $run label=$Label origin=$OriginDb sandbox=$sandboxDb", $kv,
        ('backup_files_new: ' + ($bakInfo -join '; ')), ('sandbox_files: ' + ($fileLines -join '; ')), ('docker_stats: ' + $stats), ('host_load_at_start: ' + $load), ('events: ' + $ev.Count))
    [IO.File]::AppendAllText((Join-Path $outDir 'runs.log'), (($detail -join "`n") + "`n"), $utf8)
    $detail | Out-Host
    $co.Close(); $cs.Close()
    if ($s.state -ne 'ready') { throw "state=$($s.state): 기준선이 ready가 아니라 멈춘다($csv, $outDir 확인)" }
    Start-Sleep -Seconds 5
}
