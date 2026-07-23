using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class JoinConditionCompletionHelper
{
    public static void AddJoinConditionCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        int caretPosition,
        string prefix)
    {
        if (!IsImmediateJoinOnContext(sql, caretPosition)) return;

        var sources = SqlContextAnalyzer.FindSources(sql, metadata);
        if (sources.Count < 2) return;

        var sourcePairs = sources
            .SelectMany((left, leftIndex) => sources
                .Skip(leftIndex + 1)
                .Select(right => (left, right)));

        foreach (var (left, right) in sourcePairs)
        {
            foreach (var item in CreateConditionItems(metadata, left, right, prefix))
            {
                suggestions.Add(item);
            }
        }
    }

    private static IEnumerable<SqlCompletionItem> CreateConditionItems(
        DatabaseMetadata metadata,
        VisibleSource left,
        VisibleSource right,
        string prefix)
    {
        var fkGroups = metadata.ForeignKeys.GroupBy(fk => fk.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in fkGroups)
        {
            var columns = group.OrderBy(fk => fk.Ordinal).ToList();
            if (columns.Count == 0) continue;

            var first = columns[0];
            if (MatchesTable(first.FromSchema, first.FromTable, left) &&
                MatchesTable(first.ToSchema, first.ToTable, right))
            {
                var label = BuildCondition(columns, left.Alias, right.Alias, quoted: false);
                if (SqlCompletionHelper.Matches(label, prefix))
                {
                    yield return new SqlCompletionItem(
                        label,
                        BuildCondition(columns, left.Alias, right.Alias, quoted: true),
                        SqlCompletionKind.Column,
                        $"JOIN condition via {first.Name}");
                }
            }
            else if (MatchesTable(first.FromSchema, first.FromTable, right) &&
                     MatchesTable(first.ToSchema, first.ToTable, left))
            {
                var label = BuildCondition(columns, right.Alias, left.Alias, quoted: false);
                if (SqlCompletionHelper.Matches(label, prefix))
                {
                    yield return new SqlCompletionItem(
                        label,
                        BuildCondition(columns, right.Alias, left.Alias, quoted: true),
                        SqlCompletionKind.Column,
                        $"JOIN condition via {first.Name}");
                }
            }
        }
    }

    private static bool IsImmediateJoinOnContext(string sql, int caretPosition)
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

        if (previousTokenIndex < 0 || relevantTokens[previousTokenIndex].TokenType != TSqlTokenType.On)
            return false;

        for (int i = previousTokenIndex - 1; i >= 0; i--)
        {
            var tokenType = relevantTokens[i].TokenType;
            if (tokenType == TSqlTokenType.Join) return true;
            if (tokenType == TSqlTokenType.Select ||
                tokenType == TSqlTokenType.Where ||
                tokenType == TSqlTokenType.Group ||
                tokenType == TSqlTokenType.Order ||
                tokenType == TSqlTokenType.Semicolon)
            {
                return false;
            }
        }

        return false;
    }

    private static bool MatchesTable(string schema, string tableName, VisibleSource source) =>
        source.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
        source.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase);

    private static string BuildCondition(
        IReadOnlyList<ForeignKeyMetadata> columns,
        string fromAlias,
        string toAlias,
        bool quoted)
    {
        return string.Join(" AND ", columns.Select(fk =>
            $"{FormatName(fromAlias, quoted)}.{FormatName(fk.FromColumn, quoted)} = {FormatName(toAlias, quoted)}.{FormatName(fk.ToColumn, quoted)}"));
    }

    private static string FormatName(string name, bool quoted) =>
        quoted ? SqlCompletionHelper.Quote(name) : name;
}
