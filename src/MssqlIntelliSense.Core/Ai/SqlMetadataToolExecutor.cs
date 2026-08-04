using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Ai;

public static class SqlMetadataToolExecutor
{
    private const int MaxObjectSearchMatches = 20;
    private const int ObjectSearchDefinitionSnippetLength = 300;

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
        return ExecuteToolCoreAsync(toolName, arguments, metadata);
    }

    public static async Task<string> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        DatabaseMetadata metadata)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return await ExecuteToolCoreAsync(toolName, document.RootElement, metadata);
    }

    private static Task<string> ExecuteToolCoreAsync(
        string toolName,
        JsonElement arguments,
        DatabaseMetadata metadata)
    {
        var normalizedTool = NormalizeToolName(toolName);
        var safeMetadata = metadata ?? DatabaseMetadata.Empty;

        var output = normalizedTool switch
        {
            ListTablesToolName => GetListTablesToolResult(safeMetadata, arguments),
            TableSchemaToolName => GetTableSchemaToolResult(safeMetadata, arguments),
            TableRelationsToolName => GetTableRelationsToolResult(safeMetadata, arguments),
            TableIndexesToolName => GetTableIndexesToolResult(safeMetadata, arguments),
            SearchObjectsToolName or SearchSchemaObjectsToolName => GetSearchObjectsToolResult(safeMetadata, arguments),
            FindColumnToolName => GetFindColumnToolResult(safeMetadata, arguments),
            ListEndpointsToolName => GetListEndpointsToolResult(safeMetadata),
            ExecuteSqlToolName => new
            {
                error = "The execute tool requires an SSMS runtime connection executor."
            },
            _ => throw new NotSupportedException($"Tool '{toolName}' is not supported.")
        };

        return Task.FromResult(JsonSerializer.Serialize(output, JsonOptions));
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
        metadata = MetadataDescriptionEditor.ApplyStoredDescriptions(metadata);
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
                    ordinal = c.Ordinal,
                    description = c.Description
                }).ToList(),
                primaryKeyColumns = table.PrimaryKeyColumns,
                description = table.Description
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

        metadata = MetadataDescriptionEditor.ApplyStoredDescriptions(metadata);
        var candidates = BuildObjectSearchCandidates(metadata).ToList();
        return BuildObjectSearchRows(candidates, query);
    }

    private static IEnumerable BuildObjectSearchRows(
        IReadOnlyList<ObjectSearchCandidate> candidates,
        string query)
    {
        return candidates
            .Select(candidate =>
            {
                var lexicalScore = ScoreObjectSearch(candidate, query);
                return new
                {
                    Candidate = candidate,
                    Score = lexicalScore,
                    LexicalScore = lexicalScore
                };
            })
            .Where(o => string.IsNullOrWhiteSpace(query) || o.LexicalScore > 0)
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
                o.Candidate.columnDescription,
                definitionSnippet = Snippet(o.Candidate.definition, ObjectSearchDefinitionSnippetLength),
                score = o.Score,
                lexicalScore = o.LexicalScore
            })
            .Take(MaxObjectSearchMatches)
            .ToList();
    }

    private static IEnumerable<ObjectSearchCandidate> BuildObjectSearchCandidates(DatabaseMetadata metadata)
    {
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
                BuildColumnDescriptionText(t.Columns)));
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
                BuildColumnDescriptionText(v.Columns)));
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
                string.Empty));
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
                string.Empty));

        return tables.Concat(views).Concat(procedures).Concat(functions);
    }

    public static IEnumerable BuildColumnSearchRows(DatabaseMetadata metadata, string query)
    {
        if (metadata == null) return Array.Empty<object>();

        metadata = MetadataDescriptionEditor.ApplyStoredDescriptions(metadata);
        var tableColumns = (metadata.Tables ?? Enumerable.Empty<TableMetadata>())
            .SelectMany(t => (t.Columns ?? Enumerable.Empty<ColumnMetadata>()).Select(c =>
            {
                return new
                {
                    kind = "table",
                    database = t.Database,
                    schema = t.Schema,
                    objectName = t.Name,
                    column = c.Name,
                    dataType = c.DataType,
                    isNullable = c.IsNullable,
                    description = c.Description
                };
            }));

        var viewColumns = (metadata.Views ?? Enumerable.Empty<ViewMetadata>())
            .SelectMany(v => (v.Columns ?? Enumerable.Empty<ColumnMetadata>()).Select(c =>
            {
                return new
                {
                    kind = "view",
                    database = v.Database,
                    schema = v.Schema,
                    objectName = v.Name,
                    column = c.Name,
                    dataType = c.DataType,
                    isNullable = c.IsNullable,
                    description = c.Description
                };
            }));

        return tableColumns.Concat(viewColumns)
            .Where(c => Matches(c.column, query) || Matches(c.description, query))
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

    public static string GetArgument(JsonElement arguments, string name, string defaultValue)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? defaultValue;
            }
        }

        return defaultValue;
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
        string columnDescription,
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
        string columnDescription)
    {
        var searchableText = string.Join(" ", new[]
        {
            kind,
            database,
            schema,
            name,
            schema + "." + name,
            description,
            columnDescription,
            keywords,
            secondaryText,
            definition
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new ObjectSearchCandidate(
            kind,
            database,
            schema,
            name,
            description,
            columnDescription,
            searchableText,
            definition);
    }

    private static string BuildColumnDescriptionText(IEnumerable<ColumnMetadata> columns)
    {
        return string.Join(" ", columns
            .Select(column => string.IsNullOrWhiteSpace(column.Description) ? string.Empty : $"{column.Name}: {column.Description}")
            .Where(description => !string.IsNullOrWhiteSpace(description)));
    }

    private static int ScoreObjectSearch(ObjectSearchCandidate candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 1;
        }

        var tokens = TokenizeObjectSearchQuery(query);

        if (tokens.Length == 0)
        {
            return 1;
        }

        var score = 0;
        foreach (var token in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tokenScore = 0;
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.name, token, exact: 120, prefix: 90, contains: 60));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.schema + "." + candidate.name, token, exact: 110, prefix: 80, contains: 55));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.description, token, exact: 95, prefix: 75, contains: 50));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.columnDescription, token, exact: 50, prefix: 35, contains: 22));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.searchableText, token, exact: 25, prefix: 18, contains: 12));
            tokenScore = Math.Max(tokenScore, ScoreField(candidate.definition, token, exact: 16, prefix: 12, contains: 8));

            score += tokenScore;
        }

        var phraseScore = ScorePhrases(candidate, tokens);
        return score == 0 && phraseScore == 0 ? 0 : score + phraseScore;
    }

    private static string[] TokenizeObjectSearchQuery(string query)
    {
        return query
            .Split(new[] { ' ', '.', '_', '-', '/', '\\', '[', ']', '(', ')', ',', ';', ':', '"', '\'', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Where(t => NormalizeSearchText(t).Length > 1)
            .ToArray();
    }

    private static int ScorePhrases(ObjectSearchCandidate candidate, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2)
        {
            return 0;
        }

        var score = 0;
        foreach (var phrase in BuildQueryPhrases(tokens, maxLength: 5))
        {
            var tokenCount = phrase.Count(c => c == ' ') + 1;
            var weight = tokenCount <= 2 ? 1 : tokenCount;
            score = Math.Max(score, ScoreNormalizedField(candidate.description, phrase, exact: 160 * weight, prefix: 130 * weight, contains: 100 * weight));
            score = Math.Max(score, ScoreNormalizedField(candidate.name, phrase, exact: 140 * weight, prefix: 110 * weight, contains: 80 * weight));
            score = Math.Max(score, ScoreNormalizedField(candidate.searchableText, phrase, exact: 80 * weight, prefix: 60 * weight, contains: 45 * weight));
        }

        return score;
    }

    private static IEnumerable<string> BuildQueryPhrases(IReadOnlyList<string> tokens, int maxLength)
    {
        var normalizedTokens = tokens
            .Select(NormalizeSearchText)
            .Where(token => token.Length > 1)
            .ToArray();

        for (var length = Math.Min(maxLength, normalizedTokens.Length); length >= 2; length--)
        {
            for (var start = 0; start <= normalizedTokens.Length - length; start++)
            {
                yield return string.Join(" ", normalizedTokens.Skip(start).Take(length));
            }
        }
    }

    private static int ScoreField(string? value, string token, int exact, int prefix, int contains)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return ScoreNormalizedField(value, token, exact, prefix, contains);
    }

    private static int ScoreNormalizedField(string? value, string token, int exact, int prefix, int contains)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
        {
            return 0;
        }

        var text = NormalizeSearchText(value);
        var normalizedToken = NormalizeSearchText(token);
        if (text.Equals(normalizedToken, StringComparison.OrdinalIgnoreCase))
        {
            return exact;
        }

        if (text.StartsWith(normalizedToken, StringComparison.OrdinalIgnoreCase))
        {
            return prefix;
        }

        return text.IndexOf(normalizedToken, StringComparison.OrdinalIgnoreCase) >= 0 ? contains : 0;
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value!;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c == 'đ' ? 'd' : c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
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
