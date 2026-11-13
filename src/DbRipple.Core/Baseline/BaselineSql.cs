using System.Text.RegularExpressions;
using DbRipple.Core.Capture;
using DbRipple.Core.Sql;

namespace DbRipple.Core.Baseline;

/// <summary>RESTORE FILELISTONLY 한 행(Type: D 데이터, L 로그, S FILESTREAM·메모리 최적화, F 전체 텍스트). FileGroupName은 로그면 null.</summary>
public sealed record BackupFile(string LogicalName, char Type, long FileId, string? FileGroupName = null);

/// <summary>Split 복원 경로 정보. 데이터·로그 폴더는 로컬 SERVERPROPERTY('InstanceDefaultDataPath'/'InstanceDefaultLogPath'),
/// SpecialFileGroups는 원본 sys.filegroups의 FD(FILESTREAM)·FX(메모리 최적화) 이름 → 형식.</summary>
public sealed record RestoreLayout(string DataDir, string LogDir, IReadOnlyDictionary<string, string>? SpecialFileGroups = null);

/// <summary>C8 기준선 구축 SQL(§5.1). 모두 자동 커밋에서 실행.</summary>
public static class BaselineSql
{
    public const string DataDir = "/var/opt/mssql/data/";

    // \z: $는 끝 줄바꿈 앞에서도 맞으므로 쓰지 않는다
    static readonly Regex DatabaseName = new(@"^[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// DB 이름 규칙(scripts/run-sql.ps1과 같음): 영문자·숫자·밑줄만. 서버 정렬 규칙은 끝 공백·전각 문자·대소문자를 같은 이름으로
    /// 보므로(DB_ID(N'AW_Origin ') = 원본) 그 밖의 문자를 받으면 다른 DB를 가리킬 수 있다.
    /// </summary>
    public static void ValidateName(string name, string role)
    {
        if (name is null || !DatabaseName.IsMatch(name))
            throw new ArgumentException($"{role} DB 이름 '{name}'은(는) 영문자·숫자·밑줄만 쓸 수 있음");
    }

    /// <summary>
    /// 1단계(스냅샷 삭제) 전 서버 사전 검사. 원본: 있고, 이름이 정확히 같고(별칭 거부), ONLINE 사용자 DB. 샌드박스: 복원 대상 가드 + 원본과 다른 DB.
    /// 스냅샷: 있으면 이름이 정확히 같고 샌드박스의 스냅샷. 어기면 아무것도 바꾸기 전에 THROW. SingleServer용(= SourcePrecheck + SandboxPrecheck).
    /// </summary>
    public static string Precheck(string source, string sandbox, string snapshot) =>
        SourcePrecheck(source) + "\n" + SandboxPrecheck(sandbox, snapshot, source);

    /// <summary>원본 사전 검사(읽기만). Split에서는 원본 연결의 master에서 실행한다.</summary>
    public static string SourcePrecheck(string source)
    {
        var s = SqlText.NLiteral(source);
        return $"""
            IF DB_ID({s}) IS NULL
                THROW 50010, {SqlText.NLiteral($"원본 DB {source}가 없음")}, 1;
            IF {ResolvesToOtherName(s)}
                THROW 50011, {SqlText.NLiteral($"원본 이름 {source}를 서버가 다른 이름의 DB로 해석함(정확한 이름을 쓸 것)")}, 1;
            IF DB_ID({s}) <= 4
               OR NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID({s}) AND source_database_id IS NULL AND state_desc = N'ONLINE')
                THROW 50012, {SqlText.NLiteral($"원본 {source}는 ONLINE 상태의 사용자 DB여야 함(시스템 DB·스냅샷 불가)")}, 1;
            """;
    }

    /// <summary>샌드박스 쪽 사전 검사: 복원 대상 가드 + 스냅샷 이름 검사. source는 같은 인스턴스(SingleServer)일 때만 넘긴다.</summary>
    public static string SandboxPrecheck(string sandbox, string snapshot, string? source = null)
    {
        string b = SqlText.NLiteral(sandbox), p = SqlText.NLiteral(snapshot);
        return $"""
            {RestoreTargetGuard(sandbox, source)}
            IF DB_ID({p}) IS NOT NULL
               AND ({ResolvesToOtherName(p)}
                    OR NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID({p}) AND source_database_id = DB_ID({b})))
                THROW 50014, {SqlText.NLiteral($"스냅샷 이름 {snapshot}의 DB가 {sandbox}의 스냅샷이 아니거나 서버가 다른 이름으로 해석함")}, 1;
            """;
    }

    /// <summary>
    /// 복원 대상 가드(기준선 샌드박스, dbripple restore 공용). 대상이 이미 있으면 시스템 DB·스냅샷·별칭(서버가 다른 이름으로 해석)이 아니어야 하고,
    /// source가 있으면 그 DB와도 달라야 한다.
    /// </summary>
    public static string RestoreTargetGuard(string db, string? source = null)
    {
        var n = SqlText.NLiteral(db);
        var notSource = source is null ? "" : $" OR DB_ID({n}) = DB_ID({SqlText.NLiteral(source)})";
        var what = source is null ? "시스템 DB·스냅샷" : $"시스템 DB·스냅샷·원본({source})";
        return $"""
            IF DB_ID({n}) IS NOT NULL
               AND (DB_ID({n}) <= 4
                    OR {ResolvesToOtherName(n)}
                    OR EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID({n}) AND source_database_id IS NOT NULL){notSource})
                THROW 50013, {SqlText.NLiteral($"복원 대상 {db}가 {what}이거나 서버가 다른 이름의 DB로 해석함")}, 1;
            """;
    }

    /// <summary>
    /// 이미 있는 DB를 서버가 다른 이름으로 해석하는지(별칭). 대소문자·전각은 BIN2 비교로 잡지만, 끝 공백은 BIN2 비교에서도 무시되므로
    /// (실측: DB_NAME(DB_ID(N'AW_Origin ')) COLLATE Latin1_General_BIN2 = N'AW_Origin ') DATALENGTH도 비교한다. nameLiteral은 N'...' 리터럴.
    /// </summary>
    static string ResolvesToOtherName(string nameLiteral) =>
        $"(DB_NAME(DB_ID({nameLiteral})) COLLATE Latin1_General_BIN2 <> {nameLiteral} OR DATALENGTH(DB_NAME(DB_ID({nameLiteral}))) <> DATALENGTH({nameLiteral}))";

    /// <summary>원본에 감사 저장소 스키마가 이미 있으면(감사 저장소가 깔린 DB이거나 사용자 스키마 이름 충돌) 아무것도 바꾸기 전에 멈춘다. 원본 DB 문맥에서 실행(읽기만).
    /// 대소문자 구분 DB의 'Audit' 스키마도 잡도록 정렬을 지정해 비교한다(설치 뒤 생성기 판정과 같은 기준).</summary>
    public static string SourceGuard(string source) => $"""
        IF EXISTS (SELECT 1 FROM sys.schemas WHERE name COLLATE Latin1_General_CI_AS = {SqlText.NLiteral(AuditTriggerGenerator.StoreSchema)})
            THROW 50030, {SqlText.NLiteral($"원본 {source}에 이미 {AuditTriggerGenerator.StoreSchema} 스키마가 있음: 감사 저장소 스키마(§6.2)와 겹치므로 기준선을 만들지 않음")}, 1;
        """;

    /// <summary>기존 기준 스냅샷 삭제. 그 이름이 샌드박스의 스냅샷이 아니면(일반 DB 등) 지우지 않고 오류를 낸다(사전 검사와 이중 확인).</summary>
    public static string DropSnapshot(string snapshot, string sandbox) => $"""
        IF DB_ID({SqlText.NLiteral(snapshot)}) IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = {SqlText.NLiteral(snapshot)} AND source_database_id = DB_ID({SqlText.NLiteral(sandbox)}))
                THROW 50001, {SqlText.NLiteral($"{snapshot}은(는) {sandbox}의 스냅샷이 아니므로 지우지 않음")}, 1;
            DROP DATABASE {SqlText.Quote(snapshot)};
        END;
        """;

    /// <summary>사용자 테이블별 행 수(sys.dm_db_partition_stats, 힙·클러스터형만). 설치 전후 비교용.</summary>
    public const string RowCounts = """
        SELECT s.name AS schema_name, t.name AS table_name, SUM(p.row_count) AS row_count
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        JOIN sys.dm_db_partition_stats AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
        WHERE t.is_ms_shipped = 0
        GROUP BY s.name, t.name;
        """;

    public static string Backup(string source, string backupPath) =>
        $"BACKUP DATABASE {SqlText.Quote(source)} TO DISK = {SqlText.NLiteral(backupPath)} WITH COPY_ONLY, INIT;";

    /// <summary>
    /// RESTORE … REPLACE 준비(복원 대상 가드를 통과한 뒤에만 실행). ONLINE이면 다른 연결을 끊고(SINGLE_USER, 복원 뒤 접근 모드는 백업 값),
    /// RESTORING 등 비정상 상태면 ALTER가 5052로 실패하므로 지우고 새로 복원한다.
    /// </summary>
    public static string PrepareRestoreTarget(string db) => $"""
        IF DB_ID({SqlText.NLiteral(db)}) IS NOT NULL
        BEGIN
            IF (SELECT state_desc FROM sys.databases WHERE database_id = DB_ID({SqlText.NLiteral(db)})) <> N'ONLINE'
                DROP DATABASE {SqlText.Quote(db)};
            ELSE
                ALTER DATABASE {SqlText.Quote(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        END;
        """;

    /// <summary>RESTORE가 실패해 대상이 ONLINE·SINGLE_USER로 남았으면 MULTI_USER로 되돌린다(5094·3101·디스크 부족 등).</summary>
    public static string AfterRestoreFailure(string db) => $"""
        IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID({SqlText.NLiteral(db)}) AND state_desc = N'ONLINE' AND user_access_desc = N'SINGLE_USER')
            ALTER DATABASE {SqlText.Quote(db)} SET MULTI_USER WITH ROLLBACK IMMEDIATE;
        """;

    public static string FileListOnly(string backupPath) =>
        $"RESTORE FILELISTONLY FROM DISK = {SqlText.NLiteral(backupPath)};";

    public static string Restore(string sandbox, string backupPath, IEnumerable<BackupFile> files) =>
        Restore(sandbox, backupPath, files, new RestoreLayout(DataDir, DataDir));

    /// <summary>Split 복원: 데이터·로그 폴더를 로컬 인스턴스 기본 경로로 지정한다(SingleServer는 DataDir 고정 그대로).</summary>
    public static string Restore(string sandbox, string backupPath, IEnumerable<BackupFile> files, RestoreLayout layout)
    {
        var moves = files.Select(f => $"MOVE {SqlText.NLiteral(f.LogicalName)} TO {SqlText.NLiteral(RestorePath(sandbox, f, layout))}");
        return $"RESTORE DATABASE {SqlText.Quote(sandbox)} FROM DISK = {SqlText.NLiteral(backupPath)} WITH {string.Join(", ", moves)}, REPLACE;";
    }

    /// <summary>/var/opt/mssql/data/&lt;db&gt;_&lt;논리명&gt;.&lt;ext&gt;. 데이터(D)·로그(L) 파일만 지원한다.</summary>
    public static string RestorePath(string db, BackupFile f) => RestorePath(db, f, new RestoreLayout(DataDir, DataDir));

    public static string RestorePath(string db, BackupFile f, RestoreLayout layout)
    {
        var name = FilePart(db) + "_" + FilePart(f.LogicalName) + ".";
        return f.Type switch
        {
            'L' => ServerPaths.Join(layout.LogDir, name + "ldf"),
            'D' => ServerPaths.Join(layout.DataDir, name + (f.FileId == 1 ? "mdf" : "ndf")),
            _ => throw new NotSupportedException(
                $"백업 파일 {f.LogicalName}의 형식 {f.Type}({FileTypeName(f, layout)})은 지원하지 않음: 기준선은 데이터(D)·로그(L) 파일만 복원한다. " +
                "이런 파일이 있는 DB는 기준선을 만들 수 없음(" + Why(f, layout) + ")"),
        };
    }

    static string FileTypeName(BackupFile f, RestoreLayout layout) => f.Type switch
    {
        'S' when Special(f, layout) == "FD" => "FILESTREAM 파일 그룹 컨테이너",
        'S' when Special(f, layout) == "FX" => "메모리 최적화 파일 그룹 컨테이너",
        'S' => "FILESTREAM 또는 메모리 최적화 파일 그룹 컨테이너",
        'F' => "전체 텍스트 카탈로그",
        _ => "알 수 없는 형식",
    };

    static string Why(BackupFile f, RestoreLayout layout) => Special(f, layout) switch
    {
        "FD" => "FILESTREAM은 미지원: 데이터베이스 스냅샷에서 FILESTREAM 파일 그룹이 오프라인이 되어 기준 스냅샷 비교가 성립하지 않음",
        "FX" => "메모리 최적화 파일 그룹은 미지원: 데이터베이스 스냅샷을 만들 수 없어 기준 스냅샷 단계가 실패함",
        _ => "대안 없음, 지원하려면 C8에 파일 형식별 복원 경로를 추가해야 함",
    };

    static string? Special(BackupFile f, RestoreLayout layout) =>
        f.FileGroupName is { } g && layout.SpecialFileGroups is { } map && map.TryGetValue(g, out var t) ? t : null;

    public static string SnapshotPath(string snapshot, string logicalName) =>
        DataDir + FilePart(snapshot) + "_" + FilePart(logicalName) + ".ss";

    /// <summary>스냅샷 스파스 파일 경로(지정 폴더, 서버 쪽 경로 구분자를 따른다).</summary>
    public static string SnapshotPath(string snapshot, string logicalName, string dir) =>
        ServerPaths.Join(dir, FilePart(snapshot) + "_" + FilePart(logicalName) + ".ss");

    /// <summary>데이터 파일마다 스파스 파일을 지정한 스냅샷 생성.</summary>
    public static string CreateSnapshot(string snapshot, string sandbox, IEnumerable<string> dataLogicalNames) =>
        CreateSnapshot(snapshot, sandbox, dataLogicalNames, n => SnapshotPath(snapshot, n));

    /// <summary>스파스 파일 경로를 논리 이름별로 정하는 스냅샷 생성(Split 기준 스냅샷, [적용] 원본 스냅샷).</summary>
    public static string CreateSnapshot(string snapshot, string source, IEnumerable<string> dataLogicalNames, Func<string, string> pathOf)
    {
        var files = dataLogicalNames.Select(n => $"(NAME = {SqlText.Quote(n)}, FILENAME = {SqlText.NLiteral(pathOf(n))})").ToList();
        if (files.Count == 0) throw new ArgumentException("데이터 파일이 없음", nameof(dataLogicalNames));
        return $"CREATE DATABASE {SqlText.Quote(snapshot)} ON {string.Join(", ", files)} AS SNAPSHOT OF {SqlText.Quote(source)};";
    }

    // ---- Split(2026-11-13): 원본 COPY_ONLY 백업, 로컬 HEADERONLY·복원 뒤 정리 ----

    /// <summary>원본 서버에서 하는 유일한 쓰기 성격 명령(백업 파일 생성). 원본 DB 자체는 바꾸지 않는다.</summary>
    public static string SplitBackup(string source, string serverPath) =>
        $"BACKUP DATABASE {SqlText.Quote(source)} TO DISK = {SqlText.NLiteral(serverPath)} WITH COPY_ONLY, COMPRESSION, CHECKSUM, INIT;";

    public static string HeaderOnly(string backupPath) =>
        $"RESTORE HEADERONLY FROM DISK = {SqlText.NLiteral(backupPath)};";

    /// <summary>로컬 인스턴스 정보: 이름·기계 이름·주버전·기본 데이터·로그 경로(SERVERPROPERTY는 모든 사용자가 읽는다).</summary>
    public const string LocalServerInfo = """
        SELECT CAST(@@SERVERNAME AS nvarchar(128)), CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
               CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260)), CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(260));
        """;

    /// <summary>원본 쪽 인스턴스 이름(읽기만).</summary>
    public const string InstanceIdentity = "SELECT CAST(@@SERVERNAME AS nvarchar(128)), CAST(SERVERPROPERTY('MachineName') AS nvarchar(128));";

    /// <summary>로컬 master의 인스턴스 표식(DB 수준 확장 속성) 이름. 원본에는 쓰지 않는다.</summary>
    public const string InstanceMarkerName = "dbripple_instance";

    /// <summary>표식 읽기(master 문맥). 없거나 안 보이면 행 없음.</summary>
    public const string ReadInstanceMarker =
        "SELECT CAST(value AS nvarchar(128)) FROM sys.extended_properties WHERE class = 0 AND name = N'" + InstanceMarkerName + "';";

    /// <summary>표식이 없으면 만들고 값을 돌려준다(로컬 master 문맥에서만).</summary>
    public static string EnsureInstanceMarker(Guid value) => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = N'{InstanceMarkerName}')
            EXEC sys.sp_addextendedproperty @name = N'{InstanceMarkerName}', @value = N'{value:D}';
        {ReadInstanceMarker}
        """;

    /// <summary>원본의 FILESTREAM(FD)·메모리 최적화(FX) 파일 그룹(원본 DB 문맥, 읽기만).</summary>
    public const string SpecialFileGroups = "SELECT name, type FROM sys.filegroups WHERE type IN (N'FD', N'FX');";

    /// <summary>
    /// 복원 뒤 로컬 정리: DB 소유자를 로컬 sa(SID 0x01)로(원본 로그인 SID는 로컬에 없다), 서버 옵션 nested triggers·disallow results from triggers를 원본 값에 맞춘다(로컬 인스턴스만).
    /// </summary>
    public static string AfterRestoreLocal(string sandbox, int nestedTriggers, int disallowResults)
    {
        if (nestedTriggers is not (0 or 1) || disallowResults is not (0 or 1))
            throw new ArgumentException("nested triggers·disallow results from triggers 값은 0 또는 1");
        return $"""
            DECLARE @sa sysname = SUSER_SNAME(0x01);
            DECLARE @sql nvarchar(max) = N'ALTER AUTHORIZATION ON DATABASE::' + QUOTENAME({SqlText.NLiteral(sandbox)}) + N' TO ' + QUOTENAME(@sa) + N';';
            EXEC sys.sp_executesql @sql;
            IF (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'nested triggers') <> {nestedTriggers}
            BEGIN
                EXEC sys.sp_configure N'nested triggers', {nestedTriggers};
                RECONFIGURE;
            END;
            IF (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'disallow results from triggers') <> {disallowResults}
            BEGIN
                EXEC sys.sp_configure N'show advanced options', 1;
                RECONFIGURE;
                EXEC sys.sp_configure N'disallow results from triggers', {disallowResults};
                RECONFIGURE;
            END;
            """;
    }

    // 경로 조각에 쓸 수 없는 문자(/, .. 등)는 _로 바꾼다
    static string FilePart(string s) => Regex.Replace(s, @"[^\w\-]", "_");
}
