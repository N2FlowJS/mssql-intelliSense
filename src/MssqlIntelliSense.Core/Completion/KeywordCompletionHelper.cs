using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlIntelliSense.Core.Completion;

public static class KeywordCompletionHelper
{
    private static readonly HashSet<string> FunctionKeywordSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "COALESCE", "NULLIF", "ISNULL", "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD",
        "FIRST_VALUE", "LAST_VALUE"
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
                var insertText = $"{keyword}()";
                suggestions.Add(new SqlCompletionItem(
                    keyword, insertText, SqlCompletionKind.Keyword, "T-SQL function", keyword.Length + 1));
            }
            else
            {
                suggestions.Add(new SqlCompletionItem(keyword, keyword, SqlCompletionKind.Keyword, "T-SQL keyword"));
            }
        }
    }

    public static bool IsFunctionKeyword(string keyword) =>
        FunctionKeywordSet.Contains(keyword);
}
