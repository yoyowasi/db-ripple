using System.Text;
using System.Text.RegularExpressions;
using DbRipple.Contracts;

namespace DbRipple.Postgres;

/// <summary>
/// 트리거 함수 본문의 보수적 정적 분석(설계 결정 7). 주석·문자열을 지운 뒤 INSERT INTO·UPDATE·DELETE FROM 대상을 모은다.
/// 동적 SQL(EXECUTE), plpgsql·sql 밖의 언어, 이름을 풀 수 없는 대상이 있으면 WriteTargetsUnknown. 결과는 원인 귀속에서 inferred 근거로만 쓴다.
/// </summary>
public static partial class PgTriggerAnalyzer
{
    [GeneratedRegex(@"\bINSERT\s+INTO\s+(?<t>(?:""[^""]+""|[A-Za-z_][\w$]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z_][\w$]*))?)", RegexOptions.IgnoreCase)]
    private static partial Regex InsertTarget();

    [GeneratedRegex(@"\bUPDATE\s+(?:ONLY\s+)?(?<t>(?:""[^""]+""|[A-Za-z_][\w$]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z_][\w$]*))?)\s+(?:AS\s+\w+\s+|\w+\s+)?SET\b", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateTarget();

    [GeneratedRegex(@"\bDELETE\s+FROM\s+(?:ONLY\s+)?(?<t>(?:""[^""]+""|[A-Za-z_][\w$]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z_][\w$]*))?)", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteTarget();

    [GeneratedRegex(@"\bEXECUTE\b", RegexOptions.IgnoreCase)]
    private static partial Regex DynamicSql();

    [GeneratedRegex(@"\bRAISE\s+(?:EXCEPTION\b|SQLSTATE\b|'|USING\b)|\bRAISE\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex Raises();

    // 2026-11-19: 따옴표 이름("pg_notify"(…), "public"."dblink_exec"(…))도 잡는다. 전에는 \b…\s*\(라 따옴표가 끼면 놓쳤다.
    [GeneratedRegex(@"(?<![\w$])""?(?<f>dblink\w*|pg_notify|lo_import|lo_export|pg_read_file|pg_read_binary_file|pg_file_write|http_\w+)""?\s*\(|\b(?<f>COPY)\b|\b(?<f>NOTIFY)\b", RegexOptions.IgnoreCase)]
    private static partial Regex External();

    [GeneratedRegex(@"[Uu]&""")]
    private static partial Regex UnicodeIdentifier();

    public static IReadOnlyList<TriggerStaticInfo> Analyze(PgCatalog pg)
    {
        var tables = pg.Tables.ToDictionary(t => t.ObjectId);
        var result = new List<TriggerStaticInfo>();
        foreach (var t in pg.Triggers)
        {
            if (!tables.TryGetValue(t.TableId, out var table)) continue;
            var body = Clean(t.FunctionSource ?? "");
            var opaque = t.FunctionSource is null || !(t.Language is "plpgsql" or "sql");
            var dynamic = DynamicSql().IsMatch(body);
            var unresolved = false;
            var targets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var re in new[] { InsertTarget(), UpdateTarget(), DeleteTarget() })
                foreach (Match m in re.Matches(body))
                {
                    var resolved = Resolve(pg, m.Groups["t"].Value, table.Schema);
                    if (resolved is null) unresolved = true;
                    else targets.Add(resolved);
                }
            var external = External().Matches(body).Select(m => m.Groups["f"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // U&"…" 식별자는 이름을 풀지 않으므로 외부 함수를 숨길 수 있어 외부 부작용으로 둔다(닫힌 쪽, 2026-11-19).
            if (UnicodeIdentifier().IsMatch(body)) external.Add("U&\"…\" 식별자(해석 안 함)");
            // 신뢰하지 않는 언어(plpython3u·plperlu·pltclu 등)는 파일·네트워크에 닿을 수 있어 본문을 보지 않고 외부 부작용으로 둔다.
            if (t.Language.StartsWith("pl", StringComparison.Ordinal) && t.Language.EndsWith('u')) external.Add("untrusted language " + t.Language);
            result.Add(new TriggerStaticInfo
            {
                Trigger = table.Schema + "." + t.Name,
                ObjectId = t.ObjectId,
                Table = table.Display,
                Events = t.Events.Select(e => e switch { "INSERT" => ChangeOperation.Insert, "UPDATE" => ChangeOperation.Update, _ => ChangeOperation.Delete })
                    .Distinct().OrderBy(o => o).ToList(),
                IsInsteadOf = t.IsInsteadOf,
                IsDisabled = t.IsDisabled,
                IsEncrypted = false,
                IsAuditTrigger = false,
                WriteTargets = [.. targets],
                WriteTargetsUnknown = opaque || dynamic || unresolved,
                HasDynamicSql = dynamic || opaque,
                HasRollback = Raises().IsMatch(body),
                ExternalSideEffects = external,
            });
        }
        return result;
    }

    /// <summary>"name" 또는 "schema.name"(인용 가능) → 카탈로그의 "스키마.테이블". 스키마가 없으면 트리거 테이블의 스키마, 그다음 public, 그다음 이름이 하나뿐인 스키마.</summary>
    static string? Resolve(PgCatalog pg, string raw, string defaultSchema)
    {
        var parts = raw.Split('.').Select(p => p.Trim()).Select(Unquote).ToList();
        if (parts.Count == 2)
            return pg.Tables.FirstOrDefault(t => t.Schema == parts[0] && t.Name == parts[1])?.Display;
        var name = parts[0];
        return (pg.Tables.FirstOrDefault(t => t.Schema == defaultSchema && t.Name == name)
                ?? pg.Tables.FirstOrDefault(t => t.Schema == "public" && t.Name == name)
                ?? (pg.Tables.Count(t => t.Name == name) == 1 ? pg.Tables.First(t => t.Name == name) : null))?.Display;
    }

    // 인용 식별자는 그대로, 아니면 PostgreSQL처럼 소문자로 접는다.
    static string Unquote(string p) => p.StartsWith('"') && p.EndsWith('"') && p.Length >= 2 ? p[1..^1].Replace("\"\"", "\"") : p.ToLowerInvariant();

    /// <summary>주석(--, /* */)과 문자열('…', $태그$…$태그$ 안쪽은 본문이 아니므로 그대로 둔다)을 지운다. 대상 오탐을 줄이려는 것이지 완전한 파서는 아니다.</summary>
    internal static string Clean(string src)
    {
        var sb = new StringBuilder(src.Length);
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c == '-' && i + 1 < src.Length && src[i + 1] == '-')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < src.Length && depth > 0)
                {
                    if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*') { depth++; i += 2; }
                    else if (src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                i--;
                sb.Append(' ');
            }
            else if (c == '\'')
            {
                i++;
                while (i < src.Length && !(src[i] == '\'' && (i + 1 >= src.Length || src[i + 1] != '\''))) i += src[i] == '\'' ? 2 : 1;
                sb.Append("''");
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
