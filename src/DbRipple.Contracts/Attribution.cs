namespace DbRipple.Contracts;

/// <summary>
/// C13 입력(§4 `Resolve(firings, net, catalog, manifest)`). 설계의 네 인자에 문장 결과·행 변경·괄호 판정·재조회 차이를 더한 묶음이다
/// (FK 키 일치는 행이 필요하고, 문장 노드·DIRECT 판정은 문장이 필요하다).
/// </summary>
public sealed record ResolveInput
{
    /// <summary>문장 결과(C10).</summary>
    public required IReadOnlyList<StatementResult> Statements { get; init; }
    /// <summary>C11 결과(firings, rowChanges, contextLinks, rereadDifferences).</summary>
    public required CollectResult Collected { get; init; }
    /// <summary>최종 차이(C12, RawCapture.NetChanges).</summary>
    public required IReadOnlyList<NetChange> Net { get; init; }
    /// <summary>카탈로그(FK 메타데이터·트리거 정의)·트리거 정적 분석·매니페스트(BaselineContext.Catalog/Triggers/Manifest).</summary>
    public required BaselineContext Baseline { get; init; }
    /// <summary>
    /// 문장 원문(선택): 문장 전역 번호(StatementResult.Index) → AnalyzedStatement.Sql. 그래프 statement 노드 label(§6.3 "문장 앞부분")에 쓴다.
    /// 파이프라인이 채운다. null이거나 번호가 없으면 label은 "연산 대상"으로 대신한다. 2026-11-10 통합 때 추가(비파괴).
    /// </summary>
    public IReadOnlyDictionary<int, string>? StatementSql { get; init; }
}

/// <summary>원인 귀속 결과(§4 C13 `CausalModel`). 설계는 필드를 정하지 않았다. §6.2의 귀속 필드가 채워진 결과 목록으로 정한다.</summary>
public sealed record CausalModel
{
    /// <summary>Cause·OrderGuarantee가 채워진 묶음(captureSeq 순).</summary>
    public IReadOnlyList<Firing> Firings { get; init; } = [];
    /// <summary>ParentRowIds가 채워진 행 변경.</summary>
    public IReadOnlyList<RowChange> RowChanges { get; init; } = [];
    /// <summary>잔차.</summary>
    public IReadOnlyList<Residual> Residuals { get; init; } = [];
    /// <summary>ExplainedBy가 채워진 최종 차이.</summary>
    public IReadOnlyList<NetChange> NetChanges { get; init; } = [];
}

/// <summary>C13 결과(§4 → `CausalModel`, `GraphDto`).</summary>
public sealed record Resolution
{
    /// <summary>귀속 결과.</summary>
    public required CausalModel Model { get; init; }
    /// <summary>그래프(§6.3, 노드 200개 초과면 group으로 접은 것).</summary>
    public required GraphDto Graph { get; init; }
}

/// <summary>
/// C13 원인 귀속·그래프 조립(§4 C13, §7.2 3~7단계, §6.3). 1·2단계는 C11이 했다(CollectResult.ContextLinks).
/// 감사 트리거(TriggerStaticInfo.IsAuditTrigger)와 비활성 트리거·비활성 FK(E22)는 후보에서 뺀다. DB에 접근하지 않는다(순수 함수).
/// </summary>
public interface ICausalResolver
{
    /// <summary>§4 `Resolve(firings, net, catalog, manifest)`.</summary>
    Resolution Resolve(ResolveInput input);
}
