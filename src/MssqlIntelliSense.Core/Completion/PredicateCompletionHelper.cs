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
        if (IsHavingStartContext(sql, caretPosition))
        {
            AddAggregatePredicateItem(suggestions, prefix, "COUNT(*) > ?", "COUNT predicate");
            AddAggregatePredicateItem(suggestions, prefix, "SUM(?) > ?", "SUM predicate");
            AddAggregatePredicateItem(suggestions, prefix, "AVG(?) > ?", "AVG predicate");
            AddAggregatePredicateItem(suggestions, prefix, "MIN(?) = ?", "MIN predicate");
            AddAggregatePredicateItem(suggestions, prefix, "MAX(?) = ?", "MAX predicate");
        }

        foreach (var source in sources)
        {
            foreach (var column in source.Columns.Where(c => SqlCompletionHelper.Matches(c.Name, prefix)))
            {
                var qualifiedLabel = $"{source.Alias}.{column.Name}";
                var qualifiedInsert = $"{SqlCompletionHelper.Quote(source.Alias)}.{SqlCompletionHelper.Quote(column.Name)}";
                var description = $"Predicate for {source.Schema}.{source.Name}.{column.Name} ({column.DataType})";

                AddPredicateItem(suggestions, qualifiedLabel, qualifiedInsert, " = ?", description);
                AddPredicateItem(suggestions, qualifiedLabel, qualifiedInsert, " LIKE ?", description);
                AddPredicateItem(suggestions, qualifiedLabel, qualifiedInsert, " BETWEEN ? AND ?", description);
                AddPredicateItem(suggestions, qualifiedLabel, qualifiedInsert, " IN (?)", description);
                AddPredicateItem(suggestions, qualifiedLabel, qualifiedInsert, " IS NULL", description);
                AddPredicateItem(suggestions, qualifiedLabel, qualifiedInsert, " IS NOT NULL", description);
            }
        }
    }

    private static void AddPredicateItem(
        List<SqlCompletionItem> suggestions,
        string qualifiedLabel,
        string qualifiedInsert,
        string operatorText,
        string description)
    {
        var label = $"{qualifiedLabel}{operatorText}";
        var insertText = $"{qualifiedInsert}{operatorText}";
        var placeholderStart = insertText.IndexOf("?", StringComparison.Ordinal);
        var selectionEnd = placeholderStart >= 0 ? placeholderStart + 1 : -1;
        suggestions.Add(new SqlCompletionItem(
            label,
            insertText,
            SqlCompletionKind.Column,
            description,
            placeholderStart,
            placeholderStart,
            selectionEnd));
    }

    private static void AddAggregatePredicateItem(
        List<SqlCompletionItem> suggestions,
        string prefix,
        string insertText,
        string description)
    {
        var label = insertText;
        if (!SqlCompletionHelper.Matches(label, prefix))
            return;

        var placeholderStart = insertText.IndexOf("?", StringComparison.Ordinal);
        var selectionEnd = placeholderStart >= 0 ? placeholderStart + 1 : -1;
        suggestions.Add(new SqlCompletionItem(
            label,
            insertText,
            SqlCompletionKind.Snippet,
            description,
            placeholderStart,
            placeholderStart,
            selectionEnd));
    }

    private static bool IsPredicateStartContext(string sql, int caretPosition)
    {
        return GetPredicateStartToken(sql, caretPosition) != null;
    }

    private static bool IsHavingStartContext(string sql, int caretPosition)
    {
        var token = GetPredicateStartToken(sql, caretPosition);
        return token?.TokenType == TSqlTokenType.Having;
    }

    private static TSqlParserToken? GetPredicateStartToken(string sql, int caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null) return null;

        var relevantTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();
        if (relevantTokens.Count == 0) return null;

        var previousTokenIndex = relevantTokens.Count - 1;
        var previousToken = relevantTokens[previousTokenIndex];
        if (previousToken.Offset + previousToken.Text.Length >= caretPosition &&
            SqlCompletionHelper.IsIdentifierOrKeyword(previousToken))
        {
            previousTokenIndex--;
        }

        if (previousTokenIndex < 0) return null;

        var previous = relevantTokens[previousTokenIndex];
        return previous.TokenType == TSqlTokenType.Where ||
               previous.TokenType == TSqlTokenType.Having ||
               previous.Text.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
               previous.Text.Equals("OR", StringComparison.OrdinalIgnoreCase)
            ? previous
            : null;
    }
}
