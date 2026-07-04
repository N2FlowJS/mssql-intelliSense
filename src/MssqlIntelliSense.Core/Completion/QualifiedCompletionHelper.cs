using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class QualifiedCompletionHelper
{
    public static void AddQualifiedCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        CompletionToken token)
    {
        if (token.Qualifiers.Count == 0) return;

        var lastQualifier = token.Qualifiers[^1];

        // ─── 1. Qualifier is a Schema ──────────────────────────────────────────
        var isSchema = SqlCompletionHelper.IsSchemaName(metadata, lastQualifier);

        if (isSchema)
        {
            AddObjectsInSchema(suggestions, metadata, lastQualifier, token.Prefix);
            return;
        }

        // ─── 2. Qualifier is a Database ────────────────────────────────────────
        var isDatabase = SqlCompletionHelper.IsDatabaseName(metadata, lastQualifier);
        if (isDatabase)
        {
            // Suggest schemas in that database
            var schemas = metadata.Tables    .Where(t => t.Database.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase)).Select(t => t.Schema)
                .Concat(metadata.Views     .Where(v => v.Database.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase)).Select(v => v.Schema))
                .Concat(metadata.Procedures.Where(p => p.Database.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase)).Select(p => p.Schema))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(s => SqlCompletionHelper.Matches(s, token.Prefix));

            foreach (var schema in schemas)
            {
                suggestions.Add(new SqlCompletionItem(
                    schema,
                    $"{SqlCompletionHelper.Quote(schema)}.",
                    SqlCompletionKind.Schema,
                    $"Schema in {lastQualifier}"));
            }
            return;
        }

        // ─── 3. Qualifier is a LinkedServer ────────────────────────────────────
        var isLinkedServer = SqlCompletionHelper.IsLinkedServerName(metadata, lastQualifier);
        if (isLinkedServer)
        {
            // Suggest database names available (suggest all known databases as hint)
            foreach (var db in metadata.Databases.Where(d => SqlCompletionHelper.Matches(d, token.Prefix)))
            {
                suggestions.Add(new SqlCompletionItem(
                    db,
                    $"{SqlCompletionHelper.Quote(db)}.",
                    SqlCompletionKind.Database,
                    $"Database on linked server {lastQualifier}"));
            }
            return;
        }

        // ─── 4. Multi-qualifier: db.schema. → show tables ─────────────────────
        if (token.Qualifiers.Count >= 2)
        {
            var dbQ     = token.Qualifiers[^2];
            var schemaQ = token.Qualifiers[^1];

            var dbMatch = metadata.Databases.Any(d => d.Equals(dbQ, StringComparison.OrdinalIgnoreCase));
            if (dbMatch)
            {
                AddObjectsInSchema(suggestions, metadata, schemaQ, token.Prefix,
                    dbFilter: dbQ);
                return;
            }
        }

        // ─── 5. Qualifier is alias / table name → show columns ─────────────────
        var sources = SqlContextAnalyzer.FindSources(sql, metadata);
        VisibleSource? source = null;

        if (token.Qualifiers.Count == 1)
        {
            var q = token.Qualifiers[0];
            // Try alias match first
            source = sources.FirstOrDefault(s =>
                s.Alias.Equals(q, StringComparison.OrdinalIgnoreCase));

            // Fallback: match by table/view name (user typed unqualified name instead of alias)
            if (source == null)
            {
                source = sources.FirstOrDefault(s =>
                    s.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
            }

            // Fallback 2: user typed a table/view name that is in metadata but NOT in FROM/JOIN
            // (useful in WHERE clause when user writes [tableName].[col] without the table in FROM)
            if (source == null)
            {
                var matchingTable = metadata.Tables.FirstOrDefault(t =>
                    t.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
                if (matchingTable != null)
                {
                    source = new VisibleSource(matchingTable.Schema, matchingTable.Name, q, matchingTable.Columns);
                }
                else
                {
                    var matchingView = metadata.Views.FirstOrDefault(v =>
                        v.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
                    if (matchingView != null)
                    {
                        source = new VisibleSource(matchingView.Schema, matchingView.Name, q, matchingView.Columns);
                    }
                }
            }
        }
        else if (token.Qualifiers.Count >= 2)
        {
            var schemaQ = token.Qualifiers[^2];
            var nameQ   = token.Qualifiers[^1];

            // Try sources first
            source = sources.FirstOrDefault(s =>
                s.Schema.Equals(schemaQ, StringComparison.OrdinalIgnoreCase) &&
                s.Name  .Equals(nameQ,   StringComparison.OrdinalIgnoreCase));

            // Fallback: look directly in metadata
            if (source == null)
            {
                var matchingTable = metadata.Tables.FirstOrDefault(t =>
                    t.Schema.Equals(schemaQ, StringComparison.OrdinalIgnoreCase) &&
                    t.Name  .Equals(nameQ,   StringComparison.OrdinalIgnoreCase));
                if (matchingTable != null)
                {
                    source = new VisibleSource(matchingTable.Schema, matchingTable.Name, nameQ, matchingTable.Columns);
                }
                else
                {
                    var matchingView = metadata.Views.FirstOrDefault(v =>
                        v.Schema.Equals(schemaQ, StringComparison.OrdinalIgnoreCase) &&
                        v.Name  .Equals(nameQ,   StringComparison.OrdinalIgnoreCase));
                    if (matchingView != null)
                    {
                        source = new VisibleSource(matchingView.Schema, matchingView.Name, nameQ, matchingView.Columns);
                    }
                }
            }
        }

        // ─── 6. Qualifier is INSERTED/DELETED pseudo-table → resolve to DML target ──
        if (source == null && token.Qualifiers.Count == 1)
        {
            var q = token.Qualifiers[0];
            if (string.Equals(q, "INSERTED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(q, "DELETED", StringComparison.OrdinalIgnoreCase))
            {
                var (targetSchema, targetTable) = SqlContextAnalyzer.FindDmlTargetTable(sql, token.Start);
                if (targetTable != null)
                {
                    var matchingTable = metadata.Tables.FirstOrDefault(t =>
                        t.Name.Equals(targetTable, StringComparison.OrdinalIgnoreCase) &&
                        (targetSchema == null || t.Schema.Equals(targetSchema, StringComparison.OrdinalIgnoreCase)));
                    if (matchingTable != null)
                    {
                        source = new VisibleSource(matchingTable.Schema, matchingTable.Name, q, matchingTable.Columns);
                    }
                }
            }
        }

        if (source == null) return;

        foreach (var column in source.Columns.Where(c => SqlCompletionHelper.Matches(c.Name, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                column.Name,
                SqlCompletionHelper.Quote(column.Name),
                SqlCompletionKind.Column,
                $"{source.Schema}.{source.Name}.{column.Name} ({column.DataType}{(column.IsNullable ? ", nullable" : string.Empty)})"));
        }
    }

    // ─── Private helpers ───────────────────────────────────────────────────────

    private static void AddObjectsInSchema(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string schema,
        string prefix,
        string? dbFilter = null)
    {
        var adapter = new MetadataAdapter(metadata);
        foreach (var candidate in adapter.GetCandidatesInSchema(schema))
        {
            if (!SqlCompletionHelper.Matches(candidate.Name, prefix)) continue;

            if (!string.IsNullOrEmpty(dbFilter) && candidate is IDbObject dbObj &&
                !dbObj.DatabaseName.Equals(dbFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            switch (candidate)
            {
                case TableCandidate t:
                    suggestions.Add(new SqlCompletionItem(
                        t.Name, SqlCompletionHelper.Quote(t.Name), SqlCompletionKind.Table,
                        $"Table {t.Schema}.{t.Name}"));
                    break;
                case ViewCandidate v:
                    suggestions.Add(new SqlCompletionItem(
                        v.Name, SqlCompletionHelper.Quote(v.Name), SqlCompletionKind.View,
                        $"View {v.Schema}.{v.Name}"));
                    break;
                case ProcedureCandidate p:
                    suggestions.Add(new SqlCompletionItem(
                        p.Name, SqlCompletionHelper.Quote(p.Name), SqlCompletionKind.Procedure,
                        $"Stored Procedure {p.Schema}.{p.Name}"));
                    break;
                case FunctionCandidate fn:
                    suggestions.Add(new SqlCompletionItem(
                        fn.Name,
                        fn.FunctionType is "TF" or "IF"
                            ? $"{SqlCompletionHelper.Quote(fn.Name)}()"
                            : SqlCompletionHelper.Quote(fn.Name),
                        SqlCompletionKind.Function,
                        $"Function {fn.Schema}.{fn.Name}"));
                    break;
                case SynonymCandidate syn:
                    suggestions.Add(new SqlCompletionItem(
                        syn.Name, SqlCompletionHelper.Quote(syn.Name), SqlCompletionKind.Synonym,
                        $"Synonym {syn.Schema}.{syn.Name} -> {syn.TargetObject}"));
                    break;
            }
        }
    }
}
