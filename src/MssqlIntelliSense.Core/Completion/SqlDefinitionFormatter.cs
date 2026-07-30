using System;
using System.Linq;
using System.Text;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class SqlDefinitionFormatter
{
    public static string FormatTableDefinition(TableMetadata table)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{table.Schema}].[{table.Name}]");
        sb.AppendLine("(");
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            var nullable = col.IsNullable ? "NULL" : "NOT NULL";
            var comma = (i < table.Columns.Count - 1 || table.PrimaryKeyColumns.Count > 0) ? "," : "";
            sb.AppendLine($"    [{col.Name}] {col.DataType} {nullable}{comma}");
        }
        if (table.PrimaryKeyColumns.Count > 0)
        {
            var pkCols = string.Join(", ", table.PrimaryKeyColumns.Select(c => $"[{c}]"));
            sb.AppendLine($"    PRIMARY KEY ({pkCols})");
        }
        sb.Append(")");
        return sb.ToString();
    }

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
