using System.Reflection;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

public sealed class SqlServerMetadataProviderTests
{
    [Fact]
    public void Constructor_RejectsInvalidCommandTimeout()
    {
        var action = () => new SqlServerMetadataProvider("Server=.;Database=master", commandTimeoutSeconds: 0);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("commandTimeoutSeconds");
    }

    [Fact]
    public void CreateCommand_AppliesConfiguredCommandTimeout()
    {
        var provider = new SqlServerMetadataProvider("Server=.;Database=master", commandTimeoutSeconds: 3);
        using var connection = new SqlConnection("Server=.;Database=master");

        var method = typeof(SqlServerMetadataProvider).GetMethod(
            "CreateCommand",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        using var command = (SqlCommand)method!.Invoke(provider, [connection, "SELECT 1"])!;

        command.CommandText.Should().Be("SELECT 1");
        command.CommandTimeout.Should().Be(3);
    }

    [Fact]
    public void QuoteSqlIdentifier_EscapesClosingBracket()
    {
        var method = typeof(SqlServerMetadataProvider).GetMethod(
            "QuoteSqlIdentifier",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var quoted = (string)method!.Invoke(null, ["Tenant]Db"])!;

        quoted.Should().Be("[Tenant]]Db]");
    }

    [Fact]
    public void DatabaseDiscoverySql_FiltersDatabasesWithoutAccess()
    {
        var field = typeof(SqlServerMetadataProvider).GetField(
            "DatabaseDiscoverySql",
            BindingFlags.Static | BindingFlags.NonPublic);

        field.Should().NotBeNull();
        var sql = (string)field!.GetValue(null)!;

        sql.Should().Contain("HAS_DBACCESS(name) = 1");
    }
}
