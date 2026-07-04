using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;
using MssqlIntelliSense.Core.Parsing;

namespace MssqlIntelliSense.Core.Analysis;

public enum SqlWarningSeverity { Info, Warning, Error }

public sealed record SqlWarning(string Code, string Message, int Line, int Column, SqlWarningSeverity Severity);

public sealed class DangerousSqlAnalyzer
{
    private readonly TSqlParserService _parser;
    public DangerousSqlAnalyzer(TSqlParserService? parser = null) => _parser = parser ?? new TSqlParserService();

    public IReadOnlyList<SqlWarning> Analyze(string sql, DatabaseMetadata? metadata = null)
    {
        var parse = _parser.Parse(sql);
        if (!parse.IsValid || parse.Fragment is null)
            return parse.Errors.Select(error => new SqlWarning("PARSE_ERROR", error.Message, error.Line, error.Column, SqlWarningSeverity.Error)).ToArray();
        var visitor = new WarningVisitor(metadata);
        parse.Fragment.Accept(visitor);
        return visitor.Warnings;
    }

    private sealed class WarningVisitor(DatabaseMetadata? metadata) : TSqlFragmentVisitor
    {
        public List<SqlWarning> Warnings { get; } = [];

        public override void ExplicitVisit(DeleteSpecification node)
        {
            if (node.WhereClause is null) Add("DELETE_WITHOUT_WHERE", "DELETE without WHERE can affect every row.", node, SqlWarningSeverity.Error);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            if (node.WhereClause is null) Add("UPDATE_WITHOUT_WHERE", "UPDATE without WHERE can affect every row.", node, SqlWarningSeverity.Error);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStarExpression node)
        {
            Add("SELECT_STAR", "SELECT * is fragile and can read unnecessary columns.", node, SqlWarningSeverity.Warning);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            if (metadata is not null &&
                (IsLikelyImplicitConversion(node.FirstExpression, node.SecondExpression, metadata) ||
                 IsLikelyImplicitConversion(node.SecondExpression, node.FirstExpression, metadata)))
                Add("IMPLICIT_CONVERSION", "Comparison may cause an implicit data type conversion.", node, SqlWarningSeverity.Warning);
            base.ExplicitVisit(node);
        }

        private static bool IsLikelyImplicitConversion(ScalarExpression columnSide, ScalarExpression literalSide, DatabaseMetadata metadata)
        {
            if (columnSide is not ColumnReferenceExpression column || literalSide is not Literal literal) return false;
            var columnName = column.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
            if (columnName is null) return false;
            var types = metadata.Tables.SelectMany(table => table.Columns).Where(item => item.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase)).Select(item => item.DataType);
            return types.Any(type => IsNumeric(type) && literal.LiteralType == LiteralType.String);
        }

        private static bool IsNumeric(string type) => type.Equals("int", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("bigint", StringComparison.OrdinalIgnoreCase) || type.Equals("smallint", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("decimal", StringComparison.OrdinalIgnoreCase) || type.Equals("numeric", StringComparison.OrdinalIgnoreCase);

        private void Add(string code, string message, TSqlFragment node, SqlWarningSeverity severity) =>
            Warnings.Add(new SqlWarning(code, message, node.StartLine, node.StartColumn, severity));
    }
}
