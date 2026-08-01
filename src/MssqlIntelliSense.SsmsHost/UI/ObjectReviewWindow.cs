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
        Width = 820;
        Height = 700;
        MinWidth = 560;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = GetBrush(EnvironmentColors.ToolWindowBackgroundBrushKey, Color.FromRgb(31, 31, 31));
        Foreground = GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Colors.White);
        Content = BuildContent(title, subtitle, details, definition, customDescription);
    }

    public static void ShowForCompletion(SqlCompletionItem item, DatabaseMetadata metadata, Window? owner = null)
    {
        if (!SqlObjectReviewFormatter.CanReview(item.Kind))
        {
            return;
        }

        try
        {
            MetadataDescriptionEditor.EnsureLegacyDescriptionsMigrated();
            var (title, subtitle, details, definition, objectKey, customDescription) = BuildReviewText(item, metadata);
            var columns = GetColumns(item, metadata);
            var window = new ObjectReviewWindow(title, subtitle, details, definition, objectKey, customDescription, columns);
            if (owner != null)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
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
        var panelBrush = GetBrush(EnvironmentColors.ToolWindowBackgroundBrushKey, Color.FromRgb(31, 31, 31));
        var panelAltBrush = GetBrush(EnvironmentColors.ToolWindowCodeBlockBackgroundBrushKey, Color.FromRgb(37, 37, 38));
        var textBrush = GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Colors.White);
        var borderBrush = GetBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(63, 63, 70));
        var accentBrush = GetBrush(EnvironmentColors.SystemHighlightBrushKey, Color.FromRgb(45, 125, 154));
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(CreateIconButton("\uE8C8", "Copy object summary and definition", (_, _) => CopyText(_copyAll)));
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

        var tabs = new TabControl
        {
            Background = panelBrush,
            Foreground = textBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            ItemContainerStyle = CreateTabItemStyle(panelBrush, panelAltBrush, textBrush, borderBrush, accentBrush)
        };
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
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

        var infoBox = CreateTextBox(details, acceptsReturn: true);
        infoBox.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(infoBox);

        var customPanel = new Grid
        {
            Margin = new Thickness(0, 10, 0, 0),
            Background = GetBrush(EnvironmentColors.ToolWindowBackgroundBrushKey, Color.FromRgb(31, 31, 31))
        };
        customPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        customPanel.Children.Add(new TextBlock
        {
            Text = "Description",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Colors.White)
        });
        var saveDescriptionButton = CreateIconButton("\uE74E", "Save description", (_, _) => SaveCustomDescription());
        Grid.SetColumn(saveDescriptionButton, 1);
        customPanel.Children.Add(saveDescriptionButton);

        _customDescriptionTextBox = CreateTextBox(customDescription, acceptsReturn: true);
        _customDescriptionTextBox.MinHeight = 80;
        _customDescriptionTextBox.TextWrapping = TextWrapping.Wrap;
        _customDescriptionTextBox.IsReadOnly = false;
        _customDescriptionTextBox.ToolTip = "Edit the cached object description. This is the field that can later be synchronized with SQL Server MS_Description.";
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

        foreach (var column in _columns.OrderBy(column => column.Ordinal))
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
        return (review.Title, review.Subtitle, review.Details, review.Definition, review.ObjectKey, review.Description);
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

    private static Style CreateTabItemStyle(Brush panelBrush, Brush panelAltBrush, Brush textBrush, Brush borderBrush, Brush accentBrush)
    {
        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, panelAltBrush));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateTabItemTemplate(panelBrush, panelAltBrush, textBrush, borderBrush, accentBrush)));
        return style;
    }

    private static ControlTemplate CreateTabItemTemplate(Brush panelBrush, Brush panelAltBrush, Brush textBrush, Brush borderBrush, Brush accentBrush)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "TabBorder";
        border.SetValue(Border.BackgroundProperty, panelAltBrush);
        border.SetValue(Border.BorderBrushProperty, borderBrush);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 5, 10, 5));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(TabItem)) { VisualTree = border };
        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, panelBrush, "TabBorder"));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, accentBrush, "TabBorder"));
        template.Triggers.Add(selected);
        return template;
    }

    private Button CreateIconButton(string glyph, string tooltip, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            Width = 30,
            Height = 30,
            MinWidth = 30,
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
            var savedToMetadata = TryParseObjectKey(_objectKey, out var kind, out var database, out var schema, out var name) &&
                MetadataDescriptionEditor.TryUpdateObjectDescription(kind, database, schema, name, _customDescriptionTextBox?.Text ?? string.Empty);
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
            Title = "MSSQL IntelliSense Object Review - description saved";
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Object Review Save Description Error] {ex}");
            Title = "MSSQL IntelliSense Object Review - save failed";
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

    private static Brush GetBrush(object key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
