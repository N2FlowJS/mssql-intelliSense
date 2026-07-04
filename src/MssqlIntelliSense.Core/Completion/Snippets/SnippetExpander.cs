using System;
using System.Collections.Generic;
using System.Text;

namespace MssqlIntelliSense.Core.Completion.Snippets;

public sealed record ExpandedSnippet(
    string Text,
    int CursorOffset);

public static class SnippetExpander
{
    public static ExpandedSnippet Expand(Snippet snippet, Dictionary<string, string>? builtInReplacements = null)
    {
        var body = snippet.Body;
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in snippet.Placeholders)
            resolved[p.Name] = p.DefaultValue;

        if (builtInReplacements != null)
        {
            foreach (var kv in builtInReplacements)
                resolved[kv.Key] = kv.Value;
        }

        if (!resolved.ContainsKey("CURSOR"))
            resolved["CURSOR"] = "\x1F";

        var sb = new StringBuilder(body);
        int cursorOffset = -1;

        foreach (var kv in resolved)
        {
            var placeholder = $"${kv.Key}$";
            int idx = 0;
            while ((idx = sb.ToString().IndexOf(placeholder, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                sb.Remove(idx, placeholder.Length);
                sb.Insert(idx, kv.Value);
                idx += kv.Value.Length;
            }
        }

        var finalText = sb.ToString();

        int cursorIdx = finalText.IndexOf("\x1F", StringComparison.Ordinal); // U+001F as cursor marker
        if (cursorIdx >= 0)
        {
            cursorOffset = cursorIdx;
            finalText = finalText.Remove(cursorIdx, 1);
        }

        return new ExpandedSnippet(finalText, cursorOffset);
    }
}
