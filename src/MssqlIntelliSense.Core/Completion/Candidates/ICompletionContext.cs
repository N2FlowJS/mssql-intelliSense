namespace MssqlIntelliSense.Core.Completion.Candidates;

public interface ICompletionContext
{
    int StartIndex { get; }
    int EndIndex { get; }
    string Filter { get; }
    string DefaultDatabase { get; }
    bool IsExecContext { get; }
    bool IsExpressionContext { get; }
    bool IsInsertIntoContext { get; }
    bool IsUpdateSetContext { get; }
    bool IsFromOrJoinContext { get; }
    bool IsOutputIntoContext { get; }
    bool IsTypeContext { get; }
    bool IsOrderByContext { get; }
    bool IsComparisonRhsContext { get; }
}
