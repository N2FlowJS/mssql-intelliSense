using System;
using System.Linq;

namespace MssqlIntelliSense.Core.Metadata;

public static class MetadataDescriptionEditor
{
    private static readonly object MigrationSyncRoot = new();
    private static bool _legacyDescriptionsMigrated;

    public static void EnsureLegacyDescriptionsMigrated()
    {
        lock (MigrationSyncRoot)
        {
            if (_legacyDescriptionsMigrated)
            {
                return;
            }

            foreach (var entry in ObjectDescriptionStore.LoadAll())
            {
                if (TryParseColumnKey(entry.Key, out var columnKind, out var columnDatabase, out var columnSchema, out var columnObject, out var columnName))
                {
                    if (TryUpdateColumnDescription(columnKind, columnDatabase, columnSchema, columnObject, columnName, entry.Value))
                    {
                        ObjectDescriptionStore.SaveDescription(entry.Key, string.Empty);
                    }
                }
                else if (TryParseObjectKey(entry.Key, out var kind, out var database, out var schema, out var name) &&
                         TryUpdateObjectDescription(kind, database, schema, name, entry.Value))
                {
                    ObjectDescriptionStore.SaveDescription(entry.Key, string.Empty);
                }
            }

            _legacyDescriptionsMigrated = true;
        }
    }

    public static bool TryUpdateObjectDescription(string kind, string database, string schema, string name, string description)
    {
        foreach (var connection in MssqlIntelliSenseCacheReader.GetConnections())
        {
            var metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(connection.Id).Metadata;
            if (!ContainsObject(metadata, kind, database, schema, name))
            {
                continue;
            }

            MssqlIntelliSenseCacheWriter.SaveSchemaCache(
                connection.Id,
                UpdateMetadata(metadata, kind, database, schema, name, columnName: null, description));
            return true;
        }

        return false;
    }

    public static bool TryUpdateColumnDescription(string kind, string database, string schema, string name, string columnName, string description)
    {
        foreach (var connection in MssqlIntelliSenseCacheReader.GetConnections())
        {
            var metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(connection.Id).Metadata;
            if (!ContainsObject(metadata, kind, database, schema, name))
            {
                continue;
            }

            MssqlIntelliSenseCacheWriter.SaveSchemaCache(
                connection.Id,
                UpdateMetadata(metadata, kind, database, schema, name, columnName, description));
            return true;
        }

        return false;
    }

    private static bool ContainsObject(DatabaseMetadata metadata, string kind, string database, string schema, string name) =>
        kind.Equals("table", StringComparison.OrdinalIgnoreCase)
            ? metadata.Tables.Any(item => Matches(item.Database, database, item.Schema, schema, item.Name, name))
            : kind.Equals("view", StringComparison.OrdinalIgnoreCase)
                ? metadata.Views.Any(item => Matches(item.Database, database, item.Schema, schema, item.Name, name))
                : kind.Equals("procedure", StringComparison.OrdinalIgnoreCase)
                    ? metadata.Procedures.Any(item => Matches(item.Database, database, item.Schema, schema, item.Name, name))
                                        : kind.Equals("function", StringComparison.OrdinalIgnoreCase)
                                                ? metadata.Functions.Any(item => Matches(item.Database, database, item.Schema, schema, item.Name, name))
                                                : kind.Equals("usertype", StringComparison.OrdinalIgnoreCase)
                                                        ? metadata.UserTypes.Any(item => Matches(item.Database, database, item.Schema, schema, item.Name, name))
                                                        : kind.Equals("synonym", StringComparison.OrdinalIgnoreCase) &&
                                                            metadata.Synonyms.Any(item => Matches(item.Database, database, item.Schema, schema, item.Name, name));

    private static DatabaseMetadata UpdateMetadata(
        DatabaseMetadata metadata,
        string kind,
        string database,
        string schema,
        string name,
        string? columnName,
        string description)
    {
        var tables = metadata.Tables.Select(item =>
            kind.Equals("table", StringComparison.OrdinalIgnoreCase) && Matches(item.Database, database, item.Schema, schema, item.Name, name)
                ? item with
                {
                    ExtendedDescription = columnName == null ? description : item.ExtendedDescription,
                    Columns = columnName == null ? item.Columns : UpdateColumns(item.Columns, columnName, description)
                }
                : item).ToArray();
        var views = metadata.Views.Select(item =>
            kind.Equals("view", StringComparison.OrdinalIgnoreCase) && Matches(item.Database, database, item.Schema, schema, item.Name, name)
                ? item with
                {
                    ExtendedDescription = columnName == null ? description : item.ExtendedDescription,
                    Columns = columnName == null ? item.Columns : UpdateColumns(item.Columns, columnName, description)
                }
                : item).ToArray();
        var procedures = metadata.Procedures.Select(item =>
            kind.Equals("procedure", StringComparison.OrdinalIgnoreCase) && Matches(item.Database, database, item.Schema, schema, item.Name, name)
                ? item with { ExtendedDescription = description }
                : item).ToArray();
        var functions = metadata.Functions.Select(item =>
            kind.Equals("function", StringComparison.OrdinalIgnoreCase) && Matches(item.Database, database, item.Schema, schema, item.Name, name)
                ? item with { ExtendedDescription = description }
                : item).ToArray();
        var userTypes = metadata.UserTypes.Select(item =>
            kind.Equals("usertype", StringComparison.OrdinalIgnoreCase) && Matches(item.Database, database, item.Schema, schema, item.Name, name)
                ? item with
                {
                    ExtendedDescription = columnName == null ? description : item.ExtendedDescription,
                    Columns = columnName == null ? item.Columns : UpdateColumns(item.Columns, columnName, description)
                }
                : item).ToArray();
        var synonyms = metadata.Synonyms.Select(item =>
            kind.Equals("synonym", StringComparison.OrdinalIgnoreCase) && Matches(item.Database, database, item.Schema, schema, item.Name, name)
                ? item with { ExtendedDescription = description }
                : item).ToArray();

        return new DatabaseMetadata(tables, metadata.ForeignKeys, metadata.Indexes, metadata.Databases, metadata.LinkedServers)
        {
            Procedures = procedures,
            Views = views,
            Functions = functions,
            Triggers = metadata.Triggers,
            UserTypes = userTypes,
            Synonyms = synonyms,
            Users = metadata.Users,
            Endpoints = metadata.Endpoints
        };
    }

    private static System.Collections.Generic.IReadOnlyList<ColumnMetadata> UpdateColumns(
        System.Collections.Generic.IReadOnlyList<ColumnMetadata> columns,
        string columnName,
        string description) =>
        columns.Select(column => column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase)
            ? column with { Description = description }
            : column).ToArray();

    private static bool Matches(string database, string expectedDatabase, string schema, string expectedSchema, string name, string expectedName) =>
        database.Equals(expectedDatabase, StringComparison.OrdinalIgnoreCase) &&
        schema.Equals(expectedSchema, StringComparison.OrdinalIgnoreCase) &&
        name.Equals(expectedName, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseObjectKey(string key, out string kind, out string database, out string schema, out string name)
    {
        var parts = key.Split('|');
        if (parts.Length == 4)
        {
            kind = parts[0];
            database = parts[1];
            schema = parts[2];
            name = parts[3];
            return true;
        }

        kind = database = schema = name = string.Empty;
        return false;
    }

    private static bool TryParseColumnKey(string key, out string kind, out string database, out string schema, out string name, out string columnName)
    {
        var parts = key.Split('|');
        if (parts.Length == 6 && parts[4].Equals("column", StringComparison.OrdinalIgnoreCase))
        {
            kind = parts[0];
            database = parts[1];
            schema = parts[2];
            name = parts[3];
            columnName = parts[5];
            return true;
        }

        kind = database = schema = name = columnName = string.Empty;
        return false;
    }
}