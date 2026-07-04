namespace MssqlIntelliSense.Core.Ai;

public sealed record OpenAiSqlAgentOptions
{
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public Uri Endpoint { get; init; } = new("https://api.openai.com/v1/responses");
    public Func<OpenAiSqlToolCall, CancellationToken, Task<bool>> ToolApprovalHandler { get; init; } =
        (_, _) => Task.FromResult(false);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new ArgumentException("ApiKey cannot be null or whitespace.", nameof(ApiKey));
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Model cannot be null or whitespace.", nameof(Model));
        if (!Endpoint.IsAbsoluteUri) throw new ArgumentException("OpenAI endpoint must be an absolute URI.", nameof(Endpoint));
    }
}
