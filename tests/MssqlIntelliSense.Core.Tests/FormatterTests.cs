using FluentAssertions;
using MssqlIntelliSense.Core.Formatting;

namespace MssqlIntelliSense.Core.Tests;

public sealed class FormatterTests
{
    [Fact]
    public void Format_UppercasesKeywordsAndPlacesClausesOnLines()
    {
        var formatted = new SqlFormatter().Format("select id, name from dbo.Users where id = 1 order by name");
        formatted.Should().Contain("SELECT");
        formatted.Should().Contain("\nFROM");
        formatted.Should().Contain("\nWHERE");
        formatted.Should().Contain("\nORDER BY");
        formatted.Should().MatchRegex("id,?\\r?\\n\\s+name");
    }

    [Fact]
    public void Format_InvalidSql_ThrowsWithParseErrors()
    {
        var action = () => new SqlFormatter().Format("SELECT FROM");
        action.Should().Throw<SqlFormattingException>().Which.Errors.Should().NotBeEmpty();
    }
}
