using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class ComparisonRhsCompletionHelper
{
    public static void AddComparisonRhsCompletions(
        List<SqlCompletionItem> suggestions,
        string sql,
        int caretPosition,
        string prefix)
    {
        var context = DetectContext(sql, caretPosition);
        if (context == RhsContext.None)
            return;

        if (context == RhsContext.Like && SqlCompletionHelper.Matches("LIKE pattern", prefix))
        {
            AddSnippet(suggestions, "LIKE pattern", "N'%?%'", "LIKE search pattern");
        }
        else if (context == RhsContext.In && SqlCompletionHelper.Matches("IN list", prefix))
        {
            AddSnippet(suggestions, "IN list", "(?)", "IN value list");
        }
        else if (context == RhsContext.Between && SqlCompletionHelper.Matches("BETWEEN range", prefix))
        {
            AddSnippet(suggestions, "BETWEEN range", "? AND ?", "BETWEEN range values");
        }
    }

    private static void AddSnippet(
        List<SqlCompletionItem> suggestions,
        string label,
        string insertText,
        string description)
    {
        var placeholderStart = insertText.IndexOf("?", StringComparison.Ordinal);
        suggestions.Add(new SqlCompletionItem(
            label,
            insertText,
            SqlCompletionKind.Snippet,
            description,
            placeholderStart,
            placeholderStart,
            placeholderStart + 1));
    }

    private static RhsContext DetectContext(string sql, int caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null)
            return RhsContext.None;

        var relevantTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var previousTokenIndex = relevantTokens.Count - 1;
        if (previousTokenIndex < 0)
            return RhsContext.None;

        var previousToken = relevantTokens[previousTokenIndex];
        if (previousToken.Offset + previousToken.Text.Length >= caretPosition &&
            SqlCompletionHelper.IsIdentifierOrKeyword(previousToken))
        {
            previousTokenIndex--;
        }

        for (var i = previousTokenIndex; i >= 0; i--)
        {
            var token = relevantTokens[i];
            if (token.TokenType == TSqlTokenType.Like)
                return RhsContext.Like;
            if (token.Text.Equals("IN", StringComparison.OrdinalIgnoreCase))
                return RhsContext.In;
            if (token.Text.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase))
                return RhsContext.Between;
            if (token.TokenType == TSqlTokenType.Where ||
                token.TokenType == TSqlTokenType.Having ||
                token.TokenType == TSqlTokenType.Join ||
                token.TokenType == TSqlTokenType.Comma ||
                token.TokenType == TSqlTokenType.Semicolon)
            {
                break;
            }
        }

        return RhsContext.None;
    }

    private enum RhsContext
    {
        None,
        Like,
        In,
        Between
    }
}
