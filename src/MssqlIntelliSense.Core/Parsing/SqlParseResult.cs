using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Parsing;

public sealed record SqlParseError(int Number, int Line, int Column, int Offset, string Message);

public sealed record SqlParseResult(TSqlFragment? Fragment, IReadOnlyList<SqlParseError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
