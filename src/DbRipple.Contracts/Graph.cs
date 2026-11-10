using System.Text.Json.Serialization;

namespace DbRipple.Contracts;

/// <summary>전파 그래프(§6.3, C2 입력). 노드는 행이 아니라 발동 단위다. C13이 만든다.</summary>
public sealed record GraphDto
{
    /// <summary>노드(type 판별자로 6종).</summary>
    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];
    /// <summary>간선.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];
}

/// <summary>
/// 그래프 노드 공통(§6.3). JSON의 "type" 판별자로 6종을 나눈다(statement, firing, trigger, residual, error, group).
/// 표시 플래그(zeroRows·partial·auditNotLast)는 프런트가 계산한다: zeroRows는 노드 rowCount, partial·auditNotLast는 firingId로 PreviewResult.firings를 찾아 imageMode·auditOrder로(열린 질문 Q15).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatementNode), "statement")]
[JsonDerivedType(typeof(FiringNode), "firing")]
[JsonDerivedType(typeof(TriggerNode), "trigger")]
[JsonDerivedType(typeof(ResidualNode), "residual")]
[JsonDerivedType(typeof(ErrorNode), "error")]
[JsonDerivedType(typeof(GroupNode), "group")]
public abstract record GraphNode
{
    /// <summary>노드 id(ContractIds 형식: st-, f-, t-, r-, err-, g-).</summary>
    public required string Id { get; init; }
}

/// <summary>사용자 문장 노드(§6.3 statement).</summary>
public sealed record StatementNode : GraphNode
{
    /// <summary>문장 번호.</summary>
    public required int StatementIndex { get; init; }
    /// <summary>문장 앞부분.</summary>
    public required string Label { get; init; }
    /// <summary>영향 행 수.</summary>
    public long? RowsAffected { get; init; }
    /// <summary>문장 상태.</summary>
    public required StatementStatus Status { get; init; }
}

/// <summary>변경 묶음 노드(§6.3 firing). 노드 id는 묶음 id와 같다(§6.3 예시 "f-1006").</summary>
public sealed record FiringNode : GraphNode
{
    /// <summary>묶음 id(§6.3 표의 firingId).</summary>
    public required string FiringId { get; init; }
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>연산.</summary>
    public required ChangeOperation Operation { get; init; }
    /// <summary>행 수(0이면 zeroRows).</summary>
    public required long RowCount { get; init; }
    /// <summary>깊이.</summary>
    public required int Depth { get; init; }
    /// <summary>수집 발동 순번.</summary>
    public required long CaptureSeq { get; init; }
}

/// <summary>트리거 1회 실행 노드(§6.3 trigger).</summary>
public sealed record TriggerNode : GraphNode
{
    /// <summary>"스키마.트리거".</summary>
    public required string Trigger { get; init; }
    /// <summary>발동시킨 묶음 id. INSTEAD OF면 null.</summary>
    public string? ParentFiringId { get; init; }
    /// <summary>문장 번호.</summary>
    public required int StatementIndex { get; init; }
    /// <summary>변경을 남겼는지. false면 카탈로그로 "발동했을 것"만 점선으로 그린다(§6.3).</summary>
    public required bool Observed { get; init; }
}

/// <summary>잔차 노드(§6.3 residual).</summary>
public sealed record ResidualNode : GraphNode
{
    /// <summary>잔차 id.</summary>
    public required string ResidualId { get; init; }
    /// <summary>"스키마.테이블".</summary>
    public required string Table { get; init; }
    /// <summary>범주.</summary>
    public required ResidualCategory Category { get; init; }
}

/// <summary>실패·거부 노드(§6.3 error).</summary>
public sealed record ErrorNode : GraphNode
{
    /// <summary>오류 번호.</summary>
    public required int ErrorNumber { get; init; }
    /// <summary>메시지.</summary>
    public required string Message { get; init; }
    /// <summary>수집 장치 오류(D3 규칙 3).</summary>
    public required bool IsToolFault { get; init; }
}

/// <summary>접힌 묶음 노드(§6.3 group): 노드 200개 초과 시 같은 테이블·연산·깊이(D8).</summary>
public sealed record GroupNode : GraphNode
{
    /// <summary>접힌 노드 id.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];
    /// <summary>행 수 합.</summary>
    public required long RowCount { get; init; }
}

/// <summary>그래프 간선(§6.3 공통 필드 + FK 간선 필드).</summary>
public sealed record GraphEdge
{
    /// <summary>"e{n}".</summary>
    public required string Id { get; init; }
    /// <summary>출발 노드 id.</summary>
    public required string Source { get; init; }
    /// <summary>도착 노드 id.</summary>
    public required string Target { get; init; }
    /// <summary>간선 종류.</summary>
    public required EdgeKind Kind { get; init; }
    /// <summary>확신도. WRITES는 앞선 FIRES·REPLACES의 확신도를 넘지 않는다(§6.3).</summary>
    public required Confidence Confidence { get; init; }
    /// <summary>순서 보장.</summary>
    public required OrderGuarantee OrderGuarantee { get; init; }
    /// <summary>근거.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    /// <summary>FK 간선: FK 이름. 다른 간선은 null.</summary>
    public string? Fk { get; init; }
    /// <summary>FK 간선: 참조 동작. 다른 간선은 null.</summary>
    public FkAction? Action { get; init; }
    /// <summary>FK 간선: 일치 행 수. 다른 간선은 null.</summary>
    public long? MatchedRows { get; init; }
}
