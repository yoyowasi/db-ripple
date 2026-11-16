# SP19 helper regressions; no SQL, Docker daemon, or test packages required.
param([string]$ScriptPath = (Join-Path $PSScriptRoot '../../spikes/sql/sp19_00_run.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'SP19 script parse failed' }
foreach ($name in 'Read-Table', 'Get-FreeGb') {
    $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    Invoke-Expression $node.Extent.Text
}
$utf8 = New-Object Text.UTF8Encoding($false)
$isWin = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('sp19-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $scratch | Out-Null
function Assert([bool]$ok, [string]$name) { if (-not $ok) { throw "FAIL $name" }; Write-Host "PASS $name" }
try {
    $missing = Read-Table (Join-Path $scratch 'missing.log') 'round rows_ change_ms'
    Assert ($null -ne $missing -and $missing.Count -eq 0) 'missing result returns empty array'
    $result = Join-Path $scratch 'result.log'
    [IO.File]::WriteAllText($result, "round rows_ change_ms`n----- ----- ---------`n1 1000 12.5`n", $utf8)
    $rows = Read-Table $result 'round rows_ change_ms'
    Assert ($rows.Count -eq 1 -and $rows[0].Count -eq 3 -and $rows[0][2] -eq '12.5') 'single result preserves row shape'
    [IO.File]::AppendAllText($result, "2 1000 10.0`n", $utf8)
    $rows = Read-Table $result 'round rows_ change_ms'
    Assert ($rows.Count -eq 2 -and $rows[1][2] -eq '10.0') 'multiple results preserve row shape'
    $free = Get-FreeGb $scratch
    Assert ($free.Gb -gt 0 -and $free.Fs) 'real local disk capacity is readable'
    if (-not $isWin) {
        # Replace only the native Docker boundary; exercise the actual parser and fail-closed logic.
        function docker { $global:LASTEXITCODE = 0; 'Filesystem 1024-blocks Used Available Capacity Mounted'; 'fixture 2097152 1048576 1048576 50% /var/opt/mssql' }
        $free = Get-FreeGb '/var/opt/mssql' 'fixture'
        Assert ($free.Gb -eq 1 -and $free.Fs -eq 'fixture') 'container disk capacity is parsed'
        function docker { $global:LASTEXITCODE = 1 }
        $refused = $false
        try { $null = Get-FreeGb '/var/opt/mssql' 'fixture' } catch { $refused = $_.Exception.Message -like '*디스크 여유 확인 실패*' }
        Assert $refused 'unreadable container disk refuses measurement'
    }
} finally { Remove-Item $scratch -Recurse -Force }
