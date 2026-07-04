using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Completion.Snippets;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public sealed class SqlCompletionProvider
{
    private MetadataAdapter? _adapter;
    private ICandidateUsageRecorder? _usageRecorder;
    private IReadOnlyList<Snippet>? _snippets;
    private bool _snippetsLoaded;

    public ICandidateUsageRecorder? UsageRecorder
    {
        get => _usageRecorder;
        set => _usageRecorder = value;
    }

    /// <summary>Source of snippet definitions. Defaults to embedded snippets if not set.</summary>
    public string? SnippetDirectory { get; set; }

    /// <summary>Provides the current list of snippets (from directory or defaults).</summary>
    public IReadOnlyList<Snippet> GetSnippets()
    {
        if (!_snippetsLoaded)
        {
            if (SnippetDirectory != null)
                _snippets = SnippetLoader.LoadFromDirectory(SnippetDirectory).ToList();
            _snippets ??= SnippetDefaults.GetDefaultSnippets();
            _snippetsLoaded = true;
        }
        return _snippets ?? [];
    }

    /// <summary>Record that the user selected the given completion item (for MRU prioritization).</summary>
    public void RecordUsage(SqlCompletionItem item)
    {
        _usageRecorder?.CandidateUsed(item.Kind, item.Label);
    }

    private MetadataAdapter GetAdapter(DatabaseMetadata metadata) =>
        _adapter ??= new MetadataAdapter(metadata);

    public IReadOnlyList<SqlCompletionItem> GetCompletions(
        string sql,
        int caretPosition,
        DatabaseMetadata? metadata = null)
    {
        if (sql == null) throw new ArgumentNullException(nameof(sql));
        if (caretPosition < 0 || caretPosition > sql.Length)
            throw new ArgumentOutOfRangeException(nameof(caretPosition));

        metadata ??= DatabaseMetadata.Empty;
        _adapter = null;
        var suggestions = new List<SqlCompletionItem>();

        SqlContextAnalyzer.AnalyzeCaretContext(
            sql,
            caretPosition,
            out var qualifiers,
            out var prefix,
            out var isTableContext,
            out var isProcedureContext,
            out var isJoinContext,
            out var isTypeContext,
            out var targetTableSchema,
            out var targetTableName,
            out var isOrderByContext,
            out var isComparisonRhsContext,
            out var isOutputContext,
            out var isInsertIntoContext,
            out var isUpdateSetContext,
            out var isFromOrJoinContext);

        var token = new CompletionToken(
            caretPosition,
            qualifiers,
            qualifiers.Count > 0 ? qualifiers[^1] : null,
            prefix,
            isInsertIntoContext,
            isUpdateSetContext,
            isOutputContext && qualifiers.Count > 0,
            isFromOrJoinContext,
            isProcedureContext);

        if (isTableContext)
        {
            AddTableCompletions(suggestions, metadata, token);
            if (isJoinContext)
            {
                TableCompletionHelper.AddJoinCompletions(suggestions, metadata, sql, token);
            }
            SnippetCompletionHelper.AddSnippetCompletions(suggestions, prefix, _usageRecorder, GetSnippets());
        }
        else if (isProcedureContext)
        {
            AddProcedureCompletions(suggestions, metadata, token);
        }
        else if (isTypeContext)
        {
            TypeCompletionHelper.AddTypeCompletions(suggestions, metadata, token);
        }
        else if (isOrderByContext)
        {
            var selectAliases = SqlContextAnalyzer.ExtractSelectAliases(sql, caretPosition);
            foreach (var alias in selectAliases.Where(a => SqlCompletionHelper.Matches(a, prefix)))
            {
                suggestions.Add(new SqlCompletionItem(
                    alias,
                    SqlCompletionHelper.Quote(alias),
                    SqlCompletionKind.Column,
                    "SELECT output alias"));
            }
            ColumnCompletionHelper.AddVisibleColumnCompletions(suggestions, metadata, sql, prefix);
            KeywordCompletionHelper.AddKeywordCompletions(suggestions, prefix, isExpressionContext: true);
        }
        else if (isComparisonRhsContext)
        {
            ColumnCompletionHelper.AddVisibleColumnCompletions(suggestions, metadata, sql, prefix);
            AddScalarFunctionCompletions(suggestions, metadata, token);
            KeywordCompletionHelper.AddKeywordCompletions(suggestions, prefix, isExpressionContext: true);
        }
        else if (isOutputContext && qualifiers.Count > 0)
        {
            QualifiedCompletionHelper.AddQualifiedCompletions(suggestions, metadata, sql, token);
        }
        else if (isOutputContext)
        {
            foreach (var pseudo in new[] { "INSERTED", "DELETED" }.Where(p => SqlCompletionHelper.Matches(p, prefix)))
            {
                suggestions.Add(new SqlCompletionItem(
                    pseudo, pseudo, SqlCompletionKind.Keyword, "OUTPUT pseudo-table"));
            }
            KeywordCompletionHelper.AddKeywordCompletions(suggestions, prefix, isExpressionContext: true);
        }
        else if (isInsertIntoContext)
        {
            ColumnCompletionHelper.AddTargetTableColumns(suggestions, metadata, sql, prefix, targetTableSchema, targetTableName);
            KeywordCompletionHelper.AddKeywordCompletions(suggestions, prefix);
        }
        else if (isUpdateSetContext)
        {
            ColumnCompletionHelper.AddTargetTableColumns(suggestions, metadata, sql, prefix, targetTableSchema, targetTableName);
            KeywordCompletionHelper.AddKeywordCompletions(suggestions, prefix);
        }
        else if (token.Qualifiers.Count > 0)
        {
            QualifiedCompletionHelper.AddQualifiedCompletions(suggestions, metadata, sql, token);
        }
        else
        {
            ColumnCompletionHelper.AddVisibleColumnCompletions(suggestions, metadata, sql, prefix, targetTableSchema, targetTableName);
            AddScalarFunctionCompletions(suggestions, metadata, token);
            SnippetCompletionHelper.AddSnippetCompletions(suggestions, prefix, _usageRecorder, GetSnippets());
            KeywordCompletionHelper.AddKeywordCompletions(suggestions, prefix, isExpressionContext: true);
        }

        return suggestions
            .Distinct(CompletionIdentityComparer.Instance)
            .OrderBy(item => SqlCompletionHelper.Rank(item.Label, prefix, item.Kind))
            .ThenBy(item => SqlCompletionHelper.UsageRank(item.Kind, item.Label, _usageRecorder))
            .ThenBy(item => item.Label.Length)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IEnumerable<ICandidate> GetCandidates(DatabaseMetadata metadata, string? filter = null)
    {
        var adapter = GetAdapter(metadata);
        foreach (var schema in adapter.Schemas)
        {
            foreach (var candidate in adapter.GetCandidatesInSchema(schema.Name))
            {
                if (filter == null || SqlCompletionHelper.Matches(candidate.Name, filter))
                    yield return candidate;
            }
        }
    }

    public IReadOnlyList<SqlCompletionItem> GetCandidateCompletions(
        DatabaseMetadata metadata,
        string sql,
        int caretPosition)
    {
        var suggestions = new List<SqlCompletionItem>();
        _adapter = null;
        var adapter = GetAdapter(metadata);
        var completer = new CandidateCompleter(metadata);

        SqlContextAnalyzer.AnalyzeCaretContext(
            sql,
            caretPosition,
            out var qualifiers,
            out var prefix,
            out var isTableContext,
            out var isProcedureContext,
            out _,
            out var isTypeContext,
            out _,
            out _,
            out var isOrderByContext,
            out var isComparisonRhsContext,
            out var isOutputContext,
            out var isInsertIntoContext,
            out var isUpdateSetContext,
            out var isFromOrJoinContext);

        var context = new CompletionContext
        {
            StartIndex = caretPosition - prefix.Length,
            EndIndex = caretPosition,
            Filter = prefix,
            IsExecContext = isProcedureContext,
            IsExpressionContext = !isTableContext && !isProcedureContext && !isTypeContext && !isOrderByContext,
            IsInsertIntoContext = isInsertIntoContext,
            IsUpdateSetContext = isUpdateSetContext,
            IsFromOrJoinContext = isFromOrJoinContext,
            IsOutputIntoContext = isOutputContext && qualifiers.Count > 0,
            IsTypeContext = isTypeContext,
            IsOrderByContext = isOrderByContext,
            IsComparisonRhsContext = isComparisonRhsContext,
        };

        foreach (var schema in adapter.Schemas)
        {
            foreach (var candidate in adapter.GetCandidatesInSchema(schema.Name))
            {
                if (!SqlCompletionHelper.Matches(candidate.Name, prefix)) continue;

                var fragment = candidate switch
                {
                    TableCandidate t => completer.CompleteTable(t, context),
                    ProcedureCandidate p when context.IsExecContext => completer.CompleteStoredProcedure(p, context),
                    FunctionCandidate f when context.IsExpressionContext => completer.CompleteScalarFunction(f, context),
                    _ => null
                };

                if (fragment == null) continue;

                suggestions.Add(new SqlCompletionItem(
                    $"{schema.Name}.{candidate.Name}",
                    fragment.Text,
                    CandidateKindToSqlCompletionKind(candidate.ObjectType),
                    $"{candidate.ObjectType} {schema.Name}.{candidate.Name}",
                    fragment.CaretOffset));
            }
        }

        return suggestions
            .Distinct(CompletionIdentityComparer.Instance)
            .OrderBy(item => SqlCompletionHelper.Rank(item.Label, prefix, item.Kind))
            .ThenBy(item => SqlCompletionHelper.UsageRank(item.Kind, item.Label, _usageRecorder))
            .ThenBy(item => item.Label.Length)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ── Candidate-based table completions ──────────────────────────────────

    private void AddTableCompletions(List<SqlCompletionItem> suggestions, DatabaseMetadata metadata, CompletionToken token)
    {
        var adapter = GetAdapter(metadata);
        var completer = new CandidateCompleter(metadata);

        if (token.Qualifiers.Count > 0)
        {
            var lastQualifier = token.Qualifiers[^1];

            if (SqlCompletionHelper.IsSchemaName(metadata, lastQualifier))
            {
                AddSchemaQualifiedTableCompletions(suggestions, metadata, token, lastQualifier);
                return;
            }

            if (SqlCompletionHelper.IsDatabaseName(metadata, lastQualifier))
            {
                foreach (var schema in adapter.Schemas.Where(s => SqlCompletionHelper.Matches(s.Name, token.Prefix)))
                    suggestions.Add(new SqlCompletionItem(
                        schema.Name,
                        $"{SqlCompletionHelper.Quote(schema.Name)}.",
                        SqlCompletionKind.Schema,
                        $"Schema {schema.Name}"));
                return;
            }

            if (SqlCompletionHelper.IsLinkedServerName(metadata, lastQualifier))
            {
                foreach (var db in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
                    suggestions.Add(new SqlCompletionItem(
                        db,
                        $"{SqlCompletionHelper.Quote(db)}.",
                        SqlCompletionKind.Database,
                        $"Database {db}"));
                return;
            }

            return;
        }

        // Unqualified: linked servers, databases, schemas
        foreach (var ls in metadata.LinkedServers.Where(ls => SqlCompletionHelper.Matches(ls.Name, token.Prefix)))
            suggestions.Add(new SqlCompletionItem(
                ls.Name, $"{SqlCompletionHelper.Quote(ls.Name)}.", SqlCompletionKind.LinkedServer, $"Linked Server {ls.Name}"));

        foreach (var db in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
            suggestions.Add(new SqlCompletionItem(
                db, $"{SqlCompletionHelper.Quote(db)}.", SqlCompletionKind.Database, $"Database {db}"));

        foreach (var schema in adapter.Schemas.Where(s => SqlCompletionHelper.Matches(s.Name, token.Prefix)))
            suggestions.Add(new SqlCompletionItem(
                schema.Name, $"{SqlCompletionHelper.Quote(schema.Name)}.", SqlCompletionKind.Schema, $"Schema {schema.Name}"));

        // Unqualified: objects with schema prefix
        var context = BuildCompletionContext(token);
        foreach (var schema in adapter.Schemas)
        {
            foreach (var candidate in adapter.GetCandidatesInSchema(schema.Name))
            {
                if (!SqlCompletionHelper.Matches(candidate.Name, token.Prefix)) continue;

                AddCandidateObject(suggestions, candidate, schema.Name, token, context, completer, includeSchema: true);
            }
        }
    }

    private void AddSchemaQualifiedTableCompletions(
        List<SqlCompletionItem> suggestions, DatabaseMetadata metadata, CompletionToken token, string schema)
    {
        var adapter = GetAdapter(metadata);

        // Tables
        foreach (var candidate in adapter.GetCandidatesInSchema(schema))
        {
            if (!SqlCompletionHelper.Matches(candidate.Name, token.Prefix)) continue;

            switch (candidate)
            {
                case TableCandidate t:
                    suggestions.Add(new SqlCompletionItem(
                        t.Name, SqlCompletionHelper.Quote(t.Name), SqlCompletionKind.Table,
                        $"Table {schema}.{t.Name}"));
                    break;
                case ViewCandidate v:
                    suggestions.Add(new SqlCompletionItem(
                        v.Name, SqlCompletionHelper.Quote(v.Name), SqlCompletionKind.View,
                        $"View {schema}.{v.Name}"));
                    break;
                case FunctionCandidate fn when fn.FunctionType is "TF" or "IF":
                    {
                        var @params = fn.Parameters;
                        string insertText;
                        int caretOffset;
                        var quoted = SqlCompletionHelper.Quote(fn.Name);

                        if (@params.Count > 0)
                        {
                            var paramList = string.Join(", ", @params.Select(p => $"@{p.Name}"));
                            insertText = $"{quoted}({paramList})";
                            caretOffset = quoted.Length + 1;
                        }
                        else
                        {
                            insertText = $"{quoted}()";
                            caretOffset = quoted.Length + 1;
                        }

                        suggestions.Add(new SqlCompletionItem(
                            fn.Name, insertText, SqlCompletionKind.Function,
                            $"Table Function {schema}.{fn.Name}", caretOffset));
                    }
                    break;
                case SynonymCandidate syn:
                    suggestions.Add(new SqlCompletionItem(
                        syn.Name, SqlCompletionHelper.Quote(syn.Name), SqlCompletionKind.Synonym,
                        $"Synonym {schema}.{syn.Name} -> {syn.TargetObject}"));
                    break;
            }
        }
    }

    // ── Candidate-based procedure completions ──────────────────────────────

    private void AddProcedureCompletions(List<SqlCompletionItem> suggestions, DatabaseMetadata metadata, CompletionToken token)
    {
        var adapter = GetAdapter(metadata);

        if (token.Qualifiers.Count > 0)
        {
            var lastQualifier = token.Qualifiers[^1];

            if (SqlCompletionHelper.IsSchemaName(metadata, lastQualifier))
            {
                AddSchemaQualifiedProcedureCompletions(suggestions, metadata, token, lastQualifier);
                return;
            }

            if (SqlCompletionHelper.IsDatabaseName(metadata, lastQualifier))
            {
                foreach (var schema in adapter.Schemas.Where(s => SqlCompletionHelper.Matches(s.Name, token.Prefix)))
                    suggestions.Add(new SqlCompletionItem(
                        schema.Name, $"{SqlCompletionHelper.Quote(schema.Name)}.", SqlCompletionKind.Schema, $"Schema {schema.Name}"));
                return;
            }

            if (SqlCompletionHelper.IsLinkedServerName(metadata, lastQualifier))
            {
                foreach (var db in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
                    suggestions.Add(new SqlCompletionItem(
                        db, $"{SqlCompletionHelper.Quote(db)}.", SqlCompletionKind.Database, $"Database {db}"));
                return;
            }

            return;
        }

        // Unqualified: linked servers, databases, schemas
        foreach (var ls in metadata.LinkedServers.Where(ls => SqlCompletionHelper.Matches(ls.Name, token.Prefix)))
            suggestions.Add(new SqlCompletionItem(
                ls.Name, $"{SqlCompletionHelper.Quote(ls.Name)}.", SqlCompletionKind.LinkedServer, $"Linked Server {ls.Name}"));

        foreach (var db in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
            suggestions.Add(new SqlCompletionItem(
                db, $"{SqlCompletionHelper.Quote(db)}.", SqlCompletionKind.Database, $"Database {db}"));

        foreach (var schema in adapter.Schemas.Where(s => SqlCompletionHelper.Matches(s.Name, token.Prefix)))
            suggestions.Add(new SqlCompletionItem(
                schema.Name, $"{SqlCompletionHelper.Quote(schema.Name)}.", SqlCompletionKind.Schema, $"Schema {schema.Name}"));

        // Unqualified: procedures with schema prefix
        foreach (var schema in adapter.Schemas)
        {
            foreach (var candidate in adapter.GetCandidatesInSchema(schema.Name))
            {
                if (candidate is not ProcedureCandidate proc) continue;
                if (!SqlCompletionHelper.Matches(proc.Name, token.Prefix)) continue;

                var insertText = $"{SqlCompletionHelper.Quote(schema.Name)}.{SqlCompletionHelper.Quote(proc.Name)}";
                suggestions.Add(CreateProcedureItem(proc, insertText, token.IsProcedureExecContext));
            }
        }
    }

    private void AddSchemaQualifiedProcedureCompletions(
        List<SqlCompletionItem> suggestions, DatabaseMetadata metadata, CompletionToken token, string schema)
    {
        var adapter = GetAdapter(metadata);
        foreach (var candidate in adapter.GetCandidatesInSchema(schema))
        {
            if (candidate is not ProcedureCandidate proc) continue;
            if (!SqlCompletionHelper.Matches(proc.Name, token.Prefix)) continue;

            var insertText = SqlCompletionHelper.Quote(proc.Name);
            suggestions.Add(CreateProcedureItem(proc, insertText, token.IsProcedureExecContext, label: proc.Name));
        }
    }

    private static SqlCompletionItem CreateProcedureItem(
        ProcedureCandidate proc, string insertText, bool isExecContext, string? label = null)
    {
        label ??= $"{proc.Schema}.{proc.Name}";
        if (isExecContext && proc.Parameters.Count > 0)
        {
            var paramList = string.Join(", ", proc.Parameters.Select(p => $"{p.Name} = ?"));
            var bodyInsertText = $"{insertText}({paramList})";
            var caretOffset = insertText.Length + 1;
            return new SqlCompletionItem(label, bodyInsertText, SqlCompletionKind.Procedure,
                $"Stored Procedure {proc.Schema}.{proc.Name}", CaretOffset: caretOffset);
        }
        return new SqlCompletionItem(label, insertText, SqlCompletionKind.Procedure,
            $"Stored Procedure {proc.Schema}.{proc.Name}");
    }

    // ── Candidate-based scalar function completions ────────────────────────

    private void AddScalarFunctionCompletions(List<SqlCompletionItem> suggestions, DatabaseMetadata metadata, CompletionToken token)
    {
        var adapter = GetAdapter(metadata);
        foreach (var schema in adapter.Schemas)
        {
            foreach (var candidate in adapter.GetCandidatesInSchema(schema.Name))
            {
                if (candidate is not FunctionCandidate fn) continue;
                if (fn.FunctionType != "FN") continue;
                if (!SqlCompletionHelper.Matches(fn.Name, token.Prefix)) continue;

                var quoted = SqlCompletionHelper.Quote(fn.Name);
                string insertText;
                int caretOffset;

                if (fn.Parameters.Count > 0)
                {
                    var paramList = string.Join(", ", fn.Parameters.Select(p => $"{p.Name}"));
                    insertText = $"{quoted}({paramList})";
                    caretOffset = quoted.Length + 1;
                }
                else
                {
                    insertText = $"{quoted}()";
                    caretOffset = quoted.Length + 1;
                }

                suggestions.Add(new SqlCompletionItem(
                    $"{schema.Name}.{fn.Name}",
                    insertText,
                    SqlCompletionKind.Function,
                    $"Scalar Function {schema.Name}.{fn.Name} ({fn.ReturnType})",
                    caretOffset));
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static CompletionContext BuildCompletionContext(CompletionToken token)
    {
        return new CompletionContext
        {
            Filter = token.Prefix,
            IsExecContext = token.IsProcedureExecContext,
            IsInsertIntoContext = token.IsInsertIntoContext,
            IsUpdateSetContext = token.IsUpdateSetContext,
            IsOutputIntoContext = token.IsOutputIntoContext,
            IsFromOrJoinContext = token.IsFromJoinContext,
        };
    }

    private static void AddCandidateObject(
        List<SqlCompletionItem> suggestions,
        ICandidate candidate,
        string schema,
        CompletionToken token,
        CompletionContext context,
        CandidateCompleter completer,
        bool includeSchema)
    {
        switch (candidate)
        {
            case TableCandidate t:
            {
                var qualifiedName = includeSchema
                    ? $"{SqlCompletionHelper.Quote(schema)}.{SqlCompletionHelper.Quote(t.Name)}"
                    : SqlCompletionHelper.Quote(t.Name);
                string insertText;
                int caretOffset = -1;

                if (includeSchema && token.IsInsertIntoContext && t.Columns.Count > 0)
                {
                    var colNames = t.Columns.Select(c => SqlCompletionHelper.Quote(c.Name));
                    var colList = string.Join(", ", colNames);
                    var valList = string.Join(", ", t.Columns.Select(c => GetDefaultValue(c.DataType)));
                    insertText = $"{qualifiedName}\r\n({colList})\r\nVALUES ({valList})";
                    caretOffset = insertText.Length - (valList.Length + 1);
                }
                else if (includeSchema && token.IsOutputIntoContext && t.Columns.Count > 0)
                {
                    var colNames = t.Columns.Select(c => SqlCompletionHelper.Quote(c.Name));
                    insertText = $"{qualifiedName} ({string.Join(", ", colNames)})";
                }
                else
                {
                    insertText = qualifiedName;
                }

                var label = includeSchema ? $"{schema}.{t.Name}" : t.Name;
                suggestions.Add(new SqlCompletionItem(
                    label, insertText, SqlCompletionKind.Table, $"Table {schema}.{t.Name}", caretOffset));
                break;
            }

            case ViewCandidate v:
            {
                var qualifiedName = includeSchema
                    ? $"{SqlCompletionHelper.Quote(schema)}.{SqlCompletionHelper.Quote(v.Name)}"
                    : SqlCompletionHelper.Quote(v.Name);
                var label = includeSchema ? $"{schema}.{v.Name}" : v.Name;
                suggestions.Add(new SqlCompletionItem(
                    label, qualifiedName, SqlCompletionKind.View, $"View {schema}.{v.Name}"));
                break;
            }

            case FunctionCandidate fn when fn.FunctionType is "TF" or "IF":
            {
                var baseName = includeSchema
                    ? $"{SqlCompletionHelper.Quote(schema)}.{SqlCompletionHelper.Quote(fn.Name)}"
                    : SqlCompletionHelper.Quote(fn.Name);

                var @params = fn.Parameters;
                string insertText;
                int caretOffset;

                if (@params.Count > 0)
                {
                    var paramList = string.Join(", ", @params.Select(p => $"@{p.Name}"));
                    insertText = $"{baseName}({paramList})";
                    caretOffset = baseName.Length + 1;
                }
                else
                {
                    insertText = $"{baseName}()";
                    caretOffset = baseName.Length + 1;
                }

                var label = includeSchema ? $"{schema}.{fn.Name}" : fn.Name;
                suggestions.Add(new SqlCompletionItem(
                    label, insertText, SqlCompletionKind.Function, $"Table Function {schema}.{fn.Name}", caretOffset));
                break;
            }

            case SynonymCandidate syn:
            {
                var qualifiedName = includeSchema
                    ? $"{SqlCompletionHelper.Quote(schema)}.{SqlCompletionHelper.Quote(syn.Name)}"
                    : SqlCompletionHelper.Quote(syn.Name);
                var label = includeSchema ? $"{schema}.{syn.Name}" : syn.Name;
                suggestions.Add(new SqlCompletionItem(
                    label, qualifiedName, SqlCompletionKind.Synonym,
                    $"Synonym {schema}.{syn.Name} -> {syn.TargetObject}"));
                break;
            }
        }
    }

    private static string GetDefaultValue(string dataType)
    {
        var type = dataType.ToUpperInvariant();
        if (type.Contains("INT") || type.Contains("NUMERIC") || type.Contains("DECIMAL") ||
            type.Contains("FLOAT") || type.Contains("REAL") || type.Contains("MONEY") ||
            type.Contains("BIT") || type.Contains("TINYINT") || type.Contains("SMALLINT") ||
            type.Contains("BIGINT"))
            return "0";
        if (type.Contains("DATE") || type.Contains("TIME"))
            return "NULL";
        if (type.Contains("BINARY") || type.Contains("IMAGE") || type.Contains("VARBINARY"))
            return "NULL";
        return "''";
    }

    private static SqlCompletionKind CandidateKindToSqlCompletionKind(SqlObjectType type) => type switch
    {
        SqlObjectType.Table => SqlCompletionKind.Table,
        SqlObjectType.View => SqlCompletionKind.View,
        SqlObjectType.StoredProcedure => SqlCompletionKind.Procedure,
        SqlObjectType.ScalarFunction or SqlObjectType.TableValuedFunction => SqlCompletionKind.Function,
        SqlObjectType.Synonym => SqlCompletionKind.Synonym,
        SqlObjectType.Schema => SqlCompletionKind.Schema,
        SqlObjectType.Column => SqlCompletionKind.Column,
        SqlObjectType.UserDefinedType => SqlCompletionKind.UserType,
        _ => SqlCompletionKind.Keyword
    };
}

public sealed class CompletionIdentityComparer : IEqualityComparer<SqlCompletionItem>
{
    public static readonly CompletionIdentityComparer Instance = new();

    public bool Equals(SqlCompletionItem? x, SqlCompletionItem? y) =>
        x != null && y != null &&
        x.InsertText.Equals(y.InsertText, StringComparison.OrdinalIgnoreCase) &&
        x.Kind == y.Kind;

    public int GetHashCode(SqlCompletionItem obj) =>
        HashCode.Combine(obj.InsertText.ToUpperInvariant(), obj.Kind);
}
