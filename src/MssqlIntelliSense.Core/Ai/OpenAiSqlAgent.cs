using System.ClientModel;
using System.Linq;
using System.Net;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Ai;

public class OpenAiSqlAgent : IAiSqlAssistant
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient; // Left for custom testing and backwards compatibility
    private readonly OpenAiSqlAgentOptions _options;

    public OpenAiSqlAgent(HttpClient httpClient, OpenAiSqlAgentOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _options.Validate();
    }

    public async Task<AiSqlResult> ImproveSqlAsync(
        string sql, DatabaseMetadata metadata, string instruction, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a SQL Server T-SQL expert. Use the provided function tools to inspect metadata when object or column details matter. Do not invent tables or columns. Return only the final JSON result when ready."),
            new UserChatMessage($"User instruction:\n{instruction}\n\nSQL:\n{sql}")
        };

        var clientOptions = new OpenAIClientOptions();
        if (_options.Endpoint != null)
        {
            clientOptions.Endpoint = _options.Endpoint;
        }
        if (_httpClient != null)
        {
            clientOptions.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(_httpClient);
        }

        var client = new OpenAIClient(new ApiKeyCredential(_options.ApiKey), clientOptions);
        var chatClient = client.GetChatClient(_options.Model);

        var completionOptions = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "sql_result",
                jsonSchema: BinaryData.FromObjectAsJson(SqlResultSchema),
                jsonSchemaIsStrict: true
            )
        };
        foreach (var tool in CreateMetadataTools())
        {
            completionOptions.Tools.Add(tool);
        }

        const int maxIterations = 5;
        for (int i = 0; i < maxIterations; i++)
        {
            ChatCompletion response;
            try
            {
                response = await chatClient.CompleteChatAsync(messages, completionOptions, cancellationToken);
            }
            catch (ClientResultException exception)
            {
                var rawResponse = exception.GetRawResponse();
                var statusCode = rawResponse != null ? (HttpStatusCode)rawResponse.Status : HttpStatusCode.BadRequest;
                throw new OpenAiSqlAgentException(exception.Message, statusCode);
            }
            
            IReadOnlyList<ChatToolCall> toolCalls;
            try
            {
                toolCalls = response.ToolCalls ?? Array.Empty<ChatToolCall>();
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new OpenAiSqlAgentException("OpenAI returned an unexpected response shape.");
            }

            if (toolCalls.Count > 0)
            {
                messages.Add(ChatMessage.CreateAssistantMessage(response));

                foreach (var toolCall in toolCalls)
                {
                    var toolName = SqlMetadataToolExecutor.NormalizeToolName(toolCall.FunctionName);
                    var argumentsJson = toolCall.FunctionArguments?.ToString() ?? "{}";
                    string toolOutput;
                    try
                    {
                        var approval = new OpenAiSqlToolCall(
                            toolName,
                            argumentsJson,
                            SqlMetadataToolExecutor.GetToolDescription(toolName));

                        var approved = await _options.ToolApprovalHandler(approval, cancellationToken);
                        if (!approved)
                        {
                            toolOutput = $"## Tool: {toolName}\n\n**Error:** Tool call rejected by user.";
                        }
                        else
                        {
                            toolOutput = await SqlMetadataToolExecutor.ExecuteToolAsync(
                                toolName, argumentsJson, metadata);
                        }
                    }
                    catch (Exception ex)
                    {
                        toolOutput = $"## Tool: {toolName}\n\n**Error:** {ex.Message}";
                    }

                    messages.Add(ChatMessage.CreateToolMessage(toolCall.Id, toolOutput));
                }

                continue;
            }

            string outputText;
            try
            {
                if (response.Content == null || response.Content.Count == 0)
                    throw new OpenAiSqlAgentException("OpenAI returned an empty response.");
                outputText = response.Content[0].Text;
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new OpenAiSqlAgentException("OpenAI returned an empty response.");
            }

            return JsonSerializer.Deserialize<AiSqlResult>(outputText, JsonOptions)
                ?? throw new OpenAiSqlAgentException("Failed to deserialize final result.");
        }

        throw new OpenAiSqlAgentException("Agent reached maximum iterations without completing.");
    }

    private static IEnumerable<ChatTool> CreateMetadataTools()
    {
        foreach (var toolName in SqlMetadataToolExecutor.AllToolNames
                     .Where(tool => !string.Equals(tool, SqlMetadataToolExecutor.ExecuteSqlToolName, StringComparison.OrdinalIgnoreCase))
                     .Select(SqlMetadataToolExecutor.NormalizeToolName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return ChatTool.CreateFunctionTool(
                functionName: toolName,
                functionDescription: SqlMetadataToolExecutor.GetToolDescription(toolName),
                functionParameters: BinaryData.FromString(GetToolSchema(toolName)),
                functionSchemaIsStrict: false);
        }
    }

    private static string GetToolSchema(string toolName) => SqlMetadataToolExecutor.NormalizeToolName(toolName) switch
    {
        SqlMetadataToolExecutor.TableSchemaToolName => """
            { "type": "object", "properties": { "schemaName": { "type": "string" }, "tableName": { "type": "string" } }, "required": [ "tableName" ] }
            """,
        SqlMetadataToolExecutor.TableRelationsToolName or SqlMetadataToolExecutor.TableIndexesToolName => """
            { "type": "object", "properties": { "tableName": { "type": "string" } }, "required": [ "tableName" ] }
            """,
        SqlMetadataToolExecutor.SearchObjectsToolName or SqlMetadataToolExecutor.FindColumnToolName => """
            { "type": "object", "properties": { "query": { "type": "string" }, "columnName": { "type": "string" } } }
            """,
        _ => """
            { "type": "object", "properties": { "schemaName": { "type": "string" }, "tableName": { "type": "string" }, "query": { "type": "string" }, "columnName": { "type": "string" } } }
            """
    };

    private static readonly object SqlResultSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "improvedSql", "explanation", "warnings", "indexSuggestions" },
        properties = new
        {
            improvedSql = new { type = "string" },
            explanation = new { type = "string" },
            warnings = new { type = "array", items = new { type = "string" } },
            indexSuggestions = new { type = "array", items = new { type = "string" } }
        }
    };
}

public class OpenAiSqlAgentException : Exception
{
    public OpenAiSqlAgentException(string message, HttpStatusCode? statusCode = null) : base(message) => StatusCode = statusCode;
    public OpenAiSqlAgentException(string message, Exception innerException) : base(message, innerException) { }
    public HttpStatusCode? StatusCode { get; }
}
