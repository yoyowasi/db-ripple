using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.Postgres;

/// <summary>
/// PostgreSQL 최소판 문장 판별(설계 결정 6). 문장 나누기는 PgSqlLexer, 판정은 첫 낱말과 대상 테이블만 본다(완전한 파서가 아니다).
/// 허용: INSERT INTO t, UPDATE [ONLY] t, DELETE FROM [ONLY] t. 대상은 카탈로그에 있고 감사가 깔린 사용자 테이블이어야 한다.
/// 스키마를 생략한 이름은 public에서만 찾는다(샌드박스는 원본 DB의 search_path 설정을 물려받지 않는다).
/// 문장 안의 위험 함수(set_config·백엔드 종료·파일·dblink·SQL 문자열 실행 등)는 거부한다. 사용자 함수 안까지는 보지 않으므로 WARN_PG_MINIMAL_SCOPE를 늘 낸다.
/// 예외를 던지지 않고 거부로 돌려준다(계약).
/// </summary>
public sealed class PgBatchAnalyzer : IBatchAnalyzer
{
    static readonly HashSet<string> Ddl = new(StringComparer.Ordinal)
        { "CREATE", "ALTER", "DROP", "GRANT", "REVOKE", "COMMENT", "SECURITY", "IMPORT", "REINDEX" };
    static readonly HashSet<string> Transaction = new(StringComparer.Ordinal)
        { "BEGIN", "START", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE", "ABORT", "PREPARE" };
    static readonly HashSet<string> Admin = new(StringComparer.Ordinal)
        { "VACUUM", "ANALYZE", "ANALYSE", "CHECKPOINT", "LOCK", "LISTEN", "NOTIFY", "UNLISTEN", "DISCARD", "LOAD", "REFRESH", "CLUSTER", "DEALLOCATE", "FETCH", "MOVE", "CLOSE", "DECLARE" };

    // 문장 안 함수 호출 거부 목록(소문자). 미리보기 역할 전환(SET LOCAL ROLE)은 연결 사용자가 superuser면 set_config('role', …)로 되돌릴 수 있어 막는다.
    static readonly HashSet<string> AdminFunctions = new(StringComparer.Ordinal)
    {
        "set_config", "pg_terminate_backend", "pg_cancel_backend", "pg_reload_conf", "pg_rotate_logfile", "pg_switch_wal",
        "pg_create_restore_point", "pg_backup_start", "pg_backup_stop", "pg_start_backup", "pg_stop_backup", "pg_promote",
        "pg_logical_emit_message", "pg_notify", "pg_sleep", "pg_sleep_for", "pg_sleep_until",
        "pg_advisory_lock", "pg_advisory_xact_lock", "pg_advisory_lock_shared", "pg_advisory_xact_lock_shared",
        "query_to_xml", "query_to_xml_and_xmlschema", "query_to_xmlschema", "cursor_to_xml", "cursor_to_xmlschema",
        "table_to_xml", "table_to_xml_and_xmlschema", "schema_to_xml", "schema_to_xml_and_xmlschema", "database_to_xml", "database_to_xml_and_xmlschema",
        "lo_unlink", "lo_create", "lo_from_bytea", "lo_put", "txid_current", "pg_current_xact_id",
    };
    static readonly HashSet<string> ExternalFunctions = new(StringComparer.Ordinal)
    {
        "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file", "pg_file_write", "lo_import", "lo_export",
        "dblink", "dblink_exec", "dblink_connect", "dblink_send_query", "dblink_open",
    };
    static readonly HashSet<string> SystemSchemas = new(StringComparer.Ordinal) { "pg_catalog", "information_schema", "pg_toast" };

    public AnalysisResult Analyze(string sql, BaselineContext baseline)
    {
        try
        {
            return AnalyzeCore(sql ?? "", baseline);
        }
        catch (Exception ex)
        {
            return new AnalysisResult
            {
                Rejects = [Reject(null, Whole(sql ?? ""), AnalysisRules.RejectAnalysisError, "분석 중 오류: " + ex.Message)],
            };
        }
    }

    static AnalysisResult AnalyzeCore(string sql, BaselineContext baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (string.IsNullOrWhiteSpace(sql))
            return new AnalysisResult { Rejects = [Reject(null, Whole(sql), AnalysisRules.RejectEmptyScript, "실행할 문장이 없습니다.")] };

        var (parts, error, errorAt) = PgSqlLexer.Split(sql);
        if (error is not null)
            return new AnalysisResult { Rejects = [Reject(null, Span(sql, errorAt, Math.Min(sql.Length, errorAt + 1)), AnalysisRules.RejectParseError, error)] };
        if (parts.Count == 0)
            return new AnalysisResult { Rejects = [Reject(null, Whole(sql), AnalysisRules.RejectEmptyScript, "실행할 문장이 없습니다(주석만 있음).")] };

        var tables = baseline.Catalog.Tables.ToDictionary(t => (t.Schema, t.Name), t => t);
        var manifest = baseline.Manifest.Tables.ToDictionary(t => t.Table, StringComparer.Ordinal);
        var excluded = baseline.Manifest.ExcludedTables.ToDictionary(t => t.Table, t => t.Reason, StringComparer.Ordinal);
        var statements = new List<AnalyzedStatement>();
        var rejects = new List<Diagnostic>();
        var warnings = new List<Diagnostic>
        {
            new()
            {
                Span = Span(sql, parts[0].Start, parts[0].Start),
                Severity = Severity.Warning,
                RuleId = AnalysisRules.WarnPgMinimalScope,
                Message = "PostgreSQL 최소판입니다: 미리보기만 되고 [적용]은 없습니다. 문장 안 사용자 함수와 트리거 본문은 이름 수준으로만 검사합니다.",
            },
        };

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var index = i + 1;
            var span = Span(sql, part.Start, part.End);
            var tokens = PgSqlLexer.Tokens(part);
            var first = tokens.Count > 0 && !tokens[0].Quoted ? tokens[0].Upper : "";
            ChangeOperation op;
            int targetAt;
            switch (first)
            {
                case "INSERT" when Word(tokens, 1) == "INTO":
                    (op, targetAt) = (ChangeOperation.Insert, 2);
                    break;
                case "UPDATE":
                    (op, targetAt) = (ChangeOperation.Update, Word(tokens, 1) == "ONLY" ? 2 : 1);
                    break;
                case "DELETE" when Word(tokens, 1) == "FROM":
                    (op, targetAt) = (ChangeOperation.Delete, Word(tokens, 2) == "ONLY" ? 3 : 2);
                    break;
                default:
                    rejects.Add(Reject(index, span, StatementRule(first, tokens), StatementMessage(first)));
                    continue;
            }

            var (schema, name, ok) = TargetName(tokens, targetAt);
            if (!ok)
            {
                rejects.Add(Reject(index, span, AnalysisRules.RejectParseError, "대상 테이블 이름을 읽지 못했습니다."));
                continue;
            }
            var resolvedSchema = schema ?? "public";
            var display = resolvedSchema + "." + name;
            if (resolvedSchema == PgNames.AuditSchema)
            {
                rejects.Add(Reject(index, span, AnalysisRules.RejectCaptureStore, "감사 저장소(dbr_audit)는 대상이 될 수 없습니다."));
                continue;
            }
            if (SystemSchemas.Contains(resolvedSchema) || !tables.ContainsKey((resolvedSchema, name)))
            {
                rejects.Add(Reject(index, span, AnalysisRules.RejectTargetNotUserTable, schema is null
                    ? $"public.{name} 테이블이 없습니다. 다른 스키마면 스키마를 붙여 주세요(최소판은 search_path를 따르지 않습니다)."
                    : $"{display}는 사용자 테이블이 아닙니다(뷰·외부 테이블·시스템 테이블은 받지 않습니다)."));
                continue;
            }
            if (excluded.TryGetValue(display, out var reason) || !manifest.TryGetValue(display, out var mt) || !mt.Covered)
            {
                rejects.Add(Reject(index, span, AnalysisRules.RejectTargetNotUserTable, $"{display}는 감사 수집 대상이 아닙니다({reason ?? "NOT_COVERED"}). 파티션 테이블은 최소판에서 받지 않습니다."));
                continue;
            }
            if (DangerousCall(part, tokens) is { } call)
            {
                rejects.Add(Reject(index, span, call.Rule, $"문장 안 함수 {call.Name}()은(는) 미리보기에서 쓸 수 없습니다."));
                continue;
            }

            // 도달하는 트리거(직접 대상 + FK 연쇄 + 트리거 쓰기 대상)의 외부 부작용은 거부, 동적·모름은 경고.
            foreach (var t in Reachable(display, op, baseline))
            {
                if (t.ExternalSideEffects.Count > 0)
                    rejects.Add(Reject(index, span, AnalysisRules.RejectTriggerExternalEffect,
                        $"트리거 {t.Trigger}에 외부 부작용({string.Join(", ", t.ExternalSideEffects)})이 있어 미리보기에서 실행할 수 없습니다."));
                else if (t.WriteTargetsUnknown || t.HasDynamicSql)
                    warnings.Add(Warn(index, span, AnalysisRules.WarnTriggerDynamicSql, $"트리거 {t.Trigger}의 쓰기 대상을 다 알 수 없습니다(동적 SQL·다른 언어). 원인은 추정으로 표시됩니다."));
            }
            var order = op switch { ChangeOperation.Insert => mt.AuditOrder.Insert, ChangeOperation.Update => mt.AuditOrder.Update, _ => mt.AuditOrder.Delete };
            if (order == CaptureManifest.None)
                warnings.Add(Warn(index, span, AnalysisRules.WarnAuditNotLast, $"{display}에 감사 트리거보다 늦게 도는 문장 단위 트리거가 있어 마지막 값이 다를 수 있습니다."));

            statements.Add(new AnalyzedStatement
            {
                Index = index, Batch = 1, Position = index, Kind = op, Target = display, Span = span, Sql = part.Sql,
            });
        }
        return new AnalysisResult { Statements = statements, Rejects = rejects, Warnings = Dedupe(warnings) };
    }

    static string Word(IReadOnlyList<PgToken> tokens, int i) => i < tokens.Count && !tokens[i].Quoted ? tokens[i].Upper : "";

    /// <summary>tokens[at]부터 이름 또는 스키마.이름. 인용하지 않은 낱말은 소문자로 접는다.</summary>
    internal static (string? Schema, string Name, bool Ok) TargetName(IReadOnlyList<PgToken> tokens, int at)
    {
        static string? Ident(PgToken? t) => t is null || t.Text is "." or "(" ? null : t.Quoted ? t.Text : t.Text.ToLowerInvariant();
        var a = Ident(at < tokens.Count ? tokens[at] : null);
        if (a is null) return (null, "", false);
        if (at + 2 < tokens.Count && tokens[at + 1].Text == "." && !tokens[at + 1].Quoted)
        {
            var b = Ident(tokens[at + 2]);
            if (b is null) return (null, "", false);
            // 세 부분 이름(db.schema.table)은 받지 않는다.
            if (at + 3 < tokens.Count && tokens[at + 3].Text == "." && !tokens[at + 3].Quoted) return (null, "", false);
            return (a, b, true);
        }
        return (null, a, true);
    }

    /// <summary>거부 목록 함수 호출(이름 바로 뒤 공백만 두고 여는 괄호)을 찾는다.</summary>
    internal static (string Name, string Rule)? DangerousCall(PgStatementText st, IReadOnlyList<PgToken> tokens)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Quoted || t.Text is "." or "(" || tokens[i + 1].Text != "(") continue;
            // 이름과 괄호 사이에 공백 말고 다른 것(연산자 등)이 있으면 함수 호출이 아니다.
            if (!string.IsNullOrWhiteSpace(st.Cleaned[(t.End - st.Start)..(tokens[i + 1].Start - st.Start)])) continue;
            var name = t.Text.ToLowerInvariant();
            if (AdminFunctions.Contains(name)) return (name, AnalysisRules.RejectAdminCommand);
            if (ExternalFunctions.Contains(name) || name.StartsWith("dblink", StringComparison.Ordinal)) return (name, AnalysisRules.RejectExternalRowset);
        }
        return null;
    }

    /// <summary>
    /// 문장이 닿는 트리거(활성, 감사 제외). (테이블, 연산)에서 시작해 그 테이블 트리거, FK 연쇄 자식, 트리거 쓰기 대상(연산은 모르므로 셋 다)으로 넓힌다.
    /// </summary>
    internal static IReadOnlyList<TriggerStaticInfo> Reachable(string table, ChangeOperation op, BaselineContext baseline)
    {
        var names = baseline.Catalog.Tables.ToDictionary(t => t.ObjectId, t => t.Schema + "." + t.Name);
        var ids = names.ToDictionary(x => x.Value, x => x.Key, StringComparer.Ordinal);
        var seen = new HashSet<(string, ChangeOperation)>();
        var found = new Dictionary<string, TriggerStaticInfo>(StringComparer.Ordinal);
        var queue = new Queue<(string Table, ChangeOperation Op)>();
        queue.Enqueue((table, op));
        while (queue.Count > 0)
        {
            var (t, o) = queue.Dequeue();
            if (!seen.Add((t, o))) continue;
            foreach (var tr in baseline.Triggers.Where(x => !x.IsDisabled && !x.IsAuditTrigger && x.Table == t && x.Events.Contains(o)))
            {
                found.TryAdd(tr.Trigger, tr);
                foreach (var w in tr.WriteTargets)
                    foreach (var wo in new[] { ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete })
                        queue.Enqueue((w, wo));
            }
            if (!ids.TryGetValue(t, out var id) || o == ChangeOperation.Insert) continue;
            foreach (var fk in baseline.Catalog.ForeignKeys.Where(f => f.ReferencedObjectId == id).GroupBy(f => f.ObjectId).Select(g => g.First()))
            {
                var action = o == ChangeOperation.Delete ? fk.DeleteAction : fk.UpdateAction;
                ChangeOperation? childOp = action switch
                {
                    "CASCADE" => o,
                    "SET_NULL" or "SET_DEFAULT" => ChangeOperation.Update,
                    _ => null,
                };
                if (childOp is { } co && names.TryGetValue(fk.ParentObjectId, out var child)) queue.Enqueue((child, co));
            }
        }
        return [.. found.Values.OrderBy(x => x.Trigger, StringComparer.Ordinal)];
    }

    static string StatementRule(string first, IReadOnlyList<PgToken> tokens) => first switch
    {
        "MERGE" => AnalysisRules.RejectMerge,
        "TRUNCATE" => AnalysisRules.RejectTruncate,
        "COPY" => AnalysisRules.RejectBulkInsert,
        "CALL" or "DO" or "EXECUTE" => AnalysisRules.RejectExec,
        "SET" or "RESET" => AnalysisRules.RejectSetOption,
        "SELECT" when tokens.Any(t => !t.Quoted && t.Upper == "INTO") => AnalysisRules.RejectSelectInto,
        _ when Ddl.Contains(first) => AnalysisRules.RejectDdl,
        _ when Transaction.Contains(first) => AnalysisRules.RejectTransactionControl,
        _ when Admin.Contains(first) => AnalysisRules.RejectAdminCommand,
        _ => AnalysisRules.RejectNotAllowed,
    };

    static string StatementMessage(string first) => first switch
    {
        "" => "문장 첫 낱말을 읽지 못했습니다.",
        "WITH" => "WITH(CTE) 문장은 최소판에서 받지 않습니다. INSERT·UPDATE·DELETE로 바로 써 주세요.",
        "MERGE" => "MERGE는 받지 않습니다.",
        "CALL" or "DO" or "EXECUTE" => "프로시저 호출·DO 블록은 최소판에서 받지 않습니다.",
        _ => $"{first} 문장은 받지 않습니다. INSERT INTO·UPDATE·DELETE FROM만 미리보기합니다.",
    };

    static List<Diagnostic> Dedupe(List<Diagnostic> list) =>
        list.GroupBy(d => (d.StatementIndex, d.RuleId, d.Message)).Select(g => g.First()).ToList();

    static Diagnostic Reject(int? index, SourceSpan span, string rule, string message) =>
        new() { StatementIndex = index, Span = span, Severity = Severity.Error, RuleId = rule, Message = message };

    static Diagnostic Warn(int index, SourceSpan span, string rule, string message) =>
        new() { StatementIndex = index, Span = span, Severity = Severity.Warning, RuleId = rule, Message = message };

    static SourceSpan Whole(string sql) => Span(sql, 0, sql.Length);

    static SourceSpan Span(string sql, int start, int end)
    {
        var (sl, sc) = PgSqlLexer.Position(sql, start);
        var (el, ec) = PgSqlLexer.Position(sql, end);
        return new SourceSpan { StartLine = sl, StartColumn = sc, EndLine = el, EndColumn = ec };
    }
}
