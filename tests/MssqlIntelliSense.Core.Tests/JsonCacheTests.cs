using FluentAssertions;
using MssqlIntelliSense.Core.Metadata;
using System.Text.Json;

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
        File.Exists(Path.Combine(_cacheRoot, "connections", $"connection-{connectionId}.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_cacheRoot, "cache.json")).Should().NotContain("Orders");

        MssqlIntelliSenseCacheWriter.DeleteConnection(connectionId);
        MssqlIntelliSenseCacheReader.GetConnections().Should().BeEmpty();
        File.Exists(Path.Combine(_cacheRoot, "connections", $"connection-{connectionId}.json")).Should().BeFalse();
    }

    [Fact]
    public void JsonCache_MigratesEmbeddedMetadata_ToPerConnectionFile()
    {
        Directory.CreateDirectory(_cacheRoot);
        File.WriteAllText(Path.Combine(_cacheRoot, "cache.json"), """
        {
          "nextConnectionId": 2,
          "connections": [
            {
              "id": 1,
              "name": "legacy",
              "connectionString": "Data Source=.;Integrated Security=True;Trust Server Certificate=True",
              "isActive": true,
              "lastSeenAt": "2026-07-30T00:00:00+00:00",
              "schemaUpdatedAt": "2026-07-30T00:01:00+00:00",
              "metadata": {
                "tables": [
                  {
                    "schema": "dbo",
                    "name": "LegacyUsers",
                    "columns": [
                      { "name": "Id", "dataType": "int", "isNullable": false, "ordinal": 1 }
                    ],
                    "primaryKeyColumns": [ "Id" ],
                    "database": "LegacyDb"
                  }
                ],
                "foreignKeys": [],
                "indexes": [],
                "databases": [ "LegacyDb" ],
                "linkedServers": []
              }
            }
          ]
        }
        """);

        var details = MssqlIntelliSenseCacheReader.GetSchemaDetails(1);

        details.Metadata.Tables.Should().ContainSingle(t => t.Name == "LegacyUsers");
        File.Exists(Path.Combine(_cacheRoot, "connections", "connection-1.json")).Should().BeTrue();
        var indexJson = File.ReadAllText(Path.Combine(_cacheRoot, "cache.json"));
        indexJson.Should().Contain("connections");
        indexJson.Should().Contain("connection-1.json");
        indexJson.Should().NotContain("LegacyUsers");
    }

    [Fact]
    public async Task JsonCache_RoundTrippedMetadata_SupportsAllAgentTools()
    {
        MssqlIntelliSenseCacheWriter.InitializeDatabase();
        var connectionId = MssqlIntelliSenseCacheWriter.RegisterConnection(
            "Server=.;Database=TestDb;Integrated Security=True;TrustServerCertificate=True",
            "local");
        MssqlIntelliSenseCacheWriter.SaveSchemaCache(connectionId, TestMetadata.Create());

        var metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(connectionId).Metadata;
        var argumentsByTool = new Dictionary<string, string>
        {
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.ListTablesToolName] = "{}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.TableSchemaToolName] = "{\"schemaName\":\"dbo\",\"tableName\":\"Users\"}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.TableRelationsToolName] = "{\"tableName\":\"Orders\"}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.TableIndexesToolName] = "{\"tableName\":\"Users\"}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.SearchObjectsToolName] = "{\"query\":\"User\"}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.SearchSchemaObjectsToolName] = "{\"query\":\"User\"}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.FindColumnToolName] = "{\"query\":\"Email\"}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.ListEndpointsToolName] = "{}",
            [MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.ExecuteSqlToolName] = "{\"query\":\"SELECT 1\"}"
        };

        foreach (var toolName in MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.AllToolNames)
        {
            using var args = JsonDocument.Parse(argumentsByTool[toolName]);
            var output = await MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.ExecuteToolAsync(toolName, args.RootElement, metadata);

            if (toolName == MssqlIntelliSense.Core.Ai.SqlMetadataToolExecutor.ExecuteSqlToolName)
            {
                output.Should().Contain("requires an SSMS runtime connection executor");
            }
            else
            {
                output.Should().NotContain("\"error\"", toolName);
            }
        }
    }

    [Fact]
    public void JsonCache_RoundTrips_ViewProcedureAndFunctionDefinitions()
    {
        MssqlIntelliSenseCacheWriter.InitializeDatabase();
        var connectionId = MssqlIntelliSenseCacheWriter.RegisterConnection(
            "Server=.;Database=TestDb;Integrated Security=True;TrustServerCertificate=True",
            "local");

        MssqlIntelliSenseCacheWriter.SaveSchemaCache(connectionId, TestMetadata.Create());

        var metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(connectionId).Metadata;
        metadata.Views.Should().ContainSingle(v =>
            v.Name == "ActiveUsers" &&
            v.Definition.Contains("CREATE VIEW [dbo].[ActiveUsers]") &&
            v.Definition.Contains("WHERE IsActive = 1"));
        metadata.Procedures.Should().ContainSingle(p =>
            p.Name == "GetUser" &&
            p.Definition.Contains("CREATE PROCEDURE [dbo].[GetUser]") &&
            p.Definition.Contains("@IncludeInactive bit"));
        metadata.Functions.Should().ContainSingle(f =>
            f.Name == "NormalizeEmail" &&
            f.Definition.Contains("CREATE FUNCTION [dbo].[NormalizeEmail]") &&
            f.Definition.Contains("RETURN LOWER(@value);"));
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
