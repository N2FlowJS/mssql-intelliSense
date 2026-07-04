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
                var statusCode = exception.GetRawResponse() != null ? (HttpStatusCode)exception.GetRawResponse().Status : HttpStatusCode.BadRequest;
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
                        GetToolDescription(toolName));

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
                        toolOutput = await ExecuteToolAsync(toolName, arguments, metadata);
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

    private static string GetToolDescription(string toolName) => toolName switch
    {
        "list_tables" => "Liệt kê danh sách table từ schema cache hoặc GraphQL metadata.",
        "get_table_schema" => "Đọc column, kiểu dữ liệu và primary key của một table.",
        "get_table_relations" => "Đọc foreign key/relationship liên quan đến table.",
        "get_table_indexes" => "Đọc index metadata liên quan đến table.",
        "search_schema_objects" => "Tìm kiếm các đối tượng trong schema (bảng, view, procedure, function) bằng từ khóa hoặc mô tả.",
        _ => "Tool metadata request."
    };

    private async Task<string> ExecuteToolAsync(string toolName, JsonElement arguments, DatabaseMetadata metadata)
    {
        if (metadata != null && metadata.Tables != null && metadata.Tables.Count > 0)
        {
            switch (toolName)
            {
                case "list_tables":
                    return JsonSerializer.Serialize(new
                    {
                        tablesList = metadata.Tables.Select(t => new { schema = t.Schema, name = t.Name }).ToList()
                    });

                case "get_table_schema":
                    string schemaName = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("schemaName", out var sProp) ? sProp.GetString() ?? "dbo" : "dbo";
                    string tableName = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("tableName", out var tProp) ? tProp.GetString() ?? "" : "";
                    var table = metadata.FindTable(schemaName, tableName);
                    if (table == null) return "{\"error\":\"Table not found.\"}";
                    return JsonSerializer.Serialize(new
                    {
                        tableSchema = new
                        {
                            schema = table.Schema,
                            name = table.Name,
                            columns = table.Columns.Select(c => new { name = c.Name, dataType = c.DataType, isNullable = c.IsNullable, ordinal = c.Ordinal }).ToList(),
                            primaryKeyColumns = table.PrimaryKeyColumns
                        }
                    });

                case "get_table_relations":
                    string relTable = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("tableName", out var rProp) ? rProp.GetString() ?? "" : "";
                    var relations = metadata.ForeignKeys.Where(fk =>
                        fk.FromTable.Equals(relTable, StringComparison.OrdinalIgnoreCase) ||
                        fk.ToTable.Equals(relTable, StringComparison.OrdinalIgnoreCase))
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
                    return JsonSerializer.Serialize(relations);

                case "get_table_indexes":
                    string idxTable = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("tableName", out var iProp) ? iProp.GetString() ?? "" : "";
                    var indexes = metadata.Indexes.Where(idx =>
                        idx.Table.Equals(idxTable, StringComparison.OrdinalIgnoreCase))
                        .Select(idx => new
                        {
                            schema = idx.Schema,
                            table = idx.Table,
                            name = idx.Name,
                            isUnique = idx.IsUnique,
                            columns = idx.Columns
                        }).ToList();
                    return JsonSerializer.Serialize(indexes);

                case "search_schema_objects":
                    string searchQuery = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? "" : "";
                    return SearchSchemaObjects(searchQuery, metadata);
            }
        }

        switch (toolName)
        {
            case "list_tables":
                return await CallGraphQLToolAsync("query { tablesList { schema name } }");

            case "get_table_schema":
                string schemaName = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("schemaName", out var sProp) ? sProp.GetString() ?? "dbo" : "dbo";
                string tableName = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("tableName", out var tProp) ? tProp.GetString() ?? "" : "";
                return await CallGraphQLToolAsync(
                    "query($schema: String!, $name: String!) { tableSchema(schema: $schema, name: $name) { schema name columns { name dataType isNullable ordinal } primaryKeyColumns } }",
                    new { schema = schemaName, name = tableName }
                );

            case "get_table_relations":
                string relTable = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("tableName", out var rProp) ? rProp.GetString() ?? "" : "";
                return await CallGraphQLToolAsync(
                    "query($tableName: String!) { tableRelations(tableName: $tableName) { name fromSchema fromTable fromColumn toSchema toTable toColumn } }",
                    new { tableName = relTable }
                );

            case "get_table_indexes":
                string idxTable2 = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("tableName", out var iProp2) ? iProp2.GetString() ?? "" : "";
                return await CallGraphQLToolAsync(
                    "query($tableName: String!) { tableIndexes(tableName: $tableName) { schema table name isUnique columns } }",
                    new { tableName = idxTable2 }
                );

            case "search_schema_objects":
                return "{\"results\":[],\"error\":\"Search is only supported when database metadata is cached locally.\"}";

            default:
                throw new NotSupportedException($"Tool '{toolName}' is not supported.");
        }
    }

    private string SearchSchemaObjects(string query, DatabaseMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "{\"error\":\"Query cannot be empty.\"}";
        }

        var terms = query.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => t.Trim().ToLowerInvariant())
                         .ToList();

        if (terms.Count == 0)
        {
            return "{\"error\":\"Query cannot be empty.\"}";
        }

        var results = new List<object>();

        // Search tables
        if (metadata.Tables != null)
        {
            foreach (var t in metadata.Tables)
            {
                if (terms.Any(term => t.Name.ToLowerInvariant().Contains(term) ||
                                      t.Description.ToLowerInvariant().Contains(term) ||
                                      t.Keywords.ToLowerInvariant().Contains(term)))
                {
                    results.Add(new { type = "table", schema = t.Schema, name = t.Name, description = t.Description });
                }
            }
        }

        // Search views
        if (metadata.Views != null)
        {
            foreach (var v in metadata.Views)
            {
                if (terms.Any(term => v.Name.ToLowerInvariant().Contains(term) ||
                                      v.Description.ToLowerInvariant().Contains(term) ||
                                      v.Keywords.ToLowerInvariant().Contains(term)))
                {
                    results.Add(new { type = "view", schema = v.Schema, name = v.Name, description = v.Description });
                }
            }
        }

        // Search procedures
        if (metadata.Procedures != null)
        {
            foreach (var p in metadata.Procedures)
            {
                if (terms.Any(term => p.Name.ToLowerInvariant().Contains(term) ||
                                      p.Description.ToLowerInvariant().Contains(term) ||
                                      p.Keywords.ToLowerInvariant().Contains(term)))
                {
                    results.Add(new { type = "procedure", schema = p.Schema, name = p.Name, description = p.Description });
                }
            }
        }

        // Search functions
        if (metadata.Functions != null)
        {
            foreach (var f in metadata.Functions)
            {
                if (terms.Any(term => f.Name.ToLowerInvariant().Contains(term) ||
                                      f.Description.ToLowerInvariant().Contains(term) ||
                                      f.Keywords.ToLowerInvariant().Contains(term)))
                {
                    results.Add(new { type = "function", schema = f.Schema, name = f.Name, description = f.Description });
                }
            }
        }

        // Limit results to top 20 to avoid exceeding token limit
        var limitedResults = results.Take(20).ToList();

        return JsonSerializer.Serialize(new { results = limitedResults });
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
                    name = new { type = "string", @enum = new[] { "list_tables", "get_table_schema", "get_table_relations", "get_table_indexes", "search_schema_objects" } },
                    arguments = new
                    {
                        type = "object",
                        properties = new
                        {
                            schemaName = new { type = "string" },
                            tableName = new { type = "string" },
                            query = new { type = "string" }
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
