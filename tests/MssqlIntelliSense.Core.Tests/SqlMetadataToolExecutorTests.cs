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
    public async Task ExecuteToolAsync_GetTableSchema_IncludesObjectAndColumnGuidance()
    {
        var previous = Environment.GetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA");
        var tempFolder = Path.Combine(Path.GetTempPath(), "mssql-intellisense-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", tempFolder);
        try
        {
            var objectKey = ObjectDescriptionStore.BuildKey("table", "TestDb", "dbo", "Users");
            ObjectDescriptionStore.SaveDescription(objectKey, "Represents authenticated application users.");
            ObjectDescriptionStore.SaveDescription(
                ObjectDescriptionStore.BuildColumnKey(objectKey, "Email"),
                "Primary login email. Treat as personally identifiable information.");
            using var args = JsonDocument.Parse("{\"schemaName\":\"dbo\",\"tableName\":\"Users\"}");

            var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.TableSchemaToolName, args.RootElement, TestMetadata.Create());

            json.Should().Contain("Represents authenticated application users.");
            json.Should().Contain("Primary login email. Treat as personally identifiable information.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", previous);
            DeleteDirectoryWithRetry(tempFolder);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_GetTableSchema_IncludesDatabaseColumnDescription()
    {
        var metadata = new DatabaseMetadata(
            [new TableMetadata("dbo", "Accounts", [new ColumnMetadata("AccountNumber", "nvarchar", false, 1, "External account identifier used for reconciliation.")], ["AccountNumber"])
            { Database = "TestDb" }],
            [], [], ["TestDb"], []);
        using var args = JsonDocument.Parse("{\"schemaName\":\"dbo\",\"tableName\":\"Accounts\"}");

        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.TableSchemaToolName, args.RootElement, metadata);

        json.Should().Contain("External account identifier used for reconciliation.");
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
    public async Task ExecuteToolAsync_SearchObjects_FindsVietnameseItsObjectGuidance()
    {
        var previous = Environment.GetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA");
        var tempFolder = Path.Combine(Path.GetTempPath(), "mssql-intellisense-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", tempFolder);
        try
        {
            var metadata = new DatabaseMetadata(
                [new TableMetadata("dbo", "PDApplicationReference", Array.Empty<ColumnMetadata>(), Array.Empty<string>()) { Database = "ITS" }],
                [], [], ["ITS"], []);
            ObjectDescriptionStore.SaveDescription("table", "ITS", "dbo", "PDApplicationReference", "đơn trình văn");
            using var args = JsonDocument.Parse("{\"query\":\"đơn trình văn\"}");

            var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, args.RootElement, metadata);

            json.Should().Contain("PDApplicationReference");
            using var result = JsonDocument.Parse(json);
            result.RootElement.GetProperty("matches")[0].GetProperty("description").GetString().Should().StartWith("đơn trình văn");
            result.RootElement.GetProperty("matches")[0].TryGetProperty("customDescription", out _).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", previous);
            DeleteDirectoryWithRetry(tempFolder);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_SearchObjects_FindsVietnameseDescriptionFromNaturalLanguageQuestion()
    {
        var previous = Environment.GetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA");
        var tempFolder = Path.Combine(Path.GetTempPath(), "mssql-intellisense-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", tempFolder);
        try
        {
            var metadata = new DatabaseMetadata(
                [new TableMetadata("dbo", "PDApplicationReference", Array.Empty<ColumnMetadata>(), Array.Empty<string>()) { Database = "ITS" }],
                [], [], ["ITS"], []);
            ObjectDescriptionStore.SaveDescription("table", "ITS", "dbo", "PDApplicationReference", "đơn trình văn");
            using var args = JsonDocument.Parse("{\"query\":\"TÌM CHO TÔI NHỮNG BẢNG LIÊN QUAN ĐẾN ĐƠN TRÌNH VĂN\"}");

            var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, args.RootElement, metadata);

            json.Should().Contain("PDApplicationReference");
            using var result = JsonDocument.Parse(json);
            result.RootElement.GetProperty("matches")[0].GetProperty("description").GetString().Should().Be("đơn trình văn");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", previous);
            DeleteDirectoryWithRetry(tempFolder);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_SearchObjects_UsesDatabaseObjectDescription()
    {
        var metadata = new DatabaseMetadata(
            [new TableMetadata("dbo", "Invoices", Array.Empty<ColumnMetadata>(), Array.Empty<string>())
            { Database = "TestDb", ExtendedDescription = "Financial billing documents used for payment reconciliation." }],
            [], [], ["TestDb"], []);
        using var args = JsonDocument.Parse("{\"query\":\"payment reconciliation\"}");

        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, args.RootElement, metadata);

        json.Should().Contain("Invoices");
        json.Should().Contain("Financial billing documents used for payment reconciliation.");
    }

    [Fact]
    public async Task ExecuteToolAsync_SearchObjects_UsesProcedureAndFunctionDescriptions()
    {
        var metadata = new DatabaseMetadata([], [], [], ["TestDb"], [])
        {
            Procedures =
            [
                new ProcedureMetadata("dbo", "ReconcilePayment")
                {
                    Database = "TestDb",
                    ExtendedDescription = "Reconciles settled payment batches with financial ledger entries."
                }
            ],
            Functions =
            [
                new FunctionMetadata("dbo", "NormalizeCustomerIdentity")
                {
                    Database = "TestDb",
                    ExtendedDescription = "Normalizes customer identity values before matching duplicate records."
                }
            ]
        };
        using var procedureArgs = JsonDocument.Parse("{\"query\":\"payment batches\"}");
        using var functionArgs = JsonDocument.Parse("{\"query\":\"duplicate records\"}");

        var procedureJson = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, procedureArgs.RootElement, metadata);
        var functionJson = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, functionArgs.RootElement, metadata);

        procedureJson.Should().Contain("ReconcilePayment").And.Contain("financial ledger entries");
        functionJson.Should().Contain("NormalizeCustomerIdentity").And.Contain("duplicate records");
    }

    [Fact]
    public async Task ExecuteToolAsync_SearchObjects_UsesLexicalDescriptionSearch()
    {
        var metadata = new DatabaseMetadata([], [], [], ["TestDb"], [])
        {
            Procedures =
            [
                new ProcedureMetadata("dbo", "GetAuthenticatedUser")
                {
                    Database = "TestDb",
                    ExtendedDescription = "Returns the application authentication profile for a user."
                }
            ]
        };
        using var args = JsonDocument.Parse("{\"query\":\"authentication profile\"}");

        var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.SearchObjectsToolName, args.RootElement, metadata);

        json.Should().Contain("GetAuthenticatedUser");
        json.Should().Contain("\"lexicalScore\"");
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
    public async Task ExecuteToolAsync_FindColumn_SearchesColumnGuidance()
    {
        var previous = Environment.GetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA");
        var tempFolder = Path.Combine(Path.GetTempPath(), "mssql-intellisense-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", tempFolder);
        try
        {
            var objectKey = ObjectDescriptionStore.BuildKey("table", "TestDb", "dbo", "Users");
            ObjectDescriptionStore.SaveDescription(
                ObjectDescriptionStore.BuildColumnKey(objectKey, "Email"),
                "Primary login email for password recovery.");
            using var args = JsonDocument.Parse("{\"query\":\"password recovery\"}");

            var json = await SqlMetadataToolExecutor.ExecuteToolAsync(SqlMetadataToolExecutor.FindColumnToolName, args.RootElement, TestMetadata.Create());

            json.Should().Contain("Email");
            json.Should().Contain("Primary login email for password recovery.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_INTELLISENSE_APPDATA", previous);
            DeleteDirectoryWithRetry(tempFolder);
        }
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
