using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class ColumnCompletionHelper
{
    public static void AddVisibleColumnCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        string prefix,
        string? targetSchema = null,
        string? targetTableName = null)
    {
        // ─── INSERT/UPDATE target table: only show that table's columns ─────────
        if (!string.IsNullOrEmpty(targetTableName))
        {
            AddTargetTableColumnsInternal(suggestions, metadata, prefix, targetSchema, targetTableName);
            return;
        }

        var sources = SqlContextAnalyzer.FindSources(sql, metadata);

        // ─── Multi-source: suggest alias shortcuts first ────────────────────────
        // When there are 2+ sources, suggest "alias." items so user can pick a source
        // before having to type the column name. These appear as Table-kind items.
        if (sources.Count >= 2 && string.IsNullOrEmpty(prefix))
        {
            foreach (var src in sources)
            {
                var aliasLabel = $"{src.Alias}.";
                suggestions.Add(new SqlCompletionItem(
                    aliasLabel,
                    $"{SqlCompletionHelper.Quote(src.Alias)}.",
                    SqlCompletionKind.Table,
                    $"Columns of {src.Schema}.{src.Name}"));
            }
        }

        // ─── Detect duplicate column names across sources ──────────────────────
        var duplicateNames = sources.SelectMany(s => s.Columns)
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            foreach (var column in source.Columns.Where(c => SqlCompletionHelper.Matches(c.Name, prefix)))
            {
                // When column name is ambiguous, always qualify with alias
                var qualified = duplicateNames.Contains(column.Name) || sources.Count >= 2;
                suggestions.Add(new SqlCompletionItem(
                    qualified ? $"{source.Alias}.{column.Name}" : column.Name,
                    qualified
                        ? $"{SqlCompletionHelper.Quote(source.Alias)}.{SqlCompletionHelper.Quote(column.Name)}"
                        : SqlCompletionHelper.Quote(column.Name),
                    SqlCompletionKind.Column,
                    $"{source.Schema}.{source.Name}.{column.Name} ({column.DataType})"));
            }
        }
    }

    public static void AddTargetTableColumns(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        string prefix,
        string? targetSchema,
        string? targetTableName)
    {
        AddTargetTableColumnsInternal(suggestions, metadata, prefix, targetSchema, targetTableName);
    }

    private static void AddTargetTableColumnsInternal(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string prefix,
        string? targetSchema,
        string? targetTableName)
    {
        if (string.IsNullOrEmpty(targetTableName))
            return;

        var adapter = new MetadataAdapter(metadata);
        var currentDb = adapter.Server.CurrentDatabase;
        var match = currentDb.Schemas
            .SelectMany(s => s.Children.AllCandidates())
            .FirstOrDefault(c =>
                c.Name.Equals(targetTableName, StringComparison.OrdinalIgnoreCase) &&
                (targetSchema is null || MatchesSchema(c, targetSchema)));

        if (match == null) return;

        var (objSchema, columns) = match switch
        {
            TableCandidate t => (t.Schema, (IEnumerable<ColumnMetadata>)t.Columns),
            ViewCandidate v => (v.Schema, v.Columns),
            _ => (null, null)
        };

        if (columns == null) return;

        foreach (var column in columns.Where(c => SqlCompletionHelper.Matches(c.Name, prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                column.Name,
                SqlCompletionHelper.Quote(column.Name),
                SqlCompletionKind.Column,
                $"{objSchema}.{match.Name}.{column.Name} ({column.DataType})"));
        }
    }

    private static bool MatchesSchema(ICandidate c, string schema)
    {
        return c switch
        {
            TableCandidate t => t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase),
            ViewCandidate v => v.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }
}
