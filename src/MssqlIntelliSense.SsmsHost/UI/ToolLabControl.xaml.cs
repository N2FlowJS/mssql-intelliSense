using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

public partial class ToolLabControl : UserControl
{
    private const string ListTablesToolName = "list_tables";
    private const string TableSchemaToolName = "get_table_schema";
    private const string TableRelationsToolName = "get_table_relations";
    private const string TableIndexesToolName = "get_table_indexes";
    private const string SearchObjectsToolName = "search_objects";
    private const string FindColumnToolName = "find_column";
    private const string ListEndpointsToolName = "list_endpoints";

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
                    result = MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(activeConnectionString);
                }
                else
                {
                    result = MssqlIntelliSenseCacheReader.GetSchemaDetails(connection.Id).Metadata;
                }

                var activeDatabase = toolConnection.ActiveDatabase;
                return string.IsNullOrWhiteSpace(activeDatabase)
                    ? result
                    : MssqlIntelliSenseCacheReader.FilterByDatabase(result, activeDatabase);
            });
            var arguments = JsonSerializer.Serialize(new { schemaName, tableName, query, columnName = query }, JsonOptions);
            var output = await Task.Run(() => ExecuteTool(toolName, arguments, metadata));

            var connectionHeader = string.IsNullOrWhiteSpace(toolConnection.DisplayName)
                ? $"Connection: {connection.Name}"
                : $"Connection: {toolConnection.DisplayName}";
            OutputTextBox.Text = connectionHeader + Environment.NewLine + PrettyPrintJson(output);
            OutputDataGrid.ItemsSource = BuildPreviewRows(toolName, metadata, schemaName, tableName, query);
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
            var normalizedConnectionString = NormalizeServerConnectionString(activeConnectionString);
            var cachedConnection = MssqlIntelliSenseCacheReader.GetConnections()
                .FirstOrDefault(c => NormalizeServerConnectionString(c.ConnectionString)
                    .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

            if (cachedConnection == null && registerIfMissing)
            {
                var serverName = GetServerName(activeConnectionString);
                var name = string.IsNullOrWhiteSpace(serverName) ? "Active SQL connection" : serverName;
                var connectionId = MssqlIntelliSenseCacheWriter.RegisterConnection(normalizedConnectionString, name);
                cachedConnection = MssqlIntelliSenseCacheReader.GetConnections().FirstOrDefault(c => c.Id == connectionId);
            }

            return new ToolConnectionContext
            {
                Connection = cachedConnection,
                ActiveConnectionString = activeConnectionString,
                ActiveDatabase = activeDatabase,
                DisplayName = BuildConnectionDisplayName(activeConnectionString, activeDatabase)
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
            return connectionString;
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
            var database = string.IsNullOrWhiteSpace(activeDatabase) ? builder.InitialCatalog : activeDatabase;
            return string.IsNullOrWhiteSpace(database) ? server : $"{server} / {database}";
        }
        catch
        {
            return string.IsNullOrWhiteSpace(activeDatabase) ? connectionString : $"{connectionString} / {activeDatabase}";
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

    private static string ExecuteTool(string toolName, string argumentsJson, DatabaseMetadata metadata)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var arguments = document.RootElement;

        return toolName switch
        {
            ListTablesToolName => JsonSerializer.Serialize(new
            {
                tablesList = metadata.Tables.Select(t => new { database = t.Database, schema = t.Schema, name = t.Name }).ToList()
            }, JsonOptions),
            TableSchemaToolName => JsonSerializer.Serialize(GetTableSchemaToolResult(metadata, arguments), JsonOptions),
            TableRelationsToolName => JsonSerializer.Serialize(GetTableRelationsToolResult(metadata, arguments), JsonOptions),
            TableIndexesToolName => JsonSerializer.Serialize(GetTableIndexesToolResult(metadata, arguments), JsonOptions),
            SearchObjectsToolName => JsonSerializer.Serialize(GetSearchObjectsToolResult(metadata, arguments), JsonOptions),
            FindColumnToolName => JsonSerializer.Serialize(GetFindColumnToolResult(metadata, arguments), JsonOptions),
            ListEndpointsToolName => JsonSerializer.Serialize(new
            {
                endpoints = metadata.Endpoints.OrderBy(ep => ep.Name).Select(ep => new { ep.Name, ep.Type, ep.Protocol, ep.State, ep.Port }).ToList()
            }, JsonOptions),
            _ => JsonSerializer.Serialize(new { error = $"Tool '{toolName}' is not supported." }, JsonOptions)
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
        return new
        {
            query,
            matches = BuildObjectSearchRows(metadata, query)
        };
    }

    private static object GetFindColumnToolResult(DatabaseMetadata metadata, JsonElement arguments)
    {
        var query = GetArgument(arguments, "query", GetArgument(arguments, "columnName", string.Empty));
        return new
        {
            query,
            matches = BuildColumnSearchRows(metadata, query)
        };
    }

    private static IEnumerable? BuildPreviewRows(string toolName, DatabaseMetadata metadata, string schemaName, string tableName, string query)
    {
        return toolName switch
        {
            ListTablesToolName => metadata.Tables.Select(t => new { t.Database, t.Schema, t.Name }).ToList(),
            TableSchemaToolName => metadata.FindTable(schemaName, tableName)?.Columns.Select(c => new { c.Ordinal, c.Name, c.DataType, c.IsNullable }).ToList(),
            TableRelationsToolName => metadata.ForeignKeys.Where(fk => fk.FromTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) || fk.ToTable.Equals(tableName, StringComparison.OrdinalIgnoreCase)).ToList(),
            TableIndexesToolName => metadata.Indexes.Where(idx => idx.Table.Equals(tableName, StringComparison.OrdinalIgnoreCase)).Select(idx => new { idx.Schema, idx.Table, idx.Name, idx.IsUnique, Columns = string.Join(", ", idx.Columns) }).ToList(),
            SearchObjectsToolName => BuildObjectSearchRows(metadata, query),
            FindColumnToolName => BuildColumnSearchRows(metadata, query),
            ListEndpointsToolName => metadata.Endpoints.OrderBy(ep => ep.Name).Select(ep => new { ep.Name, ep.Type, ep.Protocol, ep.State, ep.Port }).ToList(),
            _ => null
        };
    }

    private static IEnumerable BuildObjectSearchRows(DatabaseMetadata metadata, string query)
    {
        return metadata.Tables.Select(t => new { Kind = "table", t.Database, t.Schema, t.Name })
            .Concat(metadata.Views.Select(v => new { Kind = "view", v.Database, v.Schema, v.Name }))
            .Concat(metadata.Procedures.Select(p => new { Kind = "procedure", p.Database, p.Schema, p.Name }))
            .Concat(metadata.Functions.Select(f => new { Kind = "function", f.Database, f.Schema, f.Name }))
            .Where(o => Matches(o.Name, query) || Matches(o.Schema + "." + o.Name, query))
            .OrderBy(o => o.Kind)
            .ThenBy(o => o.Schema)
            .ThenBy(o => o.Name)
            .Take(100)
            .ToList();
    }

    private static IEnumerable BuildColumnSearchRows(DatabaseMetadata metadata, string query)
    {
        var tableColumns = metadata.Tables.SelectMany(t => t.Columns.Select(c => new { Kind = "table", t.Database, t.Schema, ObjectName = t.Name, Column = c.Name, c.DataType, c.IsNullable }));
        var viewColumns = metadata.Views.SelectMany(v => v.Columns.Select(c => new { Kind = "view", v.Database, v.Schema, ObjectName = v.Name, Column = c.Name, c.DataType, c.IsNullable }));
        return tableColumns.Concat(viewColumns)
            .Where(c => Matches(c.Column, query))
            .OrderBy(c => c.Schema)
            .ThenBy(c => c.ObjectName)
            .ThenBy(c => c.Column)
            .Take(150)
            .ToList();
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

    private static string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
