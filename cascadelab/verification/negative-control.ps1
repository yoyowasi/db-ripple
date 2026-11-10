# 비교기 음성 대조: 정답 8건을 일부러 한 군데씩 틀리게 고친 사본으로 오라클을 돌려, 그 항목에서만 MISMATCH가 나는지 본다.
# 기대: CL01·CL02·CL03b·CL04·CL05·CL06·CL07·CL08a = MISMATCH(고친 항목만), CL03·CL04b·CL08b = MATCH. 도구 종료 코드 1이 정상이다.
# 사용(저장소 루트): .\cascadelab\verification\negative-control.ps1   (결과: cascadelab\verification\logs\oracle-negative-control-<날짜>.*)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$utf8 = New-Object System.Text.UTF8Encoding($false)
$dst = Join-Path ([IO.Path]::GetTempPath()) ('cascadelab-neg-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dst | Out-Null
$mut = @{
    'CL01-cascade-4.json'              = @('{ "key": { "id": 1100 }', '{ "key": { "id": 2000 }')
    'CL02-set-null.json'               = @('{ "key": { "id": 10 }, "causes": ["FK_SET_NULL"], "changedColumns": ["dept_id"], "after": { "dept_id": null } }',
                                           '{ "key": { "id": 10 }, "causes": ["FK_SET_NULL"], "changedColumns": ["dept_id"], "after": { "dept_id": 1 } }')
    'CL03b-set-default-missing.json'   = @('"errorNumber": 547', '"errorNumber": 548')
    'CL04-update-cascade-pk.json'      = @('{ "key": { "id": 102 }, "causes": ["DIRECT"] }', '{ "key": { "id": 103 }, "causes": ["DIRECT"] }')
    'CL05-two-chains.json'             = @('{ "table": "cl05.Y2"', '{ "table": "cl05.Y3"')
    'CL06-cascade-target-trigger.json' = @('"keyColumns": ["line_id"]', '"keyColumns": ["id"]')
    'CL07-trigger-chain-3.json'        = @('"changedColumns": ["cnt"]', '"changedColumns": ["cnt", "id"]')
    'CL08a-self-update-rt-off.json'    = @('"after": { "touched": 1 }', '"after": { "touched": 3 }')
}
try {
    foreach ($f in Get-ChildItem 'cascadelab\cases' -Filter *.json) {
        $t = [IO.File]::ReadAllText($f.FullName, $utf8)
        if ($mut.ContainsKey($f.Name)) {
            $p = $mut[$f.Name]
            if (-not $t.Contains($p[0])) { throw "변형 대상 문자열이 없습니다: $($f.Name)" }
            $t = $t.Replace($p[0], $p[1])
        }
        [IO.File]::WriteAllText((Join-Path $dst $f.Name), $t, $utf8)
    }
    . .\scripts\env.ps1
    $tag = 'negative-control-' + (Get-Date -Format 'yyyy-MM-dd')
    dotnet run --project cascadelab\verification\CascadeLab.Verify -c Release -- oracle --cases $dst --tag $tag |
        Select-String -Pattern '^== |\[MISMATCH\]|^summary' | ForEach-Object { $_.Line }
    "도구 종료 코드 $LASTEXITCODE (변형한 사례가 MISMATCH면 1이 정상)"
}
finally { Remove-Item -Recurse -Force $dst }
