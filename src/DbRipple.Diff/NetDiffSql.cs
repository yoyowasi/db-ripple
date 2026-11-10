using DbRipple.Core.Capture;
using DbRipple.Core.Sql;

namespace DbRipple.Diff;

/// <summary>
/// C12 질의 생성. 테이블 하나당 질의 하나로 live(현재 DB)와 snap(스냅샷)을 서버에서 비교하고, 다른 행만 §6.1 행 이미지 JSON으로 돌려준다.
/// <list type="bullet">
/// <item>키 있는 테이블(PK·UNIQUE): 키로 FULL OUTER JOIN하고, 값 비교는 바이트 단위(아래 <see cref="DiffPredicate"/>)로 한다.
/// 서버 비교는 "다르면 반드시 잡는" 상위 집합이고, 최종 판정(UPDATED·바뀐 열)은 NetDiffer가 §6.1 문자열 사전으로 한다.</item>
/// <item>키 없는 테이블(ROW): §6.1 행 이미지 JSON을 바이트(varbinary)로 묶어 양쪽 개수를 세고 개수가 다른 묶음만 돌려준다(다중집합, E6).</item>
/// </list>
/// 행 이미지 속성 이름은 위치 별칭(p0, p1, …)이다. 열 이름에 '.'이 있거나 생성 속성과 이름이 겹쳐도(SP6 JSON_KEY_DOT·CONFLICT) FOR JSON 구조가 깨지지 않는다.
/// </summary>
internal static class NetDiffSql
{
    const string JsonOptions = "FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES";

    /// <summary>
    /// SQL Server의 같음(=, IS DISTINCT FROM)이 §6.1 문자열의 같음과 일치하는 형식. 같은 열(같은 정밀도·스케일)끼리 비교하므로 값이 같으면 표현도 같다.
    /// 여기 없는 형식은 바이트로 비교한다: 문자열(대소문자 무시 데이터 정렬, 끝 공백 무시), float·real(-0 = 0),
    /// datetimeoffset(같은 순간이면 오프셋이 달라도 같음), xml·공간·CLR 형식(= 연산 없음)은 = 결과가 §6.1 표현보다 거칠기 때문이다.
    /// </summary>
    static readonly HashSet<string> ExactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney",
        "date", "datetime", "datetime2", "smalldatetime", "time", "uniqueidentifier",
    };

    /// <summary>바로 varbinary로 바꿀 수 없는 형식(오류 529 실측: text, json) → 먼저 문자열(max)로 바꾼다.</summary>
    static readonly Dictionary<string, string> ViaString = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = "varchar(max)",
        ["ntext"] = "nvarchar(max)",
        ["json"] = "nvarchar(max)",
        ["vector"] = "nvarchar(max)",
    };

    /// <summary>
    /// 열 하나의 행 이미지 항목(식, 속성 이름). Core AuditValueExpressions.Items와 같은 식이고(C11과 같은 §6.1 형식),
    /// 감사 트리거가 볼 수 없는 text·ntext·image만 C12가 직접 정한다(§6.1: 최종 차이에서만 비교). 각각 varchar(max)·nvarchar(max)·varbinary(max)(base64)로 바꾼다.
    /// 생성 속성(@srid·@type)이 실제 열 이름과 겹치면 생성 속성을 뺀다(실제 열 값이 우선, 바이트 비교는 그대로).
    /// </summary>
    public static IEnumerable<ImageItem> ImageItems(TableInfo t, string alias, bool includeComputed = true)
    {
        var realNames = t.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in t.Columns)
        {
            if (c.IsComputed && !includeComputed) continue;
            var col = alias + "." + SqlText.Quote(c.Name);
            if (AuditValueExpressions.IsUnobservable(c))
            {
                var target = c.TypeName.Equals("image", StringComparison.OrdinalIgnoreCase) ? "varbinary(max)"
                    : c.TypeName.Equals("text", StringComparison.OrdinalIgnoreCase) ? "varchar(max)" : "nvarchar(max)";
                yield return new ImageItem($"CONVERT({target}, {col})", c.Name, c.Name);
                continue;
            }
            var first = true;
            foreach (var (expr, prop) in AuditValueExpressions.Items(c, alias))
            {
                if (!first && realNames.Contains(prop)) continue;
                first = false;
                yield return new ImageItem(expr, prop, c.Name);
            }
        }
    }

    /// <summary>행 이미지 JSON 식(위치 별칭).</summary>
    static string ImageJson(IReadOnlyList<ImageItem> items) =>
        "(SELECT " + string.Join(", ", items.Select((x, i) => $"{x.Expression} AS [p{i}]")) + " " + JsonOptions + ")";

    /// <summary>값을 바이트로(문자열은 데이터 정렬과 끝 공백을, float는 부호를, datetimeoffset은 오프셋을 구분).</summary>
    static string Bytes(ColumnInfo c, string alias)
    {
        var col = alias + "." + SqlText.Quote(c.Name);
        return ViaString.TryGetValue(c.TypeName, out var s) && !c.IsUserClrType
            ? $"CAST(CAST({col} AS {s}) AS varbinary(max))"
            : $"CAST({col} AS varbinary(max))";
    }

    /// <summary>
    /// 두 행의 한 열이 다를 수 있으면 참(NULL 안전). 바이트 비교는 varbinary 비교가 끝의 0x00을 무시하므로(실측 0x01 = 0x0100) 길이도 비교한다.
    /// sql_variant는 바이트가 같아도 기저 형식이 다를 수 있어 BaseType도 비교한다.
    /// </summary>
    public static string DiffPredicate(ColumnInfo c)
    {
        var l = "l." + SqlText.Quote(c.Name);
        var s = "s." + SqlText.Quote(c.Name);
        if (!c.IsUserClrType && ExactTypes.Contains(c.TypeName)) return $"{l} IS DISTINCT FROM {s}";
        var bl = Bytes(c, "l");
        var bs = Bytes(c, "s");
        var p = $"{bl} IS DISTINCT FROM {bs} OR DATALENGTH({bl}) IS DISTINCT FROM DATALENGTH({bs})";
        if (!c.IsUserClrType && c.TypeName.Equals("sql_variant", StringComparison.OrdinalIgnoreCase))
            p += $" OR SQL_VARIANT_PROPERTY({l}, 'BaseType') IS DISTINCT FROM SQL_VARIANT_PROPERTY({s}, 'BaseType')";
        return p;
    }

    static string Table(string db, TableInfo t) => SqlText.Quote(db) + "." + t.QuotedName;

    /// <summary>
    /// 키 테이블 질의. 결과 열: chg(1 = live에만, 2 = snap에만, 3 = 양쪽, 값이 다를 수 있음), before(snap 이미지), after(live 이미지).
    /// 키 열은 NOT NULL(PK 또는 NOT NULL 유일 인덱스)이므로 조인 결과의 키 NULL이 "그쪽에 없음"이다. 정렬은 키의 SQL 순서(결정적).
    /// </summary>
    public static string Keyed(TableInfo t, IReadOnlyList<string> keyColumns, string liveDb, string snapDb, out IReadOnlyList<ImageItem> items)
    {
        items = ImageItems(t, "x").ToList();
        var after = ImageJson(ImageItems(t, "l").ToList());
        var before = ImageJson(ImageItems(t, "s").ToList());
        var k0 = SqlText.Quote(keyColumns[0]);
        var on = string.Join(" AND ", keyColumns.Select(k => $"l.{SqlText.Quote(k)} = s.{SqlText.Quote(k)}"));
        var order = string.Join(", ", keyColumns.Select(k => $"COALESCE(l.{SqlText.Quote(k)}, s.{SqlText.Quote(k)})"));
        // 계산 열은 비교 조건에서 뺀다: 저장 값이 아니라 같은 행의 다른 열에서 나오고(바뀌면 기본 열도 바뀜),
        // 비지속 계산 열은 읽기만 해도 식이 평가되어 전 행 비교 중 한 행의 오류(R18 8115)가 질의 전체를 실패시킨다.
        // 이미지(바뀐 행만 평가)에는 넣으므로 바뀐 열·값에는 계산 열이 나온다.
        var stored = t.Columns.Where(c => !c.IsComputed).ToList();
        var diff = stored.Count == 0 ? "1 = 0" : string.Join("\n       OR ", stored.Select(c => "(" + DiffPredicate(c) + ")"));
        return $"""
            SELECT CASE WHEN s.{k0} IS NULL THEN 1 WHEN l.{k0} IS NULL THEN 2 ELSE 3 END AS chg,
                   CASE WHEN s.{k0} IS NULL THEN NULL ELSE {before} END AS before_json,
                   CASE WHEN l.{k0} IS NULL THEN NULL ELSE {after} END AS after_json
            FROM {Table(liveDb, t)} AS l
            FULL OUTER JOIN {Table(snapDb, t)} AS s ON {on}
            WHERE l.{k0} IS NULL OR s.{k0} IS NULL
               OR {diff}
            ORDER BY {order}, chg;
            """;
    }

    /// <summary>
    /// 키 없는 테이블 질의. 결과 열: image(행 이미지 JSON), live_n, snap_n(같은 이미지의 개수, 다른 것만). 정렬은 이미지 바이트(결정적).
    /// 이미지 JSON은 원시 NUL을 내지 않으므로(\u0000으로 이스케이프) 끝 0x00 무시 문제가 없지만, 길이도 함께 묶어 그 가정에 기대지 않는다.
    /// </summary>
    public static string Multiset(TableInfo t, string liveDb, string snapDb, out IReadOnlyList<ImageItem> items)
    {
        // 전 행 이미지를 만들므로 계산 열은 뺀다(Keyed와 같은 이유: 파생 값, 비지속 계산 열 평가 오류). 결과 이미지에도 계산 열이 없다.
        items = ImageItems(t, "r", includeComputed: false).ToList();
        var img = ImageJson(items);
        string Side(string db) => $"""
            SELECT b, DATALENGTH(b) AS len, COUNT_BIG(*) AS n
                FROM (SELECT CAST({img} AS varbinary(max)) AS b FROM {Table(db, t)} AS r) AS x
                GROUP BY b, DATALENGTH(b)
            """;
        return $"""
            SELECT CAST(COALESCE(l.b, s.b) AS nvarchar(max)) AS image_json, ISNULL(l.n, 0) AS live_n, ISNULL(s.n, 0) AS snap_n
            FROM ({Side(liveDb)}) AS l
            FULL OUTER JOIN ({Side(snapDb)}) AS s ON l.b = s.b AND l.len = s.len
            WHERE ISNULL(l.n, 0) <> ISNULL(s.n, 0)
            ORDER BY COALESCE(l.b, s.b), COALESCE(l.len, s.len);
            """;
    }
}

/// <summary>행 이미지 항목: SQL 식, 사전 속성 이름(열 이름 또는 열@srid·열@type), 소속 열 이름(바뀐 열 접기용).</summary>
internal sealed record ImageItem(string Expression, string Property, string Column);
