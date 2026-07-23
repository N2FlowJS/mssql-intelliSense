using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class CteCompletionHelper
{
    public static IReadOnlyList<VisibleSource> FindCteSources(string sql)
    {
        var tokens = GetActiveTokens(sql);
        if (tokens.Count == 0) return [];

        var definitions = ExtractCteDefinitions(tokens);
        if (definitions.Count == 0) return [];

        var sources = new List<VisibleSource>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.From &&
                tokens[i].TokenType != TSqlTokenType.Join)
            {
                continue;
            }

            var tableTokenIndex = i + 1;
            if (tableTokenIndex >= tokens.Count ||
                !SqlCompletionHelper.IsIdentifierOrKeyword(tokens[tableTokenIndex]))
            {
                continue;
            }

            var cteName = SqlCompletionHelper.Unquote(tokens[tableTokenIndex].Text);
            if (!definitions.TryGetValue(cteName, out var columns))
                continue;

            var alias = cteName;
            var aliasTokenIndex = tableTokenIndex + 1;
            if (aliasTokenIndex < tokens.Count && tokens[aliasTokenIndex].TokenType == TSqlTokenType.As)
            {
                aliasTokenIndex++;
            }
            if (aliasTokenIndex < tokens.Count &&
                SqlCompletionHelper.IsIdentifierOrKeyword(tokens[aliasTokenIndex]) &&
                !IsAliasStopWord(tokens[aliasTokenIndex].Text))
            {
                alias = SqlCompletionHelper.Unquote(tokens[aliasTokenIndex].Text);
            }

            sources.Add(new VisibleSource("cte", cteName, alias, columns));
        }

        return sources;
    }

    private static Dictionary<string, IReadOnlyList<ColumnMetadata>> ExtractCteDefinitions(
        IReadOnlyList<TSqlParserToken> tokens)
    {
        var definitions = new Dictionary<string, IReadOnlyList<ColumnMetadata>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.With)
                continue;

            var scan = i + 1;
            while (scan < tokens.Count)
            {
                if (!SqlCompletionHelper.IsIdentifierOrKeyword(tokens[scan]))
                    break;

                var cteName = SqlCompletionHelper.Unquote(tokens[scan].Text);
                var columns = ReadExplicitColumnList(tokens, scan + 1);
                var asIndex = FindNextToken(tokens, scan + 1, TSqlTokenType.As);
                if (asIndex < 0)
                    break;

                if (columns.Count == 0)
                {
                    columns = ReadInferredColumnList(tokens, asIndex + 1);
                }
                if (columns.Count > 0)
                {
                    definitions[cteName] = columns;
                }

                var afterBody = SkipParenthesizedExpression(tokens, asIndex + 1);
                if (afterBody < 0 || afterBody >= tokens.Count || tokens[afterBody].TokenType != TSqlTokenType.Comma)
                    break;

                scan = afterBody + 1;
            }
        }

        return definitions;
    }

    private static IReadOnlyList<ColumnMetadata> ReadExplicitColumnList(
        IReadOnlyList<TSqlParserToken> tokens,
        int startIndex)
    {
        if (startIndex >= tokens.Count || tokens[startIndex].TokenType != TSqlTokenType.LeftParenthesis)
            return [];

        var columns = new List<ColumnMetadata>();
        var depth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
                continue;
            }
            if (token.TokenType == TSqlTokenType.RightParenthesis)
            {
                depth--;
                if (depth <= 0)
                    break;
                continue;
            }

            if (depth == 1 && SqlCompletionHelper.IsIdentifierOrKeyword(token))
            {
                var name = SqlCompletionHelper.Unquote(token.Text);
                columns.Add(new ColumnMetadata(name, "sql_variant", true, columns.Count + 1));
            }
        }

        return columns;
    }

    internal static IReadOnlyList<ColumnMetadata> ReadInferredColumnList(
        IReadOnlyList<TSqlParserToken> tokens,
        int bodyStartIndex)
    {
        if (bodyStartIndex >= tokens.Count || tokens[bodyStartIndex].TokenType != TSqlTokenType.LeftParenthesis)
            return [];

        var selectIndex = bodyStartIndex + 1;
        while (selectIndex < tokens.Count && tokens[selectIndex].TokenType != TSqlTokenType.Select)
        {
            if (tokens[selectIndex].TokenType == TSqlTokenType.RightParenthesis)
                return [];
            selectIndex++;
        }
        if (selectIndex >= tokens.Count)
            return [];

        var columns = new List<ColumnMetadata>();
        var expressionTokens = new List<TSqlParserToken>();
        var depth = 0;

        for (var i = selectIndex + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 && token.TokenType == TSqlTokenType.From)
            {
                AddInferredColumn(columns, expressionTokens);
                break;
            }

            if (token.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
            }
            else if (token.TokenType == TSqlTokenType.RightParenthesis)
            {
                if (depth == 0)
                {
                    AddInferredColumn(columns, expressionTokens);
                    break;
                }
                depth--;
            }

            if (depth == 0 && token.TokenType == TSqlTokenType.Comma)
            {
                AddInferredColumn(columns, expressionTokens);
                expressionTokens.Clear();
                continue;
            }

            expressionTokens.Add(token);
        }

        return columns;
    }

    private static void AddInferredColumn(
        List<ColumnMetadata> columns,
        IReadOnlyList<TSqlParserToken> expressionTokens)
    {
        var name = TryInferColumnName(expressionTokens);
        if (name is not { Length: > 0 } ||
            columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        columns.Add(new ColumnMetadata(name, "sql_variant", true, columns.Count + 1));
    }

    private static string? TryInferColumnName(IReadOnlyList<TSqlParserToken> expressionTokens)
    {
        for (var i = 0; i < expressionTokens.Count - 1; i++)
        {
            if (expressionTokens[i].TokenType == TSqlTokenType.As &&
                SqlCompletionHelper.IsIdentifierOrKeyword(expressionTokens[i + 1]))
            {
                return SqlCompletionHelper.Unquote(expressionTokens[i + 1].Text);
            }
        }

        for (var i = expressionTokens.Count - 1; i >= 0; i--)
        {
            var token = expressionTokens[i];
            if (SqlCompletionHelper.IsIdentifierOrKeyword(token) &&
                !IsExpressionKeyword(token.Text))
            {
                return SqlCompletionHelper.Unquote(token.Text);
            }
        }

        return null;
    }

    private static int FindNextToken(
        IReadOnlyList<TSqlParserToken> tokens,
        int startIndex,
        TSqlTokenType tokenType)
    {
        var depth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
                depth++;
            else if (tokens[i].TokenType == TSqlTokenType.RightParenthesis)
                depth = Math.Max(0, depth - 1);

            if (depth == 0 && tokens[i].TokenType == tokenType)
                return i;

            if (depth == 0 && tokens[i].TokenType == TSqlTokenType.Select)
                return -1;
        }

        return -1;
    }

    private static int SkipParenthesizedExpression(IReadOnlyList<TSqlParserToken> tokens, int startIndex)
    {
        if (startIndex >= tokens.Count || tokens[startIndex].TokenType != TSqlTokenType.LeftParenthesis)
            return -1;

        var depth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
                depth++;
            else if (tokens[i].TokenType == TSqlTokenType.RightParenthesis)
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }
        }

        return -1;
    }

    private static IReadOnlyList<TSqlParserToken> GetActiveTokens(string sql)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null) return [];

        return tokens
            .Where(t => t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();
    }

    private static bool IsAliasStopWord(string text) =>
        text.Equals("WHERE", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("JOIN", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("GROUP", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("ORDER", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("HAVING", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpressionKeyword(string text) =>
        text.Equals("DISTINCT", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("TOP", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("CASE", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("WHEN", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("THEN", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("ELSE", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("END", StringComparison.OrdinalIgnoreCase);
}
