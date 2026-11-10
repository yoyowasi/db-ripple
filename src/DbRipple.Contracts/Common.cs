using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbRipple.Core.Capture;
using Microsoft.Data.SqlClient;

namespace DbRipple.Contracts;

// 공통 규칙
// - 테이블 이름은 "스키마.테이블" 표시형(대괄호 없음)이다. Core TableInfo.DisplayName·CaptureManifest와 같다.
//   감사 헤더 tableName은 대괄호 인용형("[Sales].[SalesOrderDetail]")으로 저장되므로 C11이 tableObjectId로 카탈로그를 찾아 표시형으로 바꾼다.
// - 트리거 이름도 "스키마.트리거" 표시형이다(§6.3 예시 "Sales.iduSalesOrderDetail").
// - 행 값(before/after, 키 값)은 §6.1 규칙의 문자열이다. NULL은 null. 열 이름 → 값 사전(RowValues).

/// <summary>스크립트 전체 기준 위치(§6.2 Statement.span, §6.4 Diagnostic.span). 줄·열은 1부터(ScriptDom, D5 v1.3).</summary>
public sealed record SourceSpan
{
    /// <summary>시작 줄(1부터).</summary>
    public required int StartLine { get; init; }
    /// <summary>시작 열(1부터).</summary>
    public required int StartColumn { get; init; }
    /// <summary>끝 줄.</summary>
    public required int EndLine { get; init; }
    /// <summary>끝 열.</summary>
    public required int EndColumn { get; init; }
}

/// <summary>행 식별 키(§6.2 RowChange.key, Residual.key, NetChange.key). 선택 규칙은 Core TableKey.Choose(PK → NOT NULL 유일 인덱스 → ROW)와 같아야 한다.</summary>
public sealed record RowKey
{
    /// <summary>PK, UNIQUE(NOT NULL 유일 인덱스), ROW(키 없음, 다중집합) — §6.2 key.kind, E6.</summary>
    public required KeyKind Kind { get; init; }
    /// <summary>키 열 이름(키 순서). ROW면 빈 배열.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>키 값(§6.1 문자열, Columns와 같은 순서). ROW면 빈 배열.</summary>
    public IReadOnlyList<string?> Values { get; init; } = [];
}

/// <summary>
/// 열린 연결과 (있으면) 그 연결의 로컬 트랜잭션. §4 C12 "열린 트랜잭션을 가진 연결을 받아 그 연결에서 계산"을 C#으로 옮긴 형태다.
/// SqlClient는 로컬 트랜잭션이 걸린 연결의 명령에 SqlCommand.Transaction을 요구하므로 연결만으로는 부족하다.
/// 실행기(C10)가 T-SQL BEGIN TRAN으로 트랜잭션을 열었다면 Transaction은 null이다. 받은 쪽은 연결을 닫거나 트랜잭션을 끝내지 않는다.
/// </summary>
public sealed record SqlSession
{
    /// <summary>열린 연결(풀링 끔, §4 C10 실행 봉투).</summary>
    public required SqlConnection Connection { get; init; }
    /// <summary>SqlClient 로컬 트랜잭션. 없거나 T-SQL로 연 트랜잭션이면 null.</summary>
    public SqlTransaction? Transaction { get; init; }

    /// <summary>이 세션에 묶인 명령을 만든다(Transaction 설정 포함).</summary>
    public SqlCommand CreateCommand(string sql, int? timeoutSeconds = null)
    {
        var cmd = new SqlCommand(sql, Connection, Transaction);
        if (timeoutSeconds is int t) cmd.CommandTimeout = t;
        return cmd;
    }
}

/// <summary>
/// 결과 안 식별자 형식. 설계 예시(§6.2·§6.3)의 형식을 따르며 C11·C13·그래프가 같은 규칙을 쓰도록 한 곳에 둔다.
/// </summary>
public static class ContractIds
{
    /// <summary>변경 묶음 id "f-{captureSeq}"(§6.2 Firing.id 예시 "f-1005").</summary>
    public static string Firing(long captureSeq) => $"f-{captureSeq}";
    /// <summary>행 변경 id "rc-{captureSeq}-{n}", n은 묶음 안 1부터(§6.2 예시 "rc-1005-1"). 순서 의미 없음.</summary>
    public static string RowChange(long captureSeq, int n) => $"rc-{captureSeq}-{n}";
    /// <summary>잔차 id "r-{n}", n은 결과 안 1부터(§6.2 예시 "r-1").</summary>
    public static string Residual(int n) => $"r-{n}";
    /// <summary>문장 노드 id "st-{statementIndex}"(§6.3 예시 "st-1").</summary>
    public static string StatementNode(int statementIndex) => $"st-{statementIndex}";
    /// <summary>트리거 실행 노드 id "t-{n}"(§6.3 예시 "t-1").</summary>
    public static string TriggerNode(int n) => $"t-{n}";
    /// <summary>간선 id "e{n}"(§6.3 예시 "e1").</summary>
    public static string Edge(int n) => $"e{n}";
    /// <summary>오류 노드 id "err-{statementIndex}". 설계에 예시가 없어 이 계약이 정했다(열린 질문 Q9).</summary>
    public static string ErrorNode(int statementIndex) => $"err-{statementIndex}";
    /// <summary>접힌 묶음 노드 id "g-{n}". 설계에 예시가 없어 이 계약이 정했다(열린 질문 Q9).</summary>
    public static string GroupNode(int n) => $"g-{n}";
}

/// <summary>오류 분류 규칙(D3 규칙 3 v1.3 정정, R18).</summary>
public static class ErrorClassification
{
    /// <summary>
    /// 수집 장치(감사 트리거)가 낸 오류인지. 판정은 메시지 본문이 아니라 오류의 프로시저 필드(SqlClient SqlError.Procedure, T-SQL ERROR_PROCEDURE())가
    /// 감사 트리거 접두 "dbr_audit_"로 시작하는지다. 스키마가 붙어 오면("Sales.dbr_audit_…", "[Sales].[…]") 마지막 부분만 본다.
    /// 근거: logs/week1/r18-computed-overflow-repro.log(ERROR_PROCEDURE = dbr_audit_T_U). SqlError.Procedure 값 형식은 아직 실측하지 않았다.
    /// </summary>
    public static bool IsToolFault(string? procedure)
    {
        if (string.IsNullOrEmpty(procedure)) return false;
        var last = procedure;
        var dot = last.LastIndexOf('.');
        if (dot >= 0) last = last[(dot + 1)..];
        last = last.Trim('[', ']');
        return last.StartsWith(AuditTriggerGenerator.TriggerPrefix, StringComparison.Ordinal);
    }
}

/// <summary>계약 타입의 JSON 직렬화 설정. API(C5), 저장소 파일(C14), 내보내기, 평가 하네스가 같은 설정을 쓴다.</summary>
public static class ContractJson
{
    /// <summary>camelCase, 열거형 문자열(Core KeyKind 포함), NULL 속성 포함(§6.2 예시의 "error": null), 들여쓰기 없음.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Options와 같은 설정의 새 인스턴스(ASP.NET Core ConfigureHttpJsonOptions에 옮겨 담을 때 쓴다).</summary>
    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary>ASP.NET Core 등 이미 만들어진 옵션에 같은 설정을 적용한다.</summary>
    public static void Apply(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        target.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        target.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        if (!target.Converters.Any(c => c is JsonStringEnumConverter)) target.Converters.Add(new JsonStringEnumConverter());
    }
}
