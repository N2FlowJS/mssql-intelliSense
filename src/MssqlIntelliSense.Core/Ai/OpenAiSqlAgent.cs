using System.ClientModel;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
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
            new SystemChatMessage("You are a SQL Server T-SQL expert. You have access to database schema tools to query metadata. Resolve schema details using tools before generating SQL. Do not invent tables or columns. Once you have all the information, return status 'completed' along with the SQL result."),
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
                jsonSchemaFormatName: "agent_response",
                jsonSchema: BinaryData.FromObjectAsJson(AgentResponseSchema),
                jsonSchemaIsStrict: true
            )
        };

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

            using var resDoc = JsonDocument.Parse(outputText);
            var root = resDoc.RootElement;
            string status = root.GetProperty("status").GetString()!;
            if (status == "completed")
            {
                var resultNode = root.GetProperty("result");
                return JsonSerializer.Deserialize<AiSqlResult>(resultNode.GetRawText(), JsonOptions)
                    ?? throw new OpenAiSqlAgentException("Failed to deserialize final result.");
            }
            else if (status == "tool_call")
            {
                var toolCall = root.GetProperty("toolCall");
                string toolName = toolCall.GetProperty("name").GetString()!;
                JsonElement arguments = default;
                if (toolCall.TryGetProperty("arguments", out var argsElement))
                {
                    arguments = argsElement;
                }

                string toolOutput;
                try
                {
                    var approval = new OpenAiSqlToolCall(
                        toolName,
                        arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText(),
                        SqlMetadataToolExecutor.GetToolDescription(toolName));

                    var approved = await _options.ToolApprovalHandler(approval, cancellationToken);
                    if (!approved)
                    {
                        toolOutput = JsonSerializer.Serialize(new
                        {
                            error = "Tool call rejected by user.",
                            tool = toolName
                        });
                    }
                    else
                    {
                        toolOutput = await SqlMetadataToolExecutor.ExecuteToolAsync(
                            toolName, arguments, metadata, CallGraphQLToolAsync);
                    }
                }
                catch (Exception ex)
                {
                    toolOutput = JsonSerializer.Serialize(new
                    {
                        error = ex.Message,
                        tool = toolName
                    }, JsonOptions);
                }

                messages.Add(new AssistantChatMessage(outputText));
                messages.Add(new UserChatMessage($"Tool output for {toolName}:\n{toolOutput}"));
            }
            else
            {
                throw new OpenAiSqlAgentException($"Unknown status: {status}");
            }
        }

        throw new OpenAiSqlAgentException("Agent reached maximum iterations without completing.");
    }

    protected virtual async Task<string> CallGraphQLToolAsync(string query, object? variables = null)
    {
        using (var client = new HttpClient())
        {
            var requestBody = new
            {
                query = query,
                variables = variables
            };
            var content = JsonContent.Create(requestBody);
            var response = await client.PostAsync("http://localhost:5070/graphql", content);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            throw new Exception($"GraphQL tool execution failed: {response.ReasonPhrase}");
        }
    }

    private static readonly object AgentResponseSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "status" },
        properties = new
        {
            status = new { type = "string", @enum = new[] { "tool_call", "completed" } },
            toolCall = new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", @enum = SqlMetadataToolExecutor.AllToolNames },
                    arguments = new
                    {
                        type = "object",
                        properties = new
                        {
                            schemaName = new { type = "string" },
                            tableName = new { type = "string" },
                            query = new { type = "string" },
                            columnName = new { type = "string" }
                        }
                    }
                }
            },
            result = new
            {
                type = "object",
                properties = new
                {
                    improvedSql = new { type = "string" },
                    explanation = new { type = "string" },
                    warnings = new { type = "array", items = new { type = "string" } },
                    indexSuggestions = new { type = "array", items = new { type = "string" } }
                }
            }
        }
    };
}

public class OpenAiSqlAgentException : Exception
{
    public OpenAiSqlAgentException(string message, HttpStatusCode? statusCode = null) : base(message) => StatusCode = statusCode;
    public OpenAiSqlAgentException(string message, Exception innerException) : base(message, innerException) { }
    public HttpStatusCode? StatusCode { get; }
}
