using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MssqlIntelliSense.Core;

namespace MssqlIntelliSense.Core.Ai;

/// <summary>
/// Offline hashing TF-IDF provider for schema search. IDF is fitted from the cached
/// schema corpus, then queries are transformed with the same feature statistics.
/// </summary>
public sealed class LocalTextEmbeddingProvider : ITextEmbeddingProvider, IEmbeddingCacheFingerprint
{
    private const int Dimensions = 2048;
    private readonly string _synonymsPath;

    public LocalTextEmbeddingProvider(string? synonymsPath = null)
    {
        _synonymsPath = string.IsNullOrWhiteSpace(synonymsPath)
            ? MssqlIntelliSenseConfig.GetSearchSynonymsPath()
            : synonymsPath!.Trim();
    }

    public string CacheFingerprint => BuildSynonymSnapshot().Fingerprint;

    public Task<ITextEmbeddingModel> CreateModelAsync(IReadOnlyList<string> corpus, CancellationToken cancellationToken)
    {
        if (corpus == null) throw new ArgumentNullException(nameof(corpus));

        var synonymSnapshot = BuildSynonymSnapshot();
        var documentFrequency = new int[Dimensions];
        foreach (var document in corpus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var featureIndex in GetFeatureWeights(document, synonymSnapshot.Synonyms).Keys)
            {
                documentFrequency[featureIndex]++;
            }
        }

        var inverseDocumentFrequency = new float[Dimensions];
        for (var index = 0; index < inverseDocumentFrequency.Length; index++)
        {
            inverseDocumentFrequency[index] = (float)(Math.Log((corpus.Count + 1d) / (documentFrequency[index] + 1d)) + 1d);
        }

        return Task.FromResult<ITextEmbeddingModel>(new TfIdfModel(inverseDocumentFrequency, synonymSnapshot.Synonyms));
    }

    private sealed class TfIdfModel : ITextEmbeddingModel
    {
        private readonly float[] _inverseDocumentFrequency;
        private readonly IReadOnlyDictionary<string, string[]> _synonyms;

        public TfIdfModel(float[] inverseDocumentFrequency, IReadOnlyDictionary<string, string[]> synonyms)
        {
            _inverseDocumentFrequency = inverseDocumentFrequency;
            _synonyms = synonyms;
        }

        public int Dimensions => _inverseDocumentFrequency.Length;

        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            if (texts == null) throw new ArgumentNullException(nameof(texts));

            var vectors = new List<float[]>(texts.Count);
            foreach (var text in texts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vector = new float[Dimensions];
                foreach (var feature in GetFeatureWeights(text, _synonyms))
                {
                    vector[feature.Key] = feature.Value * _inverseDocumentFrequency[feature.Key];
                }
                vectors.Add(vector);
            }

            return vectors;
        }
    }

    private static IReadOnlyDictionary<int, float> GetFeatureWeights(
        string? text,
        IReadOnlyDictionary<string, string[]> synonyms)
    {
        var weights = new Dictionary<int, float>();
        foreach (var token in Tokenize(Normalize(text)))
        {
            AddFeature(weights, token, 1f);
            if (synonyms.TryGetValue(token, out var expansions))
            {
                foreach (var synonym in expansions)
                {
                    AddFeature(weights, synonym, 1f);
                }
            }

            if (token.Length >= 3)
            {
                for (var index = 0; index <= token.Length - 3; index++)
                {
                    AddFeature(weights, token.Substring(index, 3), 0.25f);
                }
            }
        }

        return weights;
    }

    private SynonymSnapshot BuildSynonymSnapshot()
    {
        if (!File.Exists(_synonymsPath))
        {
            return SynonymSnapshot.Empty;
        }

        try
        {
            var json = File.ReadAllText(_synonymsPath);
            var source = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            if (source == null)
            {
                return SynonymSnapshot.Empty;
            }

            var synonyms = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                var key = Normalize(pair.Key);
                var expansions = (pair.Value ?? Array.Empty<string>())
                    .Select(Normalize)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (!string.IsNullOrWhiteSpace(key) && expansions.Length > 0)
                {
                    synonyms[key] = expansions;
                }
            }

            return new SynonymSnapshot(synonyms, ComputeFingerprint(json));
        }
        catch
        {
            return SynonymSnapshot.Empty;
        }
    }

    private static string ComputeFingerprint(string value)
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

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value!.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(character == 'đ' || character == 'Đ' ? 'd' : char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static IEnumerable<string> Tokenize(string text) =>
        text.Split(new[] { ' ', '.', ',', ';', ':', '_', '-', '/', '\\', '[', ']', '(', ')', '{', '}', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1);

    private static void AddFeature(IDictionary<int, float> weights, string feature, float weight)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in feature)
            {
                hash ^= character;
                hash *= 16777619;
            }

            var index = (int)(hash % Dimensions);
            var signedWeight = (hash & 1) == 0 ? weight : -weight;
            if (weights.TryGetValue(index, out var existing))
            {
                weights[index] = existing + signedWeight;
            }
            else
            {
                weights[index] = signedWeight;
            }
        }
    }

    private sealed record SynonymSnapshot(IReadOnlyDictionary<string, string[]> Synonyms, string Fingerprint)
    {
        public static SynonymSnapshot Empty { get; } = new(
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            "no-synonyms");
    }
}
