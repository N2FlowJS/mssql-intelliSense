using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class FunctionCompletionHelper
{
    public static void AddScalarFunctionCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        CompletionToken token)
    {
        // Scalar functions (FN type) from metadata
        foreach (var fn in metadata.Functions.Where(f =>
                     f.FunctionType == "FN" &&
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
                $"Scalar Function {fn.Schema}.{fn.Name} ({fn.ReturnType})",
                caretOffset,
                selectionStart,
                selectionEnd));
        }
    }
}
