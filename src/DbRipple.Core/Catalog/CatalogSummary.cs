namespace DbRipple.Core.Catalog;

/// <summary>C7 카탈로그 수치 요약(설계 A24 재확인용). 트리거 수는 트리거 개체 단위다(이벤트 행이 아님).</summary>
public sealed record CatalogSummary(
    int Tables,
    int ForeignKeys,
    int DisabledForeignKeys,
    int UntrustedForeignKeys,
    IReadOnlyDictionary<string, int> OnDelete,
    IReadOnlyDictionary<string, int> OnUpdate,
    IReadOnlyList<string> ReferentialActions,
    int AfterTriggers,
    int InsteadOfTriggers,
    IReadOnlyList<string> DmlTriggers,
    IReadOnlyList<string> DdlTriggers,
    IReadOnlyList<string> TablesWithoutPrimaryKey,
    int DataFiles)
{
    public static CatalogSummary From(DbCatalog c)
    {
        var names = c.Tables.ToDictionary(t => t.ObjectId, t => t.Schema + "." + t.Name);
        string Name(int id) => names.TryGetValue(id, out var n) ? n : $"object_id {id}";

        var fks = c.ForeignKeys.GroupBy(f => f.ObjectId).Select(g => g.First()).ToList();
        var dml = c.Triggers.Where(t => t.ParentClass == "OBJECT_OR_COLUMN").GroupBy(t => t.ObjectId).ToList();
        var pkTables = c.KeyIndexes.Where(i => i.IsPrimaryKey).Select(i => i.ObjectId).ToHashSet();

        return new CatalogSummary(
            c.Tables.Count,
            fks.Count,
            fks.Count(f => f.IsDisabled),
            fks.Count(f => f.IsNotTrusted),
            CountBy(fks.Select(f => f.DeleteAction)),
            CountBy(fks.Select(f => f.UpdateAction)),
            fks.Where(f => f.DeleteAction != "NO_ACTION" || f.UpdateAction != "NO_ACTION")
                .Select(f => $"{f.Name}: {Name(f.ParentObjectId)} → {Name(f.ReferencedObjectId)} ON DELETE {f.DeleteAction}, ON UPDATE {f.UpdateAction}")
                .OrderBy(s => s, StringComparer.Ordinal).ToList(),
            dml.Count(g => !g.First().IsInsteadOf),
            dml.Count(g => g.First().IsInsteadOf),
            dml.Select(g =>
            {
                var t = g.First();
                var order = string.Concat(g.Where(x => x.IsFirst).Select(x => $" First({x.Event})")) + string.Concat(g.Where(x => x.IsLast).Select(x => $" Last({x.Event})"));
                return $"{Name(t.ParentId)}.{t.Name}: {(t.IsInsteadOf ? "INSTEAD OF" : "AFTER")} {string.Join(", ", g.Select(x => x.Event))}"
                    + (t.IsDisabled ? " 비활성" : "") + (t.IsEncrypted ? " 암호화" : "") + order;
            }).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            c.Triggers.Where(t => t.ParentClass == "DATABASE").Select(t => t.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            c.Tables.Where(t => !pkTables.Contains(t.ObjectId)).Select(t => t.Schema + "." + t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            c.Files.Count(f => f.TypeDesc == "ROWS"));
    }

    public IEnumerable<string> Lines()
    {
        yield return $"사용자 테이블: {Tables}";
        yield return $"FK: {ForeignKeys}(비활성 {DisabledForeignKeys}, 신뢰 안 됨 {UntrustedForeignKeys})";
        yield return "ON DELETE: " + Format(OnDelete);
        yield return "ON UPDATE: " + Format(OnUpdate);
        foreach (var r in ReferentialActions) yield return "  참조 동작: " + r;
        yield return $"DML 트리거: {AfterTriggers + InsteadOfTriggers}(AFTER {AfterTriggers}, INSTEAD OF {InsteadOfTriggers})";
        foreach (var t in DmlTriggers) yield return "  " + t;
        yield return $"DDL 트리거(데이터베이스): {DdlTriggers.Count}" + (DdlTriggers.Count > 0 ? " — " + string.Join(", ", DdlTriggers) : "");
        yield return $"PK 없는 테이블: {TablesWithoutPrimaryKey.Count}" + (TablesWithoutPrimaryKey.Count > 0 ? " — " + string.Join(", ", TablesWithoutPrimaryKey) : "");
        yield return $"데이터 파일(ROWS): {DataFiles}";
    }

    static IReadOnlyDictionary<string, int> CountBy(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    static string Format(IReadOnlyDictionary<string, int> counts) => string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"));
}
