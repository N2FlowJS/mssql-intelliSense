using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public sealed class SqlObjectReviewInfo
{
    public SqlObjectReviewInfo(
        string title,
        string subtitle,
        string details,
        string definition,
        string objectKey,
        string customDescription)
    {
        Title = title;
        Subtitle = subtitle;
        Details = details;
        Definition = definition;
        ObjectKey = objectKey;
        CustomDescription = customDescription;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Details { get; }
    public string Definition { get; }
    public string ObjectKey { get; }
    public string CustomDescription { get; }
}

public static class SqlObjectReviewFormatter
{
    public static bool CanReview(SqlCompletionKind kind) =>
        kind is SqlCompletionKind.Table or
            SqlCompletionKind.View or
            SqlCompletionKind.Procedure or
            SqlCompletionKind.Function or
            SqlCompletionKind.UserType or
            SqlCompletionKind.Synonym;

    public static SqlObjectReviewInfo Build(SqlCompletionItem item, DatabaseMetadata metadata)
    {
        var candidates = ExtractObjectCandidates(item).ToArray();

        switch (item.Kind)
        {
            case SqlCompletionKind.Table:
                var table = metadata.Tables.FirstOrDefault(t => MatchesAny(t.Schema, t.Name, candidates));
                if (table != null)
                {
                    return BuildTableReview(table, metadata);
                }
                break;
            case SqlCompletionKind.View:
                var view = metadata.Views.FirstOrDefault(v => MatchesAny(v.Schema, v.Name, candidates));
                if (view != null)
                {
                    return BuildViewReview(view);
                }
                break;
            case SqlCompletionKind.Procedure:
                var proc = metadata.Procedures.FirstOrDefault(p => MatchesAny(p.Schema, p.Name, candidates));
                if (proc != null)
                {
                    return BuildProcedureReview(proc);
                }
                break;
            case SqlCompletionKind.Function:
                var fn = metadata.Functions.FirstOrDefault(f => MatchesAny(f.Schema, f.Name, candidates));
                if (fn != null)
                {
                    return BuildFunctionReview(fn);
                }
                break;
            case SqlCompletionKind.UserType:
                var userType = metadata.UserTypes.FirstOrDefault(t => MatchesAny(t.Schema, t.Name, candidates));
                if (userType != null)
                {
                    return BuildUserTypeReview(userType);
                }
                break;
            case SqlCompletionKind.Synonym:
                var synonym = metadata.Synonyms.FirstOrDefault(s => MatchesAny(s.Schema, s.Name, candidates));
                if (synonym != null)
                {
                    return BuildSynonymReview(synonym);
                }
                break;
        }

        var fallbackTitle = $"{item.Kind}: {item.Label}";
        var fallbackDetails = $"Kind: {item.Kind}{Environment.NewLine}Label: {item.Label}{Environment.NewLine}Insert: {item.InsertText}";
        var fallbackKey = ObjectDescriptionStore.BuildKey(item.Kind.ToString(), string.Empty, string.Empty, item.Label);
        var fallbackCustom = ObjectDescriptionStore.LoadAll().TryGetValue(fallbackKey, out var value) ? value : string.Empty;
        return new SqlObjectReviewInfo(fallbackTitle, item.Label, fallbackDetails, item.Description, fallbackKey, fallbackCustom);
    }

    private static SqlObjectReviewInfo BuildTableReview(TableMetadata table, DatabaseMetadata metadata)
    {
        var details = new StringBuilder();
        AppendCommon(details, "Table", table.Database, table.Schema, table.Name);
        if (!string.IsNullOrWhiteSpace(table.ExtendedDescription))
        {
            details.AppendLine($"Description: {table.ExtendedDescription}");
        }
        details.AppendLine($"Primary key: {(table.PrimaryKeyColumns.Count == 0 ? "(none)" : string.Join(", ", table.PrimaryKeyColumns))}");
        details.AppendLine();
        details.AppendLine("Columns:");
        foreach (var c in table.Columns.OrderBy(c => c.Ordinal))
        {
            details.AppendLine($"- {c.Name}: {c.DataType} {(c.IsNullable ? "NULL" : "NOT NULL")}");
        }

        var indexes = metadata.Indexes
            .Where(i => i.Schema.Equals(table.Schema, StringComparison.OrdinalIgnoreCase) &&
                        i.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (indexes.Length > 0)
        {
            details.AppendLine();
            details.AppendLine("Indexes:");
            foreach (var i in indexes)
            {
                details.AppendLine($"- {i.Name}: {(i.IsUnique ? "UNIQUE " : "")}{string.Join(", ", i.Columns)}");
            }
        }

        var definition = SqlDefinitionFormatter.FormatTableDefinition(table);
        var customDescription = BuildDefaultCustomDescription(
            "table",
            table.Database,
            table.Schema,
            table.Name,
            details.ToString().TrimEnd(),
            definition);
        return WithDescription("table", table.Database, table.Schema, table.Name,
            $"Table [{table.Schema}].[{table.Name}]", $"{table.Database}.{table.Schema}.{table.Name}", details.ToString().TrimEnd(), definition, customDescription);
    }

    private static SqlObjectReviewInfo BuildViewReview(ViewMetadata view)
    {
        var details = new StringBuilder();
        AppendCommon(details, "View", view.Database, view.Schema, view.Name);
        if (!string.IsNullOrWhiteSpace(view.ExtendedDescription))
        {
            details.AppendLine($"Description: {view.ExtendedDescription}");
        }
        details.AppendLine($"Indexed: {(view.IsIndexed ? "Yes" : "No")}");
        details.AppendLine();
        details.AppendLine("Columns:");
        foreach (var c in view.Columns.OrderBy(c => c.Ordinal))
        {
            details.AppendLine($"- {c.Name}: {c.DataType} {(c.IsNullable ? "NULL" : "NOT NULL")}");
        }

        var definition = SqlDefinitionFormatter.FormatViewDefinition(view);
        var customDescription = BuildDefaultCustomDescription(
            "view",
            view.Database,
            view.Schema,
            view.Name,
            details.ToString().TrimEnd(),
            definition);
        return WithDescription("view", view.Database, view.Schema, view.Name,
            $"View [{view.Schema}].[{view.Name}]", $"{view.Database}.{view.Schema}.{view.Name}", details.ToString().TrimEnd(), definition, customDescription);
    }

    private static SqlObjectReviewInfo BuildProcedureReview(ProcedureMetadata proc)
    {
        var details = new StringBuilder();
        AppendCommon(details, "Stored procedure", proc.Database, proc.Schema, proc.Name);
        details.AppendLine($"Object type: {proc.ObjectType}");
        details.AppendLine();
        AppendParameters(details, proc.Parameters);

        var definition = SqlDefinitionFormatter.FormatProcedureDefinition(proc);
        var customDescription = BuildDefaultCustomDescription(
            "procedure",
            proc.Database,
            proc.Schema,
            proc.Name,
            details.ToString().TrimEnd(),
            definition);
        return WithDescription("procedure", proc.Database, proc.Schema, proc.Name,
            $"Procedure [{proc.Schema}].[{proc.Name}]", $"{proc.Database}.{proc.Schema}.{proc.Name}", details.ToString().TrimEnd(), definition, customDescription);
    }

    private static SqlObjectReviewInfo BuildFunctionReview(FunctionMetadata fn)
    {
        var details = new StringBuilder();
        AppendCommon(details, "Function", fn.Database, fn.Schema, fn.Name);
        details.AppendLine($"Function type: {fn.FunctionType}");
        details.AppendLine($"Return type: {(string.IsNullOrWhiteSpace(fn.ReturnType) ? "(unknown)" : fn.ReturnType)}");
        details.AppendLine();
        AppendParameters(details, fn.Parameters);

        var definition = SqlDefinitionFormatter.FormatFunctionDefinition(fn);
        var customDescription = BuildDefaultCustomDescription(
            "function",
            fn.Database,
            fn.Schema,
            fn.Name,
            details.ToString().TrimEnd(),
            definition);
        return WithDescription("function", fn.Database, fn.Schema, fn.Name,
            $"Function [{fn.Schema}].[{fn.Name}]", $"{fn.Database}.{fn.Schema}.{fn.Name}", details.ToString().TrimEnd(), definition, customDescription);
    }

    private static SqlObjectReviewInfo BuildUserTypeReview(UserTypeMetadata type)
    {
        var details = new StringBuilder();
        AppendCommon(details, "User type", type.Database, type.Schema, type.Name);
        details.AppendLine($"Base type: {type.BaseType}");
        details.AppendLine($"Nullable: {(type.IsNullable ? "Yes" : "No")}");
        details.AppendLine($"Table type: {(type.IsTableType ? "Yes" : "No")}");
        var definition = SqlDefinitionFormatter.FormatUserTypeDefinition(type);
        var customDescription = BuildDefaultCustomDescription(
            "userType",
            type.Database,
            type.Schema,
            type.Name,
            details.ToString().TrimEnd(),
            definition);
        return WithDescription("userType", type.Database, type.Schema, type.Name,
            $"User type [{type.Schema}].[{type.Name}]", $"{type.Database}.{type.Schema}.{type.Name}", details.ToString().TrimEnd(), definition, customDescription);
    }

    private static SqlObjectReviewInfo BuildSynonymReview(SynonymMetadata synonym)
    {
        var details = new StringBuilder();
        AppendCommon(details, "Synonym", synonym.Database, synonym.Schema, synonym.Name);
        details.AppendLine($"Target: {synonym.TargetObject}");
        var definition = SqlDefinitionFormatter.FormatSynonymDefinition(synonym);
        var customDescription = BuildDefaultCustomDescription(
            "synonym",
            synonym.Database,
            synonym.Schema,
            synonym.Name,
            details.ToString().TrimEnd(),
            definition);
        return WithDescription("synonym", synonym.Database, synonym.Schema, synonym.Name,
            $"Synonym [{synonym.Schema}].[{synonym.Name}]", $"{synonym.Database}.{synonym.Schema}.{synonym.Name}", details.ToString().TrimEnd(), definition, customDescription);
    }

    private static SqlObjectReviewInfo WithDescription(
        string kind,
        string database,
        string schema,
        string name,
        string title,
        string subtitle,
        string details,
        string definition,
        string? customDescriptionOverride = null)
    {
        var key = ObjectDescriptionStore.BuildKey(kind, database, schema, name);
        var savedCustomDescription = ObjectDescriptionStore.LoadAll().TryGetValue(key, out var value) ? value : string.Empty;
        var customDescription = string.IsNullOrWhiteSpace(savedCustomDescription) ? customDescriptionOverride ?? string.Empty : savedCustomDescription;
        return new SqlObjectReviewInfo(title, subtitle, details, definition, key, customDescription);
    }

    private static string BuildDefaultCustomDescription(string kind, string database, string schema, string name, string details, string definition)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Object: {kind} {schema}.{name}");
        builder.AppendLine($"Database: {database}");
        builder.AppendLine($"Schema: {schema}");
        builder.AppendLine();
        builder.AppendLine("Summary:");
        builder.AppendLine(details.Replace(Environment.NewLine, Environment.NewLine + "  "));
        builder.AppendLine();
        builder.AppendLine("Definition:");
        builder.AppendLine(definition.Length > 400 ? definition[..400] + "..." : definition);
        return builder.ToString().TrimEnd();
    }

    private static void AppendCommon(StringBuilder sb, string type, string database, string schema, string name)
    {
        sb.AppendLine($"Type: {type}");
        sb.AppendLine($"Database: {EmptyToUnknown(database)}");
        sb.AppendLine($"Schema: {schema}");
        sb.AppendLine($"Name: {name}");
    }

    private static void AppendParameters(StringBuilder sb, IReadOnlyList<FunctionParameterMetadata> parameters)
    {
        sb.AppendLine("Parameters:");
        if (parameters.Count == 0)
        {
            sb.AppendLine("- (none)");
            return;
        }

        foreach (var p in parameters.OrderBy(p => p.Ordinal))
        {
            sb.AppendLine($"- {p.Name}: {p.DataType}{(p.IsOutput ? " OUTPUT" : "")}");
        }
    }

    private static bool MatchesAny(string candidateSchema, string candidateName, IReadOnlyList<(string? Schema, string Name)> candidates) =>
        candidates.Any(candidate => Matches(candidateSchema, candidateName, candidate.Schema, candidate.Name));

    private static bool Matches(string candidateSchema, string candidateName, string? schema, string name)
    {
        if (!candidateName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(schema) ||
               candidateSchema.Equals(schema, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string? Schema, string Name)> ExtractObjectCandidates(SqlCompletionItem item)
    {
        foreach (var value in new[] { item.Label, item.InsertText })
        {
            var candidate = ExtractObjectCandidate(value);
            if (!string.IsNullOrWhiteSpace(candidate.Name))
            {
                yield return candidate;
            }
        }
    }

    private static (string? Schema, string Name) ExtractObjectCandidate(string value)
    {
        var aliasIndex = value.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (aliasIndex >= 0)
        {
            value = value.Substring(0, aliasIndex);
        }

        value = value.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal) && value.Contains("].[", StringComparison.Ordinal))
        {
            value = value.Replace("].[", ".");
        }

        var parts = value.Split('.');
        var name = Unquote(parts[parts.Length - 1]);
        var schema = parts.Length >= 2 ? Unquote(parts[parts.Length - 2]) : null;
        return (schema, name);
    }

    private static string Unquote(string value) =>
        value.Trim().Trim('[', ']').Replace("]]", "]");

    private static string EmptyToUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;
}
