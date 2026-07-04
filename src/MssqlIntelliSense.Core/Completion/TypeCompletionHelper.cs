using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class TypeCompletionHelper
{
    private static readonly string[] BaseTypes = new[]
    {
        "BIGINT", "BINARY", "BIT", "CHAR", "DATE", "DATETIME", "DATETIME2", "DATETIMEOFFSET",
        "DECIMAL", "FLOAT", "IMAGE", "INT", "MONEY", "NCHAR", "NTEXT", "NUMERIC", "NVARCHAR",
        "REAL", "ROWVERSION", "SMALLDATETIME", "SMALLINT", "SMALLMONEY", "SQL_VARIANT",
        "TEXT", "TIME", "TIMESTAMP", "TINYINT", "UNIQUEIDENTIFIER", "VARBINARY", "VARCHAR", "XML"
    };

    public static void AddTypeCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        CompletionToken token)
    {
        if (token.Qualifiers.Count > 0)
        {
            var lastQualifier = token.Qualifiers[^1];

            var adapter = new MetadataAdapter(metadata);
            foreach (var ut in adapter.GetCandidatesInSchema(lastQualifier)
                         .AllCandidates()
                         .OfType<UserTypeCandidate>()
                         .Where(u => SqlCompletionHelper.Matches(u.Name, token.Prefix)))
            {
                suggestions.Add(new SqlCompletionItem(
                    ut.Name,
                    SqlCompletionHelper.Quote(ut.Name),
                    SqlCompletionKind.UserType,
                    $"User Type {ut.Schema}.{ut.Name} (base: {ut.BaseType})"));
            }

            return;
        }

        // Standard base types
        foreach (var bt in BaseTypes.Where(t => SqlCompletionHelper.Matches(t, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                bt,
                bt,
                SqlCompletionKind.BaseType,
                "Base Data Type"));
        }

        // Schema completions (for types)
        var adapter2 = new MetadataAdapter(metadata);
        foreach (var schema in adapter2.Schemas
                     .Where(s => s.Children.AllCandidates().Any(c => c is UserTypeCandidate))
                     .Where(s => SqlCompletionHelper.Matches(s.Name, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                schema.Name,
                $"{SqlCompletionHelper.Quote(schema.Name)}.",
                SqlCompletionKind.Schema,
                $"Schema {schema.Name}"));
        }

        // User types
        foreach (var ut in adapter2.GetAllCandidates(SqlObjectType.UserDefinedType)
                     .OfType<UserTypeCandidate>()
                     .Where(u => SqlCompletionHelper.Matches(u.Name, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                $"{ut.Schema}.{ut.Name}",
                $"{SqlCompletionHelper.Quote(ut.Schema)}.{SqlCompletionHelper.Quote(ut.Name)}",
                SqlCompletionKind.UserType,
                $"User Type {ut.Schema}.{ut.Name} (base: {ut.BaseType})"));
        }
    }
}
