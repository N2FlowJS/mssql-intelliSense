using System;
using MssqlIntelliSense.Core.Cache;

namespace MssqlIntelliSense.Core.Metadata;

public static class MssqlIntelliSenseCacheWriter
{
    public static void InitializeDatabase()
    {
        MssqlIntelliSenseJsonCache.Initialize();
    }

    public static int RegisterConnection(string connectionString, string name)
    {
        var normalizedConnectionString = NormalizeServerConnectionString(connectionString);
        return MssqlIntelliSenseJsonCache.RegisterConnection(normalizedConnectionString, name);
    }

    internal static string NormalizeServerConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            builder.Remove("Initial Catalog");
            builder.Remove("Database");
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    public static void SaveSchemaCache(int connectionId, DatabaseMetadata metadata)
    {
        MssqlIntelliSenseJsonCache.SaveSchemaCache(connectionId, metadata);
    }

    public static DateTimeOffset? GetSchemaUpdatedAt(int connectionId)
    {
        return MssqlIntelliSenseJsonCache.GetSchemaUpdatedAt(connectionId);
    }

    public static void DeleteConnection(int connectionId, IProgress<string>? progress = null)
    {
        MssqlIntelliSenseJsonCache.DeleteConnection(connectionId, progress);
    }
}
