using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Ai;

public sealed record AiSqlResult(
    string ImprovedSql,
    string Explanation,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> IndexSuggestions);

public interface IAiSqlAssistant
{
    Task<AiSqlResult> ImproveSqlAsync(
        string sql,
        DatabaseMetadata metadata,
        string instruction,
        CancellationToken cancellationToken);
}
