using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;
using MssqlIntelliSense.Core;
using MssqlIntelliSense.Core.Cache;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;
using MssqlIntelliSense.SsmsHost;

namespace MssqlIntelliSense.DebugApp;

public partial class MainWindow : Window
{
    private static readonly string DebugLogPath = Path.Combine(Path.GetTempPath(), "mssql-intellisense-debugapp.log");
    private readonly SqlCompletionProvider _completionProvider = new();
    private DatabaseMetadata? _currentMetadata;
    private ConnectionInfo? _savedConnection;
    private string _savedConnectionString = string.Empty;
    private string _savedDatabaseName = string.Empty;
    private bool _isInitialized;
    private bool _isLoadingSavedContext;

    public MainWindow()
    {
        InitializeComponent();
        _isInitialized = true;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            DebugLog("MainWindow loaded.");
            LoadExistingSsmsContext();
            _ = LoadCacheJsonAsync();
            _ = TriggerCompletionAsync();
        }
        catch (Exception ex)
        {
            DebugLog("Initialization error", ex);
            if (StatusBarText != null) StatusBarText.Text = "Init error: " + ex.Message;
        }
    }

    private void LoadExistingSsmsContext()
    {
        DebugLog("Loading saved SSMS config/cache.");
        _currentMetadata = null;
        var settings = MssqlIntelliSenseConfig.GetLlmSettings();
        DebugLog($"Config loaded. ApiKeyPresent={!string.IsNullOrWhiteSpace(settings.ApiKey)}, Model={settings.Model}, Endpoint={settings.Endpoint}");
        if (!string.IsNullOrWhiteSpace(settings.ApiKey) && ApiKeyPasswordBox != null)
        {
            ApiKeyPasswordBox.Password = settings.ApiKey;
        }

        MssqlIntelliSensePackage.DebugActiveConnectionString = null;
        MssqlIntelliSensePackage.DebugActiveDatabaseName = null;

        string? connectionString = null;
        string? databaseName = null;
        ConnectionInfo? cachedConnection = null;
        var source = "saved SSMS cache";

        var cachedConnections = MssqlIntelliSenseCacheReader.GetConnections()
            .OrderByDescending(c => c.SchemaUpdatedAt.HasValue)
            .ThenByDescending(c => c.SchemaUpdatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.LastSeenAt ?? DateTimeOffset.MinValue)
            .ToArray();
        cachedConnection = cachedConnections.FirstOrDefault();
        DebugLog(cachedConnection == null
            ? "No saved cached connection found."
            : $"Cached connection selected. Id={cachedConnection.Id}, Name={cachedConnection.Name}, HasSchema={cachedConnection.SchemaUpdatedAt.HasValue}, Conn={cachedConnection.ConnectionString}");

        if (cachedConnection != null)
        {
            connectionString = cachedConnection.ConnectionString;
            databaseName = ResolveDatabaseName(cachedConnection, connectionString);
            DebugLog($"Resolved saved database: {databaseName}");
            source = string.IsNullOrWhiteSpace(cachedConnection.Name)
                ? "saved SSMS cache"
                : $"saved SSMS cache: {cachedConnection.Name}";
        }

        _savedConnection = cachedConnection;
        _savedConnectionString = connectionString ?? string.Empty;
        _savedDatabaseName = databaseName ?? string.Empty;

        PopulateSavedConnectionSelector(cachedConnections, cachedConnection);
        PopulateSavedDatabaseSelector(cachedConnection, _savedDatabaseName);
        ApplySavedConnectionToEmbeddedControls();
        ApplySavedLlmConfigToDebugOptions();

        if (StatusBarText != null)
        {
            StatusBarText.Text = string.IsNullOrWhiteSpace(_savedConnectionString)
                ? "No saved SSMS cache"
                : $"Loaded: {_savedDatabaseName}";
        }

        if (SsmsContextText != null)
        {
            SsmsContextText.Text = string.IsNullOrWhiteSpace(_savedConnectionString)
                ? "No saved SSMS cache loaded"
                : $"Using {source} / {_savedDatabaseName}";
        }
        DebugLog($"Saved context applied. ConnectionPresent={!string.IsNullOrWhiteSpace(_savedConnectionString)}, Database={_savedDatabaseName}");
    }

    private void PopulateSavedConnectionSelector(ConnectionInfo[] connections, ConnectionInfo? selectedConnection)
    {
        if (SavedConnectionComboBox == null)
        {
            return;
        }

        _isLoadingSavedContext = true;
        try
        {
            SavedConnectionComboBox.ItemsSource = connections
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .ToArray();
            SavedConnectionComboBox.IsEnabled = connections.Length > 1;

            if (selectedConnection != null)
            {
                SavedConnectionComboBox.SelectedItem = connections.FirstOrDefault(c => c.Id == selectedConnection.Id);
            }
            else
            {
                SavedConnectionComboBox.SelectedIndex = -1;
            }
        }
        finally
        {
            _isLoadingSavedContext = false;
        }

        DebugLog($"Connection selector loaded. Count={connections.Length}, Selected={selectedConnection?.Name}");
    }

    private void ApplySavedConnectionToEmbeddedControls()
    {
        if (DebugChatAgentControl != null)
        {
            DebugChatAgentControl.SetSelectedConnection(_savedConnection, _savedDatabaseName);
        }

        if (DebugToolLabControl != null)
        {
            DebugToolLabControl.SetSelectedConnection(_savedConnection, _savedDatabaseName);
        }
    }

    private void PopulateSavedDatabaseSelector(ConnectionInfo? connection, string selectedDatabase)
    {
        if (SavedDatabaseComboBox == null)
        {
            return;
        }

        var databases = Array.Empty<string>();
        if (connection != null)
        {
            try
            {
                var metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(connection.Id).Metadata;
                databases = metadata.Databases
                    .Concat(metadata.Tables.Select(t => t.Database))
                    .Concat(metadata.Views.Select(v => v.Database))
                    .Where(db => !string.IsNullOrWhiteSpace(db))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(db => db)
                    .ToArray();
            }
            catch (Exception ex)
            {
                DebugLog("PopulateSavedDatabaseSelector failed", ex);
            }
        }

        _isLoadingSavedContext = true;
        try
        {
            SavedDatabaseComboBox.ItemsSource = databases;
            SavedDatabaseComboBox.IsEnabled = databases.Length > 1;

            if (!string.IsNullOrWhiteSpace(selectedDatabase) &&
                databases.Any(db => db.Equals(selectedDatabase, StringComparison.OrdinalIgnoreCase)))
            {
                _savedDatabaseName = databases.First(db => db.Equals(selectedDatabase, StringComparison.OrdinalIgnoreCase));
                SavedDatabaseComboBox.SelectedItem = _savedDatabaseName;
            }
            else if (databases.Length > 0)
            {
                _savedDatabaseName = databases[0];
                SavedDatabaseComboBox.SelectedIndex = 0;
            }
            else
            {
                _savedDatabaseName = string.Empty;
                SavedDatabaseComboBox.SelectedIndex = -1;
            }
        }
        finally
        {
            _isLoadingSavedContext = false;
        }

        DebugLog($"Database selector loaded. Count={databases.Length}, Selected={_savedDatabaseName}");
    }

    private static string ResolveDatabaseName(ConnectionInfo cachedConnection, string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                if (!string.IsNullOrWhiteSpace(builder.InitialCatalog))
                {
                    return builder.InitialCatalog;
                }
            }
            catch
            {
            }
        }

        try
        {
            var metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(cachedConnection.Id).Metadata;
            var databaseName = metadata.Databases.FirstOrDefault(db => !string.IsNullOrWhiteSpace(db))
                ?? metadata.Tables.Select(t => t.Database).FirstOrDefault(db => !string.IsNullOrWhiteSpace(db))
                ?? metadata.Views.Select(v => v.Database).FirstOrDefault(db => !string.IsNullOrWhiteSpace(db))
                ?? string.Empty;
            DebugLog($"ResolveDatabaseName metadata counts. Databases={metadata.Databases.Count}, Tables={metadata.Tables.Count}, Views={metadata.Views.Count}, Database={databaseName}");
            return databaseName;
        }
        catch (Exception ex)
        {
            DebugLog("ResolveDatabaseName failed", ex);
            return string.Empty;
        }
    }

    private void UpdateDebugConnectionContext()
    {
        if (!_isInitialized) return;
        ApplySavedLlmConfigToDebugOptions();

        MssqlIntelliSensePackage.DebugActiveConnectionString = null;
        MssqlIntelliSensePackage.DebugActiveDatabaseName = null;

        if (StatusBarText != null)
        {
            StatusBarText.Text = string.IsNullOrWhiteSpace(_savedConnectionString)
                ? "Saved SSMS cache is empty"
                : $"Database: {_savedDatabaseName}";
        }
    }

    private void ApplySavedLlmConfigToDebugOptions()
    {
        // DebugApp reads saved LLM settings directly from config.json. Avoid creating
        // the SSMS DialogPage-backed options object outside the SSMS shell.
    }

    private void UpdateSavedContextText()
    {
        if (SsmsContextText != null)
        {
            SsmsContextText.Text = string.IsNullOrWhiteSpace(_savedConnectionString)
                ? "No saved SSMS cache loaded"
                : $"Using saved SSMS cache: {_savedConnection?.Name ?? "connection"} / {_savedDatabaseName}";
        }

        if (StatusBarText != null)
        {
            StatusBarText.Text = string.IsNullOrWhiteSpace(_savedConnectionString)
                ? "Saved SSMS cache is empty"
                : $"{_savedConnection?.Name ?? "connection"} / {_savedDatabaseName}";
        }
    }

    private void ConnectionStringTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDebugConnectionContext();
    }

    private void DatabaseNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDebugConnectionContext();
    }

    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateDebugConnectionContext();
    }

#pragma warning disable VSTHRD100
    private async void SavedDatabaseComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
#pragma warning restore VSTHRD100
    {
        if (!_isInitialized || _isLoadingSavedContext || SavedDatabaseComboBox.SelectedItem is not string databaseName)
        {
            return;
        }

        _savedDatabaseName = databaseName;
        _currentMetadata = null;
        ApplySavedConnectionToEmbeddedControls();
        UpdateSavedContextText();
        DebugLog($"User selected database: {_savedDatabaseName}");
        await TriggerCompletionAsync();
    }

#pragma warning disable VSTHRD100
    private async void SavedConnectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
#pragma warning restore VSTHRD100
    {
        if (!_isInitialized || _isLoadingSavedContext || SavedConnectionComboBox.SelectedItem is not ConnectionInfo connection)
        {
            return;
        }

        _savedConnection = connection;
        _savedConnectionString = connection.ConnectionString;
        _savedDatabaseName = ResolveDatabaseName(connection, connection.ConnectionString);
        _currentMetadata = null;
        PopulateSavedDatabaseSelector(connection, _savedDatabaseName);
        ApplySavedConnectionToEmbeddedControls();
        UpdateSavedContextText();
        DebugLog($"User selected connection: Id={connection.Id}, Name={connection.Name}, Database={_savedDatabaseName}");
        await LoadCacheJsonAsync();
        await TriggerCompletionAsync();
    }

#pragma warning disable VSTHRD100
    private async void ReloadSsmsContextButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
    {
        LoadExistingSsmsContext();
        await LoadCacheJsonAsync();
        await TriggerCompletionAsync();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateDebugConnectionContext();
        MessageBox.Show("Debug settings saved successfully!", "MSSQL IntelliSense Debugger", MessageBoxButton.OK, MessageBoxImage.Information);
    }

#pragma warning disable VSTHRD100
    private async void RefreshMetadataButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
    {
        try
        {
            RefreshMetadataButton.IsEnabled = false;
            if (StatusBarText != null) StatusBarText.Text = "Scanning schema...";

            var connStr = _savedConnectionString;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                MessageBox.Show("Please enter a valid connection string.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await Task.Run(async () =>
            {
                var provider = new SqlServerMetadataProvider(connStr);
                var metadata = await provider.GetMetadataAsync();
                int connId = MssqlIntelliSenseCacheWriter.RegisterConnection(connStr, "DebugServer");
                MssqlIntelliSenseCacheWriter.SaveSchemaCache(connId, metadata);
                _currentMetadata = metadata;
            });

            if (StatusBarText != null) StatusBarText.Text = "Schema cache updated";
            await LoadCacheJsonAsync();
            await TriggerCompletionAsync();
        }
        catch (Exception ex)
        {
            if (StatusBarText != null) StatusBarText.Text = "Scan error: " + ex.Message;
            MessageBox.Show("Failed to scan schema: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshMetadataButton.IsEnabled = true;
        }
    }

    private void SqlInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = TriggerCompletionAsync();
    }

    private void SqlInputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _ = TriggerCompletionAsync();
    }

    private void RunCompletionButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TriggerCompletionAsync();
    }

    private readonly System.Windows.Threading.DispatcherTimer _gridLongClickTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private System.Windows.Point _gridMouseDownPoint;
    private bool _gridLongClickHandled;

    private void CompletionResultsDataGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _gridMouseDownPoint = e.GetPosition(CompletionResultsDataGrid);
        _gridLongClickHandled = false;
        _gridLongClickTimer.Stop();
        _gridLongClickTimer.Tick -= OnGridLongClickTimerTick;
        _gridLongClickTimer.Tick += OnGridLongClickTimerTick;
        _gridLongClickTimer.Start();
    }

    private void CompletionResultsDataGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_gridLongClickTimer.IsEnabled)
        {
            var currentPoint = e.GetPosition(CompletionResultsDataGrid);
            if (Math.Abs(currentPoint.X - _gridMouseDownPoint.X) > 8 || Math.Abs(currentPoint.Y - _gridMouseDownPoint.Y) > 8)
            {
                _gridLongClickTimer.Stop();
            }
        }
    }

    private void CompletionResultsDataGrid_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _gridLongClickTimer.Stop();
        if (_gridLongClickHandled)
        {
            _gridLongClickHandled = false;
            e.Handled = true;
        }
    }

    private void OnGridLongClickTimerTick(object? sender, EventArgs e)
    {
        _gridLongClickTimer.Stop();
        _gridLongClickHandled = true;
        OpenSelectedCompletionReview();
    }

    private void ReviewCompletionButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedCompletionReview();
    }

    private void CompletionResultsDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OpenSelectedCompletionReview();
    }

    private async Task TriggerCompletionAsync()
    {
        if (!_isInitialized || SqlInputTextBox == null)
        {
            DebugLog("TriggerCompletion skipped: window not initialized or SQL input missing.");
            return;
        }

        try
        {
            var sql = SqlInputTextBox.Text ?? string.Empty;
            var caretIndex = SqlInputTextBox.SelectionStart;
            var connStr = _savedConnectionString;
            var dbName = _savedDatabaseName;
            DebugLog($"TriggerCompletion. ConnPresent={!string.IsNullOrWhiteSpace(connStr)}, Database={dbName}, SqlLength={sql.Length}, Caret={caretIndex}");

            if (_currentMetadata == null && !string.IsNullOrWhiteSpace(connStr))
            {
                _currentMetadata = await Task.Run(() => MssqlIntelliSenseCacheReader.GetMetadataByConnectionStringAndDatabase(connStr, dbName));
                DebugLog($"Metadata loaded for completion. IsEmpty={ReferenceEquals(_currentMetadata, DatabaseMetadata.Empty)}, Tables={_currentMetadata.Tables.Count}, Views={_currentMetadata.Views.Count}");
            }

            if (_currentMetadata == null || ReferenceEquals(_currentMetadata, DatabaseMetadata.Empty))
            {
                if (StatusBarText != null) StatusBarText.Text = "Completion: no metadata";
                DebugLog("TriggerCompletion stopped: no metadata loaded.");
                return;
            }

            var items = _completionProvider.GetCompletions(sql, caretIndex, _currentMetadata);
            if (CompletionResultsDataGrid != null)
            {
                CompletionResultsDataGrid.ItemsSource = items;

                if (items.Count > 0 && CompletionResultsDataGrid.SelectedItem == null)
                {
                    CompletionResultsDataGrid.SelectedIndex = 0;
                }
            }

            if (StatusBarText != null) StatusBarText.Text = $"Completion: {items.Count} suggestion(s)";
        }
        catch (Exception ex)
        {
            DebugLog("Completion error", ex);
            if (StatusBarText != null) StatusBarText.Text = "Completion error: " + ex.Message;
        }
    }

    private void CompletionResultsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompletionDescriptionPreview == null) return;

        if (CompletionResultsDataGrid.SelectedItem is SqlCompletionItem item)
        {
            CompletionDescriptionPreview.Text = item.Description;
            if (ReviewCompletionButton != null)
            {
                ReviewCompletionButton.IsEnabled = _currentMetadata != null && CanReviewCompletion(item);
            }
        }
        else
        {
            CompletionDescriptionPreview.Text = string.Empty;
            if (ReviewCompletionButton != null)
            {
                ReviewCompletionButton.IsEnabled = false;
            }
        }
    }

    private void OpenSelectedCompletionReview()
    {
        if (CompletionResultsDataGrid?.SelectedItem is not SqlCompletionItem item)
        {
            if (StatusBarText != null) StatusBarText.Text = "Review: select a suggestion first";
            return;
        }

        if (_currentMetadata == null || ReferenceEquals(_currentMetadata, DatabaseMetadata.Empty))
        {
            if (StatusBarText != null) StatusBarText.Text = "Review: no metadata loaded";
            return;
        }

        if (!CanReviewCompletion(item))
        {
            if (StatusBarText != null) StatusBarText.Text = $"Review: {item.Kind} is not an object";
            return;
        }

        ObjectReviewWindow.ShowForCompletion(item, _currentMetadata);
        if (StatusBarText != null) StatusBarText.Text = $"Review opened: {item.Label}";
    }

    private static bool CanReviewCompletion(SqlCompletionItem item) =>
        item.Kind is SqlCompletionKind.Table or
            SqlCompletionKind.View or
            SqlCompletionKind.Procedure or
            SqlCompletionKind.Function or
            SqlCompletionKind.UserType or
            SqlCompletionKind.Synonym;

#pragma warning disable VSTHRD100
    private async void LoadCacheJsonButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
    {
        await LoadCacheJsonAsync();
    }

    private async Task LoadCacheJsonAsync()
    {
        if (!_isInitialized) return;

        try
        {
            var dbPath = MssqlIntelliSenseCacheReader.GetCacheFilePath();
            if (CachePathStatusText != null) CachePathStatusText.Text = $"Cache File Path: {dbPath}";

            if (File.Exists(dbPath))
            {
                var json = await Task.Run(() => File.ReadAllText(dbPath));
                if (CacheJsonViewerTextBox != null)
                {
                    if (json.Length > 200000)
                    {
                        CacheJsonViewerTextBox.Text = json.Substring(0, 200000) + $"\r\n\r\n... [Output truncated for UI performance. Total length: {json.Length} characters]";
                    }
                    else
                    {
                        CacheJsonViewerTextBox.Text = json;
                    }
                }
            }
            else
            {
                if (CacheJsonViewerTextBox != null) CacheJsonViewerTextBox.Text = "Cache file does not exist yet.";
            }
        }
        catch (Exception ex)
        {
            if (CacheJsonViewerTextBox != null) CacheJsonViewerTextBox.Text = "Failed to read cache file: " + ex.Message;
        }
    }

    private static void DebugLog(string message, Exception? exception = null)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            File.AppendAllText(DebugLogPath, line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
