using System;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class SqlCompletionHelper
{
    // 4-tier matching: exact / prefix / word-boundary / substring
    public static bool Matches(string candidate, string prefix) =>
        prefix.Length == 0 ||
        candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        IsWordBoundaryMatch(candidate, prefix) ||
        candidate.Contains(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Composite rank: lower = better.
    /// Exact → prefix → word-boundary priority, then by object kind.
    /// </summary>
    public static int Rank(string candidate, string prefix, SqlCompletionKind kind = SqlCompletionKind.Keyword)
    {
        int prefixScore;
        if (prefix.Length == 0)
        {
            prefixScore = 0;
        }
        else if (candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            prefixScore = 0;  // exact match
        }
        else if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            prefixScore = 1;  // prefix match
        }
        else if (IsWordBoundaryMatch(candidate, prefix))
        {
            prefixScore = 2;  // camelCase / underscore-boundary match
        }
        else
        {
            prefixScore = 3;  // substring / contains (fallback, currently unused)
        }

        return prefixScore * 100 + KindRank(kind);
    }

    /// <summary>Recency bonus for usage-based MRU reordering. Negative = sorts earlier. Only meaningful as tiebreaker within same Rank group.</summary>
    public static int UsageRank(SqlCompletionKind kind, string label, ICandidateUsageRecorder? recorder)
    {
        if (recorder == null) return 0;
        if (!recorder.TryGetLastUsedTime(kind, label, out var lastUsed)) return 0;

        var age = DateTime.UtcNow - lastUsed;
        if (age.TotalMinutes < 1) return -99;
        if (age.TotalMinutes < 5) return -50;
        if (age.TotalHours < 1) return -20;
        if (age.TotalDays < 1) return -10;
        return -5;
    }

    /// <summary>
    /// CamelCase / word-boundary matching, e.g. "fn" → "FirstName", "dbo" → "DisplayBatchOutput".
    /// </summary>
    private static bool IsWordBoundaryMatch(string candidate, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        if (string.IsNullOrEmpty(candidate)) return false;
        return IsWordBoundaryMatchInternal(candidate, filter, 0, 0);
    }

    private static bool IsWordBoundaryMatchInternal(string candidate, string filter, int ci, int fi)
    {
        // Match each character of filter against word-starting characters in candidate
        char fc = char.ToUpperInvariant(filter[fi]);

        for (; ci < candidate.Length; ci++)
        {
            char cc = candidate[ci];

            // Check if current position is a word boundary
            if (ci > 0)
            {
                char prev = candidate[ci - 1];
                bool isWordStart = (!char.IsLetterOrDigit(prev) && prev != '_') ||
                                    (char.IsLower(prev) && char.IsUpper(cc)) ||
                                    (char.IsDigit(prev) && char.IsLetter(cc));
                if (!isWordStart && char.ToUpperInvariant(cc) == fc && fi == 0)
                {
                    // For first filter char, also check non-boundary match as fallback
                }
            }

            if (char.ToUpperInvariant(cc) == fc)
            {
                bool isFirstChar = fi == 0;
                bool isBoundary = ci == 0 ||
                                  !char.IsLetterOrDigit(candidate[ci - 1]) ||
                                  (char.IsLower(candidate[ci - 1]) && char.IsUpper(cc)) ||
                                  (char.IsDigit(candidate[ci - 1]) && char.IsLetter(cc));

                if (isFirstChar || isBoundary)
                {
                    if (fi == filter.Length - 1) return true;
                    if (IsWordBoundaryMatchInternal(candidate, filter, ci + 1, fi + 1))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Extracts the abbreviation (uppercase letters at word boundaries) from a name.
    /// Used for word-boundary matching.
    /// </summary>
    public static string GetAbbreviation(string source)
    {
        if (string.IsNullOrEmpty(source)) return "";
        var sb = new StringBuilder(source.Length / 2);
        char prev = 'a';
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (char.IsLetter(c))
            {
                if (sb.Length == 0)
                    sb.Append(char.ToLowerInvariant(c));
                else if (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev) ||
                         (i < source.Length - 1 && char.IsUpper(prev) && char.IsLower(source[i + 1]))))
                    sb.Append(char.ToLowerInvariant(c));
                else if (!char.IsLetter(prev) && !char.IsDigit(prev))
                    sb.Append(char.ToLowerInvariant(c));
            }
            prev = c;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Priority score per completion kind (lower = appears higher in list).
    /// </summary>
    public static int KindRank(SqlCompletionKind kind) => kind switch
    {
        SqlCompletionKind.Column      => 0,
        SqlCompletionKind.BaseType    => 1,
        SqlCompletionKind.Table       => 2,
        SqlCompletionKind.View        => 3,
        SqlCompletionKind.Synonym     => 4,
        SqlCompletionKind.Procedure   => 5,
        SqlCompletionKind.Function    => 6,
        SqlCompletionKind.UserType    => 7,
        SqlCompletionKind.Schema      => 8,
        SqlCompletionKind.Database    => 9,
        SqlCompletionKind.LinkedServer => 10,
        SqlCompletionKind.Snippet     => 11,
        SqlCompletionKind.Keyword     => 12,
        _ => 12
    };

    public static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    
    public static string Unquote(string identifier) => identifier.Trim().Trim('[', ']', '"');

    public static bool IsSchemaName(DatabaseMetadata metadata, string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return metadata.Tables.Any(t => t.Schema.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
               metadata.Views.Any(v => v.Schema.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
               metadata.Procedures.Any(p => p.Schema.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
               metadata.Functions.Any(f => f.Schema.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
               metadata.Synonyms.Any(s => s.Schema.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDatabaseName(DatabaseMetadata metadata, string name) =>
        !string.IsNullOrEmpty(name) &&
        metadata.Databases.Any(db => db.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool IsLinkedServerName(DatabaseMetadata metadata, string name) =>
        !string.IsNullOrEmpty(name) &&
        metadata.LinkedServers.Any(ls => ls.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool IsIdentifierOrKeyword(TSqlParserToken token)
    {
        if (token.TokenType == TSqlTokenType.Identifier ||
            token.TokenType == TSqlTokenType.QuotedIdentifier)
        {
            return true;
        }
        if (string.IsNullOrEmpty(token.Text)) return false;
        char c = token.Text[0];
        return char.IsLetter(c) || c == '_' || c == '@' || c == '#' || c == '[' || c == '"';
    }

    public static string FormatParameter(string paramName)
    {
        if (string.IsNullOrEmpty(paramName)) return string.Empty;
        return paramName.StartsWith("@", StringComparison.Ordinal) ? paramName : "@" + paramName;
    }
}
