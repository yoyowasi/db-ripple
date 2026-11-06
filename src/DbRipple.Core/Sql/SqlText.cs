namespace DbRipple.Core.Sql;

/// <summary>T-SQL 이름 인용과 문자열 리터럴 이스케이프.</summary>
public static class SqlText
{
    /// <summary>[name] 인용(] → ]]).</summary>
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Quote(string schema, string name) => Quote(schema) + "." + Quote(name);

    /// <summary>N'...' 리터럴(' → '').</summary>
    public static string NLiteral(string value) => "N'" + value.Replace("'", "''") + "'";
}
