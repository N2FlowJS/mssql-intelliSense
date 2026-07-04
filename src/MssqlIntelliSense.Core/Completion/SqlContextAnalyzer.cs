using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public sealed record CompletionToken(
    int Start,
    IReadOnlyList<string> Qualifiers,
    string? Qualifier,
    string Prefix,
    bool IsInsertIntoContext = false,
    bool IsUpdateSetContext = false,
    bool IsOutputIntoContext = false,
    bool IsFromJoinContext = false,
    bool IsProcedureExecContext = false);
public sealed record VisibleSource(string Schema, string Name, string Alias, IReadOnlyList<ColumnMetadata> Columns);

public static class SqlContextAnalyzer
{
    private static readonly HashSet<string> AliasStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "FULL", "CROSS", "OUTER", "ON",
        "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "SET", "VALUES",
        "MERGE", "USING", "APPLY", "OUTPUT", "INSERTED", "DELETED",
        "PIVOT", "UNPIVOT", "FOR", "OPTION"
    };

    public static void AnalyzeCaretContext(
        string sql,
        int caretPosition,
        out IReadOnlyList<string> qualifiers,
        out string prefix,
        out bool isTableContext,
        out bool isProcedureContext,
        out bool isJoinContext,
        out bool isTypeContext,
        out string? targetTableSchema,
        out string? targetTableName,
        out bool isOrderByContext,
        out bool isComparisonRhsContext,
        out bool isOutputContext,
        out bool isInsertIntoContext,
        out bool isUpdateSetContext,
        out bool isFromOrJoinContext)
    {
        qualifiers = Array.Empty<string>();
        prefix = string.Empty;
        isTableContext = false;
        isProcedureContext = false;
        isJoinContext = false;
        isTypeContext = false;
        targetTableSchema = null;
        targetTableName = null;
        isOrderByContext = false;
        isComparisonRhsContext = false;
        isOutputContext = false;
        isInsertIntoContext = false;
        isUpdateSetContext = false;
        isFromOrJoinContext = false;

        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(reader, out var errors);
        if (tokens == null) return;

        var activeTokens = tokens
            .Where(t => t.TokenType != TSqlTokenType.WhiteSpace &&
                        t.TokenType != TSqlTokenType.SingleLineComment &&
                        t.TokenType != TSqlTokenType.MultilineComment)
            .ToList();

        var relevantTokens = activeTokens.Where(t => t.Offset < caretPosition).ToList();
        if (relevantTokens.Count == 0) return;

        int prefixTokenIndex = -1;
        var quals = new List<string>();
        var lastToken = relevantTokens[^1];

        if (lastToken.Offset + lastToken.Text.Length >= caretPosition &&
            SqlCompletionHelper.IsIdentifierOrKeyword(lastToken))
        {
            prefixTokenIndex = relevantTokens.Count - 1;
            prefix = sql[lastToken.Offset..caretPosition];
            if (lastToken.TokenType == TSqlTokenType.QuotedIdentifier)
            {
                prefix = SqlCompletionHelper.Unquote(prefix);
            }
        }

        int scanIndex = prefixTokenIndex != -1 ? prefixTokenIndex - 1 : relevantTokens.Count - 1;
        while (scanIndex >= 1 && relevantTokens[scanIndex].TokenType == TSqlTokenType.Dot)
        {
            var qualToken = relevantTokens[scanIndex - 1];
            if (SqlCompletionHelper.IsIdentifierOrKeyword(qualToken))
            {
                quals.Insert(0, SqlCompletionHelper.Unquote(qualToken.Text));
                scanIndex -= 2;
            }
            else
            {
                break;
            }
        }
        qualifiers = quals;

        int contextStartTokenIndex = prefixTokenIndex != -1 ? prefixTokenIndex : relevantTokens.Count;
        if (quals.Count > 0)
        {
            contextStartTokenIndex -= (quals.Count * 2);
        }

        if (contextStartTokenIndex > 0)
        {
            var prevToken = relevantTokens[contextStartTokenIndex - 1];
            var prevType = prevToken.TokenType;

            if (prevType == TSqlTokenType.From ||
                prevType == TSqlTokenType.Join ||
                prevType == TSqlTokenType.Update ||
                prevType == TSqlTokenType.Into ||
                prevType == TSqlTokenType.Insert ||
                prevType == TSqlTokenType.Delete)
            {
                isTableContext = true;
                if (prevType == TSqlTokenType.Join)
                {
                    isJoinContext = true;
                }
                if (prevType == TSqlTokenType.From || prevType == TSqlTokenType.Join)
                {
                    isFromOrJoinContext = true;
                }
            }
            else if (prevType == TSqlTokenType.Exec ||
                     prevType == TSqlTokenType.Execute)
            {
                isProcedureContext = true;
            }
            else if (prevType == TSqlTokenType.As ||
                     string.Equals(prevToken.Text, "CAST", StringComparison.OrdinalIgnoreCase))
            {
                isTypeContext = true;
            }
            else if (prevType == TSqlTokenType.Variable && contextStartTokenIndex > 1)
            {
                var prevPrevType = relevantTokens[contextStartTokenIndex - 2].TokenType;
                if (prevPrevType == TSqlTokenType.Declare)
                {
                    isTypeContext = true;
                }
            }
            else if ((prevType == TSqlTokenType.Table || prevType == TSqlTokenType.View) &&
                     contextStartTokenIndex > 1)
            {
                var alterOrDropOrTruncate = relevantTokens[contextStartTokenIndex - 2].TokenType;
                if (alterOrDropOrTruncate == TSqlTokenType.Alter ||
                    alterOrDropOrTruncate == TSqlTokenType.Drop ||
                    alterOrDropOrTruncate == TSqlTokenType.Truncate)
                {
                    isTableContext = true;
                }
            }
            else if ((prevType == TSqlTokenType.Proc || prevType == TSqlTokenType.Procedure) &&
                     contextStartTokenIndex > 1)
            {
                var alterOrDrop = relevantTokens[contextStartTokenIndex - 2].TokenType;
                if (alterOrDrop == TSqlTokenType.Alter || alterOrDrop == TSqlTokenType.Drop)
                {
                    isProcedureContext = true;
                }
            }
            else if (prevType == TSqlTokenType.On &&
                     IsCreateIndexContext(relevantTokens, contextStartTokenIndex - 1))
            {
                isTableContext = true;
            }
            else if (prevType == TSqlTokenType.Join && contextStartTokenIndex > 1)
            {
                isTableContext = true;
                isJoinContext = true;
                isFromOrJoinContext = true;
            }
            else if (string.Equals(prevToken.Text, "MERGE", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(prevToken.Text, "USING", StringComparison.OrdinalIgnoreCase))
            {
                isTableContext = true;
                isFromOrJoinContext = true;
            }
            else if (string.Equals(prevToken.Text, "APPLY", StringComparison.OrdinalIgnoreCase))
            {
                isTableContext = true;
                isFromOrJoinContext = true;
            }
            else if (string.Equals(prevToken.Text, "OUTPUT", StringComparison.OrdinalIgnoreCase))
            {
                isOutputContext = true;
            }
            else if (IsOrderByOrGroupByContext(relevantTokens, contextStartTokenIndex - 1))
            {
                isOrderByContext = true;
            }
            else if (IsComparisonRhsContext(relevantTokens, contextStartTokenIndex - 1))
            {
                isComparisonRhsContext = true;
            }

            // Independent context checks (not else-if): INSERT INTO and UPDATE SET can overlap with table context
            if (IsInsertIntoContext(relevantTokens, contextStartTokenIndex - 1))
            {
                isInsertIntoContext = true;
            }
            if (IsUpdateSetContext(relevantTokens, contextStartTokenIndex - 1))
            {
                isUpdateSetContext = true;
            }

            // Detect target table for INSERT or UPDATE statements
            TryGetInsertOrUpdateTargetTable(relevantTokens, contextStartTokenIndex, out targetTableSchema, out targetTableName);
        }
    }

    private static bool IsInsertIntoContext(IReadOnlyList<TSqlParserToken> tokens, int fromIndex)
    {
        for (int i = fromIndex; i >= 0; i--)
        {
            var t = tokens[i].TokenType;
            if (t == TSqlTokenType.Into)
            {
                // Look for INSERT before INTO
                for (int j = i - 1; j >= 0; j--)
                {
                    if (tokens[j].TokenType == TSqlTokenType.Insert)
                        return true;
                    if (tokens[j].TokenType == TSqlTokenType.Select || tokens[j].TokenType == TSqlTokenType.From ||
                        tokens[j].TokenType == TSqlTokenType.Where || tokens[j].TokenType == TSqlTokenType.Join ||
                        tokens[j].TokenType == TSqlTokenType.Semicolon)
                        break;
                }
            }
            // Stop at clause boundaries
            if (t == TSqlTokenType.Select || t == TSqlTokenType.From ||
                t == TSqlTokenType.Where || t == TSqlTokenType.Join ||
                t == TSqlTokenType.Semicolon)
                break;
        }
        return false;
    }

    private static bool IsUpdateSetContext(IReadOnlyList<TSqlParserToken> tokens, int fromIndex)
    {
        for (int i = fromIndex; i >= 0; i--)
        {
            var t = tokens[i].TokenType;
            if (t == TSqlTokenType.Set)
            {
                // Look for UPDATE before SET
                for (int j = i - 1; j >= 0; j--)
                {
                    if (tokens[j].TokenType == TSqlTokenType.Update)
                        return true;
                    if (tokens[j].TokenType == TSqlTokenType.Select || tokens[j].TokenType == TSqlTokenType.From ||
                        tokens[j].TokenType == TSqlTokenType.Where || tokens[j].TokenType == TSqlTokenType.Join ||
                        tokens[j].TokenType == TSqlTokenType.Semicolon)
                        break;
                }
            }
            // Stop at clause boundaries
            if (t == TSqlTokenType.Select || t == TSqlTokenType.From ||
                t == TSqlTokenType.Where || t == TSqlTokenType.Join ||
                t == TSqlTokenType.Semicolon)
                break;
        }
        return false;
    }

    // ─── New context detectors ────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the caret is in an ORDER BY or GROUP BY clause
    /// (i.e. the nearest non-comma keyword scanning backwards is ORDER or GROUP or BY).
    /// </summary>
    private static bool IsOrderByOrGroupByContext(IReadOnlyList<TSqlParserToken> tokens, int fromIndex)
    {
        for (int i = fromIndex; i >= 0; i--)
        {
            var t = tokens[i].TokenType;
            if (t == TSqlTokenType.Order || t == TSqlTokenType.Group || t == TSqlTokenType.By)
                return true;
            // Stop at clause boundaries
            if (t == TSqlTokenType.Select || t == TSqlTokenType.From ||
                t == TSqlTokenType.Where  || t == TSqlTokenType.Having ||
                t == TSqlTokenType.Join   || t == TSqlTokenType.Semicolon)
                break;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the caret appears after a comparison operator
    /// (=, &lt;, &gt;, &lt;=, &gt;=, &lt;&gt;, !=, LIKE, IN, NOT IN).
    /// </summary>
    private static bool IsComparisonRhsContext(IReadOnlyList<TSqlParserToken> tokens, int fromIndex)
    {
        for (int i = fromIndex; i >= 0; i--)
        {
            var t = tokens[i].TokenType;
            if (t == TSqlTokenType.EqualsSign ||
                t == TSqlTokenType.LessThan   ||
                t == TSqlTokenType.GreaterThan ||
                t == TSqlTokenType.Like)
                return true;
            if (string.Equals(tokens[i].Text, "IN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tokens[i].Text, "NOT", StringComparison.OrdinalIgnoreCase))
                return true;
            // Stop at clause / statement boundaries
            if (t == TSqlTokenType.Select || t == TSqlTokenType.From ||
                t == TSqlTokenType.Where  || t == TSqlTokenType.Join ||
                t == TSqlTokenType.Set    || t == TSqlTokenType.Comma ||
                t == TSqlTokenType.Semicolon)
                break;
        }
        return false;
    }

    /// <summary>
    /// Scans the SELECT list (up to FROM) and returns all column aliases defined there.
    /// Used to suggest alias names in ORDER BY / GROUP BY.
    /// </summary>
    public static IReadOnlyList<string> ExtractSelectAliases(string sql, int caretPosition)
    {
        var aliases = new List<string>();
        try
        {
            using var reader = new StringReader(sql);
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var tokens = parser.GetTokenStream(reader, out _);
            if (tokens == null) return aliases;

            var active = tokens
                .Where(t => t.TokenType != TSqlTokenType.WhiteSpace &&
                            t.TokenType != TSqlTokenType.SingleLineComment &&
                            t.TokenType != TSqlTokenType.MultilineComment)
                .ToList();

            // Find the SELECT keyword of the current statement
            int selectIdx = -1;
            int fromIdx   = -1;
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i].Offset >= caretPosition) break;
                if (active[i].TokenType == TSqlTokenType.Select) { selectIdx = i; fromIdx = -1; }
                if (active[i].TokenType == TSqlTokenType.From  && selectIdx >= 0 && fromIdx < 0) { fromIdx = i; }
            }

            if (selectIdx < 0) return aliases;
            int end = fromIdx >= 0 ? fromIdx : active.Count;

            // Walk the SELECT list looking for "expr AS alias" or implicit last-identifier alias
            for (int i = selectIdx + 1; i < end - 1; i++)
            {
                if (active[i].TokenType == TSqlTokenType.As &&
                    i + 1 < end &&
                    SqlCompletionHelper.IsIdentifierOrKeyword(active[i + 1]))
                {
                    aliases.Add(SqlCompletionHelper.Unquote(active[i + 1].Text));
                    i++; // skip alias token
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ExtractSelectAliases: {ex.Message}");
        }
        return aliases;
    }

    private static bool IsCreateIndexContext(IReadOnlyList<TSqlParserToken> tokens, int onTokenIndex)
    {
        int k = onTokenIndex - 1;
        bool foundIndex = false;
        while (k >= 0)
        {
            var t = tokens[k].TokenType;
            if (t == TSqlTokenType.Index)
            {
                foundIndex = true;
            }
            else if (t == TSqlTokenType.Create)
            {
                return foundIndex;
            }
            else if (t == TSqlTokenType.Select || t == TSqlTokenType.From || t == TSqlTokenType.Where || t == TSqlTokenType.Join || t == TSqlTokenType.Semicolon)
            {
                break;
            }
            k--;
        }
        return false;
    }

    private static (string schema, string name, IReadOnlyList<ColumnMetadata> columns) ResolveObject(
        DatabaseMetadata metadata, IReadOnlyList<string> qualifiers, string name)
    {
        string? schemaQ = qualifiers.Count >= 1 ? qualifiers[^1] : null;
        string? db = qualifiers.Count >= 2 ? qualifiers[^2] : null;
        string? conn = qualifiers.Count >= 3 ? qualifiers[^3] : null;

        static bool MatchDb(string? dbFilter, string db) =>
            dbFilter == null || db.Equals(dbFilter, StringComparison.OrdinalIgnoreCase);
        static bool MatchConn(string? connFilter, string conn) =>
            connFilter == null
                ? string.IsNullOrEmpty(conn)
                : conn.Equals(connFilter, StringComparison.OrdinalIgnoreCase);

        var table = metadata.Tables.FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (schemaQ == null || t.Schema.Equals(schemaQ, StringComparison.OrdinalIgnoreCase)) &&
            MatchDb(db, t.Database) &&
            MatchConn(conn, t.Connection));
        if (table is null && qualifiers.Count == 0)
        {
            var matches = metadata.Tables.Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(t.Connection)).Take(2).ToArray();
            if (matches.Length == 1) table = matches[0];
        }
        if (table != null) return (table.Schema, table.Name, table.Columns);

        var view = metadata.Views.FirstOrDefault(v =>
            v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (schemaQ == null || v.Schema.Equals(schemaQ, StringComparison.OrdinalIgnoreCase)) &&
            MatchDb(db, v.Database) &&
            MatchConn(conn, v.Connection));
        if (view is null && qualifiers.Count == 0)
        {
            var matches = metadata.Views.Where(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(v.Connection)).Take(2).ToArray();
            if (matches.Length == 1) view = matches[0];
        }
        if (view != null) return (view.Schema, view.Name, view.Columns);

        return (qualifiers.Count >= 1 ? qualifiers[^1] : "dbo", name, Array.Empty<ColumnMetadata>());
    }

    public static IReadOnlyList<VisibleSource> FindSources(string sql, DatabaseMetadata metadata)
    {
        var sources = new List<VisibleSource>();

        try
        {
            using var reader = new StringReader(sql);
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var fragment = parser.Parse(reader, out var errors);
            if (fragment != null)
            {
                var visitor = new TableVisitor();
                fragment.Accept(visitor);

                foreach (var node in visitor.TableReferences)
                {
                    var identifiers = node.SchemaObject.Identifiers;
                    if (identifiers.Count == 0) continue;

                    var tableName = identifiers[^1].Value;
                    var qualifiersList = identifiers.Take(identifiers.Count - 1).Select(id => id.Value).ToList();

                    var (foundSchema, foundName, columns) = ResolveObject(metadata, qualifiersList, tableName);
                    if (columns.Count == 0) continue;

                    var alias = node.Alias?.Value ?? foundName;
                    if (AliasStopWords.Contains(alias)) alias = foundName;

                    sources.Add(new VisibleSource(foundSchema, foundName, alias, columns));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FindSources (AST): {ex.Message}");
        }

        // Fallback to token-stream-based extraction
        if (sources.Count == 0)
        {
            try
            {
                using var reader = new StringReader(sql);
                var parser = new TSql160Parser(initialQuotedIdentifiers: true);
                var tokens = parser.GetTokenStream(reader, out var errors);
                if (tokens != null)
                {
                    var activeTokens = tokens
                        .Where(t => t.TokenType != TSqlTokenType.WhiteSpace &&
                                    t.TokenType != TSqlTokenType.SingleLineComment &&
                                    t.TokenType != TSqlTokenType.MultilineComment)
                        .ToList();

                    for (int i = 0; i < activeTokens.Count; i++)
                    {
                        var type = activeTokens[i].TokenType;
                        if (type == TSqlTokenType.From ||
                            type == TSqlTokenType.Join ||
                            type == TSqlTokenType.Update ||
                            type == TSqlTokenType.Into)
                        {
                            int j = i + 1;
                            while (j < activeTokens.Count)
                            {
                                var parts = new List<string>();
                                while (j < activeTokens.Count)
                                {
                                    var token = activeTokens[j];
                                    if (SqlCompletionHelper.IsIdentifierOrKeyword(token))
                                    {
                                        parts.Add(SqlCompletionHelper.Unquote(token.Text));
                                        j++;
                                        if (j < activeTokens.Count && activeTokens[j].TokenType == TSqlTokenType.Dot)
                                        {
                                            j++;
                                            continue;
                                        }
                                    }
                                    break;
                                }

                                if (parts.Count > 0)
                                {
                                    var name = parts[^1];
                                    var qualifiersList = parts.Take(parts.Count - 1).ToList();

                                    var (foundSchema, foundName, columns) = ResolveObject(metadata, qualifiersList, name);
                                    if (columns.Count > 0)
                                    {
                                        string alias = foundName;
                                        if (j < activeTokens.Count)
                                        {
                                            if (activeTokens[j].TokenType == TSqlTokenType.As)
                                            {
                                                j++;
                                                if (j < activeTokens.Count && SqlCompletionHelper.IsIdentifierOrKeyword(activeTokens[j]) && !AliasStopWords.Contains(activeTokens[j].Text))
                                                {
                                                    alias = SqlCompletionHelper.Unquote(activeTokens[j].Text);
                                                    j++;
                                                }
                                            }
                                            else if (SqlCompletionHelper.IsIdentifierOrKeyword(activeTokens[j]) && !AliasStopWords.Contains(activeTokens[j].Text))
                                            {
                                                alias = SqlCompletionHelper.Unquote(activeTokens[j].Text);
                                                j++;
                                            }
                                        }

                                        if (AliasStopWords.Contains(alias)) alias = foundName;
                                        sources.Add(new VisibleSource(foundSchema, foundName, alias, columns));
                                    }
                                }

                                if (j < activeTokens.Count && activeTokens[j].TokenType == TSqlTokenType.Comma)
                                {
                                    j++;
                                    continue;
                                }

                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FindSources (token): {ex.Message}");
            }
        }

        return sources;
    }

    /// <summary>
    /// Finds the target table of the nearest DML statement (INSERT/UPDATE/DELETE/MERGE)
    /// before the caret position. Used to resolve OUTPUT pseudo-tables (INSERTED, DELETED).
    /// </summary>
    public static (string? schema, string? tableName) FindDmlTargetTable(string sql, int caretPosition)
    {
        try
        {
            using var reader = new StringReader(sql);
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var tokens = parser.GetTokenStream(reader, out _);
            if (tokens == null) return (null, null);

            var active = tokens
                .Where(t => t.TokenType != TSqlTokenType.WhiteSpace &&
                            t.TokenType != TSqlTokenType.SingleLineComment &&
                            t.TokenType != TSqlTokenType.MultilineComment)
                .ToList();

            for (int i = 0; i < active.Count && active[i].Offset < caretPosition; i++)
            {
                var type = active[i].TokenType;
                if (type == TSqlTokenType.Insert || type == TSqlTokenType.Update ||
                    type == TSqlTokenType.Delete ||
                    string.Equals(active[i].Text, "MERGE", StringComparison.OrdinalIgnoreCase))
                {
                    int scan = type == TSqlTokenType.Insert ? i + 2 : i + 1;
                    if (type == TSqlTokenType.Insert &&
                        scan < active.Count &&
                        active[scan - 1].TokenType == TSqlTokenType.Into)
                    {
                        scan = i + 2;
                    }

                    // Skip FROM keyword for DELETE FROM / MERGE USING
                    if (scan < active.Count &&
                        string.Equals(active[scan].Text, "FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        scan++;
                    }

                    var parts = new List<string>();
                    while (scan < active.Count && active[scan].Offset < caretPosition)
                    {
                        var t = active[scan];
                        if (t.TokenType == TSqlTokenType.Identifier ||
                            t.TokenType == TSqlTokenType.QuotedIdentifier)
                        {
                            parts.Add(SqlCompletionHelper.Unquote(t.Text));
                            scan++;
                            if (scan < active.Count && active[scan].TokenType == TSqlTokenType.Dot)
                            {
                                scan++;
                                continue;
                            }
                        }
                        break;
                    }

                    if (parts.Count > 0)
                    {
                        var tableName = parts[^1];
                        var schema = parts.Count >= 2 ? parts[^2] : null;
                        return (schema, tableName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FindDmlTargetTable: {ex.Message}");
        }

        return (null, null);
    }

    private sealed class TableVisitor : TSqlFragmentVisitor
    {
        public List<NamedTableReference> TableReferences { get; } = new();

        public override void ExplicitVisit(NamedTableReference node)
        {
            TableReferences.Add(node);
            base.ExplicitVisit(node);
        }
    }

    private static bool TryGetInsertOrUpdateTargetTable(
        IReadOnlyList<TSqlParserToken> relevantTokens,
        int contextStartTokenIndex,
        out string? schema,
        out string? tableName)
    {
        schema = null;
        tableName = null;

        if (contextStartTokenIndex <= 0) return false;

        // Scenario 1: INSERT INTO TableName (col1, col2, prefix
        // We traverse backward to find the first LeftParenthesis '(' not matched by a RightParenthesis
        int openParenthesisIndex = -1;
        int parenDepth = 0;
        for (int i = contextStartTokenIndex - 1; i >= 0; i--)
        {
            var t = relevantTokens[i].TokenType;
            if (t == TSqlTokenType.RightParenthesis)
            {
                parenDepth++;
            }
            else if (t == TSqlTokenType.LeftParenthesis)
            {
                parenDepth--;
                if (parenDepth < 0)
                {
                    openParenthesisIndex = i;
                    break;
                }
            }
            if (t == TSqlTokenType.Select || t == TSqlTokenType.Where || t == TSqlTokenType.Semicolon)
            {
                break;
            }
        }

        if (openParenthesisIndex > 0)
        {
            int scan = openParenthesisIndex - 1;
            var parts = new List<string>();
            while (scan >= 0)
            {
                var token = relevantTokens[scan];
                if (SqlCompletionHelper.IsIdentifierOrKeyword(token))
                {
                    parts.Insert(0, SqlCompletionHelper.Unquote(token.Text));
                    scan--;
                    if (scan >= 0 && relevantTokens[scan].TokenType == TSqlTokenType.Dot)
                    {
                        scan--;
                        continue;
                    }
                }
                break;
            }

            if (parts.Count > 0)
            {
                int checkIdx = scan;
                bool isInsert = false;
                while (checkIdx >= 0)
                {
                    var t = relevantTokens[checkIdx].TokenType;
                    if (t == TSqlTokenType.Into || t == TSqlTokenType.Insert)
                    {
                        isInsert = true;
                        break;
                    }
                    if (SqlCompletionHelper.IsIdentifierOrKeyword(relevantTokens[checkIdx]) || relevantTokens[checkIdx].TokenType == TSqlTokenType.Dot)
                    {
                        checkIdx--;
                        continue;
                    }
                    break;
                }

                if (isInsert)
                {
                    schema = parts.Count >= 2 ? parts[^2] : null;
                    tableName = parts[^1];
                    return true;
                }
            }
        }

        // Scenario 2: UPDATE TableName SET col1 = val1, prefix
        // We traverse backward to find the SET keyword
        int setIndex = -1;
        for (int i = contextStartTokenIndex - 1; i >= 0; i--)
        {
            var t = relevantTokens[i].TokenType;
            if (t == TSqlTokenType.Set)
            {
                setIndex = i;
                break;
            }
            if (t == TSqlTokenType.Select || t == TSqlTokenType.Where || t == TSqlTokenType.From || t == TSqlTokenType.Semicolon)
            {
                break;
            }
        }

        if (setIndex > 0)
        {
            int scan = setIndex - 1;
            var parts = new List<string>();
            while (scan >= 0)
            {
                var token = relevantTokens[scan];
                if (SqlCompletionHelper.IsIdentifierOrKeyword(token))
                {
                    parts.Insert(0, SqlCompletionHelper.Unquote(token.Text));
                    scan--;
                    if (scan >= 0 && relevantTokens[scan].TokenType == TSqlTokenType.Dot)
                    {
                        scan--;
                        continue;
                    }
                }
                break;
            }

            if (parts.Count > 0)
            {
                int checkIdx = scan;
                bool isUpdate = false;
                while (checkIdx >= 0)
                {
                    var t = relevantTokens[checkIdx].TokenType;
                    if (t == TSqlTokenType.Update)
                    {
                        isUpdate = true;
                        break;
                    }
                    if (SqlCompletionHelper.IsIdentifierOrKeyword(relevantTokens[checkIdx]) || relevantTokens[checkIdx].TokenType == TSqlTokenType.Dot)
                    {
                        checkIdx--;
                        continue;
                    }
                    break;
                }

                if (isUpdate)
                {
                    schema = parts.Count >= 2 ? parts[^2] : null;
                    tableName = parts[^1];
                    return true;
                }
            }
        }

        return false;
    }
}
