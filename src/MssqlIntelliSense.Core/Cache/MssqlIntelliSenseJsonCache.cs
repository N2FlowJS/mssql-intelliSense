using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Cache;

internal static class MssqlIntelliSenseJsonCache
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            _ = Load();
        }
    }

    public static IReadOnlyList<ConnectionInfo> GetConnections()
    {
        lock (SyncRoot)
        {
            return Load().Connections
                .OrderByDescending(c => c.LastSeenAt)
                .ThenBy(c => c.Name)
                .Select(c => new ConnectionInfo(
                    c.Id,
                    c.Name,
                    c.ConnectionString,
                    c.IsActive,
                    c.LastSeenAt,
                    c.SchemaUpdatedAt))
                .ToList();
        }
    }

    public static int RegisterConnection(string normalizedConnectionString, string name)
    {
        lock (SyncRoot)
        {
            var store = Load();
            var existing = store.Connections.FirstOrDefault(c =>
                MssqlIntelliSenseCacheWriter.NormalizeServerConnectionString(c.ConnectionString)
                    .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Name = name;
                existing.ConnectionString = normalizedConnectionString;
                existing.IsActive = true;
                existing.LastSeenAt = DateTimeOffset.UtcNow;
                Save(store);
                return existing.Id;
            }

            var id = store.NextConnectionId++;
            store.Connections.Add(new JsonConnectionRecord
            {
                Id = id,
                Name = name,
                ConnectionString = normalizedConnectionString,
                IsActive = true,
                LastSeenAt = DateTimeOffset.UtcNow
            });

            Save(store);
            return id;
        }
    }

    public static void SaveSchemaCache(int connectionId, DatabaseMetadata metadata)
    {
        lock (SyncRoot)
        {
            var store = Load();
            var connection = store.Connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection == null)
            {
                return;
            }

            connection.Metadata = metadata;
            connection.SchemaUpdatedAt = DateTimeOffset.UtcNow;
            Save(store);
        }
    }

    public static (DatabaseMetadata Metadata, string RawJson, DateTimeOffset? SchemaUpdatedAt) GetSchemaDetails(int connectionId)
    {
        lock (SyncRoot)
        {
            var connection = Load().Connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection == null)
            {
                return (DatabaseMetadata.Empty, string.Empty, null);
            }

            var metadata = connection.Metadata ?? DatabaseMetadata.Empty;
            return (metadata, JsonSerializer.Serialize(metadata, JsonOptions), connection.SchemaUpdatedAt);
        }
    }

    public static DatabaseMetadata GetMetadataByConnectionString(string normalizedConnectionString)
    {
        lock (SyncRoot)
        {
            var connection = Load().Connections.FirstOrDefault(c =>
                MssqlIntelliSenseCacheWriter.NormalizeServerConnectionString(c.ConnectionString)
                    .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

            return connection?.Metadata ?? DatabaseMetadata.Empty;
        }
    }

    public static DateTimeOffset? GetSchemaUpdatedAt(int connectionId)
    {
        lock (SyncRoot)
        {
            return Load().Connections.FirstOrDefault(c => c.Id == connectionId)?.SchemaUpdatedAt;
        }
    }

    public static void DeleteConnection(int connectionId, IProgress<string>? progress)
    {
        lock (SyncRoot)
        {
            progress?.Report("Dang xoa du lieu schema cache...");
            var store = Load();
            store.Connections.RemoveAll(c => c.Id == connectionId);
            Save(store);
            progress?.Report("Da xoa hoan tat.");
        }
    }

    private static JsonCacheStore Load()
    {
        var path = MssqlIntelliSenseConfig.GetCacheJsonPath();
        if (!File.Exists(path))
        {
            var empty = new JsonCacheStore();
            Save(empty);
            return empty;
        }

        try
        {
            var store = JsonSerializer.Deserialize<JsonCacheStore>(File.ReadAllText(path), JsonOptions) ?? new JsonCacheStore();
            if (store.NextConnectionId <= 0)
            {
                store.NextConnectionId = store.Connections.Count == 0 ? 1 : store.Connections.Max(c => c.Id) + 1;
            }

            return store;
        }
        catch
        {
            return new JsonCacheStore();
        }
    }

    private static void Save(JsonCacheStore store)
    {
        var path = MssqlIntelliSenseConfig.GetCacheJsonPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(store, JsonOptions));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
    }

    private sealed class JsonCacheStore
    {
        public int NextConnectionId { get; set; } = 1;
        public List<JsonConnectionRecord> Connections { get; set; } = new();
    }

    private sealed class JsonConnectionRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public DateTimeOffset? LastSeenAt { get; set; }
        public DateTimeOffset? SchemaUpdatedAt { get; set; }
        public DatabaseMetadata? Metadata { get; set; }
    }
}
