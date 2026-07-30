using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Ai;

public static class SqlMetadataToolExecutor
{
    public const string ListTablesToolName = "list_tables";
    public const string TableSchemaToolName = "get_table_schema";
    public const string TableRelationsToolName = "get_table_relations";
    public const string TableIndexesToolName = "get_table_indexes";
    public const string SearchObjectsToolName = "search_objects";
    public const string SearchSchemaObjectsToolName = "search_schema_objects";
    public const string FindColumnToolName = "find_column";
    public const string ListEndpointsToolName = "list_endpoints";
    public const string ExecuteSqlToolName = "execute";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static readonly string[] AllToolNames = new[]
    {
        ListTablesToolName,
        TableSchemaToolName,
        TableRelationsToolName,
        TableIndexesToolName,
        SearchObjectsToolName,
        SearchSchemaObjectsToolName,
        FindColumnToolName,
        ListEndpointsToolName,
        ExecuteSqlToolName
    };

    public static Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        DatabaseMetadata metadata)
    {
        return ExecuteToolAsync(toolName, arguments, metadata, graphQlFallback: null);
    }

    public static async Task<string> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        DatabaseMetadata metadata)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return await ExecuteToolAsync(toolName, document.RootElement, metadata, graphQlFallback: null);
    }

    public static async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        DatabaseMetadata metadata,
        Func<string, object?, Task<string>>? graphQlFallback)
    {
        var normalizedTool = NormalizeToolName(toolName);
        var safeMetadata = metadata ?? DatabaseMetadata.Empty;

        if ((metadata == null || metadata.Tables == null || metadata.Tables.Count == 0) && graphQlFallback != null)
        {
            switch (normalizedTool)
            {
                case ListTablesToolName:
                    return await graphQlFallback("query { tablesList { schema name } }", null);

                case TableSchemaToolName:
                    string schemaName = GetArgument(arguments, "schemaName", "dbo");
                    string tableName = GetArgument(arguments, "tableName", string.Empty);
                    return await graphQlFallback(
                        "query($schema: String!, $name: String!) { tableSchema(schema: $schema, name: $name) { schema name columns { name dataType isNullable ordinal } primaryKeyColumns } }",
                        new { schema = schemaName, name = tableName }
                    );

                case TableRelationsToolName:
                    string relTable = GetArgument(arguments, "tableName", string.Empty);
                    return await graphQlFallback(
                        "query($tableName: String!) { tableRelations(tableName: $tableName) { name fromSchema fromTable fromColumn toSchema toTable toColumn } }",
                        new { tableName = relTable }
                    );

                case TableIndexesToolName:
                    string idxTable = GetArgument(arguments, "tableName", string.Empty);
                    return await graphQlFallback(
                        "query($tableName: String!) { tableIndexes(tableName: $tableName) { schema table name isUnique columns } }",
                        new { tableName = idxTable }
                    );

                case SearchObjectsToolName:
                case SearchSchemaObjectsToolName:
                case FindColumnToolName:
                case ListEndpointsToolName:
                    return JsonSerializer.Serialize(new
                    {
                        results = Array.Empty<object>(),
                        error = "Search/endpoint tools are supported when database metadata is cached locally."
                    }, JsonOptions);

                default:
                    throw new NotSupportedException($"Tool '{toolName}' is not supported.");
            }
        }

        switch (normalizedTool)
        {
            case ListTablesToolName:
                return JsonSerializer.Serialize(GetListTablesToolResult(safeMetadata, arguments), JsonOptions);

            case TableSchemaToolName:
                return JsonSerializer.Serialize(GetTableSchemaToolResult(safeMetadata, arguments), JsonOptions);

            case TableRelationsToolName:
                return JsonSerializer.Serialize(GetTableRelationsToolResult(safeMetadata, arguments), JsonOptions);

            case TableIndexesToolName:
                return JsonSerializer.Serialize(GetTableIndexesToolResult(safeMetadata, arguments), JsonOptions);

            case SearchObjectsToolName:
            case SearchSchemaObjectsToolName:
                return JsonSerializer.Serialize(GetSearchObjectsToolResult(safeMetadata, arguments), JsonOptions);

            case FindColumnToolName:
                return JsonSerializer.Serialize(GetFindColumnToolResult(safeMetadata, arguments), JsonOptions);

            case ListEndpointsToolName:
                return JsonSerializer.Serialize(GetListEndpointsToolResult(safeMetadata), JsonOptions);

            case ExecuteSqlToolName:
                return JsonSerializer.Serialize(new
                {
                    error = "The execute tool requires an SSMS runtime connection executor."
                }, JsonOptions);

            default:
                throw new NotSupportedException($"Tool '{toolName}' is not supported.");
        }
    }

    public static object GetListTablesToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        if (metadata?.Tables == null) return new { tablesList = Array.Empty<object>(), totalCount = 0, truncated = false };

        var schemaFilter = GetArgument(arguments, "schemaName", string.Empty);
        var queryFilter = GetArgument(arguments, "query", string.Empty);
        var tableNameFilter = GetArgument(arguments, "tableName", string.Empty);
        var query = !string.IsNullOrWhiteSpace(queryFilter) ? queryFilter : tableNameFilter;

        var source = metadata.Tables.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(t => Matches(t.Name, query) || Matches(t.Schema + "." + t.Name, query));
        }

        var allMatching = source.Select(t => new { database = t.Database, schema = t.Schema, name = t.Name }).ToList();
        var totalCount = allMatching.Count;
        var truncated = totalCount > 500;
        var tablesList = truncated ? allMatching.Take(500).ToList() : allMatching;

        return new { tablesList, totalCount, truncated };
    }

    public static object GetTableSchemaToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var schemaName = GetArgument(arguments, "schemaName", "dbo");
        var tableName = GetArgument(arguments, "tableName", string.Empty);
        var table = metadata?.FindTable(schemaName, tableName);
        if (table == null)
        {
            return new { error = "Table not found.", schemaName, tableName };
        }

        return new
        {
            tableSchema = new
            {
                database = table.Database,
                schema = table.Schema,
                name = table.Name,
                columns = table.Columns.Select(c => new
                {
                    name = c.Name,
                    dataType = c.DataType,
                    isNullable = c.IsNullable,
                    ordinal = c.Ordinal
                }).ToList(),
                primaryKeyColumns = table.PrimaryKeyColumns
            }
        };
    }

    public static object GetTableRelationsToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var tableName = GetArgument(arguments, "tableName", string.Empty);
        if (metadata?.ForeignKeys == null) return Array.Empty<object>();

        return metadata.ForeignKeys.Where(fk =>
                fk.FromTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) ||
                fk.ToTable.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            .Select(fk => new
            {
                name = fk.Name,
                fromSchema = fk.FromSchema,
                fromTable = fk.FromTable,
                fromColumn = fk.FromColumn,
                toSchema = fk.ToSchema,
                toTable = fk.ToTable,
                toColumn = fk.ToColumn
            }).ToList();
    }

    public static object GetTableIndexesToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var tableName = GetArgument(arguments, "tableName", string.Empty);
        if (metadata?.Indexes == null) return Array.Empty<object>();

        return metadata.Indexes.Where(idx => idx.Table.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            .Select(idx => new
            {
                schema = idx.Schema,
                table = idx.Table,
                name = idx.Name,
                isUnique = idx.IsUnique,
                columns = idx.Columns
            }).ToList();
    }

    public static object GetSearchObjectsToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var query = GetArgument(arguments, "query", GetArgument(arguments, "tableName", string.Empty));
        var matches = BuildObjectSearchRows(metadata, query);
        return new { query, matches };
    }

    public static object GetFindColumnToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var query = GetArgument(arguments, "query", GetArgument(arguments, "columnName", string.Empty));
        var matches = BuildColumnSearchRows(metadata, query);
        return new { query, matches };
    }

    public static object GetListEndpointsToolResult(DatabaseMetadata metadata)
    {
        var endpoints = metadata?.Endpoints != null
            ? metadata.Endpoints.OrderBy(ep => ep.Name).Select(ep => new { ep.Name, ep.Type, ep.Protocol, ep.State, ep.Port }).ToList()
            : (object)Array.Empty<object>();

        return new { endpoints };
    }

    public static IEnumerable? BuildPreviewRows(string toolName, DatabaseMetadata metadata, string schemaName, string tableName, string query)
    {
        if (metadata == null) return null;

        var normalized = NormalizeToolName(toolName);
        return normalized switch
        {
            ListTablesToolName => BuildListTablesPreviewRows(metadata, schemaName, query),
            TableSchemaToolName => metadata.FindTable(schemaName, tableName)?.Columns?.Select(c => new { c.Ordinal, c.Name, c.DataType, c.IsNullable })?.ToList(),
            TableRelationsToolName => (metadata.ForeignKeys ?? Enumerable.Empty<ForeignKeyMetadata>())
                .Where(fk => fk.FromTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) || fk.ToTable.Equals(tableName, StringComparison.OrdinalIgnoreCase)).ToList(),
            TableIndexesToolName => (metadata.Indexes ?? Enumerable.Empty<IndexMetadata>())
                .Where(idx => idx.Table.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                .Select(idx => new { idx.Schema, idx.Table, idx.Name, idx.IsUnique, Columns = string.Join(", ", idx.Columns) }).ToList(),
            SearchObjectsToolName or SearchSchemaObjectsToolName => BuildObjectSearchRows(metadata, query),
            FindColumnToolName => BuildColumnSearchRows(metadata, query),
            ListEndpointsToolName => (metadata.Endpoints ?? Enumerable.Empty<EndpointInfo>())
                .OrderBy(ep => ep.Name).Select(ep => new { ep.Name, ep.Type, ep.Protocol, ep.State, ep.Port }).ToList(),
            ExecuteSqlToolName => Array.Empty<object>(),
            _ => null
        };
    }

    public static IEnumerable BuildListTablesPreviewRows(DatabaseMetadata metadata, string schemaName, string query)
    {
        if (metadata?.Tables == null) return Array.Empty<object>();

        var source = metadata.Tables.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(t => Matches(t.Name, query) || Matches(t.Schema + "." + t.Name, query));
        }

        return source.Select(t => new { t.Database, t.Schema, t.Name }).Take(500).ToList();
    }

    public static IEnumerable BuildObjectSearchRows(DatabaseMetadata metadata, string query)
    {
        if (metadata == null) return Array.Empty<object>();

        var customDescriptions = ObjectDescriptionStore.LoadAll();

        var tables = (metadata.Tables ?? Enumerable.Empty<TableMetadata>())
            .Select(t => CreateObjectSearchCandidate(
                "table",
                t.Database,
                t.Schema,
                t.Name,
                t.Description,
                t.Keywords,
                string.Join(" ", t.Columns.Select(c => c.Name)),
                string.Empty,
                customDescriptions));
        var views = (metadata.Views ?? Enumerable.Empty<ViewMetadata>())
            .Select(v => CreateObjectSearchCandidate(
                "view",
                v.Database,
                v.Schema,
                v.Name,
                v.Description,
                v.Keywords,
                string.Join(" ", v.Columns.Select(c => c.Name)),
                v.Definition,
                customDescriptions));
        var procedures = (metadata.Procedures ?? Enumerable.Empty<ProcedureMetadata>())
            .Select(p => CreateObjectSearchCandidate(
                "procedure",
                p.Database,
                p.Schema,
                p.Name,
                p.Description,
                p.Keywords,
                string.Join(" ", p.Parameters.Select(param => param.Name)),
                p.Definition,
                customDescriptions));
        var functions = (metadata.Functions ?? Enumerable.Empty<FunctionMetadata>())
            .Select(f => CreateObjectSearchCandidate(
                "function",
                f.Database,
                f.Schema,
                f.Name,
                f.Description,
                f.Keywords,
                string.Join(" ", f.Parameters.Select(param => param.Name)),
                f.Definition,
                customDescriptions));

        return tables.Concat(views).Concat(procedures).Concat(functions)
            .Select(o => new { Candidate = o, Score = ScoreObjectSearch(o, query) })
            .Where(o => string.IsNullOrWhiteSpace(query) || o.Score > 0)
            .OrderByDescending(o => o.Score)
            .ThenBy(o => o.Candidate.kind)
            .ThenBy(o => o.Candidate.schema)
            .ThenBy(o => o.Candidate.name)
            .Select(o => new
            {
                o.Candidate.kind,
                o.Candidate.database,
                o.Candidate.schema,
                o.Candidate.name,
                o.Candidate.description,
                o.Candidate.customDescription,
                definitionSnippet = Snippet(o.Candidate.definition, 1000),
                score = o.Score
            })
            .Take(100)
            .ToList();
    }

    public static IEnumerable BuildColumnSearchRows(DatabaseMetadata metadata, string query)
    {
        if (metadata == null) return Array.Empty<object>();

        var tableColumns = (metadata.Tables ?? Enumerable.Empty<TableMetadata>())
            .SelectMany(t => (t.Columns ?? Enumerable.Empty<ColumnMetadata>()).Select(c => new
            {
                kind = "table",
                database = t.Database,
                schema = t.Schema,
                objectName = t.Name,
                column = c.Name,
                dataType = c.DataType,
                isNullable = c.IsNullable
            }));

        var viewColumns = (metadata.Views ?? Enumerable.Empty<ViewMetadata>())
            .SelectMany(v => (v.Columns ?? Enumerable.Empty<ColumnMetadata>()).Select(c => new
            {
                kind = "view",
                database = v.Database,
                schema = v.Schema,
                objectName = v.Name,
                column = c.Name,
                dataType = c.DataType,
                isNullable = c.IsNullable
            }));

        return tableColumns.Concat(viewColumns)
            .Where(c => Matches(c.column, query))
            .OrderBy(c => c.schema)
            .ThenBy(c => c.objectName)
            .ThenBy(c => c.column)
            .Take(150)
            .ToList();
    }

    public static string NormalizeToolName(string toolName)
    {
        if (string.Equals(toolName, SearchSchemaObjectsToolName, StringComparison.OrdinalIgnoreCase))
        {
            return SearchObjectsToolName;
        }

        return toolName ?? string.Empty;
    }

    public static string GetToolDescription(string toolName) => NormalizeToolName(toolName) switch
    {
        ListTablesToolName => "Liệt kê danh sách table từ schema cache hoặc database metadata.",
        TableSchemaToolName => "Đọc column, kiểu dữ liệu và primary key của một table.",
        TableRelationsToolName => "Đọc foreign key/relationship liên quan đến table.",
        TableIndexesToolName => "Đọc index metadata liên quan đến table.",
        SearchObjectsToolName => "Tìm kiếm thông minh các đối tượng trong schema (bảng, view, procedure, function) bằng tên, cột, định nghĩa SQL và mô tả tùy chỉnh cho agent.",
        FindColumnToolName => "Tìm column theo tên trong table/view đã cache.",
        ListEndpointsToolName => "Liệt kê SQL Server endpoints thuộc Server Objects.",
        ExecuteSqlToolName => "Thực thi câu SQL read-only trên connection/database đang chọn trong SSMS. Chỉ dùng cho SELECT/WITH/DECLARE và metadata query an toàn.",
        _ => "Tool metadata request."
    };

    public static string GetToolPlannerDescription(string toolName) => NormalizeToolName(toolName) switch
    {
        ListTablesToolName => "list_tables: list available tables.",
        TableSchemaToolName => "get_table_schema: get columns and primary key for one table. Arguments: schemaName, tableName.",
        TableRelationsToolName => "get_table_relations: get foreign keys for one table. Arguments: tableName.",
        TableIndexesToolName => "get_table_indexes: get indexes for one table. Arguments: tableName.",
        SearchObjectsToolName => "search_objects: weighted search across tables, views, procedures and functions by object name, columns/parameters, SQL definition and custom agent descriptions. Arguments: query.",
        FindColumnToolName => "find_column: search table/view columns by partial column name. Arguments: query.",
        ListEndpointsToolName => "list_endpoints: list SQL Server endpoints under Server Objects.",
        ExecuteSqlToolName => "execute: run a read-only SQL query against the active SSMS connection/database after explicit user approval. Arguments: query. Only use SELECT/WITH/DECLARE or safe metadata procedures.",
        _ => toolName + ": enabled tool."
    };

    public static string GetToolApprovalReason(string toolName) => NormalizeToolName(toolName) switch
    {
        ListTablesToolName => "Reads cached table names only.",
        TableSchemaToolName => "Reads cached columns, data types and primary key information for one table.",
        TableRelationsToolName => "Reads cached foreign-key relationships for one table.",
        TableIndexesToolName => "Reads cached index metadata for one table.",
        SearchObjectsToolName => "Searches cached object names, columns/parameters, SQL definitions and custom agent descriptions.",
        FindColumnToolName => "Searches cached table/view column names.",
        ListEndpointsToolName => "Reads cached SQL Server endpoint metadata under Server Objects.",
        ExecuteSqlToolName => "Runs a read-only SQL query against the active SSMS connection/database. DML, DDL and unsafe EXEC calls are blocked.",
        _ => "Reads cached metadata for this chat session."
    };

    public static string GetArgument(JsonElement arguments, string name, string fallback)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? fallback;
            }
        }

        return fallback;
    }

    private static bool Matches(string? value, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private sealed record ObjectSearchCandidate(
        string kind,
        string database,
        string schema,
        string name,
        string description,
        string customDescription,
        string searchableText,
        string definition);

    private static ObjectSearchCandidate CreateObjectSearchCandidate(
        string kind,
        string database,
        string schema,
        string name,
        string description,
        string keywords,
        string secondaryText,
        string definition,
        IReadOnlyDictionary<string, string> customDescriptions)
    {
        var key = ObjectDescriptionStore.BuildKey(kind, database, schema, name);
        customDescriptions.TryGetValue(key, out var customDescription);
        customDescription ??= string.Empty;

        var searchableText = string.Join(" ", new[]
        {
            kind,
            database,
            schema,
            name,
            schema + "." + name,
            description,
            customDescription,
            keywords,
            secondaryText,
            definition
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new ObjectSearchCandidate(
            kind,
            database,
            schema,
            name,
            string.IsNullOrWhiteSpace(customDescription) ? description : customDescription,
            customDescription,
            searchableText,
            definition);
    }

    private static int ScoreObjectSearch(ObjectSearchCandidate candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 1;
        }

        var tokens = query
            .Split(new[] { ' ', '.', '_', '-', '/', '\\', '[', ']', '(', ')', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
        {
            return 1;
        }

        var score = 0;
        foreach (var token in tokens)
        {
            var tokenScore = 0;
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.name, token, exact: 120, prefix: 90, contains: 60));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.schema + "." + candidate.name, token, exact: 110, prefix: 80, contains: 55));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.customDescription, token, exact: 95, prefix: 75, contains: 50));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.description, token, exact: 50, prefix: 35, contains: 22));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.searchableText, token, exact: 25, prefix: 18, contains: 12));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.definition, token, exact: 16, prefix: 12, contains: 8));

            if (tokenScore == 0)
            {
                return 0;
            }

            score += tokenScore;
        }

        return score;
    }

    private static int ScoreField(string? value, string token, int exact, int prefix, int contains)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var text = value!;
        if (text.Equals(token, StringComparison.OrdinalIgnoreCase))
        {
            return exact;
        }

        if (text.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return prefix;
        }

        return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ? contains : 0;
    }

    private static string Snippet(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value!;
        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength) + "...";
    }
}
