using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MssqlIntelliSense.Core.Completion.Snippets;

internal sealed record SnippetData(
    string Prefix,
    string? Description,
    string? Body,
    List<PlaceholderData>? Placeholders);

internal sealed record PlaceholderData(string Name, string? DefaultValue);

public static class SnippetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IEnumerable<Snippet> LoadFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            yield break;

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.json"))
        {
            var snippet = LoadFromFile(filePath);
            if (snippet != null)
                yield return snippet;
        }
    }

    public static Snippet? LoadFromFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            return LoadFromJson(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public static Snippet? LoadFromJson(string json)
    {
        SnippetData? data;
        try
        {
            data = JsonSerializer.Deserialize<SnippetData>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (data == null || string.IsNullOrWhiteSpace(data.Prefix))
            return null;

        return new Snippet
        {
            Prefix = data.Prefix,
            Description = data.Description ?? "",
            Body = data.Body ?? "",
            Placeholders = data.Placeholders?.ConvertAll(p => new SnippetPlaceholder(p.Name, p.DefaultValue ?? "")) ?? []
        };
    }
}
