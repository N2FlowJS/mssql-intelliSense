using System;
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Completion;

public static class OrderByCompletionHelper
{
    public static void AddOrderByCompletions(List<SqlCompletionItem> suggestions, string prefix)
    {
        AddSimpleKeyword(suggestions, prefix, "ASC");
        AddSimpleKeyword(suggestions, prefix, "DESC");

        if (SqlCompletionHelper.Matches("OFFSET", prefix))
        {
            var insertText = "OFFSET ? ROWS FETCH NEXT ? ROWS ONLY";
            var placeholderStart = insertText.IndexOf("?", StringComparison.Ordinal);
            suggestions.Add(new SqlCompletionItem(
                "OFFSET FETCH",
                insertText,
                SqlCompletionKind.Snippet,
                "ORDER BY paging",
                placeholderStart,
                placeholderStart,
                placeholderStart + 1));
        }

        if (SqlCompletionHelper.Matches("FETCH", prefix))
        {
            var insertText = "FETCH NEXT ? ROWS ONLY";
            var placeholderStart = insertText.IndexOf("?", StringComparison.Ordinal);
            suggestions.Add(new SqlCompletionItem(
                "FETCH NEXT",
                insertText,
                SqlCompletionKind.Snippet,
                "ORDER BY fetch count",
                placeholderStart,
                placeholderStart,
                placeholderStart + 1));
        }
    }

    private static void AddSimpleKeyword(List<SqlCompletionItem> suggestions, string prefix, string keyword)
    {
        if (!SqlCompletionHelper.Matches(keyword, prefix))
            return;

        suggestions.Add(new SqlCompletionItem(
            keyword,
            keyword,
            SqlCompletionKind.Keyword,
            "ORDER BY direction"));
    }
}
