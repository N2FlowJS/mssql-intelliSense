using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                LastSeenAt = DateTimeOffset.UtcNow,
                MetadataFile = GetConnectionMetadataFileName(id)
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

            connection.MetadataFile = GetConnectionMetadataFileName(connection.Id);
            connection.Metadata = null;
            connection.SchemaUpdatedAt = DateTimeOffset.UtcNow;
            SaveConnectionMetadata(connection, metadata);
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

            var metadata = LoadConnectionMetadata(connection);
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

            return connection == null
                ? DatabaseMetadata.Empty
                : LoadConnectionMetadata(connection);
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
            var connection = store.Connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection != null)
            {
                DeleteConnectionMetadata(connection);
            }

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
            return NormalizeStore(JsonSerializer.Deserialize<JsonCacheStore>(ReadAllTextShared(path), JsonOptions));
        }
        catch (JsonException)
        {
            return LoadBackupStore(path);
        }
        catch (IOException)
        {
            return LoadBackupStore(path);
        }
    }

    private static JsonCacheStore LoadBackupStore(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new JsonCacheStore();
        }

        var candidates = Directory.GetFiles(directory, fileName + "*")
            .Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .Where(f => f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToArray();

        foreach (var candidate in candidates)
        {
            try
            {
                var store = NormalizeStore(JsonSerializer.Deserialize<JsonCacheStore>(ReadAllTextShared(candidate.FullName), JsonOptions));
                if (store.Connections.Count > 0)
                {
                    return store;
                }
            }
            catch (Exception) when (
                candidate.Exists &&
                (candidate.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                 candidate.Name.IndexOf(".TMP", StringComparison.OrdinalIgnoreCase) >= 0))
            {
            }
        }

        return new JsonCacheStore();
    }

    private static JsonCacheStore NormalizeStore(JsonCacheStore? store)
    {
        store ??= new JsonCacheStore();
        if (store.NextConnectionId <= 0)
        {
            store.NextConnectionId = store.Connections.Count == 0 ? 1 : store.Connections.Max(c => c.Id) + 1;
        }

        var migrated = false;
        foreach (var connection in store.Connections)
        {
            if (string.IsNullOrWhiteSpace(connection.MetadataFile))
            {
                connection.MetadataFile = GetConnectionMetadataFileName(connection.Id);
                migrated = true;
            }

            if (connection.Metadata != null)
            {
                SaveConnectionMetadata(connection, connection.Metadata);
                connection.Metadata = null;
                migrated = true;
            }
        }

        if (migrated)
        {
            Save(store);
        }

        return store;
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

    private static DatabaseMetadata LoadConnectionMetadata(JsonConnectionRecord connection)
    {
        if (connection.Metadata != null)
        {
            return connection.Metadata;
        }

        var path = GetConnectionMetadataPath(connection);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return DatabaseMetadata.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<DatabaseMetadata>(ReadAllTextShared(path), JsonOptions)
                ?? DatabaseMetadata.Empty;
        }
        catch (JsonException)
        {
            return DatabaseMetadata.Empty;
        }
        catch (IOException)
        {
            return DatabaseMetadata.Empty;
        }
    }

    private static void SaveConnectionMetadata(JsonConnectionRecord connection, DatabaseMetadata metadata)
    {
        var path = GetConnectionMetadataPath(connection);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteAllTextExclusive(tempPath, JsonSerializer.Serialize(metadata ?? DatabaseMetadata.Empty, JsonOptions));
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

    private static void DeleteConnectionMetadata(JsonConnectionRecord connection)
    {
        var path = GetConnectionMetadataPath(connection);
        if (File.Exists(path))
        {
            RetryFileOperation(() => File.Delete(path));
        }
    }

    private static string GetConnectionMetadataFileName(int connectionId)
    {
        return Path.Combine("connections", $"connection-{connectionId}.json");
    }

    private static string GetConnectionMetadataPath(JsonConnectionRecord connection)
    {
        var fileName = string.IsNullOrWhiteSpace(connection.MetadataFile)
            ? GetConnectionMetadataFileName(connection.Id)
            : connection.MetadataFile;

        return Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(MssqlIntelliSenseConfig.GetAppDataFolder(), fileName);
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
        public string MetadataFile { get; set; } = string.Empty;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DatabaseMetadata? Metadata { get; set; }
    }
}
