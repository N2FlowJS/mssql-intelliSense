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
}
