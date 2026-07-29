using System;
using System.IO;
using System.Text.Json;

namespace MssqlIntelliSense.Core;

public record LlmSettings(string ApiKey, string Model, string Endpoint);

public static class MssqlIntelliSenseConfig
{
    private const string AppDataOverrideEnvironmentVariable = "MSSQL_INTELLISENSE_APPDATA";

    public static string GetAppDataFolder()
    {
        var configuredFolder = Environment.GetEnvironmentVariable(AppDataOverrideEnvironmentVariable);
        var appDataFolder = !string.IsNullOrWhiteSpace(configuredFolder)
            ? configuredFolder
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MssqlIntelliSense"
            );

        if (!Directory.Exists(appDataFolder))
        {
            Directory.CreateDirectory(appDataFolder);
        }
        return appDataFolder;
    }

    public static string GetCacheJsonPath()
    {
        return Path.Combine(GetAppDataFolder(), "cache.json");
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
