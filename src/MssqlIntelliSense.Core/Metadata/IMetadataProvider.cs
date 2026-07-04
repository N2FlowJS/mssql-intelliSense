namespace MssqlIntelliSense.Core.Metadata;

public interface IMetadataProvider
{
    Task<DatabaseMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);
}
