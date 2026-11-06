using System.Security.Cryptography;
using System.Text;
using DbRipple.Core.Sql;

namespace DbRipple.Core.Capture;

/// <summary>C9: 감사 저장소 + 테이블·연산별 감사 트리거 설치 계획을 만든다(자동 커밋 실행 전제).</summary>
public static class AuditTriggerGenerator
{
    public const string TriggerPrefix = "dbr_audit_";
    const int MaxNameLength = 128;
    const int HashLength = 8;
    static readonly DmlOperation[] Ops = [DmlOperation.Insert, DmlOperation.Update, DmlOperation.Delete];
    const string JsonOptions = "FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES";

    public static InstallPlan Generate(IEnumerable<TableInfo> tables, DateTimeOffset now)
    {
        var batches = new List<SqlBatch>(StoreBatches());
        var covered = new List<ManifestTable>();
        var excluded = new List<ExcludedTable>();
        var notes = new List<string>();

        foreach (var t in tables.OrderBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var reason = ExclusionReason(t);
            if (reason is not null) { excluded.Add(new(t.DisplayName, reason)); continue; }

            var key = TableKey.Choose(t);
            var order = new Dictionary<DmlOperation, string>();
            var orderSql = new StringBuilder();
            foreach (var op in Ops)
            {
                batches.Add(new($"trigger {t.DisplayName} {Code(op)}", TriggerSql(t, key, op)));
                if (t.HasUserLast(op)) { order[op] = CaptureManifest.None; continue; }
                order[op] = CaptureManifest.Last;
                orderSql.Append("EXEC sys.sp_settriggerorder @triggername = ")
                    .Append(SqlText.NLiteral(SqlText.Quote(t.Schema, TriggerName(t.Name, op))))
                    .Append(", @order = N'Last', @stmttype = N'").Append(Keyword(op)).Append("';\n");
            }
            if (orderSql.Length > 0) batches.Add(new($"order {t.DisplayName}", orderSql.ToString()));

            var unobservable = t.Columns.Where(AuditValueExpressions.IsUnobservable).Select(c => c.Name).ToList();
            var volatileCols = t.Columns.Select(c => (c.Name, Reason: VolatileReason(c)))
                .Where(x => x.Reason is not null).Select(x => new VolatileColumn(x.Name, x.Reason!)).ToList();
            foreach (var c in t.Columns.Where(AuditValueExpressions.IsUnverified))
                notes.Add($"TODO(SP6): {t.DisplayName}.{c.Name}({c.TypeName}) CLR UDT 변환 미검증");

            covered.Add(new(t.DisplayName, true,
                new AuditOrderInfo(order[DmlOperation.Insert], order[DmlOperation.Update], order[DmlOperation.Delete]),
                unobservable, volatileCols));
        }

        var manifest = new CaptureManifest("m-" + now.ToString("yyyyMMdd-HHmm"), covered, excluded, notes);
        return new InstallPlan(batches, manifest);
    }

    /// <summary>감사 저장소 DDL. IDENTITY 열 금지 — 순번은 SEQUENCE.</summary>
    public static IReadOnlyList<SqlBatch> StoreBatches() =>
    [
        new("schema", "CREATE SCHEMA audit AUTHORIZATION dbo;"),
        new("sequence", "CREATE SEQUENCE audit.CaptureSeq AS bigint START WITH 1 INCREMENT BY 1;"),
        new("AuditHeader", """
            CREATE TABLE audit.AuditHeader (
                captureSeq bigint NOT NULL CONSTRAINT PK_AuditHeader PRIMARY KEY,
                tableObjectId int NOT NULL,
                tableName nvarchar(300) NOT NULL,
                operation char(1) NOT NULL CONSTRAINT CK_AuditHeader_operation CHECK (operation IN ('I', 'U', 'D')),
                insertedCount bigint NOT NULL,
                deletedCount bigint NOT NULL,
                nestLevel int NOT NULL,
                tranCount int NOT NULL,
                imageMode varchar(4) NOT NULL CONSTRAINT CK_AuditHeader_imageMode CHECK (imageMode IN ('FULL', 'KEYS'))
            );
            """),
        new("AuditRowImage", """
            CREATE TABLE audit.AuditRowImage (
                captureSeq bigint NOT NULL,
                imageKind varchar(8) NOT NULL,
                ordinal int NOT NULL,
                keyJson nvarchar(max) NULL,
                rowJson nvarchar(max) NULL,
                CONSTRAINT PK_AuditRowImage PRIMARY KEY (captureSeq, imageKind, ordinal)
            );
            """),
        new("AuditControl", """
            CREATE TABLE audit.AuditControl (
                id tinyint NOT NULL CONSTRAINT PK_AuditControl PRIMARY KEY CONSTRAINT CK_AuditControl_id CHECK (id = 1),
                mode varchar(4) NOT NULL CONSTRAINT CK_AuditControl_mode CHECK (mode IN ('FULL', 'KEYS')),
                cap bigint NOT NULL
            );
            INSERT INTO audit.AuditControl (id, mode, cap) VALUES (1, 'FULL', 100000);
            """),
    ];

    /// <summary>[스키마].[dbr_audit_테이블_I|U|D]의 이름 부분. 128자 초과면 자르고 해시를 붙인다.</summary>
    public static string TriggerName(string table, DmlOperation op)
    {
        var suffix = "_" + Code(op);
        var name = TriggerPrefix + table + suffix;
        if (name.Length <= MaxNameLength) return name;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table)))[..HashLength].ToLowerInvariant();
        var keep = MaxNameLength - TriggerPrefix.Length - 1 - HashLength - suffix.Length;
        return TriggerPrefix + table[..keep] + "_" + hash + suffix;
    }

    public static string TriggerSql(TableInfo t, TableKey key, DmlOperation op)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TRIGGER ").Append(SqlText.Quote(t.Schema, TriggerName(t.Name, op)))
          .Append(" ON ").Append(t.QuotedName).Append('\n')
          .Append("AFTER ").Append(Keyword(op)).Append('\n')
          .Append("AS\nBEGIN\n")
          .Append("    SET NOCOUNT ON;\n")
          .Append("    DECLARE @dbr_seq bigint, @dbr_mode varchar(4);\n")
          .Append("    SET @dbr_seq = NEXT VALUE FOR audit.CaptureSeq;\n")
          .Append("    SELECT @dbr_mode = mode FROM audit.AuditControl WHERE id = 1;\n")
          .Append("    SET @dbr_mode = ISNULL(@dbr_mode, 'FULL');\n")
          .Append("    INSERT INTO audit.AuditHeader (captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode)\n")
          .Append("    SELECT @dbr_seq, ").Append(t.ObjectId).Append(", ").Append(SqlText.NLiteral(t.QuotedName))
          .Append(", '").Append(Code(op)).Append("', (SELECT COUNT_BIG(*) FROM inserted), (SELECT COUNT_BIG(*) FROM deleted), TRIGGER_NESTLEVEL(), @@TRANCOUNT, @dbr_mode;\n");

        // U는 DELETED 다음 INSERTED(마지막 INSERT의 행 수 = 발동 행 수)
        if (op != DmlOperation.Insert) AppendImage(sb, t, key, "DELETED", "deleted", "d");
        if (op != DmlOperation.Delete) AppendImage(sb, t, key, "INSERTED", "inserted", "i");
        sb.Append("END;\n");
        return sb.ToString();
    }

    static void AppendImage(StringBuilder sb, TableInfo t, TableKey key, string kind, string source, string alias)
    {
        var keyExpr = key.Kind == KeyKind.ROW
            ? "NULL"
            : JsonObject(key.Columns.Select(n => t.Columns.FirstOrDefault(c => c.Name == n) ?? new ColumnInfo(n, "")), alias);
        var rowCols = t.Columns.Where(c => !AuditValueExpressions.IsUnobservable(c)).ToList();
        var rowExpr = rowCols.Count == 0
            ? "NULL"
            : $"CASE WHEN @dbr_mode = 'KEYS' THEN NULL ELSE {JsonObject(rowCols, alias)} END";
        sb.Append("    INSERT INTO audit.AuditRowImage (captureSeq, imageKind, ordinal, keyJson, rowJson)\n")
          .Append("    SELECT @dbr_seq, '").Append(kind).Append("', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),\n")
          .Append("        ").Append(keyExpr).Append(",\n")
          .Append("        ").Append(rowExpr).Append('\n')
          .Append("    FROM ").Append(source).Append(" AS ").Append(alias).Append(";\n");
    }

    static string JsonObject(IEnumerable<ColumnInfo> cols, string alias) =>
        "(SELECT " + string.Join(", ", cols.SelectMany(c => AuditValueExpressions.SelectItems(c, alias))) + " " + JsonOptions + ")";

    static string? ExclusionReason(TableInfo t) =>
        t.IsMemoryOptimized ? "MEMORY_OPTIMIZED"
        : t.Temporal == TemporalType.History ? "TEMPORAL_HISTORY"
        : null;

    static string? VolatileReason(ColumnInfo c) =>
        c.IsIdentity ? "IDENTITY"
        : c.IsRowVersion ? "ROWVERSION"
        : c.HasNondeterministicDefault ? "NONDETERMINISTIC_DEFAULT"
        : c.IsComputed ? "COMPUTED"
        : null;

    public static string Code(DmlOperation op) => op switch
    {
        DmlOperation.Insert => "I",
        DmlOperation.Update => "U",
        _ => "D",
    };

    public static string Keyword(DmlOperation op) => op switch
    {
        DmlOperation.Insert => "INSERT",
        DmlOperation.Update => "UPDATE",
        _ => "DELETE",
    };
}
