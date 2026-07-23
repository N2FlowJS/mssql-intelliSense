using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class LocalVariableCompletionHelper
{
    private sealed record DeclaredVariable(
        string Name,
        bool IsTableVariable,
        IReadOnlyList<ColumnMetadata> Columns);

    public static void AddLocalVariableCompletions(
        List<SqlCompletionItem> suggestions,
        string sql,
        int caretPosition,
        string prefix,
        bool includeWhenPrefixEmpty = false)
    {
        if (prefix.Length == 0 && !includeWhenPrefixEmpty)
            return;
        if (prefix.Length > 0 && !prefix.StartsWith("@", StringComparison.Ordinal))
            return;

        foreach (var variable in ExtractDeclaredVariables(sql, caretPosition)
                     .Where(v => !v.IsTableVariable)
                     .Select(v => v.Name)
                     .Where(v => SqlCompletionHelper.Matches(v, prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                variable,
                variable,
                SqlCompletionKind.Variable,
                "Local variable"));
        }
    }

    public static void AddTableVariableCompletions(
        List<SqlCompletionItem> suggestions,
        string sql,
        int caretPosition,
        string prefix)
    {
        if (prefix.Length == 0)
            return;
        if (!prefix.StartsWith("@", StringComparison.Ordinal))
            return;

        foreach (var variable in ExtractDeclaredVariables(sql, caretPosition)
                     .Where(v => v.IsTableVariable)
                     .Select(v => v.Name)
                     .Where(v => SqlCompletionHelper.Matches(v, prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                variable,
                variable,
                SqlCompletionKind.Variable,
                "Table variable"));
        }
    }

    public static IReadOnlyList<VisibleSource> FindTableVariableSources(string sql)
    {
        var tokens = GetActiveTokens(sql, caretPosition: null);
        if (tokens.Count == 0) return [];

        var definitions = ExtractDeclaredVariables(sql, caretPosition: null)
            .Where(v => v.IsTableVariable && v.Columns.Count > 0)
            .ToDictionary(v => v.Name, v => v.Columns, StringComparer.OrdinalIgnoreCase);
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
                tokens[tableTokenIndex].TokenType != TSqlTokenType.Variable)
            {
                continue;
            }

            var tableName = SqlCompletionHelper.FormatParameter(tokens[tableTokenIndex].Text);
            if (!definitions.TryGetValue(tableName, out var columns))
                continue;

            var alias = tableName;
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

            sources.Add(new VisibleSource("@", tableName, alias, columns));
        }

        return sources;
    }

    private static IReadOnlyList<DeclaredVariable> ExtractDeclaredVariables(string sql, int? caretPosition)
    {
        var activeTokens = GetActiveTokens(sql, caretPosition);

        var variables = new List<DeclaredVariable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < activeTokens.Count; i++)
        {
            if (activeTokens[i].TokenType != TSqlTokenType.Declare)
                continue;

            var expectVariable = true;
            var parenDepth = 0;
            for (var j = i + 1; j < activeTokens.Count; j++)
            {
                var token = activeTokens[j];
                if (caretPosition.HasValue && TokenContainsCaret(token, caretPosition.Value))
                    break;
                if (token.TokenType == TSqlTokenType.Semicolon || IsStatementBoundary(token.TokenType))
                    break;

                if (token.TokenType == TSqlTokenType.LeftParenthesis)
                {
                    parenDepth++;
                    continue;
                }
                if (token.TokenType == TSqlTokenType.RightParenthesis)
                {
                    parenDepth = Math.Max(0, parenDepth - 1);
                    continue;
                }
                if (token.TokenType == TSqlTokenType.Comma && parenDepth == 0)
                {
                    expectVariable = true;
                    continue;
                }

                if (expectVariable && token.TokenType == TSqlTokenType.Variable)
                {
                    var variable = SqlCompletionHelper.FormatParameter(token.Text);
                    var columns = TryReadTableVariableColumns(activeTokens, j + 1);
                    var isTableVariable = columns != null;
                    if (seen.Add(variable))
                    {
                        variables.Add(new DeclaredVariable(variable, isTableVariable, columns ?? []));
                    }
                    expectVariable = false;
                }
            }
        }

        return variables;
    }

    private static IReadOnlyList<ColumnMetadata>? TryReadTableVariableColumns(
        IReadOnlyList<TSqlParserToken> tokens,
        int startIndex)
    {
        var parenDepth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.LeftParenthesis)
            {
                parenDepth++;
                continue;
            }
            if (token.TokenType == TSqlTokenType.RightParenthesis)
            {
                parenDepth = Math.Max(0, parenDepth - 1);
                continue;
            }

            if (parenDepth == 0)
            {
                if (token.TokenType == TSqlTokenType.Table)
                    return ReadTableVariableColumns(tokens, i + 1);
                if (token.TokenType == TSqlTokenType.Comma ||
                    token.TokenType == TSqlTokenType.Semicolon ||
                    IsStatementBoundary(token.TokenType))
                    return null;
            }
        }

        return null;
    }

    private static IReadOnlyList<ColumnMetadata> ReadTableVariableColumns(
        IReadOnlyList<TSqlParserToken> tokens,
        int startIndex)
    {
        var openParenIndex = -1;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
            {
                openParenIndex = i;
                break;
            }
            if (tokens[i].TokenType == TSqlTokenType.Semicolon ||
                IsStatementBoundary(tokens[i].TokenType))
            {
                return [];
            }
        }
        if (openParenIndex < 0) return [];

        var columns = new List<ColumnMetadata>();
        var expectColumnName = true;
        var depth = 0;
        for (var i = openParenIndex; i < tokens.Count; i++)
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
            if (depth == 1 && token.TokenType == TSqlTokenType.Comma)
            {
                expectColumnName = true;
                continue;
            }

            if (depth == 1 && expectColumnName && SqlCompletionHelper.IsIdentifierOrKeyword(token))
            {
                if (IsColumnConstraintKeyword(token.Text))
                {
                    expectColumnName = false;
                    continue;
                }

                var columnName = SqlCompletionHelper.Unquote(token.Text);
                var dataType = ReadColumnDataType(tokens, i + 1);
                columns.Add(new ColumnMetadata(columnName, dataType, true, columns.Count + 1));
                expectColumnName = false;
            }
        }

        return columns;
    }

    private static string ReadColumnDataType(IReadOnlyList<TSqlParserToken> tokens, int startIndex)
    {
        var parts = new List<string>();
        var depth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 &&
                (token.TokenType == TSqlTokenType.Comma ||
                 token.TokenType == TSqlTokenType.RightParenthesis ||
                 IsColumnConstraintKeyword(token.Text)))
            {
                break;
            }

            if (token.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
            }
            else if (token.TokenType == TSqlTokenType.RightParenthesis)
            {
                if (depth == 0)
                    break;
                depth--;
            }

            parts.Add(token.Text);
        }

        return parts.Count == 0 ? "sql_variant" : string.Concat(parts);
    }

    private static bool TokenContainsCaret(TSqlParserToken token, int caretPosition) =>
        token.Offset < caretPosition && token.Offset + token.Text.Length >= caretPosition;

    private static IReadOnlyList<TSqlParserToken> GetActiveTokens(string sql, int? caretPosition)
    {
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null) return [];

        return tokens
            .Where(t => (!caretPosition.HasValue || t.Offset < caretPosition.Value) &&
                        t.TokenType != TSqlTokenType.WhiteSpace &&
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

    private static bool IsColumnConstraintKeyword(string text) =>
        text.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("CHECK", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("COLLATE", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("NOT", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("IDENTITY", StringComparison.OrdinalIgnoreCase);

    private static bool IsStatementBoundary(TSqlTokenType tokenType) =>
        tokenType is TSqlTokenType.Select
            or TSqlTokenType.Insert
            or TSqlTokenType.Update
            or TSqlTokenType.Delete
            or TSqlTokenType.Merge
            or TSqlTokenType.Exec
            or TSqlTokenType.Execute
            or TSqlTokenType.Create
            or TSqlTokenType.Alter
            or TSqlTokenType.Drop
            or TSqlTokenType.Truncate;
}
