using System.Text.RegularExpressions;
using DbRipple.Core.Sql;

namespace DbRipple.Core.Baseline;

/// <summary>RESTORE FILELISTONLY 한 행(Type: D 데이터, L 로그).</summary>
public sealed record BackupFile(string LogicalName, char Type, long FileId);

/// <summary>C8 기준선 구축 SQL(§5.1). 모두 자동 커밋에서 실행.</summary>
public static class BaselineSql
{
    public const string DataDir = "/var/opt/mssql/data/";

    public static string DropSnapshot(string snapshot) =>
        $"IF DB_ID({SqlText.NLiteral(snapshot)}) IS NOT NULL DROP DATABASE {SqlText.Quote(snapshot)};";

    public static string Backup(string source, string backupPath) =>
        $"BACKUP DATABASE {SqlText.Quote(source)} TO DISK = {SqlText.NLiteral(backupPath)} WITH COPY_ONLY, INIT;";

    public static string FileListOnly(string backupPath) =>
        $"RESTORE FILELISTONLY FROM DISK = {SqlText.NLiteral(backupPath)};";

    public static string Restore(string sandbox, string backupPath, IEnumerable<BackupFile> files)
    {
        var moves = files.Select(f => $"MOVE {SqlText.NLiteral(f.LogicalName)} TO {SqlText.NLiteral(RestorePath(sandbox, f))}");
        return $"RESTORE DATABASE {SqlText.Quote(sandbox)} FROM DISK = {SqlText.NLiteral(backupPath)} WITH {string.Join(", ", moves)}, REPLACE;";
    }

    /// <summary>/var/opt/mssql/data/&lt;db&gt;_&lt;논리명&gt;.&lt;ext&gt;</summary>
    public static string RestorePath(string db, BackupFile f) => DataDir + FilePart(db) + "_" + FilePart(f.LogicalName) + "." + f.Type switch
    {
        'L' => "ldf",
        'D' => f.FileId == 1 ? "mdf" : "ndf",
        _ => throw new NotSupportedException($"파일 형식 {f.Type}({f.LogicalName})은 지원하지 않음"),
    };

    public static string SnapshotPath(string snapshot, string logicalName) =>
        DataDir + FilePart(snapshot) + "_" + FilePart(logicalName) + ".ss";

    /// <summary>데이터 파일마다 스파스 파일을 지정한 스냅샷 생성.</summary>
    public static string CreateSnapshot(string snapshot, string sandbox, IEnumerable<string> dataLogicalNames)
    {
        var files = dataLogicalNames.Select(n => $"(NAME = {SqlText.Quote(n)}, FILENAME = {SqlText.NLiteral(SnapshotPath(snapshot, n))})").ToList();
        if (files.Count == 0) throw new ArgumentException("데이터 파일이 없음", nameof(dataLogicalNames));
        return $"CREATE DATABASE {SqlText.Quote(snapshot)} ON {string.Join(", ", files)} AS SNAPSHOT OF {SqlText.Quote(sandbox)};";
    }

    // 경로 조각에 쓸 수 없는 문자(/, .. 등)는 _로 바꾼다
    static string FilePart(string s) => Regex.Replace(s, @"[^\w\-]", "_");
}
