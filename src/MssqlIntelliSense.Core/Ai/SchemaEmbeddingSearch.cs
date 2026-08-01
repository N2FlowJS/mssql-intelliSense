using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MssqlIntelliSense.Core.Ai;

public interface ITextEmbeddingProvider
{
    Task<ITextEmbeddingModel> CreateModelAsync(IReadOnlyList<string> corpus, CancellationToken cancellationToken);
}

public interface ITextEmbeddingModel
{
    int Dimensions { get; }
    IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public interface IEmbeddingCacheFingerprint
{
    string CacheFingerprint { get; }
}

public static class SchemaEmbeddingSearch
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, CacheEntry> IndexCache = new(StringComparer.Ordinal);
    private static string _lastCacheOperation = "Not built";
    private static DateTimeOffset? _lastCacheOperationAt;
    private static int _lastDocumentCount;
    private static int _lastDimensions;

    public static SchemaEmbeddingCacheStatus GetCacheStatus()
    {
        lock (SyncRoot)
        {
            return new SchemaEmbeddingCacheStatus(
                SchemaEmbeddingIndexStore.GetDirectoryPath(),
                IndexCache.Count,
                SchemaEmbeddingIndexStore.CountEntries(),
                _lastCacheOperation,
                _lastCacheOperationAt,
                _lastDocumentCount,
                _lastDimensions);
        }
    }

    public static async Task<int> EnsureIndexAsync(
        IReadOnlyList<string> documents,
        ITextEmbeddingProvider embeddingProvider,
        CancellationToken cancellationToken)
    {
        if (documents == null || documents.Count == 0)
        {
            return 0;
        }

        var entry = await GetOrCreateIndexAsync(documents, embeddingProvider, cancellationToken);
        return entry.Index.Count;
    }

    public static async Task<IReadOnlyDictionary<int, float>> SearchAsync(
        IReadOnlyList<string> documents,
        string query,
        int limit,
        ITextEmbeddingProvider embeddingProvider,
        CancellationToken cancellationToken)
    {
        if (documents == null || documents.Count == 0 || string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return new Dictionary<int, float>();
        }

        var cacheEntry = await GetOrCreateIndexAsync(documents, embeddingProvider, cancellationToken);
        var queryVectors = cacheEntry.Model.Embed(new[] { query }, cancellationToken);
        if (queryVectors == null || queryVectors.Count != 1 || queryVectors[0] == null || queryVectors[0].Length != cacheEntry.Dimensions)
        {
            return new Dictionary<int, float>();
        }

        return cacheEntry.Index.Search(queryVectors[0], Math.Min(limit, documents.Count))
            .ToDictionary(result => result.Id, result => result.Score);
    }

    private static async Task<CacheEntry> GetOrCreateIndexAsync(
        IReadOnlyList<string> documents,
        ITextEmbeddingProvider embeddingProvider,
        CancellationToken cancellationToken)
    {
        var key = BuildCacheKey(documents, embeddingProvider);
        lock (SyncRoot)
        {
            if (IndexCache.TryGetValue(key, out var existing))
            {
                RecordCacheOperation("Memory cache hit", existing);
                return existing;
            }
        }

        var model = await embeddingProvider.CreateModelAsync(documents, cancellationToken);
        var persistedEntry = SchemaEmbeddingIndexStore.TryLoad(key, documents.Count, model.Dimensions);
        if (persistedEntry != null)
        {
            try
            {
                var persistedIndex = HnswVectorIndex.ImportSnapshot(persistedEntry.IndexSnapshot);
                var loaded = new CacheEntry(persistedIndex, model, persistedEntry.Dimensions);
                lock (SyncRoot)
                {
                    if (!IndexCache.TryGetValue(key, out var existing))
                    {
                        CacheIndex(key, loaded);
                        RecordCacheOperation("Loaded persisted HNSW index", loaded);
                        return loaded;
                    }

                    RecordCacheOperation("Memory cache hit", existing);
                    return existing;
                }
            }
            catch
            {
            }
        }

        var vectors = model.Embed(documents, cancellationToken);
        if (vectors == null || vectors.Count != documents.Count || vectors.Count == 0 || vectors[0] == null || vectors[0].Length == 0)
        {
            throw new InvalidOperationException("The embedding provider returned an incomplete schema vector set.");
        }

        var dimensions = vectors[0].Length;
        if (vectors.Any(vector => vector == null || vector.Length != dimensions))
        {
            throw new InvalidOperationException("The embedding provider returned inconsistent vector dimensions.");
        }

        var index = new HnswVectorIndex();
        foreach (var vector in vectors)
        {
            index.Add(vector);
        }

        var created = new CacheEntry(index, model, dimensions);
        SchemaEmbeddingIndexStore.Save(new SchemaEmbeddingIndexCacheEntry(
            key,
            documents.Count,
            dimensions,
            DateTimeOffset.UtcNow,
            index.ExportSnapshot()));
        lock (SyncRoot)
        {
            if (IndexCache.TryGetValue(key, out var existing))
            {
                RecordCacheOperation("Memory cache hit", existing);
                return existing;
            }

            CacheIndex(key, created);
            RecordCacheOperation("Built and persisted HNSW index", created);
            return created;
        }
    }

    private static void CacheIndex(string key, CacheEntry entry)
    {
        if (IndexCache.Count >= 8)
        {
            IndexCache.Clear();
        }
        IndexCache[key] = entry;
    }

    private static void RecordCacheOperation(string operation, CacheEntry entry)
    {
        _lastCacheOperation = operation;
        _lastCacheOperationAt = DateTimeOffset.UtcNow;
        _lastDocumentCount = entry.Index.Count;
        _lastDimensions = entry.Dimensions;
    }

    private static string BuildCacheKey(IReadOnlyList<string> documents, ITextEmbeddingProvider embeddingProvider)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var document in documents)
            {
                foreach (var character in document ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                hash ^= '|';
                hash *= 16777619;
            }
            var providerFingerprint = embeddingProvider is IEmbeddingCacheFingerprint cacheFingerprint
                ? cacheFingerprint.CacheFingerprint
                : embeddingProvider.GetType().FullName ?? string.Empty;
            return providerFingerprint + ":" + documents.Count + ":" + hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private sealed record CacheEntry(HnswVectorIndex Index, ITextEmbeddingModel Model, int Dimensions);
}

public sealed record SchemaEmbeddingCacheStatus(
    string CacheDirectory,
    int MemoryIndexCount,
    int PersistedIndexCount,
    string LastOperation,
    DateTimeOffset? LastOperationAt,
    int DocumentCount,
    int Dimensions);
