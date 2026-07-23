using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class TemporaryTableCompletionHelper
{
    public static void AddTemporaryTableCompletions(
        List<SqlCompletionItem> suggestions,
        string sql,
        int caretPosition,
        string prefix)
    {
        if (prefix.Length > 0 && !prefix.StartsWith("#", StringComparison.Ordinal))
            return;

        foreach (var tableName in ExtractTemporaryTables(sql, caretPosition)
                     .Where(t => SqlCompletionHelper.Matches(t, prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                tableName,
                tableName,
                SqlCompletionKind.Table,
                "Temporary table"));
        }
    }

    private static IReadOnlyList<string> ExtractTemporaryTables(string sql, int caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null) return [];

        var activeTokens = tokens
            .Where(t => t.Offset < caretPosition &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < activeTokens.Count; i++)
        {
            var token = activeTokens[i];
            string? tempTable = null;

            if (token.TokenType == TSqlTokenType.Create)
            {
                tempTable = TryReadCreateTableName(activeTokens, i + 1);
            }
            else if (token.TokenType == TSqlTokenType.Select)
            {
                tempTable = TryReadSelectIntoName(activeTokens, i + 1);
            }

            if (tempTable is { Length: > 0 } && seen.Add(tempTable))
            {
                names.Add(tempTable);
            }
        }

        return names;
    }

    private static string? TryReadCreateTableName(IReadOnlyList<TSqlParserToken> tokens, int startIndex)
    {
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.Table && i + 1 < tokens.Count)
            {
                return ReadTemporaryTableName(tokens, i + 1);
            }
            if (IsStatementBoundary(token.TokenType))
                return null;
        }

        return null;
    }

    private static string? TryReadSelectIntoName(IReadOnlyList<TSqlParserToken> tokens, int startIndex)
    {
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.Into && i + 1 < tokens.Count)
            {
                return ReadTemporaryTableName(tokens, i + 1);
            }
            if (token.TokenType == TSqlTokenType.From ||
                token.TokenType == TSqlTokenType.Semicolon ||
                IsStatementBoundary(token.TokenType))
            {
                return null;
            }
        }

        return null;
    }

    private static string? ReadTemporaryTableName(IReadOnlyList<TSqlParserToken> tokens, int startIndex)
    {
        var token = tokens[startIndex];
        var name = SqlCompletionHelper.Unquote(token.Text);
        return name.StartsWith("#", StringComparison.Ordinal) ? name : null;
    }

    private static bool IsStatementBoundary(TSqlTokenType tokenType) =>
        tokenType is TSqlTokenType.Insert
            or TSqlTokenType.Update
            or TSqlTokenType.Delete
            or TSqlTokenType.Merge
            or TSqlTokenType.Exec
            or TSqlTokenType.Execute
            or TSqlTokenType.Alter
            or TSqlTokenType.Drop
            or TSqlTokenType.Truncate;
}
