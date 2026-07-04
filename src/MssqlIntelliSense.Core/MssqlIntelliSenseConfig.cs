using System;
using System.IO;
using System.Text.Json;

namespace MssqlIntelliSense.Core;

public record LlmSettings(string ApiKey, string Model, string Endpoint);

public static class MssqlIntelliSenseConfig
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MssqlIntelliSense"
    );

    public static string GetAppDataFolder()
    {
        if (!Directory.Exists(AppDataFolder))
        {
            Directory.CreateDirectory(AppDataFolder);
        }
        return AppDataFolder;
    }

    public static string GetDbConnectionString()
    {
        var dbPath = Path.Combine(GetAppDataFolder(), "MssqlIntelliSense.db");
        return $"Data Source={dbPath};";
    }

    public static string GetConfigPath()
    {
        return Path.Combine(GetAppDataFolder(), "config.json");
    }

    public static LlmSettings GetLlmSettings()
    {
        var path = GetConfigPath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string apiKey = root.TryGetProperty("LlmApiKey", out var keyProp) ? keyProp.GetString() ?? "" : "";
                    string model = root.TryGetProperty("LlmModel", out var modelProp) ? modelProp.GetString() ?? "gpt-4o" : "gpt-4o";
                    string endpoint = root.TryGetProperty("LlmEndpoint", out var endProp) ? endProp.GetString() ?? "https://api.openai.com/v1/responses" : "https://api.openai.com/v1/responses";
                    return new LlmSettings(apiKey, model, endpoint);
                }
            }
            catch { }
        }
        return new LlmSettings("", "gpt-4o", "https://api.openai.com/v1/responses");
    }
}
