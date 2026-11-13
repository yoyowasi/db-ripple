using DbRipple.Core.Sql;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Baseline;

/// <summary>
/// 환경 지문(§6.2). v2(2026-11-13): 원본·샌드박스를 각자의 연결에서 읽고, 판정은 <see cref="FingerprintComparison"/>이 필드별(차단/경고)로 한다.
/// 위치 매개변수 8개는 v1 그대로이고, v2 필드는 init 속성이다(옛 JSON·옛 생성 코드와 호환, 없으면 null).
/// Language·DateFormat·DateFirst는 읽은 연결(= 같은 로그인의 실행 연결)의 세션 값이다.
/// </summary>
public sealed record Fingerprint(
    string ProductVersion,
    string Edition,
    int CompatibilityLevel,
    bool IsRecursiveTriggersOn,
    int NestedTriggers,
    int DisallowResultsFromTriggers,
    int SessionOptions,
    string Collation)
{
    /// <summary>서버 기본 정렬(SERVERPROPERTY('Collation')).</summary>
    public string? ServerCollation { get; init; }
    /// <summary>Linux 또는 Windows(@@VERSION, 공개 권한으로 읽힘).</summary>
    public string? HostPlatform { get; init; }
    /// <summary>주버전(SERVERPROPERTY('ProductMajorVersion')).</summary>
    public int? MajorVersion { get; init; }
    /// <summary>실행 연결의 @@LANGUAGE.</summary>
    public string? Language { get; init; }
    /// <summary>실행 연결의 DATEFORMAT(sys.dm_exec_sessions 자기 세션 행).</summary>
    public string? DateFormat { get; init; }
    /// <summary>실행 연결의 @@DATEFIRST.</summary>
    public int? DateFirst { get; init; }
    /// <summary>DB에 사용자 CLR 어셈블리가 있는지(볼 수 있는 범위에서).</summary>
    public bool? HasUserClrAssemblies { get; init; }

    /// <summary>master 문맥에서 database의 지문을 읽는 SQL. database는 이름 규칙을 통과해야 한다(3부분 이름에 쓴다).</summary>
    public static string SqlFor(string database)
    {
        BaselineSql.ValidateName(database, "지문 대상");
        return $"""
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   d.compatibility_level,
                   d.is_recursive_triggers_on,
                   (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'nested triggers'),
                   (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'disallow results from triggers'),
                   @@OPTIONS,
                   CAST(d.collation_name AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Collation') AS nvarchar(128)),
                   CAST(CASE WHEN @@VERSION LIKE N'%on Linux%' THEN N'Linux' ELSE N'Windows' END AS nvarchar(16)),
                   CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
                   CAST(@@LANGUAGE AS nvarchar(128)),
                   (SELECT CAST(s.date_format AS nvarchar(3)) FROM sys.dm_exec_sessions AS s WHERE s.session_id = @@SPID),
                   CAST(@@DATEFIRST AS int),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM {SqlText.Quote(database)}.sys.assemblies WHERE is_user_defined = 1) THEN 1 ELSE 0 END AS bit)
            FROM sys.databases AS d
            WHERE d.name = @db;
            """;
    }

    public static async Task<Fingerprint> ReadAsync(SqlConnection conn, string database, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand(SqlFor(database), conn);
        cmd.Parameters.Add(new SqlParameter("@db", System.Data.SqlDbType.NVarChar, 128) { Value = database });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"DB 없음: {database}");
        return new Fingerprint(r.GetString(0), r.GetString(1), r.GetByte(2), r.GetBoolean(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7))
        {
            ServerCollation = r.IsDBNull(8) ? null : r.GetString(8),
            HostPlatform = r.GetString(9),
            MajorVersion = r.IsDBNull(10) ? null : r.GetInt32(10),
            Language = r.GetString(11),
            DateFormat = r.IsDBNull(12) ? null : r.GetString(12),
            DateFirst = r.GetInt32(13),
            HasUserClrAssemblies = r.GetBoolean(14),
        };
    }
}

/// <summary>
/// 지문 판정(2026-11-13 v2). 차단 = 결과 의미가 달라지는 설정, 경고 = 버전·판·플랫폼 등.
/// SingleServer는 지금 동작 그대로 레코드 전체가 같아야 일치다(같은 인스턴스라 늘 같다). Split은 차단 항목이 모두 같으면 일치다.
/// </summary>
public sealed record FingerprintComparison(bool Match, IReadOnlyList<string> Mismatches, IReadOnlyList<string> Warnings)
{
    public static FingerprintComparison Compare(Fingerprint origin, Fingerprint sandbox, ServerMode mode)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sandbox);
        var blocking = new List<string>();
        var warnings = new List<string>();
        void Block<T>(string what, T o, T s, string? hint = null)
        {
            if (!EqualityComparer<T>.Default.Equals(o, s)) blocking.Add($"{what}: 원본 {Show(o)}, 샌드박스 {Show(s)}{hint}");
        }
        void Warn<T>(string what, T o, T s)
        {
            if (!EqualityComparer<T>.Default.Equals(o, s)) warnings.Add($"{what}: 원본 {Show(o)}, 샌드박스 {Show(s)}");
        }

        Block("호환성 수준", origin.CompatibilityLevel, sandbox.CompatibilityLevel);
        Block("DB 정렬", origin.Collation, sandbox.Collation);
        Block("서버 정렬", origin.ServerCollation, sandbox.ServerCollation, " (컨테이너는 처음 만들 때 MSSQL_COLLATION으로 정함)");
        Block("nested triggers", origin.NestedTriggers, sandbox.NestedTriggers);
        Block("disallow results from triggers", origin.DisallowResultsFromTriggers, sandbox.DisallowResultsFromTriggers);
        Block("RECURSIVE_TRIGGERS", origin.IsRecursiveTriggersOn, sandbox.IsRecursiveTriggersOn);
        var languageHint = origin.Language is { } lang ? $" (샌드박스 연결 문자열에 Current Language={lang}을 넣으면 언어·DATEFORMAT·DATEFIRST가 함께 맞는다)" : null;
        Block("언어(@@LANGUAGE)", origin.Language, sandbox.Language, languageHint);
        Block("DATEFORMAT", origin.DateFormat, sandbox.DateFormat, languageHint);
        Block("DATEFIRST", origin.DateFirst, sandbox.DateFirst, languageHint);

        Warn("ProductVersion", origin.ProductVersion, sandbox.ProductVersion);
        Warn("Edition", origin.Edition, sandbox.Edition);
        Warn("HostPlatform", origin.HostPlatform, sandbox.HostPlatform);
        Warn("주버전", origin.MajorVersion, sandbox.MajorVersion);
        Warn("세션 @@OPTIONS(실행 봉투가 다시 SET함)", origin.SessionOptions, sandbox.SessionOptions);
        if (origin.HasUserClrAssemblies == true || sandbox.HasUserClrAssemblies == true)
            warnings.Add("사용자 CLR 어셈블리가 있음: CLR 트리거·함수의 동작은 비교하지 않으며 거부 후보다(문서화한 한계)");

        var match = mode == ServerMode.SingleServer ? origin == sandbox : blocking.Count == 0;
        return new FingerprintComparison(match, blocking, warnings);
    }

    static string Show<T>(T v) => v is null ? "(모름)" : v.ToString() ?? "(모름)";
}
