using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

public static class TableCompletionHelper
{
    public static void AddTableCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        CompletionToken token)
    {
        if (token.Qualifiers.Count > 0)
        {
            var lastQualifier = token.Qualifiers[^1];

            // 1. Check if the qualifier is a Schema
            var isSchema = SqlCompletionHelper.IsSchemaName(metadata, lastQualifier);

            if (isSchema)
            {
                // Tables
                foreach (var table in metadata.Tables.Where(t =>
                             t.Schema.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase) &&
                             SqlCompletionHelper.Matches(t.Name, token.Prefix)))
                {
                    suggestions.Add(new SqlCompletionItem(
                        table.Name,
                        SqlCompletionHelper.Quote(table.Name),
                        SqlCompletionKind.Table,
                        $"Table {table.Schema}.{table.Name}"));
                }

                // Views
                foreach (var view in metadata.Views.Where(v =>
                             v.Schema.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase) &&
                             SqlCompletionHelper.Matches(v.Name, token.Prefix)))
                {
                    suggestions.Add(new SqlCompletionItem(
                        view.Name,
                        SqlCompletionHelper.Quote(view.Name),
                        SqlCompletionKind.View,
                        $"View {view.Schema}.{view.Name}"));
                }

                // Synonyms
                foreach (var syn in metadata.Synonyms.Where(s =>
                             s.Schema.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase) &&
                             SqlCompletionHelper.Matches(s.Name, token.Prefix)))
                {
                    suggestions.Add(new SqlCompletionItem(
                        syn.Name,
                        SqlCompletionHelper.Quote(syn.Name),
                        SqlCompletionKind.Synonym,
                        $"Synonym {syn.Schema}.{syn.Name} -> {syn.TargetObject}"));
                }

                // Table-Valued Functions (TF / IF)
                foreach (var fn in metadata.Functions.Where(f =>
                             f.Schema.Equals(lastQualifier, StringComparison.OrdinalIgnoreCase) &&
                             (f.FunctionType == "TF" || f.FunctionType == "IF") &&
                             SqlCompletionHelper.Matches(f.Name, token.Prefix)))
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
                        fn.Name,
                        insertText,
                        SqlCompletionKind.Function,
                        $"Table Function {fn.Schema}.{fn.Name}",
                        caretOffset));
                }

                return;
            }

            // 2. Check if database
            var isDatabase = SqlCompletionHelper.IsDatabaseName(metadata, lastQualifier);
            if (isDatabase)
            {
                var schemas = metadata.Tables.Select(t => t.Schema)
                    .Concat(metadata.Views.Select(v => v.Schema))
                    .Concat(metadata.Procedures.Select(p => p.Schema))
                    .Concat(metadata.Functions.Select(f => f.Schema))
                    .Concat(metadata.Synonyms.Select(s => s.Schema))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(schema => SqlCompletionHelper.Matches(schema, token.Prefix));

                foreach (var schema in schemas)
                {
                    suggestions.Add(new SqlCompletionItem(
                        schema,
                        $"{SqlCompletionHelper.Quote(schema)}.",
                        SqlCompletionKind.Schema,
                        $"Schema {schema}"));
                }
                return;
            }

            // 3. Check if linked server
            var isLinkedServer = SqlCompletionHelper.IsLinkedServerName(metadata, lastQualifier);
            if (isLinkedServer)
            {
                foreach (var database in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
                {
                    suggestions.Add(new SqlCompletionItem(
                        database,
                        $"{SqlCompletionHelper.Quote(database)}.",
                        SqlCompletionKind.Database,
                        $"Database {database}"));
                }
                return;
            }

            return;
        }

        // Unqualified table context completions
        foreach (var ls in metadata.LinkedServers.Where(ls => SqlCompletionHelper.Matches(ls.Name, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                ls.Name,
                $"{SqlCompletionHelper.Quote(ls.Name)}.",
                SqlCompletionKind.LinkedServer,
                $"Linked Server {ls.Name}"));
        }

        foreach (var db in metadata.Databases.Where(db => SqlCompletionHelper.Matches(db, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                db,
                $"{SqlCompletionHelper.Quote(db)}.",
                SqlCompletionKind.Database,
                $"Database {db}"));
        }

        var allSchemas = metadata.Tables.Select(t => t.Schema)
            .Concat(metadata.Views.Select(v => v.Schema))
            .Concat(metadata.Procedures.Select(p => p.Schema))
            .Concat(metadata.Functions.Select(f => f.Schema))
            .Concat(metadata.Synonyms.Select(s => s.Schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(schema => SqlCompletionHelper.Matches(schema, token.Prefix));

        foreach (var schema in allSchemas)
        {
            suggestions.Add(new SqlCompletionItem(
                schema,
                $"{SqlCompletionHelper.Quote(schema)}.",
                SqlCompletionKind.Schema,
                $"Schema {schema}"));
        }

        // Tables
        foreach (var table in metadata.Tables.Where(t => SqlCompletionHelper.Matches(t.Name, token.Prefix)))
        {
            AddTableWithContext(suggestions, table.Schema, table.Name, SqlCompletionKind.Table,
                $"Table {table.Schema}.{table.Name}", table.Columns, token);
        }

        // Views
        foreach (var view in metadata.Views.Where(v => SqlCompletionHelper.Matches(v.Name, token.Prefix)))
        {
            AddTableWithContext(suggestions, view.Schema, view.Name, SqlCompletionKind.View,
                $"View {view.Schema}.{view.Name}", view.Columns, token);
        }

        // Synonyms
        foreach (var syn in metadata.Synonyms.Where(s => SqlCompletionHelper.Matches(s.Name, token.Prefix)))
        {
            suggestions.Add(new SqlCompletionItem(
                $"{syn.Schema}.{syn.Name}",
                $"{SqlCompletionHelper.Quote(syn.Schema)}.{SqlCompletionHelper.Quote(syn.Name)}",
                SqlCompletionKind.Synonym,
                $"Synonym {syn.Schema}.{syn.Name}"));
        }

        // Table functions
        foreach (var fn in metadata.Functions.Where(f => (f.FunctionType == "TF" || f.FunctionType == "IF") && SqlCompletionHelper.Matches(f.Name, token.Prefix)))
        {
            AddTableWithContext(suggestions, fn.Schema, fn.Name, SqlCompletionKind.Function,
                $"Table Function {fn.Schema}.{fn.Name}", null, token, fn.Parameters);
        }
    }

    public static void AddJoinCompletions(
        List<SqlCompletionItem> suggestions,
        DatabaseMetadata metadata,
        string sql,
        CompletionToken token)
    {
        var sources = SqlContextAnalyzer.FindSources(sql, metadata);
        if (sources.Count == 0) return;

        var adapter = new MetadataAdapter(metadata);

        foreach (var source in sources)
        {
            var tableMetadata = adapter.CurrentDatabase.FindTableCandidate(source.Schema, source.Name)?.Source;
            if (tableMetadata == null) continue;

            var visited = sources.Select(s => $"{s.Schema}.{s.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentPath = new List<TableRelationStep>();
            var paths = new List<List<TableRelationStep>>();

            FindPaths(tableMetadata, currentPath, visited, adapter.CurrentDatabase, maxDepth: 3, paths);

            foreach (var path in paths)
            {
                var finalTable = path[^1].ToTable;
                if (!SqlCompletionHelper.Matches(finalTable.Name, token.Prefix) && 
                    !SqlCompletionHelper.Matches(finalTable.Schema, token.Prefix))
                {
                    continue;
                }

                var activeSources = sources.ToList();
                var joinClauses = new List<string>();

                for (int i = 0; i < path.Count; i++)
                {
                    var step = path[i];
                    var sourceAlias = i == 0 ? source.Alias : activeSources[sources.Count + i - 1].Alias;
                    var targetAlias = GenerateAlias(step.ToTable.Name, activeSources);
                    activeSources.Add(new VisibleSource(step.ToTable.Schema, step.ToTable.Name, targetAlias, step.ToTable.Columns));

                    string conditionStr;
                    if (step.IsOutgoing)
                    {
                        var conditions = step.Columns.Select(fk =>
                            $"{SqlCompletionHelper.Quote(targetAlias)}.{SqlCompletionHelper.Quote(fk.ToColumn)} = {SqlCompletionHelper.Quote(sourceAlias)}.{SqlCompletionHelper.Quote(fk.FromColumn)}");
                        conditionStr = string.Join(" AND ", conditions);
                    }
                    else
                    {
                        var conditions = step.Columns.Select(fk =>
                            $"{SqlCompletionHelper.Quote(targetAlias)}.{SqlCompletionHelper.Quote(fk.FromColumn)} = {SqlCompletionHelper.Quote(sourceAlias)}.{SqlCompletionHelper.Quote(fk.ToColumn)}");
                        conditionStr = string.Join(" AND ", conditions);
                    }

                    if (i == 0)
                    {
                        joinClauses.Add($"{SqlCompletionHelper.Quote(step.ToTable.Schema)}.{SqlCompletionHelper.Quote(step.ToTable.Name)} AS {SqlCompletionHelper.Quote(targetAlias)} ON {conditionStr}");
                    }
                    else
                    {
                        joinClauses.Add($"JOIN {SqlCompletionHelper.Quote(step.ToTable.Schema)}.{SqlCompletionHelper.Quote(step.ToTable.Name)} AS {SqlCompletionHelper.Quote(targetAlias)} ON {conditionStr}");
                    }
                }

                var lastStep = path[^1];
                var lastSourceAlias = path.Count == 1 ? source.Alias : activeSources[sources.Count + path.Count - 2].Alias;
                var lastTargetAlias = activeSources[sources.Count + path.Count - 1].Alias;
                string lastConditionStr;
                if (lastStep.IsOutgoing)
                {
                    var conditions = lastStep.Columns.Select(fk =>
                        $"{SqlCompletionHelper.Quote(lastTargetAlias)}.{SqlCompletionHelper.Quote(fk.ToColumn)} = {SqlCompletionHelper.Quote(lastSourceAlias)}.{SqlCompletionHelper.Quote(fk.FromColumn)}");
                    lastConditionStr = string.Join(" AND ", conditions);
                }
                else
                {
                    var conditions = lastStep.Columns.Select(fk =>
                        $"{SqlCompletionHelper.Quote(lastTargetAlias)}.{SqlCompletionHelper.Quote(fk.FromColumn)} = {SqlCompletionHelper.Quote(lastSourceAlias)}.{SqlCompletionHelper.Quote(fk.ToColumn)}");
                    lastConditionStr = string.Join(" AND ", conditions);
                }

                var viaPath = string.Join(" -> ", path.Take(path.Count - 1).Select(s => s.ToTable.Name));
                var label = string.IsNullOrEmpty(viaPath)
                    ? $"{finalTable.Schema}.{finalTable.Name} ON {lastConditionStr}"
                    : $"{finalTable.Schema}.{finalTable.Name} (via {viaPath}) ON {lastConditionStr}";

                var insertText = string.Join(" ", joinClauses);
                var fkChain = string.Join(" -> ", path.Select(s => s.ForeignKeyName));
                var description = $"JOIN {finalTable.Schema}.{finalTable.Name} via {fkChain}";

                suggestions.Add(new SqlCompletionItem(
                    label,
                    insertText,
                    SqlCompletionKind.Table,
                    description));
            }
        }
    }

    private static void AddTableWithContext(
        List<SqlCompletionItem> suggestions,
        string schema,
        string name,
        SqlCompletionKind kind,
        string description,
        IReadOnlyList<ColumnMetadata>? columns,
        CompletionToken token,
        IReadOnlyList<FunctionParameterMetadata>? parameters = null)
    {
        var label = $"{schema}.{name}";
        string insertText;
        int caretOffset = -1;

        var qualifiedName = $"{SqlCompletionHelper.Quote(schema)}.{SqlCompletionHelper.Quote(name)}";

        if (kind == SqlCompletionKind.Function)
        {
            if (parameters != null && parameters.Count > 0)
            {
                var paramList = string.Join(", ", parameters.Select(p => $"@{p.Name}"));
                insertText = $"{qualifiedName}({paramList})";
                caretOffset = qualifiedName.Length + 1;
            }
            else
            {
                insertText = $"{qualifiedName}()";
                caretOffset = qualifiedName.Length + 1;
            }
        }
        else if (token.IsInsertIntoContext && columns != null && columns.Count > 0)
        {
            var colNames = columns.Select(c => SqlCompletionHelper.Quote(c.Name));
            var colList = string.Join(", ", colNames);
            var valList = string.Join(", ", columns.Select(c => GetDefaultValue(c.DataType)));
            insertText = $"{qualifiedName}\r\n({colList})\r\nVALUES ({valList})";
            caretOffset = insertText.Length - (valList.Length + 1);
        }
        else if (token.IsOutputIntoContext && columns != null && columns.Count > 0)
        {
            var colNames = columns.Select(c => SqlCompletionHelper.Quote(c.Name));
            insertText = $"{qualifiedName} ({string.Join(", ", colNames)})";
        }
        else
        {
            insertText = qualifiedName;
        }

        suggestions.Add(new SqlCompletionItem(
            label,
            insertText,
            kind,
            description,
            caretOffset));
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

    private static string GenerateAlias(string tableName, IReadOnlyList<VisibleSource> existingSources)
    {
        var baseAlias = new string(tableName.Where(char.IsUpper).ToArray()).ToLower();
        if (string.IsNullOrEmpty(baseAlias))
        {
            baseAlias = tableName[..1].ToLower();
        }

        var alias = baseAlias;
        int counter = 1;
        while (existingSources.Any(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
        {
            alias = $"{baseAlias}{counter++}";
        }
        return alias;
    }

    private sealed record TableRelationStep(
        TableMetadata FromTable,
        TableMetadata ToTable,
        string ForeignKeyName,
        IReadOnlyList<ForeignKeyMetadata> Columns,
        bool IsOutgoing
    );

    private static List<TableRelationStep> GetRelations(TableMetadata table, DatabaseCandidate db)
    {
        var relations = new List<TableRelationStep>();
        var fkGroups = db.Metadata.ForeignKeys.GroupBy(fk => fk.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in fkGroups)
        {
            var first = group.First();
            var columns = group.OrderBy(fk => fk.Ordinal).ToList();

            if (columns.All(fk => fk.FromSchema.Equals(table.Schema, StringComparison.OrdinalIgnoreCase) &&
                                  fk.FromTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var targetTable = db.FindTableCandidate(first.ToSchema, first.ToTable)?.Source;
                if (targetTable != null)
                {
                    relations.Add(new TableRelationStep(table, targetTable, first.Name, columns, IsOutgoing: true));
                }
            }
            else if (columns.All(fk => fk.ToSchema.Equals(table.Schema, StringComparison.OrdinalIgnoreCase) &&
                                       fk.ToTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var sourceTable = db.FindTableCandidate(first.FromSchema, first.FromTable)?.Source;
                if (sourceTable != null)
                {
                    relations.Add(new TableRelationStep(table, sourceTable, first.Name, columns, IsOutgoing: false));
                }
            }
        }
        return relations;
    }

    private static void FindPaths(
        TableMetadata currentTable,
        List<TableRelationStep> currentPath,
        HashSet<string> visitedTables,
        DatabaseCandidate db,
        int maxDepth,
        List<List<TableRelationStep>> result)
    {
        if (currentPath.Count >= maxDepth) return;

        var relations = GetRelations(currentTable, db);
        foreach (var relation in relations)
        {
            var targetKey = $"{relation.ToTable.Schema}.{relation.ToTable.Name}";
            if (visitedTables.Contains(targetKey)) continue;

            currentPath.Add(relation);
            visitedTables.Add(targetKey);

            result.Add(currentPath.ToList());

            FindPaths(relation.ToTable, currentPath, visitedTables, db, maxDepth, result);

            visitedTables.Remove(targetKey);
            currentPath.RemoveAt(currentPath.Count - 1);
        }
    }
}
