using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MssqlIntelliSense.Core.Metadata;

public static class ObjectDescriptionStore
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string GetPath() =>
        Path.Combine(MssqlIntelliSenseConfig.GetAppDataFolder(), "object-descriptions.json");

    public static string BuildKey(string kind, string database, string schema, string name) =>
        string.Join("|", new[]
        {
            NormalizePart(kind),
            NormalizePart(database),
            NormalizePart(schema),
            NormalizePart(name)
        });

    public static IReadOnlyDictionary<string, string> LoadAll()
    {
        lock (SyncRoot)
        {
            return new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string GetDescription(string kind, string database, string schema, string name)
    {
        var key = BuildKey(kind, database, schema, name);
        lock (SyncRoot)
        {
            return Load().TryGetValue(key, out var value) ? value : string.Empty;
        }
    }

    public static void SaveDescription(string kind, string database, string schema, string name, string description)
    {
        var key = BuildKey(kind, database, schema, name);
        SaveDescription(key, description);
    }

    public static void SaveDescription(string key, string description)
    {
        lock (SyncRoot)
        {
            var data = Load();
            if (string.IsNullOrWhiteSpace(description))
            {
                data.Remove(key);
            }
            else
            {
                data[key] = description.Trim();
            }

            Save(data);
        }
    }

    private static Dictionary<string, string> Load()
    {
        var path = GetPath();
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions);
            return data == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(Dictionary<string, string> data)
    {
        var path = GetPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static string NormalizePart(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
}
