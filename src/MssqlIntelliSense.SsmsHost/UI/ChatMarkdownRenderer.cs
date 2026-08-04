using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace MssqlIntelliSense.SsmsHost;

internal static class ChatMarkdownRenderer
{
    public static RichTextBox CreateSelectableMessageBox(Brush foreground, Brush background)
    {
        return new RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = foreground,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsDocumentEnabled = false,
            Focusable = true,
            Cursor = Cursors.IBeam,
            SelectionBrush = new SolidColorBrush(Color.FromArgb(120, 51, 153, 255))
        };
    }

    public static FlowDocument CreatePlainTextDocument(string text, Brush foreground)
    {
        var document = CreateBaseFlowDocument(foreground);
        document.Blocks.Add(new Paragraph(new Run(text ?? string.Empty))
        {
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Margin = new Thickness(0)
        });
        return document;
    }

    public static FlowDocument CreateMarkdownDocument(string markdownText, Brush foreground, Brush codeBackground)
    {
        var document = CreateBaseFlowDocument(foreground);
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return document;
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var paragraphBuffer = new List<string>();
        var codeBuffer = new StringBuilder();
        var inCodeBlock = false;
        var codeLanguage = string.Empty;

        void FlushParagraph()
        {
            if (paragraphBuffer.Count == 0)
            {
                return;
            }

            var paragraph = CreateParagraph(foreground, marginBottom: 5);
            for (var i = 0; i < paragraphBuffer.Count; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                AddInlineMarkdown(paragraph.Inlines, paragraphBuffer[i], foreground, codeBackground);
            }

            document.Blocks.Add(paragraph);
            paragraphBuffer.Clear();
        }

        void FlushCodeBlock()
        {
            var code = codeBuffer.ToString().TrimEnd('\n');
            if (!string.IsNullOrWhiteSpace(codeLanguage))
            {
                document.Blocks.Add(new Paragraph(new Run(codeLanguage.ToLowerInvariant()))
                {
                    Foreground = CreateOpacityBrush(foreground, 0.7),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 2)
                });
            }

            document.Blocks.Add(new Paragraph(new Run(code))
            {
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 11.5,
                Foreground = foreground,
                Background = codeBackground,
                Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(8)
            });
            codeBuffer.Clear();
            codeLanguage = string.Empty;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    FlushCodeBlock();
                    inCodeBlock = false;
                }
                else
                {
                    FlushParagraph();
                    inCodeBlock = true;
                    codeLanguage = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : string.Empty;
                }

                continue;
            }

            if (inCodeBlock)
            {
                codeBuffer.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph();
                continue;
            }

            if (IsMarkdownTableStart(lines, i))
            {
                FlushParagraph();
                var tableLines = new List<string> { lines[i] };
                i += 2;
                while (i < lines.Length && IsPipeRow(lines[i]))
                {
                    tableLines.Add(lines[i]);
                    i++;
                }

                i--;
                document.Blocks.Add(CreateSelectableMarkdownTable(tableLines, foreground, codeBackground));
                continue;
            }

            if (TryGetHeading(trimmed, out var headingLevel, out var headingText))
            {
                FlushParagraph();
                var heading = new Paragraph();
                AddInlineMarkdown(heading.Inlines, headingText, foreground, codeBackground);
                heading.Foreground = foreground;
                heading.FontWeight = FontWeights.Bold;
                heading.FontSize = headingLevel == 1 ? 16 : headingLevel == 2 ? 14 : 12.5;
                heading.Margin = new Thickness(0, headingLevel == 1 ? 8 : 6, 0, 4);
                document.Blocks.Add(heading);
                continue;
            }

            if (trimmed == "---" || trimmed == "***")
            {
                FlushParagraph();
                document.Blocks.Add(new Paragraph(new Run(new string('-', 40)))
                {
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    Foreground = CreateOpacityBrush(foreground, 0.65),
                    Margin = new Thickness(0, 4, 0, 4)
                });
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var quote = CreateParagraph(foreground, marginBottom: 5);
                quote.Margin = new Thickness(8, 2, 0, 5);
                quote.Padding = new Thickness(8, 0, 0, 0);
                quote.BorderThickness = new Thickness(3, 0, 0, 0);
                quote.BorderBrush = foreground;
                quote.Foreground = CreateOpacityBrush(foreground, 0.85);
                AddInlineMarkdown(quote.Inlines, trimmed.Substring(2).Trim(), foreground, codeBackground);
                document.Blocks.Add(quote);
                continue;
            }

            if (IsListItem(trimmed))
            {
                FlushParagraph();
                document.Blocks.Add(CreateSelectableListItem(trimmed, foreground, codeBackground));
                continue;
            }

            paragraphBuffer.Add(line);
        }

        if (inCodeBlock)
        {
            FlushCodeBlock();
        }

        FlushParagraph();
        return document;
    }

    public static Brush CreateOpacityBrush(Brush source, double opacity)
    {
        if (source is SolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color) { Opacity = opacity };
        }

        var clone = source.Clone();
        clone.Opacity = opacity;
        return clone;
    }

    private static FlowDocument CreateBaseFlowDocument(Brush foreground)
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = foreground,
            LineHeight = 18
        };
    }

    private static Paragraph CreateParagraph(Brush foreground, double marginBottom)
    {
        return new Paragraph
        {
            Foreground = foreground,
            Margin = new Thickness(0, 1, 0, marginBottom)
        };
    }

    private static bool TryGetHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        var count = trimmed.TakeWhile(c => c == '#').Count();
        if (count is < 1 or > 6 || trimmed.Length <= count || trimmed[count] != ' ')
        {
            return false;
        }

        level = count;
        text = trimmed.Substring(count + 1).Trim();
        return true;
    }

    private static Paragraph CreateSelectableListItem(string trimmed, Brush foreground, Brush codeBackground)
    {
        var markerEnd = trimmed.IndexOf(' ');
        var marker = markerEnd >= 0 ? trimmed.Substring(0, markerEnd) : "-";
        var body = markerEnd >= 0 ? trimmed.Substring(markerEnd + 1).Trim() : trimmed;
        if (marker == "*" || marker == "-")
        {
            marker = "-";
        }

        var paragraph = CreateParagraph(foreground, marginBottom: 3);
        paragraph.Margin = new Thickness(10, 1, 0, 3);
        paragraph.Inlines.Add(new Run(marker + " ") { FontWeight = FontWeights.Bold });
        AddInlineMarkdown(paragraph.Inlines, body, foreground, codeBackground);
        return paragraph;
    }

    private static Block CreateSelectableMarkdownTable(IReadOnlyList<string> tableLines, Brush foreground, Brush codeBackground)
    {
        var rows = tableLines.Select(SplitMarkdownTableRow).Where(row => row.Count > 0).ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        if (columnCount == 0)
        {
            return new Paragraph();
        }

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 8)
        };

        for (var col = 0; col < columnCount; col++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var tableRow = new TableRow();
            group.Rows.Add(tableRow);

            for (var col = 0; col < columnCount; col++)
            {
                var cell = col < row.Count ? StripInlineMarkdown(row[col]) : string.Empty;
                tableRow.Cells.Add(CreateTableCell(cell, foreground, codeBackground, rowIndex == 0));
            }
        }

        return table;
    }

    private static TableCell CreateTableCell(string text, Brush foreground, Brush background, bool isHeader)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            FontSize = 11,
            Foreground = foreground,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal
        };
        AddInlineMarkdown(paragraph.Inlines, text, foreground, background);

        return new TableCell(paragraph)
        {
            BorderBrush = CreateOpacityBrush(foreground, 0.18),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = isHeader ? CreateOpacityBrush(foreground, 0.08) : Brushes.Transparent,
            Padding = new Thickness(6, 4, 8, 4)
        };
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text, Brush foreground, Brush codeBackground)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inlineRegex = new System.Text.RegularExpressions.Regex(
            @"(\*\*(?<bold>.+?)\*\*)|(\*(?<italic>[^*]+?)\*)|(`(?<code>.+?)`)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var lastIndex = 0;
        var matches = inlineRegex.Matches(text);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
            }

            if (match.Groups["bold"].Success)
            {
                inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeights.Bold });
            }
            else if (match.Groups["italic"].Success)
            {
                inlines.Add(new Run(match.Groups["italic"].Value) { FontStyle = FontStyles.Italic });
            }
            else if (match.Groups["code"].Success)
            {
                inlines.Add(new Run(match.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    Background = codeBackground,
                    Foreground = foreground
                });
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            inlines.Add(new Run(text.Substring(lastIndex)));
        }
    }

    private static string StripInlineMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("**", string.Empty)
            .Replace("`", string.Empty)
            .Trim('*')
            .Trim();
    }

    private static bool IsListItem(string trimmed)
    {
        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
        {
            return true;
        }

        var markerEnd = trimmed.IndexOf(' ');
        if (markerEnd < 2)
        {
            return false;
        }

        var marker = trimmed.Substring(0, markerEnd);
        return (marker.EndsWith(".") || marker.EndsWith(")"))
            && marker.Length > 1
            && marker.Take(marker.Length - 1).All(char.IsDigit);
    }

    private static bool IsMarkdownTableStart(string[] lines, int index)
    {
        return index + 1 < lines.Length
            && IsPipeRow(lines[index])
            && IsMarkdownTableSeparator(lines[index + 1]);
    }

    private static bool IsPipeRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Contains('|') && trimmed.Count(c => c == '|') >= 2;
    }

    private static bool IsMarkdownTableSeparator(string line)
    {
        var cells = SplitMarkdownTableRow(line);
        return cells.Count > 0
            && cells.All(cell =>
            {
                var value = cell.Trim();
                return value.Length >= 3 && value.Trim('-', ':').Length == 0;
            });
    }

    private static List<string> SplitMarkdownTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|"))
        {
            trimmed = trimmed.Substring(1);
        }

        if (trimmed.EndsWith("|"))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        }

        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }
}
