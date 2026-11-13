using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Contracts;

/// <summary>
/// 현재 기준선 한 벌의 읽기 전용 문맥(§5.1 결과). 미리보기 한 건 동안 바뀌지 않는다. C6·C10·C11·C13이 함께 받는다.
/// 설계 §4의 개별 인자(catalog, manifest)를 한 묶음으로 넘기기 위한 계약 타입이다(설계 외 묶음, 필드는 모두 설계 항목).
/// </summary>
public sealed record BaselineContext
{
    /// <summary>원본 역할 DB(§3.2 AW_Origin, PreviewResult.baseline.originDb).</summary>
    public required string OriginDb { get; init; }
    /// <summary>샌드박스 DB(AW_Sandbox).</summary>
    public required string SandboxDb { get; init; }
    /// <summary>기준 스냅샷 DB(AW_Sandbox_base).</summary>
    public required string SnapshotDb { get; init; }
    /// <summary>기준 시각 = 백업 완료 시각(§5.1 8).</summary>
    public required DateTimeOffset BackupFinishedAt { get; init; }
    /// <summary>샌드박스 환경 지문(§6.2, Core Fingerprint).</summary>
    public required Fingerprint Fingerprint { get; init; }
    /// <summary>원본과 샌드박스 지문 일치(§5.1 6). false면 미리보기·[적용]을 막는다.</summary>
    public required bool FingerprintMatch { get; init; }
    /// <summary>샌드박스 카탈로그(C7, Core DbCatalog). 감사 저장소(audit.*)와 감사 트리거(dbr_audit_*)도 들어 있으니 쓰는 쪽이 거른다.</summary>
    public required DbCatalog Catalog { get; init; }
    /// <summary>트리거 본문 정적 분석(C6 ITriggerAnalyzer 결과).</summary>
    public required IReadOnlyList<TriggerStaticInfo> Triggers { get; init; }
    /// <summary>수집 범위 매니페스트(§6.2, Core CaptureManifest). 기준선 구축 때 쓴 manifest.json을 읽는다(샌드박스 카탈로그로 다시 만들면 감사 트리거가 Last 점유로 보여 틀린다).</summary>
    public required CaptureManifest Manifest { get; init; }
    /// <summary>서버 배치(2026-11-13). 기본 SingleServer(기존 동작).</summary>
    public ServerMode Mode { get; init; } = ServerMode.SingleServer;
    /// <summary>Split: 원본 @@SERVERNAME(비밀값 아님). SingleServer는 null.</summary>
    public string? OriginServer { get; init; }
    /// <summary>원본 연결로 읽은 지문(Split). SingleServer는 null.</summary>
    public Fingerprint? OriginFingerprint { get; init; }
    /// <summary>지문 경고(FingerprintComparison.Warnings, 막지 않음).</summary>
    public IReadOnlyList<string> FingerprintWarnings { get; init; } = [];
    /// <summary>지문 차단 항목 불일치(FingerprintComparison.Mismatches). 있으면 FingerprintMatch = false.</summary>
    public IReadOnlyList<string> FingerprintMismatches { get; init; } = [];
    /// <summary>원본 로그인 표시(SQL 인증이면 사용자 이름, Windows 인증이면 "Windows 인증"). 비밀값 없음.</summary>
    public string? OriginLogin { get; init; }
    /// <summary>원본 로그인이 sysadmin인지(IS_SRVROLEMEMBER). 모르면 null.</summary>
    public bool? OriginIsSysadmin { get; init; }
    /// <summary>문맥을 읽을 때 원본에 연결됐는지(Split). false면 지문 비교를 보류하고 미리보기만 허용한다(2026-11-13 N4).</summary>
    public bool OriginReachable { get; init; } = true;
}

/// <summary>원본 표시(§6.4 SandboxStatus.origin, 2026-11-13 추가). 비밀값 없음.</summary>
public sealed record OriginInfo
{
    /// <summary>원본 @@SERVERNAME(SingleServer는 null).</summary>
    public string? Server { get; init; }
    /// <summary>원본 DB 이름.</summary>
    public required string Database { get; init; }
    /// <summary>원본 로그인(SQL 인증 사용자 이름 또는 "Windows 인증"). 비밀값 없음(2026-11-13 N4).</summary>
    public string? Login { get; init; }
    /// <summary>원본 로그인이 sysadmin인지. 모르면 null(2026-11-13 N4).</summary>
    public bool? IsSysadmin { get; init; }
}

/// <summary>매니페스트 요약(§6.4 SandboxStatus.manifest).</summary>
public sealed record ManifestSummary
{
    /// <summary>감사 트리거를 깐 테이블 수.</summary>
    public required int CoveredTables { get; init; }
    /// <summary>제외 테이블 수.</summary>
    public required int ExcludedTables { get; init; }
    /// <summary>감사 순서가 NONE인 연산이 하나라도 있는 테이블 수(E15).</summary>
    public required int AuditNotLast { get; init; }
}

/// <summary>샌드박스 상태(§6.4 GET /api/sandbox, §4 C8 GetStatus).</summary>
public sealed record SandboxStatus
{
    /// <summary>building, ready, dirty, error.</summary>
    public required SandboxState State { get; init; }
    /// <summary>기준 시각(백업 완료). 기준선이 없으면 null.</summary>
    public DateTimeOffset? BaselineAt { get; init; }
    /// <summary>원본·샌드박스 지문 일치.</summary>
    public required bool FingerprintMatch { get; init; }
    /// <summary>미리보기 1건 잠금 중(§4 C8).</summary>
    public required bool Locked { get; init; }
    /// <summary>매니페스트 요약. 기준선이 없으면 null.</summary>
    public ManifestSummary? Manifest { get; init; }
    /// <summary>서버 배치 split | single_server(2026-11-13 추가).</summary>
    public ServerMode Mode { get; init; } = ServerMode.SingleServer;
    /// <summary>원본 서버·DB(비밀값 없음). 기준선 문맥이 없으면 null.</summary>
    public OriginInfo? Origin { get; init; }
    /// <summary>지문 경고(막지 않음).</summary>
    public IReadOnlyList<string> FingerprintWarnings { get; init; } = [];
    /// <summary>지문 차단 항목 불일치(fingerprintMatch = false의 이유).</summary>
    public IReadOnlyList<string> FingerprintMismatches { get; init; } = [];
    /// <summary>Split: [적용]으로 원본이 바뀐 뒤 자동 재구축하지 않아 기준선이 오래됨. 새로 고침으로 풀린다.</summary>
    public bool BaselineStale { get; init; }
    /// <summary>원본에 연결되는지(마지막 기준선 읽기 기준). false면 미리보기는 기존 기준선으로 허용(경고), [적용]은 ORIGIN_UNREACHABLE(2026-11-13 N4).</summary>
    public bool OriginReachable { get; init; } = true;
}

/// <summary>
/// C8 실행 시점 측면(§4 C8 `BuildBaseline()`, `Refresh()`, `GetStatus()`, 미리보기 1건 잠금, 오염 표시). 기준선 구축 자체는 Core BaselineBuilder를 쓴다.
/// 2주차 구현은 Preview 프로젝트(API 담당)가 한다.
/// </summary>
public interface ISandboxManager
{
    /// <summary>현재 상태.</summary>
    Task<SandboxStatus> GetStatusAsync(CancellationToken ct);
    /// <summary>현재 기준선 문맥. 기준선이 없거나 building·error면 InvalidOperationException.</summary>
    Task<BaselineContext> GetContextAsync(CancellationToken ct);
    /// <summary>§5.1을 다시 하고 끝날 때까지 기다린다(파이프라인의 오염 재구축, §5.2 2). 잠금 중이면 풀릴 때까지 기다린다.</summary>
    Task RefreshAsync(CancellationToken ct);
    /// <summary>§5.1 재구축을 백그라운드로 시작하고 바로 돌아온다(POST /api/sandbox/refresh 202, 진행은 GetStatusAsync의 building). 이미 building이면 아무것도 안 함.</summary>
    void StartRefresh();
    /// <summary>미리보기 1건 잠금(§4 C8). Dispose로 푼다. 이미 잠겨 있으면 기다리지 않고 InvalidOperationException(API 409).</summary>
    Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct);
    /// <summary>롤백 뒤 남은 변경이 있어 오염으로 표시(§5.2 4). 다음 미리보기 전에 재구축한다(§5.2 2).</summary>
    void MarkDirty(string reason);
    /// <summary>Split: [적용] 뒤 원본이 바뀌었지만 자동 재구축하지 않고 "기준선 오래됨"으로만 표시(2026-11-13). 기본은 아무것도 안 함.</summary>
    void MarkStale(string reason) { }
}
