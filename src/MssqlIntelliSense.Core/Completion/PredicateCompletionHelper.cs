using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class PredicateCompletionHelper
{
    public static void AddPredicateCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        int caretPosition,
        string prefix)
    {
        if (!IsPredicateStartContext(sql, caretPosition)) return;

        var sources = SqlContextAnalyzer.FindSources(sql, metadata);
        foreach (var source in sources)
        {
            foreach (var column in source.Columns.Where(c => SqlCompletionHelper.Matches(c.Name, prefix)))
            {
                var label = $"{source.Alias}.{column.Name} = ?";
                var insertText = $"{SqlCompletionHelper.Quote(source.Alias)}.{SqlCompletionHelper.Quote(column.Name)} = ?";
                var placeholderStart = insertText.Length - 1;
                suggestions.Add(new SqlCompletionItem(
                    label,
                    insertText,
                    SqlCompletionKind.Column,
                    $"Predicate for {source.Schema}.{source.Name}.{column.Name} ({column.DataType})",
                    placeholderStart,
                    placeholderStart,
                    placeholderStart + 1));
            }
        }
    }

    private static bool IsPredicateStartContext(string sql, int caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null) return false;

        var relevantTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();
        if (relevantTokens.Count == 0) return false;

        var previousTokenIndex = relevantTokens.Count - 1;
        var previousToken = relevantTokens[previousTokenIndex];
        if (previousToken.Offset + previousToken.Text.Length >= caretPosition &&
            SqlCompletionHelper.IsIdentifierOrKeyword(previousToken))
        {
            previousTokenIndex--;
        }

        if (previousTokenIndex < 0) return false;

        var previous = relevantTokens[previousTokenIndex];
        return previous.TokenType == TSqlTokenType.Where ||
               previous.Text.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
               previous.Text.Equals("OR", StringComparison.OrdinalIgnoreCase);
    }
}
