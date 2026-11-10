using DbRipple.Core.Capture;
using DbRipple.Core.Sql;

namespace DbRipple.Core.Baseline;

/// <summary>
/// 미리보기 실행 사용자(열린 질문 Q12 결정): 샌드박스 안의 <c>dbr_preview_user</c>(WITHOUT LOGIN, 기본 스키마 dbo, 사용자 스키마 DML 권한만).
/// 감사 저장소(audit 스키마)에는 권한을 주지 않는다. 감사 쓰기는 dbo 소유권 체인으로 된다(SP10).
/// <para>
/// 기준선 구축(BaselineBuilder, C9 설치 전 단계)이 이 배치로 사용자를 만든다. AW의 DDL 트리거 ddlDatabaseTriggerLog가 CREATE USER·GRANT에도
/// 발동해 dbo.DatabaseLog에 행을 자동 커밋하므로(SP10), 그 행이 기준 스냅샷에 들어가야 미리보기마다 샌드박스가 오염되지 않는다(Q12 수정, 2026-11-10).
/// 실행기(C10)는 같은 배치로 멱등 확인만 한다(이미 있으면 DDL을 하지 않는다).
/// </para>
/// </summary>
public static class PreviewUserSql
{
    /// <summary>실행 사용자 이름(Q12).</summary>
    public const string UserName = "dbr_preview_user";

    /// <summary>스키마마다 주는 권한(DML만, D5 허용 목록 INSERT·UPDATE·DELETE와 그 WHERE·조인을 위한 SELECT).</summary>
    public static readonly IReadOnlyList<string> Permissions = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    /// <summary>EnsureBatch가 처음 실행될 때 내는 DDL 이벤트(DDL 트리거 부작용 기록용).</summary>
    public static readonly IReadOnlyList<string> DdlEvents = ["CREATE_USER", "GRANT_DATABASE"];

    /// <summary>
    /// 사용자와 권한을 멱등으로 맞추는 배치. 이미 있으면 DDL을 하지 않는다(DDL 트리거를 다시 발동하지 않게).
    /// 결과 집합 1개: ddl(이번에 CREATE USER·GRANT를 했는지 0/1). 사용자가 역할에 속해 있으면 오류 50000으로 실패한다(DML 권한만이라는 전제가 깨짐).
    /// schemas는 사용자 테이블 스키마(감사 저장소 스키마는 넘겨도 뺀다).
    /// </summary>
    public static string EnsureBatch(IEnumerable<string> schemas, string user = UserName)
    {
        var u = SqlText.NLiteral(user);
        var q = SqlText.Quote(user);
        var sb = new System.Text.StringBuilder();
        sb.Append("DECLARE @ddl bit = 0;\n");
        sb.Append($"IF USER_ID({u}) IS NULL BEGIN CREATE USER {q} WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo]; SET @ddl = 1; END;\n");
        sb.Append($"IF EXISTS (SELECT 1 FROM sys.database_role_members WHERE member_principal_id = USER_ID({u}))\n");
        sb.Append($"    THROW 50000, N'{user.Replace("'", "''")}가 데이터베이스 역할에 속해 있음(DML 권한만이라는 전제가 깨짐)', 1;\n");
        var perms = string.Join(", ", Permissions.Select(p => "N'" + p + "'"));
        foreach (var s in schemas.Where(s => !string.Equals(s, AuditTriggerGenerator.StoreSchema, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal))
        {
            var sl = SqlText.NLiteral(s);
            sb.Append($"IF (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = USER_ID({u}) AND class = 3 AND major_id = SCHEMA_ID({sl})")
              .Append($" AND state = 'G' AND permission_name IN ({perms})) < {Permissions.Count}\n")
              .Append($"BEGIN GRANT {string.Join(", ", Permissions)} ON SCHEMA::{SqlText.Quote(s)} TO {q}; SET @ddl = 1; END;\n");
        }
        sb.Append("SELECT @ddl;\n");
        return sb.ToString();
    }
}
