using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using Microsoft.Data.SqlClient;

namespace DbRipple.Diff;

/// <summary>
/// C12 최종 차이 계산기(§4 C12). 받은 세션(연결 + 있으면 트랜잭션)에서 liveDb 대 snapshotDb를 테이블마다 질의 하나로 비교한다.
/// 같은 연결이므로 열린 트랜잭션의 미커밋 변경이 보이고, 트랜잭션이 없어도(실패 뒤 검사, §5.2 4) 그대로 동작한다.
/// 연결을 닫거나 트랜잭션을 끝내지 않는다. 평가 하네스와 코드를 공유하지 않는다(§10.3: 하네스는 행 해시, 여기는 키 조인 + 바이트 비교).
/// <para>값 사전은 §6.1 문자열(C11과 같은 해석): JSON 문자열은 그 문자열, null은 null, 그 밖(숫자·true/false·객체)은 JSON 원문.</para>
/// </summary>
public sealed class NetDiffer : INetDiffer
{
    /// <summary>테이블 질의 하나의 명령 제한 시간(초). 0이면 무제한.</summary>
    public int CommandTimeoutSeconds { get; init; } = 600;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetChange>> DiffAsync(SqlSession session, string liveDb, string snapshotDb, IReadOnlyList<TableInfo> tables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDb);

        var result = new List<NetChange>();
        // 결과 순서: 테이블 표시 이름(서수), 테이블 안은 질의의 ORDER BY(키 순). 같은 테이블이 두 번 오면 한 번만.
        var ordered = tables.GroupBy(t => t.DisplayName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.DisplayName, StringComparer.Ordinal);
        foreach (var t in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (t.Columns.Count == 0) continue;
            await DiffTableAsync(session, liveDb, snapshotDb, t, result, ct);
        }
        return result;
    }

    async Task DiffTableAsync(SqlSession session, string liveDb, string snapshotDb, TableInfo t, List<NetChange> result, CancellationToken ct)
    {
        var key = TableKey.Choose(t);
        IReadOnlyList<ImageItem> items;
        var sql = key.Kind == KeyKind.ROW
            ? NetDiffSql.Multiset(t, liveDb, snapshotDb, out items)
            : NetDiffSql.Keyed(t, key.Columns, liveDb, snapshotDb, out items);

        await using var cmd = Command(session, sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (key.Kind == KeyKind.ROW)
        {
            var rowKey = new RowKey { Kind = KeyKind.ROW };
            while (await r.ReadAsync(ct))
            {
                var image = Parse(r.GetString(0), items);
                var live = r.GetInt64(1);
                var snap = r.GetInt64(2);
                result.Add(live > snap
                    ? new NetChange { Table = t.DisplayName, Key = rowKey, Change = NetChangeKind.MultisetAdded, Multiplicity = live - snap, After = image }
                    : new NetChange { Table = t.DisplayName, Key = rowKey, Change = NetChangeKind.MultisetRemoved, Multiplicity = snap - live, Before = image });
            }
            return;
        }

        while (await r.ReadAsync(ct))
        {
            var chg = r.GetInt32(0);
            var before = r.IsDBNull(1) ? null : Parse(r.GetString(1), items);
            var after = r.IsDBNull(2) ? null : Parse(r.GetString(2), items);
            switch (chg)
            {
                case 1:
                    result.Add(new NetChange { Table = t.DisplayName, Key = Key(key, after!), Change = NetChangeKind.Inserted, Multiplicity = 1, After = after });
                    break;
                case 2:
                    result.Add(new NetChange { Table = t.DisplayName, Key = Key(key, before!), Change = NetChangeKind.Deleted, Multiplicity = 1, Before = before });
                    break;
                default:
                    // 서버 바이트 비교는 상위 집합(예: 같은 텍스트의 xml 내부 표현 차이)이므로 §6.1 문자열로 최종 판정한다.
                    var changed = ChangedColumns(items, before!, after!);
                    if (changed.Count > 0)
                        result.Add(new NetChange
                        {
                            Table = t.DisplayName, Key = Key(key, after!), Change = NetChangeKind.Updated, Multiplicity = 1,
                            Before = before, After = after, ChangedColumns = changed,
                        });
                    break;
            }
        }
    }

    SqlCommand Command(SqlSession session, string sql)
    {
        // SqlClient 로컬 트랜잭션이 실패로 끝났으면(좀비: Connection == null) 그 트랜잭션을 붙이지 않는다(실패 뒤 검사).
        var tx = session.Transaction is { Connection: not null } live ? live : null;
        return new SqlCommand(sql, session.Connection, tx) { CommandTimeout = CommandTimeoutSeconds };
    }

    /// <summary>위치 별칭(p0, p1, …) JSON을 §6.1 문자열 사전(열 순서)으로 바꾼다.</summary>
    internal static Dictionary<string, string?> Parse(string json, IReadOnlyList<ImageItem> items)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, string?>(items.Count, StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
            map[items[i].Property] = doc.RootElement.TryGetProperty("p" + i, out var v) ? ToText(v) : null;
        return map;
    }

    /// <summary>§6.1 값 문자열(C11과 같은 Core AuditJson 규칙, 위험 8-2).</summary>
    internal static string? ToText(JsonElement v) => AuditJson.ToText(v);

    /// <summary>값이 다른 속성을 기본 열 이름으로 접는다(열 순서, 중복 없음).</summary>
    static List<string> ChangedColumns(IReadOnlyList<ImageItem> items, IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after)
    {
        var changed = new List<string>();
        foreach (var it in items)
            if (!string.Equals(before[it.Property], after[it.Property], StringComparison.Ordinal) && !changed.Contains(it.Column, StringComparer.Ordinal))
                changed.Add(it.Column);
        return changed;
    }

    static RowKey Key(TableKey key, IReadOnlyDictionary<string, string?> image) => new()
    {
        Kind = key.Kind,
        Columns = key.Columns,
        Values = key.Columns.Select(c => image[c]).ToList(),
    };
}
