using FluentAssertions;
using MssqlIntelliSense.Core.Parsing;

namespace MssqlIntelliSense.Core.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Parse_ValidSql_ReturnsFragmentWithoutErrors()
    {
        var result = new TSqlParserService().Parse("select 1;");
        result.IsValid.Should().BeTrue();
        result.Fragment.Should().NotBeNull();
    }

    [Fact]
    public void Parse_InvalidSql_ReturnsLocation()
    {
        var result = new TSqlParserService().Parse("SELECT\nFROM;");
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Line > 0 && error.Column > 0);
    }
}
