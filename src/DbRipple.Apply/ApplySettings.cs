using DbRipple.Contracts;

namespace DbRipple.Apply;

/// <summary>적용 서비스 서버 설정(배포 구성, 요청 아님). Host가 appsettings DbRipple 절에서 채운다.</summary>
public sealed record ApplySettings
{
    /// <summary>원본 스냅샷 이름 접미사(§5.3 "AW_Origin_pre" = OriginDb + "_pre").</summary>
    public const string DefaultOriginSnapshotSuffix = "_pre";

    /// <summary>원본 사용자 문장 구간의 EXECUTE AS USER 대상(§5.3 그림, E19). 미리보기 ExecutionSettings.ExecuteAsUser와 같은 값. null이면 가장하지 않는다(테스트 전용).</summary>
    public string? ExecuteAsUser { get; init; }
    /// <summary>문장별 CommandTimeout 초(E20).</summary>
    public int CommandTimeoutSeconds { get; init; } = 60;
    /// <summary>스냅샷 생성·최종 차이 같은 도구 명령의 CommandTimeout 초.</summary>
    public int ToolCommandTimeoutSeconds { get; init; } = 600;
    /// <summary>원본 스냅샷 이름 접미사.</summary>
    public string OriginSnapshotSuffix { get; init; } = DefaultOriginSnapshotSuffix;
    /// <summary>미리보기 유효 시간(§5.3 선행 조건 "30분(설정값)").</summary>
    public TimeSpan PreviewExpiry { get; init; } = IPreviewStore.DefaultExpiry;
    /// <summary>스냅샷을 만들 수 있어도 KEYSET으로 검증한다(테스트·데모 전용, §5.3 대체 경로 확인용).</summary>
    public bool ForceKeyset { get; init; }
}
