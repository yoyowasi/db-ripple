using System.Globalization;
using System.Text.Json;

namespace DbRipple.Eval;

/// <summary>M1 IDENTITY 짝 매핑 결과(E2). Rows = 정답 쪽 IDENTITY 키 INSERTED 행 수, Paired = 예측 행과 짝지은 수.</summary>
public sealed record IdentityAlignment(IReadOnlyList<OracleRowChange> Truth, long Rows, long Paired, IReadOnlyList<string> Unpaired);

/// <summary>
/// M1 IDENTITY 짝 매핑(§10.4 "IDENTITY는 짝 매핑 뒤 비교", E2). 제품 C15 ApplyMatcher와 독립 구현이다(AD7, 코드 공유 없음).
/// 오라클 원본은 롤백해도 IDENTITY 값이 되돌아가지 않아 샌드박스(기준선에서 되돌림)와 새 IDENTITY 값이 어긋난다. 그래서 정답 쪽을 예측 쪽 값으로 옮긴 뒤 셀을 비교한다.
/// <list type="bullet">
/// <item>대상: 키에 IDENTITY 열이 있는 테이블(OracleTable.IdentityKeyColumns)의 INSERTED 행.</item>
/// <item>순서: FK로 부모 테이블부터(자기 참조는 무시). 서로 참조하면 남은 것은 이름 순.</item>
/// <item>짝 서명: after 값 가운데 IDENTITY 열, 비결정 열(VolatileColumns), 아직 짝을 안 지은 IDENTITY 테이블을 참조하는 FK 열을 뺀 나머지. 이미 짝지은 부모를 참조하는 FK 열은
/// 정답 값을 짝 매핑으로 바꾼 뒤 비교한다(D7 "자식 FK 값은 짝 매핑으로 치환").</item>
/// <item>서명이 같은 행이 여럿이면 양쪽 모두 IDENTITY 값 오름차순으로 차례대로 짝짓는다.</item>
/// <item>다시 쓰기: 짝지은 정답 행의 키를 예측 키로, IDENTITY 열 값을 예측 값으로 바꾼다. 모든 정답 행에서 IDENTITY 테이블을 참조하는 FK 열 값(before·after·키)이
/// 짝 매핑에 있으면 바꾼다. 짝을 못 지은 행은 그대로 둬서 비교에서 fn·fp로 남는다.</item>
/// <item>extraRefs: FK가 아닌데 새 IDENTITY 값을 담는 열(손 정답 identityRefs, 예: CL12c 트리거가 읽은 @@IDENTITY를 적는 IdentSeen.seen_identity).
/// 테이블 → 열 → 참조 IDENTITY 테이블. FK와 같은 방식으로 치환한다(참조 테이블의 첫 IDENTITY 키 열).</item>
/// </list>
/// </summary>
public static class IdentityPairing
{
    /// <summary>정답(truth)을 예측(predicted)의 IDENTITY 값으로 옮긴다. tables는 오라클이 읽은 테이블(FK 포함).</summary>
    public static IdentityAlignment Align(IReadOnlyList<OracleRowChange> truth, IReadOnlyList<OracleRowChange> predicted, IReadOnlyList<OracleTable> tables,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? extraRefs = null)
    {
        var byName = tables.ToDictionary(t => t.Display, StringComparer.OrdinalIgnoreCase);
        // 손 정답 identityRefs를 FK처럼 다룬다(열 하나짜리 가상 FK).
        if (extraRefs is { Count: > 0 })
            foreach (var (table, cols) in extraRefs)
                if (byName.TryGetValue(table, out var ct))
                {
                    var add = cols.Where(c => byName.TryGetValue(c.Value, out var rt) && rt.IdentityKeyColumns.Count > 0
                                              && !ct.ForeignKeys.Any(f => f.Columns.Count == 1 && string.Equals(f.Columns[0], c.Key, StringComparison.OrdinalIgnoreCase)))
                        .Select(c => new OracleForeignKey("hand:" + c.Key, [c.Key], byName[c.Value].Display, [byName[c.Value].IdentityKeyColumns[0]]))
                        .ToList();
                    if (add.Count > 0) byName[table] = ct with { ForeignKeys = [.. ct.ForeignKeys, .. add] };
                }
        tables = byName.Values.ToList();
        var idTables = tables.Where(t => t.IdentityKeyColumns.Count > 0).ToList();
        if (idTables.Count == 0) return new IdentityAlignment(truth, 0, 0, []);

        // (테이블, IDENTITY 열) → 정답 값 → 예측 값
        var valueMap = new Dictionary<(string Table, string Column), Dictionary<string, string?>>();
        var keyMap = new Dictionary<(string Table, string Key), string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unpaired = new List<string>();
        long rows = 0, paired = 0;

        foreach (var t in Order(idTables))
        {
            var idCols = t.IdentityKeyColumns;
            var excluded = new HashSet<string>(t.VolatileColumns, StringComparer.OrdinalIgnoreCase);
            foreach (var c in idCols) excluded.Add(c);
            var translate = new Dictionary<string, (string Table, string Column)>(StringComparer.OrdinalIgnoreCase);
            foreach (var fk in t.ForeignKeys)
            {
                if (!byName.TryGetValue(fk.ReferencedTable, out var parent) || parent.IdentityKeyColumns.Count == 0) continue;
                for (var i = 0; i < fk.Columns.Count; i++)
                {
                    var refCol = fk.ReferencedColumns[i];
                    if (!parent.IdentityKeyColumns.Contains(refCol, StringComparer.OrdinalIgnoreCase)) continue;
                    if (done.Contains(parent.Display) && !string.Equals(parent.Display, t.Display, StringComparison.OrdinalIgnoreCase))
                        translate[fk.Columns[i]] = (parent.Display.ToUpperInvariant(), refCol.ToUpperInvariant());
                    else excluded.Add(fk.Columns[i]);
                }
            }

            string Signature(OracleRowChange r, bool fromTruth) =>
                RowKeys.Canonical((r.After ?? new Dictionary<string, string?>())
                    .Where(kv => !excluded.Contains(BaseColumn(kv.Key)))
                    .Select(kv => new KeyValuePair<string, string?>(kv.Key,
                        fromTruth && translate.TryGetValue(kv.Key, out var tc) && kv.Value is not null
                            && valueMap.TryGetValue(tc, out var m) && m.TryGetValue(kv.Value, out var mapped) ? mapped : kv.Value)));

            var predQueues = predicted
                .Where(r => r.Change == ChangeKinds.Inserted && string.Equals(r.Table, t.Display, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => IdOrder(r, idCols[0])).ThenBy(r => r.Key, StringComparer.Ordinal)
                .GroupBy(r => Signature(r, false), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new Queue<OracleRowChange>(g), StringComparer.Ordinal);
            var truthRows = truth
                .Where(r => r.Change == ChangeKinds.Inserted && string.Equals(r.Table, t.Display, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => IdOrder(r, idCols[0])).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
            foreach (var r in truthRows)
            {
                rows++;
                if (!predQueues.TryGetValue(Signature(r, true), out var q) || q.Count == 0) { unpaired.Add(r.Display); continue; }
                var p = q.Dequeue();
                paired++;
                keyMap[(t.Display.ToUpperInvariant(), r.Key)] = p.Key;
                foreach (var c in idCols)
                {
                    var tv = Get(r.After, c);
                    if (tv is null) continue;
                    var k = (t.Display.ToUpperInvariant(), c.ToUpperInvariant());
                    if (!valueMap.TryGetValue(k, out var m)) valueMap[k] = m = new Dictionary<string, string?>(StringComparer.Ordinal);
                    m[tv] = Get(p.After, c);
                }
            }
            done.Add(t.Display);
        }

        // FK 열 → 참조하는 IDENTITY (테이블, 열)
        var fkTranslate = new Dictionary<string, Dictionary<string, (string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
            foreach (var fk in t.ForeignKeys)
                for (var i = 0; i < fk.Columns.Count; i++)
                {
                    var k = (fk.ReferencedTable.ToUpperInvariant(), fk.ReferencedColumns[i].ToUpperInvariant());
                    if (!valueMap.ContainsKey(k)) continue;
                    if (!fkTranslate.TryGetValue(t.Display, out var d)) fkTranslate[t.Display] = d = new(StringComparer.OrdinalIgnoreCase);
                    d[fk.Columns[i]] = k;
                }

        IReadOnlyDictionary<string, string?>? Rewrite(string table, IReadOnlyDictionary<string, string?>? values)
        {
            if (values is null) return null;
            var own = byName.TryGetValue(table, out var ot) ? ot.IdentityKeyColumns : [];
            fkTranslate.TryGetValue(table, out var fks);
            var changed = false;
            var d = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (c, v) in values)
            {
                var nv = v;
                if (v is not null && own.Contains(c, StringComparer.OrdinalIgnoreCase)
                    && valueMap.TryGetValue((table.ToUpperInvariant(), c.ToUpperInvariant()), out var m1) && m1.TryGetValue(v, out var x1)) nv = x1;
                else if (v is not null && fks is not null && fks.TryGetValue(c, out var target) && valueMap[target].TryGetValue(v, out var x2)) nv = x2;
                changed |= !string.Equals(nv, v, StringComparison.Ordinal);
                d[c] = nv;
            }
            return changed ? d : values;
        }

        string RewriteKey(string table, string key)
        {
            if (keyMap.TryGetValue((table.ToUpperInvariant(), key), out var pk)) return pk;
            if (!fkTranslate.TryGetValue(table, out var fks)) return key;
            List<List<string?>>? pairs;
            try { pairs = JsonSerializer.Deserialize<List<List<string?>>>(key); }
            catch (JsonException) { return key; }
            if (pairs is null || pairs.Count == 0 || pairs.Any(p => p.Count != 2 || p[0] is null)) return key;
            return RowKeys.Canonical(pairs.Select(p => new KeyValuePair<string, string?>(p[0]!,
                p[1] is not null && fks.TryGetValue(p[0]!, out var target) && valueMap[target].TryGetValue(p[1]!, out var x) ? x : p[1])));
        }

        var rewritten = truth.Select(r =>
        {
            if (byName.TryGetValue(r.Table, out var tt) && tt.KeyKind == OracleKeyKind.ROW) return r;
            var key = RewriteKey(r.Table, r.Key);
            var before = Rewrite(r.Table, r.Before);
            var after = Rewrite(r.Table, r.After);
            return ReferenceEquals(before, r.Before) && ReferenceEquals(after, r.After) && key == r.Key ? r : r with { Key = key, Before = before, After = after };
        }).ToList();
        return new IdentityAlignment(rewritten, rows, paired, unpaired);
    }

    static IEnumerable<OracleTable> Order(List<OracleTable> idTables)
    {
        var names = idTables.Select(t => t.Display).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = idTables.OrderBy(t => t.Display, StringComparer.Ordinal).ToList();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(t => t.ForeignKeys.All(fk => !names.Contains(fk.ReferencedTable)
                || string.Equals(fk.ReferencedTable, t.Display, StringComparison.OrdinalIgnoreCase) || emitted.Contains(fk.ReferencedTable))) ?? left[0];
            emitted.Add(next.Display);
            left.Remove(next);
            yield return next;
        }
    }

    static (int, decimal) IdOrder(OracleRowChange r, string column) =>
        Get(r.After, column) is { } v && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (0, d) : (1, 0m);

    static string? Get(IReadOnlyDictionary<string, string?>? d, string column)
    {
        if (d is null) return null;
        if (d.TryGetValue(column, out var v)) return v;
        foreach (var (k, x) in d)
            if (string.Equals(k, column, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    static string BaseColumn(string c) => c.IndexOf('@') is var i and > 0 ? c[..i] : c;
}
