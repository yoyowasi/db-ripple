# 실행 파일 폴더 게시(2026-11-13 N4): web 운영 빌드 → 검사 → Host 자체 포함 게시(win-x64, 폴더) → wwwroot 복사 → 안내 파일 → zip.
# 사용: .\scripts\publish.ps1            (결과: artifacts\db-ripple\, artifacts\db-ripple-win-x64.zip)
# npm 의존성 설치(npm ci/install)는 하지 않는다. web\node_modules가 없으면 먼저 설치하고 다시 실행한다.
# 단일 파일 게시는 하지 않는다(SqlClient 네이티브 SNI·ScriptDom 리소스 때문에 폴더 게시가 기본).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'web'
$dist = Join-Path $web 'dist'
$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts 'db-ripple'
$zip = Join-Path $artifacts 'db-ripple-win-x64.zip'

function Fail([string]$message) { Write-Host ('게시 실패: ' + $message) -ForegroundColor Red; exit 1 }
# 네이티브 명령의 stderr 출력(경고 등)이 PS 5.1에서 종료 오류가 되지 않게 실행 동안만 Continue. 성공 여부는 종료 코드로 본다.
function Invoke-Native([scriptblock]$command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command } finally { $ErrorActionPreference = $saved }
}

if (-not (Test-Path (Join-Path $web 'node_modules'))) { Fail 'web\node_modules가 없습니다. web 폴더에서 npm 설치를 먼저 하세요(이 스크립트는 설치하지 않음).' }

# 1. web 운영 빌드. 개발용 측정 화면(SP9)은 켜지 않는다.
Remove-Item Env:VITE_ENABLE_SP9 -ErrorAction SilentlyContinue
foreach ($f in Get-ChildItem $web -Filter '.env*' -File -ErrorAction SilentlyContinue) {
    if (Select-String -Path $f.FullName -Pattern 'VITE_ENABLE_SP9' -Quiet) { Fail ($f.Name + '에 VITE_ENABLE_SP9가 있습니다(운영 빌드에서 SP9를 켜지 않음).') }
}
Push-Location $web
try {
    Invoke-Native { npm.cmd run build }
    if ($LASTEXITCODE -ne 0) { Fail ('npm run build 종료 코드 ' + $LASTEXITCODE) }
} finally { Pop-Location }
if (-not (Test-Path (Join-Path $dist 'index.html'))) { Fail 'web\dist\index.html이 없습니다.' }

# 2. 빌드 검사: 소스맵 없음, 개발·시험 전용 코드 없음.
$maps = @(Get-ChildItem $dist -Recurse -File -Filter '*.map')
if ($maps.Count -gt 0) { Fail ('dist에 소스맵이 있습니다(' + $maps.Count + '개, 예: ' + $maps[0].Name + '). web\vite.config.ts build.sourcemap을 끄세요.') }
$devOnly = @(Get-ChildItem $dist -Recurse -File | Select-String -Pattern 'fakeApi|FixturePicker|FixtureNote|Sp9View|fixture=' -List)
if ($devOnly.Count -gt 0) {
    $names = ($devOnly | ForEach-Object { $_.Filename + ':' + $_.Matches[0].Value }) -join ', '
    Fail ('dist에 개발·시험 전용 문자열이 있습니다: ' + $names)
}

# 3. Host 게시(자체 포함, 폴더).
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
Invoke-Native { dotnet publish (Join-Path $root 'src\DbRipple.Host\DbRipple.Host.csproj') -c Release -r win-x64 --self-contained true -o $out -nologo }
if ($LASTEXITCODE -ne 0) { Fail ('dotnet publish 종료 코드 ' + $LASTEXITCODE) }
Get-ChildItem $out -Filter 'appsettings.Development.json' | Remove-Item -Force

# 4. 화면(wwwroot), 샌드박스 compose(가져오기 폴더 = %LOCALAPPDATA%\db-ripple\import), .env 예시.
Copy-Item $dist (Join-Path $out 'wwwroot') -Recurse
$compose = [IO.File]::ReadAllText((Join-Path $root 'compose.yaml'))
$importMount = '- ./.data/import:/var/opt/mssql/import'
if (-not $compose.Contains($importMount)) { Fail 'compose.yaml에서 가져오기 폴더 마운트를 찾지 못했습니다.' }
$compose = $compose.Replace($importMount, '- ${LOCALAPPDATA}/db-ripple/import:/var/opt/mssql/import')
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $out 'compose.yaml'), $compose, $utf8)
Copy-Item (Join-Path $root '.env.example') (Join-Path $out '.env.example')

$readme = @'
db-ripple 실행 방법
===================

준비(처음 한 번)
1. Docker Desktop을 설치하고 실행한다(샌드박스 SQL Server용). Node.js·.NET 설치는 필요 없다.
2. 이 폴더의 .env.example을 .env로 복사하고 MSSQL_SA_PASSWORD를 강한 비밀번호로 바꾼다.
   원본 서버의 정렬이 기본값과 다르면 MSSQL_COLLATION도 원본과 같게 적는다(컨테이너를 처음 만들 때만 정해짐).
3. 이 폴더에서 PowerShell을 열고 샌드박스를 띄운다:
     mkdir $env:LOCALAPPDATA\db-ripple\import -Force
     docker compose up -d
   (127.0.0.1:14333에만 열린다. 백업 가져오기 폴더는 %LOCALAPPDATA%\db-ripple\import)

실행
1. DbRipple.Host.exe를 실행한다. 콘솔 창이 열리고 기본 브라우저가 실행 주소로 열린다.
   - 실행 주소(?launch=...)는 한 번만 쓸 수 있다. 다른 창에서 열려면 콘솔에 새로 찍힌 주소를 쓴다.
   - 실행 파일이 연 창이 아니면 "실행 파일이 연 브라우저 창에서만 쓸 수 있습니다"가 나온다.
   - 5080 포트가 쓰이고 있으면 다른 빈 포트로 열고 콘솔에 주소를 알린다.
   - 브라우저를 자동으로 열지 않으려면: DbRipple.Host.exe --no-browser
2. 처음에는 화면의 설정에서 원본 서버(서버·DB·인증)와 샌드박스(127.0.0.1,14333 / sa / .env 비밀번호)를 입력하고
   [연결 시험] 후 저장한다. 비밀번호 기억을 켜면 이 Windows 계정에서만 풀 수 있게 암호화해 저장한다.
   설정 파일: %LOCALAPPDATA%\db-ripple\profile.json (비밀번호는 profile.secret에 따로)
3. 끝낼 때는 콘솔 창에서 Ctrl+C. 샌드박스를 멈추려면 이 폴더에서 docker compose stop.

권한과 주의
- 원본 로그인은 sysadmin이 아닌 본인 계정을 권한다. 원본에는 읽기, COPY_ONLY 백업, [적용]만 한다.
- 원본 서버 쪽 백업 폴더에 남는 dbr_*.bak 파일은 이 도구가 지우지 못한다. 필요 없으면 사용자나 DBA가 지운다.
- 미리보기 결과(행 이미지)는 %LOCALAPPDATA%\db-ripple\previews에 30분 동안 남는다. 민감한 데이터를 다뤘다면 지워도 된다.
- 이 도구는 이 PC(127.0.0.1)에서만 열린다. 실행 주소로 연 창만 세션 쿠키와 요청 키(화면에 들어 있는 난수)를 함께 받고,
  API는 둘 다 맞아야 응답한다. 그래서 같은 PC의 다른 계정이나 프로그램이 포트에 바로 접속하는 것,
  다른 웹사이트가 이 도구를 부르는 것, 127.0.0.1의 다른 포트에서 도는 서버가 브라우저 쿠키를 받아 재사용하는 것은 막는다.
- 막지 못하는 것: 같은 Windows 계정으로 도는 프로그램과, 이 브라우저의 쿠키·페이지를 읽을 수 있는 브라우저 확장.
  실행 주소(?launch=...)는 브라우저를 열 때 명령줄에 들어가 같은 계정의 프로그램이 볼 수 있고, 브라우저 기록에도 남을 수 있다
  (한 번 쓰면 무효가 된다). 믿을 수 없는 프로그램·확장이 있는 계정에서는 쓰지 않는다.
'@
[IO.File]::WriteAllText((Join-Path $out 'README-실행.txt'), $readme.Replace("`n", "`r`n").Replace("`r`r`n", "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

# 5. zip
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
$exe = Join-Path $out 'DbRipple.Host.exe'
if (-not (Test-Path $exe)) { Fail 'DbRipple.Host.exe가 없습니다.' }
Write-Host ('게시 완료: ' + $out)
Write-Host ('zip: ' + $zip + ' (' + [Math]::Round((Get-Item $zip).Length / 1MB, 1) + ' MB)')
