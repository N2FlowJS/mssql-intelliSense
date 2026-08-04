using System;
using System.Text.Json;

namespace MssqlIntelliSense.SsmsHost;

internal static class ToolArgumentReader
{
    public static string GetString(string? argumentsJson, string name, string defaultValue = "")
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return defaultValue;
        }

        var json = argumentsJson ?? "{}";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? defaultValue;
            }
        }
        catch (JsonException)
        {
            return defaultValue;
        }

        return defaultValue;
    }
}
