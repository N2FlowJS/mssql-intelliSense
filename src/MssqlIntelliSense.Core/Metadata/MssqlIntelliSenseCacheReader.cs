using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Cache;

namespace MssqlIntelliSense.Core.Metadata;

public sealed record ConnectionInfo(int Id, string Name, string ConnectionString, bool IsActive, DateTimeOffset? LastSeenAt, DateTimeOffset? SchemaUpdatedAt);

public static class MssqlIntelliSenseCacheReader
{
    public static IReadOnlyList<ConnectionInfo> GetConnections()
    {
        try
        {
            return MssqlIntelliSenseJsonCache.GetConnections();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache Read Error] {ex.Message}");
            return Array.Empty<ConnectionInfo>();
        }
    }

    public static (DatabaseMetadata Metadata, string RawJson, DateTimeOffset? SchemaUpdatedAt) GetSchemaDetails(int connectionId)
    {
        try
        {
            return MssqlIntelliSenseJsonCache.GetSchemaDetails(connectionId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache Read Error] {ex.Message}");
            return (DatabaseMetadata.Empty, string.Empty, null);
        }
    }

    public static DatabaseMetadata GetMetadataByConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return DatabaseMetadata.Empty;
        }

        var normalizedConnectionString = MssqlIntelliSenseCacheWriter.NormalizeServerConnectionString(connectionString);

        try
        {
            return MssqlIntelliSenseJsonCache.GetMetadataByConnectionString(normalizedConnectionString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache Read Error] {ex.Message}");
            return DatabaseMetadata.Empty;
        }
    }

    public static DatabaseMetadata GetMetadataByConnectionStringAndDatabase(
        string connectionString,
        string? activeDatabase)
    {
        var full = GetMetadataByConnectionString(connectionString);
        if (full == DatabaseMetadata.Empty) return full;
        if (string.IsNullOrWhiteSpace(activeDatabase)) return full;
        return FilterByDatabase(full, activeDatabase!);
    }

    public static DatabaseMetadata FilterByDatabase(DatabaseMetadata full, string databaseName)
    {
        bool hasDbData = full.Tables.Any(t => !string.IsNullOrEmpty(t.Database));
        if (!hasDbData) return full;

        var tables     = full.Tables    .Where(t => t.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var fks        = full.ForeignKeys.Where(f => f.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var indexes    = full.Indexes   .Where(i => i.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var procedures = full.Procedures.Where(p => p.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var views      = full.Views     .Where(v => v.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var functions  = full.Functions .Where(f => f.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var triggers   = full.Triggers  .Where(t => t.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var userTypes  = full.UserTypes .Where(u => u.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var synonyms   = full.Synonyms  .Where(s => s.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var users      = full.Users     .Where(u => u.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (tables.Length == 0 && views.Length == 0 && procedures.Length == 0)
            return full;

        return new DatabaseMetadata(tables, fks, indexes, new[] { databaseName }, full.LinkedServers)
        {
            Procedures = procedures,
            Views      = views,
            Functions  = functions,
            Triggers   = triggers,
            UserTypes  = userTypes,
            Synonyms   = synonyms,
            Users      = users,
            Endpoints  = full.Endpoints
        };
    }
}
