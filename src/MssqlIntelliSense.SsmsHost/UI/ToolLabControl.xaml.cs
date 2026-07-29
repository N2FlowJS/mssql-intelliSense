using System;
using System.Collections;
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ObservableCollection<ConnectionInfo> _connections = new();

    private sealed class ToolConnectionContext
    {
        public ConnectionInfo? Connection { get; set; }
        public string? ActiveConnectionString { get; set; }
        public string? ActiveDatabase { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    public ToolLabControl()
    {
        InitializeComponent();
        ConnectionsComboBox.ItemsSource = _connections;
        _ = RefreshConnectionsAsync();
    }

    private void RefreshConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshConnectionsAsync();
    }

    private void RunToolButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunToolAsync();
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
            else if (_connections.Count > 0 && ConnectionsComboBox.SelectedItem == null)
            {
                ConnectionsComboBox.SelectedIndex = 0;
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
            RunToolButton.IsEnabled = false;
            OutputTextBox.Text = "Running tool...";
            OutputDataGrid.ItemsSource = null;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var toolConnection = ResolveToolConnectionContext(registerIfMissing: true);
            var connection = toolConnection.Connection ?? ConnectionsComboBox.SelectedItem as ConnectionInfo;
            if (connection == null)
            {
                OutputTextBox.Text = "No active or selected cached connection found.";
                return;
            }

            if (!_connections.Any(c => c.Id == connection.Id))
            {
                _connections.Add(connection);
            }

            ConnectionsComboBox.SelectedItem = _connections.FirstOrDefault(c => c.Id == connection.Id) ?? connection;

            var toolName = GetSelectedToolName();
            var schemaName = string.IsNullOrWhiteSpace(SchemaTextBox.Text)
                ? "dbo"
                : SchemaTextBox.Text.Trim();
            var tableName = TableTextBox.Text?.Trim() ?? string.Empty;
            var query = QueryTextBox.Text?.Trim() ?? string.Empty;

            var metadata = await Task.Run(() =>
            {
                DatabaseMetadata result;
                var activeConnectionString = toolConnection.ActiveConnectionString;
                if (!string.IsNullOrWhiteSpace(activeConnectionString))
                {
                    result = MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(activeConnectionString!);
                }
                else
                {
                    result = MssqlIntelliSenseCacheReader.GetSchemaDetails(connection.Id).Metadata;
                }

                var activeDatabase = toolConnection.ActiveDatabase;
                return string.IsNullOrWhiteSpace(activeDatabase)
                    ? result
                    : MssqlIntelliSenseCacheReader.FilterByDatabase(result, activeDatabase!);
            });
            var arguments = JsonSerializer.Serialize(new { schemaName, tableName, query, columnName = query }, JsonOptions);
            using var doc = JsonDocument.Parse(arguments);
            var output = await SqlMetadataToolExecutor.ExecuteToolAsync(toolName, doc.RootElement, metadata);

            var connectionHeader = string.IsNullOrWhiteSpace(toolConnection.DisplayName)
                ? $"Connection: {connection.Name}"
                : $"Connection: {toolConnection.DisplayName}";
            OutputTextBox.Text = connectionHeader + Environment.NewLine + PrettyPrintJson(output);
            OutputDataGrid.ItemsSource = SqlMetadataToolExecutor.BuildPreviewRows(toolName, metadata, schemaName, tableName, query);
        }
        catch (Exception ex)
        {
            OutputTextBox.Text = "Tool execution failed: " + ex.Message;
            MssqlIntelliSensePackage.Log($"[Tool Lab Execute Error] {ex}");
        }
        finally
        {
            RunToolButton.IsEnabled = true;
        }
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
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

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

        if (ConnectionsComboBox.SelectedItem is ConnectionInfo selected)
        {
            return new ToolConnectionContext
            {
                Connection = selected,
                ActiveConnectionString = selected.ConnectionString,
                DisplayName = selected.Name
            };
        }

        return new ToolConnectionContext();
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
