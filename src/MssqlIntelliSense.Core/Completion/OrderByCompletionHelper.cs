using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class OrderByCompletionHelper
{
    public static void AddOrderByCompletions(
        List<SqlCompletionItem> suggestions,
        string sql,
        int caretPosition,
        string prefix)
    {
        if (!IsOrderByClauseContext(sql, caretPosition))
            return;

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

    public static bool IsOrderByClauseContext(string sql, int caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null)
            return false;

        var relevantTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var previousTokenIndex = relevantTokens.Count - 1;
        if (previousTokenIndex < 0)
            return false;

        var previousToken = relevantTokens[previousTokenIndex];
        if (previousToken.Offset + previousToken.Text.Length >= caretPosition &&
            SqlCompletionHelper.IsIdentifierOrKeyword(previousToken))
        {
            previousTokenIndex--;
        }

        for (var i = previousTokenIndex; i >= 0; i--)
        {
            var token = relevantTokens[i];
            if (token.TokenType == TSqlTokenType.Order)
                return true;
            if (token.TokenType == TSqlTokenType.Group)
                return false;
            if (token.TokenType == TSqlTokenType.Select ||
                token.TokenType == TSqlTokenType.From ||
                token.TokenType == TSqlTokenType.Where ||
                token.TokenType == TSqlTokenType.Having ||
                token.TokenType == TSqlTokenType.Join ||
                token.TokenType == TSqlTokenType.Semicolon)
            {
                break;
            }
        }

        return false;
    }
}
