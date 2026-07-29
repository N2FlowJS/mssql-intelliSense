using FluentAssertions;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

public class JsonCacheTests : IDisposable
{
    private readonly string _cacheRoot;
    private readonly string? _oldCacheRoot;

    public JsonCacheTests()
    {
        _oldCacheRoot = Environment.GetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA");
        _cacheRoot = Path.Combine(Path.GetTempPath(), "mssql-intellisense-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", _cacheRoot);
    }

    [Fact]
    public void JsonCache_RoundTrips_Metadata_AndDeletesConnection()
    {
        MssqlIntelliSenseCacheWriter.InitializeDatabase();
        var connectionId = MssqlIntelliSenseCacheWriter.RegisterConnection(
            "Server=.;Database=Sales;Integrated Security=True;TrustServerCertificate=True",
            "local");

        var metadata = new DatabaseMetadata(
            new[]
            {
                new TableMetadata(
                    "dbo",
                    "Orders",
                    new[] { new ColumnMetadata("Id", "int", false, 1) },
                    new[] { "Id" }) { Database = "Sales" }
            },
            Array.Empty<ForeignKeyMetadata>(),
            Array.Empty<IndexMetadata>(),
            new[] { "Sales" },
            Array.Empty<LinkedServerInfo>())
        {
            Views = new[] { new ViewMetadata("dbo", "OrderView", Array.Empty<ColumnMetadata>()) { Database = "Sales" } },
            Endpoints = new[] { new EndpointInfo("TSQL Default TCP", "TSQL", "TCP", "STARTED", 1433) }
        };

        MssqlIntelliSenseCacheWriter.SaveSchemaCache(connectionId, metadata);

        var connections = MssqlIntelliSenseCacheReader.GetConnections();
        connections.Should().ContainSingle(c => c.Id == connectionId && c.SchemaUpdatedAt != null);

        var details = MssqlIntelliSenseCacheReader.GetSchemaDetails(connectionId);
        details.Metadata.Tables.Should().ContainSingle(t => t.Name == "Orders" && t.Columns[0].Name == "Id");
        details.Metadata.Views.Should().ContainSingle(v => v.Name == "OrderView");
        details.Metadata.Endpoints.Should().ContainSingle(e => e.Port == 1433);
        File.Exists(Path.Combine(_cacheRoot, "cache.json")).Should().BeTrue();

        MssqlIntelliSenseCacheWriter.DeleteConnection(connectionId);
        MssqlIntelliSenseCacheReader.GetConnections().Should().BeEmpty();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", _oldCacheRoot);
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }
}
