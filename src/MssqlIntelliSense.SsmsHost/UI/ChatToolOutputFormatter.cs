using System;
using System.Linq;

namespace MssqlIntelliSense.SsmsHost;

internal static class ChatToolOutputFormatter
{
    private const int MaxAgentContextChars = 1800;
    private const int MaxAgentContextLines = 45;

    public static string FormatForChat(string toolName, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return $"## Tool: {toolName}\n\n(empty output)";
        }

        var trimmed = output.TrimStart();
        if (trimmed.StartsWith("## Tool:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("# Tool:", StringComparison.OrdinalIgnoreCase))
        {
            return output;
        }

        return $"## Tool: {toolName}\n\n{output}";
    }

    public static string FormatForAgentContext(string toolName, string output)
    {
        var markdown = FormatForChat(toolName, output).Trim();
        if (markdown.Length <= MaxAgentContextChars)
        {
            return markdown;
        }

        var lines = markdown
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MaxAgentContextLines)
            .ToList();

        var compact = string.Join("\n", lines);
        if (compact.Length > MaxAgentContextChars)
        {
            compact = compact.Substring(0, MaxAgentContextChars).TrimEnd();
        }

        return compact + "\n\n_Output truncated for LLM context; full markdown is visible in the chat/tool UI._";
    }
}
