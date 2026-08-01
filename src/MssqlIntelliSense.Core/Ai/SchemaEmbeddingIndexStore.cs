using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MssqlIntelliSense.Core;

namespace MssqlIntelliSense.Core.Ai;

internal static class SchemaEmbeddingIndexStore
{
    private const int MaximumCachedIndexes = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SchemaEmbeddingIndexCacheEntry? TryLoad(string cacheKey, int documentCount, int dimensions)
    {
        var path = GetPath(cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<SchemaEmbeddingIndexCacheEntry>(File.ReadAllText(path), JsonOptions);
            if (entry == null ||
                !string.Equals(entry.CacheKey, cacheKey, StringComparison.Ordinal) ||
                entry.DocumentCount != documentCount ||
                entry.Dimensions != dimensions)
            {
                return null;
            }

            return entry;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SchemaEmbeddingIndexCacheEntry entry)
    {
        try
        {
            var path = GetPath(entry.CacheKey);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry, JsonOptions));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporaryPath, path);
            TrimOldEntries();
        }
        catch
        {
        }
    }

    public static int CountEntries()
    {
        try
        {
            return Directory.GetFiles(MssqlIntelliSenseConfig.GetSearchIndexCacheFolder(), "hnsw-*.json").Length;
        }
        catch
        {
            return 0;
        }
    }

    public static string GetDirectoryPath() => MssqlIntelliSenseConfig.GetSearchIndexCacheFolder();

    private static string GetPath(string cacheKey)
    {
        return Path.Combine(GetDirectoryPath(), "hnsw-" + ComputeHash(cacheKey) + ".json");
    }

    private static void TrimOldEntries()
    {
        try
        {
            var files = new DirectoryInfo(GetDirectoryPath())
                .GetFiles("hnsw-*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaximumCachedIndexes)
                .ToArray();
            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }

    private static string ComputeHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}

internal sealed record SchemaEmbeddingIndexCacheEntry(
    string CacheKey,
    int DocumentCount,
    int Dimensions,
    DateTimeOffset BuiltAtUtc,
    HnswVectorIndexSnapshot IndexSnapshot);
