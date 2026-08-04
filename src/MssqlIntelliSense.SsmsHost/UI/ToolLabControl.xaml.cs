using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using MssqlIntelliSense.Core.Ai;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

public partial class ToolLabControl : UserControl
{
    private const string ListTablesToolName = SqlMetadataToolExecutor.ListTablesToolName;
    private const string TableSchemaToolName = SqlMetadataToolExecutor.TableSchemaToolName;
    private const string TableRelationsToolName = SqlMetadataToolExecutor.TableRelationsToolName;
    private const string TableIndexesToolName = SqlMetadataToolExecutor.TableIndexesToolName;
    private const string SearchObjectsToolName = SqlMetadataToolExecutor.SearchObjectsToolName;
    private const string FindColumnToolName = SqlMetadataToolExecutor.FindColumnToolName;
    private const string ListEndpointsToolName = SqlMetadataToolExecutor.ListEndpointsToolName;
    private const string ExecuteSqlToolName = SqlMetadataToolExecutor.ExecuteSqlToolName;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ObservableCollection<ConnectionInfo> _connections = new();
    private ConnectionInfo? _selectedConnection;
    private string? _selectedDatabase;
    private bool _showConnectionSelector = true;

    public bool ShowConnectionSelector
    {
        get => _showConnectionSelector;
        set
        {
            _showConnectionSelector = value;
            if (ConnectionSelectorPanel != null)
            {
                ConnectionSelectorPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private sealed class ToolConnectionContext
    {
        public ConnectionInfo? Connection { get; set; }
        public string? ActiveConnectionString { get; set; }
        public string? ActiveDatabase { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class ToolRunRequest
    {
        public int ConnectionId { get; set; }
        public string ConnectionName { get; set; } = string.Empty;
        public string? ActiveConnectionString { get; set; }
        public string? ActiveDatabase { get; set; }
        public string DatabaseName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ToolName { get; set; } = ListTablesToolName;
        public string SchemaName { get; set; } = "dbo";
        public string TableName { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
    }

    private sealed class ToolRunResult
    {
        public string OutputText { get; set; } = string.Empty;
        public IList<object>? PreviewRows { get; set; }
    }

    private sealed class ObjectSearchPreviewRow
    {
        public string Kind { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public string Schema { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Score { get; set; }
        public int LexicalScore { get; set; }
        public double SemanticScore { get; set; }
    }

    public event EventHandler? ToolExecutionCompleted;

    public ToolLabControl()
    {
        InitializeComponent();
        ConnectionsComboBox.ItemsSource = _connections;
        _ = RefreshConnectionsAsync();
    }

    public void SetSelectedConnection(ConnectionInfo? connection, string? databaseName = null)
    {
        _selectedConnection = connection;
        _selectedDatabase = databaseName;

        if (DatabaseTextBox != null && string.IsNullOrWhiteSpace(DatabaseTextBox.Text))
        {
            DatabaseTextBox.Text = databaseName?.Trim() ?? string.Empty;
        }

        if (connection != null)
        {
            var existing = _connections.FirstOrDefault(c => c.Id == connection.Id);
            if (existing == null)
            {
                _connections.Add(connection);
                existing = connection;
            }

            ConnectionsComboBox.SelectedItem = existing;
        }
    }

    private void RefreshConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshConnectionsButtonClickAsync();
    }

    private async Task RefreshConnectionsButtonClickAsync()
    {
        try
        {
            await RefreshConnectionsAsync();
        }
        catch (Exception ex)
        {
            await UpdateToolUiAsync(() => OutputTextBox.Text = "Failed to load connections: " + ex.Message);
            MssqlIntelliSensePackage.Log($"[Tool Lab Load Connections Handler Error] {ex}");
        }
    }

    private void RunToolButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunToolButtonClickAsync();
    }

    private async Task RunToolButtonClickAsync()
    {
        try
        {
            await RunToolAsync();
        }
        catch (Exception ex)
        {
            await UpdateToolUiAsync(() => OutputTextBox.Text = "Tool execution failed: " + ex.Message);
            MssqlIntelliSensePackage.Log($"[Tool Lab Execute Handler Error] {ex}");
        }
    }

    private async Task RefreshConnectionsAsync()
    {
        try
        {
            RefreshConnectionsButton.IsEnabled = false;
            OutputTextBox.Text = "Loading cached connections...";
            OutputDataGrid.ItemsSource = null;

            var connections = await Task.Run(MssqlIntelliSenseCacheReader.GetConnections);
            _connections.Clear();
            foreach (var connection in connections.OrderBy(c => c.Name))
            {
                _connections.Add(connection);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var activeContext = ResolveToolConnectionContext(registerIfMissing: true);
            if (activeContext.Connection != null)
            {
                var activeItem = _connections.FirstOrDefault(c => c.Id == activeContext.Connection.Id);
                if (activeItem == null)
                {
                    _connections.Add(activeContext.Connection);
                    activeItem = activeContext.Connection;
                }

                ConnectionsComboBox.SelectedItem = activeItem;
            }
            else if (_selectedConnection != null)
            {
                SetSelectedConnection(_selectedConnection, _selectedDatabase);
            }
            else if (_connections.Count > 0 && ConnectionsComboBox.SelectedItem == null)
            {
                ConnectionsComboBox.SelectedIndex = 0;
            }

            if (string.IsNullOrWhiteSpace(DatabaseTextBox.Text) && !string.IsNullOrWhiteSpace(activeContext.ActiveDatabase))
            {
                DatabaseTextBox.Text = activeContext.ActiveDatabase;
            }

            OutputTextBox.Text = !string.IsNullOrWhiteSpace(activeContext.DisplayName)
                ? $"Active connection: {activeContext.DisplayName}"
                : _connections.Count == 0
                ? "No cached connections found."
                : $"Loaded {_connections.Count} cached connection(s).";
        }
        catch (Exception ex)
        {
            OutputTextBox.Text = "Failed to load connections: " + ex.Message;
            MssqlIntelliSensePackage.Log($"[Tool Lab Load Connections Error] {ex}");
        }
        finally
        {
            RefreshConnectionsButton.IsEnabled = true;
        }
    }

    private async Task RunToolAsync()
    {
        try
        {
            await UpdateToolUiAsync(() =>
            {
                RunToolButton.IsEnabled = false;
                OutputTextBox.Text = "Running tool...";
                OutputDataGrid.ItemsSource = null;
            });

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var request = CaptureRunRequest();
            if (request == null)
            {
                await UpdateToolUiAsync(() => OutputTextBox.Text = "No active or selected cached connection found.");
                return;
            }

            var result = await Task.Run(async () => await ExecuteToolRequestAsync(request));
            await UpdateToolUiAsync(() =>
            {
                OutputTextBox.Text = result.OutputText;
                OutputDataGrid.ItemsSource = result.PreviewRows;
            });
            ToolExecutionCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            await UpdateToolUiAsync(() => OutputTextBox.Text = "Tool execution failed: " + ex.Message);
            MssqlIntelliSensePackage.Log($"[Tool Lab Execute Error] {ex}");
        }
        finally
        {
            await UpdateToolUiAsync(() => RunToolButton.IsEnabled = true);
        }
    }

    private ToolRunRequest? CaptureRunRequest()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        EnsureOnUiThread();
        var toolConnection = ResolveToolConnectionContext(registerIfMissing: true);
        var connection = toolConnection.Connection ?? ConnectionsComboBox.SelectedItem as ConnectionInfo;
        if (connection == null)
        {
            return null;
        }

        if (!_connections.Any(c => c.Id == connection.Id))
        {
            _connections.Add(connection);
        }

        ConnectionsComboBox.SelectedItem = _connections.FirstOrDefault(c => c.Id == connection.Id) ?? connection;

        var userDatabase = DatabaseTextBox.Text?.Trim() ?? string.Empty;
        var activeDb = !string.IsNullOrWhiteSpace(userDatabase)
            ? userDatabase
            : toolConnection.ActiveDatabase;

        return new ToolRunRequest
        {
            ConnectionId = connection.Id,
            ConnectionName = connection.Name,
            ActiveConnectionString = toolConnection.ActiveConnectionString,
            ActiveDatabase = activeDb,
            DatabaseName = userDatabase,
            DisplayName = toolConnection.DisplayName,
            ToolName = GetSelectedToolName(),
            SchemaName = string.IsNullOrWhiteSpace(SchemaTextBox.Text) ? "dbo" : SchemaTextBox.Text.Trim(),
            TableName = TableTextBox.Text?.Trim() ?? string.Empty,
            Query = QueryTextBox.Text?.Trim() ?? string.Empty
        };
    }

    private static async Task<ToolRunResult> ExecuteToolRequestAsync(ToolRunRequest request)
    {
        DatabaseMetadata metadata;
        if (!string.IsNullOrWhiteSpace(request.ActiveConnectionString))
        {
            metadata = MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(request.ActiveConnectionString!);
        }
        else
        {
            metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(request.ConnectionId).Metadata;
        }

        var dbFilter = !string.IsNullOrWhiteSpace(request.DatabaseName)
            ? request.DatabaseName
            : request.ActiveDatabase;

        if (!string.IsNullOrWhiteSpace(dbFilter))
        {
            metadata = MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, dbFilter!);
        }

        var arguments = JsonSerializer.Serialize(
            new
            {
                database = dbFilter ?? string.Empty,
                databaseName = dbFilter ?? string.Empty,
                schemaName = request.SchemaName,
                tableName = request.TableName,
                query = request.Query,
                columnName = request.Query
            },
            JsonOptions);
        using var doc = JsonDocument.Parse(arguments);
        var output = await SqlMetadataToolExecutorBridge.ExecuteToolAsync(
            request.ToolName,
            doc.RootElement,
            metadata,
            query => SqlReadOnlyQueryExecutor.ExecuteAsync(
                request.ActiveConnectionString ?? string.Empty,
                dbFilter,
                query));
        var previewRows = IsObjectSearchTool(request.ToolName)
            ? ExtractObjectSearchPreviewRows(output)
            : SqlMetadataToolExecutor.BuildPreviewRows(
                request.ToolName,
                metadata,
                request.SchemaName,
                request.TableName,
                request.Query)
            ?.Cast<object>()
            .ToList();

        var connectionHeader = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"Connection: {request.ConnectionName}"
            : $"Connection: {request.DisplayName}";

        return new ToolRunResult
        {
            OutputText = connectionHeader + Environment.NewLine + PrettyPrintJson(output),
            PreviewRows = previewRows
        };
    }

    private static bool IsObjectSearchTool(string toolName) =>
        string.Equals(toolName, SearchObjectsToolName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, SqlMetadataToolExecutor.SearchSchemaObjectsToolName, StringComparison.OrdinalIgnoreCase);

    private static IList<object> ExtractObjectSearchPreviewRows(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("matches", out var matches) ||
                matches.ValueKind != JsonValueKind.Array)
            {
                return new List<object>();
            }

            return matches.EnumerateArray()
                .Select(match => (object)new ObjectSearchPreviewRow
                {
                    Kind = GetJsonString(match, "kind"),
                    Database = GetJsonString(match, "database"),
                    Schema = GetJsonString(match, "schema"),
                    Name = GetJsonString(match, "name"),
                    Description = GetJsonString(match, "description"),
                    Score = GetJsonInt(match, "score"),
                    LexicalScore = GetJsonInt(match, "lexicalScore"),
                    SemanticScore = GetJsonDouble(match, "semanticScore")
                })
                .ToList();
        }
        catch
        {
            return new List<object>();
        }
    }

    private static string GetJsonString(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int GetJsonInt(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static double GetJsonDouble(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : 0;

    private Task UpdateToolUiAsync(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

#pragma warning disable VSTHRD001
        return Dispatcher.InvokeAsync(action).Task;
#pragma warning restore VSTHRD001
    }

    private string GetSelectedToolName()
    {
        if (ActionComboBox.SelectedItem is ComboBoxItem { Tag: string toolName })
        {
            return toolName;
        }

        return ListTablesToolName;
    }

    private ToolConnectionContext ResolveToolConnectionContext(bool registerIfMissing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        EnsureOnUiThread();

        var activeConnectionString = MssqlIntelliSensePackage.GetActiveConnectionString();
        var activeDatabase = MssqlIntelliSensePackage.GetActiveDatabaseName();
        if (!string.IsNullOrWhiteSpace(activeConnectionString))
        {
            var normalizedConnectionString = NormalizeServerConnectionString(activeConnectionString!);
            var cachedConnection = MssqlIntelliSenseCacheReader.GetConnections()
                .FirstOrDefault(c => NormalizeServerConnectionString(c.ConnectionString)
                    .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

            if (cachedConnection == null && registerIfMissing)
            {
                var serverName = GetServerName(activeConnectionString!);
                var name = string.IsNullOrWhiteSpace(serverName) ? "Active SQL connection" : serverName;
                var connectionId = MssqlIntelliSenseCacheWriter.RegisterConnection(normalizedConnectionString, name);
                cachedConnection = MssqlIntelliSenseCacheReader.GetConnections().FirstOrDefault(c => c.Id == connectionId);
            }

            return new ToolConnectionContext
            {
                Connection = cachedConnection,
                ActiveConnectionString = activeConnectionString,
                ActiveDatabase = activeDatabase,
                DisplayName = BuildConnectionDisplayName(activeConnectionString!, activeDatabase)
            };
        }

        if (_selectedConnection != null)
        {
            return new ToolConnectionContext
            {
                Connection = _selectedConnection,
                ActiveConnectionString = _selectedConnection.ConnectionString,
                ActiveDatabase = _selectedDatabase,
                DisplayName = BuildConnectionDisplayName(_selectedConnection.ConnectionString, _selectedDatabase)
            };
        }

        if (ConnectionsComboBox.SelectedItem is ConnectionInfo selected)
        {
            return new ToolConnectionContext
            {
                Connection = selected,
                ActiveConnectionString = selected.ConnectionString,
                ActiveDatabase = _selectedDatabase,
                DisplayName = selected.Name
            };
        }

        return new ToolConnectionContext();
    }

    private void EnsureOnUiThread()
    {
        if (!Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Tool Lab UI access must occur on the WPF dispatcher thread.");
        }
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

    private static string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        const int maxDisplayLength = 100000;

        try
        {
            using var document = JsonDocument.Parse(json);
            var formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            if (formatted.Length > maxDisplayLength)
            {
                return formatted.Substring(0, maxDisplayLength) + Environment.NewLine + $"... [Output truncated for UI performance. Total length: {formatted.Length} characters]";
            }
            return formatted;
        }
        catch
        {
            if (json.Length > maxDisplayLength)
            {
                return json.Substring(0, maxDisplayLength) + Environment.NewLine + $"... [Output truncated for UI performance. Total length: {json.Length} characters]";
            }
            return json;
        }
    }
}
