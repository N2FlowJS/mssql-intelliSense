using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Parsing;

public sealed class TSqlParserService
{
    public SqlParseResult Parse(string sql)
    {
        if (sql == null) throw new ArgumentNullException(nameof(sql));
        using var reader = new StringReader(sql);
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(reader, out var errors);
        return new SqlParseResult(
            fragment,
            errors.Select(error => new SqlParseError(
                error.Number, error.Line, error.Column, error.Offset, error.Message)).ToArray());
    }
}
