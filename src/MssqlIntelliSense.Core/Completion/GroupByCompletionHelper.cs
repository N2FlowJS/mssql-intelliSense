using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class GroupByCompletionHelper
{
    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP", "STRING_AGG"
    };

    public static void AddGroupByCompletions(
        List<SqlCompletionItem> suggestions,
        string sql,
        int caretPosition,
        string prefix)
    {
        if (!IsGroupByClauseContext(sql, caretPosition))
            return;

        if (!string.IsNullOrEmpty(prefix) &&
            !SqlCompletionHelper.Matches("GROUP BY SELECT columns", prefix))
        {
            return;
        }

        var groupableExpressions = ExtractGroupableSelectExpressions(sql, caretPosition);
        if (groupableExpressions.Count == 0)
            return;

        var insertText = string.Join(", ", groupableExpressions);
        suggestions.Add(new SqlCompletionItem(
            "GROUP BY SELECT columns",
            insertText,
            SqlCompletionKind.Snippet,
            "Group by non-aggregate SELECT expressions"));
    }

    public static bool IsGroupByClauseContext(string sql, int caretPosition)
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
            if (token.TokenType == TSqlTokenType.Group)
                return true;
            if (token.TokenType == TSqlTokenType.Order)
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

    private static IReadOnlyList<string> ExtractGroupableSelectExpressions(string sql, int caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null)
            return [];

        var activeTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var selectIndex = -1;
        var fromIndex = -1;
        for (var i = 0; i < activeTokens.Count; i++)
        {
            var tokenType = activeTokens[i].TokenType;
            if (tokenType == TSqlTokenType.Select)
            {
                selectIndex = i;
                fromIndex = -1;
            }
            else if (tokenType == TSqlTokenType.From && selectIndex >= 0)
            {
                fromIndex = i;
                break;
            }
        }

        if (selectIndex < 0 || fromIndex <= selectIndex)
            return [];

        var expressions = new List<string>();
        var current = new List<TSqlParserToken>();
        var depth = 0;

        for (var i = selectIndex + 1; i < fromIndex; i++)
        {
            var token = activeTokens[i];
            if (token.TokenType == TSqlTokenType.LeftParenthesis)
                depth++;
            else if (token.TokenType == TSqlTokenType.RightParenthesis && depth > 0)
                depth--;

            if (token.TokenType == TSqlTokenType.Comma && depth == 0)
            {
                AddGroupableExpression(expressions, current);
                current.Clear();
                continue;
            }

            current.Add(token);
        }

        AddGroupableExpression(expressions, current);
        return expressions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddGroupableExpression(List<string> expressions, List<TSqlParserToken> tokens)
    {
        if (tokens.Count == 0 || ContainsAggregateFunction(tokens))
            return;

        var expressionTokens = RemoveAlias(tokens);
        if (expressionTokens.Count == 0 ||
            expressionTokens.Any(t => t.TokenType == TSqlTokenType.Star))
        {
            return;
        }

        var expression = string.Concat(expressionTokens.Select(t => t.Text));
        if (!string.IsNullOrWhiteSpace(expression))
            expressions.Add(expression);
    }

    private static bool ContainsAggregateFunction(IReadOnlyList<TSqlParserToken> tokens)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (AggregateFunctions.Contains(tokens[i].Text) &&
                tokens[i + 1].TokenType == TSqlTokenType.LeftParenthesis)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<TSqlParserToken> RemoveAlias(IReadOnlyList<TSqlParserToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType == TSqlTokenType.As)
                return tokens.Take(i).ToArray();
        }

        if (tokens.Count >= 2 &&
            SqlCompletionHelper.IsIdentifierOrKeyword(tokens[^1]) &&
            tokens[^2].TokenType != TSqlTokenType.Dot)
        {
            return tokens.Take(tokens.Count - 1).ToArray();
        }

        return tokens;
    }
}
