using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Completion.Snippets;

public sealed record SnippetPlaceholder(string Name, string DefaultValue);

public sealed class Snippet
{
    public string Prefix { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<SnippetPlaceholder> Placeholders { get; init; } = [];
}
