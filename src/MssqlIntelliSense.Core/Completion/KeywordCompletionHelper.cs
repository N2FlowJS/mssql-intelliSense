using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class KeywordCompletionHelper
{
    private static readonly HashSet<string> FunctionKeywordSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "COALESCE", "NULLIF", "ISNULL", "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT",
        "DATEADD", "DATEDIFF", "DATEPART", "YEAR", "MONTH", "DAY", "GETDATE", "SYSDATETIME",
        "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "LEN", "LEFT", "RIGHT", "SUBSTRING", "REPLACE", "CONCAT",
        "ABS", "ROUND", "CEILING", "FLOOR", "POWER", "SQRT", "RAND",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD",
        "FIRST_VALUE", "LAST_VALUE"
    };

    private static readonly IReadOnlyDictionary<string, string> FunctionKeywordTemplates =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["COUNT"] = "COUNT(*)",
            ["SUM"] = "SUM(?)",
            ["AVG"] = "AVG(?)",
            ["MIN"] = "MIN(?)",
            ["MAX"] = "MAX(?)",
            ["COALESCE"] = "COALESCE(?, ?)",
            ["ISNULL"] = "ISNULL(?, ?)",
            ["NULLIF"] = "NULLIF(?, ?)",
            ["CAST"] = "CAST(? AS INT)",
            ["TRY_CAST"] = "TRY_CAST(? AS INT)",
            ["CONVERT"] = "CONVERT(INT, ?)",
            ["TRY_CONVERT"] = "TRY_CONVERT(INT, ?)",
            ["DATEADD"] = "DATEADD(day, ?, ?)",
            ["DATEDIFF"] = "DATEDIFF(day, ?, ?)",
            ["DATEPART"] = "DATEPART(day, ?)",
            ["YEAR"] = "YEAR(?)",
            ["MONTH"] = "MONTH(?)",
            ["DAY"] = "DAY(?)",
            ["GETDATE"] = "GETDATE()",
            ["SYSDATETIME"] = "SYSDATETIME()",
            ["UPPER"] = "UPPER(?)",
            ["LOWER"] = "LOWER(?)",
            ["LTRIM"] = "LTRIM(?)",
            ["RTRIM"] = "RTRIM(?)",
            ["TRIM"] = "TRIM(?)",
            ["LEN"] = "LEN(?)",
            ["LEFT"] = "LEFT(?, ?)",
            ["RIGHT"] = "RIGHT(?, ?)",
            ["SUBSTRING"] = "SUBSTRING(?, ?, ?)",
            ["REPLACE"] = "REPLACE(?, ?, ?)",
            ["CONCAT"] = "CONCAT(?, ?)",
            ["ABS"] = "ABS(?)",
            ["ROUND"] = "ROUND(?, ?)",
            ["CEILING"] = "CEILING(?)",
            ["FLOOR"] = "FLOOR(?)",
            ["POWER"] = "POWER(?, ?)",
            ["SQRT"] = "SQRT(?)",
            ["RAND"] = "RAND()",
            ["ROW_NUMBER"] = "ROW_NUMBER() OVER (ORDER BY ?)",
            ["RANK"] = "RANK() OVER (ORDER BY ?)",
            ["DENSE_RANK"] = "DENSE_RANK() OVER (ORDER BY ?)",
            ["LAG"] = "LAG(?) OVER (ORDER BY ?)",
            ["LEAD"] = "LEAD(?) OVER (ORDER BY ?)",
            ["FIRST_VALUE"] = "FIRST_VALUE(?) OVER (ORDER BY ?)",
            ["LAST_VALUE"] = "LAST_VALUE(?) OVER (ORDER BY ?)",
            ["NTILE"] = "NTILE(?) OVER (ORDER BY ?)"
        };

    private static readonly string[] CustomKeywords =
    [
        "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "CROSS JOIN",
        "GROUP BY", "ORDER BY", "INSERT INTO", "DELETE FROM", "UNION ALL",
        "IS NULL", "IS NOT NULL", "PARTITION BY",
        "INT", "BIGINT", "SMALLINT", "TINYINT", "BIT", "DECIMAL", "NUMERIC", "MONEY", "FLOAT",
        "REAL", "DATE", "DATETIME", "DATETIME2", "SMALLDATETIME", "CHAR", "VARCHAR", "NCHAR",
        "NVARCHAR", "UNIQUEIDENTIFIER", "XML",
        "CROSS APPLY", "OUTER APPLY",
        "THROW", "TRY", "CATCH",
        "RECOMPILE", "MAXDOP", "OFFSET", "FETCH", "OPTION"
    ];

    private static readonly string[] Keywords = InitializeKeywords();

    private static string[] InitializeKeywords()
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in Enum.GetNames(typeof(TSqlTokenType)))
        {
            if (name == "None" || name == "EndOfFile" || name == "Identifier" || name == "QuotedIdentifier" ||
                name == "WhiteSpace" || name == "SingleLineComment" || name == "MultilineComment" ||
                name == "Integer" || name == "Numeric" || name == "Real" || name == "HexLiteral" || name == "Money" ||
                name == "Variable" || name == "Go" || name.Contains("Equals") || name.Contains("Sign") ||
                name.Contains("Parenthesis") || name.Contains("Curly") || name.Contains("Comment") ||
                name == "Star" || name == "Plus" || name == "Comma" || name == "Minus" || name == "Dot" ||
                name == "Divide" || name == "Colon" || name == "DoubleColon" || name == "Semicolon" ||
                name == "LessThan" || name == "GreaterThan" || name == "Circumflex" || name == "VerticalLine" ||
                name == "Tilde" || name == "LeftShift" || name == "RightShift" || name == "Concat" ||
                name == "OdbcInitiator" || name == "ProcNameSemicolon" || name == "Bang" || name == "Ampersand")
            {
                continue;
            }

            if (!parser.ValidateIdentifier(name))
            {
                list.Add(name.ToUpperInvariant());
            }
        }

        foreach (var custom in CustomKeywords)
        {
            list.Add(custom.ToUpperInvariant());
        }
        foreach (var fn in FunctionKeywordSet)
        {
            list.Add(fn.ToUpperInvariant());
        }

        return list.OrderBy(k => k).ToArray();
    }

    public static void AddKeywordCompletions(List<SqlCompletionItem> suggestions, string prefix, bool isExpressionContext = false)
    {
        foreach (var keyword in Keywords.Where(keyword => SqlCompletionHelper.Matches(keyword, prefix)))
        {
            if (isExpressionContext && FunctionKeywordSet.Contains(keyword))
            {
                suggestions.Add(CreateFunctionKeywordItem(keyword));
            }
            else
            {
                suggestions.Add(new SqlCompletionItem(keyword, keyword, SqlCompletionKind.Keyword, "T-SQL keyword"));
            }
        }
    }

    private static SqlCompletionItem CreateFunctionKeywordItem(string keyword)
    {
        if (!FunctionKeywordTemplates.TryGetValue(keyword, out var insertText))
        {
            insertText = $"{keyword}()";
            return new SqlCompletionItem(
                keyword,
                insertText,
                SqlCompletionKind.Keyword,
                "T-SQL function",
                keyword.Length + 1);
        }

        var selectionStart = insertText.IndexOf("?", StringComparison.Ordinal);
        var selectionEnd = selectionStart >= 0 ? selectionStart + 1 : -1;
        return new SqlCompletionItem(
            keyword,
            insertText,
            SqlCompletionKind.Keyword,
            "T-SQL function",
            selectionStart,
            selectionStart,
            selectionEnd);
    }

    public static bool IsFunctionKeyword(string keyword) =>
        FunctionKeywordSet.Contains(keyword);
}
