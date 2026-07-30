using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
    private TextBox? _customDescriptionTextBox;

    private ObjectReviewWindow(string title, string subtitle, string details, string definition, string objectKey, string customDescription)
    {
        _copyName = subtitle;
        _copyDefinition = definition;
        _copyAll = details + Environment.NewLine + Environment.NewLine + definition;
        _objectKey = objectKey;

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
            var window = new ObjectReviewWindow(title, subtitle, details, definition, objectKey, customDescription);
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
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(CreateButton("Copy name", (_, _) => CopyText(_copyName)));
        actions.Children.Add(CreateButton("Copy definition", (_, _) => CopyText(_copyDefinition)));
        actions.Children.Add(CreateButton("Copy all", (_, _) => CopyText(_copyAll)));
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

        var infoBox = CreateTextBox(details, acceptsReturn: true);
        infoBox.MinHeight = 120;
        infoBox.MaxHeight = 220;
        Grid.SetRow(infoBox, 1);
        root.Children.Add(infoBox);

        var customPanel = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        customPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customPanel.Children.Add(new TextBlock
        {
            Text = "Agent description",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var saveDescriptionButton = CreateButton("Save", (_, _) => SaveCustomDescription());
        Grid.SetColumn(saveDescriptionButton, 1);
        customPanel.Children.Add(saveDescriptionButton);

        _customDescriptionTextBox = CreateTextBox(customDescription, acceptsReturn: true);
        _customDescriptionTextBox.MinHeight = 70;
        _customDescriptionTextBox.TextWrapping = TextWrapping.Wrap;
        _customDescriptionTextBox.IsReadOnly = false;
        Grid.SetRow(_customDescriptionTextBox, 1);
        Grid.SetColumnSpan(_customDescriptionTextBox, 2);
        customPanel.Children.Add(_customDescriptionTextBox);
        Grid.SetRow(customPanel, 2);
        root.Children.Add(customPanel);

        var definitionBox = CreateTextBox(definition, acceptsReturn: true);
        definitionBox.Margin = new Thickness(0, 12, 0, 0);
        definitionBox.FontFamily = new FontFamily("Consolas");
        definitionBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        definitionBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetRow(definitionBox, 3);
        root.Children.Add(definitionBox);

        return root;
    }

    private static (string Title, string Subtitle, string Details, string Definition, string ObjectKey, string CustomDescription) BuildReviewText(
        SqlCompletionItem item,
        DatabaseMetadata metadata)
    {
        var review = SqlObjectReviewFormatter.Build(item, metadata);
        return (review.Title, review.Subtitle, review.Details, review.Definition, review.ObjectKey, review.CustomDescription);
    }

    private Button CreateButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 82,
            Background = GetBrush(EnvironmentColors.SystemButtonFaceBrushKey, Color.FromRgb(45, 45, 48)),
            Foreground = GetBrush(EnvironmentColors.SystemButtonTextBrushKey, Colors.White),
            BorderBrush = GetBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(63, 63, 70))
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
