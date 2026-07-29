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
        ListEndpointsToolName
    };

    public static async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        DatabaseMetadata metadata,
        Func<string, object?, Task<string>>? graphQlFallback = null)
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

        var tables = (metadata.Tables ?? Enumerable.Empty<TableMetadata>())
            .Select(t => new { kind = "table", database = t.Database, schema = t.Schema, name = t.Name, description = t.Description });
        var views = (metadata.Views ?? Enumerable.Empty<ViewMetadata>())
            .Select(v => new { kind = "view", database = v.Database, schema = v.Schema, name = v.Name, description = v.Description });
        var procedures = (metadata.Procedures ?? Enumerable.Empty<ProcedureMetadata>())
            .Select(p => new { kind = "procedure", database = p.Database, schema = p.Schema, name = p.Name, description = p.Description });
        var functions = (metadata.Functions ?? Enumerable.Empty<FunctionMetadata>())
            .Select(f => new { kind = "function", database = f.Database, schema = f.Schema, name = f.Name, description = f.Description });

        return tables.Concat(views).Concat(procedures).Concat(functions)
            .Where(o => Matches(o.name, query) || Matches(o.schema + "." + o.name, query) || Matches(o.description, query))
            .OrderBy(o => o.kind)
            .ThenBy(o => o.schema)
            .ThenBy(o => o.name)
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
        SearchObjectsToolName => "Tìm kiếm các đối tượng trong schema (bảng, view, procedure, function) bằng từ khóa hoặc mô tả.",
        FindColumnToolName => "Tìm column theo tên trong table/view đã cache.",
        ListEndpointsToolName => "Liệt kê SQL Server endpoints thuộc Server Objects.",
        _ => "Tool metadata request."
    };

    public static string GetToolPlannerDescription(string toolName) => NormalizeToolName(toolName) switch
    {
        ListTablesToolName => "list_tables: list available tables.",
        TableSchemaToolName => "get_table_schema: get columns and primary key for one table. Arguments: schemaName, tableName.",
        TableRelationsToolName => "get_table_relations: get foreign keys for one table. Arguments: tableName.",
        TableIndexesToolName => "get_table_indexes: get indexes for one table. Arguments: tableName.",
        SearchObjectsToolName => "search_objects: search tables, views, procedures and functions by partial name/description. Arguments: query.",
        FindColumnToolName => "find_column: search table/view columns by partial column name. Arguments: query.",
        ListEndpointsToolName => "list_endpoints: list SQL Server endpoints under Server Objects.",
        _ => toolName + ": enabled tool."
    };

    public static string GetToolApprovalReason(string toolName) => NormalizeToolName(toolName) switch
    {
        ListTablesToolName => "Reads cached table names only.",
        TableSchemaToolName => "Reads cached columns, data types and primary key information for one table.",
        TableRelationsToolName => "Reads cached foreign-key relationships for one table.",
        TableIndexesToolName => "Reads cached index metadata for one table.",
        SearchObjectsToolName => "Searches cached object names across tables, views, procedures and functions.",
        FindColumnToolName => "Searches cached table/view column names.",
        ListEndpointsToolName => "Reads cached SQL Server endpoint metadata under Server Objects.",
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
}
