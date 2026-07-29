using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Cache;

internal static class MssqlIntelliSenseJsonCache
{
    private const string MutexName = @"Local\MssqlIntelliSenseJsonCache";
    private const int FileRetryCount = 8;
    private const int FileRetryDelayMs = 75;
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static void Initialize()
    {
        WithCacheLock(() => _ = Load());
    }

    public static IReadOnlyList<ConnectionInfo> GetConnections()
    {
        return WithCacheLock(() =>
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
        });
    }

    public static int RegisterConnection(string normalizedConnectionString, string name)
    {
        return WithCacheLock(() =>
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
        });
    }

    public static void SaveSchemaCache(int connectionId, DatabaseMetadata metadata)
    {
        WithCacheLock(() =>
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
        });
    }

    public static (DatabaseMetadata Metadata, string RawJson, DateTimeOffset? SchemaUpdatedAt) GetSchemaDetails(int connectionId)
    {
        return WithCacheLock(() =>
        {
            var connection = Load().Connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection == null)
            {
                return (DatabaseMetadata.Empty, string.Empty, null);
            }

            var metadata = connection.Metadata ?? DatabaseMetadata.Empty;
            return (metadata, JsonSerializer.Serialize(metadata, JsonOptions), connection.SchemaUpdatedAt);
        });
    }

    public static DatabaseMetadata GetMetadataByConnectionString(string normalizedConnectionString)
    {
        return WithCacheLock(() =>
        {
            var connection = Load().Connections.FirstOrDefault(c =>
                MssqlIntelliSenseCacheWriter.NormalizeServerConnectionString(c.ConnectionString)
                    .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

            return connection?.Metadata ?? DatabaseMetadata.Empty;
        });
    }

    public static DateTimeOffset? GetSchemaUpdatedAt(int connectionId)
    {
        return WithCacheLock(() =>
        {
            return Load().Connections.FirstOrDefault(c => c.Id == connectionId)?.SchemaUpdatedAt;
        });
    }

    public static void DeleteConnection(int connectionId, IProgress<string>? progress)
    {
        WithCacheLock(() =>
        {
            progress?.Report("Dang xoa du lieu schema cache...");
            var store = Load();
            store.Connections.RemoveAll(c => c.Id == connectionId);
            Save(store);
            progress?.Report("Da xoa hoan tat.");
        });
    }

    private static T WithCacheLock<T>(Func<T> action)
    {
        lock (SyncRoot)
        {
            using var mutex = new Mutex(false, MutexName);
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                if (!acquired)
                {
                    throw new IOException("Timed out waiting for MSSQL IntelliSense cache lock.");
                }

                return action();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    private static void WithCacheLock(Action action)
    {
        WithCacheLock(() =>
        {
            action();
            return true;
        });
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
            var store = JsonSerializer.Deserialize<JsonCacheStore>(ReadAllTextShared(path), JsonOptions) ?? new JsonCacheStore();
            if (store.NextConnectionId <= 0)
            {
                store.NextConnectionId = store.Connections.Count == 0 ? 1 : store.Connections.Max(c => c.Id) + 1;
            }

            return store;
        }
        catch (JsonException)
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

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteAllTextExclusive(tempPath, JsonSerializer.Serialize(store, JsonOptions));
        RetryFileOperation(() =>
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        });

        if (File.Exists(tempPath))
        {
            RetryFileOperation(() => File.Delete(tempPath));
        }
    }

    private static string ReadAllTextShared(string path)
    {
        return RetryFileOperation(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }

    private static void WriteAllTextExclusive(string path, string content)
    {
        RetryFileOperation(() =>
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        });
    }

    private static T RetryFileOperation<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (attempt < FileRetryCount)
            {
                Thread.Sleep(FileRetryDelayMs * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < FileRetryCount)
            {
                Thread.Sleep(FileRetryDelayMs * attempt);
            }
        }
    }

    private static void RetryFileOperation(Action operation)
    {
        RetryFileOperation(() =>
        {
            operation();
            return true;
        });
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
