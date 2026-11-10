# SP2(c) — 드라이버 영향 행 수(ExecuteNonQuery 반환값)를 감사 유무별로 비교. Windows PowerShell 5.1, System.Data.SqlClient.
# 연결 문자열: 환경 변수 DBRIPPLE_SQL_CONNECTION (출력하지 않는다). 대상: SpikeLab_SP2 PART 2 설치 직후(드롭 전) 상태.
# 실행: powershell -NoProfile -File spikes\sql\sp2c_rowcount.ps1
$ErrorActionPreference = 'Stop'
$cs = $env:DBRIPPLE_SQL_CONNECTION
if ([string]::IsNullOrWhiteSpace($cs)) { Write-Error 'DBRIPPLE_SQL_CONNECTION is not set'; exit 1 }
Add-Type -AssemblyName System.Data
$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($cs)
$builder['Initial Catalog'] = 'SpikeLab_SP2'
$conn = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
$results = @()
try {
    $conn.Open()
    $tx = $conn.BeginTransaction()
    foreach ($case in @(
        @{ Name = 'Plain 3 rows';   Sql = 'UPDATE dbo.Plain SET Ref = 7 WHERE Id <= 3;' },
        @{ Name = 'Audited 3 rows'; Sql = 'UPDATE dbo.Audited SET Ref = 7 WHERE Id <= 3;' },
        @{ Name = 'Audited 0 rows'; Sql = 'UPDATE dbo.Audited SET Ref = 7 WHERE 1 = 0;' },
        @{ Name = 'Par cascade';    Sql = 'DELETE dbo.Par WHERE Id = 1;' },
        # (c 보조, 2026-11-10) 감사 트리거와 무관한 대조: 트리거 마지막 문장 5행(NOCOUNT ON), NOCOUNT 없는 트리거 +2행
        @{ Name = 'Last5 3 rows (trigger NOCOUNT ON, last stmt 5 rows)'; Sql = 'UPDATE dbo.Last5 SET Ref = 7 WHERE Id <= 3;' },
        @{ Name = 'NoCnt 3 rows (trigger without NOCOUNT, +2 rows)';    Sql = 'UPDATE dbo.NoCnt SET Ref = 7 WHERE Id <= 3;' })) {
        $cmd = $conn.CreateCommand()
        $cmd.Transaction = $tx
        $cmd.CommandText = $case.Sql
        $n = $cmd.ExecuteNonQuery()
        $results += [pscustomobject]@{ Case = $case.Name; RecordsAffected = $n }
    }
    $tx.Rollback()
}
finally {
    $conn.Dispose()
}
$results | Format-Table -AutoSize
# 판정: 'Plain 3 rows'와 'Audited 3 rows'가 같은 값(3)이면 SAME. 감사 트리거의 SET NOCOUNT ON으로 내부 INSERT 행 수가 섞이지 않아야 한다.
# 'Par cascade'는 감사 유무와 비교할 짝이 없으므로 값만 기록(연쇄·트리거 행 수가 섞이는지 관찰).
