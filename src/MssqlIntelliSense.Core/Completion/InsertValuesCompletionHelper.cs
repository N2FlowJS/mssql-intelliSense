using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class InsertValuesCompletionHelper
{
    public static void AddInsertValuesCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        int caretPosition,
        string prefix)
    {
        if (!string.IsNullOrEmpty(prefix) &&
            !SqlCompletionHelper.Matches("VALUES placeholders", prefix))
        {
            return;
        }

        var placeholderCount = TryGetPlaceholderCount(sql, caretPosition, metadata);
        if (placeholderCount <= 0)
            return;

        var insertText = string.Join(", ", Enumerable.Repeat("?", placeholderCount));
        suggestions.Add(new SqlCompletionItem(
            "VALUES placeholders",
            insertText,
            SqlCompletionKind.Snippet,
            "INSERT VALUES placeholders",
            0,
            0,
            1));
    }

    private static int TryGetPlaceholderCount(string sql, int caretPosition, DatabaseMetadata metadata)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null)
            return 0;

        var relevantTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var previousTokenIndex = relevantTokens.Count - 1;
        if (previousTokenIndex < 0)
            return 0;

        var previousToken = relevantTokens[previousTokenIndex];
        if (previousToken.Offset + previousToken.Text.Length >= caretPosition &&
            SqlCompletionHelper.IsIdentifierOrKeyword(previousToken))
        {
            previousTokenIndex--;
        }

        var valuesOpenIndex = FindOpenValuesParenthesis(relevantTokens, previousTokenIndex);
        if (valuesOpenIndex < 1 ||
            !relevantTokens[valuesOpenIndex - 1].Text.Equals("VALUES", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var valuesKeywordIndex = valuesOpenIndex - 1;
        var intoIndex = FindPrevious(relevantTokens, valuesKeywordIndex - 1, TSqlTokenType.Into);
        if (intoIndex < 0 || FindPrevious(relevantTokens, intoIndex - 1, TSqlTokenType.Insert) < 0)
            return 0;

        var tableEndIndex = FindTableEndIndex(relevantTokens, intoIndex + 1, valuesKeywordIndex);
        if (tableEndIndex < 0)
            return 0;

        var explicitColumns = TryGetExplicitColumnCount(relevantTokens, tableEndIndex + 1, valuesKeywordIndex);
        if (explicitColumns > 0)
            return explicitColumns;

        return TryGetTargetColumnCount(relevantTokens, intoIndex + 1, tableEndIndex, metadata);
    }

    private static int FindOpenValuesParenthesis(IReadOnlyList<TSqlParserToken> tokens, int fromIndex)
    {
        var depth = 0;
        for (var i = fromIndex; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.RightParenthesis)
            {
                depth++;
            }
            else if (token.TokenType == TSqlTokenType.LeftParenthesis)
            {
                if (depth == 0)
                    return i;
                depth--;
            }

            if (token.TokenType == TSqlTokenType.Semicolon ||
                token.TokenType == TSqlTokenType.Select ||
                token.TokenType == TSqlTokenType.From)
            {
                break;
            }
        }

        return -1;
    }

    private static int FindPrevious(IReadOnlyList<TSqlParserToken> tokens, int fromIndex, TSqlTokenType tokenType)
    {
        for (var i = fromIndex; i >= 0; i--)
        {
            if (tokens[i].TokenType == tokenType)
                return i;
            if (tokens[i].TokenType == TSqlTokenType.Semicolon)
                break;
        }

        return -1;
    }

    private static int FindTableEndIndex(IReadOnlyList<TSqlParserToken> tokens, int fromIndex, int beforeIndex)
    {
        var lastIdentifier = -1;
        for (var i = fromIndex; i < beforeIndex; i++)
        {
            var token = tokens[i];
            if (SqlCompletionHelper.IsIdentifierOrKeyword(token))
            {
                lastIdentifier = i;
                continue;
            }

            if (token.TokenType == TSqlTokenType.Dot)
                continue;

            break;
        }

        return lastIdentifier;
    }

    private static int TryGetExplicitColumnCount(IReadOnlyList<TSqlParserToken> tokens, int fromIndex, int beforeIndex)
    {
        if (fromIndex >= beforeIndex || tokens[fromIndex].TokenType != TSqlTokenType.LeftParenthesis)
            return 0;

        var count = 0;
        var depth = 0;
        var hasColumnToken = false;
        for (var i = fromIndex + 1; i < beforeIndex; i++)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
            }
            else if (token.TokenType == TSqlTokenType.RightParenthesis)
            {
                if (depth == 0)
                    return hasColumnToken ? count + 1 : count;
                depth--;
            }
            else if (token.TokenType == TSqlTokenType.Comma && depth == 0)
            {
                if (hasColumnToken)
                    count++;
                hasColumnToken = false;
            }
            else if (SqlCompletionHelper.IsIdentifierOrKeyword(token))
            {
                hasColumnToken = true;
            }
        }

        return 0;
    }

    private static int TryGetTargetColumnCount(
        IReadOnlyList<TSqlParserToken> tokens,
        int fromIndex,
        int tableEndIndex,
        DatabaseMetadata metadata)
    {
        var parts = new List<string>();
        for (var i = fromIndex; i <= tableEndIndex; i++)
        {
            if (SqlCompletionHelper.IsIdentifierOrKeyword(tokens[i]))
                parts.Add(SqlCompletionHelper.Unquote(tokens[i].Text));
        }

        if (parts.Count == 0)
            return 0;

        var tableName = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;

        var table = metadata.Tables.FirstOrDefault(t =>
            t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            (schema == null || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));

        return table?.Columns.Count ?? 0;
    }
}
