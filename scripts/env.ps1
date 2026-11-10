# .env 값을 현재 프로세스 환경 변수로 읽고, 호스트용 연결 문자열 DBRIPPLE_SQL_CONNECTION을 만든다. 값은 출력하지 않는다.
# 사용(점 소싱): . .\scripts\env.ps1
# .env 형식: KEY=VALUE 한 줄씩. 값 양끝의 같은 따옴표('…' 또는 "…")는 벗기고, 따옴표 없는 값에서 ' #' 뒤는 주석으로 본다(compose와 같음).
# compose가 같은 .env를 보간하므로 비밀번호에 $는 쓰지 않는다. 임시 변수는 스크립트 블록 안에만 두어 호출한 셸에 남기지 않는다.
& {
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
}
