using System;
using System.Linq;
using System.Text;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class SqlDefinitionFormatter
{
    public static string FormatTableDefinition(
        TableMetadata table,
        IEnumerable<ForeignKeyMetadata>? foreignKeys = null,
        IEnumerable<IndexMetadata>? indexes = null)
    {
        var tableForeignKeys = GetTableForeignKeys(table, foreignKeys).ToArray();
        var tableIndexes = GetTableIndexes(table, indexes).ToArray();
        var hasTableConstraints = table.PrimaryKeyColumns.Count > 0 || tableForeignKeys.Length > 0;

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{table.Schema}].[{table.Name}]");
        sb.AppendLine("(");
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            var nullable = col.IsNullable ? "NULL" : "NOT NULL";
            var comma = (i < table.Columns.Count - 1 || hasTableConstraints) ? "," : "";
            sb.AppendLine($"    [{col.Name}] {col.DataType} {nullable}{comma}");
        }
        if (table.PrimaryKeyColumns.Count > 0)
        {
            var pkCols = string.Join(", ", table.PrimaryKeyColumns.Select(c => $"[{c}]"));
            var comma = tableForeignKeys.Length > 0 ? "," : "";
            sb.AppendLine($"    PRIMARY KEY ({pkCols}){comma}");
        }
        for (int i = 0; i < tableForeignKeys.Length; i++)
        {
            var fkGroup = tableForeignKeys[i];
            var first = fkGroup[0];
            var fromCols = string.Join(", ", fkGroup.Select(fk => QuoteIdentifier(fk.FromColumn)));
            var toCols = string.Join(", ", fkGroup.Select(fk => QuoteIdentifier(fk.ToColumn)));
            var comma = i < tableForeignKeys.Length - 1 ? "," : "";
            sb.AppendLine($"    CONSTRAINT {QuoteIdentifier(first.Name)} FOREIGN KEY ({fromCols}) REFERENCES {QuoteIdentifier(first.ToSchema)}.{QuoteIdentifier(first.ToTable)} ({toCols}){comma}");
        }
        sb.Append(")");

        foreach (var index in tableIndexes)
        {
            var unique = index.IsUnique ? "UNIQUE " : "";
            var columns = string.Join(", ", index.Columns.Select(QuoteIdentifier));
            sb.AppendLine();
            sb.Append($"CREATE {unique}INDEX {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(index.Schema)}.{QuoteIdentifier(index.Table)} ({columns});");
        }

        return sb.ToString();
    }

    private static IEnumerable<IReadOnlyList<ForeignKeyMetadata>> GetTableForeignKeys(
        TableMetadata table,
        IEnumerable<ForeignKeyMetadata>? foreignKeys)
    {
        if (foreignKeys == null)
            yield break;

        var groups = foreignKeys
            .Where(fk => MatchesTable(table, fk.Database, fk.FromSchema, fk.FromTable))
            .GroupBy(
                fk => $"{fk.Name}|{fk.FromSchema}|{fk.FromTable}|{fk.ToSchema}|{fk.ToTable}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
            yield return group.OrderBy(fk => fk.Ordinal).ToArray();
    }

    private static IEnumerable<IndexMetadata> GetTableIndexes(
        TableMetadata table,
        IEnumerable<IndexMetadata>? indexes)
    {
        if (indexes == null)
            yield break;

        foreach (var index in indexes
                     .Where(i => MatchesTable(table, i.Database, i.Schema, i.Table))
                     .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsPrimaryKeyIndex(table, index))
                continue;

            yield return index;
        }
    }

    private static bool MatchesTable(TableMetadata table, string database, string schema, string name)
    {
        return (string.IsNullOrWhiteSpace(table.Database) ||
                string.IsNullOrWhiteSpace(database) ||
                table.Database.Equals(database, StringComparison.OrdinalIgnoreCase)) &&
               table.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
               table.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrimaryKeyIndex(TableMetadata table, IndexMetadata index)
    {
        return table.PrimaryKeyColumns.Count > 0 &&
               index.IsUnique &&
               table.PrimaryKeyColumns.SequenceEqual(index.Columns, StringComparer.OrdinalIgnoreCase);
    }

    private static string QuoteIdentifier(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    public static string FormatViewDefinition(ViewMetadata view)
    {
        if (!string.IsNullOrWhiteSpace(view.Definition))
        {
            return view.Definition.Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE VIEW [{view.Schema}].[{view.Name}]");
        if (view.Columns.Count > 0)
        {
            sb.AppendLine("(");
            for (int i = 0; i < view.Columns.Count; i++)
            {
                var col = view.Columns[i];
                var nullable = col.IsNullable ? "NULL" : "NOT NULL";
                var comma = i < view.Columns.Count - 1 ? "," : "";
                sb.AppendLine($"    [{col.Name}] {col.DataType} {nullable}{comma}");
            }
            sb.Append(")");
        }
        return sb.ToString();
    }

    public static string FormatProcedureDefinition(ProcedureMetadata proc)
    {
        if (!string.IsNullOrWhiteSpace(proc.Definition))
        {
            return proc.Definition.Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE PROCEDURE [{proc.Schema}].[{proc.Name}]");
        if (proc.Parameters.Count > 0)
        {
            for (int i = 0; i < proc.Parameters.Count; i++)
            {
                var p = proc.Parameters[i];
                var paramName = p.Name.StartsWith("@", StringComparison.Ordinal) ? p.Name : $"@{p.Name}";
                var outText = p.IsOutput ? " OUTPUT" : "";
                var comma = i < proc.Parameters.Count - 1 ? "," : "";
                sb.AppendLine($"    {paramName} {p.DataType}{outText}{comma}");
            }
        }
        else
        {
            sb.AppendLine("    /* No parameters */");
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatFunctionDefinition(FunctionMetadata fn)
    {
        if (!string.IsNullOrWhiteSpace(fn.Definition))
        {
            return fn.Definition.Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE FUNCTION [{fn.Schema}].[{fn.Name}]");
        sb.AppendLine("(");
        if (fn.Parameters.Count > 0)
        {
            for (int i = 0; i < fn.Parameters.Count; i++)
            {
                var p = fn.Parameters[i];
                var paramName = p.Name.StartsWith("@", StringComparison.Ordinal) ? p.Name : $"@{p.Name}";
                var comma = i < fn.Parameters.Count - 1 ? "," : "";
                sb.AppendLine($"    {paramName} {p.DataType}{comma}");
            }
        }
        sb.AppendLine(")");
        var returnType = string.IsNullOrWhiteSpace(fn.ReturnType)
            ? (fn.FunctionType is "TF" or "IF" ? "TABLE" : "VOID")
            : fn.ReturnType;
        sb.Append($"RETURNS {returnType}");
        return sb.ToString();
    }

    public static string FormatUserTypeDefinition(UserTypeMetadata ut)
    {
        if (ut.IsTableType && ut.Columns.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TYPE [{ut.Schema}].[{ut.Name}] AS TABLE");
            sb.AppendLine("(");
            for (int i = 0; i < ut.Columns.Count; i++)
            {
                var col = ut.Columns[i];
                var nullable = col.IsNullable ? "NULL" : "NOT NULL";
                var comma = i < ut.Columns.Count - 1 ? "," : "";
                sb.AppendLine($"    [{col.Name}] {col.DataType} {nullable}{comma}");
            }
            sb.Append(")");
            return sb.ToString();
        }
        var nullability = ut.IsNullable ? "NULL" : "NOT NULL";
        return $"CREATE TYPE [{ut.Schema}].[{ut.Name}] FROM {ut.BaseType} {nullability}";
    }

    public static string FormatSynonymDefinition(SynonymMetadata syn)
    {
        return $"CREATE SYNONYM [{syn.Schema}].[{syn.Name}] FOR {syn.TargetObject}";
    }
}
