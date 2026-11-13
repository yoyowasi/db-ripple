namespace DbRipple.Core.Baseline;

/// <summary>
/// 샌드박스 인스턴스 설정 점검(2026-11-13, 읽기만, 막지 않음). 미리보기는 사용자 SQL과 트리거를 샌드박스에서 실제로 실행하므로
/// 밖으로 닿는 기능(OS 명령, COM, 원격 질의, CLR, 외부 스크립트, 연결된 서버)이 켜져 있으면 SandboxStatus.instanceWarnings로 알린다.
/// </summary>
public static class SandboxInstanceCheck
{
    /// <summary>점검하는 sys.configurations 이름 → 짧은 설명.</summary>
    public static readonly IReadOnlyDictionary<string, string> RiskyOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["xp_cmdshell"] = "OS 명령 실행",
        ["Ole Automation Procedures"] = "COM 개체(sp_OA*)",
        ["Ad Hoc Distributed Queries"] = "OPENROWSET·OPENDATASOURCE 원격 질의",
        ["clr enabled"] = "CLR 코드",
        ["external scripts enabled"] = "외부 스크립트(R·Python)",
    };

    /// <summary>설정 값(master 문맥, 읽기만). 결과 집합 1: name, value_in_use. 결과 집합 2: 로컬(server_id 0)이 아닌 sys.servers 이름.</summary>
    public const string Sql = """
        SELECT name, CAST(value_in_use AS int)
        FROM sys.configurations
        WHERE name IN (N'xp_cmdshell', N'Ole Automation Procedures', N'Ad Hoc Distributed Queries', N'clr enabled', N'external scripts enabled');
        SELECT name FROM sys.servers WHERE server_id <> 0 ORDER BY name;
        """;

    /// <summary>읽지 못했을 때 문구.</summary>
    public const string Unknown = "샌드박스 인스턴스 설정(sys.configurations·sys.servers)을 확인 못 함";

    /// <summary>켜진 위험 설정·연결된 서버 → 경고 문구(없으면 빈 목록).</summary>
    public static IReadOnlyList<string> Warnings(IReadOnlyDictionary<string, int> options, IReadOnlyList<string> otherServers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(otherServers);
        var list = new List<string>();
        foreach (var (name, what) in RiskyOptions)
            if (options.TryGetValue(name, out var v) && v == 1)
                list.Add($"샌드박스 인스턴스에 '{name}'이(가) 켜짐({what}): 미리보기 중 트리거·SQL이 샌드박스 밖에 부작용을 낼 수 있음");
        if (otherServers.Count > 0)
            list.Add($"샌드박스 인스턴스에 연결된 서버 {otherServers.Count}개({string.Join(", ", otherServers)}): 미리보기가 원격 서버에 닿을 수 있음");
        return list;
    }
}
