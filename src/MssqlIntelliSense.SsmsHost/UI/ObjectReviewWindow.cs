using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

public sealed class ObjectReviewWindow : Window
{
    private readonly string _copyName;
    private readonly string _copyDefinition;
    private readonly string _copyAll;
    private readonly string _objectKey;
    private readonly IReadOnlyList<ColumnMetadata> _columns;
    private TextBox? _customDescriptionTextBox;
    private readonly List<ColumnGuidanceRow> _columnGuidanceRows = new();

    private sealed class ColumnGuidanceRow
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Nullable { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    private ObjectReviewWindow(
        string title,
        string subtitle,
        string details,
        string definition,
        string objectKey,
        string customDescription,
        IReadOnlyList<ColumnMetadata> columns)
    {
        _copyName = subtitle;
        _copyDefinition = definition;
        _copyAll = details + Environment.NewLine + Environment.NewLine + definition;
        _objectKey = objectKey;
        _columns = columns;

        Title = "MSSQL IntelliSense Object Review";
        Width = 720;
        Height = 760;
        MinWidth = 360;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = GetBrush(EnvironmentColors.ToolWindowBackgroundBrushKey, Color.FromRgb(31, 31, 31));
        Foreground = GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Colors.White);
        Content = BuildContent(title, subtitle, details, definition, customDescription);
    }

    public static void ShowForCompletion(SqlCompletionItem item, DatabaseMetadata metadata)
    {
        if (!SqlObjectReviewFormatter.CanReview(item.Kind))
        {
            return;
        }

        try
        {
            var (title, subtitle, details, definition, objectKey, customDescription) = BuildReviewText(item, metadata);
            var columns = GetColumns(item, metadata);
            var window = new ObjectReviewWindow(title, subtitle, details, definition, objectKey, customDescription, columns);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Object Review Error] {ex}");
        }
    }

    private UIElement BuildContent(string title, string subtitle, string details, string definition, string customDescription)
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(CreateButton("Copy", "Copy object summary and definition", (_, _) => CopyText(_copyAll)));
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var titleStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        titleStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Color.FromRgb(190, 190, 190)),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });
        header.Children.Add(titleStack);
        root.Children.Add(header);

        var tabs = new TabControl();
        tabs.Items.Add(BuildOverviewTab(details, customDescription));
        if (_columns.Count > 0)
        {
            tabs.Items.Add(BuildColumnsTab());
        }
        tabs.Items.Add(BuildDefinitionTab(definition));
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        return root;
    }

    private TabItem BuildOverviewTab(string details, string customDescription)
    {
        var content = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var infoBox = CreateTextBox(details, acceptsReturn: true);
        infoBox.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(infoBox);

        var customPanel = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        customPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customPanel.Children.Add(new TextBlock
        {
            Text = "Object guidance",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var saveDescriptionButton = CreateButton("Save", "Save object and column guidance", (_, _) => SaveCustomDescription());
        Grid.SetColumn(saveDescriptionButton, 1);
        customPanel.Children.Add(saveDescriptionButton);

        _customDescriptionTextBox = CreateTextBox(customDescription, acceptsReturn: true);
        _customDescriptionTextBox.MinHeight = 60;
        _customDescriptionTextBox.TextWrapping = TextWrapping.Wrap;
        _customDescriptionTextBox.IsReadOnly = false;
        _customDescriptionTextBox.ToolTip = "Describe the business purpose, data owner, common use cases, and important constraints for this object.";
        Grid.SetRow(_customDescriptionTextBox, 1);
        Grid.SetColumnSpan(_customDescriptionTextBox, 2);
        customPanel.Children.Add(_customDescriptionTextBox);
        Grid.SetRow(customPanel, 1);
        content.Children.Add(customPanel);

        return new TabItem { Header = "Overview", Content = content };
    }

    private TabItem BuildColumnsTab()
    {
        var content = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        content.Children.Add(new TextBlock
        {
            Text = "Describe the business meaning, valid values, data sensitivity, relationships, and examples that help the agent use each column correctly.",
            Foreground = GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Color.FromRgb(190, 190, 190)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var descriptions = ObjectDescriptionStore.LoadAll();
        foreach (var column in _columns.OrderBy(column => column.Ordinal))
        {
            var key = ObjectDescriptionStore.BuildColumnKey(_objectKey, column.Name);
            descriptions.TryGetValue(key, out var description);
            _columnGuidanceRows.Add(new ColumnGuidanceRow
            {
                Key = key,
                Name = column.Name,
                DataType = column.DataType,
                Nullable = column.IsNullable ? "Yes" : "No",
                Description = description ?? string.Empty
            });
        }

        var grid = new DataGrid
        {
            ItemsSource = _columnGuidanceRows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            IsReadOnly = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            Margin = new Thickness(0)
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Column",
            Binding = new Binding("Name"),
            IsReadOnly = true,
            Width = new DataGridLength(150)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Type",
            Binding = new Binding("DataType"),
            IsReadOnly = true,
            Width = new DataGridLength(120)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Nullable",
            Binding = new Binding("Nullable"),
            IsReadOnly = true,
            Width = new DataGridLength(70)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Business guidance / rules",
            Binding = new Binding("Description") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        Grid.SetRow(grid, 1);
        content.Children.Add(grid);

        return new TabItem { Header = "Columns", Content = content };
    }

    private static (string Title, string Subtitle, string Details, string Definition, string ObjectKey, string CustomDescription) BuildReviewText(
        SqlCompletionItem item,
        DatabaseMetadata metadata)
    {
        var review = SqlObjectReviewFormatter.Build(item, metadata);
        return (review.Title, review.Subtitle, review.Details, review.Definition, review.ObjectKey, review.CustomDescription);
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

    private TabItem BuildDefinitionTab(string definition)
    {
        var definitionBox = CreateTextBox(definition, acceptsReturn: true);
        definitionBox.Margin = new Thickness(0, 10, 0, 0);
        definitionBox.FontFamily = new FontFamily("Consolas");
        definitionBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        definitionBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        return new TabItem { Header = "Definition", Content = definitionBox };
    }

    private Button CreateButton(string text, string tooltip, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 54,
            MinHeight = 30,
            Background = GetBrush(EnvironmentColors.SystemButtonFaceBrushKey, Color.FromRgb(45, 45, 48)),
            Foreground = GetBrush(EnvironmentColors.SystemButtonTextBrushKey, Colors.White),
            BorderBrush = GetBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(63, 63, 70)),
            ToolTip = tooltip
        };
        button.Click += handler;
        return button;
    }

    private TextBox CreateTextBox(string text, bool acceptsReturn)
    {
        return new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = acceptsReturn,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            Background = GetBrush(EnvironmentColors.ToolWindowCodeBlockBackgroundBrushKey, Color.FromRgb(37, 37, 38)),
            Foreground = GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Colors.White),
            BorderBrush = GetBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(63, 63, 70)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
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

    private void SaveCustomDescription()
    {
        try
        {
            ObjectDescriptionStore.SaveDescription(_objectKey, _customDescriptionTextBox?.Text ?? string.Empty);
            foreach (var row in _columnGuidanceRows)
            {
                ObjectDescriptionStore.SaveDescription(row.Key, row.Description ?? string.Empty);
            }
            Title = "MSSQL IntelliSense Object Review - saved";
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Object Review Save Description Error] {ex}");
            Title = "MSSQL IntelliSense Object Review - save failed";
        }
    }

    private static Brush GetBrush(object key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
