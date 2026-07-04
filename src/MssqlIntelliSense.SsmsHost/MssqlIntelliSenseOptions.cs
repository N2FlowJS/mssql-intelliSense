using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.Shell;

namespace MssqlIntelliSense.SsmsHost;

public sealed class MssqlIntelliSenseOptions : DialogPage
{
    [Category("AI Assistant (LLM)")]
    [DisplayName("API Key")]
    [Description("OpenAI or compatible LLM provider API key. Can be overridden by OPENAI_API_KEY environment variable.")]
    [PasswordPropertyText(true)]
    public string ApiKey { get; set; } = string.Empty;

    [Category("AI Assistant (LLM)")]
    [DisplayName("Model")]
    [Description("The model to use (e.g. gpt-4o, gemini-1.5-flash). Can be overridden by OPENAI_MODEL environment variable.")]
    public string Model { get; set; } = "gpt-4o";

    [Category("AI Assistant (LLM)")]
    [DisplayName("API Endpoint")]
    [Description("The API endpoint for completions. Defaults to https://api.openai.com/v1/responses.")]
    public string Endpoint { get; set; } = "https://api.openai.com/v1/responses";

    [Category("AI Assistant (LLM)")]
    [DisplayName("Default instruction")]
    [Description("Instruction used by the Improve SQL command.")]
    public string DefaultInstruction { get; set; } = "Optimize this SQL and explain the changes.";

    public string InstalledVersion => MssqlIntelliSensePackage.VersionString;

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);

        // Sync with config.json in %APPDATA%\MssqlIntelliSense
        try
        {
            var configPath = MssqlIntelliSense.Core.MssqlIntelliSenseConfig.GetConfigPath();
            var json = JsonSerializer.Serialize(new
            {
                LlmApiKey = ApiKey,
                LlmModel = Model,
                LlmEndpoint = Endpoint
            }, new JsonSerializerOptions { WriteIndented = true });
            
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Options Save Error] Failed to write config.json: {ex.Message}");
        }

        MssqlIntelliSensePackage.OnOptionsChanged();
    }

    public void SaveSettings(string apiKey, string model, string endpoint)
    {
        ApiKey = apiKey;
        Model = model;
        Endpoint = endpoint;

        SaveSettingsToStorage();

        try
        {
            var configPath = MssqlIntelliSense.Core.MssqlIntelliSenseConfig.GetConfigPath();
            var json = JsonSerializer.Serialize(new
            {
                LlmApiKey = ApiKey,
                LlmModel = Model,
                LlmEndpoint = Endpoint
            }, new JsonSerializerOptions { WriteIndented = true });
            
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Options Save Error] Failed to write config.json: {ex.Message}");
        }

        MssqlIntelliSensePackage.OnOptionsChanged();
    }
}
