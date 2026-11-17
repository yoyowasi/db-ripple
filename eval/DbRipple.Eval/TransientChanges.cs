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
/// <item>정의: 행 서명 = (테이블, 키, 연산, after 값). 발동 보기의 서로 다른 행 서명 가운데, 최종 차이에 같은 (테이블, 키, 연산 종류, after 값)이 없는 것의 수가 중간 변경이다.
/// 연산 종류는 INSERT=INSERTED·MULTISET_ADDED, UPDATE=UPDATED, DELETE=DELETED·MULTISET_REMOVED로 맞춘다. DELETE의 after는 없음이다.</item>
/// <item>예: 같은 행을 UPDATE한 뒤 다른 트리거가 DELETE(UPDATE 서명이 사라진다), 값이 바뀌었다가 원래 값으로 돌아옴(UPDATE 둘 다 사라진다), 중간 값을 거친 UPDATE(첫 UPDATE의 after가 최종 after와 다르다).</item>
/// <item>같은 서명이 여러 번 나오면 한 번만 센다. 최종 차이는 NetChanges ∪ ResidualCommitted다(CaseScorer의 total과 같다).</item>
/// <item>제외: 미리보기 상태가 ok가 아니면 계산하지 않는다(실패·거부·truncated는 롤백된 변경과 중간 변경이 섞인다). ImageMode가 KEYS인 묶음의 행 변경과 PK가 바뀌어 짝을 못 지은(UNPAIRED) UPDATE 반쪽은 after가 없거나 한쪽뿐이라 비교할 수 없으므로 뺀다.
/// 발동 보기가 비결정 열(RowChange.Volatile)로 보고한 열은 양쪽 after에서 뺀다(같은 실행이라도 감사 기록 값과 재조회 값이 다를 수 있다). 키는 오라클 테이블의 정렬 규칙으로 접는다(KeyFolding).</item>
/// <item>B0·B2는 최종 상태(순 변경)만 보는 시스템이라 정의상 0이고 행도 내지 않는다(해당 없음). 값은 preview 행에만 있다.</item>
/// </list>
/// </summary>
public static class TransientChanges
{
    /// <summary>계산한다. 계산 대상이 아니면(기록 없음, 상태가 ok 아님) null.</summary>
    public static TransientCounts? Compute(PreviewRecord? record, KeyFolding folding)
    {
        if (record is null || record.Result.Status != PreviewStatus.Ok) return null;
        var firings = record.Result.Firings.ToDictionary(f => f.Id, StringComparer.Ordinal);

        // 테이블별 비결정 열(발동 보기가 알려 준 것)
        var vol = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rc in record.RowChanges)
            if (rc.Volatile.Count > 0 && firings.TryGetValue(rc.FiringId, out var f))
            {
                if (!vol.TryGetValue(f.Table, out var s)) vol[f.Table] = s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in rc.Volatile) s.Add(v.Column);
            }

        string Sig(string table, string key, string op, IReadOnlyDictionary<string, string?>? after)
        {
            var v = vol.GetValueOrDefault(table);
            var cols = after is null ? "-" : string.Join(",", after.Where(p => v is null || !v.Contains(p.Key))
                .OrderBy(p => p.Key.ToUpperInvariant(), StringComparer.Ordinal).Select(p => p.Key.ToUpperInvariant() + "=" + (p.Value ?? "\u0000")));
            return table.ToUpperInvariant() + "|" + folding.Key(table, key) + "|" + op + "|" + cols;
        }

        static string KeyOf(RowKey k, IReadOnlyDictionary<string, string?>? after, IReadOnlyDictionary<string, string?>? before) =>
            k.Kind.ToString() == "ROW"
                ? RowKeys.Canonical(after ?? before ?? new Dictionary<string, string?>())
                : RowKeys.Canonical(k.Columns, k.Values);

        var events = new Dictionary<string, string>(StringComparer.Ordinal); // 서명 → 표시
        long eventRows = 0;
        foreach (var rc in record.RowChanges)
        {
            if (!firings.TryGetValue(rc.FiringId, out var f)) continue;
            eventRows++;
            if (f.ImageMode == ImageMode.Keys || rc.Pairing == Pairing.Unpaired) continue; // after가 없거나 한쪽뿐이라 비교 못 함
            var op = rc.Operation switch { ChangeOperation.Insert => "INSERT", ChangeOperation.Update => "UPDATE", ChangeOperation.Delete => "DELETE", _ => rc.Operation.ToString() };
            var key = KeyOf(rc.Key, rc.After, rc.Before);
            events.TryAdd(Sig(f.Table, key, op, op == "DELETE" ? null : rc.After), op + " " + f.Table + RowKeys.Display(folding.Key(f.Table, key)));
        }

        var finals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in record.NetChanges.Concat(record.Result.ResidualCommitted ?? []))
        {
            var op = n.Change switch
            {
                NetChangeKind.Inserted or NetChangeKind.MultisetAdded => "INSERT",
                NetChangeKind.Updated => "UPDATE",
                _ => "DELETE",
            };
            finals.Add(Sig(n.Table, KeyOf(n.Key, n.After, n.Before), op, op == "DELETE" ? null : n.After));
        }

        var transient = events.Where(e => !finals.Contains(e.Key)).Select(e => e.Value).Order(StringComparer.Ordinal).ToList();
        return new TransientCounts(eventRows, events.Count, transient.Count, transient);
    }
}
