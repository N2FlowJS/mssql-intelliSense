using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class OutputCompletionHelper
{
    public static void AddOutputCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        int caretPosition,
        string prefix)
    {
        var context = TryGetOutputContext(metadata, sql, caretPosition);
        if (context == null)
            return;

        if (context.Operation is DmlOperation.Insert or DmlOperation.Update)
        {
            AddColumnListSnippet(suggestions, prefix, "INSERTED", context.Columns);
        }
        if (context.Operation is DmlOperation.Delete or DmlOperation.Update)
        {
            AddColumnListSnippet(suggestions, prefix, "DELETED", context.Columns);
        }
    }

    private static void AddColumnListSnippet(
        List<SqlCompletionItem> suggestions,
        string prefix,
        string pseudoTable,
        IReadOnlyList<ColumnMetadata> columns)
    {
        var label = $"OUTPUT {pseudoTable} columns";
        if (columns.Count == 0 || !SqlCompletionHelper.Matches(label, prefix))
            return;

        var insertText = string.Join(", ", columns.Select(c => $"{pseudoTable}.{SqlCompletionHelper.Quote(c.Name)}"));
        suggestions.Add(new SqlCompletionItem(
            label,
            insertText,
            SqlCompletionKind.Snippet,
            $"{pseudoTable} output column list"));
    }

    private static OutputContext? TryGetOutputContext(DatabaseMetadata metadata, string sql, int caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null)
            return null;

        var activeTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var outputIndex = -1;
        for (var i = activeTokens.Count - 1; i >= 0; i--)
        {
            if (string.Equals(activeTokens[i].Text, "OUTPUT", StringComparison.OrdinalIgnoreCase))
            {
                outputIndex = i;
                break;
            }
            if (activeTokens[i].TokenType == TSqlTokenType.Semicolon)
                break;
        }
        if (outputIndex < 0)
            return null;

        var operation = DmlOperation.None;
        for (var i = outputIndex - 1; i >= 0; i--)
        {
            var token = activeTokens[i];
            if (token.TokenType == TSqlTokenType.Insert)
            {
                operation = DmlOperation.Insert;
                break;
            }
            if (token.TokenType == TSqlTokenType.Update)
            {
                operation = DmlOperation.Update;
                break;
            }
            if (token.TokenType == TSqlTokenType.Delete)
            {
                operation = DmlOperation.Delete;
                break;
            }
            if (token.TokenType == TSqlTokenType.Semicolon)
                break;
        }
        if (operation == DmlOperation.None)
            return null;

        var (targetSchema, targetTable) = SqlContextAnalyzer.FindDmlTargetTable(sql, caretPosition);
        if (targetTable == null)
            return null;

        var table = metadata.Tables.FirstOrDefault(t =>
            t.Name.Equals(targetTable, StringComparison.OrdinalIgnoreCase) &&
            (targetSchema == null || t.Schema.Equals(targetSchema, StringComparison.OrdinalIgnoreCase)));

        return table == null
            ? null
            : new OutputContext(operation, table.Columns);
    }

    private sealed record OutputContext(DmlOperation Operation, IReadOnlyList<ColumnMetadata> Columns);

    private enum DmlOperation
    {
        None,
        Insert,
        Update,
        Delete
    }
}
