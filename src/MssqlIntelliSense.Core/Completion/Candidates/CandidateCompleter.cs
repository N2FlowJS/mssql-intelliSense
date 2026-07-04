using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class CompletionContext : ICompletionContext
{
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
    public string Filter { get; init; } = "";
    public string DefaultDatabase { get; init; } = "";
    public bool IsExecContext { get; init; }
    public bool IsExpressionContext { get; init; }
    public bool IsInsertIntoContext { get; init; }
    public bool IsUpdateSetContext { get; init; }
    public bool IsFromOrJoinContext { get; init; }
    public bool IsOutputIntoContext { get; init; }
    public bool IsTypeContext { get; init; }
    public bool IsOrderByContext { get; init; }
    public bool IsComparisonRhsContext { get; init; }
}

public sealed record CompletionFragment(
    string Text,
    int StartIndex,
    int EndIndex,
    int CaretOffset = -1,
    int SelectionStart = -1,
    int SelectionEnd = -1);

public sealed class CandidateCompleter
{
    private readonly DatabaseMetadata _metadata;

    public CandidateCompleter(DatabaseMetadata metadata)
    {
        _metadata = metadata;
    }

    public CompletionFragment CompleteTable(TableCandidate candidate, ICompletionContext context)
    {
        var baseFragment = DefaultFragment(candidate.CompletionText, context);
        if (context.IsInsertIntoContext)
            return GetInsertFragment(candidate, baseFragment, context);
        if (context.IsUpdateSetContext)
            return GetUpdateFragment(candidate, baseFragment);
        if (context.IsOutputIntoContext)
            return GetOutputIntoFragment(candidate, baseFragment);
        if (context.IsFromOrJoinContext)
            return GetAliasedFragment(candidate, baseFragment);
        return baseFragment;
    }

    public CompletionFragment CompleteStoredProcedure(ProcedureCandidate candidate, ICompletionContext context)
    {
        var baseFragment = DefaultFragment(candidate.CompletionText, context);
        if (context.IsExecContext && candidate.Parameters.Count > 0)
            return GetExecFragment(candidate, baseFragment);
        return baseFragment;
    }

    public CompletionFragment CompleteScalarFunction(FunctionCandidate candidate, ICompletionContext context)
    {
        var baseFragment = DefaultFragment(candidate.CompletionText, context);
        if (context.IsExpressionContext && candidate.Parameters.Count > 0)
            return GetFunctionParametersFragment(candidate, baseFragment);
        return baseFragment;
    }

    private CompletionFragment GetInsertFragment(TableCandidate candidate, CompletionFragment baseFragment, ICompletionContext context)
    {
        var cols = string.Join(", ", candidate.Columns.Select(c => SqlCompletionHelper.Quote(c.Name)));
        var vals = string.Join(", ", candidate.Columns.Select(c =>
        {
            var def = _getInsertDefault(c.DataType);
            return def;
        }));
        var text = $"{baseFragment.Text}\n({cols})\nVALUES ({vals})";
        var caretOffset = baseFragment.Text.Length + 1;
        return baseFragment with { Text = text, CaretOffset = caretOffset };
    }

    private CompletionFragment GetUpdateFragment(TableCandidate candidate, CompletionFragment baseFragment)
    {
        var sets = string.Join(", ", candidate.Columns.Select(c => $"{SqlCompletionHelper.Quote(c.Name)} = ?"));
        var text = $"{baseFragment.Text}\nSET {sets}";
        var caretOffset = baseFragment.Text.Length + 5;
        return baseFragment with { Text = text, CaretOffset = caretOffset };
    }

    private CompletionFragment GetOutputIntoFragment(TableCandidate candidate, CompletionFragment baseFragment)
    {
        var cols = string.Join(", ", candidate.Columns.Select(c => SqlCompletionHelper.Quote(c.Name)));
        var text = $"{baseFragment.Text}\n({cols})";
        var caretOffset = baseFragment.Text.Length + 1;
        return baseFragment with { Text = text, CaretOffset = caretOffset };
    }

    private CompletionFragment GetAliasedFragment(TableCandidate candidate, CompletionFragment baseFragment)
    {
        var alias = candidate.Name.Length > 3
            ? candidate.Name[..3]
            : candidate.Name.ToLowerInvariant();
        var text = $"{baseFragment.Text} AS {alias}";
        return baseFragment with { Text = text };
    }

    private CompletionFragment GetExecFragment(ProcedureCandidate candidate, CompletionFragment baseFragment)
    {
        var paramList = string.Join(", ", candidate.Parameters.Select(p => $"{p.Name} = ?"));
        var text = $"{baseFragment.Text}({paramList})";
        var caretOffset = baseFragment.Text.Length + 1;
        return baseFragment with { Text = text, CaretOffset = caretOffset };
    }

    private CompletionFragment GetFunctionParametersFragment(FunctionCandidate candidate, CompletionFragment baseFragment)
    {
        var paramList = string.Join(", ", candidate.Parameters.Select(p => $"{p.Name}"));
        var text = $"{baseFragment.Text}({paramList})";
        var caretOffset = baseFragment.Text.Length + 1;
        return baseFragment with { Text = text, CaretOffset = caretOffset };
    }

    private static CompletionFragment DefaultFragment(string text, ICompletionContext context)
    {
        return new CompletionFragment(text, context.StartIndex, context.EndIndex);
    }

    private static readonly Dictionary<string, string> _defaultValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = "0",
        ["bigint"] = "0",
        ["smallint"] = "0",
        ["tinyint"] = "0",
        ["bit"] = "0",
        ["decimal"] = "0",
        ["numeric"] = "0",
        ["float"] = "0.0",
        ["real"] = "0.0",
        ["money"] = "0.0",
        ["smallmoney"] = "0.0",
        ["char"] = "''",
        ["nchar"] = "''",
        ["varchar"] = "''",
        ["nvarchar"] = "''",
        ["text"] = "''",
        ["ntext"] = "''",
        ["datetime"] = "'1900-01-01'",
        ["date"] = "'1900-01-01'",
        ["uniqueidentifier"] = "NEWID()",
    };

    private static string _getInsertDefault(string dataType)
    {
        var baseType = dataType.Split('(', ' ')[0];
        return _defaultValues.TryGetValue(baseType, out var val) ? val : "NULL";
    }
}
