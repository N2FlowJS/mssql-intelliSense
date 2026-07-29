using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class TypeCompletionHelper
{
    private sealed record BaseTypeTemplate(string Label, string InsertText, string Description, int CaretOffset = -1, int SelectionLength = 0);

    private static readonly BaseTypeTemplate[] BaseTypes = new BaseTypeTemplate[]
    {
        new("BIGINT", "BIGINT", "Base Data Type"),
        new("BINARY", "BINARY", "Base Data Type"),
        new("BIT", "BIT", "Base Data Type"),
        new("CHAR", "CHAR", "Base Data Type"),
        new("DATE", "DATE", "Base Data Type"),
        new("DATETIME", "DATETIME", "Base Data Type"),
        new("DATETIME2", "DATETIME2", "Base Data Type"),
        new("DATETIMEOFFSET", "DATETIMEOFFSET", "Base Data Type"),
        new("DECIMAL", "DECIMAL", "Base Data Type"),
        new("FLOAT", "FLOAT", "Base Data Type"),
        new("IMAGE", "IMAGE", "Base Data Type"),
        new("INT", "INT", "Base Data Type"),
        new("MONEY", "MONEY", "Base Data Type"),
        new("NCHAR", "NCHAR", "Base Data Type"),
        new("NTEXT", "NTEXT", "Base Data Type"),
        new("NUMERIC", "NUMERIC", "Base Data Type"),
        new("NVARCHAR", "NVARCHAR", "Base Data Type"),
        new("NVARCHAR(MAX)", "NVARCHAR(MAX)", "Base Data Type"),
        new("NVARCHAR(50)", "NVARCHAR(50)", "Base Data Type"),
        new("NVARCHAR(255)", "NVARCHAR(255)", "Base Data Type"),
        new("REAL", "REAL", "Base Data Type"),
        new("ROWVERSION", "ROWVERSION", "Base Data Type"),
        new("SMALLDATETIME", "SMALLDATETIME", "Base Data Type"),
        new("SMALLINT", "SMALLINT", "Base Data Type"),
        new("SMALLMONEY", "SMALLMONEY", "Base Data Type"),
        new("SQL_VARIANT", "SQL_VARIANT", "Base Data Type"),
        new("TABLE", "TABLE (?)", "Table Type Definition", 7, 1),
        new("TEXT", "TEXT", "Base Data Type"),
        new("TIME", "TIME", "Base Data Type"),
        new("TIMESTAMP", "TIMESTAMP", "Base Data Type"),
        new("TINYINT", "TINYINT", "Base Data Type"),
        new("UNIQUEIDENTIFIER", "UNIQUEIDENTIFIER", "Base Data Type"),
        new("VARBINARY", "VARBINARY", "Base Data Type"),
        new("VARBINARY(MAX)", "VARBINARY(MAX)", "Base Data Type"),
        new("VARCHAR", "VARCHAR", "Base Data Type"),
        new("VARCHAR(MAX)", "VARCHAR(MAX)", "Base Data Type"),
        new("VARCHAR(50)", "VARCHAR(50)", "Base Data Type"),
        new("VARCHAR(255)", "VARCHAR(255)", "Base Data Type"),
        new("XML", "XML", "Base Data Type")
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
                    SqlDefinitionFormatter.FormatUserTypeDefinition(ut.Source)));
            }

            return;
        }

        // Standard base types
        foreach (var bt in BaseTypes.Where(t => SqlCompletionHelper.Matches(t.Label, token.Prefix)))
        {
            var selectionStart = bt.SelectionLength > 0 ? bt.CaretOffset : -1;
            var selectionEnd = bt.SelectionLength > 0 ? bt.CaretOffset + bt.SelectionLength : -1;
            suggestions.Add(new SqlCompletionItem(
                bt.Label,
                bt.InsertText,
                SqlCompletionKind.BaseType,
                bt.Description,
                bt.CaretOffset,
                selectionStart,
                selectionEnd));
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
                SqlDefinitionFormatter.FormatUserTypeDefinition(ut.Source)));
        }
    }
}
