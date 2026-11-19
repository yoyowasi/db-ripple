using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.Postgres;

/// <summary>
/// 감사 행 이미지로 최종 차이를 계산한다(순수, 설계 결정 4). 스냅샷 DB가 없는 PostgreSQL 최소판의 C12 대신이다.
/// 각 묶음의 DELETED 이미지를 −1, INSERTED 이미지를 +1로 (키, 행 JSON)마다 더한다. 전이 테이블은 그 문장이 지우고 넣은 행 그대로라
/// 더하는 순서와 상관없이 "최종 − 처음"이 남는다(트리거 안 같은 행 재수정처럼 기록 순서가 실제 순서와 달라도 맞다).
/// 값 비교는 감사 함수가 만든 문자열 그대로다(같은 row_of라 같은 값은 같은 문자열).
/// KEYS 모드 묶음(행 JSON 없음)이 있는 테이블은 이 방법으로 셀 수 없어 Unresolved로 돌려준다(실행기가 재조회로 계산).
/// 한 문장 안에서 FULL·KEYS 묶음이 섞인 테이블도 묶음 하나라도 KEYS면 테이블 전체를 재조회로 넘긴다(FULL 이미지만 더하면 KEYS 묶음 변경이 빠진다).
/// </summary>
public static class ImageNetDiff
{
    public sealed record Result(IReadOnlyList<NetChange> Changes, IReadOnlyList<int> Unresolved);

    public static Result Compute(IReadOnlyList<AuditHeaderRow> headers, IReadOnlyList<AuditRowImageRow> images, IReadOnlyDictionary<int, TableInfo> tables)
    {
        var imagesBySeq = images.ToLookup(i => i.CaptureSeq);
        var changes = new List<NetChange>();
        var unresolved = new List<int>();
        foreach (var group in headers.GroupBy(h => h.TableObjectId).OrderBy(g => tables.TryGetValue(g.Key, out var t) ? t.DisplayName : "", StringComparer.Ordinal))
        {
            if (!tables.TryGetValue(group.Key, out var table) || group.Any(h => h.ImageMode == ImageMode.Keys))
            {
                unresolved.Add(group.Key);
                continue;
            }
            var key = TableKey.Choose(table);
            // 키 → (행 JSON → 개수). 키 없는 테이블은 키를 빈 문자열 하나로 둔다(다중집합).
            var delta = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            foreach (var h in group)
                foreach (var img in imagesBySeq[h.CaptureSeq])
                {
                    if (img.RowJson is null) continue;
                    var k = key.Kind == KeyKind.ROW ? "" : img.KeyJson ?? "";
                    if (!delta.TryGetValue(k, out var rows)) delta[k] = rows = new Dictionary<string, long>(StringComparer.Ordinal);
                    rows[img.RowJson] = rows.GetValueOrDefault(img.RowJson) + (img.ImageKind == ImageKind.Inserted ? 1 : -1);
                }
            changes.AddRange(key.Kind == KeyKind.ROW ? Multiset(table, delta.GetValueOrDefault("")) : Keyed(table, key, delta));
        }
        return new Result(changes, unresolved);
    }

    static IEnumerable<NetChange> Keyed(TableInfo table, TableKey key, Dictionary<string, Dictionary<string, long>> delta)
    {
        foreach (var (keyJson, rows) in delta.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var removed = rows.Where(r => r.Value < 0).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
            var added = rows.Where(r => r.Value > 0).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
            var rowKey = KeyOf(key, keyJson);
            if (removed is [{ Value: -1 } b] && added is [{ Value: 1 } a])
            {
                var before = AuditJson.ParseObject(b.Key);
                var after = AuditJson.ParseObject(a.Key);
                yield return new NetChange
                {
                    Table = table.DisplayName, Key = rowKey, Change = NetChangeKind.Updated, Multiplicity = 1,
                    Before = before, After = after, ChangedColumns = Changed(table, before, after),
                };
                continue;
            }
            // 키 있는 테이블에서는 키마다 지운 행·넣은 행이 많아야 하나씩이다. 그 밖이면 남은 행을 그대로 지움·넣음으로 보인다.
            foreach (var r in removed)
                yield return new NetChange { Table = table.DisplayName, Key = rowKey, Change = NetChangeKind.Deleted, Multiplicity = -r.Value, Before = AuditJson.ParseObject(r.Key) };
            foreach (var r in added)
                yield return new NetChange { Table = table.DisplayName, Key = rowKey, Change = NetChangeKind.Inserted, Multiplicity = r.Value, After = AuditJson.ParseObject(r.Key) };
        }
    }

    static IEnumerable<NetChange> Multiset(TableInfo table, Dictionary<string, long>? rows)
    {
        if (rows is null) yield break;
        var rowKey = new RowKey { Kind = KeyKind.ROW };
        foreach (var (json, n) in rows.Where(r => r.Value != 0).OrderBy(r => r.Key, StringComparer.Ordinal))
            yield return n < 0
                ? new NetChange { Table = table.DisplayName, Key = rowKey, Change = NetChangeKind.MultisetRemoved, Multiplicity = -n, Before = AuditJson.ParseObject(json) }
                : new NetChange { Table = table.DisplayName, Key = rowKey, Change = NetChangeKind.MultisetAdded, Multiplicity = n, After = AuditJson.ParseObject(json) };
    }

    /// <summary>키 JSON → RowKey(수집기 KeyOf와 같은 모양).</summary>
    internal static RowKey KeyOf(TableKey key, string keyJson)
    {
        var values = keyJson.Length == 0 ? new Dictionary<string, string?>() : AuditJson.ParseObject(keyJson);
        return new RowKey { Kind = key.Kind, Columns = key.Columns, Values = key.Columns.Select(c => values.GetValueOrDefault(c)).ToList() };
    }

    /// <summary>값이 다른 열(테이블 열 순서).</summary>
    internal static IReadOnlyList<string> Changed(TableInfo table, IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after) =>
        table.Columns.Select(c => c.Name)
            .Where(c => !string.Equals(before.GetValueOrDefault(c), after.GetValueOrDefault(c), StringComparison.Ordinal))
            .ToList();
}
