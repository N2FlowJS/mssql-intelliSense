using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class ComparisonRhsCompletionHelper
{
    public static void AddComparisonRhsCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
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
        else if (context == RhsContext.BetweenEnd && SqlCompletionHelper.Matches("BETWEEN end", prefix))
        {
            AddSnippet(suggestions, "BETWEEN end", "?", "BETWEEN end value");
        }
        else if (context == RhsContext.Value)
        {
            AddTypedValueCompletions(suggestions, metadata, sql, caretPosition, prefix);
            if (SqlCompletionHelper.Matches("value", prefix))
            {
                AddSnippet(suggestions, "value", "?", "Comparison value");
            }
        }
    }

    private static void AddTypedValueCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        int caretPosition,
        string prefix)
    {
        var dataType = TryGetComparedColumnDataType(sql, caretPosition, metadata);
        if (dataType == null)
            return;

        if (IsStringType(dataType) && SqlCompletionHelper.Matches("string value", prefix))
        {
            AddSnippet(suggestions, "string value", "N'?'", "Unicode string comparison value");
        }
        else if (IsDateOrTimeType(dataType) && SqlCompletionHelper.Matches("date value", prefix))
        {
            AddSnippet(suggestions, "date value", "'?'", "Date/time comparison value");
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
        if (previousTokenIndex >= 0 &&
            relevantTokens[previousTokenIndex].Text.Equals("AND", StringComparison.OrdinalIgnoreCase) &&
            IsInsideBetween(relevantTokens, previousTokenIndex - 1))
        {
            return RhsContext.BetweenEnd;
        }

        for (var i = previousTokenIndex; i >= 0; i--)
        {
            var token = relevantTokens[i];
            if (IsComparisonOperator(token))
            {
                return RhsContext.Value;
            }
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

    private static string? TryGetComparedColumnDataType(string sql, int caretPosition, DatabaseMetadata metadata)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null)
            return null;

        var relevantTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var previousTokenIndex = relevantTokens.Count - 1;
        if (previousTokenIndex < 0)
            return null;

        var previousToken = relevantTokens[previousTokenIndex];
        if (previousToken.Offset + previousToken.Text.Length >= caretPosition &&
            SqlCompletionHelper.IsIdentifierOrKeyword(previousToken))
        {
            previousTokenIndex--;
        }

        for (var i = previousTokenIndex; i >= 0; i--)
        {
            var token = relevantTokens[i];
            if (IsComparisonOperator(token))
            {
                return TryGetColumnDataTypeBeforeOperator(sql, metadata, relevantTokens, i);
            }

            if (token.TokenType == TSqlTokenType.Where ||
                token.TokenType == TSqlTokenType.Having ||
                token.TokenType == TSqlTokenType.Join ||
                token.TokenType == TSqlTokenType.Comma ||
                token.TokenType == TSqlTokenType.Semicolon)
            {
                break;
            }
        }

        return null;
    }

    private static string? TryGetColumnDataTypeBeforeOperator(
        string sql,
        DatabaseMetadata metadata,
        IReadOnlyList<TSqlParserToken> tokens,
        int operatorIndex)
    {
        while (operatorIndex > 0 && IsComparisonOperator(tokens[operatorIndex - 1]))
        {
            operatorIndex--;
        }

        var columnIndex = operatorIndex - 1;
        if (columnIndex < 0 || !SqlCompletionHelper.IsIdentifierOrKeyword(tokens[columnIndex]))
            return null;

        var columnName = SqlCompletionHelper.Unquote(tokens[columnIndex].Text);
        string? alias = null;
        if (columnIndex >= 2 &&
            tokens[columnIndex - 1].TokenType == TSqlTokenType.Dot &&
            SqlCompletionHelper.IsIdentifierOrKeyword(tokens[columnIndex - 2]))
        {
            alias = SqlCompletionHelper.Unquote(tokens[columnIndex - 2].Text);
        }

        var sources = SqlContextAnalyzer.FindSources(sql, metadata);
        if (alias != null)
        {
            var source = sources.FirstOrDefault(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
            return source?.Columns.FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))?.DataType;
        }

        var matches = sources
            .SelectMany(s => s.Columns)
            .Where(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0].DataType : null;
    }

    private static bool IsStringType(string dataType)
    {
        return dataType.Contains("char", StringComparison.OrdinalIgnoreCase) ||
               dataType.Contains("text", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDateOrTimeType(string dataType)
    {
        return dataType.Contains("date", StringComparison.OrdinalIgnoreCase) ||
               dataType.Contains("time", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsComparisonOperator(TSqlParserToken token)
    {
        if (token.TokenType == TSqlTokenType.EqualsSign ||
            token.TokenType == TSqlTokenType.LessThan ||
            token.TokenType == TSqlTokenType.GreaterThan)
        {
            return true;
        }

        return token.Text is "=" or "<" or ">" or "<=" or ">=" or "<>" or "!=";
    }

    private static bool IsInsideBetween(IReadOnlyList<TSqlParserToken> tokens, int fromIndex)
    {
        for (var i = fromIndex; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.Text.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase))
                return true;
            if (token.TokenType == TSqlTokenType.Where ||
                token.TokenType == TSqlTokenType.Having ||
                token.TokenType == TSqlTokenType.Join ||
                token.TokenType == TSqlTokenType.Comma ||
                token.TokenType == TSqlTokenType.Semicolon)
            {
                break;
            }
        }

        return false;
    }

    private enum RhsContext
    {
        None,
        Like,
        In,
        Between,
        BetweenEnd,
        Value
    }
}
