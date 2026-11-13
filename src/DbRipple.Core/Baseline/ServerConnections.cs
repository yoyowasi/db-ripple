using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace DbRipple.Core.Baseline;

/// <summary>서버 배치(2026-11-13 결정). Split = 원본 서버 + 내 PC 샌드박스(기본), SingleServer = 한 인스턴스(평가 하네스·DB 테스트).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerMode>))]
public enum ServerMode
{
    /// <summary>원본 연결(원격, 본인 로그인) + 샌드박스 연결(로컬 Docker).</summary>
    [JsonStringEnumMemberName("split")] Split,
    /// <summary>원본과 샌드박스가 같은 인스턴스(연결 1개, 기존 동작).</summary>
    [JsonStringEnumMemberName("single_server")] SingleServer,
}

/// <summary>
/// 원본 서버 연결. 읽기, COPY_ONLY 백업, [적용]만 한다. 파괴 단계(RESTORE·DROP·SINGLE_USER·감사 설치·기준 스냅샷)는
/// <see cref="SandboxConnection"/>만 받으므로 이 타입으로는 부를 수 없다. ToString에 비밀값을 넣지 않는다.
/// </summary>
public sealed class OriginConnection
{
    public OriginConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
    }

    /// <summary>연결 문자열(비밀값 포함, 로그·메시지 금지).</summary>
    public string ConnectionString { get; }

    public override string ToString() => "origin " + ServerConnections.Describe(ConnectionString);
}

/// <summary>샌드박스 인스턴스 연결(내 PC Docker). 파괴 단계는 이 타입만 받는다. ToString에 비밀값을 넣지 않는다.</summary>
public sealed class SandboxConnection
{
    public SandboxConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
    }

    /// <summary>연결 문자열(비밀값 포함, 로그·메시지 금지).</summary>
    public string ConnectionString { get; }

    public override string ToString() => "sandbox " + ServerConnections.Describe(ConnectionString);
}

/// <summary>모드와 두 연결. SingleServer는 같은 문자열을 두 타입으로 감싼다.</summary>
public sealed record ServerConnections(ServerMode Mode, OriginConnection Origin, SandboxConnection Sandbox)
{
    public const string OriginEnvVar = "DBRIPPLE_ORIGIN_CONNECTION";
    public const string SandboxEnvVar = "DBRIPPLE_SANDBOX_CONNECTION";
    /// <summary>SingleServer 연결(기존 변수, CatalogReader.ConnectionEnvVar와 같음).</summary>
    public const string SingleEnvVar = "DBRIPPLE_SQL_CONNECTION";

    static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1", "[::1]", "(local)", ".", "(localdb)"];

    public static ServerConnections SingleServer(string connectionString)
    {
        var c = new OriginConnection(connectionString);
        return new(ServerMode.SingleServer, c, new SandboxConnection(connectionString));
    }

    public static ServerConnections Split(string origin, string sandbox) =>
        new(ServerMode.Split, new OriginConnection(origin), new SandboxConnection(sandbox));

    /// <summary>설정 값 DbRipple:Mode. 비어 있으면 Split.</summary>
    public static ServerMode ParseMode(string? value) => value?.Trim() switch
    {
        null or "" => ServerMode.Split,
        var v when v.Equals("Split", StringComparison.OrdinalIgnoreCase) => ServerMode.Split,
        var v when v.Equals("SingleServer", StringComparison.OrdinalIgnoreCase) => ServerMode.SingleServer,
        var v => throw new InvalidOperationException($"DbRipple:Mode 값 '{v}'을(를) 알 수 없음(Split 또는 SingleServer)"),
    };

    /// <summary>모드와 환경 변수로 연결을 정한다. 빠진 변수는 이름만 적은 오류(값은 넣지 않는다).</summary>
    public static ServerConnections Resolve(string? mode, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var m = ParseMode(mode);
        string? origin = Value(env(OriginEnvVar)), sandbox = Value(env(SandboxEnvVar)), single = Value(env(SingleEnvVar));
        if (m == ServerMode.SingleServer)
            return single is not null ? SingleServer(single)
                : throw new InvalidOperationException($"SingleServer 모드에는 {SingleEnvVar}가 필요함(scripts/env.ps1 점 소싱)");
        if (origin is not null && sandbox is not null) return Split(origin, sandbox);
        var present = new[] { (OriginEnvVar, origin), (SandboxEnvVar, sandbox), (SingleEnvVar, single) }
            .Where(p => p.Item2 is not null).Select(p => p.Item1).ToList();
        throw new InvalidOperationException(
            $"Split 모드(기본)에는 {OriginEnvVar}와 {SandboxEnvVar}가 모두 필요함(지금 있는 것: {(present.Count == 0 ? "없음" : string.Join(", ", present))}). " +
            $"한 인스턴스로 쓰려면 DbRipple:Mode=SingleServer와 {SingleEnvVar}를 쓸 것(scripts/env.ps1 -Split 참고)");
    }

    /// <summary>시작 때 알릴 경고(막지는 않는다). Split에서 샌드박스가 루프백이 아니면 경고.</summary>
    public IReadOnlyList<string> Warnings()
    {
        if (Mode != ServerMode.Split) return [];
        var ds = new SqlConnectionStringBuilder(Sandbox.ConnectionString).DataSource;
        return IsLoopback(ds) ? [] : [$"샌드박스 연결의 Data Source({ds})가 루프백이 아님: 샌드박스는 내 PC의 Docker SQL Server여야 한다(복원·DROP을 하는 연결)"];
    }

    /// <summary>Data Source가 이 PC를 가리키는지(tcp:·np:·lpc: 접두사, 포트(,), 인스턴스 이름(\)은 떼고 본다).</summary>
    public static bool IsLoopback(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource)) return false;
        var s = dataSource.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        var comma = s.LastIndexOf(',');
        if (comma >= 0) s = s[..comma];
        var slash = s.IndexOf('\\');
        if (slash >= 0) s = s[..slash];
        return LoopbackHosts.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>비밀값 없는 표시("서버/DB").</summary>
    public static string Describe(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return $"{b.DataSource}/{(string.IsNullOrEmpty(b.InitialCatalog) ? "-" : b.InitialCatalog)}";
        }
        catch (ArgumentException)
        {
            return "(연결 문자열 형식 오류)";
        }
    }

    public override string ToString() => $"{Mode}: {Origin} → {Sandbox}";

    static string? Value(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
