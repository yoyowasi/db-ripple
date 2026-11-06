# 공식 AdventureWorks2025.bak을 .data\backup\에 받는다(약 48MB). 사용: .\scripts\fetch-adventureworks.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$url = 'https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorks2025.bak'
$dir = Join-Path $root '.data\backup'
$dest = Join-Path $dir 'AdventureWorks2025.bak'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
$f = Get-Item $dest
Write-Host ("파일: {0}" -f $f.FullName)
Write-Host ("크기: {0} bytes" -f $f.Length)
Write-Host ("SHA256: {0}" -f (Get-FileHash -Algorithm SHA256 $dest).Hash)
