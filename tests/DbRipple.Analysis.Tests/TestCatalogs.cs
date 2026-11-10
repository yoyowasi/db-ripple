using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Analysis.Tests;

/// <summary>손으로 만드는 최소 DbCatalog(DB 없이). 테이블은 (스키마, 이름, 열...)로 주고, 트리거는 정의 문자열로 준다. 열 이름 앞 '!'는 IDENTITY.</summary>
public sealed class CatalogBuilder
{
    readonly List<CatalogTable> tables = [];
    readonly List<CatalogColumn> columns = [];
    readonly List<CatalogTriggerEvent> triggers = [];
    int nextId = 1000;

    public CatalogBuilder Table(string schema, string name, params string[] columnNames)
    {
        var id = nextId++;
        tables.Add(new CatalogTable(id, schema, name, TemporalType.None, false));
        var cid = 1;
        foreach (var c in columnNames)
        {
            var identity = c.StartsWith('!');
            var n = identity ? c[1..] : c;
            columns.Add(new CatalogColumn(id, cid++, n, "int", false, true, false, identity, false, null));
        }
        return this;
    }

    public int IdOf(string schema, string name) => tables.First(t => t.Schema == schema && t.Name == name).ObjectId;

    /// <summary>definition이 null이면 암호화 트리거.</summary>
    public CatalogBuilder Trigger(string schema, string table, string name, string[] events, string? definition,
        bool insteadOf = false, bool disabled = false, bool isLast = false)
    {
        var parent = IdOf(schema, table);
        var oid = nextId++;
        foreach (var e in events)
            triggers.Add(new CatalogTriggerEvent(oid, name, parent, "OBJECT_OR_COLUMN", insteadOf, disabled, e, false, isLast, definition));
        return this;
    }

    public DbCatalog Build() => new(tables, columns, [], [], triggers, []);
}
