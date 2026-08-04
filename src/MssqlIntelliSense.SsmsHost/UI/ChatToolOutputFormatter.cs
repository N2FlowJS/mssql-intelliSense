using System;

namespace MssqlIntelliSense.SsmsHost;

internal static class ChatToolOutputFormatter
{
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
}
