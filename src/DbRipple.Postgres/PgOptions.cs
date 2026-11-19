using System.Text.RegularExpressions;

namespace DbRipple.Postgres;

/// <summary>PostgreSQL 엔진 설정(한 서버: 원본과 샌드박스가 같은 서버). 연결 문자열은 관리용 DB(postgres)에 붙는다.</summary>
public sealed record PgOptions
{
    /// <summary>Npgsql 연결 문자열(Database는 관리용 DB). 비밀값이 들어 있으므로 로그에 쓰지 않는다.</summary>
    public required string ServerConnectionString { get; init; }
    /// <summary>원본 DB 이름.</summary>
    public required string OriginDb { get; init; }
    /// <summary>샌드박스 DB 이름(원본을 TEMPLATE으로 복제한다).</summary>
    public required string SandboxDb { get; init; }
    /// <summary>
    /// 사용자 문장을 실행할 LOGIN 역할(비 superuser, 다른 역할의 구성원 아님, 사용자 테이블 DML 권한만, 2026-11-19 결정 3).
    /// 실행기는 이 역할로 직접 로그인하고 비밀번호는 연결마다 무작위로 정했다가 바로 지운다(PgPreviewLogin).
    /// null이면 연결 계정 그대로 실행한다(권한 경계 없음, 테스트 전용).
    /// </summary>
    public string? PreviewRole { get; init; } = "dbr_preview";
    /// <summary>문장별 명령 시간 제한(초).</summary>
    public int CommandTimeoutSeconds { get; init; } = 60;
    /// <summary>기준선 단계 로그(선택).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>DB 이름·역할 이름 검사(식별자로 그대로 쓰므로 소문자·숫자·밑줄만 허용).</summary>
    public void Validate()
    {
        PgNames.RequireSimple(OriginDb, "원본 DB");
        PgNames.RequireSimple(SandboxDb, "샌드박스 DB");
        if (PreviewRole is not null) PgNames.RequireSimple(PreviewRole, "미리보기 역할");
        if (string.Equals(OriginDb, SandboxDb, StringComparison.Ordinal))
            throw new InvalidOperationException("원본과 샌드박스 DB 이름이 같습니다.");
    }

    /// <summary>연결 계정의 관리용 DB 연결(풀링 끔). 기준선 구축과 미리보기 역할 비밀번호 설정에 쓴다.</summary>
    internal string AdminConnectionString() => new Npgsql.NpgsqlConnectionStringBuilder(ServerConnectionString) { Pooling = false }.ConnectionString;
}

/// <summary>PostgreSQL 식별자 도우미.</summary>
public static partial class PgNames
{
    /// <summary>감사 저장소 스키마(샌드박스 안).</summary>
    public const string AuditSchema = "dbr_audit";
    /// <summary>감사 트리거 이름 접두(같은 종류 트리거는 이름순으로 돌므로 맨 뒤에 오게 zzz_).</summary>
    public const string AuditTriggerPrefix = "zzz_dbr_audit_";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SimpleName();

    /// <summary>인용 없이 쓸 수 있는 소문자 식별자만 받는다(설정 값용).</summary>
    public static void RequireSimple(string name, string what)
    {
        if (!SimpleName().IsMatch(name))
            throw new InvalidOperationException($"{what} 이름 '{name}'은 소문자·숫자·밑줄만 쓸 수 있습니다(PostgreSQL 최소판).");
    }

    /// <summary>"..." 인용(안의 "는 두 번).</summary>
    public static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    /// <summary>스키마.테이블 인용형.</summary>
    public static string Quote(string schema, string name) => Quote(schema) + "." + Quote(name);

    /// <summary>문자열 리터럴('…').</summary>
    public static string Literal(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// 기존 수집기(ChangeCollector)가 읽는 대괄호 인용형 "[schema].[table]". 카탈로그에 객체 ID가 있으면 쓰이지 않지만 같은 모양을 지킨다.
    /// </summary>
    public static string Bracket(string schema, string name) => "[" + schema.Replace("]", "]]") + "].[" + name.Replace("]", "]]") + "]";

    /// <summary>PostgreSQL oid(uint32)를 계약의 int 객체 ID로. 2^31 이상이면 기준선 구축을 실패로 끝낸다(설계 결정 9).</summary>
    public static int ObjectId(uint oid, string what) =>
        oid <= int.MaxValue ? (int)oid : throw new InvalidOperationException($"{what}의 oid {oid}가 int 범위를 넘습니다(PostgreSQL 최소판 제한).");
}
