using FluentAssertions;
using MssqlIntelliSense.Core.Ai;

namespace MssqlIntelliSense.Core.Tests;

public sealed class LocalTextEmbeddingProviderTests
{
    [Fact]
    public async Task CreateModelAsync_AssignsHigherWeightToRareTerms()
    {
        var provider = new LocalTextEmbeddingProvider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        var model = await provider.CreateModelAsync(
            ["common common common", "common rare"],
            CancellationToken.None);

        var vectors = model.Embed(["common", "rare"], CancellationToken.None);

        SquaredMagnitude(vectors[1]).Should().BeGreaterThan(SquaredMagnitude(vectors[0]));
    }

    [Fact]
    public async Task CreateModelAsync_UsesTermFrequencyForRepeatedTerms()
    {
        var provider = new LocalTextEmbeddingProvider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        var model = await provider.CreateModelAsync(["payment settlement"], CancellationToken.None);

        var vectors = model.Embed(["payment", "payment payment"], CancellationToken.None);

        SquaredMagnitude(vectors[1]).Should().BeGreaterThan(SquaredMagnitude(vectors[0]));
    }

    [Fact]
    public async Task CreateModelAsync_LoadsSynonymsFromJson()
    {
        var synonymsPath = Path.Combine(Path.GetTempPath(), "mssql-intellisense-synonyms-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(synonymsPath, "{\"tai\":[\"document\"],\"lieu\":[\"document\"]}");
            var provider = new LocalTextEmbeddingProvider(synonymsPath);
            var model = await provider.CreateModelAsync(["document archive"], CancellationToken.None);

            var vectors = model.Embed(["tài liệu", "unrelated"], CancellationToken.None);

            SquaredMagnitude(vectors[0]).Should().BeGreaterThan(SquaredMagnitude(vectors[1]));
        }
        finally
        {
            if (File.Exists(synonymsPath)) File.Delete(synonymsPath);
        }
    }

    private static double SquaredMagnitude(float[] vector) => vector.Sum(value => value * value);
}
