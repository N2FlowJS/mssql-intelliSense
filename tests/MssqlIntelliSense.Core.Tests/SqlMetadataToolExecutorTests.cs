using System;
using System.Text.Json;
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

        json.Should().Contain("endpoints");
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
}
