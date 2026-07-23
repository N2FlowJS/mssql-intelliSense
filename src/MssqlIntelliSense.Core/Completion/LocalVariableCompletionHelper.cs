using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class LocalVariableCompletionHelper
{
    private sealed record DeclaredVariable(string Name, bool IsTableVariable);

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

    private static IReadOnlyList<DeclaredVariable> ExtractDeclaredVariables(string sql, int caretPosition)
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
                if (TokenContainsCaret(token, caretPosition))
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
                    var isTableVariable = IsTableVariableDeclaration(activeTokens, j + 1);
                    if (seen.Add(variable))
                    {
                        variables.Add(new DeclaredVariable(variable, isTableVariable));
                    }
                    expectVariable = false;
                }
            }
        }

        return variables;
    }

    private static bool IsTableVariableDeclaration(IReadOnlyList<TSqlParserToken> tokens, int startIndex)
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
                    return true;
                if (token.TokenType == TSqlTokenType.Comma ||
                    token.TokenType == TSqlTokenType.Semicolon ||
                    IsStatementBoundary(token.TokenType))
                    return false;
            }
        }

        return false;
    }

    private static bool TokenContainsCaret(TSqlParserToken token, int caretPosition) =>
        token.Offset < caretPosition && token.Offset + token.Text.Length >= caretPosition;

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
