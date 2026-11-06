namespace DbRipple.Core.Catalog;

/// <summary>C7 sys.* 조회 SQL(읽기 전용).</summary>
public static class CatalogQueries
{
    public const string Tables = """
        SELECT t.object_id, s.name AS schema_name, t.name, t.temporal_type, t.is_memory_optimized
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name;
        """;

    // CLR 형식(hierarchyid 등)은 sys.types 이름, 그 밖은 시스템 형식명. rowversion = system_type_id 189
    public const string Columns = """
        SELECT c.object_id, c.column_id, c.name,
               CASE WHEN ty.is_assembly_type = 1 THEN ty.name ELSE TYPE_NAME(c.system_type_id) END AS system_type_name,
               CAST(CASE WHEN ty.is_assembly_type = 1 AND ty.is_user_defined = 1 THEN 1 ELSE 0 END AS bit) AS is_user_clr_type,
               c.is_nullable, c.is_computed, c.is_identity,
               CAST(CASE WHEN c.system_type_id = 189 THEN 1 ELSE 0 END AS bit) AS is_rowversion,
               dc.definition AS default_definition
        FROM sys.columns AS c
        JOIN sys.tables AS t ON t.object_id = c.object_id
        JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;
        """;

    // PK와 필터 없는 활성 유일 인덱스의 키 열
    public const string KeyIndexes = """
        SELECT i.object_id, i.index_id, i.name, i.is_primary_key, i.is_unique, ic.key_ordinal, c.name AS column_name, c.is_nullable
        FROM sys.indexes AS i
        JOIN sys.tables AS t ON t.object_id = i.object_id
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE t.is_ms_shipped = 0 AND (i.is_primary_key = 1 OR i.is_unique = 1)
          AND i.has_filter = 0 AND i.is_disabled = 0 AND ic.is_included_column = 0 AND ic.key_ordinal > 0
        ORDER BY i.object_id, i.index_id, ic.key_ordinal;
        """;

    public const string ForeignKeys = """
        SELECT fk.object_id, fk.name, fk.parent_object_id, fk.referenced_object_id,
               fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled, fk.is_not_trusted,
               fkc.constraint_column_id, pc.name AS parent_column, rc.name AS referenced_column
        FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fk.object_id, fkc.constraint_column_id;
        """;

    // 암호화 여부 = sys.sql_modules.definition NULL. DDL(데이터베이스) 트리거도 포함
    public const string Triggers = """
        SELECT tr.object_id, tr.name, tr.parent_id, tr.parent_class_desc, tr.is_instead_of_trigger, tr.is_disabled,
               te.type_desc AS event_type, te.is_first, te.is_last, m.definition
        FROM sys.triggers AS tr
        JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
        LEFT JOIN sys.sql_modules AS m ON m.object_id = tr.object_id
        ORDER BY tr.parent_id, tr.name, te.type;
        """;

    public const string Files = """
        SELECT file_id, name, type_desc, physical_name
        FROM sys.database_files
        ORDER BY file_id;
        """;

    public static IReadOnlyList<string> All => [Tables, Columns, KeyIndexes, ForeignKeys, Triggers, Files];
}
