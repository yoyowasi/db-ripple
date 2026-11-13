using DbRipple.Analysis;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.Preview;

namespace DbRipple.Apply.Tests;

/// <summary>기준선 구축 전 DB 범위 DDL 트리거 위험 판정(실제 C6 TriggerAnalyzer, DB 없음). Preview·Analysis를 함께 참조하는 테스트 프로젝트라 여기에 둔다.</summary>
public sealed class DdlTriggerSafetyTests
{
    // AdventureWorks ddlDatabaseTriggerLog와 같은 모양: EVENTDATA를 dbo.DatabaseLog에 INSERT
    const string AwLog = """
        CREATE TRIGGER [ddlDatabaseTriggerLog] ON DATABASE
        FOR DDL_DATABASE_LEVEL_EVENTS AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @data XML;
            DECLARE @schema sysname;
            DECLARE @object sysname;
            DECLARE @eventType sysname;
            SET @data = EVENTDATA();
            SET @eventType = @data.value('(/EVENT_INSTANCE/EventType)[1]', 'sysname');
            SET @schema = @data.value('(/EVENT_INSTANCE/SchemaName)[1]', 'sysname');
            SET @object = @data.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname')
            IF @object IS NOT NULL
                PRINT '  ' + @eventType + ' - ' + @schema + '.' + @object;
            ELSE
                PRINT '  ' + @eventType + ' - ' + @schema;
            IF @eventType IS NULL
                PRINT CONVERT(nvarchar(max), @data);
            INSERT [dbo].[DatabaseLog] ([PostTime], [DatabaseUser], [Event], [Schema], [Object], [TSQL], [XmlEvent])
            VALUES (GETDATE(), CONVERT(sysname, CURRENT_USER), @eventType, CONVERT(sysname, @schema), CONVERT(sysname, @object),
                    @data.value('(/EVENT_INSTANCE/TSQLCommand)[1]', 'nvarchar(max)'), @data);
        END;
        """;

    const string Dynamic = """
        CREATE TRIGGER [ddlDyn] ON DATABASE FOR CREATE_USER AS
        BEGIN
            DECLARE @sql nvarchar(max) = N'INSERT dbo.DatabaseLog (Event) VALUES (N''x'')';
            EXEC sys.sp_executesql @sql;
        END;
        """;

    const string Shell = """
        CREATE TRIGGER [ddlShell] ON DATABASE FOR CREATE_TRIGGER AS
        BEGIN
            EXEC master.dbo.xp_cmdshell 'echo hi';
        END;
        """;

    static DbCatalog Catalog(params CatalogTriggerEvent[] triggers) => new(
        [new(11, "dbo", "DatabaseLog", TemporalType.None, false), new(12, "dbo", "T", TemporalType.None, false)],
        [new(11, 1, "DatabaseLogID", "int", false, false, false, true, false, null), new(11, 2, "Event", "nvarchar", false, false, false, false, false, null)],
        [], [], triggers, []);

    static CatalogTriggerEvent Ddl(int id, string name, string? body, string ev = "CREATE_USER", bool disabled = false) =>
        new(id, name, 0, "DATABASE", false, disabled, ev, false, false, body);

    // 테이블 DML 트리거(DDL 검사 대상 아님): 동적 SQL이어도 막지 않는다
    static readonly CatalogTriggerEvent DmlDynamic = new(80, "tr_T", 12, "OBJECT_OR_COLUMN", false, false, "INSERT", false, false,
        "CREATE TRIGGER dbo.tr_T ON dbo.T AFTER INSERT AS EXEC (N'SELECT 1');");

    readonly TriggerAnalyzer analyzer = new();

    [Fact]
    public void AdventureWorks_ddl_log_trigger_passes()
    {
        var cat = Catalog(Ddl(90, "ddlDatabaseTriggerLog", AwLog), Ddl(90, "ddlDatabaseTriggerLog", AwLog, "CREATE_TABLE"), DmlDynamic);
        Assert.Empty(DdlTriggerSafety.Risks(cat, analyzer));
        Assert.Null(DdlTriggerSafety.Guard(cat, analyzer, allow: false));
    }

    [Fact]
    public void Dynamic_sql_ddl_trigger_is_refused()
    {
        var risk = Assert.Single(DdlTriggerSafety.Risks(Catalog(Ddl(91, "ddlDyn", Dynamic)), analyzer));
        Assert.StartsWith("ddlDyn: ", risk);
        Assert.Contains("동적 SQL", risk);
    }

    [Fact]
    public void Xp_cmdshell_ddl_trigger_is_refused_with_setting_hint()
    {
        var reason = DdlTriggerSafety.Guard(Catalog(Ddl(90, "ddlDatabaseTriggerLog", AwLog), Ddl(92, "ddlShell", Shell)), analyzer, allow: false);
        Assert.NotNull(reason);
        Assert.Contains("ddlShell", reason);
        Assert.Contains("xp_", reason);
        Assert.DoesNotContain("ddlDatabaseTriggerLog", reason);
        Assert.Contains(DdlTriggerSafety.AllowSettingKey + "=true", reason);
    }

    [Fact]
    public void Encrypted_ddl_trigger_is_refused_and_disabled_one_is_ignored()
    {
        Assert.Contains("암호화", Assert.Single(DdlTriggerSafety.Risks(Catalog(Ddl(93, "ddlHidden", null)), analyzer)));
        Assert.Empty(DdlTriggerSafety.Risks(Catalog(Ddl(94, "ddlOff", Shell, disabled: true)), analyzer));
    }

    [Fact]
    public void Setting_allows_risky_ddl_triggers()
    {
        var cat = Catalog(Ddl(92, "ddlShell", Shell), Ddl(93, "ddlHidden", null));
        Assert.Equal(2, DdlTriggerSafety.Risks(cat, analyzer).Count);
        Assert.Null(DdlTriggerSafety.Guard(cat, analyzer, allow: true));
    }
}
