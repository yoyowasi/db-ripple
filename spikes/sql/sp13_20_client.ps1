# SP13 클라이언트 쪽 측정: EXEC의 반환값·결과 집합 수·드라이버 영향 행 수를 두 가지 호출 방식으로 잰다.
#   A) 래퍼 배치: "DECLARE @__rc int; EXEC @__rc = dbo.<프로시저>; SELECT @__rc AS rc;" (제품이 EXEC 문장을 감싸 반환값을 마지막 결과 집합으로 받는 방식)
#   B) 원문 배치: "EXEC dbo.<프로시저>;" (반환값을 못 받고 결과 집합·영향 행 수만 본다)
#   C) 저장 프로시저 호출: CommandType.StoredProcedure + ReturnValue 매개변수(리더를 모두 읽고 닫은 뒤에야 값이 채워진다)
# 사전: sp13_10_setup.sql 실행(SpikeLab_SP13). 사용(저장소 루트, 같은 PowerShell): . .\scripts\env.ps1; .\spikes\sql\sp13_20_client.ps1 [-Database SpikeLab_SP13] [-Csv spikes\logs\sp13-client.csv]
# 연결 문자열은 환경 변수 DBRIPPLE_SQL_CONNECTION(env.ps1)에서만 읽고 출력하지 않는다. 모든 실행은 XACT_ABORT ON + BEGIN TRANSACTION 안에서 하고 끝에 ROLLBACK한다.
param([string]$Database = 'SpikeLab_SP13', [string]$Csv)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($env:DBRIPPLE_SQL_CONNECTION)) { throw 'DBRIPPLE_SQL_CONNECTION이 없습니다. 먼저 . .\scripts\env.ps1 을 실행하세요.' }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "DB 이름 형식 오류: $Database" }
$b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($env:DBRIPPLE_SQL_CONNECTION)
$b['Initial Catalog'] = $Database
$connectionString = $b.ConnectionString

function Invoke-Case([string]$Proc, [string]$Mode, [bool]$XactAbort) {
    $conn = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $row = [ordered]@{ proc = $Proc; mode = $Mode; xactAbort = $XactAbort; resultSets = $null; rowsInSets = $null; recordsAffected = $null; returnValue = $null; lastSetValue = $null; errNo = $null; errCount = $null }
    try {
        $conn.Open()
        $setup = $conn.CreateCommand()
        $setup.CommandText = 'SET XACT_ABORT ' + $(if ($XactAbort) { 'ON' } else { 'OFF' }) + '; BEGIN TRANSACTION;'
        [void]$setup.ExecuteNonQuery()

        $cmd = $conn.CreateCommand()
        $rv = $null
        switch ($Mode) {
            'wrapper' { $cmd.CommandText = "DECLARE @__rc int; EXEC @__rc = dbo.$Proc; SELECT @__rc AS rc;" }
            'raw'     { $cmd.CommandText = "EXEC dbo.$Proc;" }
            'storedproc' {
                $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
                $cmd.CommandText = "dbo.$Proc"
                $rv = $cmd.Parameters.Add('@RETURN_VALUE', [System.Data.SqlDbType]::Int)
                $rv.Direction = [System.Data.ParameterDirection]::ReturnValue
            }
        }
        $sets = 0; $rowsTotal = 0; $last = $null
        $reader = $null
        try {
            $reader = $cmd.ExecuteReader()
            do {
                $sets++
                while ($reader.Read()) { $rowsTotal++; if ($reader.FieldCount -ge 1) { $last = $reader.GetValue(0) } }
            } while ($reader.NextResult())
            $row.recordsAffected = $reader.RecordsAffected   # 리더가 열려 있는 동안 읽는다(-1이면 DML 영향 행 수가 없다는 뜻)
        } catch [System.Data.SqlClient.SqlException] {
            $row.errNo = $_.Exception.Number
            $row.errCount = $_.Exception.Errors.Count
        } finally {
            if ($reader -and -not $reader.IsClosed) { $reader.Close() }
        }
        $row.resultSets = $sets
        $row.rowsInSets = $rowsTotal
        if ($Mode -eq 'wrapper') { $row.lastSetValue = $last }
        if ($rv) { $row.returnValue = if ($rv.Value -is [DBNull]) { 'DBNull' } else { $rv.Value } }   # 리더를 닫은 뒤에야 채워진다
        $end = $conn.CreateCommand()
        $end.CommandText = 'IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;'
        [void]$end.ExecuteNonQuery()
    } finally {
        $conn.Dispose()
    }
    return [pscustomobject]$row
}

$procs = 'p_ret5', 'p_noret', 'p_two_resultsets', 'p_nocount_on', 'p_raiserror_continue', 'p_throw'
$results = foreach ($p in $procs) {
    foreach ($mode in 'wrapper', 'raw', 'storedproc') {
        Invoke-Case $p $mode $true
        Invoke-Case $p $mode $false
    }
}
$results | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if ($Csv) {
    New-Item -ItemType Directory -Force (Split-Path -Parent (Join-Path (Get-Location) $Csv)) | Out-Null
    $results | Export-Csv -Path $Csv -NoTypeInformation -Encoding UTF8
    Write-Host ('CSV: ' + $Csv)
}
