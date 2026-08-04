using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
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
    private static WeakReference<ChatAgentControl>? _activeControl;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DisplayJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
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

    private sealed class MetadataLoadResult
    {
        public DatabaseMetadata? Metadata { get; set; }
        public string? ErrorMessage { get; set; }
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
        Loaded += (_, _) => MarkActiveControl();
        GotKeyboardFocus += (_, _) => MarkActiveControl();
        MouseEnter += (_, _) => MarkActiveControl();
        UpdateToolSelectionSummary();
    }

    public static bool TryRedirectEditorCommandToActiveControl()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_activeControl == null || !_activeControl.TryGetTarget(out var control) || !control.IsVisible)
        {
            return false;
        }

        if (!control.IsKeyboardFocusWithin && !control.IsMouseOver)
        {
            return false;
        }

        control.FocusChatInput();
        return true;
    }

    private void MarkActiveControl()
    {
        _activeControl = new WeakReference<ChatAgentControl>(this);
    }

    private void FocusChatInput()
    {
        MarkActiveControl();
        ChatInputTextBox.Focus();
        Keyboard.Focus(ChatInputTextBox);
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
        e.Handled = true;
        _chatHistory.Clear();
        ChatMessagesPanel.Children.Clear();
        FocusChatInput();
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

        await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
        var metadataStatusBorder = AddChatMessage("Schema", "Loading schema cache... (1/3) Resolving cache source", isUser: false, isStreaming: true);
        var metadataResult = await LoadChatMetadataAsync(chatConnection, metadataStatusBorder, cancellationToken);
        var metadata = metadataResult.Metadata;

        var hasSchemaMetadata = HasSchemaMetadata(metadata);
        if (!hasSchemaMetadata)
        {
            metadata = null;
            await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
            var unavailableMessage = !string.IsNullOrWhiteSpace(metadataResult.ErrorMessage)
                ? "Schema cache load failed: " + metadataResult.ErrorMessage
                : string.IsNullOrWhiteSpace(chatConnection.DisplayName)
                    ? "No cached schema is available. The assistant can provide general SQL guidance, but cannot verify database objects."
                    : "Schema has not been scanned for this connection. Scan the schema before asking the assistant to verify tables, columns, relationships, or indexes.";
            await SafeUpdateChatMessageAsync(metadataStatusBorder, unavailableMessage);
        }
        else
        {
            await SafeUpdateChatMessageAsync(metadataStatusBorder, BuildSchemaLoadedMessage(metadata!));
        }

        Border? statusBorder = null;
        if (hasSchemaMetadata && allowedToolNames.Count > 0)
        {
            await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
            statusBorder = AddChatMessage("Assistant", "Checking available actions...", isUser: false, isStreaming: true);
        }

        var toolContext = hasSchemaMetadata
            ? await ResolveApprovedToolContextAsync(
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

        LogAgentTrace(
            model: string.IsNullOrWhiteSpace(options.Model) ? "gpt-4o" : options.Model,
            chatConnection: chatConnection,
            metadata: metadata,
            allowedToolNames: allowedToolNames,
            toolContext: toolContext,
            systemPrompt: systemPrompt);

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

    private async Task<MetadataLoadResult> LoadChatMetadataAsync(
        ChatConnectionContext chatConnection,
        Border? statusBorder,
        CancellationToken cancellationToken)
    {
        try
        {
            await SafeUpdateChatMessageAsync(statusBorder, "Loading schema cache... (1/3) Resolving cache source");

            var hasActiveConnectionString = !string.IsNullOrWhiteSpace(chatConnection.ActiveConnectionString);
            var hasCachedConnection = chatConnection.Connection != null;
            if (!hasActiveConnectionString && !hasCachedConnection)
            {
                return new MetadataLoadResult();
            }

            await SafeUpdateChatMessageAsync(statusBorder, "Loading schema cache... (2/3) Reading cached schema");
            var metadata = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hasActiveConnectionString)
                {
                    return MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(chatConnection.ActiveConnectionString!);
                }

                return MssqlIntelliSenseCacheReader.GetSchemaDetails(chatConnection.Connection!.Id).Metadata;
            }, cancellationToken);

            if (!string.IsNullOrWhiteSpace(chatConnection.ActiveDatabase))
            {
                await SafeUpdateChatMessageAsync(statusBorder, $"Loading schema cache... (3/3) Filtering database {chatConnection.ActiveDatabase}");
                metadata = await Task.Run(
                    () => MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, chatConnection.ActiveDatabase!),
                    cancellationToken);
            }
            else
            {
                await SafeUpdateChatMessageAsync(statusBorder, "Loading schema cache... (3/3) Preparing schema context");
            }

            return new MetadataLoadResult { Metadata = metadata };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Chat Agent Metadata Error] {ex}");
            return new MetadataLoadResult { ErrorMessage = ex.Message };
        }
    }

    private static string BuildSchemaLoadedMessage(DatabaseMetadata metadata)
    {
        return "Schema cache loaded. " +
               $"Tables: {metadata.Tables.Count}, Views: {metadata.Views.Count}, Procedures: {metadata.Procedures.Count}, " +
               $"Functions: {metadata.Functions.Count}, Foreign keys: {metadata.ForeignKeys.Count}, Indexes: {metadata.Indexes.Count}.";
    }

    private void LogAgentTrace(
        string model,
        ChatConnectionContext chatConnection,
        DatabaseMetadata? metadata,
        ISet<string> allowedToolNames,
        string toolContext,
        string systemPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Chat Agent Trace]");
        sb.AppendLine();
        sb.AppendLine("| Key | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Model | `{model}` |");
        sb.AppendLine($"| Connection | `{(string.IsNullOrWhiteSpace(chatConnection.DisplayName) ? "(none)" : chatConnection.DisplayName)}` |");
        sb.AppendLine($"| Active database | `{(string.IsNullOrWhiteSpace(chatConnection.ActiveDatabase) ? "(not specified)" : chatConnection.ActiveDatabase)}` |");
        sb.AppendLine($"| History turns sent | `{Math.Min(_chatHistory.Count, 12)}` |");
        sb.AppendLine($"| Allowed tools | `{(allowedToolNames.Count == 0 ? "(none)" : string.Join(", ", allowedToolNames))}` |");

        if (metadata != null)
        {
            sb.AppendLine($"| Tables | `{metadata.Tables.Count}` |");
            sb.AppendLine($"| Views | `{metadata.Views.Count}` |");
            sb.AppendLine($"| Procedures | `{metadata.Procedures.Count}` |");
            sb.AppendLine($"| Functions | `{metadata.Functions.Count}` |");
            sb.AppendLine($"| Foreign keys | `{metadata.ForeignKeys.Count}` |");
            sb.AppendLine($"| Indexes | `{metadata.Indexes.Count}` |");
        }
        else
        {
            sb.AppendLine("| Schema cache | `(unavailable)` |");
        }

        sb.AppendLine();
        sb.AppendLine("### Approved tool context");
        sb.AppendLine("```text");
        sb.AppendLine(string.IsNullOrWhiteSpace(toolContext) ? "(empty)" : toolContext);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### System prompt");
        sb.AppendLine("```text");
        sb.AppendLine(systemPrompt);
        sb.AppendLine("```");
        MssqlIntelliSensePackage.Log(sb.ToString());
    }

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
            await SafeUpdateChatMessageAsync(statusBorder, "Checking local actions...");
            var localPlannerResult = TryBuildLocalToolPlannerResult(userMessage, toolOutputs, allowedToolNames);
            if (localPlannerResult?.ToolCall != null)
            {
                MssqlIntelliSensePackage.Log($"[Chat Agent Tool Planner] Using local action '{localPlannerResult.ToolCall.Name}'.");
                await SafeUpdateChatMessageAsync(statusBorder, $"Using local action {localPlannerResult.ToolCall.Name}.");
                await ExecuteToolCallWithApprovalAsync(
                    localPlannerResult.ToolCall,
                    metadata,
                    chatConnection,
                    toolOutputs,
                    cancellationToken);
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

    private async Task<bool> ExecuteToolCallWithApprovalAsync(
        OpenAiSqlToolCall toolCall,
        DatabaseMetadata? metadata,
        ChatConnectionContext chatConnection,
        List<string> toolOutputs,
        CancellationToken cancellationToken)
    {
        var approved = await RequestToolApprovalAsync(toolCall, cancellationToken).ConfigureAwait(false);
        string output;
        if (approved)
        {
            var toolStatusBorder = await AddChatMessageOnDispatcherAsync(
                "Tool",
                $"Running {toolCall.Name}...",
                isUser: false,
                isStreaming: true,
                cancellationToken);

            try
            {
                output = await Task.Run(
                    async () => await ExecuteApprovedToolAsync(toolCall, metadata ?? DatabaseMetadata.Empty, chatConnection, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                var formattedOutput = await Task.Run(
                    () => ChatToolOutputFormatter.FormatForChat(toolCall.Name, output),
                    cancellationToken).ConfigureAwait(false);
                output = formattedOutput;
                await SafeUpdateChatMessageAsync(
                    toolStatusBorder,
                    formattedOutput);
            }
            catch (OperationCanceledException)
            {
                await SafeUpdateChatMessageAsync(toolStatusBorder, $"Cancelled {toolCall.Name}");
                throw;
            }
            catch (Exception ex)
            {
                output = $"## Tool: {toolCall.Name}\n\n**Error:** {ex.Message}";
                MssqlIntelliSensePackage.Log($"[Chat Agent Tool Execute Error] {ex}");
                await SafeUpdateChatMessageAsync(
                    toolStatusBorder,
                    $"Tool error {toolCall.Name}\n{ex.Message}");
            }
        }
        else
        {
            output = $"## Tool: {toolCall.Name}\n\n**Error:** Tool call rejected by user.";
            await AddChatMessageOnMainThreadAsync(
                "Tool",
                $"Rejected {toolCall.Name}",
                isUser: false,
                cancellationToken);
        }

        toolOutputs.Add($"Tool: {toolCall.Name}\nArguments: {toolCall.ArgumentsJson}\nOutput: {output}");
        MssqlIntelliSensePackage.Log($"[Chat Agent Tool Output] Tool '{toolCall.Name}' returned {output.Length:n0} characters for assistant context.");
        return approved;
    }

    private static ToolPlannerResult? TryBuildLocalToolPlannerResult(
        string userMessage,
        IReadOnlyList<string> toolOutputs,
        ISet<string> allowedToolNames)
    {
        if (toolOutputs.Count > 0 || string.IsNullOrWhiteSpace(userMessage))
        {
            return null;
        }

        var text = userMessage.Trim();
        var lower = text.ToLowerInvariant();

        if (IsEndpointRequest(lower) && allowedToolNames.Contains(ListEndpointsToolName))
        {
            return CreateLocalToolPlannerResult(ListEndpointsToolName, text);
        }

        if (IsListTablesRequest(lower) && allowedToolNames.Contains(ListTablesToolName))
        {
            return CreateLocalToolPlannerResult(ListTablesToolName, text);
        }

        var objectName = ExtractLikelyObjectName(text);
        if (!string.IsNullOrWhiteSpace(objectName))
        {
            if (IsIndexRequest(lower) && allowedToolNames.Contains(TableIndexesToolName))
            {
                return CreateLocalToolPlannerResult(TableIndexesToolName, objectName);
            }

            if (IsRelationRequest(lower) && allowedToolNames.Contains(TableRelationsToolName))
            {
                return CreateLocalToolPlannerResult(TableRelationsToolName, objectName);
            }

            if (IsSchemaRequest(lower) && allowedToolNames.Contains(TableSchemaToolName))
            {
                return CreateLocalToolPlannerResult(TableSchemaToolName, objectName);
            }
        }

        if (IsColumnRequest(lower) && allowedToolNames.Contains(FindColumnToolName))
        {
            return CreateLocalToolPlannerResult(FindColumnToolName, text);
        }

        if (allowedToolNames.Contains(SearchObjectsToolName))
        {
            return CreateLocalToolPlannerResult(SearchObjectsToolName, text);
        }

        return null;
    }
    private static ToolPlannerResult CreateLocalToolPlannerResult(string toolName, string query)
    {
        var (schemaName, tableName) = SplitObjectName(query);
        var arguments = JsonSerializer.Serialize(new
        {
            schemaName,
            tableName,
            query = TruncateToolQuery(query),
            columnName = string.Empty
        }, JsonOptions);

        var toolCall = new OpenAiSqlToolCall(toolName, arguments, SqlMetadataToolExecutor.GetToolDescription(toolName));
        return new ToolPlannerResult("tool_call", toolCall);
    }

    private static bool IsEndpointRequest(string lower) =>
        lower.Contains("endpoint") || lower.Contains("end point");

    private static bool IsListTablesRequest(string lower) =>
        (lower.Contains("list") || lower.Contains("show") || lower.Contains("liệt kê") || lower.Contains("danh sách")) &&
        (lower.Contains("table") || lower.Contains("tables") || lower.Contains("bảng") || lower.Contains("bang") || lower.Contains("bnagr"));

    private static bool IsSchemaRequest(string lower) =>
        lower.Contains("schema") || lower.Contains("column") || lower.Contains("columns") ||
        lower.Contains("cột") || lower.Contains("kiểu dữ liệu") || lower.Contains("primary key");

    private static bool IsRelationRequest(string lower) =>
        lower.Contains("relation") || lower.Contains("relationship") || lower.Contains("foreign key") ||
        lower.Contains("references") || lower.Contains("khóa ngoại") || lower.Contains("quan hệ");

    private static bool IsIndexRequest(string lower) =>
        lower.Contains("index") || lower.Contains("indexes") || lower.Contains("chỉ mục");

    private static bool IsColumnRequest(string lower) =>
        lower.Contains("column") || lower.Contains("columns") || lower.Contains("field") || lower.Contains("cột");

    private static string ExtractLikelyObjectName(string text)
    {
        foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', ':', '(', ')', '[', ']', '"', '\'', '`' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (trimmed.Length < 2 || !trimmed.Any(char.IsLetter))
            {
                continue;
            }

            if (trimmed.Contains(".") || trimmed.Contains("_"))
            {
                return trimmed.Trim('.');
            }
        }

        return string.Empty;
    }

    private static (string schemaName, string tableName) SplitObjectName(string query)
    {
        var objectName = ExtractLikelyObjectName(query);
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return (string.Empty, string.Empty);
        }

        var parts = objectName.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return (parts[parts.Length - 2], parts[parts.Length - 1]);
        }

        return (string.Empty, objectName);
    }

    private static string TruncateToolQuery(string query)
    {
        var normalized = query.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 240 ? normalized : normalized.Substring(0, 240);
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

    private async Task<bool> RequestToolApprovalAsync(OpenAiSqlToolCall toolCall, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        await MssqlIntelliSensePackage.SwitchToMainThreadAsync(cancellationToken);
        AddToolApprovalCard(toolCall, tcs);
        return await tcs.Task.ConfigureAwait(false);
    }

    private sealed class MessageControlState
    {
        public MessageControlState(string sender, StackPanel contentPanel, Brush foreground, Brush borderBrush, Brush codeBackground, bool renderMarkdown)
        {
            Sender = sender;
            ContentPanel = contentPanel;
            Foreground = foreground;
            BorderBrush = borderBrush;
            CodeBackground = codeBackground;
            RenderMarkdown = renderMarkdown;
        }

        public string Sender { get; }
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
        state.ContentPanel.Children.Clear();

        if (state.Sender.Equals("Tool", StringComparison.OrdinalIgnoreCase))
        {
            RenderToolMessageContent(state, message);
            return;
        }

        var richTextBox = ChatMarkdownRenderer.CreateSelectableMessageBox(state.Foreground, state.CodeBackground);
        if (state.RenderMarkdown)
        {
            richTextBox.Document = ChatMarkdownRenderer.CreateMarkdownDocument(message, state.Foreground, state.CodeBackground);
        }
        else
        {
            richTextBox.Document = ChatMarkdownRenderer.CreatePlainTextDocument(message, state.Foreground);
        }

        state.ContentPanel.Children.Add(richTextBox);
    }

    private static void RenderToolMessageContent(MessageControlState state, string message)
    {
        var toolName = ExtractToolName(message);
        var status = GetToolMessageStatus(message);

        var tags = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 7),
            Orientation = Orientation.Horizontal
        };
        tags.Children.Add(CreateToolTag("TOOL", state.Foreground, state.BorderBrush, state.CodeBackground, true));
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            tags.Children.Add(CreateToolTag(toolName, state.Foreground, state.BorderBrush, state.CodeBackground, false));
        }
        tags.Children.Add(CreateToolTag(status, state.Foreground, state.BorderBrush, state.CodeBackground, false));
        state.ContentPanel.Children.Add(tags);

        var richTextBox = ChatMarkdownRenderer.CreateSelectableMessageBox(state.Foreground, state.CodeBackground);
        richTextBox.Document = ChatMarkdownRenderer.CreateMarkdownDocument(message, state.Foreground, state.CodeBackground);
        state.ContentPanel.Children.Add(richTextBox);
    }

    private static Border CreateToolTag(string text, Brush foreground, Brush borderBrush, Brush backgroundBrush, bool strong)
    {
        return new Border
        {
            Background = strong ? ChatMarkdownRenderer.CreateOpacityBrush(foreground, 0.14) : backgroundBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 5, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 10.5,
                FontWeight = strong ? FontWeights.Bold : FontWeights.SemiBold,
                FontFamily = strong
                    ? new FontFamily("Segoe UI")
                    : new FontFamily("Consolas, Courier New, monospace")
            }
        };
    }

    private static string ExtractToolName(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var firstLine = message.Replace("\r\n", "\n").Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        if (firstLine.StartsWith("## Tool:", StringComparison.OrdinalIgnoreCase))
        {
            return firstLine.Substring("## Tool:".Length).Trim();
        }

        foreach (var prefix in new[] { "Running ", "Cancelled ", "Rejected ", "Blocked " })
        {
            if (firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return firstLine.Substring(prefix.Length).Trim().TrimEnd('.');
            }
        }

        if (firstLine.StartsWith("Tool error ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = firstLine.Substring("Tool error ".Length).Trim();
            var newline = rest.IndexOf('\n');
            return newline >= 0 ? rest.Substring(0, newline).Trim() : rest;
        }

        return string.Empty;
    }

    private static string GetToolMessageStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Pending";
        }

        var text = message.Trim();
        if (text.StartsWith("Running ", StringComparison.OrdinalIgnoreCase))
        {
            return "Running";
        }

        if (text.StartsWith("Cancelled ", StringComparison.OrdinalIgnoreCase))
        {
            return "Cancelled";
        }

        if (text.StartsWith("Rejected ", StringComparison.OrdinalIgnoreCase))
        {
            return "Rejected";
        }

        if (text.StartsWith("Blocked ", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked";
        }

        if (text.StartsWith("Tool error ", StringComparison.OrdinalIgnoreCase) ||
            text.IndexOf("**Error:**", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Error";
        }

        return "Completed";
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

        var tags = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 6),
            Orientation = Orientation.Horizontal
        };
        tags.Children.Add(CreateToolTag("APPROVAL", textBrush, borderBrush, backgroundBrush, true));
        tags.Children.Add(CreateToolTag(toolCall.Name, textBrush, borderBrush, backgroundBrush, false));
        container.Children.Add(tags);

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

        var argumentsBox = ChatMarkdownRenderer.CreateSelectableMessageBox(textBrush, backgroundBrush);
        argumentsBox.Height = 72;
        argumentsBox.MinHeight = 56;
        argumentsBox.Margin = new Thickness(0, 0, 0, 6);
        argumentsBox.Document = ChatMarkdownRenderer.CreateMarkdownDocument(
            "```json\n" + FormatJsonForDisplay(toolCall.ArgumentsJson) + "\n```",
            textBrush,
            backgroundBrush);
        container.Children.Add(argumentsBox);

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

    private static string FormatJsonForDisplay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        var jsonText = json!;
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            return JsonSerializer.Serialize(document.RootElement, DisplayJsonOptions);
        }
        catch (JsonException)
        {
            return jsonText;
        }
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

    private static async Task<string> ExecuteApprovedToolAsync(
        OpenAiSqlToolCall toolCall,
        DatabaseMetadata metadata,
        ChatConnectionContext chatConnection,
        CancellationToken cancellationToken)
    {
        var argumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson;
        if (!string.Equals(toolCall.Name, ExecuteSqlToolName, StringComparison.OrdinalIgnoreCase))
        {
            return await SqlMetadataToolExecutor.ExecuteToolAsync(
                toolCall.Name,
                argumentsJson,
                metadata);
        }

        var query = ToolArgumentReader.GetString(argumentsJson, "query");
        return await SqlReadOnlyQueryExecutor.ExecuteAsync(
            chatConnection.ActiveConnectionString ?? string.Empty,
            chatConnection.ActiveDatabase,
            query);
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

        AddRecentChatHistory(messages);
        messages.Add(new UserChatMessage(message));
        return messages;
    }

    private void AddRecentChatHistory(List<ChatMessage> messages)
    {
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
        await AddChatMessageOnDispatcherAsync(
            sender,
            message,
            isUser,
            isStreaming: false,
            cancellationToken);
    }

    private async Task<Border?> AddChatMessageOnDispatcherAsync(
        string sender,
        string message,
        bool isUser,
        bool isStreaming,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return null;
            }

            if (Dispatcher.CheckAccess())
            {
                return AddChatMessage(sender, message, isUser, isStreaming);
            }

#pragma warning disable VSTHRD001
            return await Dispatcher.InvokeAsync(
                () => AddChatMessage(sender, message, isUser, isStreaming),
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);
#pragma warning restore VSTHRD001
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MssqlIntelliSensePackage.Log($"[Chat Agent UI Add Error] {ex}");
            return null;
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
            sb.AppendLine("\nDatabase schema cache is available locally, but detailed object metadata is not included in this prompt.");
            sb.AppendLine($"Schema cache counts: Tables={metadata.Tables.Count}, Views={metadata.Views.Count}, Procedures={metadata.Procedures.Count}, Functions={metadata.Functions.Count}, ForeignKeys={metadata.ForeignKeys.Count}, Indexes={metadata.Indexes.Count}.");
            sb.AppendLine("Do not invent table names, columns, relationships, indexes, procedures, or views. Use only approved tool output for object-specific answers.");
        }

        sb.AppendLine("\nPlease format your answers using markdown.");
        return sb.ToString();
    }

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
        var state = new MessageControlState(sender, contentPanel, messageForeground, borderBrush, messageBackground, renderMarkdown: !isUser);
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

    private Brush GetThemeBrush(object key, Color defaultColor)
    {
        return TryFindResource(key) as Brush ?? new SolidColorBrush(defaultColor);
    }
}



