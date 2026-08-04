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
            "List cached SQL Server tables by optional schema, object name, or natural-language query.",
            """
            {
              "type": "object",
              "properties": {
                "schemaName": { "type": "string", "description": "Optional schema filter, for example dbo." },
                "tableName": { "type": "string", "description": "Optional table name or partial table name filter." },
                "query": { "type": "string", "description": "Optional natural-language or partial-name search text." }
              }
            }
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.TableSchemaToolName,
            "Get columns, data types, nullability, primary key columns, and descriptions for one table.",
            """
            {
              "type": "object",
              "properties": {
                "schemaName": { "type": "string", "description": "Schema name. Use dbo if unknown." },
                "tableName": { "type": "string", "description": "Exact table name." }
              },
              "required": [ "tableName" ]
            }
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.TableRelationsToolName,
            "Get cached foreign-key relationships involving one table.",
            """
            {
              "type": "object",
              "properties": {
                "tableName": { "type": "string", "description": "Exact table name to inspect relationships for." }
              },
              "required": [ "tableName" ]
            }
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.TableIndexesToolName,
            "Get cached index metadata for one table.",
            """
            {
              "type": "object",
              "properties": {
                "tableName": { "type": "string", "description": "Exact table name to inspect indexes for." }
              },
              "required": [ "tableName" ]
            }
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.SearchObjectsToolName,
            "Search cached tables, views, procedures, and functions by name, descriptions, columns/parameters, and SQL definition text.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Natural-language or keyword query, in the user's language if useful." }
              },
              "required": [ "query" ]
            }
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.FindColumnToolName,
            "Find matching table/view columns by name or column description.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Column name, partial column name, or natural-language column description." },
                "columnName": { "type": "string", "description": "Optional exact or partial column name." }
              }
            }
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.ListEndpointsToolName,
            "List cached SQL Server endpoints under Server Objects.",
            """
            {
              "type": "object",
              "properties": {}
            }
            """);

        yield return CreateDefinition(
            SqlMetadataToolExecutor.ExecuteSqlToolName,
            "Execute a read-only SQL query against the active SSMS connection. Only SELECT, WITH, DECLARE, or safe metadata EXEC statements are allowed.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Read-only T-SQL query to run." }
              },
              "required": [ "query" ]
            }
            """);
    }

    private static ChatAgentToolDefinition CreateDefinition(string toolName, string description, string schema) =>
        new(SqlMetadataToolExecutor.NormalizeToolName(toolName), description, schema);
}
