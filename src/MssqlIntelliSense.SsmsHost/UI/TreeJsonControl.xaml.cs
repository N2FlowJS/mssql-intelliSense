using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace MssqlIntelliSense.SsmsHost;

public partial class TreeJsonControl : UserControl
{
    private const int InitialExpandedDepth = 1;
    private const int MaxChildrenPerNode = 250;
    private const int MaxValueLength = 160;
    private JsonDocument? _document;
    private string _rawText = string.Empty;

    public ObservableCollection<TreeJsonNode> RootNodes { get; } = new();

    public TreeJsonControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => _document?.Dispose();
    }

    public void SetJson(string text)
    {
        _rawText = text ?? string.Empty;
        RootNodes.Clear();
        RawTextBox.Text = _rawText;
        _document?.Dispose();
        _document = null;

        if (string.IsNullOrWhiteSpace(_rawText))
        {
            ShowRaw("No JSON to display.");
            return;
        }

        try
        {
            var jsonStart = FindJsonStart(_rawText);
            if (jsonStart < 0)
            {
                ShowRaw("Text output");
                return;
            }

            var prefix = _rawText.Substring(0, jsonStart).Trim();
            _document = JsonDocument.Parse(_rawText.Substring(jsonStart));

            if (!string.IsNullOrWhiteSpace(prefix))
            {
                RootNodes.Add(TreeJsonNode.Leaf(
                    "context",
                    QuoteAndTrim(prefix),
                    CreateValueBrush(JsonValueKind.String)));
            }

            var root = CreateNode("root", _document.RootElement, depth: 0);
            RootNodes.Add(root);
            ExpandInitialNodes(root, InitialExpandedDepth);

            JsonTreeView.Visibility = Visibility.Visible;
            RawTextBox.Visibility = Visibility.Collapsed;
            StatusTextBlock.Text = "JSON";
        }
        catch (JsonException ex)
        {
            ShowRaw("Invalid JSON: " + ex.Message);
        }
        catch (Exception ex)
        {
            ShowRaw("Unable to render JSON tree: " + ex.Message);
        }
    }

    private static int FindJsonStart(string text)
    {
        var trimmedStart = text.TakeWhile(char.IsWhiteSpace).Count();
        if (trimmedStart < text.Length && (text[trimmedStart] == '{' || text[trimmedStart] == '['))
        {
            return trimmedStart;
        }

        var attempts = 0;
        for (var i = 0; i < text.Length && attempts < 64; i++)
        {
            if (text[i] != '{' && text[i] != '[')
            {
                continue;
            }

            attempts++;
            try
            {
                using var _ = JsonDocument.Parse(text.Substring(i));
                return i;
            }
            catch (JsonException)
            {
            }
        }

        return -1;
    }

    private TreeJsonNode CreateNode(string name, JsonElement element, int depth)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => TreeJsonNode.Branch(
                name,
                GetObjectSummary(element),
                CreateValueBrush(element.ValueKind),
                () => CreateChildren(element, depth + 1)),
            JsonValueKind.Array => TreeJsonNode.Branch(
                name,
                GetArraySummary(element),
                CreateValueBrush(element.ValueKind),
                () => CreateChildren(element, depth + 1)),
            JsonValueKind.String => CreateStringNode(name, element),
            _ => TreeJsonNode.Leaf(
                name,
                TrimValue(element.GetRawText()),
                CreateValueBrush(element.ValueKind))
        };
    }

    private ObservableCollection<TreeJsonNode> CreateChildren(JsonElement element, int depth)
    {
        var children = new ObservableCollection<TreeJsonNode>();
        var added = 0;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (added >= MaxChildrenPerNode)
                {
                    children.Add(TreeJsonNode.Leaf("...", $"truncated after {MaxChildrenPerNode:n0} items", CreateValueBrush(JsonValueKind.String)));
                    break;
                }

                children.Add(CreateNode(property.Name, property.Value, depth));
                added++;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (added >= MaxChildrenPerNode)
                {
                    children.Add(TreeJsonNode.Leaf("...", $"truncated after {MaxChildrenPerNode:n0} items", CreateValueBrush(JsonValueKind.String)));
                    break;
                }

                children.Add(CreateNode("[" + index + "]", item, depth));
                index++;
                added++;
            }
        }

        return children;
    }

    private TreeJsonNode CreateStringNode(string name, JsonElement element)
    {
        var value = element.GetString() ?? string.Empty;
        if (TryParseStringifiedJson(value, out var parsed))
        {
            var node = TreeJsonNode.Branch(
                name,
                parsed.RootElement.ValueKind == JsonValueKind.Object ? GetObjectSummary(parsed.RootElement) : GetArraySummary(parsed.RootElement),
                CreateValueBrush(parsed.RootElement.ValueKind),
                () => CreateStringifiedJsonChildren(parsed));
            node.Detail = "stringified JSON";
            return node;
        }

        return TreeJsonNode.Leaf(name, QuoteAndTrim(value), CreateValueBrush(element.ValueKind));
    }

    private ObservableCollection<TreeJsonNode> CreateStringifiedJsonChildren(JsonDocument document)
    {
        return CreateChildren(document.RootElement, depth: 1);
    }

    private static bool TryParseStringifiedJson(string value, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
              (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(trimmed);
            var kind = document.RootElement.ValueKind;
            if (kind == JsonValueKind.Object || kind == JsonValueKind.Array)
            {
                return true;
            }

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetObjectSummary(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
        {
            count++;
        }

        return count == 1 ? "{ 1 property }" : $"{{ {count:n0} properties }}";
    }

    private static string GetArraySummary(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateArray())
        {
            count++;
        }

        return count == 1 ? "[ 1 item ]" : $"[ {count:n0} items ]";
    }

    private static void ExpandInitialNodes(TreeJsonNode node, int remainingDepth)
    {
        node.IsExpanded = true;
        if (remainingDepth <= 0)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            if (child.HasChildren)
            {
                ExpandInitialNodes(child, remainingDepth - 1);
            }
        }
    }

    private static string QuoteAndTrim(string value)
    {
        return "\"" + TrimValue(value).Replace("\"", "\\\"") + "\"";
    }

    private static string TrimValue(string value)
    {
        if (value.Length <= MaxValueLength)
        {
            return value;
        }

        return value.Substring(0, MaxValueLength) + "...";
    }

    private Brush CreateValueBrush(JsonValueKind kind)
    {
        return kind switch
        {
            JsonValueKind.String => GetBrush(EnvironmentColors.ToolWindowTextBrushKey, Color.FromRgb(214, 157, 133)),
            JsonValueKind.Number => GetBrush(EnvironmentColors.SystemHighlightBrushKey, Color.FromRgb(181, 206, 168)),
            JsonValueKind.True or JsonValueKind.False => GetBrush(EnvironmentColors.SystemHighlightBrushKey, Color.FromRgb(86, 156, 214)),
            JsonValueKind.Null or JsonValueKind.Undefined => GetBrush(EnvironmentColors.PanelTextBrushKey, Color.FromRgb(160, 160, 160)),
            _ => GetBrush(EnvironmentColors.PanelTextBrushKey, Color.FromRgb(190, 190, 190))
        };
    }

    private static Brush GetBrush(object key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    private void ShowRaw(string status)
    {
        StatusTextBlock.Text = status;
        JsonTreeView.Visibility = Visibility.Collapsed;
        RawTextBox.Visibility = Visibility.Visible;
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var node in RootNodes)
        {
            ExpandOneLevel(node);
        }
    }

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var node in RootNodes)
        {
            CollapseLoaded(node);
        }
    }

    private static void ExpandOneLevel(TreeJsonNode node)
    {
        node.IsExpanded = true;
        foreach (var child in node.Children)
        {
            if (child.HasChildren)
            {
                child.IsExpanded = true;
            }
        }
    }

    private static void CollapseLoaded(TreeJsonNode node)
    {
        foreach (var child in node.Children)
        {
            CollapseLoaded(child);
        }

        node.IsExpanded = false;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(_rawText, copy: true);
                return;
            }
            catch (Exception ex) when (attempt < 5)
            {
                MssqlIntelliSensePackage.Log($"[Tree JSON Copy Retry] Attempt {attempt} failed: {ex.Message}");
                Thread.Sleep(80);
            }
            catch (Exception ex)
            {
                MssqlIntelliSensePackage.Log($"[Tree JSON Copy Error] {ex.Message}");
            }
        }
    }
}

public sealed class TreeJsonNode : INotifyPropertyChanged
{
    private static readonly TreeJsonNode LoadingNode = new() { Name = "...", Separator = string.Empty, DisplayValue = "expand to load" };
    private readonly Func<ObservableCollection<TreeJsonNode>>? _childrenFactory;
    private bool _childrenLoaded;
    private bool _isExpanded;

    private TreeJsonNode()
    {
    }

    private TreeJsonNode(string name, string value, Brush brush, bool hasChildren, Func<ObservableCollection<TreeJsonNode>>? childrenFactory)
    {
        Name = name;
        DisplayValue = value;
        ValueBrush = brush;
        NameWeight = hasChildren ? FontWeights.SemiBold : FontWeights.Normal;
        HasChildren = hasChildren;
        _childrenFactory = childrenFactory;

        if (hasChildren)
        {
            Children.Add(LoadingNode);
        }
    }

    public string Name { get; private set; } = string.Empty;
    public string Separator { get; private set; } = ":";
    public string DisplayValue { get; private set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public FontWeight NameWeight { get; private set; } = FontWeights.Normal;
    public Brush ValueBrush { get; private set; } = Brushes.White;
    public bool HasChildren { get; }
    public ObservableCollection<TreeJsonNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            if (value)
            {
                EnsureChildrenLoaded();
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static TreeJsonNode Branch(string name, string value, Brush brush, Func<ObservableCollection<TreeJsonNode>> childrenFactory)
    {
        return new TreeJsonNode(name, value, brush, hasChildren: true, childrenFactory);
    }

    public static TreeJsonNode Leaf(string name, string value, Brush brush)
    {
        return new TreeJsonNode(name, value, brush, hasChildren: false, childrenFactory: null);
    }

    private void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || _childrenFactory == null)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();
        foreach (var child in _childrenFactory())
        {
            Children.Add(child);
        }
    }
}
