using DbRipple.Contracts;

namespace DbRipple.Eval;

/// <summary>
/// E_cap 예측(§10.4 "미리보기 재구성"): 제품의 행 변경(PreviewRecord.RowChanges)만으로 순 변경을 접는다. 제품 C11·C12 코드를 쓰지 않는 하네스 독립 구현이다.
/// <list type="bullet">
/// <item>순서: 문장 번호 → 그 문장의 트리거 문맥 트리 전위 순서(C11 최종 after 규칙, §7.2 6단계). 트리는 captureSeq 순(후위 순서, H1)과 depth로 세운다:
/// 묶음을 captureSeq 순으로 보며 스택 위의 더 깊은 묶음을 자식으로 거두고 자신을 쌓는다. 남은 스택이 문장의 루트들(같은 순서)이다.</item>
/// <item>(테이블, 키)마다 처음 before와 최종 after를 쓴다. 행 변경의 before 이미지는 before 키의 행을 없애고 after 이미지는 after 키의 행을 둔다(PK가 바뀐 UPDATE는 옛 키 삭제 + 새 키 삽입).</item>
/// <item>처음 없던 키가 끝에도 없으면(INSERT 뒤 DELETE) 상쇄, 양쪽에 있으면 값이 다른 열만 UPDATED(원래대로 돌아온 열은 빠짐). 키 없는 테이블(ROW)은 행 값별 개수 차이로 MULTISET_*.</item>
/// <item>짝을 못 지은 UPDATE(UNPAIRED)의 반쪽 이미지는 before만 있으면 삭제, after만 있으면 삽입처럼 접는다(CL04, 2026-11-10 통합 때 고침).</item>
/// <item>KEYS 모드(이미지 없음)는 키 값만 안다: DELETE는 그대로, INSERT는 키 열만 after로, UPDATE는 바뀐 열을 알 수 없어 변경을 만들지 않는다(그 셀은 fn).</item>
/// <item>최종 차이(NetChanges)는 쓰지 않는다. 잔차로만 설명된 셀은 여기 나오지 않는다. 재조회로 고친 after(FinalAfterSource = REREAD)의 고친 열(잔차 Columns)도 빼서
/// residual_filled로 센다(2026-11-10 검토: 재조회 값이 들어가면 E_cap이 차이 엔진끼리의 비교가 된다).</item>
/// </list>
/// </summary>
public static class Reconstruction
{
    /// <summary>기록의 행 변경을 접은 순 변경(하네스 형식, 테이블·키 서수 순). record가 null이면 빈 목록.</summary>
    public static IReadOnlyList<OracleRowChange> FromRecord(PreviewRecord? record) =>
        record is null ? [] : Fold(record.Result.Firings, record.RowChanges, record.Result.Residuals);

    /// <summary>
    /// 묶음과 행 변경으로 접는다. residuals는 재조회로 after를 고친 행 변경(FinalAfterSource = REREAD)의 고친 열을 찾는 데 쓴다(Residual.RowChangeId·Columns).
    /// 그 열의 셀은 감사 기록이 아니라 재조회(최종 차이와 같은 값)에서 왔으므로 cap 예측에서 뺀다. 고친 열을 모르면 그 행의 after 셀을 모두 뺀다.
    /// </summary>
    public static IReadOnlyList<OracleRowChange> Fold(IReadOnlyList<Firing> firings, IReadOnlyList<RowChange> rowChanges, IReadOnlyList<Residual>? residuals = null)
    {
        var rereadColumns = (residuals ?? []).Where(r => r.RowChangeId is not null)
            .GroupBy(r => r.RowChangeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.SelectMany(r => r.Columns).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var order = PreorderIndex(firings);
        var tableOf = firings.ToDictionary(f => f.Id, f => f.Table, StringComparer.Ordinal);
        var events = rowChanges
            .Select((rc, i) => (rc, i, pos: order.TryGetValue(rc.FiringId, out var p) ? p : (int.MaxValue, int.MaxValue)))
            .OrderBy(x => x.pos.Item1).ThenBy(x => x.pos.Item2).ThenBy(x => x.i) // 묶음 안 순서는 의미 없음(키가 겹치지 않는다)
            .Select(x => x.rc).ToList();

        var keyed = new Dictionary<(string Table, string Key), (Row? First, Row? Current)>();
        var multiset = new Dictionary<(string Table, string Key), (long Delta, IReadOnlyDictionary<string, string?> Values)>();

        foreach (var rc in events)
        {
            var table = tableOf.GetValueOrDefault(rc.FiringId) ?? throw new InvalidOperationException($"행 변경 {rc.Id}: 묶음 {rc.FiringId} 없음");
            if (rc.Key.Kind.ToString() == "ROW")
            {
                if (rc.Before is { } b) Bump(multiset, table, b, -1);
                if (rc.After is { } a) Bump(multiset, table, a, +1);
                continue;
            }
            var op = CaseScorer.ContractEnumText(rc.Operation);
            if (op == "UPDATE" && rc.Before is null && rc.After is null) continue; // KEYS 모드 UPDATE: 바뀐 값을 모른다
            // 짝을 못 지은 UPDATE(UNPAIRED, PK를 바꾼 여러 행 UPDATE)는 반쪽 이미지다: before만 있으면 그 키 행을 없애기만, after만 있으면 두기만 한다.
            if (op == "UPDATE" && rc.Pairing == Pairing.Unpaired) op = rc.After is null ? "DELETE" : rc.Before is null ? "INSERT" : op;
            if (op != "INSERT")
            {
                var kb = KeyOf(rc.Key, rc.Before);
                var cur = keyed.TryGetValue((table, kb), out var s) ? s : (First: rc.Before is null ? KeyOnly(rc.Key) : new Row(rc.Before), Current: (Row?)null);
                keyed[(table, kb)] = (cur.First, null);
            }
            if (op != "DELETE")
            {
                var after = rc.After is null ? KeyOnly(rc.Key) : new Row(rc.After);
                if (rc.FinalAfterSource == FinalAfterSource.Reread && rc.After is not null)
                    after = after with { Tainted = rereadColumns.TryGetValue(rc.Id, out var cols) && cols.Count > 0 ? cols : rc.After.Keys.ToHashSet(StringComparer.Ordinal) };
                var ka = KeyOf(rc.Key, rc.After);
                var first = keyed.TryGetValue((table, ka), out var s2) ? s2.First : null; // 처음 본 키가 after면 원래 없던 행
                keyed[(table, ka)] = (first, after);
            }
        }

        var result = new List<OracleRowChange>();
        foreach (var ((table, key), (first, current)) in keyed)
        {
            if (first is null && current is null) continue;
            if (first is null)
            {
                var values = current!.Tainted is { } t ? current.Values.Where(kv => !t.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) : current.Values;
                if (values.Count > 0) result.Add(new OracleRowChange(table, key, ChangeKinds.Inserted, 1, null, values, []));
                continue;
            }
            if (current is null) { result.Add(new OracleRowChange(table, key, ChangeKinds.Deleted, 1, first.Values, null, [])); continue; }
            // 한쪽이 키만 알면 양쪽 다 아는 열만 비교한다
            var columns = first.IsKeyOnly || current.IsKeyOnly
                ? first.Values.Keys.Intersect(current.Values.Keys, StringComparer.Ordinal)
                : first.Values.Keys.Union(current.Values.Keys, StringComparer.Ordinal);
            var changed = columns
                .Where(c => !(first.Values.TryGetValue(c, out var x) && current.Values.TryGetValue(c, out var y) && string.Equals(x, y, StringComparison.Ordinal)))
                .Where(c => current.Tainted is null || !current.Tainted.Contains(c)) // 재조회에서 온 셀은 cap 예측에 넣지 않는다
                .ToList();
            if (changed.Count > 0) result.Add(new OracleRowChange(table, key, ChangeKinds.Updated, 1, first.Values, current.Values, changed));
        }
        foreach (var ((table, key), (delta, values)) in multiset)
        {
            if (delta > 0) result.Add(new OracleRowChange(table, key, ChangeKinds.MultisetAdded, delta, null, values, []));
            else if (delta < 0) result.Add(new OracleRowChange(table, key, ChangeKinds.MultisetRemoved, -delta, values, null, []));
        }
        return result.OrderBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Change, StringComparer.Ordinal).ToList();
    }

    /// <summary>묶음 id → (문장 번호, 그 문장 안 전위 순번).</summary>
    public static IReadOnlyDictionary<string, (int Statement, int Preorder)> PreorderIndex(IReadOnlyList<Firing> firings)
    {
        var index = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var st in firings.GroupBy(f => f.StatementIndex))
        {
            var stack = new List<Node>();
            foreach (var f in st.OrderBy(f => f.CaptureSeq))
            {
                var node = new Node(f);
                var i = stack.Count;
                while (i > 0 && stack[i - 1].Firing.Depth > f.Depth) i--;
                node.Children.AddRange(stack.Skip(i));
                stack.RemoveRange(i, stack.Count - i);
                stack.Add(node);
            }
            var n = 0;
            void Visit(Node x)
            {
                index[x.Firing.Id] = (st.Key, n++);
                foreach (var c in x.Children) Visit(c);
            }
            foreach (var root in stack) Visit(root);
        }
        return index;
    }

    sealed record Node(Firing Firing)
    {
        public List<Node> Children { get; } = [];
    }

    /// <summary>행 상태(이미지 값). IsKeyOnly면 KEYS 모드라 키 열 값만 안다. Tainted는 재조회에서 온 열(cap 예측에서 뺀다).</summary>
    sealed record Row(IReadOnlyDictionary<string, string?> Values, bool IsKeyOnly = false, IReadOnlySet<string>? Tainted = null);

    static Row KeyOnly(RowKey k) => new(k.Columns.Select((c, i) => (c, v: k.Values[i])).ToDictionary(x => x.c, x => x.v, StringComparer.Ordinal), true);

    // 이미지가 있으면 이미지의 키 열 값으로(PK가 바뀐 UPDATE의 before/after 키가 다르다), 없으면 RowKey 값으로.
    static string KeyOf(RowKey k, IReadOnlyDictionary<string, string?>? image) =>
        image is not null && k.Columns.All(image.ContainsKey)
            ? RowKeys.Canonical(k.Columns.Select(c => new KeyValuePair<string, string?>(c, image[c])))
            : RowKeys.Canonical(k.Columns, k.Values);

    static void Bump(Dictionary<(string, string), (long, IReadOnlyDictionary<string, string?>)> d, string table, IReadOnlyDictionary<string, string?> row, int by)
    {
        var key = (table, RowKeys.Canonical(row));
        d[key] = d.TryGetValue(key, out var cur) ? (cur.Item1 + by, cur.Item2) : (by, row);
    }
}
