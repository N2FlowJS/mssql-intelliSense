using FluentAssertions;
using MssqlIntelliSense.Core.Analysis;

namespace MssqlIntelliSense.Core.Tests;

public sealed class FeatureTests
{
    [Fact]
    public void Analyze_FindsUnsafeStatementsAndStar()
    {
        var warnings = new DangerousSqlAnalyzer().Analyze("DELETE FROM dbo.Users; UPDATE dbo.Users SET Name='x'; SELECT * FROM dbo.Users;");
        warnings.Select(item => item.Code).Should().Contain(["DELETE_WITHOUT_WHERE", "UPDATE_WITHOUT_WHERE", "SELECT_STAR"]);
    }
}
