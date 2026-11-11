using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DbRipple.Contracts;

// 2주차 인계 7(ApiExceptionHandler가 실행기 InvalidOperationException까지 409로 냄, 기대 500·503)과 C15가 함께 쓰는 공통 식.
// 2026-11-11 3주차 추가(비파괴). 새 예외는 InvalidOperationException을 상속하므로 기존 catch·테스트(Assert.Throws<InvalidOperationException>는 정확한 형식 비교라 예외)는 형식만 바꾸면 된다.

/// <summary>409 사유(설계 §6.4 "409: 준비 안 됨, 잠금, 지문 불일치"의 코드화, 설계 외 Q32).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictReason>))]
public enum ConflictReason
{
    /// <summary>다른 미리보기·적용이 잠금을 쥐고 있음.</summary>
    [JsonStringEnumMemberName("LOCKED")] Locked,
    /// <summary>기준선 구축 중.</summary>
    [JsonStringEnumMemberName("BUILDING")] Building,
    /// <summary>기준선이 없음(처음, 구축 실패).</summary>
    [JsonStringEnumMemberName("NO_BASELINE")] NoBaseline,
    /// <summary>원본·샌드박스 환경 지문 불일치(미리보기 차단, §5.1 6). [적용]의 지문 불일치는 409가 아니라 precondition_failed다.</summary>
    [JsonStringEnumMemberName("FINGERPRINT_MISMATCH")] FingerprintMismatch,
    /// <summary>재구축 뒤에도 오염 상태.</summary>
    [JsonStringEnumMemberName("DIRTY")] Dirty,
}

/// <summary>
/// 지금 상태로는 처리할 수 없음(API 409). SandboxManager·PreviewPipeline·ApplyService·CatalogSchemaProvider가 던진다.
/// ApiExceptionHandler는 이 형식만 409로 내고, 그 밖의 InvalidOperationException은 500으로 낸다(인계 7). ProblemDetails 확장 "reason"에 Reason 값을 싣는다.
/// </summary>
public sealed class PreviewConflictException(ConflictReason reason, string message) : InvalidOperationException(message)
{
    /// <summary>ProblemDetails 확장 속성 이름.</summary>
    public const string ReasonExtension = "reason";

    /// <summary>사유.</summary>
    public ConflictReason Reason { get; } = reason;
}

/// <summary>
/// SQL Server 연결이 끊기거나 열 수 없음(E20, API 503). 실행기·적용 서비스가 SqlException(연결 계열)이나 연결 상태 이상을 이 형식으로 감싸 던진다.
/// 메시지에 연결 문자열을 넣지 않는다.
/// </summary>
public sealed class SandboxUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>계약 값 계산 식. 같은 값을 두 컴포넌트(Preview·Apply)가 따로 계산하면 어긋나므로 한 곳에 둔다.</summary>
public static class ContractFormulas
{
    /// <summary>sqlHash = "sha256:" + SHA-256(UTF-8, 정규화 없음) 소문자 16진(Q17). Preview PreviewPipeline.SqlHash와 같은 식이다.</summary>
    public static string SqlHash(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
    }

    /// <summary>테이블별 netSummary(§6.2, D7 2). 다중집합은 multiplicity 합(Q16), 테이블 이름 서수 순. Preview PreviewPipeline.Summarize와 같은 식이다.</summary>
    public static IReadOnlyList<NetSummaryRow> Summarize(IEnumerable<NetChange> net)
    {
        ArgumentNullException.ThrowIfNull(net);
        return net
            .GroupBy(n => n.Table, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new NetSummaryRow
            {
                Table = g.Key,
                Inserted = g.Where(n => n.Change == NetChangeKind.Inserted).Sum(n => n.Multiplicity),
                Updated = g.Where(n => n.Change == NetChangeKind.Updated).Sum(n => n.Multiplicity),
                Deleted = g.Where(n => n.Change == NetChangeKind.Deleted).Sum(n => n.Multiplicity),
                MultisetRemoved = g.Where(n => n.Change == NetChangeKind.MultisetRemoved).Sum(n => n.Multiplicity),
                MultisetAdded = g.Where(n => n.Change == NetChangeKind.MultisetAdded).Sum(n => n.Multiplicity),
            })
            .ToList();
    }
}
