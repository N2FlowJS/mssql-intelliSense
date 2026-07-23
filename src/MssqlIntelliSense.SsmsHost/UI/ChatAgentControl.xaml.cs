using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using OpenAI;
using OpenAI.Chat;
using MssqlIntelliSense.Core.Ai;
using MssqlIntelliSense.Core.Metadata;
using System.ClientModel;

namespace MssqlIntelliSense.SsmsHost;

public partial class ChatAgentControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ListTablesToolName = "list_tables";
    private const string TableSchemaToolName = "get_table_schema";
    private const string TableRelationsToolName = "get_table_relations";
    private const string TableIndexesToolName = "get_table_indexes";
    private const string SearchObjectsToolName = "search_objects";
    private const string FindColumnToolName = "find_column";
    private const string ListEndpointsToolName = "list_endpoints";

    private sealed class ChatTurn
    {
        public ChatTurn(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public string Role { get; }
        public string Content { get; }
    }

    private sealed class ChatConnectionContext
    {
        public ConnectionInfo? Connection { get; set; }
        public string? ActiveConnectionString { get; set; }
        public string? ActiveDatabase { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public bool FromActiveWindow { get; set; }
    }

    private readonly List<ChatTurn> _chatHistory = new();
    private ConnectionInfo? _selectedConnection;
    private CancellationTokenSource? _activeSendCancellation;

    public ChatAgentControl()
    {
        InitializeComponent();
        UpdateToolSelectionSummary();
    }

    public void SetSelectedConnection(ConnectionInfo? connection)
    {
        _selectedConnection = connection;
    }

    private void SendChatButton_Click(object sender, RoutedEventArgs e)
    {
        _ = SendChatButtonClickAsync();
    }

    private async Task SendChatButtonClickAsync()
    {
        if (_activeSendCancellation != null)
        {
            _activeSendCancellation.Cancel();
            return;
        }

        try
        {
            var message = ChatInputTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message)) return;

            _activeSendCancellation = new CancellationTokenSource();
            await SafeSetSendButtonStateAsync("Stop", true);
            await SendChatAsync(message, _activeSendCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SafeAddChatError("Request stopped.");
        }
        catch (Exception ex)
        {
            SafeAddChatError($"Chat agent error: {ex.Message}");
            MssqlIntelliSensePackage.Log($"[Chat Agent Error] {ex}");
        }
        finally
        {
            _activeSendCancellation?.Dispose();
            _activeSendCancellation = null;
            await SafeSetSendButtonStateAsync("Send", true);
        }
    }

    private void ChatInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _ = SendChatButtonClickAsync();
        }
    }

    private void ToolMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ToolMenuPopup.IsOpen = true;
    }

    private void ToolSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateToolSelectionSummary();
    }

    private void UpdateToolSelectionSummary()
    {
        if (ToolSelectionSummaryText == null)
        {
            return;
        }

        var count = GetAllowedToolNamesFromUi().Count;
        ToolSelectionSummaryText.Text = count == 1
            ? "Tools: 1 enabled"
            : $"Tools: {count} enabled";
    }

    private async Task SendChatAsync(string message, CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            AddChatMessage("You", message, isUser: true);
            ChatInputTextBox.Text = string.Empty;
        });
        var allowedToolNames = await Dispatcher.InvokeAsync(GetAllowedToolNamesFromUi);

        // Get AI options
        var options = MssqlIntelliSensePackage.GetOptions();
        if (options == null || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            SafeAddChatError("Please configure your API key in Settings first.");
            return;
        }

        var chatConnection = await Dispatcher.InvokeAsync(ResolveChatConnectionContext);
        await Dispatcher.InvokeAsync(() => AddChatMessage(
            "Context",
            string.IsNullOrWhiteSpace(chatConnection.DisplayName)
                ? "No active SQL connection found. The assistant will answer without cached schema context."
                : $"Connection: {chatConnection.DisplayName}",
            isUser: false));

        var metadata = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                DatabaseMetadata metadata;
                var activeConnectionString = chatConnection.ActiveConnectionString;
                if (!string.IsNullOrWhiteSpace(activeConnectionString))
                {
                    metadata = MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(activeConnectionString!);
                }
                else if (chatConnection.Connection != null)
                {
                    metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(chatConnection.Connection.Id).Metadata;
                }
                else
                {
                    return null;
                }

                var activeDatabase = chatConnection.ActiveDatabase;
                return string.IsNullOrWhiteSpace(activeDatabase)
                    ? metadata
                    : MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, activeDatabase!);
            }
            catch (Exception ex)
            {
                MssqlIntelliSensePackage.Log($"[Chat Agent Metadata Error] {ex.Message}");
                return null;
            }
        }, cancellationToken);

        var toolContext = await ResolveApprovedToolContextAsync(
            endpoint: options.Endpoint,
            apiKey: options.ApiKey,
            model: string.IsNullOrWhiteSpace(options.Model) ? "gpt-4o" : options.Model,
            userMessage: message,
            metadata: metadata,
            allowedToolNames: allowedToolNames,
            cancellationToken: cancellationToken);

        var systemPrompt = BuildSystemPrompt(metadata, toolContext);
        if (!string.IsNullOrWhiteSpace(chatConnection.DisplayName))
        {
            systemPrompt = $"Active SQL connection: {chatConnection.DisplayName}\n" + systemPrompt;
        }

        Border? assistantMessageBorder = null;
        await Dispatcher.InvokeAsync(() =>
        {
            assistantMessageBorder = AddChatMessage("Assistant", string.Empty, isUser: false, isStreaming: true);
        });

        var reply = await CompleteChatStreamingTextAsync(
            endpoint: options.Endpoint,
            apiKey: options.ApiKey,
            model: string.IsNullOrWhiteSpace(options.Model) ? "gpt-4o" : options.Model,
            systemPrompt: systemPrompt,
            message: message,
            assistantMessageBorder: assistantMessageBorder,
            cancellationToken: cancellationToken);

        _chatHistory.Add(new ChatTurn("user", message));
        _chatHistory.Add(new ChatTurn("assistant", reply));
        TrimChatHistory();
    }

    private ChatConnectionContext ResolveChatConnectionContext()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

        var activeConnectionString = MssqlIntelliSensePackage.GetActiveConnectionString();
        var activeDatabase = MssqlIntelliSensePackage.GetActiveDatabaseName();
        if (!string.IsNullOrWhiteSpace(activeConnectionString))
        {
            var normalizedConnectionString = NormalizeServerConnectionString(activeConnectionString!);
            var cachedConnection = MssqlIntelliSenseCacheReader.GetConnections()
                .FirstOrDefault(c => NormalizeServerConnectionString(c.ConnectionString)
                    .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

            if (cachedConnection == null)
            {
                var serverName = GetServerName(activeConnectionString!);
                var name = string.IsNullOrWhiteSpace(serverName) ? "Active SQL connection" : serverName;
                var connectionId = MssqlIntelliSenseCacheWriter.RegisterConnection(normalizedConnectionString, name);
                cachedConnection = MssqlIntelliSenseCacheReader.GetConnections().FirstOrDefault(c => c.Id == connectionId);
            }

            return new ChatConnectionContext
            {
                Connection = cachedConnection,
                ActiveConnectionString = activeConnectionString,
                ActiveDatabase = activeDatabase,
                DisplayName = BuildConnectionDisplayName(activeConnectionString!, activeDatabase),
                FromActiveWindow = true
            };
        }

        if (_selectedConnection != null)
        {
            return new ChatConnectionContext
            {
                Connection = _selectedConnection,
                ActiveConnectionString = _selectedConnection.ConnectionString ?? string.Empty,
                DisplayName = _selectedConnection.Name,
                FromActiveWindow = false
            };
        }

        return new ChatConnectionContext();
    }

    private static string NormalizeServerConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            builder.Remove("Initial Catalog");
            builder.Remove("Database");
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    private static string BuildConnectionDisplayName(string connectionString, string? activeDatabase)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            var server = string.IsNullOrWhiteSpace(builder.DataSource) ? "Unknown server" : builder.DataSource;
            var database = string.IsNullOrWhiteSpace(activeDatabase)
                ? builder.InitialCatalog
                : activeDatabase;
            return string.IsNullOrWhiteSpace(database)
                ? server
                : $"{server} / {database}";
        }
        catch
        {
            return string.IsNullOrWhiteSpace(activeDatabase)
                ? connectionString
                : $"{connectionString} / {activeDatabase}";
        }
    }

    private static string GetServerName(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return builder.DataSource;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> ResolveApprovedToolContextAsync(
        string endpoint,
        string apiKey,
        string model,
        string userMessage,
        DatabaseMetadata? metadata,
        ISet<string> allowedToolNames,
        CancellationToken cancellationToken)
    {
        var toolOutputs = new List<string>();
        if (allowedToolNames.Count == 0)
        {
            return string.Empty;
        }

        try
        {
            var clientOptions = new OpenAIClientOptions();
            var sdkEndpoint = GetSdkEndpoint(endpoint);
            if (sdkEndpoint != null)
            {
                clientOptions.Endpoint = sdkEndpoint;
            }

            var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
            var chatClient = client.GetChatClient(model);

            for (var iteration = 0; iteration < 4; iteration++)
            {
                await Dispatcher.InvokeAsync(() => AddChatMessage(
                    "Assistant",
                    "Checking available actions...",
                    isUser: false));

                var plannerJson = await CompleteToolPlannerStreamingAsync(
                    chatClient,
                    BuildToolPlannerMessages(metadata, userMessage, toolOutputs, allowedToolNames),
                    cancellationToken);

                var plannerResult = ParseToolPlannerResult(plannerJson);
                if (plannerResult == null || plannerResult.Status == "completed")
                {
                    break;
                }

                if (plannerResult.ToolCall == null)
                {
                    break;
                }

                if (!allowedToolNames.Contains(plannerResult.ToolCall.Name))
                {
                    var blockedOutput = JsonSerializer.Serialize(new
                    {
                        error = "Tool call blocked by chat session action settings.",
                        tool = plannerResult.ToolCall.Name
                    }, JsonOptions);
                    await Dispatcher.InvokeAsync(() => AddChatMessage(
                        "Tool",
                        $"Blocked {plannerResult.ToolCall.Name}\nAction is disabled for this chat.",
                        isUser: false));
                    toolOutputs.Add($"Tool: {plannerResult.ToolCall.Name}\nArguments: {plannerResult.ToolCall.ArgumentsJson}\nOutput: {blockedOutput}");
                    break;
                }

                var approved = await RequestToolApprovalAsync(plannerResult.ToolCall, cancellationToken);
                string output;
                if (approved)
                {
                    output = await ExecuteApprovedToolAsync(plannerResult.ToolCall, metadata ?? DatabaseMetadata.Empty);
                    await Dispatcher.InvokeAsync(() => AddChatMessage(
                        "Tool",
                        $"Executed {plannerResult.ToolCall.Name}\n{SummarizeToolOutput(output)}",
                        isUser: false));
                }
                else
                {
                    output = JsonSerializer.Serialize(new
                    {
                        error = "Tool call rejected by user.",
                        tool = plannerResult.ToolCall.Name
                    }, JsonOptions);
                    await Dispatcher.InvokeAsync(() => AddChatMessage(
                        "Tool",
                        $"Rejected {plannerResult.ToolCall.Name}",
                        isUser: false));
                }

                toolOutputs.Add($"Tool: {plannerResult.ToolCall.Name}\nArguments: {plannerResult.ToolCall.ArgumentsJson}\nOutput: {output}");

                if (!approved)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Chat Agent Tool Planner Error] {ex}");
            toolOutputs.Add("Tool planner error: " + ex.Message);
        }

        return toolOutputs.Count == 0
            ? string.Empty
            : string.Join("\n\n", toolOutputs);
    }

    private async Task<string> CompleteToolPlannerStreamingAsync(
        ChatClient chatClient,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "tool_planner_response",
                jsonSchema: BinaryData.FromString(JsonSerializer.Serialize(ToolPlannerResponseSchema, JsonOptions)),
                jsonSchemaIsStrict: true)
        };

        await Task.Run(() =>
        {
            foreach (var update in chatClient.CompleteChatStreaming(messages, options, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        content.Append(part.Text);
                    }
                }
            }
        }, cancellationToken);

        return content.ToString();
    }

    private HashSet<string> GetAllowedToolNamesFromUi()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ListTablesToolCheckBox?.IsChecked == true)
        {
            allowed.Add(ListTablesToolName);
        }

        if (TableSchemaToolCheckBox?.IsChecked == true)
        {
            allowed.Add(TableSchemaToolName);
        }

        if (TableRelationsToolCheckBox?.IsChecked == true)
        {
            allowed.Add(TableRelationsToolName);
        }

        if (TableIndexesToolCheckBox?.IsChecked == true)
        {
            allowed.Add(TableIndexesToolName);
        }

        if (SearchObjectsToolCheckBox?.IsChecked == true)
        {
            allowed.Add(SearchObjectsToolName);
        }

        if (FindColumnToolCheckBox?.IsChecked == true)
        {
            allowed.Add(FindColumnToolName);
        }

        if (ListEndpointsToolCheckBox?.IsChecked == true)
        {
            allowed.Add(ListEndpointsToolName);
        }

        return allowed;
    }

    private List<ChatMessage> BuildToolPlannerMessages(
        DatabaseMetadata? metadata,
        string userMessage,
        IReadOnlyList<string> toolOutputs,
        ISet<string> allowedToolNames)
    {
        var systemPrompt = new StringBuilder();
        systemPrompt.AppendLine("You are a SQL Server chat tool planner.");
        systemPrompt.AppendLine("Decide whether the assistant must use a schema metadata tool before answering.");
        systemPrompt.AppendLine("Return only JSON that matches the schema.");
        systemPrompt.AppendLine("If the user asks about tables, columns, indexes, relations, stored procedures, views, unknown object names, or SQL generation that needs schema, return status 'tool_call'.");
        systemPrompt.AppendLine("If no tool is needed or enough tool output is already available, return status 'completed'.");
        systemPrompt.AppendLine("Allowed tools for this chat session:");
        foreach (var toolName in allowedToolNames)
        {
            systemPrompt.AppendLine("- " + GetToolPlannerDescription(toolName));
        }
        systemPrompt.AppendLine("Do not request tools that are not listed above.");

        if (metadata != null)
        {
            systemPrompt.AppendLine("Schema cache summary:");
            systemPrompt.AppendLine($"Tables: {metadata.Tables.Count}, Views: {metadata.Views.Count}, Procedures: {metadata.Procedures.Count}, Foreign keys: {metadata.ForeignKeys.Count}, Indexes: {metadata.Indexes.Count}.");
            foreach (var table in metadata.Tables.Take(40))
            {
                systemPrompt.AppendLine($"- {table.Schema}.{table.Name}");
            }
        }

        if (toolOutputs.Count > 0)
        {
            systemPrompt.AppendLine("Already approved tool outputs:");
            foreach (var output in toolOutputs)
            {
                systemPrompt.AppendLine(output);
            }
        }

        return new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt.ToString()),
            new UserChatMessage(userMessage)
        };
    }

    private ToolPlannerResult? ParseToolPlannerResult(string plannerJson)
    {
        if (string.IsNullOrWhiteSpace(plannerJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(plannerJson);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString() ?? "completed"
            : "completed";

        OpenAiSqlToolCall? toolCall = null;
        if (status == "tool_call" && root.TryGetProperty("toolCall", out var toolElement))
        {
            var name = toolElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var argumentsJson = toolElement.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.GetRawText()
                : "{}";
            toolCall = new OpenAiSqlToolCall(name, argumentsJson, GetToolDescription(name));
        }

        return new ToolPlannerResult(status, toolCall);
    }

    private async Task<bool> RequestToolApprovalAsync(OpenAiSqlToolCall toolCall, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        await Dispatcher.InvokeAsync(() => AddToolApprovalCard(toolCall, tcs));
        return await tcs.Task;
    }

    private void AddToolApprovalCard(OpenAiSqlToolCall toolCall, TaskCompletionSource<bool> completionSource)
    {
        var borderBrush = GetThemeBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(204, 204, 204));
        var textBrush = GetThemeBrush(EnvironmentColors.ToolWindowTextBrushKey, Colors.Black);
        var backgroundBrush = GetThemeBrush(EnvironmentColors.ToolWindowCodeBlockBackgroundBrushKey, Color.FromRgb(245, 245, 245));

        var border = new Border
        {
            Background = backgroundBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 620
        };

        var container = new StackPanel { Orientation = Orientation.Vertical };
        container.Children.Add(new TextBlock
        {
            Text = "Action approval",
            FontWeight = FontWeights.Bold,
            Foreground = textBrush,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        });
        container.Children.Add(new TextBlock
        {
            Text = $"Tool: {toolCall.Name}",
            Foreground = textBrush,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            TextWrapping = TextWrapping.Wrap
        });
        container.Children.Add(new TextBlock
        {
            Text = toolCall.Description,
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 3)
        });
        container.Children.Add(new TextBlock
        {
            Text = GetToolApprovalReason(toolCall.Name),
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        container.Children.Add(new TextBlock
        {
            Text = "Arguments: " + toolCall.ArgumentsJson,
            Foreground = textBrush,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var approveButton = CreateActionButton("Approve");
        var rejectButton = CreateActionButton("Reject");
        var statusText = new TextBlock
        {
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        approveButton.Click += (_, _) =>
        {
            CompleteToolApproval(approveButton, rejectButton, statusText, "Approved");
            completionSource.TrySetResult(true);
        };
        rejectButton.Click += (_, _) =>
        {
            CompleteToolApproval(approveButton, rejectButton, statusText, "Rejected");
            completionSource.TrySetResult(false);
        };

        buttons.Children.Add(approveButton);
        buttons.Children.Add(rejectButton);
        buttons.Children.Add(statusText);
        container.Children.Add(buttons);

        border.Child = container;
        ChatMessagesPanel.Children.Add(border);
        ChatMessagesScrollViewer.ScrollToEnd();
    }

    private static void CompleteToolApproval(Button approveButton, Button rejectButton, TextBlock statusText, string status)
    {
        approveButton.Visibility = Visibility.Collapsed;
        rejectButton.Visibility = Visibility.Collapsed;
        statusText.Margin = new Thickness(0);
        statusText.Text = status;
    }

    private Button CreateActionButton(string text)
    {
        return new Button
        {
            Content = text,
            Background = GetThemeBrush(EnvironmentColors.SystemButtonFaceBrushKey, Color.FromRgb(240, 240, 240)),
            Foreground = GetThemeBrush(EnvironmentColors.SystemButtonTextBrushKey, Colors.Black),
            BorderBrush = GetThemeBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(204, 204, 204)),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
            MinWidth = 72
        };
    }

    private static string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch
        {
            return json;
        }
    }

    private async Task<string> ExecuteApprovedToolAsync(OpenAiSqlToolCall toolCall, DatabaseMetadata metadata)
    {
        await Task.Yield();

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
        var arguments = document.RootElement;

        return toolCall.Name switch
        {
            "list_tables" => JsonSerializer.Serialize(new
            {
                tablesList = metadata.Tables.Select(t => new { database = t.Database, schema = t.Schema, name = t.Name }).ToList()
            }, JsonOptions),
        "get_table_schema" => JsonSerializer.Serialize(GetTableSchemaToolResult(metadata, arguments), JsonOptions),
        "get_table_relations" => JsonSerializer.Serialize(GetTableRelationsToolResult(metadata, arguments), JsonOptions),
        "get_table_indexes" => JsonSerializer.Serialize(GetTableIndexesToolResult(metadata, arguments), JsonOptions),
        "search_objects" => JsonSerializer.Serialize(GetSearchObjectsToolResult(metadata, arguments), JsonOptions),
        "find_column" => JsonSerializer.Serialize(GetFindColumnToolResult(metadata, arguments), JsonOptions),
        "list_endpoints" => JsonSerializer.Serialize(GetListEndpointsToolResult(metadata), JsonOptions),
        _ => JsonSerializer.Serialize(new { error = $"Tool '{toolCall.Name}' is not supported." }, JsonOptions)
    };
    }

    private static object GetTableSchemaToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var schemaName = GetArgument(arguments, "schemaName", "dbo");
        var tableName = GetArgument(arguments, "tableName", string.Empty);
        var table = metadata.FindTable(schemaName, tableName);
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

    private static object GetTableRelationsToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var tableName = GetArgument(arguments, "tableName", string.Empty);
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

    private static object GetTableIndexesToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var tableName = GetArgument(arguments, "tableName", string.Empty);
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

    private static object GetSearchObjectsToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var query = GetArgument(arguments, "query", GetArgument(arguments, "tableName", string.Empty));
        var matches = metadata.Tables.Select(t => new { kind = "table", database = t.Database, schema = t.Schema, name = t.Name })
            .Concat(metadata.Views.Select(v => new { kind = "view", database = v.Database, schema = v.Schema, name = v.Name }))
            .Concat(metadata.Procedures.Select(p => new { kind = "procedure", database = p.Database, schema = p.Schema, name = p.Name }))
            .Concat(metadata.Functions.Select(f => new { kind = "function", database = f.Database, schema = f.Schema, name = f.Name }))
            .Where(o => Matches(o.name, query) || Matches(o.schema + "." + o.name, query))
            .OrderBy(o => o.kind)
            .ThenBy(o => o.schema)
            .ThenBy(o => o.name)
            .Take(100)
            .ToList();

        return new { query, matches };
    }

    private static object GetFindColumnToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var query = GetArgument(arguments, "query", GetArgument(arguments, "columnName", string.Empty));
        var tableColumns = metadata.Tables.SelectMany(t => t.Columns.Select(c => new
        {
            kind = "table",
            database = t.Database,
            schema = t.Schema,
            objectName = t.Name,
            column = c.Name,
            dataType = c.DataType,
            isNullable = c.IsNullable
        }));
        var viewColumns = metadata.Views.SelectMany(v => v.Columns.Select(c => new
        {
            kind = "view",
            database = v.Database,
            schema = v.Schema,
            objectName = v.Name,
            column = c.Name,
            dataType = c.DataType,
            isNullable = c.IsNullable
        }));

        return new
        {
            query,
            matches = tableColumns.Concat(viewColumns)
                .Where(c => Matches(c.column, query))
                .OrderBy(c => c.schema)
                .ThenBy(c => c.objectName)
                .ThenBy(c => c.column)
                .Take(150)
                .ToList()
        };
    }

    private static object GetListEndpointsToolResult(DatabaseMetadata metadata)
    {
        return new
        {
            endpoints = metadata.Endpoints
                .OrderBy(ep => ep.Name)
                .Select(ep => new { ep.Name, ep.Type, ep.Protocol, ep.State, ep.Port })
                .ToList()
        };
    }

    private static bool Matches(string value, string query)
    {
        return string.IsNullOrWhiteSpace(query) ||
               value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetArgument(JsonElement arguments, string name, string fallback)
    {
        return arguments.ValueKind == JsonValueKind.Object &&
               arguments.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static string GetToolDescription(string toolName) => toolName switch
    {
        ListTablesToolName => "Liệt kê danh sách table trong schema cache của connection/database đang chọn.",
        TableSchemaToolName => "Đọc column, kiểu dữ liệu và primary key của một table.",
        TableRelationsToolName => "Đọc foreign key/relationship liên quan đến table.",
        TableIndexesToolName => "Đọc index metadata liên quan đến table.",
        SearchObjectsToolName => "Tìm table/view/procedure/function theo tên trong schema cache.",
        FindColumnToolName => "Tìm column theo tên trong table/view đã cache.",
        ListEndpointsToolName => "Liệt kê SQL Server endpoints thuộc Server Objects.",
        _ => "Tool metadata request."
    };

    private static string GetToolPlannerDescription(string toolName) => toolName switch
    {
        ListTablesToolName => "list_tables: list available tables.",
        TableSchemaToolName => "get_table_schema: get columns and primary key for one table. Arguments: schemaName, tableName.",
        TableRelationsToolName => "get_table_relations: get foreign keys for one table. Arguments: tableName.",
        TableIndexesToolName => "get_table_indexes: get indexes for one table. Arguments: tableName.",
        SearchObjectsToolName => "search_objects: search tables, views, procedures and functions by partial name. Arguments: query.",
        FindColumnToolName => "find_column: search table/view columns by partial column name. Arguments: query.",
        ListEndpointsToolName => "list_endpoints: list SQL Server endpoints under Server Objects.",
        _ => toolName + ": enabled tool."
    };

    private static string GetToolApprovalReason(string toolName) => toolName switch
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

    private static string SummarizeToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "(empty output)";
        }

        return output.Length <= 700 ? output : output.Substring(0, 700) + "...";
    }

    private sealed class ToolPlannerResult
    {
        public ToolPlannerResult(string status, OpenAiSqlToolCall? toolCall)
        {
            Status = status;
            ToolCall = toolCall;
        }

        public string Status { get; }
        public OpenAiSqlToolCall? ToolCall { get; }
    }

    private async Task<string> CompleteChatStreamingTextAsync(
        string endpoint,
        string apiKey,
        string model,
        string systemPrompt,
        string message,
        Border? assistantMessageBorder,
        CancellationToken cancellationToken)
    {
        var assistantMessageContent = new StringBuilder();
        var lastUiUpdate = Stopwatch.StartNew();
        try
        {
            var clientOptions = new OpenAIClientOptions();
            var sdkEndpoint = GetSdkEndpoint(endpoint);
            if (sdkEndpoint != null)
            {
                clientOptions.Endpoint = sdkEndpoint;
            }

            var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
            var chatClient = client.GetChatClient(model);
            var messages = BuildChatMessages(systemPrompt, message);

            await Task.Run(async () =>
            {
                var completionOptions = new ChatCompletionOptions();
                foreach (var chatUpdate in chatClient.CompleteChatStreaming(messages, completionOptions, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var part in chatUpdate.ContentUpdate)
                    {
                        if (string.IsNullOrEmpty(part.Text))
                        {
                            continue;
                        }

                        assistantMessageContent.Append(part.Text);
                        if (lastUiUpdate.ElapsedMilliseconds >= 50)
                        {
                            await SafeUpdateChatMessageAsync(assistantMessageBorder, assistantMessageContent.ToString());
                            lastUiUpdate.Restart();
                        }
                    }
                }
            }, cancellationToken);

            var reply = assistantMessageContent.ToString();
            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = "OpenAI returned an empty response.";
                await SafeUpdateChatMessageAsync(assistantMessageBorder, reply);
            }
            else
            {
                await SafeUpdateChatMessageAsync(assistantMessageBorder, reply);
            }

            return reply;
        }
        catch (OperationCanceledException)
        {
            var partial = assistantMessageContent.ToString();
            var cancelledMessage = string.IsNullOrWhiteSpace(partial)
                ? "Request stopped."
                : partial + "\n\n[Stopped]";
            await SafeUpdateChatMessageAsync(assistantMessageBorder, cancelledMessage);
            throw;
        }
        catch (Exception ex)
        {
            var errorMessage = $"OpenAI streaming error: {ex.Message}";
            MssqlIntelliSensePackage.Log($"[Chat Agent Streaming Error] {ex}");
            await SafeUpdateChatMessageAsync(assistantMessageBorder, errorMessage);
            return errorMessage;
        }
    }

    private List<ChatMessage> BuildChatMessages(string systemPrompt, string message)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        foreach (var turn in _chatHistory.Skip(Math.Max(0, _chatHistory.Count - 12)))
        {
            if (turn.Role == "assistant")
            {
                messages.Add(new AssistantChatMessage(turn.Content));
            }
            else
            {
                messages.Add(new UserChatMessage(turn.Content));
            }
        }

        messages.Add(new UserChatMessage(message));
        return messages;
    }

    private static Uri? GetSdkEndpoint(string configuredEndpoint)
    {
        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return null;
        }

        var endpoint = configuredEndpoint.TrimEnd('/');
        foreach (var suffix in new[] { "/responses", "/chat/completions" })
        {
            if (endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Substring(0, endpoint.Length - suffix.Length);
                break;
            }
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : null;
    }

    private void TrimChatHistory()
    {
        const int maxTurns = 24;
        if (_chatHistory.Count <= maxTurns) return;
        _chatHistory.RemoveRange(0, _chatHistory.Count - maxTurns);
    }

    private void SafeAddChatError(string message)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess())
            {
                AddChatMessage("Error", message, isUser: false);
            }
            else
            {
                Dispatcher.Invoke(() => AddChatMessage("Error", message, isUser: false));
            }
        }
        catch
        {
            // Last-resort guard: never let chat UI errors close SSMS.
        }
    }

    private async Task SafeUpdateChatMessageAsync(Border? messageBorder, string newContent)
    {
        try
        {
            if (messageBorder == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            await Dispatcher.InvokeAsync(() => UpdateChatMessage(messageBorder, newContent));
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Chat Agent UI Update Error] {ex.Message}");
        }
    }

    private async Task SafeSetSendButtonStateAsync(string text, bool isEnabled)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            await Dispatcher.InvokeAsync(() =>
            {
                SendChatButton.Content = text;
                SendChatButton.IsEnabled = isEnabled;
            });
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Chat Agent UI State Error] {ex.Message}");
        }
    }

    private string BuildSystemPrompt(DatabaseMetadata? metadata, string toolContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a helpful SQL Server assistant. You help write, optimize, and explain T-SQL queries.");
        sb.AppendLine("Use markdown formatting in your responses.");
        sb.AppendLine("Any tool output below was explicitly approved by the user inside the chat session. Use it as trusted context.");
        if (!string.IsNullOrWhiteSpace(toolContext))
        {
            sb.AppendLine("\nApproved tool output:");
            sb.AppendLine(toolContext);
        }
        
        if (metadata != null)
        {
            sb.AppendLine("\nDatabase schema information:");
            // Add tables info
            if (metadata.Tables.Count > 0)
            {
                sb.AppendLine("\nTables:");
                foreach (var table in metadata.Tables.Take(50))
                {
                    sb.AppendLine($"- {table.Schema}.{table.Name} (Database: {table.Database})");
                    sb.AppendLine("  Columns:");
                    foreach (var column in table.Columns.Take(20))
                    {
                        sb.AppendLine($"  - {column.Name} ({column.DataType}) { (column.IsNullable ? "NULL" : "NOT NULL") }");
                    }
                }
            }

            // Add views
            if (metadata.Views.Count > 0)
            {
                sb.AppendLine("\nViews:");
                foreach (var view in metadata.Views.Take(20))
                {
                    sb.AppendLine($"- {view.Schema}.{view.Name} (Database: {view.Database})");
                }
            }

            // Add procedures
            if (metadata.Procedures.Count > 0)
            {
                sb.AppendLine("\nStored Procedures:");
                foreach (var proc in metadata.Procedures.Take(20))
                {
                    sb.AppendLine($"- {proc.Schema}.{proc.Name} (Database: {proc.Database})");
                }
            }
        }

        sb.AppendLine("\nPlease format your answers using markdown.");
        return sb.ToString();
    }

    private static readonly object ToolPlannerResponseSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "status", "toolCall" },
        properties = new
        {
            status = new { type = "string", @enum = new[] { "tool_call", "completed" } },
            toolCall = new
            {
                anyOf = new object[]
                {
                    new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "name", "arguments" },
                        properties = new
                        {
                            name = new { type = "string", @enum = new[] { "list_tables", "get_table_schema", "get_table_relations", "get_table_indexes", "search_objects", "find_column", "list_endpoints" } },
                            arguments = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    schemaName = new { type = "string" },
                                    tableName = new { type = "string" },
                                    query = new { type = "string" },
                                    columnName = new { type = "string" }
                                },
                                required = new[] { "schemaName", "tableName", "query", "columnName" }
                            }
                        }
                    },
                    new { type = "null" }
                }
            }
        }
    };

    private Border AddChatMessage(string sender, string message, bool isUser, bool isStreaming = false)
    {
        var messageBackground = isUser
            ? GetThemeBrush(EnvironmentColors.SystemHighlightBrushKey, Color.FromRgb(0, 122, 204))
            : GetThemeBrush(EnvironmentColors.ToolWindowCodeBlockBackgroundBrushKey, Color.FromRgb(245, 245, 245));
        var messageForeground = isUser
            ? GetThemeBrush(EnvironmentColors.SystemHighlightTextBrushKey, Colors.White)
            : GetThemeBrush(EnvironmentColors.ToolWindowTextBrushKey, Colors.Black);
        var borderBrush = GetThemeBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(204, 204, 204));

        var border = new Border
        {
            Background = messageBackground,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 10, 10, 10),
            Margin = new Thickness(5, 5, 5, 5),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 600
        };

        var container = new StackPanel { Orientation = Orientation.Vertical };

        // Header (sender + copy button)
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        var senderText = new TextBlock
        {
            Text = sender,
            FontWeight = FontWeights.Bold,
            Foreground = messageForeground,
            FontSize = 12
        };
        headerPanel.Children.Add(senderText);

        if (!isUser)
        {
            var copyButton = new Button
            {
                Content = "Copy",
                Background = GetThemeBrush(EnvironmentColors.SystemButtonFaceBrushKey, Color.FromRgb(240, 240, 240)),
                Foreground = GetThemeBrush(EnvironmentColors.SystemButtonTextBrushKey, Colors.Black),
                BorderBrush = borderBrush,
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(10, 0, 0, 0),
                FontSize = 10
            };
            copyButton.Click += (s, e) =>
            {
                try
                {
                    if (border.Tag is TextBlock textBlock)
                    {
                        Clipboard.SetText(textBlock.Text ?? string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    MssqlIntelliSensePackage.Log($"Copy error: {ex.Message}");
                }
            };
            headerPanel.Children.Add(copyButton);
        }
        container.Children.Add(headerPanel);

        // Content
        var contentBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = messageForeground,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Text = message
        };
        container.Children.Add(contentBlock);

        border.Child = container;
        border.Tag = contentBlock;
        ChatMessagesPanel.Children.Add(border);

        // Scroll to bottom
        ChatMessagesScrollViewer.ScrollToEnd();
        return border;
    }

    private void UpdateChatMessage(Border messageBorder, string newContent)
    {
        if (messageBorder.Tag is TextBlock textBlock)
        {
            textBlock.Text = newContent;
        }
        ChatMessagesScrollViewer.ScrollToEnd();
    }

    private Brush GetThemeBrush(object key, Color fallbackColor)
    {
        return TryFindResource(key) as Brush ?? new SolidColorBrush(fallbackColor);
    }
}
