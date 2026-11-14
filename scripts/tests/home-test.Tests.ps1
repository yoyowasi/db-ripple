# PowerShell 5.1, no packages or DB required. Runs the real script in isolated scratch roots.
# Only process/SQL/native boundaries are replaced through the source AST; stage/summary logic stays intact.
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\logs\home-test\2026-11-14\automation'),
    [string]$HomeTestPath = (Join-Path $PSScriptRoot '..\home-test.ps1')
)
$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($HomeTestPath)
$source = [IO.File]::ReadAllText($sourcePath)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Production script parse failed' }
$utf8 = New-Object Text.UTF8Encoding($true)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $outputRoot | Out-Null
$scratch = Join-Path $outputRoot ('scratch-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $scratch | Out-Null
$checks = New-Object Collections.ArrayList
$savedEnvironment = @{}
$connectionNames = @('DBRIPPLE_SQL_CONNECTION', 'DBRIPPLE_ORIGIN_CONNECTION', 'DBRIPPLE_SANDBOX_CONNECTION')
foreach ($name in $connectionNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$savedLocation = Get-Location

function Assert-Check([string]$Name, [bool]$Passed) {
    [void]$checks.Add([pscustomobject]@{ name = $Name; passed = $Passed })
    Write-Host ($(if ($Passed) { 'PASS ' } else { 'FAIL ' }) + $Name)
}

function Invoke-Fixture([string]$Name, [string[]]$Skipped, [int]$Port = 5081) {
    $fixtureRoot = Join-Path $scratch $Name
    $scripts = Join-Path $fixtureRoot 'scripts'
    New-Item -ItemType Directory -Force $scripts,(Join-Path $fixtureRoot 'src\DbRipple.Host\bin'),(Join-Path $fixtureRoot 'cascadelab\cases') | Out-Null
    $replacements = @{
        'Invoke-Native' = @'
function Invoke-Native([string]$File, [string[]]$Arguments, [string]$OutFile) {
    $entry = [ordered]@{ kind = $Arguments[0]; single = -not [string]::IsNullOrEmpty($env:DBRIPPLE_SQL_CONNECTION); origin = -not [string]::IsNullOrEmpty($env:DBRIPPLE_ORIGIN_CONNECTION); sandbox = -not [string]::IsNullOrEmpty($env:DBRIPPLE_SANDBOX_CONNECTION) }
    [IO.File]::AppendAllText((Join-Path $outDir 'audit.jsonl'), (($entry | ConvertTo-Json -Compress) + "`r`n"))
    [IO.File]::WriteAllText($OutFile, 'fixture native output')
    if ($Arguments[0] -eq 'test') {
        $trxDir = $Arguments[$Arguments.IndexOf('--results-directory') + 1]
        New-Item -ItemType Directory -Force $trxDir | Out-Null
        [IO.File]::WriteAllText((Join-Path $trxDir 'fixture.trx'), '<TestRun><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary><Results /></TestRun>')
    } else {
        $evalDir = Join-Path (Join-Path $outDir 'eval') 'eval-quick'
        New-Item -ItemType Directory -Force $evalDir | Out-Null
        [IO.File]::WriteAllText((Join-Path $evalDir 'metrics.csv'), "system,metric,scope,value`r`npreview,M1.precision,total,1.0000")
    }
    return 0
}
'@
        'New-SqlConnection' = @'
function New-SqlConnection([string]$Database) {
    [IO.File]::AppendAllText((Join-Path $outDir 'sql-open-attempt.txt'), "fixture`r`n")
    $obj = New-Object psobject
    $obj | Add-Member ScriptMethod Open { if ($script:FixtureName -eq 'env-fail') { throw 'fixture connection failure' } }
    $obj | Add-Member ScriptMethod CreateCommand {
        $cmd = [pscustomobject]@{ CommandText = '' }
        $cmd | Add-Member ScriptMethod ExecuteScalar { return 'fixture-sql-version' }
        return $cmd
    }
    $obj | Add-Member ScriptMethod Dispose { }
    return $obj
}
'@
        'Stop-HostProcess' = 'function Stop-HostProcess($Proc) { if ($script:FixtureHost) { $script:FixtureHost.Dispose() } }'
        'Invoke-ContainerQuery' = 'function Invoke-ContainerQuery { throw ''Unexpected Docker boundary'' }'
    }
    $edits = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $replacements.ContainsKey($n.Name) }, $true))
    $copy = $source
    foreach ($node in ($edits | Sort-Object { $_.Extent.StartOffset } -Descending)) {
        $copy = $copy.Remove($node.Extent.StartOffset, $node.Extent.EndOffset - $node.Extent.StartOffset).Insert($node.Extent.StartOffset, $replacements[$node.Name])
    }
    $boundaryMocks = @'
function Start-Process {
    [IO.File]::AppendAllText((Join-Path $outDir 'host-start-attempt.txt'), "fixture`r`n")
    if ($script:FixtureName -eq 'n1-success') {
        $script:FixtureHost = New-Object HomeTestFakeHost
        $script:N1Port = $script:FixtureHost.Port
    }
    return [pscustomobject]@{ HasExited = $false; Id = -1 }
}
function git { 'fixture-commit' }
function dotnet { 'fixture-sdk' }
'@
    $copy = $copy.Replace('# ---------------------------------------------------------------- Docker', ('$script:FixtureName = ''' + $Name + "'`r`n" + $boundaryMocks + "`r`n# ---------------------------------------------------------------- Docker"))
    [IO.File]::WriteAllText((Join-Path $scripts 'home-test.ps1'), $copy, $utf8)
    $dataSource = if ($Name -eq 'env-nonlocal') { '203.0.113.1,1' } else { '127.0.0.1,14333' }
    $envFixture = '$env:DBRIPPLE_SQL_CONNECTION = ''Server=' + $dataSource + ';User ID=fixture;Password=fixture-secret-marker;Encrypt=True'''
    $envFixture += "`r`n" + '$env:DBRIPPLE_ORIGIN_CONNECTION = ''fixture-inherited-origin'''
    $envFixture += "`r`n" + '$env:DBRIPPLE_SANDBOX_CONNECTION = ''fixture-inherited-sandbox'''
    if ($Name -eq 'env-throws') { $envFixture += "`r`nthrow 'fixture env failure'" }
    [IO.File]::WriteAllText((Join-Path $scripts 'env.ps1'), $envFixture, $utf8)
    foreach ($key in $connectionNames) { [Environment]::SetEnvironmentVariable($key, 'fixture-inherited-value', 'Process') }
    & (Join-Path $scripts 'home-test.ps1') -Date 'run' -Skip $Skipped -N1Targets AW -N1Port $Port -HostTimeoutSec 5 | Out-Null
    $resultDir = Join-Path $fixtureRoot 'logs\home-test\run'
    $summary = [IO.File]::ReadAllText((Join-Path $resultDir 'summary.json')) | ConvertFrom-Json
    $auditPath = Join-Path $resultDir 'audit.jsonl'
    $audit = @(if (Test-Path $auditPath) { Get-Content $auditPath | ForEach-Object { $_ | ConvertFrom-Json } })
    return [pscustomobject]@{ summary = $summary; markdown = [IO.File]::ReadAllText((Join-Path $resultDir 'summary.md')); audit = $audit; path = $resultDir }
}

# A real pre-existing loopback HTTP server: the old script accepts it and sends refresh.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
public sealed class HomeTestFakeHost : IDisposable {
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    readonly Thread worker;
    volatile bool stopped;
    public readonly int Port;
    public int Requests, Refreshes;
    public string Error = "";
    public string LastAnalyzeBody = "", LastAnalyzeContentType = "";
    public HomeTestFakeHost() {
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        worker = new Thread(Serve); worker.IsBackground = true; worker.Start();
    }
    void Serve() {
        try {
            while (!stopped) {
                if (!listener.Pending()) { Thread.Sleep(10); continue; }
                using (var client = listener.AcceptTcpClient()) {
                    var stream = client.GetStream(); stream.ReadTimeout = 3000;
                    var rawHeader = new StringBuilder();
                    while (!rawHeader.ToString().EndsWith("\r\n\r\n")) {
                        int value = stream.ReadByte();
                        if (value < 0) throw new EndOfStreamException();
                        rawHeader.Append((char)value);
                    }
                    string[] lines = rawHeader.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string request = lines[0], contentType = "";
                    int contentLength = 0;
                    foreach (string line in lines) {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) contentLength = int.Parse(line.Substring(15).Trim());
                        if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) contentType = line.Substring(13).Trim();
                        if (line.StartsWith("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)) {
                            byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                            stream.Write(interim, 0, interim.Length); stream.Flush();
                        }
                    }
                    byte[] received = new byte[contentLength];
                    int offset = 0;
                    while (offset < received.Length) {
                        int count = stream.Read(received, offset, received.Length - offset);
                        if (count == 0) throw new EndOfStreamException();
                        offset += count;
                    }
                    if (request.Contains("/api/analyze ")) {
                        LastAnalyzeBody = new UTF8Encoding(false, true).GetString(received);
                        LastAnalyzeContentType = contentType;
                    }
                    Interlocked.Increment(ref Requests);
                    if (request.StartsWith("POST /api/sandbox/refresh ")) Interlocked.Increment(ref Refreshes);
                    string body = request.Contains("/api/analyze ") ? "{\"rejects\":[]}" : "{\"state\":\"ready\",\"baselineAt\":\"" + Guid.NewGuid().ToString("N") + "\"}";
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush();
                }
            }
        } catch (Exception ex) { if (!stopped) Error = ex.GetType().Name; }
    }
    public void Dispose() { stopped = true; worker.Join(5000); listener.Stop(); }
}
'@
$serverState = New-Object HomeTestFakeHost
try {
    $busy = Invoke-Fixture 'occupied-port' @('Docker','Build','Tests','Eval') $serverState.Port
    $n1 = $busy.summary.steps | Where-Object step -eq 'N1'
    Assert-Check 'occupied port fails N1 explicitly' ($n1.status -eq 'FAIL' -and $n1.detail -match [string]$serverState.Port)
    Assert-Check 'occupied port never starts another Host' (-not (Test-Path (Join-Path $busy.path 'host-start-attempt.txt')))
    Assert-Check 'existing HTTP Host receives no refresh or other request' ($serverState.Refreshes -eq 0 -and $serverState.Requests -eq 0)
    foreach ($mode in @('env-skip','env-fail','env-throws','env-nonlocal')) {
        $skip = @('Docker','Build'); if ($mode -eq 'env-skip') { $skip += 'Env' }
        $fixture = Invoke-Fixture $mode $skip $serverState.Port
        $tests = @($fixture.audit | Where-Object kind -eq 'test')
        Assert-Check ($mode + ': DB-free tests run with all connection variables absent') ($tests.Count -eq 1 -and -not $tests[0].single -and -not $tests[0].origin -and -not $tests[0].sandbox)
        Assert-Check ($mode + ': Eval and N1 fail before native/Host boundaries') (@($fixture.audit | Where-Object kind -eq 'run').Count -eq 0 -and -not (Test-Path (Join-Path $fixture.path 'host-start-attempt.txt')) -and @($fixture.summary.steps | Where-Object { $_.step -in @('Eval','N1') -and $_.status -eq 'FAIL' }).Count -eq 2)
        if ($mode -eq 'env-nonlocal') { Assert-Check 'nonlocal Env value rejected before SQL open' (-not (Test-Path (Join-Path $fixture.path 'sql-open-attempt.txt'))) }
    }
    $good = Invoke-Fixture 'env-success' @('Docker','Build','N1') $serverState.Port
    Assert-Check 'validated local connection reaches Tests and Eval with Split values absent' ($good.audit.Count -eq 2 -and @($good.audit | Where-Object { -not $_.single -or $_.origin -or $_.sandbox }).Count -eq 0)
    Assert-Check 'validated Env, Tests and Eval preserve successful execution' (@($good.summary.steps | Where-Object { $_.step -in @('Env','Tests','Eval') -and $_.status -ne 'PASS' }).Count -eq 0)
    $freePortListener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $freePortListener.Start(); $freePort = $freePortListener.LocalEndpoint.Port; $freePortListener.Stop()
    $n1Good = Invoke-Fixture 'n1-success' @('Docker','Build','Tests','Eval') $freePort
    Assert-Check 'free port permits the normal N1 API path' (($n1Good.summary.steps | Where-Object step -eq 'N1').status -eq 'PASS')
    Assert-Check 'completed N1 target appears in summary evidence' ($n1Good.markdown -match '완료 대상: AW' -and @($n1Good.summary.facts.n1CompletedTargets).Count -eq 1)
    $skipped = Invoke-Fixture 'all-skipped' @('Docker','Env','Build','Tests','Eval','N1') $serverState.Port
    Assert-Check 'all-skipped summary does not claim DB or Host API measurement' ($skipped.markdown -notmatch '증거 범위: DB 실측\(' -and $skipped.markdown -notmatch '임시 Host API\(')
    Assert-Check 'failed N1 summary does not claim successful Host API measurement' ($busy.markdown -notmatch '임시 Host API\(')
    $secretLeaks = @(Get-ChildItem $scratch -Filter 'summary.*' -Recurse | Where-Object { [IO.File]::ReadAllText($_.FullName) -match 'fixture-secret-marker|fixture-inherited-value' })
    Assert-Check 'summary artifacts never contain connection values' ($secretLeaks.Count -eq 0)
    Assert-Check 'fake HTTP server completes without errors' ([string]::IsNullOrEmpty($serverState.Error))
    $payloadServer = New-Object HomeTestFakeHost
    try {
        $apiFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Api' }, $true)
        & {
            $N1Port = $payloadServer.Port
            . ([scriptblock]::Create($apiFunction.Extent.Text))
            [void](Invoke-Api 'POST' '/api/analyze' @{ sql = "UPDATE dbo.[한글테이블] SET [설명] = N'서울';`r`nSELECT N'검증';" })
        }
        $decoded = $null
        try { $decoded = $payloadServer.LastAnalyzeBody | ConvertFrom-Json -ErrorAction Stop } catch { }
        Assert-Check 'Invoke-Api transmits a JSON object with sql on the HTTP wire' ($null -ne $decoded -and $decoded.PSObject.Properties.Name -contains 'sql')
        Assert-Check 'Invoke-Api preserves Korean SQL and line breaks as UTF-8' ($null -ne $decoded -and $decoded.sql -ceq "UPDATE dbo.[한글테이블] SET [설명] = N'서울';`r`nSELECT N'검증';" -and $payloadServer.LastAnalyzeContentType -match 'application/json;\s*charset=utf-8' -and [string]::IsNullOrEmpty($payloadServer.Error))
    } finally { $payloadServer.Dispose() }
    $awQuery = $ast.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-ContainerQuery' -and $node.Extent.Text -match 'AW_Origin없음' }, $true)
    $queryText = if ($awQuery) { $awQuery.CommandElements[1].Value } else { '' }
    Assert-Check 'AW existence query uses Unicode SQL literals for both Korean labels' ($queryText -cmatch "THEN N'AW_Origin없음' ELSE N'AW_Origin있음'")
} finally {
    $serverState.Dispose()
    foreach ($key in $connectionNames) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
    Set-Location $savedLocation
}
$report = [ordered]@{ powershell = $PSVersionTable.PSVersion.ToString(); scratch = $scratch; checks = @($checks); httpRequests = $serverState.Requests; refreshRequests = $serverState.Refreshes }
[IO.File]::WriteAllText((Join-Path $outputRoot 'regression-results.json'), ($report | ConvertTo-Json -Depth 5), $utf8)
$failed = @($checks | Where-Object { -not $_.passed }).Count
Write-Host ('Checks: ' + $checks.Count + ', failed: ' + $failed + ', scratch: ' + $scratch)
if ($failed) { exit 1 }
exit 0
