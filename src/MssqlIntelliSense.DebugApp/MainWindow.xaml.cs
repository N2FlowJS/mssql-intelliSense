using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MssqlIntelliSense.Core.Cache;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;
using MssqlIntelliSense.SsmsHost;

namespace MssqlIntelliSense.DebugApp;

public partial class MainWindow : Window
{
    private readonly SqlCompletionProvider _completionProvider = new();
    private DatabaseMetadata? _currentMetadata;
    private bool _isInitialized;

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
            var options = MssqlIntelliSensePackage.GetOptions();
            if (options != null && !string.IsNullOrWhiteSpace(options.ApiKey))
            {
                ApiKeyPasswordBox.Password = options.ApiKey;
            }

            // Default local debug connection string
            ConnectionStringTextBox.Text = MssqlIntelliSensePackage.DebugActiveConnectionString ?? "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
            DatabaseNameTextBox.Text = MssqlIntelliSensePackage.DebugActiveDatabaseName ?? "master";

            UpdateDebugConnectionContext();
            _ = LoadCacheJsonAsync();
            _ = TriggerCompletionAsync();
        }
        catch (Exception ex)
        {
            if (StatusBarText != null) StatusBarText.Content = "Initialization error: " + ex.Message;
        }
    }

    private void UpdateDebugConnectionContext()
    {
        if (!_isInitialized || ConnectionStringTextBox == null || DatabaseNameTextBox == null || ApiKeyPasswordBox == null) return;

        var connStr = ConnectionStringTextBox.Text?.Trim() ?? string.Empty;
        var dbName = DatabaseNameTextBox.Text?.Trim() ?? string.Empty;

        MssqlIntelliSensePackage.DebugActiveConnectionString = connStr;
        MssqlIntelliSensePackage.DebugActiveDatabaseName = dbName;

        var options = MssqlIntelliSensePackage.GetOptions();
        if (options != null)
        {
            options.ApiKey = ApiKeyPasswordBox.Password;
        }

        if (StatusBarText != null)
        {
            StatusBarText.Content = $"Debug connection set: Database={dbName}";
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

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateDebugConnectionContext();
        MessageBox.Show("Debug settings saved successfully!", "MSSQL IntelliSense Debugger", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RefreshMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshMetadataButton.IsEnabled = false;
            if (StatusBarText != null) StatusBarText.Content = "Scanning schema and updating database cache...";

            var connStr = ConnectionStringTextBox.Text.Trim();
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

            if (StatusBarText != null) StatusBarText.Content = "Schema scan completed! Cache updated.";
            await LoadCacheJsonAsync();
            await TriggerCompletionAsync();
        }
        catch (Exception ex)
        {
            if (StatusBarText != null) StatusBarText.Content = "Schema scan error: " + ex.Message;
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

    private async Task TriggerCompletionAsync()
    {
        if (!_isInitialized || SqlInputTextBox == null || ConnectionStringTextBox == null) return;

        try
        {
            var sql = SqlInputTextBox.Text ?? string.Empty;
            var caretIndex = SqlInputTextBox.SelectionStart;
            var connStr = ConnectionStringTextBox.Text?.Trim() ?? string.Empty;

            if (_currentMetadata == null && !string.IsNullOrWhiteSpace(connStr))
            {
                _currentMetadata = await Task.Run(() => MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(connStr));
            }

            if (_currentMetadata == null)
            {
                if (StatusBarText != null) StatusBarText.Content = "Completion: No metadata loaded. Scan schema first.";
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

            if (StatusBarText != null) StatusBarText.Content = $"Completion triggered at pos {caretIndex}: {items.Count} suggestion(s) returned.";
        }
        catch (Exception ex)
        {
            if (StatusBarText != null) StatusBarText.Content = "Completion error: " + ex.Message;
        }
    }

    private void CompletionResultsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompletionDescriptionPreview == null) return;

        if (CompletionResultsDataGrid.SelectedItem is SqlCompletionItem item)
        {
            CompletionDescriptionPreview.Text = item.Description;
        }
        else
        {
            CompletionDescriptionPreview.Text = string.Empty;
        }
    }

    private async void LoadCacheJsonButton_Click(object sender, RoutedEventArgs e)
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
}
