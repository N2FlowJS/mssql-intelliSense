using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;
using MssqlIntelliSense.Core.Ai;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

internal sealed class ChatAgentToolRuntimeContext
{
    public DatabaseMetadata Metadata { get; set; } = DatabaseMetadata.Empty;
    public string ActiveConnectionString { get; set; } = string.Empty;
    public string? ActiveDatabase { get; set; }
}

internal sealed class ChatAgentToolDefinition
{
    public ChatAgentToolDefinition(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = description;
        InputSchemaJson = inputSchemaJson;
    }

    public string Name { get; }
    public string Description { get; }
    public string InputSchemaJson { get; }

    public ChatTool ToChatTool() =>
        ChatTool.CreateFunctionTool(
            functionName: Name,
            functionDescription: Description,
            functionParameters: BinaryData.FromString(InputSchemaJson),
            functionSchemaIsStrict: false);
}

internal static class ChatAgentToolRegistry
{
    private static readonly IReadOnlyDictionary<string, ChatAgentToolDefinition> Definitions =
        BuildDefinitions().ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ChatTool> CreateChatTools(ISet<string> allowedToolNames)
    {
        if (allowedToolNames == null || allowedToolNames.Count == 0)
        {
            return Array.Empty<ChatTool>();
        }

        return allowedToolNames
            .Select(SqlMetadataToolExecutor.NormalizeToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Definitions.ContainsKey)
            .Select(toolName => Definitions[toolName].ToChatTool())
            .ToList();
    }

    public static ISet<string> SelectRelevantToolNames(ISet<string> allowedToolNames, string userMessage)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowedToolNames == null || allowedToolNames.Count == 0)
        {
            return selected;
        }

        var text = userMessage ?? string.Empty;
        var lower = text.ToLowerInvariant();
        AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.SearchObjectsToolName);

        if (ContainsAny(lower, "column", "columns", "field", "cột", "cot"))
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.FindColumnToolName);
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.TableSchemaToolName);
        }

        if (ContainsAny(lower, "schema", "datatype", "data type", "kiểu dữ liệu", "kieu du lieu", "primary key", "pk"))
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.TableSchemaToolName);
        }

        if (ContainsAny(lower, "relation", "relationship", "foreign key", "join", "khóa ngoại", "khoa ngoai", "quan hệ", "quan he"))
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.TableRelationsToolName);
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.TableSchemaToolName);
        }

        if (ContainsAny(lower, "index", "indexes", "chỉ mục", "chi muc", "performance", "optimize", "tối ưu", "toi uu"))
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.TableIndexesToolName);
        }

        if (ContainsAny(lower, "list", "show", "all tables", "tables", "liệt kê", "liet ke", "danh sách", "danh sach", "bảng", "bang"))
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.ListTablesToolName);
        }

        if (ContainsAny(lower, "endpoint", "end point"))
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.ListEndpointsToolName);
        }

        if (ContainsAny(lower, "execute", "run", "select", "with ", "declare", "query", "truy vấn", "truy van", "chạy", "chay", "thực thi", "thuc thi"))
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.ExecuteSqlToolName);
        }

        if (selected.Count == 0)
        {
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.SearchObjectsToolName);
            AddIfAllowed(selected, allowedToolNames, SqlMetadataToolExecutor.FindColumnToolName);
        }

        return selected;
    }

    public static OpenAiSqlToolCall ToApprovalRequest(ChatToolCall toolCall)
    {
        var toolName = SqlMetadataToolExecutor.NormalizeToolName(toolCall.FunctionName);
        var argumentsJson = toolCall.FunctionArguments?.ToString() ?? "{}";
        var description = Definitions.TryGetValue(toolName, out var definition)
            ? definition.Description
            : SqlMetadataToolExecutor.GetToolDescription(toolName);
        return new OpenAiSqlToolCall(toolName, argumentsJson, description);
    }

    public static async Task<string> ExecuteAsync(
        OpenAiSqlToolCall toolCall,
        ChatAgentToolRuntimeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var toolName = SqlMetadataToolExecutor.NormalizeToolName(toolCall.Name);
        var argumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
            ? "{}"
            : toolCall.ArgumentsJson;

        if (string.Equals(toolName, SqlMetadataToolExecutor.ExecuteSqlToolName, StringComparison.OrdinalIgnoreCase))
        {
            var query = ToolArgumentReader.GetString(argumentsJson, "query");
            return await SqlReadOnlyQueryExecutor.ExecuteAsync(
                context.ActiveConnectionString,
                context.ActiveDatabase,
                query);
        }

        return await SqlMetadataToolExecutor.ExecuteToolAsync(
            toolName,
            argumentsJson,
            context.Metadata ?? DatabaseMetadata.Empty);
    }

    private static IEnumerable<ChatAgentToolDefinition> BuildDefinitions()
    {
        yield return CreateDefinition(
            SqlMetadataToolExecutor.ListTablesToolName,
            "List cached tables by schema/name/query.",
            """
            {"type":"object","properties":{"schemaName":{"type":"string"},"tableName":{"type":"string"},"query":{"type":"string"}}}
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.TableSchemaToolName,
            "Get columns, data types, nullable flags, PK and descriptions for one table.",
            """
            {"type":"object","properties":{"schemaName":{"type":"string"},"tableName":{"type":"string"}},"required":["tableName"]}
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.TableRelationsToolName,
            "Get cached foreign keys involving one table.",
            """
            {"type":"object","properties":{"tableName":{"type":"string"}},"required":["tableName"]}
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.TableIndexesToolName,
            "Get cached indexes for one table.",
            """
            {"type":"object","properties":{"tableName":{"type":"string"}},"required":["tableName"]}
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.SearchObjectsToolName,
            "Search tables, views, procedures and functions by name, columns, SQL text and descriptions.",
            """
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.FindColumnToolName,
            "Find table/view columns by name or description.",
            """
            {"type":"object","properties":{"query":{"type":"string"},"columnName":{"type":"string"}}}
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.ListEndpointsToolName,
            "List cached SQL Server endpoints.",
            """
            {"type":"object","properties":{}}
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.ExecuteSqlToolName,
            "Execute read-only SQL on the active SSMS connection.",
            """
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}
            """);
    }

    private static ChatAgentToolDefinition CreateDefinition(string toolName, string description, string schema) =>
        new(SqlMetadataToolExecutor.NormalizeToolName(toolName), description, schema);

    private static void AddIfAllowed(HashSet<string> selected, ISet<string> allowedToolNames, string toolName)
    {
        var normalized = SqlMetadataToolExecutor.NormalizeToolName(toolName);
        if (allowedToolNames.Contains(normalized))
        {
            selected.Add(normalized);
        }
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
}
