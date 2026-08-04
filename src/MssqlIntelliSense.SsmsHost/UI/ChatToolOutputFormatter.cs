using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MssqlIntelliSense.SsmsHost;

internal static class ChatToolOutputFormatter
{
    private const int MaxToolUiJsonLength = 20000;
    private const int MaxToolUiArrayItems = 40;
    private const int MaxToolUiObjectProperties = 80;
    private const int MaxToolUiStringLength = 500;
    private const int MaxToolUiJsonDepth = 8;

    public static string FormatForChat(string toolName, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return $"## Tool: {toolName}\n\n(empty output)";
        }

        if (LooksLikeMarkdownToolOutput(output))
        {
            return output;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var errorElement))
            {
                return $"## Tool: {toolName}\n\n**Error:** {errorElement.GetString() ?? errorElement.ToString()}";
            }

            if (root.ValueKind == JsonValueKind.Object &&
                string.Equals(toolName, "execute", StringComparison.OrdinalIgnoreCase))
            {
                return FormatExecuteToolOutput(root, output);
            }

            var previewJson = CreateToolUiPreviewJson(root, output, out var previewed);
            var note = previewed
                ? "\n\n_UI preview is limited for responsiveness. The agent still receives the full tool output._"
                : string.Empty;
            return $"## Tool: {toolName}{note}\n\n```json\n{previewJson}\n```";
        }
        catch (JsonException)
        {
            return $"## Tool: {toolName}\n\n```\n{SummarizeToolOutput(output)}\n```";
        }
    }

    private static bool LooksLikeMarkdownToolOutput(string output)
    {
        var trimmed = output.TrimStart();
        return trimmed.StartsWith("## Tool:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("# Tool:", StringComparison.OrdinalIgnoreCase);
    }

    private static string SummarizeToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "(empty output)";
        }

        return output.Length <= 700 ? output : output.Substring(0, 700) + "...";
    }

    private static string FormatExecuteToolOutput(JsonElement root, string rawOutput)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Tool: execute");
        sb.AppendLine();
        if (root.TryGetProperty("database", out var databaseElement))
        {
            sb.AppendLine($"**Database:** `{databaseElement.GetString()}`");
        }

        var rowCount = root.TryGetProperty("rowCount", out var rowCountElement) && rowCountElement.TryGetInt32(out var rows)
            ? rows
            : 0;
        var elapsedMs = root.TryGetProperty("elapsedMs", out var elapsedElement) && elapsedElement.TryGetInt64(out var elapsed)
            ? elapsed
            : 0;
        var truncated = root.TryGetProperty("truncated", out var truncatedElement) && truncatedElement.ValueKind == JsonValueKind.True;
        sb.AppendLine($"**Rows:** `{rowCount}`  **Elapsed:** `{elapsedMs} ms`  **Truncated:** `{truncated}`");

        if (root.TryGetProperty("query", out var queryElement))
        {
            sb.AppendLine();
            sb.AppendLine("### Query");
            sb.AppendLine("```sql");
            sb.AppendLine(queryElement.GetString() ?? string.Empty);
            sb.AppendLine("```");
        }

        if (root.TryGetProperty("rows", out var rowsElement) &&
            rowsElement.ValueKind == JsonValueKind.Array &&
            rowsElement.GetArrayLength() > 0)
        {
            var columnNames = GetResultColumnNames(root, rowsElement);
            sb.AppendLine();
            sb.AppendLine("### Result Preview");
            AppendMarkdownTable(sb, columnNames, rowsElement, maxRows: 12);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No rows returned.");
        }

        sb.AppendLine();
        sb.AppendLine("### Raw JSON");
        sb.AppendLine("```json");
        sb.AppendLine(CreateToolUiPreviewJson(root, rawOutput, out _));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string CreateToolUiPreviewJson(JsonElement root, string rawOutput, out bool previewed)
    {
        previewed = rawOutput.Length > MaxToolUiJsonLength;
        if (!previewed)
        {
            return rawOutput;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteJsonPreview(root, writer, depth: 0, ref previewed);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonPreview(JsonElement element, Utf8JsonWriter writer, int depth, ref bool previewed)
    {
        if (depth >= MaxToolUiJsonDepth)
        {
            previewed = true;
            writer.WriteStringValue("... truncated by UI preview depth ...");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var propertyCount = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyCount >= MaxToolUiObjectProperties)
                    {
                        previewed = true;
                        writer.WriteString("__uiTruncatedProperties", $"showing first {MaxToolUiObjectProperties:n0} properties");
                        break;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteJsonPreview(property.Value, writer, depth + 1, ref previewed);
                    propertyCount++;
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var itemCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemCount >= MaxToolUiArrayItems)
                    {
                        previewed = true;
                        writer.WriteStartObject();
                        writer.WriteString("__uiTruncatedItems", $"showing first {MaxToolUiArrayItems:n0} items");
                        writer.WriteEndObject();
                        break;
                    }

                    WriteJsonPreview(item, writer, depth + 1, ref previewed);
                    itemCount++;
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (value.Length > MaxToolUiStringLength)
                {
                    previewed = true;
                    writer.WriteStringValue(value.Substring(0, MaxToolUiStringLength) + "...");
                }
                else
                {
                    writer.WriteStringValue(value);
                }

                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static List<string> GetResultColumnNames(JsonElement root, JsonElement rowsElement)
    {
        if (root.TryGetProperty("columns", out var columnsElement) && columnsElement.ValueKind == JsonValueKind.Array)
        {
            var names = columnsElement.EnumerateArray()
                .Select(column => column.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
            if (names.Count > 0)
            {
                return names;
            }
        }

        return rowsElement.EnumerateArray()
            .FirstOrDefault()
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();
    }

    private static void AppendMarkdownTable(StringBuilder sb, IReadOnlyList<string> columnNames, JsonElement rowsElement, int maxRows)
    {
        var visibleColumns = columnNames.Take(8).ToList();
        sb.Append("| ");
        sb.Append(string.Join(" | ", visibleColumns.Select(EscapeMarkdownTableCell)));
        sb.AppendLine(" |");
        sb.Append("| ");
        sb.Append(string.Join(" | ", visibleColumns.Select(_ => "---")));
        sb.AppendLine(" |");

        foreach (var row in rowsElement.EnumerateArray().Take(maxRows))
        {
            sb.Append("| ");
            sb.Append(string.Join(" | ", visibleColumns.Select(column =>
                EscapeMarkdownTableCell(row.TryGetProperty(column, out var value) ? FormatJsonValue(value) : string.Empty))));
            sb.AppendLine(" |");
        }

        if (columnNames.Count > visibleColumns.Count)
        {
            sb.AppendLine();
            sb.AppendLine($"Showing {visibleColumns.Count}/{columnNames.Count} columns.");
        }

        if (rowsElement.GetArrayLength() > maxRows)
        {
            sb.AppendLine($"Showing {maxRows}/{rowsElement.GetArrayLength()} rows.");
        }
    }

    private static string FormatJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => "",
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    private static string EscapeMarkdownTableCell(string? value)
    {
        return (value ?? string.Empty)
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
}
