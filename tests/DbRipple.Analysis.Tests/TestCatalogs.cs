using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;

namespace DbRipple.Analysis.Tests;

/// <summary>손으로 만드는 최소 DbCatalog(DB 없이). 테이블은 (스키마, 이름, 열...)로 주고, 트리거는 정의 문자열로 준다. 열 이름 앞 '!'는 IDENTITY.</summary>
public sealed class CatalogBuilder
{
    readonly List<CatalogTable> tables = [];
    readonly List<CatalogColumn> columns = [];
    readonly List<CatalogTriggerEvent> triggers = [];
    readonly List<CatalogModule> procedures = [];
    readonly List<CatalogFunction> functions = [];
    readonly List<CatalogSynonym> synonyms = [];
    readonly List<CatalogView> views = [];
    readonly List<CatalogTableFunction> tableFunctions = [];
    readonly List<CatalogIndexedView> indexedViews = [];
    readonly List<CatalogComputedColumn> computed = [];
    readonly List<CatalogForeignKeyColumn> foreignKeys = [];
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

    /// <summary>저장 프로시저 모듈(인계 1). definition이 null이면 암호화.</summary>
    public CatalogBuilder Procedure(string schema, string name, string? definition)
    {
        procedures.Add(new CatalogModule(nextId++, schema, name, definition));
        return this;
    }

    /// <summary>FK 한 개(열 하나). child가 참조하는 쪽(sys.foreign_keys.parent_object_id), parent가 참조되는 쪽. 동작은 sys 표기(NO_ACTION, CASCADE, SET_NULL, SET_DEFAULT).</summary>
    public CatalogBuilder ForeignKey(string name, string child, string parent, string onDelete = "NO_ACTION", string onUpdate = "NO_ACTION", bool disabled = false)
    {
        var c = child.Split('.');
        var p = parent.Split('.');
        foreignKeys.Add(new CatalogForeignKeyColumn(nextId++, name, IdOf(c[0], c[1]), IdOf(p[0], p[1]), onDelete, onUpdate, disabled, false, 1, "fk", "pk"));
        return this;
    }

    /// <summary>사용자 정의 함수(2026-11-13). type은 sys.objects.type(FN·IF·TF는 T-SQL, FS·FT·AF는 CLR). definition이 null이면 암호화·CLR.</summary>
    public CatalogBuilder Function(string schema, string name, string? definition, string type = "FN", bool executeAsCaller = true)
    {
        functions.Add(new CatalogFunction(nextId++, schema, name, type, definition, executeAsCaller));
        return this;
    }

    /// <summary>함수 목록은 늘 "읽음"(빈 목록 포함)으로 만든다. 예전 기준선(함수 정보 없음)은 <c>Build() with { Functions = null }</c>.</summary>
    /// <summary>동의어(2026-11-13).</summary>
    public CatalogBuilder Synonym(string schema, string name, string baseObject)
    {
        synonyms.Add(new CatalogSynonym(schema, name, baseObject));
        return this;
    }

    /// <summary>뷰(2026-11-13). definition이 null이면 암호화.</summary>
    public CatalogBuilder View(string schema, string name, string? definition)
    {
        views.Add(new CatalogView(nextId++, schema, name, definition));
        return this;
    }

    /// <summary>테이블 계산 열·DEFAULT·CHECK가 부르는 함수(2026-11-13). database가 있으면 다른 DB 함수.</summary>
    public CatalogBuilder TableFunction(string table, string function, string? database = null)
    {
        var t = table.Split('.');
        var f = function.Split('.');
        tableFunctions.Add(new CatalogTableFunction(t[0], t[1], f.Length > 1 ? f[0] : null, f[^1], database));
        return this;
    }

    /// <summary>인덱싱된 뷰와 기준 테이블(2026-11-13). 뷰 본문은 View()로 따로 준다.</summary>
    public CatalogBuilder IndexedView(string view, string table)
    {
        var v = view.Split('.');
        var t = table.Split('.');
        indexedViews.Add(new CatalogIndexedView(v[0], v[1], t[0], t[1]));
        return this;
    }

    /// <summary>계산 열 정의(2026-11-13). table은 "스키마.테이블".</summary>
    public CatalogBuilder ComputedColumn(string table, string column, string definition)
    {
        var t = table.Split('.');
        computed.Add(new CatalogComputedColumn(t[0], t[1], column, definition));
        return this;
    }

    public DbCatalog Build() => new(tables, columns, [], foreignKeys, triggers, [], procedures, functions, synonyms, views, tableFunctions, indexedViews, computed);
}
