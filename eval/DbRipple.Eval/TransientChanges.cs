using System.Globalization;
using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>"중간 변경" 한 사례의 계수(M6). Events = 발동 보기 행 변경 수, Measured = 비교한 서로 다른 행 서명 수, Transient = 그 가운데 최종 차이에 없는 수.</summary>
public sealed record TransientCounts(long Events, long Measured, long Transient, IReadOnlyList<string> TransientKeys)
{
    /// <summary>m6.csv 지표 칸(event_rows부터 transient_keys까지).</summary>
    public IReadOnlyList<string> CsvCells() =>
    [
        Events.ToString(CultureInfo.InvariantCulture), Measured.ToString(CultureInfo.InvariantCulture), Transient.ToString(CultureInfo.InvariantCulture),
        MetricsCsv.Rate(Transient, Measured), string.Join("; ", TransientKeys),
    ];
}

/// <summary>
/// M6 중간 변경(2026-11-17): 제품의 "발동 보기"(PreviewRecord.RowChanges, 묶음마다 일어난 그대로)에는 보이지만 "최종 차이"(실행 끝의 순 변경)에서는 사라지는 행 변경의 수.
/// "사본 + Data Compare"(B2)는 최종 상태만 비교하므로 이 정보를 원리상 볼 수 없다.
/// <list type="bullet">
/// <item>정의: 행 (테이블, 키)마다 최종 상태를 정한다. 최종 차이(NetChanges ∪ ResidualCommitted)에 그 행이 있으면 그 after(DELETED면 "없음"), 없으면 그 행의 처음 before(실행 전 상태, 원래 값으로 돌아온 경우.
/// 처음 이벤트가 INSERT면 "없음"). 발동 보기 이벤트 가운데 after(DELETE면 "없음")가 그 행의 최종 상태와 다른 것이 중간 변경이다. 연산 종류는 비교하지 않는다. 같은 (행, after) 이벤트는 한 번만 센다.</item>
/// <item>예: 같은 행을 UPDATE한 뒤 다른 트리거가 DELETE(UPDATE가 중간), 값이 바뀌었다가 원래 값으로 돌아옴(바뀐 값으로 간 이벤트만 중간), INSERT 뒤 트리거가 같은 행 UPDATE(INSERT가 중간), DELETE 뒤 같은 값 재INSERT(DELETE가 중간).</item>
/// <item>비교 열: 테이블마다 발동 보기 행 이미지와 최종 차이 이미지에 모두 있는 열만 비교한다(text·ntext·image처럼 발동 보기가 못 보는 열은 빠진다). 비결정 열은 뺀다(CaseScorer.VolatileMap 카탈로그 + 손 정답, 그리고 RowChange.Volatile).
/// 키 없는 테이블(ROW 키)의 키도 같은 비교 열 값으로 만든다. 키는 오라클 테이블의 정렬 규칙으로 접는다(KeyFolding).</item>
/// <item>분모 = 비교한 이벤트 서명 수(서로 다른 (행, after)). 계산 대상이 아닌 것: 미리보기 상태가 ok가 아닌 사례(롤백된 변경이 섞임), ImageMode가 KEYS인 묶음, PK가 바뀌어 짝을 못 지은(UNPAIRED) UPDATE 반쪽
/// (최종 차이는 옛 키 DELETED + 새 키 INSERTED라 행을 이을 수 없어 제외한다. 그 행의 변경은 이 지표에 들어가지 않는다).</item>
/// <item>B0·B2는 최종 상태(순 변경)만 보는 시스템이라 정의상 0이고 행도 내지 않는다(해당 없음). 값은 preview 행에만 있다.</item>
/// </list>
/// </summary>
public static class TransientChanges
{
    const string None = "\u0001NONE";

    /// <summary>계산한다. 계산 대상이 아니면(기록 없음, 상태가 ok 아님) null. volatileMap: 테이블 → 비결정 열(대소문자 무시).</summary>
    public static TransientCounts? Compute(PreviewRecord? record, KeyFolding folding, IReadOnlyDictionary<string, IReadOnlySet<string>> volatileMap)
    {
        if (record is null || record.Result.Status != PreviewStatus.Ok) return null;
        var firings = record.Result.Firings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var order = Reconstruction.PreorderIndex(record.Result.Firings);

        var net = record.NetChanges.Concat(record.Result.ResidualCommitted ?? []).ToList();
        var events = new List<(Firing F, RowChange Rc)>();
        long eventRows = 0;
        foreach (var rc in record.RowChanges)
        {
            if (!firings.TryGetValue(rc.FiringId, out var f)) continue;
            eventRows++;
            if (f.ImageMode == ImageMode.Keys || rc.Pairing == Pairing.Unpaired) continue;
            events.Add((f, rc));
        }
        events = events.OrderBy(e => order.TryGetValue(e.F.Id, out var o) ? (o.Statement, o.Preorder) : (e.F.StatementIndex, int.MaxValue)).ToList();

        // 테이블별 비교 열 = 발동 보기 이미지 열 ∩ 최종 차이 이미지 열 − 비결정 열
        var cols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Narrow(string table, IReadOnlyDictionary<string, string?>? image, bool first)
        {
            if (image is null) return;
            if (!cols.TryGetValue(table, out var set)) cols[table] = new HashSet<string>(image.Keys, StringComparer.OrdinalIgnoreCase);
            else set.IntersectWith(image.Keys);
        }
        foreach (var (f, rc) in events) { Narrow(f.Table, rc.After, true); Narrow(f.Table, rc.Before, true); }
        foreach (var n in net) { Narrow(n.Table, n.After, false); Narrow(n.Table, n.Before, false); }
        foreach (var (table, set) in cols)
        {
            if (volatileMap.GetValueOrDefault(table) is { } v) set.ExceptWith(v);
        }
        foreach (var (f, rc) in events)
            if (rc.Volatile.Count > 0 && cols.TryGetValue(f.Table, out var set)) foreach (var v in rc.Volatile) set.Remove(v.Column);

        string Project(string table, IReadOnlyDictionary<string, string?>? image)
        {
            if (image is null) return None;
            var c = cols.GetValueOrDefault(table) ?? [];
            return string.Join(",", image.Where(p => c.Contains(p.Key)).OrderBy(p => p.Key.ToUpperInvariant(), StringComparer.Ordinal)
                .Select(p => p.Key.ToUpperInvariant() + "=" + (p.Value ?? "\u0000")));
        }

        // 행 id = 테이블 + 키. 키 없는 테이블은 비교 열 값으로 만든다.
        string RowId(string table, RowKey k, IReadOnlyDictionary<string, string?>? after, IReadOnlyDictionary<string, string?>? before)
        {
            var key = k.Kind.ToString() == "ROW" ? Project(table, after ?? before) : folding.Key(table, RowKeys.Canonical(k.Columns, k.Values));
            return table.ToUpperInvariant() + "|" + key;
        }

        // 최종 상태: 최종 차이에 있으면 그 after, 없으면 처음 이벤트의 before
        var finals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in net)
            finals[RowId(n.Table, n.Key, n.After, n.Before)] =
                n.Change is NetChangeKind.Deleted or NetChangeKind.MultisetRemoved ? None : Project(n.Table, n.After);
        foreach (var (f, rc) in events)
        {
            var id = RowId(f.Table, rc.Key, rc.After, rc.Before);
            if (!finals.ContainsKey(id)) finals[id] = rc.Operation == ChangeOperation.Insert ? None : Project(f.Table, rc.Before);
        }

        var seen = new Dictionary<string, (string Label, bool Transient)>(StringComparer.Ordinal);
        foreach (var (f, rc) in events)
        {
            var id = RowId(f.Table, rc.Key, rc.After, rc.Before);
            var state = rc.Operation == ChangeOperation.Delete ? None : Project(f.Table, rc.After);
            var sig = id + "#" + state;
            if (seen.ContainsKey(sig)) continue;
            var op = rc.Operation.ToString().ToUpperInvariant();
            seen[sig] = (op + " " + f.Table + RowKeys.Display(id[(id.IndexOf('|') + 1)..]), state != finals[id]);
        }
        var transient = seen.Values.Where(v => v.Transient).Select(v => v.Label).Order(StringComparer.Ordinal).ToList();
        return new TransientCounts(eventRows, seen.Count, transient.Count, transient);
    }
}
