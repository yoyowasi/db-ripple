SELECT k.[key], CASE WHEN r.[child_id] IS NULL THEN NULL ELSE (SELECT r.[child_id] AS [child_id], r.[parent_id] AS [parent_id], r.[qty] AS [qty] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
FROM OPENJSON(@keys) AS k
LEFT JOIN [cl20].[ChildLog] AS r ON r.[child_id] = JSON_VALUE(k.[value], N'$."child_id"');