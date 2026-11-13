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

    /// <summary>스키마마다 주는 DML 권한(D5 허용 목록 INSERT·UPDATE·DELETE와 그 WHERE·조인을 위한 SELECT).</summary>
    public static readonly IReadOnlyList<string> Permissions = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    /// <summary>
    /// EXEC 미리보기용 EXECUTE 권한(2026-11-13, 보안 검토 반영). 스키마 단위로 주면 사용자 정의 함수(CLR·EXECUTE AS 포함)까지 DML 경로에서 실행할 수 있게 되므로,
    /// EXECUTE AS CALLER인 T-SQL 사용자 프로시저(본문을 읽을 수 있는 것, audit 스키마 제외)마다 개체 단위로 준다. 함수에는 주지 않는다.
    /// 목록은 배치 안에서 sys.procedures·sys.sql_modules로 만들고 이름은 QUOTENAME으로 인용한다. 기준 스냅샷 전(4b)에 주므로 그 DDL 트리거 기록도 기준선에 들어간다.
    /// 첫 EXEC 판이 준 스키마 EXECUTE는 거둔다(REVOKE, 그때만 DDL).
    /// </summary>
    public const string ExecutePermission = "EXECUTE";

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
            sb.Append($"IF EXISTS (SELECT 1 FROM sys.database_permissions WHERE grantee_principal_id = USER_ID({u}) AND class = 3 AND major_id = SCHEMA_ID({sl}) AND permission_name = N'{ExecutePermission}')\n")
              .Append($"BEGIN REVOKE {ExecutePermission} ON SCHEMA::{SqlText.Quote(s)} FROM {q}; SET @ddl = 1; END;\n");
        }
        // EXEC 미리보기: EXECUTE AS CALLER인 T-SQL 사용자 프로시저마다 EXECUTE(이미 있으면 건너뛴다)
        sb.Append("DECLARE @dbr_grants nvarchar(max) = (SELECT STRING_AGG(CONVERT(nvarchar(max), N'GRANT EXECUTE ON OBJECT::' + QUOTENAME(SCHEMA_NAME(p.schema_id)) + N'.' + QUOTENAME(p.name)")
          .Append($" + N' TO ' + QUOTENAME({u}) + N';'), N'')\n")
          .Append("    FROM sys.procedures AS p JOIN sys.sql_modules AS m ON m.object_id = p.object_id\n")
          .Append($"    WHERE p.is_ms_shipped = 0 AND p.type = 'P' AND m.definition IS NOT NULL AND m.execute_as_principal_id IS NULL AND SCHEMA_NAME(p.schema_id) <> {SqlText.NLiteral(AuditTriggerGenerator.StoreSchema)}\n")
          .Append($"      AND NOT EXISTS (SELECT 1 FROM sys.database_permissions AS d WHERE d.grantee_principal_id = USER_ID({u}) AND d.class = 1 AND d.major_id = p.object_id")
          .Append($" AND d.minor_id = 0 AND d.permission_name = N'{ExecutePermission}' AND d.state = 'G'));\n")
          .Append("IF @dbr_grants IS NOT NULL BEGIN EXEC sys.sp_executesql @dbr_grants; SET @ddl = 1; END;\n");
        sb.Append("SELECT @ddl;\n");
        return sb.ToString();
    }
}
