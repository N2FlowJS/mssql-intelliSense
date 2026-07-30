using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class FunctionCompletionHelper
{
    private sealed record BuiltInFunctionDef(string Name, string InsertTemplate, string Description, int CaretOffset, int SelectionLength = 0);

    private static readonly IReadOnlyList<BuiltInFunctionDef> BuiltInFunctions = new BuiltInFunctionDef[]
    {
        new("COUNT", "COUNT(*)", "Built-in Aggregate COUNT", 6, 1),
        new("SUM", "SUM(?)", "Built-in Aggregate SUM", 4, 1),
        new("AVG", "AVG(?)", "Built-in Aggregate AVG", 4, 1),
        new("MIN", "MIN(?)", "Built-in Aggregate MIN", 4, 1),
        new("MAX", "MAX(?)", "Built-in Aggregate MAX", 4, 1),
        new("STRING_AGG", "STRING_AGG(?, ',')", "Built-in Aggregate STRING_AGG", 11, 1),
        new("COALESCE", "COALESCE(?, ?)", "Built-in COALESCE function", 9, 1),
        new("ISNULL", "ISNULL(?, ?)", "Built-in ISNULL function", 7, 1),
        new("NULLIF", "NULLIF(?, ?)", "Built-in NULLIF function", 7, 1),
        new("CAST", "CAST(? AS INT)", "Built-in CAST function", 5, 1),
        new("CONVERT", "CONVERT(INT, ?)", "Built-in CONVERT function", 13, 1),
        new("TRY_CAST", "TRY_CAST(? AS INT)", "Built-in TRY_CAST function", 9, 1),
        new("TRY_CONVERT", "TRY_CONVERT(INT, ?)", "Built-in TRY_CONVERT function", 17, 1),
        new("DATEADD", "DATEADD(day, ?, ?)", "Built-in DATEADD function", 13, 1),
        new("DATEDIFF", "DATEDIFF(day, ?, ?)", "Built-in DATEDIFF function", 14, 1),
        new("DATEPART", "DATEPART(day, ?)", "Built-in DATEPART function", 14, 1),
        new("YEAR", "YEAR(?)", "Built-in YEAR function", 5, 1),
        new("MONTH", "MONTH(?)", "Built-in MONTH function", 6, 1),
        new("DAY", "DAY(?)", "Built-in DAY function", 4, 1),
        new("GETDATE", "GETDATE()", "Built-in GETDATE function", 9),
        new("GETUTCDATE", "GETUTCDATE()", "Built-in GETUTCDATE function", 12),
        new("SYSDATETIME", "SYSDATETIME()", "Built-in SYSDATETIME function", 13),
        new("EOMONTH", "EOMONTH(?)", "Built-in EOMONTH function", 8, 1),
        new("UPPER", "UPPER(?)", "Built-in UPPER function", 6, 1),
        new("LOWER", "LOWER(?)", "Built-in LOWER function", 6, 1),
        new("LTRIM", "LTRIM(?)", "Built-in LTRIM function", 6, 1),
        new("RTRIM", "RTRIM(?)", "Built-in RTRIM function", 6, 1),
        new("TRIM", "TRIM(?)", "Built-in TRIM function", 5, 1),
        new("LEN", "LEN(?)", "Built-in LEN function", 4, 1),
        new("SUBSTRING", "SUBSTRING(?, ?, ?)", "Built-in SUBSTRING function", 10, 1),
        new("REPLACE", "REPLACE(?, ?, ?)", "Built-in REPLACE function", 8, 1),
        new("CONCAT", "CONCAT(?, ?)", "Built-in CONCAT function", 7, 1),
        new("ABS", "ABS(?)", "Built-in ABS function", 4, 1),
        new("ROUND", "ROUND(?, ?)", "Built-in ROUND function", 6, 1),
        new("CEILING", "CEILING(?)", "Built-in CEILING function", 8, 1),
        new("FLOOR", "FLOOR(?)", "Built-in FLOOR function", 6, 1),
        new("ROW_NUMBER", "ROW_NUMBER() OVER (ORDER BY ?)", "Built-in Window ROW_NUMBER", 27, 1),
        new("RANK", "RANK() OVER (ORDER BY ?)", "Built-in Window RANK", 21, 1),
        new("DENSE_RANK", "DENSE_RANK() OVER (ORDER BY ?)", "Built-in Window DENSE_RANK", 27, 1),
        new("LAG", "LAG(?) OVER (ORDER BY ?)", "Built-in Window LAG", 4, 1),
        new("LEAD", "LEAD(?) OVER (ORDER BY ?)", "Built-in Window LEAD", 5, 1),
        new("FIRST_VALUE", "FIRST_VALUE(?) OVER (ORDER BY ?)", "Built-in Window FIRST_VALUE", 12, 1),
        new("LAST_VALUE", "LAST_VALUE(?) OVER (ORDER BY ?)", "Built-in Window LAST_VALUE", 11, 1)
    };

    public static void AddScalarFunctionCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        CompletionToken token)
    {
        // 1. Metadata functions (non-table types)
        foreach (var fn in metadata.Functions.Where(f =>
                     f.FunctionType != "TF" &&
                     f.FunctionType != "IF" &&
                     SqlCompletionHelper.Matches(f.Name, token.Prefix)))
        {
            var @params = fn.Parameters;
            string insertText;
            int caretOffset;
            int selectionStart = -1;
            int selectionEnd = -1;
            var quoted = SqlCompletionHelper.Quote(fn.Name);

            if (@params.Count > 0)
            {
                var formattedParams = @params.Select(p => SqlCompletionHelper.FormatParameter(p.Name)).ToArray();
                var paramList = string.Join(", ", formattedParams);
                insertText = $"{quoted}({paramList})";
                caretOffset = quoted.Length + 1;
                selectionStart = caretOffset;
                selectionEnd = selectionStart + formattedParams[0].Length;
            }
            else
            {
                insertText = $"{quoted}()";
                caretOffset = quoted.Length + 1;
            }

            suggestions.Add(new SqlCompletionItem(
                $"{fn.Schema}.{fn.Name}",
                insertText,
                SqlCompletionKind.Function,
                SqlDefinitionFormatter.FormatFunctionDefinition(fn),
                caretOffset,
                selectionStart,
                selectionEnd));
        }

        // 2. Built-in T-SQL functions
        foreach (var fn in BuiltInFunctions)
        {
            if (SqlCompletionHelper.Matches(fn.Name, token.Prefix))
            {
                var selectionStart = fn.SelectionLength > 0 ? fn.CaretOffset : -1;
                var selectionEnd = fn.SelectionLength > 0 ? fn.CaretOffset + fn.SelectionLength : -1;

                suggestions.Add(new SqlCompletionItem(
                    fn.Name,
                    fn.InsertTemplate,
                    SqlCompletionKind.Function,
                    fn.Description,
                    fn.CaretOffset,
                    selectionStart,
                    selectionEnd));
            }
        }
    }
}
