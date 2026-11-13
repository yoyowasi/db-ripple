# .env 값을 현재 프로세스 환경 변수로 읽고, 호스트용 연결 문자열 DBRIPPLE_SQL_CONNECTION(SingleServer)을 만든다. 값은 출력하지 않는다.
# 사용(점 소싱): . .\scripts\env.ps1          (SingleServer: 평가 하네스·DB 테스트, 기존 동작)
#               . .\scripts\env.ps1 -Split   (2026-11-13 Split 기본 모드: 원본 서버 + 내 PC Docker 샌드박스)
# -Split은 DBRIPPLE_ORIGIN_CONNECTION·DBRIPPLE_SANDBOX_CONNECTION도 만든다. 원본 값은 .env(커밋 안 함)에서 읽는다:
#   ORIGIN_SQL_SERVER(필수, 예: dev-sql.corp,1433), ORIGIN_SQL_USER(없으면 Windows 통합 인증), ORIGIN_SQL_PASSWORD(없으면 입력받음, 화면에 안 보임),
#   ORIGIN_SQL_TRUST_CERT(true면 TrustServerCertificate), SANDBOX_SQL_LANGUAGE(원본 로그인 언어와 맞출 때, 예: Korean → 샌드박스 Current Language).
param([switch]$Split)
# .env 형식: KEY=VALUE 한 줄씩. 값 양끝의 같은 따옴표('…' 또는 "…")는 벗기고, 따옴표 없는 값에서 ' #' 뒤는 주석으로 본다(compose와 같음).
# compose가 같은 .env를 보간하므로 비밀번호에 $는 쓰지 않는다. 임시 변수는 스크립트 블록 안에만 두어 호출한 셸에 남기지 않는다.
& {
    param([switch]$Split)
    $envFile = Join-Path (Split-Path -Parent $PSScriptRoot) '.env'
    if (-not (Test-Path $envFile)) { throw '.env가 없습니다. .env.example을 복사해 비밀번호를 채우세요.' }
    foreach ($line in [System.IO.File]::ReadAllLines($envFile)) {
        if ($line -match '^\s*(#|$)' -or $line -notmatch '=') { continue }
        $k, $v = $line -split '=', 2
        $v = $v.Trim()
        # 따옴표 값(뒤에 ' #' 주석이 붙어도 됨)은 따옴표 안만, 따옴표 없는 값은 ' #' 앞까지 쓴다
        $m = [regex]::Match($v, '^(["''])(.*)\1(\s+#.*)?$')
        if ($m.Success) { $v = $m.Groups[2].Value } else { $v = $v -replace '\s+#.*$', '' }
        [Environment]::SetEnvironmentVariable($k.Trim(), $v, 'Process')
    }
    # 비밀번호에 ; = 따옴표가 있어도 빌더가 인용한다(문자열 연결로 만들면 연결 실패와 함께 비밀번호 일부가 오류 메시지에 나올 수 있다)
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = '127.0.0.1,' + $(if ($env:MSSQL_HOST_PORT) { $env:MSSQL_HOST_PORT } else { '14333' })
    $b['User ID'] = 'sa'
    $b['Password'] = $env:MSSQL_SA_PASSWORD
    $b['Encrypt'] = $true
    $b['TrustServerCertificate'] = $true
    $b['Application Name'] = 'db-ripple'
    $env:DBRIPPLE_SQL_CONNECTION = $b.ConnectionString
    if (-not $Split) { return }

    # Split: 샌드박스 = 위 로컬 Docker(sa). 원본 언어에 맞출 때만 Current Language를 넣는다(지문 차단 항목 @@LANGUAGE·DATEFORMAT·DATEFIRST).
    if ($env:SANDBOX_SQL_LANGUAGE) { $b['Current Language'] = $env:SANDBOX_SQL_LANGUAGE }
    $env:DBRIPPLE_SANDBOX_CONNECTION = $b.ConnectionString
    if (-not $env:ORIGIN_SQL_SERVER) { throw '.env에 ORIGIN_SQL_SERVER가 없습니다(원본 서버, 예: dev-sql.corp,1433).' }
    $o = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $o['Data Source'] = $env:ORIGIN_SQL_SERVER
    $o['Encrypt'] = $true
    $o['TrustServerCertificate'] = ($env:ORIGIN_SQL_TRUST_CERT -eq 'true')
    $o['Application Name'] = 'db-ripple'
    if ($env:ORIGIN_SQL_USER) {
        $o['User ID'] = $env:ORIGIN_SQL_USER
        $pw = $env:ORIGIN_SQL_PASSWORD
        if (-not $pw) {
            $secure = Read-Host -AsSecureString ('원본 ' + $env:ORIGIN_SQL_USER + ' 비밀번호')
            $pw = [System.Net.NetworkCredential]::new('', $secure).Password
        }
        $o['Password'] = $pw
    } else {
        $o['Integrated Security'] = $true
    }
    $env:DBRIPPLE_ORIGIN_CONNECTION = $o.ConnectionString
    Write-Host ('Split 연결 준비: 원본 ' + $env:ORIGIN_SQL_SERVER + ', 샌드박스 ' + $b['Data Source'] + ' (비밀값은 출력하지 않음)')
} -Split:$Split
