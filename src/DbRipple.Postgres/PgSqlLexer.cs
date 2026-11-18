using System.Text;

namespace DbRipple.Postgres;

/// <summary>문장 한 개(원문 위치 포함).</summary>
public sealed record PgStatementText(string Sql, int Start, int End, string Cleaned);

/// <summary>낱말(식별자·키워드·인용 식별자)과 원문 위치.</summary>
public sealed record PgToken(string Text, bool Quoted, int Start, int End)
{
    /// <summary>키워드 비교용 대문자(인용 식별자는 그대로).</summary>
    public string Upper => Quoted ? Text : Text.ToUpperInvariant();
}

/// <summary>
/// PostgreSQL SQL 문장 나누기(설계 결정 6). 세미콜론으로 나누되 '…'(E'…' 포함), "…", $태그$…$태그$, -- 주석, 중첩 /* */ 주석 안은 건너뛴다.
/// 완전한 파서가 아니며, 허용 판단은 첫 낱말과 대상 테이블만 본다. 끝나지 않은 인용·주석은 오류로 돌려준다.
/// </summary>
public static class PgSqlLexer
{
    /// <summary>문장들로 나눈다. Cleaned는 주석을 공백으로, 문자열·달러 인용 안쪽을 공백으로 바꾼 같은 길이 문자열(위치가 원문과 같다).</summary>
    public static (IReadOnlyList<PgStatementText> Statements, string? Error, int ErrorAt) Split(string sql)
    {
        var cleaned = new StringBuilder(sql.Length);
        var result = new List<PgStatementText>();
        var start = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                var s = i;
                while (i < sql.Length && sql[i] != '\n') i++;
                cleaned.Append(' ', i - s);
                continue;
            }
            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                var s = i;
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && Peek(sql, i + 1) == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && Peek(sql, i + 1) == '/') { depth--; i += 2; }
                    else i++;
                }
                if (depth > 0) return ([], "닫히지 않은 /* 주석", s);
                AppendBlank(cleaned, sql, s, i);
                continue;
            }
            if (c == '\'')
            {
                var s = i;
                var backslash = s > 0 && (sql[s - 1] is 'E' or 'e') && (s < 2 || !IsIdentChar(sql[s - 2]));
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (backslash && sql[i] == '\\') { i += 2; continue; }
                    if (sql[i] == '\'')
                    {
                        if (Peek(sql, i + 1) == '\'') { i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return ([], "닫히지 않은 문자열(')", s);
                cleaned.Append('\'');
                AppendBlank(cleaned, sql, s + 1, i - 1);
                cleaned.Append('\'');
                continue;
            }
            if (c == '"')
            {
                var s = i;
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == '"')
                    {
                        if (Peek(sql, i + 1) == '"') { i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return ([], "닫히지 않은 인용 식별자(\")", s);
                cleaned.Append(sql, s, i - s); // 인용 식별자는 낱말이므로 그대로 둔다
                continue;
            }
            if (c == '$' && (i == 0 || !IsIdentChar(sql[i - 1])) && DollarTag(sql, i) is { } tag)
            {
                var s = i;
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                if (close < 0) return ([], "닫히지 않은 달러 인용(" + tag + ")", s);
                i = close + tag.Length;
                cleaned.Append('\'');
                AppendBlank(cleaned, sql, s + 1, i - 1);
                cleaned.Append('\'');
                continue;
            }
            if (c == ';')
            {
                Add(result, sql, cleaned.ToString(), start, i);
                cleaned.Append(' ');
                i++;
                start = i;
                continue;
            }
            cleaned.Append(c);
            i++;
        }
        Add(result, sql, cleaned.ToString(), start, sql.Length);
        return (result, null, -1);
    }

    /// <summary>문장의 낱말들(Cleaned 기준, 숫자·연산자는 건너뜀).</summary>
    public static IReadOnlyList<PgToken> Tokens(PgStatementText st, int max = int.MaxValue)
    {
        var list = new List<PgToken>();
        var s = st.Cleaned;
        var i = 0;
        while (i < s.Length && list.Count < max)
        {
            var c = s[i];
            if (c == '"')
            {
                var b = i;
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    if (s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    sb.Append(s[i++]);
                }
                list.Add(new PgToken(sb.ToString(), true, st.Start + b, st.Start + i));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var b = i;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                list.Add(new PgToken(s[b..i], false, st.Start + b, st.Start + i));
            }
            else if (c == '.')
            {
                list.Add(new PgToken(".", false, st.Start + i, st.Start + i + 1));
                i++;
            }
            else if (c == '(')
            {
                list.Add(new PgToken("(", false, st.Start + i, st.Start + i + 1));
                i++;
            }
            else i++;
        }
        return list;
    }

    /// <summary>원문 위치 → (줄, 열), 1부터.</summary>
    public static (int Line, int Column) Position(string sql, int offset)
    {
        int line = 1, col = 1;
        for (var k = 0; k < offset && k < sql.Length; k++)
        {
            if (sql[k] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }

    static void Add(List<PgStatementText> result, string sql, string cleaned, int start, int end)
    {
        // 앞뒤 공백·주석만 있는 조각은 문장이 아니다.
        var cs = start;
        var ce = end;
        while (cs < ce && char.IsWhiteSpace(cleaned[cs])) cs++;
        while (ce > cs && char.IsWhiteSpace(cleaned[ce - 1])) ce--;
        if (cs >= ce) return;
        result.Add(new PgStatementText(sql[cs..ce], cs, ce, cleaned[cs..ce]));
    }

    static void AppendBlank(StringBuilder sb, string sql, int from, int to)
    {
        for (var k = from; k < to; k++) sb.Append(sql[k] == '\n' ? '\n' : ' ');
    }

    static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>$태그$(태그는 비어도 됨)면 그 태그 문자열, 아니면 null(예 $1 매개변수).</summary>
    static string? DollarTag(string s, int i)
    {
        var j = i + 1;
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
        if (j >= s.Length || s[j] != '$') return null;
        var tag = s[i..(j + 1)];
        return tag.Length > 2 && char.IsDigit(tag[1]) ? null : tag;
    }
}
