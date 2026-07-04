using System.Text.Json;

namespace MssqlIntelliSense.Core.Ai;

public sealed record OpenAiSqlToolCall(
    string Name,
    string ArgumentsJson,
    string Description);
