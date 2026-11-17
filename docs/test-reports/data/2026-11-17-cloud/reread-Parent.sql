SELECT k.[key], CASE WHEN r.[id] IS NULL THEN NULL ELSE (SELECT r.[id] AS [id], r.[total] AS [total] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
FROM OPENJSON(@keys) AS k
LEFT JOIN [cl20].[Parent] AS r ON r.[id] = JSON_VALUE(k.[value], N'$."id"');