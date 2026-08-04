using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

public partial class ObjectReviewWindow : UserControl
{
    private static WeakReference<ObjectReviewWindow>? _activePanel;
    private readonly List<ColumnGuidanceRow> _columnGuidanceRows = new();
    private string _copyAll = string.Empty;
    private string _objectKey = string.Empty;

    private sealed class ColumnGuidanceRow
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Nullable { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public ObjectReviewWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _activePanel = new WeakReference<ObjectReviewWindow>(this);
            FocusDescriptionEditor();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _activePanel = new WeakReference<ObjectReviewWindow>(this);
            }
        };
    }

    public static void ShowForCompletion(SqlCompletionItem item, DatabaseMetadata metadata, Window? owner = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!SqlObjectReviewFormatter.CanReview(item.Kind))
        {
            return;
        }

        try
        {
            if (MssqlIntelliSensePackage.Instance != null)
            {
                MssqlIntelliSensePackage.Instance.JoinableTaskFactory.RunAsync(async () =>
                {
                    await MssqlIntelliSensePackage.Instance.ShowObjectReviewPanelAsync(item, metadata, CancellationToken.None);
                }).FileAndForget("MssqlIntelliSense/ObjectReviewPanel");
                return;
            }

            ObjectReviewStandaloneWindow.ShowForCompletion(item, metadata, owner);
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Object Review Error] {ex}");
        }
    }

    public void SetReviewContent(SqlCompletionItem item, DatabaseMetadata metadata)
    {
        var review = SqlObjectReviewFormatter.Build(item, metadata);
        var columns = GetColumns(item, metadata);

        TitleTextBlock.Text = review.Title;
        SubtitleTextBlock.Text = review.Subtitle;
        DetailsTextBox.Text = review.Details;
        DefinitionTextBox.Text = review.Definition;
        CustomDescriptionTextBox.Text = review.Description;
        _copyAll = review.Details + Environment.NewLine + Environment.NewLine + review.Definition;
        _objectKey = review.ObjectKey;

        _columnGuidanceRows.Clear();
        foreach (var column in columns.OrderBy(column => column.Ordinal))
        {
            var key = ObjectDescriptionStore.BuildColumnKey(_objectKey, column.Name);
            _columnGuidanceRows.Add(new ColumnGuidanceRow
            {
                Key = key,
                Name = column.Name,
                DataType = column.DataType,
                Nullable = column.IsNullable ? "Yes" : "No",
                Description = column.Description
            });
        }

        ColumnsTab.Visibility = _columnGuidanceRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ColumnsDataGrid.ItemsSource = null;
        ColumnsDataGrid.ItemsSource = _columnGuidanceRows;
        ReviewTabs.SelectedIndex = 0;
        _activePanel = new WeakReference<ObjectReviewWindow>(this);
        FocusDescriptionEditor();
    }

    public static bool TryRedirectEditorCommandToActiveWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_activePanel == null || !_activePanel.TryGetTarget(out var panel) || !panel.IsVisible)
        {
            return false;
        }

        panel.FocusDescriptionEditor();
        return true;
    }

    public void FocusDescriptionEditor()
    {
        if (!IsVisible)
        {
            return;
        }

        Focus();
        CustomDescriptionTextBox.Focus();
        Keyboard.Focus(CustomDescriptionTextBox);
        CustomDescriptionTextBox.CaretIndex = CustomDescriptionTextBox.Text?.Length ?? 0;
    }

    private void ObjectReviewWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveCustomDescription();
            return;
        }

        if (e.Key == Key.F5 || ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.E))
        {
            e.Handled = true;
        }
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        CopyText(_copyAll);
    }

    private void SaveDescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCustomDescription();
    }

    private void SaveCustomDescription()
    {
        try
        {
            var savedToMetadata = TryParseObjectKey(_objectKey, out var kind, out var database, out var schema, out var name) &&
                MetadataDescriptionEditor.TryUpdateObjectDescription(kind, database, schema, name, CustomDescriptionTextBox.Text ?? string.Empty);
            if (!savedToMetadata)
            {
                throw new InvalidOperationException("The reviewed object was not found in the schema cache.");
            }

            foreach (var row in _columnGuidanceRows)
            {
                var savedColumnToMetadata = TryParseColumnKey(row.Key, out kind, out database, out schema, out name, out var columnName) &&
                    MetadataDescriptionEditor.TryUpdateColumnDescription(kind, database, schema, name, columnName, row.Description ?? string.Empty);
                if (!savedColumnToMetadata)
                {
                    throw new InvalidOperationException($"Column '{columnName}' was not found in the schema cache.");
                }
            }

            MssqlIntelliSensePackage.Log("[Object Review] Description saved.");
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Object Review Save Description Error] {ex}");
        }
    }

    private static IReadOnlyList<ColumnMetadata> GetColumns(SqlCompletionItem item, DatabaseMetadata metadata)
    {
        var nameParts = item.Label.Replace("[", string.Empty).Replace("]", string.Empty).Split('.');
        var name = nameParts[nameParts.Length - 1];
        var schema = nameParts.Length > 1 ? nameParts[nameParts.Length - 2] : null;

        if (item.Kind == SqlCompletionKind.Table)
        {
            return metadata.Tables.FirstOrDefault(table =>
                table.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(schema) || table.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)))?.Columns
                ?? Array.Empty<ColumnMetadata>();
        }

        if (item.Kind == SqlCompletionKind.View)
        {
            return metadata.Views.FirstOrDefault(view =>
                view.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(schema) || view.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)))?.Columns
                ?? Array.Empty<ColumnMetadata>();
        }

        return Array.Empty<ColumnMetadata>();
    }

    private static void CopyText(string text)
    {
        var value = text ?? string.Empty;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(value, copy: true);
                return;
            }
            catch (Exception ex) when (attempt < 5)
            {
                MssqlIntelliSensePackage.Log($"[Object Review Copy Retry] Attempt {attempt} failed: {ex.Message}");
                Thread.Sleep(80);
            }
            catch (Exception ex)
            {
                MssqlIntelliSensePackage.Log($"[Object Review Copy Error] {ex.Message}");
            }
        }
    }

    private static bool TryParseObjectKey(string key, out string kind, out string database, out string schema, out string name)
    {
        var parts = key.Split('|');
        if (parts.Length == 4)
        {
            kind = parts[0];
            database = parts[1];
            schema = parts[2];
            name = parts[3];
            return true;
        }

        kind = database = schema = name = string.Empty;
        return false;
    }

    private static bool TryParseColumnKey(string key, out string kind, out string database, out string schema, out string name, out string columnName)
    {
        var parts = key.Split('|');
        if (parts.Length == 6 && parts[4].Equals("column", StringComparison.OrdinalIgnoreCase))
        {
            kind = parts[0];
            database = parts[1];
            schema = parts[2];
            name = parts[3];
            columnName = parts[5];
            return true;
        }

        kind = database = schema = name = columnName = string.Empty;
        return false;
    }
}
