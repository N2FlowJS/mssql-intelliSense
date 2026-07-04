using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class ProcedureCompletionHelper
{
public static void AddProcedureCompletions(
    List<SqlCompletionItem> suggestions,
    DatabaseMetadata metadata,
    CompletionToken token,
    bool isExecContext = false)
{
    if (token.Qualifiers.Count > 0)
    {
        var lastQualifier = token.Qualifiers[^1];

        var isSchema = SqlCompletionHelper.IsSchemaName(metadata, lastQualifier);

        if (isSchema)
        {
            // Procedures
            foreach (var proc in metadata.Procedures.Where(p =>
                         p.Schema.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase) &&
                         SqlCompletionHelper.Matches(p.Name, token.Prefix)))
            {
                suggestions.Add(CreateProcedureItem(proc, SqlCompletionHelper.Quote(proc.Name), isExecContext, label: proc.Name));
            }

            // Synonyms (might point to procedures)
            foreach (var syn in metadata.Synonyms.Where(s =>
                         s.Schema.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase) &&
                         SqlCompletionHelper.Matches(s.Name, token.Prefix)))
            {
                suggestions.Add(new SqlCompletionItem(
                    syn.Name,
                    SqlCompletionHelper.Quote(syn.Name),
                    SqlCompletionKind.Synonym,
                    $"Synonym {syn.Schema}.{syn.Name} -> {syn.TargetObject}"));
            }

            return;
        }

        var isDatabase = SqlCompletionHelper.IsDatabaseName(metadata, lastQualifier);
        if (isDatabase)
        {
                var schemas = metadata.Tables.Select(t => t.Schema)
                    .Concat(metadata.Views.Select(v => v.Schema))
                    .Concat(metadata.Procedures.Select(p => p.Schema))
                    .Concat(metadata.Functions.Select(f => f.Schema))
                    .Concat(metadata.Synonyms.Select(s => s.Schema))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(schema => SqlCompletionHelper.Matches(schema, token.Prefix));

                foreach (var schema in schemas)
                {
                    suggestions.Add(new SqlCompletionItem(
                        schema,
                        $"{SqlCompletionHelper.Quote(schema)}.",
                        SqlCompletionKind.Schema,
                        $"Schema {schema}"));
                }
                return;
            }

            var isLinkedServer = SqlCompletionHelper.IsLinkedServerName(metadata, lastQualifier);
            if (isLinkedServer)
            {
                foreach (var database in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
                {
                    suggestions.Add(new SqlCompletionItem(
                        database,
                        $"{SqlCompletionHelper.Quote(database)}.",
                        SqlCompletionKind.Database,
                        $"Database {database}"));
                }
                return;
            }

            return;
        }

        // Unqualified procedure context completions
        foreach (var ls in metadata.LinkedServers.Where(ls => SqlCompletionHelper.Matches(ls.Name, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                ls.Name,
                $"{SqlCompletionHelper.Quote(ls.Name)}.",
                SqlCompletionKind.LinkedServer,
                $"Linked Server {ls.Name}"));
        }

        foreach (var db in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                db,
                $"{SqlCompletionHelper.Quote(db)}.",
                SqlCompletionKind.Database,
                $"Database {db}"));
        }

        var allSchemas = metadata.Tables.Select(t => t.Schema)
            .Concat(metadata.Views.Select(v => v.Schema))
            .Concat(metadata.Procedures.Select(p => p.Schema))
            .Concat(metadata.Functions.Select(f => f.Schema))
            .Concat(metadata.Synonyms.Select(s => s.Schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(schema => SqlCompletionHelper.Matches(schema, token.Prefix));

        foreach (var schema in allSchemas)
        {
            suggestions.Add(new SqlCompletionItem(
                schema,
                $"{SqlCompletionHelper.Quote(schema)}.",
                SqlCompletionKind.Schema,
                $"Schema {schema}"));
        }

        // Procedures
        foreach (var proc in metadata.Procedures.Where(p => SqlCompletionHelper.Matches(p.Name, token.Prefix)))
        {
            var insertText = $"{SqlCompletionHelper.Quote(proc.Schema)}.{SqlCompletionHelper.Quote(proc.Name)}";
            suggestions.Add(CreateProcedureItem(proc, insertText, isExecContext));
        }

        // Synonyms
        foreach (var syn in metadata.Synonyms.Where(s => SqlCompletionHelper.Matches(s.Name, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                $"{syn.Schema}.{syn.Name}",
                $"{SqlCompletionHelper.Quote(syn.Schema)}.{SqlCompletionHelper.Quote(syn.Name)}",
                SqlCompletionKind.Synonym,
                $"Synonym {syn.Schema}.{syn.Name}"));
        }
    }

    private static SqlCompletionItem CreateProcedureItem(ProcedureMetadata proc, string insertText, bool isExecContext, string? label = null)
    {
        label ??= $"{proc.Schema}.{proc.Name}";
        if (isExecContext && proc.Parameters.Count > 0)
        {
            var paramList = string.Join(", ", proc.Parameters.Select(p => $"{p.Name} = ?"));
            var bodyInsertText = $"{insertText}({paramList})";
            var caretOffset = insertText.Length + 1; // position after '('
            return new SqlCompletionItem(
                label,
                bodyInsertText,
                SqlCompletionKind.Procedure,
                $"Stored Procedure {proc.Schema}.{proc.Name}",
                CaretOffset: caretOffset);
        }

        return new SqlCompletionItem(
            label,
            insertText,
            SqlCompletionKind.Procedure,
            $"Stored Procedure {proc.Schema}.{proc.Name}");
    }
}
