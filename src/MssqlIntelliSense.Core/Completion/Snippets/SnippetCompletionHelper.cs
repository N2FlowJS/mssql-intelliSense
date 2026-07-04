using System;
using System.Collections.Generic;
using MssqlIntelliSense.Core.Completion.Candidates;

namespace MssqlIntelliSense.Core.Completion.Snippets;

public static class SnippetCompletionHelper
{
    public static void AddSnippetCompletions(
        List<SqlCompletionItem> suggestions,
        string prefix,
        ICandidateUsageRecorder? usageRecorder,
        IEnumerable<Snippet> snippets)
    {
        foreach (var snippet in snippets)
        {
            if (!SqlCompletionHelper.Matches(snippet.Prefix, prefix))
                continue;

            var expanded = SnippetExpander.Expand(snippet, new Dictionary<string, string>
            {
                ["CURSOR"] = "\x1F",
                ["SELECTEDTEXT"] = "",
                ["USER"] = Environment.UserName,
                ["MACHINE"] = Environment.MachineName,
                ["GUID"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
                ["DATE"] = DateTime.Now.ToShortDateString(),
                ["TIME"] = DateTime.Now.ToShortTimeString()
            });

            suggestions.Add(new SqlCompletionItem(
                snippet.Prefix,
                expanded.Text,
                SqlCompletionKind.Snippet,
                snippet.Description,
                CaretOffset: expanded.CursorOffset >= 0 ? expanded.CursorOffset : -1));
        }
    }
}
