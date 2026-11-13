# 집에서 DB로 확인할 것 중 자동화할 수 있는 부분을 한 번에 돌린다(docs/home-test-plan.md의 "자동 단계").
# 사용(저장소 루트, PowerShell 5.1): .\scripts\home-test.ps1 [-Skip Docker,Eval,N1] [-EvalRepeat 3] [-IncludeExec] [-Date 2026-11-14]
# 단계(순서대로, 실패해도 다음 단계로 간다): Docker → Env → Build → Tests → Eval → N1 → (요약)
#   Docker : Docker·SQL Server 컨테이너(sql) 상태 확인, 안 떠 있으면 scripts\db-up.ps1로 띄운다. AW_Origin 유무를 알린다(없으면 N1 AW가 건너뛴다).
#   Env    : scripts\env.ps1(SingleServer)로 DBRIPPLE_SQL_CONNECTION을 읽고 연결을 시험한다. 값은 출력하지 않는다.
#   Build  : dotnet build DbRipple.slnx
#   Tests  : dotnet test DbRipple.slnx (DBRIPPLE_SQL_CONNECTION이 있으므로 DB 테스트 약 58개 포함). 건수는 TRX로 센다.
#   Eval   : 평가 빠른 실행(CascadeLab 30사례, 제품·B0·B2, 반복 -EvalRepeat회, 기본 3). 이전 실행(logs\eval\eval-20261112-143835)과 M1~M4를 비교한다.
#   N1     : 분석기 거부 목록 회귀. 임시 Host(127.0.0.1:5081, 개발 모드)를 DB마다 띄워 POST /api/analyze로 CascadeLab 사례·AW 시연 SQL을 분석하고,
#            결과 JSON을 저장한 뒤 정답의 기대 거부(expected.rejects)와 비교한다. 없는 CascadeLab·CascadeLab_RT·CascadeLab_Reach DB는 만든다.
# 결과: logs\home-test\<날짜>\ (summary.md, summary.json, 단계별 로그·JSON). 비밀값(연결 문자열·비밀번호)은 어디에도 쓰지 않는다.
# 이 스크립트는 Docker 컨테이너와 로컬 개발 DB(CascadeLab*, dbrt_eval_*, AW_Sandbox*)만 건드린다. 원본 서버·운영·회사 DB에는 접속하지 않는다.
param(
    [ValidateSet('Docker', 'Env', 'Build', 'Tests', 'Eval', 'N1')][string[]]$Skip = @(),
    [string]$Date = (Get-Date -Format 'yyyy-MM-dd'),
    [int]$EvalRepeat = 3,
    [string]$BaselineRun = 'logs\eval\eval-20261112-143835',
    [switch]$IncludeExec,
    [ValidateSet('AW', 'CascadeLab', 'CascadeLab_RT', 'CascadeLab_Reach')][string[]]$N1Targets = @('AW', 'CascadeLab', 'CascadeLab_RT', 'CascadeLab_Reach'),
    [int]$N1Port = 5081,
    [int]$HostTimeoutSec = 300
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$utf8 = New-Object System.Text.UTF8Encoding($false)
$outDir = Join-Path (Join-Path $root 'logs\home-test') $Date
New-Item -ItemType Directory -Force $outDir | Out-Null
$logFile = Join-Path $outDir 'home-test.log'
$script:Results = New-Object System.Collections.ArrayList
$script:Facts = [ordered]@{}

function Write-Log([string]$Message) {
    $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message)
    Write-Host $line
    [System.IO.File]::AppendAllText($logFile, $line + "`r`n", $utf8)
}

function ConvertTo-Arg([string]$Text) { if ($Text -match '[\s"]') { '"' + $Text.Replace('"', '\"') + '"' } else { $Text } }

# 네이티브 명령을 실행하고 종료 코드를 돌려준다. 출력은 파일로 받는다(PS 5.1은 네이티브 stderr를 오류로 바꿀 수 있어 Start-Process를 쓴다).
function Invoke-Native([string]$File, [string[]]$Arguments, [string]$OutFile) {
    $err = $OutFile + '.err'
    $argLine = ($Arguments | ForEach-Object { ConvertTo-Arg $_ }) -join ' '
    $p = Start-Process -FilePath $File -ArgumentList $argLine -NoNewWindow -Wait -PassThru -RedirectStandardOutput $OutFile -RedirectStandardError $err
    if (Test-Path $err) { if ((Get-Item $err).Length -eq 0) { Remove-Item $err } }
    return $p.ExitCode
}

# 컨테이너 안 sqlcmd로 질의한다(sa 비밀번호는 컨테이너 환경 변수, 호스트 명령줄·출력에 없다). 질의를 임시 파일로 복사해 -i로 실행한다(run-sql.ps1과 같은 방식: PS 5.1은 인자의 큰따옴표를 망가뜨린다).
# 결과 줄 배열을 돌려준다.
function Invoke-ContainerQuery([string]$Query, [string]$Database = 'master') {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('dbr-' + [guid]::NewGuid().ToString('N') + '.sql')
    $remote = '/tmp/' + [IO.Path]::GetFileName($tmp)
    [IO.File]::WriteAllText($tmp, ('SET NOCOUNT ON;' + "`r`n" + $Query + "`r`n"), $utf8)
    $cmd = '/opt/mssql-tools18/bin/sqlcmd -C -I -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d ' + $Database + ' -h -1 -W -b -f 65001 -i ' + $remote
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        docker compose cp $tmp ('sql:' + $remote) 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '컨테이너로 질의 파일 복사 실패' }
        $out = @(docker compose exec -T sql bash -c $cmd 2>$null | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) { throw ('컨테이너 sqlcmd 종료 코드 ' + $LASTEXITCODE) }
        return $out
    } finally {
        docker compose exec -T sql rm -f $remote 2>$null | Out-Null
        Remove-Item $tmp -ErrorAction SilentlyContinue
        $ErrorActionPreference = $saved
    }
}

function Add-Result([string]$Name, [string]$Status, [string]$Detail, [double]$Seconds) {
    [void]$script:Results.Add([pscustomobject]@{ step = $Name; status = $Status; detail = $Detail; seconds = [math]::Round($Seconds, 1) })
    Write-Log ('[{0}] {1} {2}' -f $Name, $Status, $Detail)
}

# 단계 실행기: 본문은 @{ Status = 'PASS|WARN|FAIL'; Detail = '…' } 를 돌려준다. 예외는 FAIL로 기록하고 다음 단계로 간다.
function Invoke-Step([string]$Name, [string]$Title, [scriptblock]$Body) {
    if ($Skip -contains $Name) { Add-Result $Name 'SKIP' '-Skip으로 건너뜀' 0; return }
    Write-Log ('===== [{0}] {1} =====' -f $Name, $Title)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = @(& $Body) | Where-Object { $_ -is [hashtable] } | Select-Object -Last 1
        if (-not $r) { $r = @{ Status = 'FAIL'; Detail = '단계가 결과를 돌려주지 않음' } }
        Add-Result $Name $r.Status $r.Detail $sw.Elapsed.TotalSeconds
    } catch {
        Add-Result $Name 'FAIL' ($_.Exception.GetType().Name + ': ' + $_.Exception.Message) $sw.Elapsed.TotalSeconds
    }
}

function New-SqlConnection([string]$Database) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($env:DBRIPPLE_SQL_CONNECTION)
    if ($Database) { $b['Initial Catalog'] = $Database }
    return New-Object System.Data.SqlClient.SqlConnection($b.ConnectionString)
}

# ---------------------------------------------------------------- Docker
Invoke-Step 'Docker' 'Docker·SQL Server 컨테이너 확인' {
    if (-not (Test-Path (Join-Path $root '.env'))) { return @{ Status = 'FAIL'; Detail = '.env가 없음(.env.example을 복사해 MSSQL_SA_PASSWORD를 채운다)' } }
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $ver = (docker version --format '{{.Server.Version}}' 2>$null | Out-String).Trim() } catch { $ver = '' } finally { $ErrorActionPreference = $saved }
    if (-not $ver) { return @{ Status = 'FAIL'; Detail = 'Docker 서버에 연결되지 않음(Docker Desktop을 켠다)' } }
    $script:Facts['docker'] = $ver
    $ErrorActionPreference = 'Continue'
    try {
        $id = (docker compose ps -q sql 2>$null | Out-String).Trim()
        $health = if ($id) { (docker inspect -f '{{.State.Status}}/{{.State.Health.Status}}' $id 2>$null | Out-String).Trim() } else { 'none' }
    } finally { $ErrorActionPreference = $saved }
    if ($health -ne 'running/healthy') {
        Write-Log ('컨테이너 상태 ' + $health + ' → scripts\db-up.ps1 실행')
        & (Join-Path $PSScriptRoot 'db-up.ps1') -TimeoutSec 240 | Out-Null
    }
    $ver2 = Invoke-ContainerQuery "SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(30)) + ' ' + CAST(SERVERPROPERTY('Edition') AS varchar(60))"
    $script:Facts['sqlserver'] = ($ver2 -join ' ').Trim()
    $aw = Invoke-ContainerQuery "SELECT CASE WHEN DB_ID(N'AW_Origin') IS NULL THEN 'AW_Origin없음' ELSE 'AW_Origin있음' END"
    $script:Facts['awOrigin'] = ($aw -join '').Trim()
    if ($script:Facts['awOrigin'] -ne 'AW_Origin있음') {
        return @{ Status = 'WARN'; Detail = ('Docker ' + $ver + ', SQL ' + $script:Facts['sqlserver'] + '. AW_Origin이 없어 N1 AW가 실패한다: scripts\fetch-adventureworks.ps1 → scripts\restore-aw-origin.ps1') }
    }
    return @{ Status = 'PASS'; Detail = ('Docker ' + $ver + ', SQL ' + $script:Facts['sqlserver'] + ', ' + $script:Facts['awOrigin']) }
}

# ---------------------------------------------------------------- Env
Invoke-Step 'Env' '연결 설정(SingleServer)과 시험' {
    . (Join-Path $PSScriptRoot 'env.ps1')
    if ([string]::IsNullOrEmpty($env:DBRIPPLE_SQL_CONNECTION)) { return @{ Status = 'FAIL'; Detail = 'DBRIPPLE_SQL_CONNECTION이 만들어지지 않음' } }
    $conn = New-SqlConnection 'master'
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand(); $cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(30))"
        $v = [string]$cmd.ExecuteScalar()
    } finally { $conn.Dispose() }
    return @{ Status = 'PASS'; Detail = ('연결 확인(SingleServer, 서버 ' + $v + '). 연결 문자열은 출력하지 않음') }
}

# ---------------------------------------------------------------- Build
Invoke-Step 'Build' 'dotnet build DbRipple.slnx' {
    $log = Join-Path $outDir 'dotnet-build.log'
    $code = Invoke-Native 'dotnet' @('build', 'DbRipple.slnx', '-nologo') $log
    $tail = (Get-Content $log -Tail 40 | Out-String)
    $warn = if ($tail -match '(\d+)\s*(?:경고|Warning)') { $Matches[1] } else { '?' }
    if ($code -ne 0) { return @{ Status = 'FAIL'; Detail = ('빌드 실패(종료 코드 ' + $code + '), 로그 ' + $log) } }
    return @{ Status = 'PASS'; Detail = ('빌드 성공(경고 ' + $warn + '개 표시), 로그 ' + $log) }
}

# ---------------------------------------------------------------- Tests
Invoke-Step 'Tests' 'dotnet test DbRipple.slnx (DB 테스트 포함)' {
    $dbSet = -not [string]::IsNullOrEmpty($env:DBRIPPLE_SQL_CONNECTION)
    $trx = Join-Path $outDir 'trx'
    if (Test-Path $trx) { Remove-Item $trx -Recurse -Force }
    $log = Join-Path $outDir 'dotnet-test.log'
    $code = Invoke-Native 'dotnet' @('test', 'DbRipple.slnx', '-nologo', '--no-build', '--logger', 'trx', '--results-directory', $trx) $log
    $files = @(Get-ChildItem $trx -Filter '*.trx' -ErrorAction SilentlyContinue)
    $total = 0; $passed = 0; $failed = 0; $notExec = 0; $failedNames = New-Object System.Collections.ArrayList
    foreach ($f in $files) {
        [xml]$x = Get-Content $f.FullName -Raw
        $c = $x.TestRun.ResultSummary.Counters
        $total += [int]$c.total; $passed += [int]$c.passed; $failed += [int]$c.failed; $notExec += [int]$c.notExecuted
        foreach ($r in $x.TestRun.Results.UnitTestResult) { if ($r.outcome -eq 'Failed') { [void]$failedNames.Add($r.testName) } }
    }
    $skipped = $total - $passed - $failed
    $summary = ('전체 ' + $total + ', 통과 ' + $passed + ', 실패 ' + $failed + ', 건너뜀 ' + $skipped + ' (TRX ' + $files.Count + '개)')
    if ($failedNames.Count -gt 0) { [System.IO.File]::WriteAllText((Join-Path $outDir 'failed-tests.txt'), ($failedNames -join "`r`n"), $utf8) }
    if ($files.Count -eq 0) { return @{ Status = 'FAIL'; Detail = ('TRX가 없음(종료 코드 ' + $code + '), 로그 ' + $log) } }
    if ($failed -gt 0 -or $code -ne 0) {
        return @{ Status = 'FAIL'; Detail = ($summary + ', 종료 코드 ' + $code + '. 실패 목록 failed-tests.txt, 로그 ' + $log) }
    }
    if (-not $dbSet -or $skipped -gt 10) {
        return @{ Status = 'WARN'; Detail = ($summary + '. 건너뜀이 많음: DB 테스트가 돌지 않았을 수 있음(Env 단계 확인, 기대 건너뜀 0 근처)') }
    }
    return @{ Status = 'PASS'; Detail = $summary }
}

# ---------------------------------------------------------------- Eval
function Get-HandCases {
    foreach ($f in Get-ChildItem (Join-Path $root 'cascadelab\cases') -Filter 'CL*.json') {
        $j = [System.IO.File]::ReadAllText($f.FullName, $utf8) | ConvertFrom-Json
        $j | Add-Member -NotePropertyName _file -NotePropertyValue $f.Name -Force
        $j
    }
}

function Test-RejectMatch($Expected, [string]$RuleId) {
    if ($Expected.ruleId.EndsWith('*')) { return $RuleId.StartsWith($Expected.ruleId.TrimEnd('*')) }
    return $RuleId -eq $Expected.ruleId
}

Invoke-Step 'Eval' '평가 빠른 실행과 이전 결과 비교(M1~M4)' {
    if ([string]::IsNullOrEmpty($env:DBRIPPLE_SQL_CONNECTION)) { return @{ Status = 'FAIL'; Detail = 'DBRIPPLE_SQL_CONNECTION 없음(Env 단계 필요)' } }
    # 기본: EXEC·거부 사례(CL21~CL25)는 제외해 이전 평가(CL01~CL20)와 같은 사례 집합으로 비교한다. EXEC는 합쳐졌으므로 -IncludeExec로 한 번 더 돌린다.
    $cases = @(Get-HandCases)
    $ids = @($cases | Where-Object {
            $db = $_.database -in @('CascadeLab', 'CascadeLab_RT')
            $exec = @($_.statements | Where-Object { $_.sql -match '^\s*EXEC' }).Count -gt 0
            $rej = $_.expected.status -eq 'rejected'
            $db -and ($IncludeExec -or (-not $exec -and -not $rej))
        } | ForEach-Object { $_.id })
    $runId = 'eval-quick'
    $evalOut = Join-Path $outDir 'eval'
    $runDir = Join-Path $evalOut $runId
    if (Test-Path $runDir) { Remove-Item $runDir -Recurse -Force }
    $log = Join-Path $outDir 'eval-run.log'
    # B1(FK 메타데이터 정적 예측, 2026-11-13)은 이전 결과에 없는 시스템이라 비교에서 NEW로만 기록된다.
    $evalArgs = @('run', '--project', 'eval\DbRipple.Eval.Cli', '-c', 'Release', '--', 'cascadelab', '--systems', 'preview,B0,B1,B2', '--repeat', [string]$EvalRepeat, '--out', $evalOut, '--run-id', $runId)
    if (-not $IncludeExec) { $evalArgs += @('--cases', ($ids -join ',')) }
    Write-Log ('평가 실행: 사례 ' + $ids.Count + '개, 반복 ' + $EvalRepeat + '회(몇 분 걸린다)')
    $code = Invoke-Native 'dotnet' $evalArgs $log
    $metricsNew = Join-Path $runDir 'metrics.csv'
    if (-not (Test-Path $metricsNew)) { return @{ Status = 'FAIL'; Detail = ('metrics.csv가 없음(종료 코드 ' + $code + '), 로그 ' + $log) } }

    # 이전 실행과 (system, metric, scope)별 value 비교. 비율(value)만 본다(반복 수가 달라 분자·분모는 비교하지 않는다).
    $baseMetrics = Join-Path (Join-Path $root $BaselineRun) 'metrics.csv'
    $rows = New-Object System.Collections.ArrayList
    $diffs = 0
    if (Test-Path $baseMetrics) {
        $old = @{}
        foreach ($r in (Import-Csv $baseMetrics -Encoding UTF8)) { $old[($r.system + '|' + $r.metric + '|' + $r.scope)] = $r }
        foreach ($r in (Import-Csv $metricsNew -Encoding UTF8)) {
            $k = $r.system + '|' + $r.metric + '|' + $r.scope
            if (-not $old.ContainsKey($k)) { [void]$rows.Add([pscustomobject]@{ key = $k; baseline = '(없음)'; now = $r.value; same = 'NEW' }); continue }
            $same = ($old[$k].value -eq $r.value)
            if (-not $same) { $diffs++ }
            [void]$rows.Add([pscustomobject]@{ key = $k; baseline = $old[$k].value; now = $r.value; same = $(if ($same) { 'same' } else { 'DIFF' }) })
        }
        $rows | Export-Csv (Join-Path $outDir 'eval-vs-baseline.csv') -NoTypeInformation -Encoding UTF8
    }
    # 핵심 지표 출력(제품)
    $now = @{}
    foreach ($r in (Import-Csv $metricsNew -Encoding UTF8)) { $now[($r.system + '|' + $r.metric + '|' + $r.scope)] = $r }
    function Val([string]$k) { if ($now.ContainsKey($k)) { $now[$k].value } else { '(없음)' } }
    Write-Log ('M1 total precision/recall: ' + (Val 'preview|M1.precision|total') + ' / ' + (Val 'preview|M1.recall|total') + '  (이전 1.0000 / 1.0000)')
    Write-Log ('M2 ALL recall: ' + (Val 'preview|M2.recall|ALL') + '  (이전 1.0000)')
    Write-Log ('M3 certain 과신: ' + $(if ($now.ContainsKey('preview|M3.certain_overconfident|certain')) { $now['preview|M3.certain_overconfident|certain'].numerator + '/' + $now['preview|M3.certain_overconfident|certain'].denominator } else { '(없음)' }) + '  (이전 0/940, 반복 20회 기준)')
    Write-Log ('M4 정규 서명 안정: ' + (Val 'preview|M4.canonical_stable_rate|case') + '  (이전 1.0000)')

    # 케이스 단위: 미리보기가 거부한 사례(N1 영향 후보)와 상태 불일치
    $casesCsv = Join-Path $runDir 'cases.csv'
    $rejected = @(); $statusBad = @()
    if (Test-Path $casesCsv) {
        $cc = @(Import-Csv $casesCsv -Encoding UTF8 | Where-Object { $_.system -eq 'preview' })
        $rejected = @($cc | Where-Object { $_.status_actual -eq 'rejected' -and $_.status_expected -ne 'rejected' } | ForEach-Object { $_.case_id } | Sort-Object -Unique)
        $statusBad = @($cc | Where-Object { $_.status_match -ne 'true' } | ForEach-Object { $_.case_id } | Sort-Object -Unique)
    }
    $diffFile = Join-Path $runDir 'differences.txt'
    $diffLines = if (Test-Path $diffFile) { @(Get-Content $diffFile).Count } else { 0 }
    $script:Facts['evalRun'] = $runDir
    $detail = ('사례 ' + $ids.Count + '개 × ' + $EvalRepeat + '회, 종료 코드 ' + $code + ', 이전과 다른 지표 ' + $diffs + '개(eval-vs-baseline.csv), 미리보기 거부 사례 [' + ($rejected -join ',') + '], 상태 불일치 사례 [' + ($statusBad -join ',') + '], differences.txt ' + $diffLines + '줄')
    if ($code -ne 0 -or $rejected.Count -gt 0) { return @{ Status = 'FAIL'; Detail = $detail } }
    if ($IncludeExec) { return @{ Status = 'WARN'; Detail = ('-IncludeExec: 사례 집합이 이전과 달라 지표 비교는 참고용. ' + $detail) } }
    if ($diffs -gt 0 -or $statusBad.Count -gt 0) { return @{ Status = 'WARN'; Detail = $detail + '. 지표 차이는 eval-vs-baseline.csv의 DIFF 줄, 원인은 differences.txt에서' } }
    return @{ Status = 'PASS'; Detail = $detail }
}

# ---------------------------------------------------------------- N1
function Invoke-Api([string]$Method, [string]$Path, $Body) {
    $uri = 'http://127.0.0.1:' + $N1Port + $Path
    $headers = @{ 'X-DbRipple-Request' = '1' }
    if ($Method -eq 'GET') { return Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 120 }
    $bytes = if ($null -ne $Body) { $utf8.GetBytes(($Body | ConvertTo-Json -Compress -Depth 8)) } else { [byte[]]@() }
    return Invoke-WebRequest -Uri $uri -Method $Method -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $bytes -UseBasicParsing -TimeoutSec 300
}

function Stop-HostProcess($Proc) {
    if ($Proc -and -not $Proc.HasExited) {
        $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { & taskkill /PID $Proc.Id /T /F 2>$null | Out-Null } finally { $ErrorActionPreference = $saved }
    }
}

# 한 DB를 대상으로 임시 Host를 띄워 SQL 목록을 분석한다. $Items = @{ id; sql; expected = @(…expected.rejects…) } 배열. 결과: 대상별 판정 문자열 목록
function Invoke-N1Target([string]$Name, [string]$OriginDb, [object[]]$Items) {
    $tdir = Join-Path (Join-Path $outDir 'n1') $Name
    New-Item -ItemType Directory -Force $tdir | Out-Null
    $sandbox = $(if ($OriginDb -eq 'AW_Origin') { 'AW_Sandbox' } else { $OriginDb + '_Sandbox' })
    $saved = @{}
    $overrides = [ordered]@{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DbRipple__Urls'         = ('http://127.0.0.1:' + $N1Port)
        'DbRipple__Mode'         = 'SingleServer'
        'DbRipple__OriginDb'     = $OriginDb
        'DbRipple__SandboxDb'    = $sandbox
        'DbRipple__SnapshotDb'   = ($sandbox + '_base')
        'DbRipple__BackupPath'   = ('/var/opt/mssql/backup/' + $OriginDb + '_baseline.bak')
    }
    foreach ($k in $overrides.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process'); [Environment]::SetEnvironmentVariable($k, $overrides[$k], 'Process') }
    $proc = $null
    $lines = New-Object System.Collections.ArrayList
    $bad = 0
    try {
        $hostOut = Join-Path $tdir 'host.out.log'
        # --dev: launchSettings를 쓰지 않으므로 개발 모드(실행 토큰 없음)를 직접 켠다. 없으면 실행 파일 모드라 /api가 401이다.
        $proc = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', (ConvertTo-Arg (Join-Path $root 'src\DbRipple.Host')), '--no-launch-profile', '--no-build', '--', '--dev') `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput $hostOut -RedirectStandardError ($hostOut + '.err')
        $deadline = (Get-Date).AddSeconds(90)
        $up = $false
        while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
            try { $r = Invoke-Api 'GET' '/api/sandbox' $null; $up = $true; break } catch { Start-Sleep -Seconds 2 }
        }
        if (-not $up) { throw ('임시 Host가 뜨지 않음(로그 ' + $hostOut + ')') }
        $before = (Invoke-Api 'GET' '/api/sandbox' $null).Content | ConvertFrom-Json
        [void](Invoke-Api 'POST' '/api/sandbox/refresh' $null)
        # 새로 고침은 비동기(202)다. 옛 기준선(ready)을 새것으로 착각하지 않게 building을 보았거나 baselineAt이 바뀐 뒤에만 ready를 받아들인다.
        $start = Get-Date
        $deadline = $start.AddSeconds($HostTimeoutSec)
        $state = ''
        $sawBuilding = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $s = (Invoke-Api 'GET' '/api/sandbox' $null).Content | ConvertFrom-Json
            $state = [string]$s.state
            if ($state -eq 'building') { $sawBuilding = $true; continue }
            $changed = ([string]$s.baselineAt -ne [string]$before.baselineAt)
            $waited = ((Get-Date) - $start).TotalSeconds
            if ($state -eq 'ready' -and ($sawBuilding -or $changed -or $waited -gt 30)) { break }
            if ($state -eq 'error' -and ($sawBuilding -or $waited -gt 30)) { break }
        }
        [System.IO.File]::WriteAllText((Join-Path $tdir 'sandbox.json'), ((Invoke-Api 'GET' '/api/sandbox' $null).Content), $utf8)
        if ($state -ne 'ready') { throw ('기준선이 ready가 되지 않음(state ' + $state + '). ' + $tdir + '\sandbox.json, host.out.log 확인') }
        foreach ($it in $Items) {
            $resp = Invoke-Api 'POST' '/api/analyze' @{ sql = $it.sql }
            [System.IO.File]::WriteAllText((Join-Path $tdir ($it.id + '.json')), $resp.Content, $utf8)
            $res = $resp.Content | ConvertFrom-Json
            $actual = @($res.rejects | Where-Object { $_ } | ForEach-Object { [pscustomobject]@{ ruleId = [string]$_.ruleId; statementIndex = [int]$_.statementIndex } })
            $left = New-Object System.Collections.ArrayList
            foreach ($a in $actual) { [void]$left.Add($a) }
            $problems = New-Object System.Collections.ArrayList
            foreach ($e in @($it.expected)) {
                if (-not $e) { continue }
                $hit = $null
                foreach ($a in $left) { if ($a.statementIndex -eq [int]$e.statementIndex -and (Test-RejectMatch $e $a.ruleId)) { $hit = $a; break } }
                if ($hit) { [void]$left.Remove($hit) } else { [void]$problems.Add('기대한 거부 없음 ' + $e.ruleId + '@' + $e.statementIndex) }
            }
            foreach ($a in $left) { [void]$problems.Add('예상 밖 거부 ' + $a.ruleId + '@' + $a.statementIndex) }
            if ($problems.Count -gt 0) { $bad++; [void]$lines.Add($it.id + ': FAIL ' + ($problems -join '; ')) }
            else { [void]$lines.Add($it.id + ': ok(거부 ' + $actual.Count + '건' + $(if ($actual.Count -gt 0) { ' ' + (($actual | ForEach-Object { $_.ruleId + '@' + $_.statementIndex }) -join ',') } else { '' }) + ')') }
        }
    } finally {
        Stop-HostProcess $proc
        foreach ($k in $overrides.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
    }
    [System.IO.File]::WriteAllText((Join-Path $tdir 'verdict.txt'), ($lines -join "`r`n"), $utf8)
    return [pscustomobject]@{ target = $Name; bad = $bad; total = $Items.Count; lines = $lines }
}

function Initialize-CascadeLabDatabases([string]$Target) {
    # 필요한 DB가 없거나(CascadeLab·RT) 최신 스키마(cl25)가 없으면 스크립트로 다시 만든다. 공유 CascadeLab 사본이 바뀌는 것은 의도한 동작이다(00-create-databases.sql과 같음).
    if ($Target -eq 'CascadeLab_Reach') {
        $has = (Invoke-ContainerQuery "SELECT CASE WHEN DB_ID(N'CascadeLab_Reach') IS NULL THEN 0 ELSE 1 END") -join ''
        if ($has.Trim() -ne '1') { & (Join-Path $PSScriptRoot 'run-sql.ps1') 'cascadelab\sql\extra\cl25d.sql' -Log 'logs\home-test\cl25d-create.log' | Out-Null }
        return
    }
    $need = (Invoke-ContainerQuery "SELECT CASE WHEN DB_ID(N'CascadeLab') IS NULL OR DB_ID(N'CascadeLab_RT') IS NULL THEN 1 ELSE 0 END") -join ''
    $stale = '1'
    if ($need.Trim() -ne '1') { $stale = (Invoke-ContainerQuery "SELECT CASE WHEN SCHEMA_ID(N'cl25') IS NULL THEN 1 ELSE 0 END" 'CascadeLab') -join '' }
    if ($need.Trim() -eq '1' -or $stale.Trim() -eq '1') {
        Write-Log 'CascadeLab·CascadeLab_RT를 (다시) 만든다: 00-create-databases.sql + cl01~cl25.sql'
        & (Join-Path $PSScriptRoot 'run-sql.ps1') 'cascadelab\sql\00-create-databases.sql' -Log 'logs\home-test\cl-00.log' | Out-Null
        foreach ($f in (Get-ChildItem (Join-Path $root 'cascadelab\sql') -Filter 'cl*.sql' | Sort-Object Name)) {
            & (Join-Path $PSScriptRoot 'run-sql.ps1') ('cascadelab\sql\' + $f.Name) -Log ('logs\home-test\cl-' + $f.BaseName + '.log') | Out-Null
        }
    }
}

Invoke-Step 'N1' '분석기 거부 목록 회귀(도달 폐포 N1): CascadeLab 사례·AW 시연 SQL' {
    if ([string]::IsNullOrEmpty($env:DBRIPPLE_SQL_CONNECTION)) { return @{ Status = 'FAIL'; Detail = 'DBRIPPLE_SQL_CONNECTION 없음(Env 단계 필요)' } }
    $hostDll = Join-Path $root 'src\DbRipple.Host\bin'
    if (-not (Test-Path $hostDll)) { return @{ Status = 'FAIL'; Detail = 'Host가 빌드되지 않음(Build 단계 필요)' } }
    $hand = @(Get-HandCases)
    $summary = New-Object System.Collections.ArrayList
    $worst = 'PASS'
    foreach ($t in $N1Targets) {
        try {
            if ($t -eq 'AW') {
                $items = @(
                    @{ id = 'DM1'; sql = 'DELETE FROM Sales.SalesOrderHeader WHERE SalesOrderID = 43697;'; expected = @() },
                    @{ id = 'DM2'; sql = 'UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderDetailID = 52;'; expected = @() },
                    @{ id = 'DM3'; sql = 'DELETE FROM HumanResources.Employee WHERE BusinessEntityID = 1;'; expected = @() },
                    @{ id = 'DM7'; sql = 'UPDATE Production.Product SET ListPrice = ListPrice + 1.00 WHERE ProductID = 680;'; expected = @() }
                )
                if ($script:Facts['awOrigin'] -eq 'AW_Origin없음') { throw 'AW_Origin이 없음(restore-aw-origin.ps1)' }
                $r = Invoke-N1Target 'AW' 'AW_Origin' $items
            } else {
                Initialize-CascadeLabDatabases $t
                $items = @()
                foreach ($c in ($hand | Where-Object { $_.database -eq $t })) {
                    $stmts = @($c.statements | Sort-Object index)
                    # 기본은 첫 EXEC 문장 앞까지만 분석한다(이전 회귀와 같은 범위). -IncludeExec면 EXEC 문장까지 전부.
                    $keep = @()
                    foreach ($s in $stmts) { if (-not $IncludeExec -and $s.sql -match '^\s*EXEC') { break }; $keep += $s }
                    if ($keep.Count -eq 0) { continue }
                    $exp = @()
                    if ($c.expected.PSObject.Properties.Name -contains 'rejects') {
                        $exp = @($c.expected.rejects | Where-Object { $_.statementIndex -le $keep.Count -and ($IncludeExec -or -not $_.ruleIdPending) })
                    }
                    $items += @{ id = $c.id; sql = (($keep | ForEach-Object { $_.sql }) -join "`r`n"); expected = $exp }
                }
                $r = Invoke-N1Target $t $t $items
            }
            $summary.Add(($t + ': ' + ($r.total - $r.bad) + '/' + $r.total + ' 일치' + $(if ($r.bad -gt 0) { ' — 불일치 ' + (($r.lines | Where-Object { $_ -match 'FAIL' }) -join ' | ') } else { '' }))) | Out-Null
            if ($r.bad -gt 0) { $worst = 'FAIL' }
        } catch {
            $summary.Add($t + ': 실행 실패 ' + $_.Exception.Message) | Out-Null
            $worst = 'FAIL'
        }
    }
    return @{ Status = $worst; Detail = ($summary -join ' / ') + '. 결과 JSON·판정: ' + (Join-Path $outDir 'n1') }
}

# ---------------------------------------------------------------- 요약
$rank = @{ PASS = 0; SKIP = 0; WARN = 1; FAIL = 2 }
$overall = 'PASS'
foreach ($r in $script:Results) { if ($rank[$r.status] -gt $rank[$overall]) { $overall = $r.status } }
$head = ''; $dotnetVer = ''
try { $ErrorActionPreference = 'Continue'; $head = (& git -C $root rev-parse --short HEAD 2>$null | Out-String).Trim(); $dotnetVer = (& dotnet --version 2>$null | Out-String).Trim() } catch { } finally { $ErrorActionPreference = 'Stop' }
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# 집에서 DB 테스트 결과 ' + $Date)
[void]$md.AppendLine('')
[void]$md.AppendLine('전체: **' + $overall + '** (실행 ' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + ', 저장소 ' + $head + ', dotnet ' + $dotnetVer + ', Docker ' + $script:Facts['docker'] + ', SQL ' + $script:Facts['sqlserver'] + ')')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 단계 | 결과 | 초 | 내용 |')
[void]$md.AppendLine('|---|---|---|---|')
foreach ($r in $script:Results) { [void]$md.AppendLine('| ' + $r.step + ' | ' + $r.status + ' | ' + $r.seconds + ' | ' + ($r.detail -replace '\|', '/') + ' |') }
[void]$md.AppendLine('')
[void]$md.AppendLine('증거 범위: DB 실측(로컬 Docker 컨테이너, SingleServer) + 임시 Host API(127.0.0.1:' + $N1Port + ', 개발 모드). 실행하지 않은 단계는 SKIP이다.')
[void]$md.AppendLine('남은 수동 단계(Split·스파이크 SP11~SP21·실행 파일 화면·큰 DELETE 성능)는 docs/home-test-plan.md 5절 이후를 따른다.')
[System.IO.File]::WriteAllText((Join-Path $outDir 'summary.md'), $md.ToString(), $utf8)
$json = [ordered]@{ date = $Date; overall = $overall; commit = $head; facts = $script:Facts; steps = @($script:Results) }
[System.IO.File]::WriteAllText((Join-Path $outDir 'summary.json'), ($json | ConvertTo-Json -Depth 6), $utf8)
Write-Log ('요약: ' + (Join-Path $outDir 'summary.md') + ' → 전체 ' + $overall)
if ($overall -eq 'FAIL') { exit 1 }
exit 0
