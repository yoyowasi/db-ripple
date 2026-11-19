#Requires -Version 7.0
<#
용도: Split 모드(원본 = compose sql-origin, SQL Server 2022 / 샌드박스 = sql, SQL Server 2025 CU10)에서 저장 프로시저 호출 EXEC의
      [원본에 적용]을 컨테이너 두 대와 개발 모드 Host API로 끝까지 돌려 시나리오 S0~S6의 기대값을 단언한다.
      순서는 docs/spikes/SP17.md(Split 끝까지)를 EXEC에 맞춘 것이다. CI는 .github/workflows/split-exec.yml.
      입력 EXEC: EXEC cl21.usp_Outer @id = 1, @delta = 5;  (cascadelab/sql/cl21.sql, cascadelab/cases/CL21-proc-no-branch.json)
      정상 결과: cl21.Parent id 1 qty 10→15, cl21.Touch parent_id 1 touched 0→1, cl21.Log에 'outer' 1행, cl21.Spare 그대로, 반환 값 0.
기대값 근거(코드, 문서와 다르면 코드):
      src/DbRipple.Apply/ApplyService.cs RunLockedAsync: 기본 선행 조건 → 검증 모드 결정(스냅샷 불가 + 동의 없음이면 WEAK_VERIFICATION_NOT_ACCEPTED)
        → 실패가 하나도 없을 때만 재분석과 CheckProceduresAsync(프로시저 권한·정의). 그래서 동의 없는 [적용]은 프로시저 검사 전에 WEAK로 멈춘다.
      src/DbRipple.Apply/ApplySteps.cs OriginProcedures.Judge: 정의를 못 읽으면(권한 없음·VIEW DEFINITION 없음) 또는 다르면 PROC_DEFINITION_CHANGED,
        최상위 EXECUTE가 없으면 PROC_EXECUTE_PERMISSION_MISSING(둘 다 해당하면 둘 다).
      ApplyService.ApplyAsync: 이미 커밋한 미리보기 id는 null → API 404(src/DbRipple.Api/DbRippleApi.cs). precondition_failed는 미리보기를 소비하지 않는다.
실행: 저장소 루트, PowerShell 7(Linux 러너 기준, Windows PowerShell 5.1은 지원하지 않음).
      .env에 MSSQL_SA_PASSWORD, ORIGIN_SQL_SERVER=127.0.0.1,14334, ORIGIN_SQL_USER=dbr_origin_user, ORIGIN_SQL_PASSWORD, ORIGIN_SQL_TRUST_CERT=true($ 금지).
      pwsh -NoProfile -File scripts/split-exec-check.ps1 [-OutDir logs/home-test/split-exec] [-Port 5080] [-SkipBuild] [-SkipContainerStart] [-BaselineTimeoutSec 600]
매개변수:
  -OutDir             결과 폴더(저장소 루트 기준). summary.md·summary.json, API 응답 JSON, sqlcmd 로그, Host 로그. 비밀번호는 쓰지 않는다.
  -Port               Host 포트(127.0.0.1). 개발 모드는 포트가 쓰이면 다른 포트로 바꾸지 않으므로 시작 전에 비어 있는지 본다.
  -SkipBuild          dotnet build src/DbRipple.Host(Debug, --dev에 필요)를 건너뛴다.
  -SkipContainerStart 컨테이너 기동(scripts/db-up.ps1, sql-origin)을 건너뛴다(이미 healthy일 때).
  -BaselineTimeoutSec 기준선 구축(POST /api/sandbox/refresh) 대기 상한(초).
종료 코드: 0 모두 기대와 같음, 1 하나 이상 다르거나 실행 못 함, 2 준비 단계 실패(시나리오 미실행).
주의: 원본 컨테이너의 CascadeLab을 지우고 다시 만든다(00-create-databases.sql). 원본 모사 컨테이너에서만 쓴다. 컨테이너는 끄지 않는다(CI 워크플로가 정리).
#>
[CmdletBinding()]
param(
    [string]$OutDir = 'logs/home-test/split-exec',
    [ValidateRange(1024, 65535)][int]$Port = 5080,
    [switch]$SkipBuild,
    [switch]$SkipContainerStart,
    [ValidateRange(30, 3600)][int]$BaselineTimeoutSec = 600
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest 진행 표시가 CI 로그를 채우지 않게
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
$Utf8 = [System.Text.UTF8Encoding]::new($false)
$Pwsh = (Get-Process -Id $PID).Path
$ContainerRun = Join-Path $Root 'spikes/sql/sp_container-run.ps1'
$FingerprintSql = Join-Path $Root 'spikes/sql/sp17_20_origin_fingerprint.sql'
$LoginSql = Join-Path $Root 'spikes/sql/sp17_10_create_limited_login.sql'
$ExecSql = 'EXEC cl21.usp_Outer @id = 1, @delta = 5;'
$BaseUrl = "http://127.0.0.1:$Port"
$StartState = 'Parent 1=10,2=20; Touch 1=0,2=0; Log -; Spare 100=0'

$OutAbs = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $Root $OutDir }))
$LogsRoot = [IO.Path]::GetFullPath((Join-Path $Root 'logs')) + [IO.Path]::DirectorySeparatorChar
if (Test-Path -LiteralPath $OutAbs) {
    # 지난 실행 결과가 섞이지 않게 비운다. 다른 폴더를 지우지 않도록 logs/ 아래일 때만.
    if ($OutAbs.StartsWith($LogsRoot, [StringComparison]::OrdinalIgnoreCase)) { Get-ChildItem -LiteralPath $OutAbs -File | Remove-Item -Force }
    else { Write-Warning "결과 폴더가 logs/ 밖이라 비우지 않음: $OutAbs" }
}
New-Item -ItemType Directory -Force -Path $OutAbs | Out-Null

$script:Scenarios = [System.Collections.Generic.List[object]]::new()
$script:SetupError = $null
$script:BaselineInfo = $null
$script:Envi = [ordered]@{}
$script:HostProc = $null
$script:Overall = 'FAIL'

# ---------- 공통 도우미 ----------

function Rel([string]$Path) { [IO.Path]::GetRelativePath($Root, $Path) }

function Write-Text([string]$Name, [string]$Text) {
    $p = Join-Path $OutAbs $Name
    [IO.File]::WriteAllText($p, $Text, $Utf8)
    $p
}

function Write-Step([string]$Text) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Text
    Write-Host $line
    [IO.File]::AppendAllText((Join-Path $OutAbs 'progress.log'), $line + "`n", $Utf8)
}

# 네이티브 명령: stderr(진행 메시지)를 오류로 바꾸지 않고 종료 코드로 판정한다
function Invoke-Native {
    param([Parameter(Mandatory)][string]$Exe, [string[]]$ArgList = @(), [string]$LogName)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = @()
    $code = $null
    try {
        $out = @(& $Exe @ArgList 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } catch {
        $out += "실행 실패: $Exe ($($_.Exception.Message))"
        $code = -1
    } finally { $ErrorActionPreference = $prev }
    if ($LogName) { [IO.File]::WriteAllLines((Join-Path $OutAbs $LogName), [string[]]$out, $Utf8) }
    [pscustomobject]@{ Code = $code; Lines = $out }
}

# sp_container-run.ps1을 자식 pwsh로 돌린다(그 스크립트의 Set-Location·exit·throw가 이 스크립트에 번지지 않게). sa는 컨테이너 환경 변수.
function Invoke-ContainerSql {
    param(
        [Parameter(Mandatory)][string]$File,
        [Parameter(Mandatory)][string]$LogName,
        [ValidateSet('sql-origin', 'sql')][string]$Service = 'sql-origin',
        [string]$Database = 'master',
        [string]$PasswordFromEnv
    )
    $logAbs = Join-Path $OutAbs $LogName
    $argList = @('-NoProfile', '-File', $ContainerRun, $File, '-Service', $Service, '-Database', $Database, '-Log', (Rel $logAbs))
    if ($PasswordFromEnv) { $argList += @('-PasswordFromEnv', $PasswordFromEnv) }
    $r = Invoke-Native -Exe $Pwsh -ArgList $argList
    if (-not (Test-Path -LiteralPath $logAbs)) {
        [IO.File]::WriteAllLines($logAbs, [string[]](@("# $(Rel $File) service=$Service db=$Database exit=$($r.Code) (sp_container-run이 로그를 쓰기 전에 끝남)") + $r.Lines), $Utf8)
    } elseif ($r.Code -ne 0) {
        [IO.File]::AppendAllLines($logAbs, [string[]](@('# sp_container-run 출력(실패)') + $r.Lines), $Utf8)
    }
    [pscustomobject]@{ Code = $r.Code; Lines = @(Get-Content -LiteralPath $logAbs -Encoding utf8); Log = $LogName }
}

function Get-Val($Lines, [string]$Kind) {
    foreach ($l in $Lines) { if ($l -like "VAL|$Kind|*") { return $l.Substring(("VAL|$Kind|").Length) } }
    $null
}

function Invoke-Api {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body, [string]$SaveAs)
    # 변경 요청은 X-DbRipple-Request: 1과 허용 출처가 필요하다(CrossSiteRequestGuard)
    $params = @{
        Method = $Method; Uri = $BaseUrl + $Path; SkipHttpErrorCheck = $true; TimeoutSec = 600
        Headers = @{ 'X-DbRipple-Request' = '1'; 'Origin' = $BaseUrl }
    }
    if ($null -ne $Body) {
        $params.Body = $Utf8.GetBytes(($Body | ConvertTo-Json -Compress -Depth 5))
        $params.ContentType = 'application/json; charset=utf-8'
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $code = -1
    $content = ''
    try {
        $resp = Invoke-WebRequest @params
        $code = [int]$resp.StatusCode
        $content = if ($resp.Content -is [byte[]]) { $Utf8.GetString($resp.Content) } else { [string]$resp.Content }
    } catch {
        $content = "요청 실패: $($_.Exception.Message)"
    }
    $sw.Stop()
    if ($SaveAs) { $null = Write-Text $SaveAs $content }
    $json = $null
    if ($content -and $content.TrimStart().StartsWith('{')) {
        # 대소문자만 다른 키(행 값 사전의 열 이름 등)가 있으면 PSCustomObject 변환이 실패하므로 해시 테이블로 다시 읽는다
        try { $json = $content | ConvertFrom-Json } catch { try { $json = $content | ConvertFrom-Json -AsHashtable } catch { $json = $null } }
    }
    [pscustomobject]@{ Code = $code; Content = $content; Json = $json; Ms = $sw.ElapsedMilliseconds }
}

# ---------- 원본 값(sa로 독립 확인) ----------

function Read-OriginState([string]$Tag) {
    $r = Invoke-ContainerSql -File $script:StateSqlPath -Database CascadeLab -LogName "$Tag.log"
    $s = [ordered]@{
        Ok = ($r.Code -eq 0); Parent = [ordered]@{}; Touch = [ordered]@{}; LogRows = [System.Collections.Generic.List[string]]::new()
        Spare = [ordered]@{}; Def = [ordered]@{}; Perms = [System.Collections.Generic.List[string]]::new(); File = $r.Log
    }
    foreach ($line in $r.Lines) {
        if ($line -notlike 'VAL|*') { continue }
        $p = $line.Split('|')
        switch ($p[1]) {
            'Parent' { $s.Parent[$p[2]] = $p[3] }
            'Touch' { $s.Touch[$p[2]] = $p[3] }
            'Spare' { $s.Spare[$p[2]] = $p[3] }
            'Log' { $s.LogRows.Add("$($p[2]):$($p[3])") }
            'def' { $s.Def[$p[2]] = $p[3] }
            'perm' { $s.Perms.Add($p[2]) }
        }
    }
    $s
}

function Format-Map($Map) {
    if ($Map.Count -eq 0) { return '-' }
    (@($Map.Keys) | ForEach-Object { "$_=$($Map[$_])" }) -join ','
}

function Format-State($S) {
    if ($null -eq $S -or -not $S.Ok) { return '(읽기 실패)' }
    $log = if ($S.LogRows.Count -gt 0) { $S.LogRows -join ',' } else { '-' }
    "Parent $(Format-Map $S.Parent); Touch $(Format-Map $S.Touch); Log $log; Spare $(Format-Map $S.Spare)"
}

function Read-Fingerprint([string]$Tag) {
    $r = Invoke-ContainerSql -File $FingerprintSql -LogName "$Tag.log"
    [pscustomobject]@{ Ok = ($r.Code -eq 0); Ddl = @($r.Lines | Where-Object { $_ -match '^ddl_' }); Log = $r.Log }
}

# ---------- 시나리오 기록·단언 ----------

function New-Scenario([string]$Id, [string]$Title, [string]$Request, [string]$Expected) {
    $s = [ordered]@{
        id = $Id; title = $Title; request = $Request; expected = $Expected
        http = $null; status = $null; preconditionFailures = @(); verificationMode = $null; mismatches = @()
        message = $null; originBefore = $null; originAfter = $null; verdict = 'FAIL'
        failures = [System.Collections.Generic.List[string]]::new()
        notes = [System.Collections.Generic.List[string]]::new()
        evidence = [System.Collections.Generic.List[string]]::new()
    }
    $script:Scenarios.Add($s)
    $s
}

function Test-That($S, [bool]$Condition, [string]$Text) { if (-not $Condition) { $S.failures.Add($Text) } }

function Add-Exception($S, $ErrorRecord) { $S.failures.Add("예외: $($ErrorRecord.Exception.Message)") }

function Set-Origin($S, $Before, $After) {
    $S.originBefore = Format-State $Before
    $S.originAfter = Format-State $After
    foreach ($x in @($Before, $After)) { if ($x -and $x.File -and -not $S.evidence.Contains($x.File)) { $S.evidence.Add($x.File) } }
}

function Test-Unchanged($S, $Before, $After) {
    Test-That $S ($Before.Ok -and $After.Ok) '원본 값을 sa로 읽지 못함'
    Test-That $S ((Format-State $Before) -eq (Format-State $After)) "원본 값이 바뀜: 전 [$(Format-State $Before)] 후 [$(Format-State $After)]"
}

function New-Preview($S, [string]$Tag) {
    $r = Invoke-Api -Method Post -Path '/api/previews' -Body @{ sql = $ExecSql } -SaveAs "$Tag-preview.json"
    $S.evidence.Add("$Tag-preview.json")
    $j = $r.Json
    if ($r.Code -ne 200 -or $null -eq $j -or $j.status -ne 'ok' -or -not $j.previewId -or -not $j.sqlHash) {
        $what = if ($r.Code -eq 200 -and $j) { "status $($j.status)" } elseif ($j) { "$($j.title) $($j.detail)" } else { $r.Content }
        $S.failures.Add("미리보기 실패(HTTP $($r.Code)): $what")
        return $null
    }
    $rv = @($j.statements)[0].returnValue
    $S.notes.Add("미리보기 $($j.previewId) ok, 반환 값 $rv, $($r.Ms)ms")
    $j
}

function Invoke-Apply($S, $Preview, [bool]$AllowKeyset, [string]$Tag) {
    $body = [ordered]@{ sqlHash = $Preview.sqlHash }
    if ($AllowKeyset) { $body.allowKeysetVerification = $true }
    $r = Invoke-Api -Method Post -Path "/api/previews/$([uri]::EscapeDataString([string]$Preview.previewId))/apply" -Body $body -SaveAs "$Tag-apply.json"
    $S.evidence.Add("$Tag-apply.json")
    $S.http = $r.Code
    $j = $r.Json
    if ($r.Code -eq 200 -and $j) {
        $S.status = [string]$j.status
        $S.preconditionFailures = @($j.preconditionFailures | Where-Object { $_ } | ForEach-Object { [string]$_ })
        $S.message = [string]$j.message
        $S.verificationMode = [string]$j.verification.mode
        $S.mismatches = @($j.verification.mismatches | Where-Object { $_ } | ForEach-Object { "$($_.kind): $($_.message)" })
    } elseif ($j) {
        $S.message = ("$($j.title) $($j.detail)").Trim()
    } else {
        $S.message = $r.Content
    }
    $S.notes.Add("[적용] $($r.Ms)ms")
    $r
}

function Test-Codes($S, [string[]]$Expected) {
    $actual = @($S.preconditionFailures | Sort-Object -Unique) -join ','
    $exp = @($Expected | Sort-Object -Unique) -join ','
    Test-That $S ($actual -eq $exp) "preconditionFailures 기대 [$exp], 실제 [$($S.preconditionFailures -join ',')]"
}

# precondition_failed 공통: HTTP 200(상태 5종 모두 200), 원본 로그인에 CREATE DATABASE가 없어 검증 모드는 KEYSET_HASH로 정해진 뒤 막힌다
function Test-Precondition($S, [string[]]$Codes) {
    Test-That $S ($S.http -eq 200) "HTTP 기대 200, 실제 $($S.http)"
    Test-That $S ($S.status -eq 'precondition_failed') "status 기대 precondition_failed, 실제 $($S.status)"
    Test-Codes $S $Codes
    Test-That $S ($S.verificationMode -eq 'KEYSET_HASH') "verification.mode 기대 KEYSET_HASH, 실제 $($S.verificationMode)"
}

# ---------- Host ----------

function Start-DbRippleHost {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $listener.Start(); $listener.Stop() } catch { throw "포트 $Port 이(가) 이미 쓰이고 있음(개발 모드는 다른 포트로 바꾸지 않음). -Port로 바꿀 것" }
    $dll = Get-ChildItem -Path (Join-Path $Root 'src/DbRipple.Host/bin/Debug/*/DbRipple.Host.dll') -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dll) { throw 'src/DbRipple.Host/bin/Debug/*/DbRipple.Host.dll 없음(Debug 빌드 필요, -SkipBuild 빼고 다시)' }
    # 콘텐츠 루트(appsettings.json)는 작업 폴더. 데이터 경로는 모두 절대 경로로 돌린다(기본값이면 AW 매니페스트를 덮어씀, SP17.md).
    $workDir = Join-Path $Root 'src/DbRipple.Host'
    $outLog = Join-Path $OutAbs 'host.out.log'
    $errLog = Join-Path $OutAbs 'host.err.log'
    if ($IsLinux -or $IsMacOS) {
        # 셸 리디렉션으로 로그를 OS가 직접 쓰게 한다(강제 종료해도 남음). exec라 PID가 dotnet 자신이다.
        $q = { param([string]$s) "'" + $s.Replace("'", "'\''") + "'" }
        $psi = [System.Diagnostics.ProcessStartInfo]::new('bash')
        $psi.ArgumentList.Add('-c')
        $psi.ArgumentList.Add("exec dotnet $(& $q $dll.FullName) --dev --no-browser >$(& $q $outLog) 2>$(& $q $errLog)")
        $psi.WorkingDirectory = $workDir
        $psi.UseShellExecute = $false
        $script:HostProc = [System.Diagnostics.Process]::Start($psi)
    } else {
        $script:HostProc = Start-Process -FilePath 'dotnet' -ArgumentList @("`"$($dll.FullName)`"", '--dev', '--no-browser') `
            -WorkingDirectory $workDir -NoNewWindow -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    }
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        if ($script:HostProc.HasExited) { throw "Host가 바로 끝남(종료 코드 $($script:HostProc.ExitCode)). host.out.log·host.err.log 확인" }
        if ((Invoke-Api -Method Get -Path '/api/sandbox').Code -eq 200) { return }
        Start-Sleep -Seconds 2
    }
    throw 'Host API(GET /api/sandbox) 응답 대기 시간 초과(120초)'
}

function Stop-DbRippleHost {
    if ($script:HostProc -and -not $script:HostProc.HasExited) {
        try { $script:HostProc.Kill($true) } catch { Write-Warning "Host 종료 실패: $($_.Exception.Message)" }
        try { $null = $script:HostProc.WaitForExit(15000) } catch { }
    }
}

function Invoke-Refresh([string]$Tag) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $post = Invoke-Api -Method Post -Path '/api/sandbox/refresh' -SaveAs "$Tag-refresh.json"
    $status = $null
    if ($post.Code -eq 202) {
        $deadline = (Get-Date).AddSeconds($BaselineTimeoutSec)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $g = Invoke-Api -Method Get -Path '/api/sandbox'
            if ($g.Code -eq 200 -and $null -ne $g.Json) {
                $status = $g.Json
                $null = Write-Text "$Tag-sandbox.json" $g.Content
                if ($status.state -ne 'building') { break }
            }
            if ($script:HostProc.HasExited) { break }
        }
    }
    $sw.Stop()
    [pscustomobject]@{
        Ok = ($null -ne $status -and $status.state -eq 'ready')
        Post = $post.Code
        State = $(if ($status) { [string]$status.state } else { $null })
        LastError = $(if ($status) { [string]$status.lastError } else { $null })
        Status = $status
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        File = "$Tag-sandbox.json"
    }
}

# ---------- 요약 ----------

# 이름을 Md로 하면 별칭 md(mkdir)가 먼저 불린다(별칭이 함수보다 우선)
function Format-Cell([string]$Text) { if ($null -eq $Text) { '' } else { $Text.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') } }

function Write-Summary {
    foreach ($s in $script:Scenarios) { $s.verdict = $(if ($s.failures.Count -eq 0) { 'PASS' } else { 'FAIL' }) }
    $failed = @($script:Scenarios | Where-Object { $_.verdict -ne 'PASS' })
    $script:Overall = if ($script:SetupError -or $script:Scenarios.Count -eq 0 -or $failed.Count -gt 0) { 'FAIL' } else { 'PASS' }
    $doc = [ordered]@{
        overall = $script:Overall
        finishedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        input = $ExecSql
        setupError = $script:SetupError
        environment = $script:Envi
        baseline = $script:BaselineInfo
        scenarios = @($script:Scenarios)
    }
    $null = Write-Text 'summary.json' ($doc | ConvertTo-Json -Depth 8)

    $md = [System.Collections.Generic.List[string]]::new()
    $md.Add('# Split 모드 EXEC [적용] 자동 확인')
    $md.Add('')
    $md.Add("전체: **$($script:Overall)** (시나리오 $($script:Scenarios.Count)개 중 기대와 다름 $($failed.Count)개)")
    $md.Add('')
    $md.Add("- 입력: ``$ExecSql``")
    foreach ($k in @($script:Envi.Keys)) { $md.Add("- ${k}: $(Format-Cell ([string]$script:Envi[$k]))") }
    if ($script:BaselineInfo) {
        $b = $script:BaselineInfo
        $md.Add("- 기준선(첫 구축): state $($b.state), mode $($b.mode), fingerprintMatch $($b.fingerprintMatch), 경고 $(@($b.fingerprintWarnings).Count), 불일치 $(@($b.fingerprintMismatches).Count), $($b.seconds)초")
        foreach ($w in @($b.fingerprintWarnings)) { if ($w) { $md.Add("  - 경고: $(Format-Cell ([string]$w))") } }
    }
    if ($script:SetupError) {
        $md.Add('')
        $md.Add("**준비 단계 실패:** $(Format-Cell $script:SetupError)")
        foreach ($f in @('host.out.log', 'host.err.log')) {
            $p = Join-Path $OutAbs $f
            if (Test-Path -LiteralPath $p) {
                $md.Add('')
                $md.Add("$f 마지막 20줄:")
                $md.Add('```')
                foreach ($l in @(Get-Content -LiteralPath $p -Tail 20 -Encoding utf8)) { $md.Add($l) }
                $md.Add('```')
            }
        }
    }
    $md.Add('')
    if ($script:Scenarios.Count -gt 0) {
        $md.Add('| 시나리오 | 요청 | 기대 | HTTP | status | preconditionFailures | verification.mode | mismatches | 원본 전(sa) | 원본 후(sa) | 판정 |')
        $md.Add('|---|---|---|---|---|---|---|---|---|---|---|')
        foreach ($s in $script:Scenarios) {
            $cells = @(
                "$($s.id) $($s.title)", $s.request, $s.expected, [string]$s.http, [string]$s.status,
                ($s.preconditionFailures -join ', '), [string]$s.verificationMode, $(if (@($s.mismatches).Count -eq 0) { '[]' } else { $s.mismatches -join '; ' }),
                [string]$s.originBefore, [string]$s.originAfter, $s.verdict
            )
            $md.Add('| ' + (($cells | ForEach-Object { Format-Cell ([string]$_) }) -join ' | ') + ' |')
        }
    } else {
        $md.Add('시나리오를 실행하지 못했다.')
    }
    if ($failed.Count -gt 0) {
        $md.Add('')
        $md.Add('## 기대와 다른 점')
        foreach ($s in $failed) { foreach ($f in $s.failures) { $md.Add("- $($s.id): $(Format-Cell $f)") } }
    }
    $md.Add('')
    $md.Add('## 응답 메시지와 참고 값')
    foreach ($s in $script:Scenarios) {
        if ($s.message) { $md.Add("- $($s.id) message: $(Format-Cell $s.message)") }
        foreach ($n in $s.notes) { $md.Add("- $($s.id): $(Format-Cell $n)") }
    }
    $md.Add('')
    $md.Add('## 증거 파일(결과 폴더 기준)')
    foreach ($s in $script:Scenarios) { $md.Add("- $($s.id): $(($s.evidence | Select-Object -Unique) -join ', ')") }
    $md.Add('- 공통: progress.log, host.out.log, host.err.log, setup-*.log, baseline-*.json, folders-after-baseline.log, summary.json')
    [IO.File]::WriteAllLines((Join-Path $OutAbs 'summary.md'), [string[]]$md, $Utf8)
}

# ---------- 본 실행 ----------

try {
    Write-Step "결과 폴더 $(Rel $OutAbs)"

    # 1. .env: 먼저 -Split 없이 읽어 원본 값이 있는지 보고(없으면 env.ps1이 비밀번호를 묻는다), 그다음 두 연결을 만든다
    . (Join-Path $Root 'scripts/env.ps1')
    foreach ($k in @('MSSQL_SA_PASSWORD', 'ORIGIN_SQL_SERVER', 'ORIGIN_SQL_USER', 'ORIGIN_SQL_PASSWORD')) {
        if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($k))) { throw ".env에 $k 가 없음(값은 출력하지 않음)" }
    }
    if ($env:ORIGIN_SQL_USER -ne 'dbr_origin_user') { throw "ORIGIN_SQL_USER는 dbr_origin_user여야 함(제한 로그인 시나리오). 지금: $($env:ORIGIN_SQL_USER)" }
    . (Join-Path $Root 'scripts/env.ps1') -Split
    $script:Envi['commit'] = $(if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (Invoke-Native -Exe 'git' -ArgList @('rev-parse', 'HEAD')).Lines -join '' })
    $script:Envi['os'] = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    $script:Envi['pwsh'] = $PSVersionTable.PSVersion.ToString()
    $script:Envi['originConnection'] = "$($env:ORIGIN_SQL_SERVER), 로그인 $($env:ORIGIN_SQL_USER)"

    # 2. 바인드 폴더. compose가 없는 폴더를 root 소유로 만들기 전에 이 사용자로 만든다.
    $backupRel = if ($env:ORIGIN_BACKUP_DIR) { $env:ORIGIN_BACKUP_DIR } else { './.data/origin-backup' }
    $BackupDir = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($backupRel)) { $backupRel } else { Join-Path $Root $backupRel }))
    $ImportDir = [IO.Path]::GetFullPath((Join-Path $Root '.data/import'))
    $HostData = [IO.Path]::GetFullPath((Join-Path $Root '.data/split-exec-host'))
    New-Item -ItemType Directory -Force -Path $BackupDir, $ImportDir | Out-Null
    if (Test-Path -LiteralPath $HostData) { Remove-Item -LiteralPath $HostData -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $HostData | Out-Null
    if ($IsLinux) {
        # 컨테이너 mssql(uid 10001)이 원본 백업을 쓰고 샌드박스가 가져온 사본을 읽게 한다(SP17.md "실패 시 볼 곳", 오류 5)
        $r = Invoke-Native -Exe 'chmod' -ArgList @('777', $BackupDir, $ImportDir) -LogName 'setup-chmod.log'
        if ($r.Code -ne 0) { Write-Warning "chmod 실패(setup-chmod.log). 폴더가 다른 사용자 소유일 수 있음" }
        # 원본 백업 파일은 mssql 소유·제한 모드로 생길 수 있다. 기본 ACL로 이 사용자(Host)에게 읽기를 물려 가져오기 복사가 읽게 한다(추정, CI에서 확인).
        $uid = ((Invoke-Native -Exe 'id' -ArgList @('-u')).Lines -join '').Trim()
        $r = Invoke-Native -Exe 'setfacl' -ArgList @('-d', '-m', "u:${uid}:rwx", $BackupDir) -LogName 'setup-setfacl.log'
        if ($r.Code -ne 0) { Write-Warning "setfacl 실패(setup-setfacl.log). 원본 백업 파일을 Host가 못 읽으면 기준선이 실패한다" }
    }
    Write-Step "폴더 준비: 백업 $(Rel $BackupDir), 가져오기 $(Rel $ImportDir), Host 데이터 $(Rel $HostData)"

    # 3. Host 빌드(개발 모드 --dev는 Debug 빌드에서만 열린다)
    if (-not $SkipBuild) {
        Write-Step 'Host 빌드(Debug)'
        $r = Invoke-Native -Exe 'dotnet' -ArgList @('build', 'src/DbRipple.Host', '-c', 'Debug', '-nologo') -LogName 'setup-host-build.log'
        if ($r.Code -ne 0) { throw "Host 빌드 실패(setup-host-build.log)" }
    }
    $script:Envi['dotnet'] = ((Invoke-Native -Exe 'dotnet' -ArgList @('--version')).Lines -join '').Trim()

    # 4. 컨테이너: 샌드박스 sql(2025 CU10) → 원본 sql-origin(profile origin, 기본 2022-latest)
    if (-not $SkipContainerStart) {
        Write-Step '샌드박스 컨테이너 sql 기동(db-up.ps1)'
        $r = Invoke-Native -Exe $Pwsh -ArgList @('-NoProfile', '-File', (Join-Path $Root 'scripts/db-up.ps1'), '-TimeoutSec', '300') -LogName 'setup-db-up.log'
        if ($r.Code -ne 0) { throw 'sql 컨테이너가 healthy가 되지 않음(setup-db-up.log)' }
        Write-Step '원본 컨테이너 sql-origin 기동'
        $r = Invoke-Native -Exe 'docker' -ArgList @('compose', '--profile', 'origin', 'up', '-d', 'sql-origin') -LogName 'setup-origin-up.log'
        if ($r.Code -ne 0) { throw 'sql-origin 기동 실패(setup-origin-up.log, 이미지 받기 실패일 수 있음)' }
        $deadline = (Get-Date).AddSeconds(300)
        $health = ''
        while ((Get-Date) -lt $deadline) {
            $health = ((Invoke-Native -Exe 'docker' -ArgList @('inspect', '-f', '{{.State.Health.Status}}', 'dbripple-sql-origin')).Lines -join '').Trim()
            if ($health -eq 'healthy') { break }
            Start-Sleep -Seconds 5
        }
        if ($health -ne 'healthy') { throw "sql-origin이 healthy가 되지 않음(마지막 상태 $health)" }
    }

    # 5. 두 서버 버전
    $versionSql = Write-Text 'q-version.sql' @'
SET NOCOUNT ON;
SELECT CONCAT('VAL|version|', REPLACE(REPLACE(REPLACE(@@VERSION, CHAR(13), ' '), CHAR(10), ' '), CHAR(9), ' ')) AS line;
SELECT CONCAT('VAL|server|', @@SERVERNAME) AS line;
SELECT CONCAT('VAL|collation|', CONVERT(nvarchar(128), SERVERPROPERTY('Collation'))) AS line;
'@
    foreach ($svc in @('sql-origin', 'sql')) {
        $r = Invoke-ContainerSql -File $versionSql -Service $svc -LogName "setup-version-$svc.log"
        $label = if ($svc -eq 'sql-origin') { 'origin' } else { 'sandbox' }
        $script:Envi["${label}Version"] = Get-Val $r.Lines 'version'
        $script:Envi["${label}Server"] = Get-Val $r.Lines 'server'
        $script:Envi["${label}Collation"] = Get-Val $r.Lines 'collation'
    }

    # 6. 원본에 CascadeLab(SP17.md 3단계) + 제한 로그인(4단계: db_backupoperator + db_datareader + db_datawriter, EXECUTE·VIEW DEFINITION·CREATE DATABASE 없음)
    Write-Step '원본 CascadeLab 만들기(00-create-databases.sql + cl*.sql)'
    $files = @(Join-Path $Root 'cascadelab/sql/00-create-databases.sql') + @(Get-ChildItem -Path (Join-Path $Root 'cascadelab/sql') -Filter 'cl*.sql' | Sort-Object Name | ForEach-Object FullName)
    foreach ($f in $files) {
        $r = Invoke-ContainerSql -File $f -LogName ('setup-origin-' + [IO.Path]::GetFileNameWithoutExtension($f) + '.log')
        if ($r.Code -ne 0) { throw "원본 준비 실패: $(Split-Path -Leaf $f) (로그 $($r.Log))" }
    }
    Write-Step '제한 로그인 dbr_origin_user(sp17_10)'
    $r = Invoke-ContainerSql -File $LoginSql -LogName 'setup-login.log' -PasswordFromEnv 'ORIGIN_SQL_PASSWORD'
    if ($r.Code -ne 0) { throw '제한 로그인 만들기 실패(setup-login.log)' }
    # 다시 돌려도 S1이 "권한 없음"에서 시작하게 cl21 권한을 회수한다(없는 권한의 REVOKE는 아무 일도 하지 않음)
    $resetSql = Write-Text 'q-reset-perms.sql' @'
REVOKE EXECUTE ON OBJECT::cl21.usp_Outer FROM dbr_origin_user;
REVOKE EXECUTE ON OBJECT::cl21.usp_Inner FROM dbr_origin_user;
REVOKE VIEW DEFINITION ON SCHEMA::cl21 FROM dbr_origin_user;
'@
    $r = Invoke-ContainerSql -File $resetSql -Database CascadeLab -LogName 'setup-reset-perms.log'
    if ($r.Code -ne 0) { throw 'cl21 권한 회수 실패(setup-reset-perms.log)' }

    $script:StateSqlPath = Write-Text 'q-origin-state.sql' @'
SET NOCOUNT ON;
SELECT CONCAT('VAL|Parent|', id, '|', qty) AS line FROM cl21.Parent ORDER BY id;
SELECT CONCAT('VAL|Touch|', parent_id, '|', touched) AS line FROM cl21.Touch ORDER BY parent_id;
SELECT CONCAT('VAL|Log|', log_id, '|', msg) AS line FROM cl21.Log ORDER BY log_id;
SELECT CONCAT('VAL|Spare|', id, '|', qty) AS line FROM cl21.Spare ORDER BY id;
SELECT CONCAT('VAL|def|', OBJECT_SCHEMA_NAME(m.object_id), '.', OBJECT_NAME(m.object_id), '|', CONVERT(varchar(70), HASHBYTES('SHA2_256', m.definition), 2)) AS line
FROM sys.sql_modules AS m
WHERE m.object_id IN (OBJECT_ID(N'cl21.usp_Outer'), OBJECT_ID(N'cl21.usp_Inner'))
ORDER BY line;
SELECT CONCAT('VAL|perm|', p.permission_name COLLATE DATABASE_DEFAULT, ' ',
              CASE p.class WHEN 1 THEN CONCAT(OBJECT_SCHEMA_NAME(p.major_id), '.', OBJECT_NAME(p.major_id)) ELSE CONCAT('SCHEMA::', SCHEMA_NAME(p.major_id)) END) AS line
FROM sys.database_permissions AS p
WHERE p.grantee_principal_id = USER_ID(N'dbr_origin_user') AND p.state IN ('G', 'W')
  AND ((p.class = 3 AND p.major_id = SCHEMA_ID(N'cl21')) OR (p.class = 1 AND OBJECT_SCHEMA_NAME(p.major_id) = N'cl21'))
ORDER BY line;
'@

    # 7. Split Host(SP17.md 6단계). 연결 문자열은 env.ps1 -Split이 만든 DBRIPPLE_ORIGIN_CONNECTION·DBRIPPLE_SANDBOX_CONNECTION.
    $env:DbRipple__Mode = 'Split'
    $env:DbRipple__Urls = $BaseUrl
    $env:DbRipple__OriginDb = 'CascadeLab'
    $env:DbRipple__SandboxDb = 'CascadeLab_Sandbox'
    $env:DbRipple__SnapshotDb = 'CascadeLab_Sandbox_base'
    $env:DbRipple__Backup__ServerPath = '/var/opt/mssql/backup'
    $env:DbRipple__Backup__SharePath = $BackupDir
    $env:DbRipple__Backup__ImportDir = $ImportDir
    $env:DbRipple__ManifestPath = Join-Path $HostData 'manifest.json'
    $env:DbRipple__PreviewStoreDir = Join-Path $HostData 'previews'
    $env:DBRIPPLE_ENGINE = $null
    Write-Step "Host 시작($BaseUrl, 개발 모드 --dev --no-browser)"
    Start-DbRippleHost

    # 8. 기준선(SP17.md 7단계): 원본 COPY_ONLY 백업 → 복사 → 샌드박스 복원 → 감사·스냅샷
    Write-Step '기준선 구축(POST /api/sandbox/refresh)'
    $b = Invoke-Refresh 'baseline-1'
    if ($IsLinux) {
        $null = Invoke-Native -Exe 'bash' -ArgList @('-c', "ls -la -- '$BackupDir' '$ImportDir'; getfacl -p -- '$BackupDir' 2>&1 || true") -LogName 'folders-after-baseline.log'
    }
    $st = $b.Status
    $script:BaselineInfo = [ordered]@{
        state = $b.State; mode = $null; fingerprintMatch = $null; fingerprintWarnings = @(); fingerprintMismatches = @(); instanceWarnings = @()
        origin = $null; lastError = $b.LastError; seconds = $b.Seconds; file = $b.File
    }
    if ($st) {
        # 배열은 직접 대입해야 빈 배열·1개 배열이 $null·스칼라로 풀리지 않는다
        $script:BaselineInfo.mode = [string]$st.mode
        $script:BaselineInfo.fingerprintMatch = $st.fingerprintMatch
        $script:BaselineInfo.fingerprintWarnings = @($st.fingerprintWarnings | Where-Object { $_ })
        $script:BaselineInfo.fingerprintMismatches = @($st.fingerprintMismatches | Where-Object { $_ })
        $script:BaselineInfo.instanceWarnings = @($st.instanceWarnings | Where-Object { $_ })
        $script:BaselineInfo.origin = $st.origin
    }
    if (-not $b.Ok) { throw "기준선 구축 실패: refresh HTTP $($b.Post), state $($b.State), lastError $($b.LastError) ($($b.File), host.out.log, folders-after-baseline.log)" }
    if ($st.mode -ne 'split') { throw "기준선 mode가 split이 아님: $($st.mode)" }
    Write-Step "기준선 ready($($b.Seconds)초, fingerprintMatch $($st.fingerprintMatch))"

    # ---------- S0 시작 상태 ----------
    $s0 = New-Scenario 'S0' '시작 상태' '기준선 뒤 sa로 원본 cl21 값·dbr_origin_user의 cl21 권한 읽기' "원본 $StartState, cl21 권한 없음"
    try {
        $o = Read-OriginState 's0-origin'
        Set-Origin $s0 $o $o
        Test-That $s0 $o.Ok '원본 값을 sa로 읽지 못함'
        Test-That $s0 ((Format-State $o) -eq $StartState) "시작 값이 다름: $(Format-State $o)"
        Test-That $s0 ($o.Perms.Count -eq 0) "dbr_origin_user에 cl21 권한이 이미 있음: $($o.Perms -join ', ')"
        $s0.notes.Add("정의 해시(sa): $((@($o.Def.Keys) | ForEach-Object { "$_=$($o.Def[$_].Substring(0, [Math]::Min(12, $o.Def[$_].Length)))" }) -join ', ')")
    } catch { Add-Exception $s0 $_ }

    # ---------- S1 EXECUTE 없음 ----------
    $s1a = New-Scenario 'S1a' 'EXECUTE 없음, 동의 없음' '권한: 역할 3개뿐. 새 미리보기 → [적용] {sqlHash}' 'HTTP 200, precondition_failed, [WEAK_VERIFICATION_NOT_ACCEPTED](검증 모드 결정이 먼저이고 실패가 있으면 프로시저 검사를 건너뜀), KEYSET_HASH, 원본 불변'
    $s1b = New-Scenario 'S1b' 'EXECUTE 없음, 동의 있음' '같은 미리보기(막히면 소비되지 않음) → [적용] {sqlHash, allowKeysetVerification:true}' 'HTTP 200, precondition_failed, [PROC_DEFINITION_CHANGED, PROC_EXECUTE_PERMISSION_MISSING](권한이 없어 정의도 안 보임), KEYSET_HASH, 원본 불변'
    $p1 = $null
    try {
        Write-Step 'S1a'
        $before = Read-OriginState 's1-origin-before'
        Test-That $s1a ($before.Perms.Count -eq 0) "준비 조건 다름: dbr_origin_user에 cl21 권한이 있음($($before.Perms -join ', '))"
        $p1 = New-Preview $s1a 's1'
        if ($p1) {
            $null = Invoke-Apply $s1a $p1 $false 's1a'
            $after = Read-OriginState 's1a-origin-after'
            Set-Origin $s1a $before $after
            Test-Precondition $s1a @('WEAK_VERIFICATION_NOT_ACCEPTED')
            Test-Unchanged $s1a $before $after
        }
    } catch { Add-Exception $s1a $_ }
    try {
        Write-Step 'S1b'
        if (-not $p1) { throw 'S1 미리보기가 없어 실행하지 못함' }
        $before = Read-OriginState 's1b-origin-before'
        $null = Invoke-Apply $s1b $p1 $true 's1b'
        $after = Read-OriginState 's1b-origin-after'
        Set-Origin $s1b $before $after
        Test-Precondition $s1b @('PROC_DEFINITION_CHANGED', 'PROC_EXECUTE_PERMISSION_MISSING')
        Test-Unchanged $s1b $before $after
    } catch { Add-Exception $s1b $_ }

    # ---------- S2 EXECUTE만 ----------
    $s2 = New-Scenario 'S2' 'EXECUTE만(VIEW DEFINITION 없음)' 'sa: GRANT EXECUTE usp_Outer·usp_Inner. 새 미리보기 → [적용] {sqlHash, allowKeysetVerification:true}' 'HTTP 200, precondition_failed, [PROC_DEFINITION_CHANGED](정의를 못 읽음), KEYSET_HASH, 원본 불변'
    try {
        Write-Step 'S2'
        $grant = Write-Text 'q-s2-grant-execute.sql' @'
GRANT EXECUTE ON OBJECT::cl21.usp_Outer TO dbr_origin_user;
GRANT EXECUTE ON OBJECT::cl21.usp_Inner TO dbr_origin_user;
'@
        $r = Invoke-ContainerSql -File $grant -Database CascadeLab -LogName 's2-grant.log'
        $s2.evidence.Add('s2-grant.log')
        if ($r.Code -ne 0) { throw 'GRANT EXECUTE 실패(s2-grant.log)' }
        $before = Read-OriginState 's2-origin-before'
        Test-That $s2 (($before.Perms -contains 'EXECUTE cl21.usp_Outer') -and ($before.Perms -contains 'EXECUTE cl21.usp_Inner') -and -not ($before.Perms -match 'VIEW DEFINITION')) "준비 조건 다름: 권한 $($before.Perms -join ', ')"
        $p = New-Preview $s2 's2'
        if ($p) {
            $null = Invoke-Apply $s2 $p $true 's2'
            $after = Read-OriginState 's2-origin-after'
            Set-Origin $s2 $before $after
            Test-Precondition $s2 @('PROC_DEFINITION_CHANGED')
            Test-Unchanged $s2 $before $after
        }
    } catch { Add-Exception $s2 $_ }

    # ---------- S3 VIEW DEFINITION까지, 동의 없음 ----------
    $s3 = New-Scenario 'S3' 'VIEW DEFINITION까지, 동의 없음' 'sa: GRANT VIEW DEFINITION ON SCHEMA::cl21. 새 미리보기 → [적용] {sqlHash}' 'HTTP 200, precondition_failed, [WEAK_VERIFICATION_NOT_ACCEPTED](원본 로그인에 CREATE DATABASE 없음), KEYSET_HASH, 원본 불변'
    try {
        Write-Step 'S3'
        $grant = Write-Text 'q-s3-grant-view-definition.sql' @'
GRANT VIEW DEFINITION ON SCHEMA::cl21 TO dbr_origin_user;
'@
        $r = Invoke-ContainerSql -File $grant -Database CascadeLab -LogName 's3-grant.log'
        $s3.evidence.Add('s3-grant.log')
        if ($r.Code -ne 0) { throw 'GRANT VIEW DEFINITION 실패(s3-grant.log)' }
        $before = Read-OriginState 's3-origin-before'
        Test-That $s3 ($before.Perms -contains 'VIEW DEFINITION SCHEMA::cl21') "준비 조건 다름: 권한 $($before.Perms -join ', ')"
        $p = New-Preview $s3 's3'
        if ($p) {
            $null = Invoke-Apply $s3 $p $false 's3'
            $after = Read-OriginState 's3-origin-after'
            Set-Origin $s3 $before $after
            Test-Precondition $s3 @('WEAK_VERIFICATION_NOT_ACCEPTED')
            Test-Unchanged $s3 $before $after
        }
    } catch { Add-Exception $s3 $_ }

    # ---------- S4 미리보기 뒤 원본 정의 변경 ----------
    $s4 = New-Scenario 'S4' '미리보기 뒤 원본 usp_Outer ALTER' '새 미리보기 → sa: usp_Outer 본문 앞에 주석 한 줄을 더해 ALTER → [적용] {sqlHash, allowKeysetVerification:true} → 기준선 새로 고침' 'HTTP 200, precondition_failed, [PROC_DEFINITION_CHANGED], KEYSET_HASH, 원본 값 불변, 새로 고침 ready'
    try {
        Write-Step 'S4'
        $before = Read-OriginState 's4-origin-before'
        $p = New-Preview $s4 's4'
        if ($p) {
            # 정의를 sa로 읽어 CREATE를 ALTER로 바꾸고 맨 앞에 주석 한 줄을 더한다(본문 동작은 같게). 해시가 그대로면 실패로 본다.
            $alter = Write-Text 'q-s4-alter-outer.sql' @'
SET NOCOUNT ON;
DECLARE @old nvarchar(max) = (SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'cl21.usp_Outer'));
IF @old IS NULL THROW 50001, N'cl21.usp_Outer definition not readable', 1;
DECLARE @pos int = CHARINDEX(N'CREATE', @old);
IF @pos = 0 THROW 50002, N'CREATE keyword not found in cl21.usp_Outer definition', 1;
DECLARE @new nvarchar(max) = N'-- split-exec-check S4: changed after preview' + NCHAR(13) + NCHAR(10) + STUFF(@old, @pos, 6, N'ALTER');
EXEC sys.sp_executesql @new;
DECLARE @now nvarchar(max) = (SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'cl21.usp_Outer'));
SELECT CONCAT('VAL|defbefore|', CONVERT(varchar(70), HASHBYTES('SHA2_256', @old), 2)) AS line;
SELECT CONCAT('VAL|defafter|', CONVERT(varchar(70), HASHBYTES('SHA2_256', @now), 2)) AS line;
IF HASHBYTES('SHA2_256', @now) = HASHBYTES('SHA2_256', @old) THROW 50003, N'cl21.usp_Outer definition did not change', 1;
'@
            $r = Invoke-ContainerSql -File $alter -Database CascadeLab -LogName 's4-alter.log'
            $s4.evidence.Add('s4-alter.log')
            if ($r.Code -ne 0) { throw 'usp_Outer ALTER 실패(s4-alter.log)' }
            $s4.notes.Add("usp_Outer 정의 해시 $((Get-Val $r.Lines 'defbefore')) → $((Get-Val $r.Lines 'defafter'))")
            $null = Invoke-Apply $s4 $p $true 's4'
            $after = Read-OriginState 's4-origin-after'
            Set-Origin $s4 $before $after
            Test-Precondition $s4 @('PROC_DEFINITION_CHANGED')
            Test-Unchanged $s4 $before $after
        }
    } catch { Add-Exception $s4 $_ }
    try {
        # 바뀐 정의를 기준선에 반영해야 S5가 정상 경로를 탄다
        Write-Step 'S4 뒤 기준선 새로 고침'
        $rb = Invoke-Refresh 'baseline-2'
        $s4.evidence.Add($rb.File)
        $s4.notes.Add("새로 고침: HTTP $($rb.Post), state $($rb.State), $($rb.Seconds)초")
        Test-That $s4 $rb.Ok "S4 뒤 기준선 새로 고침 실패: state $($rb.State), lastError $($rb.LastError)"
    } catch { Add-Exception $s4 $_ }

    # ---------- S5 정상 커밋 ----------
    $s5 = New-Scenario 'S5' '정상 경로(동의 있음)' '원본 지문(전) → 새 미리보기 → [적용] {sqlHash, allowKeysetVerification:true} → 원본 지문(후)' 'HTTP 200, committed, KEYSET_HASH, mismatches [], 반환 값 0, 원본 Parent 1=15·Touch 1=1·Log outer 1행·Spare 그대로, ddl_ 지문 전후 같음'
    $p5 = $null
    $after5 = $null
    $fpBefore = $null
    try {
        Write-Step 'S5'
        $fpBefore = Read-Fingerprint 's5-fingerprint-before'
        $s5.evidence.Add($fpBefore.Log)
        $before = Read-OriginState 's5-origin-before'
        $p5 = New-Preview $s5 's5'
        if ($p5) {
            $r = Invoke-Apply $s5 $p5 $true 's5'
            $after5 = Read-OriginState 's5-origin-after'
            Set-Origin $s5 $before $after5
            Test-That $s5 ($s5.http -eq 200) "HTTP 기대 200, 실제 $($s5.http)"
            Test-That $s5 ($s5.status -eq 'committed') "status 기대 committed, 실제 $($s5.status)"
            Test-That $s5 ($s5.verificationMode -eq 'KEYSET_HASH') "verification.mode 기대 KEYSET_HASH, 실제 $($s5.verificationMode)"
            Test-That $s5 (@($s5.mismatches).Count -eq 0) "mismatches 기대 [], 실제 $($s5.mismatches -join '; ')"
            Test-That $s5 (@($s5.preconditionFailures).Count -eq 0) "preconditionFailures 기대 [], 실제 $($s5.preconditionFailures -join ',')"
            $stmt = if ($r.Json) { @($r.Json.statements)[0] } else { $null }
            Test-That $s5 ($null -ne $stmt -and $null -ne $stmt.returnValue -and [int]$stmt.returnValue -eq 0) "반환 값 기대 0, 실제 $(if ($stmt) { $stmt.returnValue } else { '(문장 없음)' })"
            if ($r.Json) {
                $v = $r.Json.verification
                $s5.notes.Add("statementRowCounts: $((@($v.statementRowCounts) | Where-Object { $_ } | ForEach-Object { "#$($_.index) preview=$($_.preview) actual=$($_.actual)" }) -join '; ')")
                $s5.notes.Add("tableCounts: $((@($v.tableCounts) | Where-Object { $_ } | ForEach-Object { "$($_.table) $($_.before)->$($_.after)(기대 $($_.expectedDelta))" }) -join '; ')")
                $s5.notes.Add("limitations $(@($v.limitations).Count)건(s5-apply.json), sandboxRefreshStarted $($r.Json.sandboxRefreshStarted)")
            }
            Test-That $s5 $after5.Ok '원본 값을 sa로 읽지 못함'
            Test-That $s5 ((Format-Map $after5.Parent) -eq '1=15,2=20') "Parent 기대 1=15,2=20, 실제 $(Format-Map $after5.Parent)"
            Test-That $s5 ((Format-Map $after5.Touch) -eq '1=1,2=0') "Touch 기대 1=1,2=0, 실제 $(Format-Map $after5.Touch)"
            Test-That $s5 ($after5.LogRows.Count -eq 1 -and $after5.LogRows[0] -like '*:outer') "Log 기대 'outer' 1행, 실제 $($after5.LogRows -join ',')"
            Test-That $s5 ((Format-Map $after5.Spare) -eq '100=0') "Spare 기대 100=0, 실제 $(Format-Map $after5.Spare)"
        }
        $fpAfter = Read-Fingerprint 's5-fingerprint-after'
        $s5.evidence.Add($fpAfter.Log)
        Test-That $s5 ($fpBefore.Ok -and $fpAfter.Ok -and $fpBefore.Ddl.Count -gt 0) "원본 지문을 읽지 못함(전 $($fpBefore.Ddl.Count)줄, 후 $($fpAfter.Ddl.Count)줄)"
        $onlyBefore = @($fpBefore.Ddl | Where-Object { $fpAfter.Ddl -notcontains $_ })
        $onlyAfter = @($fpAfter.Ddl | Where-Object { $fpBefore.Ddl -notcontains $_ })
        Test-That $s5 ($onlyBefore.Count -eq 0 -and $onlyAfter.Count -eq 0) "원본 ddl_ 지문이 바뀜: 전 [$($onlyBefore -join ' / ')] 후 [$($onlyAfter -join ' / ')]"
        $s5.notes.Add("ddl_ 지문 $($fpBefore.Ddl.Count)줄 비교, 다른 줄 $($onlyBefore.Count + $onlyAfter.Count)개")
        $g = Invoke-Api -Method Get -Path '/api/sandbox' -SaveAs 's5-sandbox-after.json'
        if ($g.Json) { $s5.notes.Add("적용 뒤 /api/sandbox: state $($g.Json.state), baselineStale $($g.Json.baselineStale)") }
    } catch { Add-Exception $s5 $_ }

    # ---------- S6 같은 미리보기 재사용 ----------
    $s6 = New-Scenario 'S6' '같은 미리보기로 [적용] 다시' 'S5 미리보기 id로 [적용] {sqlHash, allowKeysetVerification:true} 한 번 더' 'HTTP 404(커밋한 미리보기는 재적용 차단·저장소에서 삭제), 원본이 두 번 바뀌지 않음'
    try {
        Write-Step 'S6'
        if (-not $p5) { throw 'S5 미리보기가 없어 실행하지 못함' }
        $before = Read-OriginState 's6-origin-before'
        $null = Invoke-Apply $s6 $p5 $true 's6'
        $after = Read-OriginState 's6-origin-after'
        Set-Origin $s6 $before $after
        Test-That $s6 ($s6.http -eq 404) "HTTP 기대 404, 실제 $($s6.http)$(if ($s6.status) { ", status $($s6.status)" })"
        Test-Unchanged $s6 $before $after
        if ($after5) { Test-That $s6 ((Format-State $after) -eq (Format-State $after5)) "S5 직후 값과 다름: S5 후 [$(Format-State $after5)] 지금 [$(Format-State $after)]" }
    } catch { Add-Exception $s6 $_ }
} catch {
    $script:SetupError = $_.Exception.Message
    Write-Step "준비 단계 또는 예기치 않은 오류: $($script:SetupError)"
} finally {
    Stop-DbRippleHost
    try { Write-Summary } catch { Write-Warning "요약 쓰기 실패: $($_.Exception.Message)" }
}

$code = if ($script:SetupError -and $script:Scenarios.Count -eq 0) { 2 } elseif ($script:Overall -eq 'PASS') { 0 } else { 1 }
Write-Step "끝: $($script:Overall) (종료 코드 $code, 요약 $(Rel (Join-Path $OutAbs 'summary.md')))"
exit $code
