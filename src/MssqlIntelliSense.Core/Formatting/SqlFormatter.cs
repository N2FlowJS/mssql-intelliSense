using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Parsing;

namespace MssqlIntelliSense.Core.Formatting;

public sealed class SqlFormatter
{
    private readonly TSqlParserService _parser;

    public SqlFormatter(TSqlParserService? parser = null) => _parser = parser ?? new TSqlParserService();

    public string Format(string sql)
    {
        var result = _parser.Parse(sql);
        if (!result.IsValid || result.Fragment is null)
        {
            throw new SqlFormattingException(result.Errors);
        }

        var options = new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = true,
            AlignClauseBodies = false,
            NewLineBeforeFromClause = true,
            NewLineBeforeWhereClause = true,
            NewLineBeforeGroupByClause = true,
            NewLineBeforeOrderByClause = true,
            NewLineBeforeJoinClause = true,
            MultilineSelectElementsList = true,
            MultilineViewColumnsList = true
        };
        new Sql160ScriptGenerator(options).GenerateScript(result.Fragment, out var formatted);
        return formatted.TrimEnd();
    }
}

public sealed class SqlFormattingException : Exception
{
    public SqlFormattingException(IReadOnlyList<SqlParseError> errors)
        : base("SQL could not be formatted because it contains syntax errors.") => Errors = errors;

    public IReadOnlyList<SqlParseError> Errors { get; }
}
