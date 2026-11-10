using DbRipple.Contracts;

namespace DbRipple.Attribution;

/// <summary>행 변경에서 열 값을 꺼내는 도우미. 행 이미지가 없으면(KEYS 모드) 키로 대신할 수 있을 때만 키를 쓴다.</summary>
internal static class RowValues
{
    public static bool TryGet(IReadOnlyDictionary<string, string?> d, string column, out string? value)
    {
        if (d.TryGetValue(column, out value)) return true;
        foreach (var kv in d)
            if (string.Equals(kv.Key, column, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        value = null;
        return false;
    }

    /// <summary>
    /// before(또는 after) 이미지에서 열 값을 꺼낸다. 이미지가 없으면 DELETE의 before·INSERT의 after에 한해 키 값으로 대신한다
    /// (UPDATE의 키는 PK가 바뀌면 어느 쪽인지 정해지지 않아 쓰지 않는다, 열린 질문 Q11). 꺼낼 수 없으면 null.
    /// </summary>
    public static IReadOnlyList<string?>? Extract(RowChange rc, bool before, IReadOnlyList<string> columns)
    {
        var image = before ? rc.Before : rc.After;
        if (image is not null)
        {
            var vals = new string?[columns.Count];
            var ok = true;
            for (var i = 0; i < columns.Count && ok; i++) ok = TryGet(image, columns[i], out vals[i]);
            if (ok) return vals;
        }
        var keySide = before ? ChangeOperation.Delete : ChangeOperation.Insert;
        if (rc.Operation == keySide) return FromKey(rc.Key, columns);
        return null;
    }

    /// <summary>키 열이 원하는 열을 모두 담으면 그 값.</summary>
    public static IReadOnlyList<string?>? FromKey(RowKey key, IReadOnlyList<string> columns)
    {
        var vals = new string?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var idx = -1;
            for (var j = 0; j < key.Columns.Count; j++)
                if (string.Equals(key.Columns[j], columns[i], StringComparison.OrdinalIgnoreCase)) { idx = j; break; }
            if (idx < 0 || idx >= key.Values.Count) return null;
            vals[i] = key.Values[idx];
        }
        return vals;
    }

    public static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
}
