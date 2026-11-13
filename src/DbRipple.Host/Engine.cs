using DbRipple.Contracts;
using DbRipple.Preview;
using DbRipple.Preview.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace DbRipple.Host;

/// <summary>연결에 묶인 서비스 한 벌(설정을 바꾸면 통째로 다시 만든다). 미리보기 저장소·분석기 같은 연결 무관 서비스는 DI 싱글턴을 같이 쓴다.</summary>
sealed record Engine(RuntimeProfile Profile, ISandboxManager Sandbox, IPreviewPipeline Pipeline, ICatalogSchemaProvider Catalog, IApplyService Apply);

/// <summary>
/// 현재 서비스 묶음(N4). 시작 때 환경 변수 또는 프로필로 만들고, PUT /api/settings가 <see cref="Switch"/>로 바꾼다.
/// 설정이 없으면 연결이 필요한 API는 503(SandboxUnavailableException)이다. 진행 중인 요청은 옛 묶음으로 끝난다.
/// </summary>
sealed class EngineHolder(Func<RuntimeProfile, Engine> factory, ILogger<EngineHolder> logger) : IConnectionSwitch
{
    volatile Engine? current;

    public Engine? Current => current;

    public Engine Require() => current
        ?? throw new SandboxUnavailableException("연결 설정이 없습니다. 설정 화면에서 원본·샌드박스 연결을 저장하세요.");

    /// <summary>시작 때 한 번(재구축 필요 표시 없음).</summary>
    public void Start(RuntimeProfile profile) => current = Create(profile);

    public void EnsureIdle()
    {
        if (current?.Sandbox is SandboxManager { IsBusy: true })
            throw new PreviewConflictException(ConflictReason.Locked, "미리보기·적용·기준선 구축 중에는 연결 설정을 바꿀 수 없습니다.");
    }

    public void Switch(RuntimeProfile profile)
    {
        EnsureIdle();
        var engine = Create(profile);
        engine.Sandbox.MarkStale("연결 설정이 바뀜(기준선 재구축 필요)");
        current = engine;
    }

    Engine Create(RuntimeProfile profile)
    {
        var engine = factory(profile);
        logger.LogInformation("DbRipple 연결 {Profile}", profile); // ToString은 서버/DB만(비밀값 없음)
        foreach (var w in profile.Connections.Warnings()) logger.LogWarning("{Warning}", w);
        return engine;
    }
}

/// <summary>
/// profile.secret 보호(N4). ASP.NET Core Data Protection(공유 프레임워크, 새 패키지 없음)의 키 링을 DPAPI CurrentUser로 보호해 DataDir\keys에 둔다.
/// 같은 PC의 다른 Windows 계정은 키를 풀 수 없다. Windows가 아니면 저장하지 않는다(키가 평문으로 남지 않게).
/// </summary>
sealed class DataProtectionSecretProtector(string keysDir) : ISecretProtector
{
    readonly Lazy<IDataProtector> protector = new(() => OperatingSystem.IsWindows()
        ? Create(keysDir)
        : throw new PlatformNotSupportedException("비밀번호 저장은 Windows에서만 됩니다."));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static IDataProtector Create(string keysDir) => DataProtectionProvider.Create(new DirectoryInfo(keysDir), b =>
    {
        b.SetApplicationName("db-ripple");
        b.ProtectKeysWithDpapi(protectToLocalMachine: false);
    }).CreateProtector("db-ripple.profile.secret.v1");

    public byte[] Protect(byte[] data) => protector.Value.Protect(data);

    public byte[] Unprotect(byte[] data) => protector.Value.Unprotect(data);
}
