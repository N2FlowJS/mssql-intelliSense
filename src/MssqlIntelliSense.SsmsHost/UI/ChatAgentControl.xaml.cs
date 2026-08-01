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
using Microsoft.VisualStudio.Shell;
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
    private const string SendIconGlyph = "\uE724";
    private const string StopIconGlyph = "\uE71A";
    private const string ApproveIconGlyph = "\uE73E";
    private const string RejectIconGlyph = "\uE711";
    private const string ListTablesToolName = SqlMetadataToolExecutor.ListTablesToolName;
    private const string TableSchemaToolName = SqlMetadataToolExecutor.TableSchemaToolName;
    private const string TableRelationsToolName = SqlMetadataToolExecutor.TableRelationsToolName;
    private const string TableIndexesToolName = SqlMetadataToolExecutor.TableIndexesToolName;
    private const string SearchObjectsToolName = SqlMetadataToolExecutor.SearchObjectsToolName;
    private const string FindColumnToolName = SqlMetadataToolExecutor.FindColumnToolName;
    private const string ListEndpointsToolName = SqlMetadataToolExecutor.ListEndpointsToolName;
    private const string ExecuteSqlToolName = SqlMetadataToolExecutor.ExecuteSqlToolName;

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
    private readonly List<string> _sentInputHistory = new();
    private int _historyIndex = -1;
    private string _currentDraft = string.Empty;
    private ConnectionInfo? _selectedConnection;
    private string? _selectedDatabase;
    private CancellationTokenSource? _activeSendCancellation;

    public ChatAgentControl()
    {
        InitializeComponent();
        UpdateToolSelectionSummary();
    }

    public void SetSelectedConnection(ConnectionInfo? connection, string? databaseName = null)
    {
        _selectedConnection = connection;
        _selectedDatabase = databaseName;
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

            if (_sentInputHistory.Count == 0 || _sentInputHistory[_sentInputHistory.Count - 1] != message)
            {
                _sentInputHistory.Add(message);
            }
            _historyIndex = -1;
            _currentDraft = string.Empty;

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
            return;
        }

        if (e.Key == Key.Up)
        {
            if (_sentInputHistory.Count > 0)
            {
                var caretLineIndex = ChatInputTextBox.GetLineIndexFromCharacterIndex(ChatInputTextBox.CaretIndex);
                if (caretLineIndex <= 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (_historyIndex == -1)
                    {
                        _currentDraft = ChatInputTextBox.Text;
                        _historyIndex = _sentInputHistory.Count - 1;
                    }
                    else if (_historyIndex > 0)
                    {
                        _historyIndex--;
                    }

                    ChatInputTextBox.Text = _sentInputHistory[_historyIndex];
                    ChatInputTextBox.CaretIndex = ChatInputTextBox.Text.Length;
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Down)
        {
            if (_historyIndex != -1)
            {
                var lineCount = ChatInputTextBox.LineCount;
                var caretLineIndex = ChatInputTextBox.GetLineIndexFromCharacterIndex(ChatInputTextBox.CaretIndex);
                if (caretLineIndex >= lineCount - 1 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (_historyIndex < _sentInputHistory.Count - 1)
                    {
                        _historyIndex++;
                        ChatInputTextBox.Text = _sentInputHistory[_historyIndex];
                    }
                    else
                    {
                        _historyIndex = -1;
                        ChatInputTextBox.Text = _currentDraft;
                    }

                    ChatInputTextBox.CaretIndex = ChatInputTextBox.Text.Length;
                    e.Handled = true;
                }
            }
        }
    }

    private void ToolMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ToolMenuPopup.IsOpen = true;
    }

    private void ClearChatButton_Click(object sender, RoutedEventArgs e)
    {
        _chatHistory.Clear();
        ChatMessagesPanel.Children.Clear();
    }

    private void ToolSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateToolSelectionSummary();
    }

    private void UpdateToolSelectionSummary()
    {
        var count = GetAllowedToolNamesFromUi().Count;
        if (ToolBadgeText != null)
        {
            ToolBadgeText.Text = count.ToString();
        }

        if (ToolMenuButton != null)
        {
            ToolMenuButton.ToolTip = $"Configure active chat tools ({count} enabled)";
        }
    }

    private async Task SendChatAsync(string message, CancellationToken cancellationToken)
    {
        await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
        AddChatMessage("You", message, isUser: true);
        ChatInputTextBox.Text = string.Empty;
        var allowedToolNames = GetAllowedToolNamesFromUi();

        // Get AI options from SSMS options inside SSMS, or saved config.json in DebugApp.
        var options = await MssqlIntelliSensePackage.FetchLlmSettingsStaticAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            SafeAddChatError("Please configure your API key in Settings first.");
            return;
        }

        await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
#pragma warning disable VSTHRD010
        var chatConnection = ResolveChatConnectionContext();
#pragma warning restore VSTHRD010
        AddChatMessage(
            "Context",
            string.IsNullOrWhiteSpace(chatConnection.DisplayName)
                ? "No active SQL connection found. The assistant will answer without cached schema context."
                : $"Connection: {chatConnection.DisplayName}",
            isUser: false);

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

        var hasSchemaMetadata = HasSchemaMetadata(metadata);
        if (!hasSchemaMetadata)
        {
            metadata = null;
            await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
            AddChatMessage(
                "Schema",
                string.IsNullOrWhiteSpace(chatConnection.DisplayName)
                    ? "No cached schema is available. The assistant can provide general SQL guidance, but cannot verify database objects."
                    : "Schema has not been scanned for this connection. Scan the schema before asking the assistant to verify tables, columns, relationships, or indexes.",
                isUser: false);
        }

        Border? statusBorder = null;
        if (hasSchemaMetadata && allowedToolNames.Count > 0)
        {
            await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
            statusBorder = AddChatMessage("Assistant", "Checking available actions...", isUser: false, isStreaming: true);
        }

        var toolContext = hasSchemaMetadata
            ? await ResolveApprovedToolContextAsync(
                endpoint: options.Endpoint,
                apiKey: options.ApiKey,
                model: string.IsNullOrWhiteSpace(options.Model) ? "gpt-4o" : options.Model,
                userMessage: message,
                metadata: metadata,
                chatConnection: chatConnection,
                allowedToolNames: allowedToolNames,
                statusBorder: statusBorder,
                cancellationToken: cancellationToken)
            : "Schema cache is unavailable. Do not assume or invent tables, columns, relationships, indexes, procedures, or views. Ask the user to scan the schema when object-specific information is required.";

        if (statusBorder != null)
        {
            await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
            ChatMessagesPanel.Children.Remove(statusBorder);
            statusBorder = null;
        }

        var systemPrompt = BuildSystemPrompt(metadata, toolContext);
        if (!string.IsNullOrWhiteSpace(chatConnection.DisplayName))
        {
            systemPrompt = $"Active SQL connection: {chatConnection.DisplayName}\n" + systemPrompt;
        }

        Border? assistantMessageBorder = null;
        await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
        assistantMessageBorder = AddChatMessage("Assistant", string.Empty, isUser: false, isStreaming: true);

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

    private static bool HasSchemaMetadata(DatabaseMetadata? metadata) =>
        metadata != null &&
        !ReferenceEquals(metadata, DatabaseMetadata.Empty) &&
        (metadata.Tables.Count > 0 ||
         metadata.Views.Count > 0 ||
         metadata.Procedures.Count > 0 ||
         metadata.Functions.Count > 0 ||
         metadata.UserTypes.Count > 0 ||
         metadata.Synonyms.Count > 0);

    private ChatConnectionContext ResolveChatConnectionContext()
    {
        if (MssqlIntelliSensePackage.Instance != null)
        {
#pragma warning disable VSTHRD108
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
#pragma warning restore VSTHRD108
        }

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
                ActiveDatabase = _selectedDatabase,
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
        ChatConnectionContext chatConnection,
        ISet<string> allowedToolNames,
        Border? statusBorder,
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
                await SafeUpdateChatMessageAsync(statusBorder, "Checking available actions...");

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
                    await AddChatMessageOnMainThreadAsync(
                        "Tool",
                        $"Blocked {plannerResult.ToolCall.Name}\nAction is disabled for this chat.",
                        isUser: false,
                        cancellationToken);
                    toolOutputs.Add($"Tool: {plannerResult.ToolCall.Name}\nArguments: {plannerResult.ToolCall.ArgumentsJson}\nOutput: {blockedOutput}");
                    break;
                }

                var approved = await RequestToolApprovalAsync(plannerResult.ToolCall, cancellationToken);
                string output;
                if (approved)
                {
                    output = await ExecuteApprovedToolAsync(plannerResult.ToolCall, metadata ?? DatabaseMetadata.Empty, chatConnection);
                    await AddChatMessageOnMainThreadAsync(
                        "Tool",
                        $"Executed {plannerResult.ToolCall.Name}\n{SummarizeToolOutput(output)}",
                        isUser: false,
                        cancellationToken);
                }
                else
                {
                    output = JsonSerializer.Serialize(new
                    {
                        error = "Tool call rejected by user.",
                        tool = plannerResult.ToolCall.Name
                    }, JsonOptions);
                    await AddChatMessageOnMainThreadAsync(
                        "Tool",
                        $"Rejected {plannerResult.ToolCall.Name}",
                        isUser: false,
                        cancellationToken);
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
            try
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
            }
            catch (Exception ex)
            {
                MssqlIntelliSensePackage.Log($"[Chat Agent Tool Planner JsonSchema Error] {ex.Message}");
                content.Clear();
                try
                {
                    var fallbackOptions = new ChatCompletionOptions();
                    foreach (var update in chatClient.CompleteChatStreaming(messages, fallbackOptions, cancellationToken))
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
                }
                catch (Exception fallbackEx)
                {
                    MssqlIntelliSensePackage.Log($"[Chat Agent Tool Planner Fallback Error] {fallbackEx.Message}");
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

        if (ExecuteSqlToolCheckBox?.IsChecked == true)
        {
            allowed.Add(ExecuteSqlToolName);
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
            systemPrompt.AppendLine("- " + SqlMetadataToolExecutor.GetToolPlannerDescription(toolName));
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
            toolCall = new OpenAiSqlToolCall(name, argumentsJson, SqlMetadataToolExecutor.GetToolDescription(name));
        }

        return new ToolPlannerResult(status, toolCall);
    }

    private async Task<bool> RequestToolApprovalAsync(OpenAiSqlToolCall toolCall, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
        AddToolApprovalCard(toolCall, tcs);
        return await tcs.Task;
    }

    private sealed class MessageControlState
    {
        public MessageControlState(StackPanel contentPanel, Brush foreground, Brush borderBrush, Brush codeBackground, bool renderMarkdown)
        {
            ContentPanel = contentPanel;
            Foreground = foreground;
            BorderBrush = borderBrush;
            CodeBackground = codeBackground;
            RenderMarkdown = renderMarkdown;
        }

        public StackPanel ContentPanel { get; }
        public Brush Foreground { get; }
        public Brush BorderBrush { get; }
        public Brush CodeBackground { get; }
        public bool RenderMarkdown { get; }
        public string RawText { get; set; } = string.Empty;
    }

    private static void RenderMessageContent(MessageControlState state, string message)
    {
        state.RawText = message;
        if (state.RenderMarkdown)
        {
            RenderMarkdownToContainer(
                state.ContentPanel,
                message,
                state.Foreground,
                state.BorderBrush,
                state.CodeBackground);
            return;
        }

        state.ContentPanel.Children.Clear();
        state.ContentPanel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = state.Foreground,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Text = message
        });
    }

    private static void RenderMarkdownToContainer(
        StackPanel contentPanel,
        string markdownText,
        Brush foreground,
        Brush borderBrush,
        Brush codeBackground)
    {
        contentPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return;
        }

        var codeBlockRegex = new System.Text.RegularExpressions.Regex(@"```(?<lang>\w*)\r?\n(?<code>[\s\S]*?)```|```(?<code2>[\s\S]*?)```", System.Text.RegularExpressions.RegexOptions.Compiled);
        int lastIndex = 0;
        var matches = codeBlockRegex.Matches(markdownText);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                var textSegment = markdownText.Substring(lastIndex, match.Index - lastIndex);
                RenderTextParagraphs(contentPanel, textSegment, foreground, borderBrush, codeBackground);
            }

            string code = match.Groups["code"].Success ? match.Groups["code"].Value : match.Groups["code2"].Value;
            code = code.TrimEnd('\r', '\n');
            string lang = match.Groups["lang"].Success ? match.Groups["lang"].Value : string.Empty;

            var codeContainer = new Grid();
            codeContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            codeContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var codeHeaderGrid = new Grid
            {
                Margin = new Thickness(0, 0, 0, 4)
            };
            codeHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            codeHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var langTextBlock = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(lang) ? "code" : lang.ToLowerInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(langTextBlock, 0);
            codeHeaderGrid.Children.Add(langTextBlock);

            var copyCodeButton = new Button
            {
                Content = "Copy",
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 10,
                MinHeight = 20,
                Background = codeBackground,
                Foreground = foreground,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Copy code block to clipboard"
            };

            var capturedCode = code;
            copyCodeButton.Click += (s, e) =>
            {
                _ = CopyCodeWithFeedbackAsync(capturedCode, copyCodeButton);
            };
            Grid.SetColumn(copyCodeButton, 1);
            codeHeaderGrid.Children.Add(copyCodeButton);

            Grid.SetRow(codeHeaderGrid, 0);
            codeContainer.Children.Add(codeHeaderGrid);

            var codeTextBox = new TextBox
            {
                Text = code,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 11.5,
                Foreground = foreground,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Padding = new Thickness(0)
            };
            Grid.SetRow(codeTextBox, 1);
            codeContainer.Children.Add(codeTextBox);

            var codeBorder = new Border
            {
                Background = codeBackground,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = codeContainer
            };

            contentPanel.Children.Add(codeBorder);

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < markdownText.Length)
        {
            var textSegment = markdownText.Substring(lastIndex);
            RenderTextParagraphs(contentPanel, textSegment, foreground, borderBrush, codeBackground);
        }
    }

    private static async Task CopyCodeWithFeedbackAsync(string code, Button copyButton)
    {
        try
        {
            Clipboard.SetText(code);
            copyButton.Content = "Copied!";
            await Task.Delay(1500);
            copyButton.Content = "Copy";
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"Copy code error: {ex.Message}");
        }
    }

    private static void RenderTextParagraphs(StackPanel container, string text, Brush foreground, Brush borderBrush, Brush codeBackground)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        TextBlock? currentTextBlock = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                currentTextBlock = null;
                continue;
            }

            if (IsMarkdownTableStart(lines, i))
            {
                currentTextBlock = null;
                var tableLines = new List<string> { lines[i] };
                i += 2;
                while (i < lines.Length && IsPipeRow(lines[i]))
                {
                    tableLines.Add(lines[i]);
                    i++;
                }

                i--;
                container.Children.Add(CreateMarkdownTable(tableLines, foreground, borderBrush, codeBackground));
            }
            else if (trimmed.StartsWith("# "))
            {
                container.Children.Add(new TextBlock
                {
                    Text = trimmed.Substring(2).Trim(),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = foreground,
                    Margin = new Thickness(0, 4, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                });
                currentTextBlock = null;
            }
            else if (trimmed.StartsWith("## "))
            {
                container.Children.Add(new TextBlock
                {
                    Text = trimmed.Substring(3).Trim(),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = foreground,
                    Margin = new Thickness(0, 3, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                });
                currentTextBlock = null;
            }
            else if (trimmed.StartsWith("### "))
            {
                container.Children.Add(new TextBlock
                {
                    Text = trimmed.Substring(4).Trim(),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = foreground,
                    Margin = new Thickness(0, 2, 0, 1),
                    TextWrapping = TextWrapping.Wrap
                });
                currentTextBlock = null;
            }
            else if (IsListItem(trimmed))
            {
                currentTextBlock = null;
                container.Children.Add(CreateListItem(trimmed, foreground));
            }
            else
            {
                if (currentTextBlock == null)
                {
                    currentTextBlock = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = foreground,
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    container.Children.Add(currentTextBlock);
                }
                else
                {
                    currentTextBlock.Inlines.Add(new System.Windows.Documents.LineBreak());
                }

                ParseInlineFormattedText(currentTextBlock, line);
            }
        }
    }

    private static bool IsListItem(string trimmed)
    {
        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
        {
            return true;
        }

        var markerEnd = trimmed.IndexOf(' ');
        if (markerEnd < 2)
        {
            return false;
        }

        var marker = trimmed.Substring(0, markerEnd);
        return (marker.EndsWith(".") || marker.EndsWith(")"))
            && marker.Length > 1
            && marker.Take(marker.Length - 1).All(char.IsDigit);
    }

    private static TextBlock CreateListItem(string trimmed, Brush foreground)
    {
        var markerEnd = trimmed.IndexOf(' ');
        var body = markerEnd >= 0 ? trimmed.Substring(markerEnd + 1).Trim() : trimmed;
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = foreground,
            Margin = new Thickness(10, 1, 0, 1)
        };
        textBlock.Inlines.Add(new System.Windows.Documents.Run("- ")
        {
            FontWeight = FontWeights.Bold
        });
        ParseInlineFormattedText(textBlock, body);
        return textBlock;
    }

    private static bool IsMarkdownTableStart(string[] lines, int index)
    {
        return index + 1 < lines.Length
            && IsPipeRow(lines[index])
            && IsMarkdownTableSeparator(lines[index + 1]);
    }

    private static bool IsPipeRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Contains('|') && trimmed.Count(c => c == '|') >= 2;
    }

    private static bool IsMarkdownTableSeparator(string line)
    {
        var cells = SplitMarkdownTableRow(line);
        return cells.Count > 0
            && cells.All(cell =>
            {
                var value = cell.Trim();
                return value.Length >= 3 && value.Trim('-', ':').Length == 0;
            });
    }

    private static Grid CreateMarkdownTable(IReadOnlyList<string> tableLines, Brush foreground, Brush borderBrush, Brush codeBackground)
    {
        var rows = tableLines.Select(SplitMarkdownTableRow).Where(row => row.Count > 0).ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var grid = new Grid
        {
            Margin = new Thickness(0, 5, 0, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 64 });
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[rowIndex];
            for (var column = 0; column < columnCount; column++)
            {
                var cellText = column < row.Count ? row[column] : string.Empty;
                var cellTextBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = foreground,
                    Margin = new Thickness(0)
                };
                ParseInlineFormattedText(cellTextBlock, cellText);

                var cell = new Border
                {
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = rowIndex == 0 ? codeBackground : Brushes.Transparent,
                    Padding = new Thickness(6, 4, 6, 4),
                    Child = cellTextBlock
                };

                if (rowIndex == 0)
                {
                    cellTextBlock.FontWeight = FontWeights.SemiBold;
                }

                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        var frame = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Child = grid
        };

        var outerGrid = new Grid { ClipToBounds = true };
        outerGrid.Children.Add(frame);
        return outerGrid;
    }

    private static List<string> SplitMarkdownTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|"))
        {
            trimmed = trimmed.Substring(1);
        }

        if (trimmed.EndsWith("|"))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        }

        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static void ParseInlineFormattedText(TextBlock textBlock, string text)
    {
        var inlineRegex = new System.Text.RegularExpressions.Regex(@"(\*\*(?<bold>.*?)\*\*)|(`(?<code>.*?)`)", System.Text.RegularExpressions.RegexOptions.Compiled);
        int lastIndex = 0;
        var matches = inlineRegex.Matches(text);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                textBlock.Inlines.Add(new System.Windows.Documents.Run(text.Substring(lastIndex, match.Index - lastIndex)));
            }

            if (match.Groups["bold"].Success)
            {
                textBlock.Inlines.Add(new System.Windows.Documents.Run(match.Groups["bold"].Value)
                {
                    FontWeight = FontWeights.Bold
                });
            }
            else if (match.Groups["code"].Success)
            {
                textBlock.Inlines.Add(new System.Windows.Documents.Run(match.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    Background = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128))
                });
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            textBlock.Inlines.Add(new System.Windows.Documents.Run(text.Substring(lastIndex)));
        }
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
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        container.Children.Add(new TextBlock
        {
            Text = $"Action approval  •  {timestamp}",
            FontWeight = FontWeights.Bold,
            Foreground = textBrush,
            FontSize = 11,
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
            Text = SqlMetadataToolExecutor.GetToolApprovalReason(toolCall.Name),
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
        var isApprove = text.Equals("Approve", StringComparison.OrdinalIgnoreCase);
        return new Button
        {
            Content = CreateIconGlyph(isApprove ? ApproveIconGlyph : RejectIconGlyph),
            Background = GetThemeBrush(EnvironmentColors.SystemButtonFaceBrushKey, Color.FromRgb(240, 240, 240)),
            Foreground = GetThemeBrush(EnvironmentColors.SystemButtonTextBrushKey, Colors.Black),
            BorderBrush = GetThemeBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(204, 204, 204)),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            Width = 30,
            MinWidth = 30,
            Height = 28,
            MinHeight = 28,
            ToolTip = text
        };
    }

    private static TextBlock CreateIconGlyph(string glyph)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static async Task<string> ExecuteApprovedToolAsync(OpenAiSqlToolCall toolCall, DatabaseMetadata metadata, ChatConnectionContext chatConnection)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
        return await SqlMetadataToolExecutorBridge.ExecuteToolAsync(
            toolCall.Name,
            document.RootElement,
            metadata,
            query => SqlReadOnlyQueryExecutor.ExecuteAsync(
                chatConnection.ActiveConnectionString ?? string.Empty,
                chatConnection.ActiveDatabase,
                query));
    }

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

            await Task.Run(() =>
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
                            SafeUpdateChatMessageAsync(assistantMessageBorder, assistantMessageContent.ToString()).GetAwaiter().GetResult();
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
#pragma warning disable VSTHRD001
                Dispatcher.Invoke(() => AddChatMessage("Error", message, isUser: false));
#pragma warning restore VSTHRD001
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
            if (messageBorder == null || messageBorder.Dispatcher.HasShutdownStarted || messageBorder.Dispatcher.HasShutdownFinished) return;
            if (messageBorder.Dispatcher.CheckAccess())
            {
                UpdateChatMessage(messageBorder, newContent);
            }
            else
            {
#pragma warning disable VSTHRD001
                await messageBorder.Dispatcher.InvokeAsync(() => UpdateChatMessage(messageBorder, newContent));
#pragma warning restore VSTHRD001
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Chat Agent UI Update Error] {ex.Message}");
        }
    }

    private async Task SafeSetSendButtonStateAsync(string text, bool isEnabled)
    {
        var isStop = text.Equals("Stop", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess())
            {
                SendChatButton.Content = CreateIconGlyph(isStop ? StopIconGlyph : SendIconGlyph);
                SendChatButton.IsEnabled = isEnabled;
                SendChatButton.ToolTip = isStop ? "Stop response" : "Send message";
            }
            else
            {
#pragma warning disable VSTHRD001
                await Dispatcher.InvokeAsync(() =>
                {
                    SendChatButton.Content = CreateIconGlyph(isStop ? StopIconGlyph : SendIconGlyph);
                    SendChatButton.IsEnabled = isEnabled;
                    SendChatButton.ToolTip = isStop ? "Stop response" : "Send message";
                });
#pragma warning restore VSTHRD001
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Chat Agent UI State Error] {ex.Message}");
        }
    }

    private async Task AddChatMessageOnMainThreadAsync(
        string sender,
        string message,
        bool isUser,
        CancellationToken cancellationToken)
    {
        await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
        AddChatMessage(sender, message, isUser);
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
                    if (!string.IsNullOrWhiteSpace(table.ExtendedDescription))
                    {
                        sb.AppendLine($"  Description: {table.ExtendedDescription}");
                    }
                    sb.AppendLine("  Columns:");
                    foreach (var column in table.Columns.Take(20))
                    {
                        sb.AppendLine($"  - {column.Name} ({column.DataType}) { (column.IsNullable ? "NULL" : "NOT NULL") }");
                        if (!string.IsNullOrWhiteSpace(column.Description))
                        {
                            sb.AppendLine($"    Description: {column.Description}");
                        }
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
                    if (!string.IsNullOrWhiteSpace(view.ExtendedDescription))
                    {
                        sb.AppendLine($"  Description: {view.ExtendedDescription}");
                    }
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
                            name = new { type = "string", @enum = SqlMetadataToolExecutor.AllToolNames },
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

        // Header (sender + timestamp + copy button)
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var senderText = new TextBlock
        {
            Text = $"{sender}  •  {timestamp}",
            FontWeight = FontWeights.Bold,
            Foreground = messageForeground,
            FontSize = 11
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
                    if (border.Tag is MessageControlState state)
                    {
                        Clipboard.SetText(state.RawText ?? string.Empty);
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
        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        container.Children.Add(contentPanel);

        border.Child = container;
        var state = new MessageControlState(contentPanel, messageForeground, borderBrush, messageBackground, renderMarkdown: !isUser);
        RenderMessageContent(state, message);
        border.Tag = state;
        ChatMessagesPanel.Children.Add(border);

        // Scroll to bottom
        ChatMessagesScrollViewer.ScrollToEnd();
        return border;
    }

    private void UpdateChatMessage(Border messageBorder, string newContent)
    {
        if (messageBorder.Tag is MessageControlState state)
        {
            RenderMessageContent(state, newContent);
        }
        ChatMessagesScrollViewer.ScrollToEnd();
    }

    private Brush GetThemeBrush(object key, Color fallbackColor)
    {
        return TryFindResource(key) as Brush ?? new SolidColorBrush(fallbackColor);
    }
}
