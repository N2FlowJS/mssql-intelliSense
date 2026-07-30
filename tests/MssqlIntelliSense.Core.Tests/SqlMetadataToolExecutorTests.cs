using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MssqlIntelliSense.Core.Ai;
using MssqlIntelliSense.Core.Metadata;
using Xunit;

namespace MssqlIntelliSense.Core.Tests;

public sealed class SqlMetadataToolExecutorTests
{
    [Fact]
    public async Task ExecuteToolAsync_ListTables_ReturnsAllTables()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{}");
        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.ListTablesToolName, args.RootElement, metadata);

        json.Should().Contain("Users").And.Contain("Orders");
    }

    [Fact]
    public async Task ExecuteToolAsync_GetTableSchema_ReturnsColumnsAndPk()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{\"schemaName\":\"dbo\",\"tableName\":\"Users\"}");
        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.TableSchemaToolName, args.RootElement, metadata);

        json.Should().Contain("Users").And.Contain("nvarchar").And.Contain("primaryKeyColumns");
    }

    [Fact]
    public async Task ExecuteToolAsync_GetTableRelations_ReturnsForeignKeys()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{\"tableName\":\"Orders\"}");
        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.TableRelationsToolName, args.RootElement, metadata);

        json.Should().Contain("FK_Orders_Users");
    }

    [Fact]
    public async Task ExecuteToolAsync_SearchObjects_And_SearchSchemaObjects_BehaveIdentically()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{\"query\":\"Users\"}");

        var json1 = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, args.RootElement, metadata);
        var json2 = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchSchemaObjectsToolName, args.RootElement, metadata);

        json1.Should().Contain("Users");
        json2.Should().Be(json1);
    }

    [Fact]
    public async Task ExecuteToolAsync_SearchObjects_UsesCustomAgentDescription()
    {
        var previous = Environment.GetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA");
        var tempFolder = Path.Combine(Path.GetTempPath(), "mssql-intellisense-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", tempFolder);
        try
        {
            var metadata = TestMetadata.Create();
            ObjectDescriptionStore.SaveDescription("table", "TestDb", "dbo", "Users", "customer login identity profile");
            using var args = JsonDocument.Parse("{\"query\":\"identity profile\"}");

            var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, args.RootElement, metadata);

            json.Should().Contain("Users");
            json.Should().Contain("customer login identity profile");
            json.Should().Contain("\"score\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", previous);
            DeleteDirectoryWithRetry(tempFolder);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_SearchObjects_UsesSqlDefinitionText()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{\"query\":\"IsActive\"}");

        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, args.RootElement, metadata);

        json.Should().Contain("ActiveUsers");
        json.Should().Contain("WHERE IsActive = 1");
    }

    [Fact]
    public async Task ExecuteToolAsync_FindColumn_FindsMatchingColumns()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{\"query\":\"UserId\"}");

        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.FindColumnToolName, args.RootElement, metadata);

        json.Should().Contain("UserId").And.Contain("Orders");
    }

    [Fact]
    public async Task ExecuteToolAsync_ListEndpoints_ReturnsEndpoints()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{}");

        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.ListEndpointsToolName, args.RootElement, metadata);

        json.Should().Contain("endpoints").And.Contain("TSQL Default TCP");
    }

    [Fact]
    public async Task ExecuteToolAsync_GetTableIndexes_ReturnsIndexes()
    {
        var metadata = TestMetadata.Create();
        using var args = JsonDocument.Parse("{\"tableName\":\"Users\"}");

        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.TableIndexesToolName, args.RootElement, metadata);

        json.Should().Contain("IX_Users_Email").And.Contain("Email");
    }

    [Fact]
    public async Task ExecuteToolAsync_AllAgentTools_RunAgainstCachedMetadata()
    {
        var metadata = TestMetadata.Create();
        var argumentsByTool = new Dictionary<string, string>
        {
            [SqlMetadataToolExecutor.ListTablesToolName] = "{}",
            [SqlMetadataToolExecutor.TableSchemaToolName] = "{\"schemaName\":\"dbo\",\"tableName\":\"Users\"}",
            [SqlMetadataToolExecutor.TableRelationsToolName] = "{\"tableName\":\"Orders\"}",
            [SqlMetadataToolExecutor.TableIndexesToolName] = "{\"tableName\":\"Users\"}",
            [SqlMetadataToolExecutor.SearchObjectsToolName] = "{\"query\":\"User\"}",
            [SqlMetadataToolExecutor.SearchSchemaObjectsToolName] = "{\"query\":\"User\"}",
            [SqlMetadataToolExecutor.FindColumnToolName] = "{\"query\":\"Email\"}",
            [SqlMetadataToolExecutor.ListEndpointsToolName] = "{}",
            [SqlMetadataToolExecutor.ExecuteSqlToolName] = "{\"query\":\"SELECT 1\"}"
        };

        foreach (var toolName in SqlMetadataToolExecutor.AllToolNames)
        {
            using var args = JsonDocument.Parse(argumentsByTool[toolName]);
            var json = await SqlMetadataToolExecutor.ExecuteToolAsync(toolName, args.RootElement, metadata);

            json.Should().NotBeNullOrWhiteSpace(toolName);
            if (toolName == SqlMetadataToolExecutor.ExecuteSqlToolName)
            {
                json.Should().Contain("requires an SSMS runtime connection executor");
            }
            else
            {
                json.Should().NotContain("\"error\"", toolName);
            }

            SqlMetadataToolExecutor.BuildPreviewRows(toolName, metadata, "dbo", "Users", "Email")
                .Should().NotBeNull(toolName);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_GraphQlFallback_TriggeredWhenEmptyMetadata()
    {
        bool calledFallback = false;
        Func<string, object?, Task<string>> fallback = (query, vars) =>
        {
            calledFallback = true;
            return Task.FromResult("{\"data\":{\"tablesList\":[]}}");
        };

        using var args = JsonDocument.Parse("{}");
        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(
            SqlMetadataToolExecutor.ListTablesToolName,
            args.RootElement,
            DatabaseMetadata.Empty,
            fallback);

        calledFallback.Should().BeTrue();
        json.Should().Contain("tablesList");
    }

    [Fact]
    public void BuildPreviewRows_ReturnsEnumerableForKnownTools()
    {
        var metadata = TestMetadata.Create();

        var tableRows = SqlMetadataToolExecutor.BuildPreviewRows(SqlMetadataToolExecutor.ListTablesToolName, metadata, "dbo", "Users", "");
        tableRows.Should().NotBeNull();

        var schemaRows = SqlMetadataToolExecutor.BuildPreviewRows(SqlMetadataToolExecutor.TableSchemaToolName, metadata, "dbo", "Users", "");
        schemaRows.Should().NotBeNull();
    }

    [Fact]
    public void GetToolDescription_ReturnsNonEmptyDescriptions()
    {
        foreach (var tool in SqlMetadataToolExecutor.AllToolNames)
        {
            var desc = SqlMetadataToolExecutor.GetToolDescription(tool);
            desc.Should().NotBeNullOrWhiteSpace();

            var plannerDesc = SqlMetadataToolExecutor.GetToolPlannerDescription(tool);
            plannerDesc.Should().NotBeNullOrWhiteSpace();

            var approvalReason = SqlMetadataToolExecutor.GetToolApprovalReason(tool);
            approvalReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_ListTables_TruncatesWhenOver500Tables()
    {
        var tables = new List<TableMetadata>();
        for (int i = 0; i < 600; i++)
        {
            tables.Add(new TableMetadata("dbo", $"Table_{i}", Array.Empty<ColumnMetadata>(), Array.Empty<string>()));
        }

        var metadata = new DatabaseMetadata(tables, Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>(), new[] { "TestDb" }, Array.Empty<LinkedServerInfo>());
        using var args = JsonDocument.Parse("{}");
        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.ListTablesToolName, args.RootElement, metadata);

        json.Should().Contain("\"truncated\":true");
        json.Should().Contain("\"totalCount\":600");
    }

    [Fact]
    public void BuildPreviewRows_HandlesNullCollectionsAndCapsAt500()
    {
        var emptyMetadata = DatabaseMetadata.Empty;
        var tableRows = SqlMetadataToolExecutor.BuildPreviewRows(SqlMetadataToolExecutor.ListTablesToolName, emptyMetadata, "dbo", "", "");
        tableRows.Should().NotBeNull();

        var tables = new List<TableMetadata>();
        for (int i = 0; i < 600; i++)
        {
            tables.Add(new TableMetadata("dbo", $"Table_{i}", Array.Empty<ColumnMetadata>(), Array.Empty<string>()));
        }

        var largeMetadata = new DatabaseMetadata(tables, Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>(), new[] { "TestDb" }, Array.Empty<LinkedServerInfo>());
        var preview = SqlMetadataToolExecutor.BuildPreviewRows(SqlMetadataToolExecutor.ListTablesToolName, largeMetadata, "dbo", "", "");
        preview.Should().NotBeNull();
        var count = 0;
        foreach (var _ in preview!) count++;
        count.Should().Be(500);
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(attempt * 75);
            }
            catch (UnauthorizedAccessException) when (attempt < 8)
            {
                Thread.Sleep(attempt * 75);
            }
        }
    }
}
