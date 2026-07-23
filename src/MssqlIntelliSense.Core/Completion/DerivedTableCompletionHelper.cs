using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class DerivedTableCompletionHelper
{
    public static IReadOnlyList<VisibleSource> FindDerivedTableSources(string sql)
    {
        var tokens = GetActiveTokens(sql);
        if (tokens.Count == 0) return [];

        var sources = new List<VisibleSource>();
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.From &&
                tokens[i].TokenType != TSqlTokenType.Join)
            {
                continue;
            }

            var bodyStartIndex = i + 1;
            if (tokens[bodyStartIndex].TokenType != TSqlTokenType.LeftParenthesis ||
                !ParenthesizedBodyStartsWithSelect(tokens, bodyStartIndex))
            {
                continue;
            }

            var columns = CteCompletionHelper.ReadInferredColumnList(tokens, bodyStartIndex);
            if (columns.Count == 0)
                continue;

            var afterBody = SkipParenthesizedExpression(tokens, bodyStartIndex);
            if (afterBody < 0 || afterBody >= tokens.Count)
                continue;

            var aliasIndex = afterBody;
            if (tokens[aliasIndex].TokenType == TSqlTokenType.As)
            {
                aliasIndex++;
            }
            if (aliasIndex >= tokens.Count ||
                !SqlCompletionHelper.IsIdentifierOrKeyword(tokens[aliasIndex]) ||
                IsAliasStopWord(tokens[aliasIndex].Text))
            {
                continue;
            }

            var alias = SqlCompletionHelper.Unquote(tokens[aliasIndex].Text);
            sources.Add(new VisibleSource("derived", alias, alias, columns));
        }

        return sources;
    }

    private static bool ParenthesizedBodyStartsWithSelect(
        IReadOnlyList<TSqlParserToken> tokens,
        int bodyStartIndex)
    {
        return bodyStartIndex + 1 < tokens.Count &&
               tokens[bodyStartIndex + 1].TokenType == TSqlTokenType.Select;
    }

    private static int SkipParenthesizedExpression(IReadOnlyList<TSqlParserToken> tokens, int startIndex)
    {
        var depth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
            }
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
}
