using DbRipple.Contracts;
using DbRipple.Core.Catalog;

namespace DbRipple.Postgres;

/// <summary>
/// FK 연쇄 묶음의 깊이를 SQL Server 모양으로 맞춘다(순수, 설계 결정 5).
/// PostgreSQL은 ON DELETE/UPDATE 동작을 부모 테이블의 행 단위 AFTER 트리거(RI)에서 자식 테이블에 대한 별도 문장으로 실행하므로
/// 자식 감사 묶음이 부모보다 한 단계 깊게(pg_trigger_depth() + 1) 기록된다. 기존 원인 판정(FkJudge H2)은 FK 연쇄 자식이 부모와 같은 깊이라고 보므로,
/// FK로만 설명되는 자식 묶음(과 그 아래 묶음)의 깊이를 한 단계 올린다.
/// 전제(SP22에서 실측 예정): 감사 묶음은 후위 순서(자식이 부모보다 먼저 기록)다. 부모를 후위로 못 찾으면 앞쪽에서 찾는다.
/// 부모 테이블에 같은 이벤트로 자식 테이블에 쓸 수 있는 사용자 트리거가 있으면(쓰기 대상 모름 포함) 출처가 모호하므로 그대로 둔다.
/// </summary>
public static class PgFkDepth
{
    /// <param name="headers">감사 헤더(captureSeq 순).</param>
    /// <param name="statementHighs">문장별 경계(마지막 captureSeq, 오름차순). 깊이 판단은 문장을 넘지 않는다.</param>
    public static IReadOnlyList<AuditHeaderRow> Normalize(
        IReadOnlyList<AuditHeaderRow> headers, IReadOnlyList<long> statementHighs, DbCatalog catalog, IReadOnlyList<TriggerStaticInfo> triggers)
    {
        var names = catalog.Tables.ToDictionary(t => t.ObjectId, t => t.Schema + "." + t.Name);
        var result = new List<AuditHeaderRow>(headers.Count);
        var low = long.MinValue;
        foreach (var high in statementHighs.Append(long.MaxValue))
        {
            var part = headers.Where(h => h.CaptureSeq > low && h.CaptureSeq <= high).OrderBy(h => h.CaptureSeq).ToList();
            result.AddRange(NormalizeStatement(part, (c, p) => FkCaused(c, p, catalog, triggers, names)));
            low = high;
        }
        return result;
    }

    /// <summary>한 문장의 묶음(captureSeq 순). fkCaused(자식, 부모)가 참인 간선마다 그 아래 전체의 깊이를 하나 줄인다.</summary>
    internal static IReadOnlyList<AuditHeaderRow> NormalizeStatement(IReadOnlyList<AuditHeaderRow> h, Func<AuditHeaderRow, AuditHeaderRow, bool> fkCaused)
    {
        var n = h.Count;
        var parent = new int[n];
        Array.Fill(parent, -1);
        // 후위 순서 부모(수집기 Bracket과 같은 규칙): i 앞에서 깊이가 i 이하인 마지막 묶음 j 다음부터 i 앞까지, 깊이 d+1인 묶음의 부모는 i.
        for (var i = 0; i < n; i++)
        {
            var d = h[i].NestLevel;
            var j = i - 1;
            while (j >= 0 && h[j].NestLevel > d) j--;
            for (var k = j + 1; k < i; k++)
                if (h[k].NestLevel == d + 1 && parent[k] < 0) parent[k] = i;
        }
        // 후위 부모가 없으면 앞쪽의 가장 가까운 깊이 d−1 묶음(전위 순서일 때).
        for (var k = 0; k < n; k++)
        {
            if (parent[k] >= 0 || h[k].NestLevel < 2) continue;
            for (var j = k - 1; j >= 0; j--)
            {
                if (h[j].NestLevel == h[k].NestLevel - 1) { parent[k] = j; break; }
                if (h[j].NestLevel < h[k].NestLevel - 1) break;
            }
        }
        // 얕은 깊이부터: shift(자식) = shift(부모) + (FK 간선이면 1).
        var shift = new int[n];
        foreach (var k in Enumerable.Range(0, n).OrderBy(k => h[k].NestLevel))
            if (parent[k] is var p and >= 0)
                shift[k] = shift[p] + (fkCaused(h[k], h[p]) ? 1 : 0);
        return h.Select((x, k) => shift[k] == 0 ? x : x with { NestLevel = Math.Max(1, x.NestLevel - shift[k]) }).ToList();
    }

    /// <summary>자식 묶음이 부모 묶음의 FK 동작으로만 설명되는지.</summary>
    internal static bool FkCaused(AuditHeaderRow child, AuditHeaderRow parent, DbCatalog catalog, IReadOnlyList<TriggerStaticInfo> triggers, IReadOnlyDictionary<int, string> names)
    {
        var matches = catalog.ForeignKeys
            .Where(f => f.ParentObjectId == child.TableObjectId && f.ReferencedObjectId == parent.TableObjectId)
            .GroupBy(f => f.ObjectId).Select(g => g.First())
            .Any(f => (parent.Operation, child.Operation) switch
            {
                (ChangeOperation.Delete, ChangeOperation.Delete) => f.DeleteAction == "CASCADE",
                (ChangeOperation.Delete, ChangeOperation.Update) => f.DeleteAction is "SET_NULL" or "SET_DEFAULT",
                (ChangeOperation.Update, ChangeOperation.Update) => f.UpdateAction is "CASCADE" or "SET_NULL" or "SET_DEFAULT",
                _ => false,
            });
        if (!matches || !names.TryGetValue(parent.TableObjectId, out var parentName) || !names.TryGetValue(child.TableObjectId, out var childName)) return false;
        return !triggers.Any(t => !t.IsDisabled && !t.IsAuditTrigger && t.Table == parentName && t.Events.Contains(parent.Operation)
                                  && (t.WriteTargetsUnknown || t.WriteTargets.Contains(childName)));
    }
}
