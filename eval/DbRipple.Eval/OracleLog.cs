using System.Text.Json;

namespace DbRipple.Eval;

/// <summary>
/// 1주차 오라클 기록(cascadelab/verification/logs/oracle-2026-11-10.json)과의 대조. 하네스 오라클이 1주차 독립 도구와 같은 결과를 내는지
/// (테스트 지원의 시드 실행기를 제품과 함께 쓰는 위험, docs/week2-plan.md §8 8번)를 확인한다.
/// 비교 항목: 사례 상태, 문장별 상태·오류 번호, 테이블별 키 열, INSERTED·DELETED 키 집합, UPDATED 키·바뀐 열·바뀐 열의 before/after 값.
/// </summary>
public static class OracleLog
{
    /// <summary>"문장이 종료되었습니다" 안내(심각도 0). XACT_ABORT OFF에서만 오류 목록에 붙는다.</summary>
    public const int StatementTerminated = 3621;

    /// <summary>파일의 사례 id → 사례 JSON(복제본).</summary>
    public static IReadOnlyDictionary<string, JsonElement> LoadCases(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("Cases").EnumerateArray()
            .ToDictionary(c => c.GetProperty("Id").GetString()!, c => c.Clone(), StringComparer.Ordinal);
    }

    /// <summary>차이 목록(비면 같음).</summary>
    public static IReadOnlyList<string> Diff(JsonElement logCase, OracleResult actual)
    {
        var d = new List<string>();
        var status = logCase.GetProperty("Status").GetString();
        if (status != actual.Status) d.Add($"status: 기록 {status}, 실제 {actual.Status}");

        var logSt = logCase.GetProperty("Statements").EnumerateArray().ToList();
        if (logSt.Count != actual.Statements.Count) d.Add($"문장 수: 기록 {logSt.Count}, 실제 {actual.Statements.Count}");
        foreach (var (ls, i) in logSt.Select((x, i) => (x, i)).Where(p => p.i < actual.Statements.Count))
        {
            var a = actual.Statements[i];
            if (ls.GetProperty("Status").GetString() != a.Status) d.Add($"문장 {a.Index} 상태: 기록 {ls.GetProperty("Status").GetString()}, 실제 {a.Status}");
            // 3621(문장 종료 안내)은 XACT_ABORT OFF에서만 붙는다. 1주차 기록은 OFF, 지금 오라클은 제품 봉투와 같은 ON이라 비교에서 뺀다(2026-11-10).
            var nums = ls.GetProperty("Errors").EnumerateArray().Select(e => e.GetProperty("Number").GetInt32()).Where(n => n != StatementTerminated).ToList();
            if (!nums.SequenceEqual(a.ErrorNumbers.Where(n => n != StatementTerminated))) d.Add($"문장 {a.Index} 오류: 기록 [{string.Join(",", nums)}], 실제 [{string.Join(",", a.ErrorNumbers)}]");
        }

        var tables = actual.Tables.ToDictionary(t => t.Display, StringComparer.OrdinalIgnoreCase);
        var actualByTable = actual.Changes.GroupBy(c => c.Table, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var logTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nc in logCase.GetProperty("NetChanges").EnumerateArray())
        {
            var table = nc.GetProperty("Table").GetString()!;
            logTables.Add(table);
            var keyCols = nc.GetProperty("KeyColumns").EnumerateArray().Select(x => x.GetString()!).ToList();
            if (!tables.TryGetValue(table, out var meta)) { d.Add($"{table}: 실제 카탈로그에 없음"); continue; }
            if (!keyCols.SequenceEqual(meta.KeyColumns, StringComparer.OrdinalIgnoreCase))
                d.Add($"{table} 키 열: 기록 [{string.Join(",", keyCols)}], 실제 [{string.Join(",", meta.KeyColumns)}]");
            var act = actualByTable.GetValueOrDefault(table) ?? [];
            CompareKeys(d, table, "inserted", Keys(nc.GetProperty("Inserted")), act.Where(c => c.Change == ChangeKinds.Inserted).Select(c => c.Key));
            CompareKeys(d, table, "deleted", Keys(nc.GetProperty("Deleted")), act.Where(c => c.Change == ChangeKinds.Deleted).Select(c => c.Key));
            var upd = act.Where(c => c.Change == ChangeKinds.Updated).ToDictionary(c => c.Key, StringComparer.Ordinal);
            var logUpd = nc.GetProperty("Updated").EnumerateArray().ToList();
            CompareKeys(d, table, "updated", logUpd.Select(u => KeyOf(u.GetProperty("Key").GetString()!)), upd.Keys);
            foreach (var u in logUpd)
            {
                if (!upd.TryGetValue(KeyOf(u.GetProperty("Key").GetString()!), out var a)) continue;
                var cols = u.GetProperty("ChangedColumns").EnumerateArray().Select(x => x.GetString()!).ToList();
                if (!cols.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(a.ChangedColumns))
                    d.Add($"{a.Display} 바뀐 열: 기록 [{string.Join(",", cols)}], 실제 [{string.Join(",", a.ChangedColumns)}]");
                foreach (var (side, logSide, vals) in new[] { ("before", u.GetProperty("Before"), a.Before), ("after", u.GetProperty("After"), a.After) })
                    foreach (var p in logSide.EnumerateObject())
                    {
                        var expected = RowKeys.JsonValueText(p.Value);
                        string? got = null;
                        var has = vals is not null && vals.TryGetValue(p.Name, out got);
                        if (!has || !string.Equals(expected, got, StringComparison.Ordinal))
                            d.Add($"{a.Display}.{p.Name} {side}: 기록 {expected ?? "NULL"}, 실제 {(has ? got ?? "NULL" : "(없음)")}");
                    }
            }
        }
        foreach (var t in actualByTable.Keys.Where(t => !logTables.Contains(t)))
            d.Add($"{t}: 기록에 없는 변경 {actualByTable[t].Count}건");
        d.AddRange(actual.Changes.Where(c => c.Change is ChangeKinds.MultisetAdded or ChangeKinds.MultisetRemoved).Select(c => $"{c.Display}: 기록 형식에 없는 {c.Change}"));
        return d;
    }

    static string KeyOf(string jsonObject)
    {
        using var doc = JsonDocument.Parse(jsonObject);
        return RowKeys.FromJsonObject(doc.RootElement);
    }

    static IEnumerable<string> Keys(JsonElement arr) => arr.EnumerateArray().Select(x => KeyOf(x.GetString()!));

    static void CompareKeys(List<string> d, string table, string what, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = expected.ToHashSet(StringComparer.Ordinal);
        var a = actual.ToHashSet(StringComparer.Ordinal);
        if (!e.SetEquals(a))
            d.Add($"{table} {what}: 기록 {string.Join(" ", e.Order().Select(RowKeys.Display))}, 실제 {string.Join(" ", a.Order().Select(RowKeys.Display))}");
    }
}
