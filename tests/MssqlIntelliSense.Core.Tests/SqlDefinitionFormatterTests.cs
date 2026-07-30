using FluentAssertions;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

public sealed class SqlDefinitionFormatterTests
{
    [Fact]
    public void FormatViewDefinition_UsesSavedSqlDefinitionWhenAvailable()
    {
        var view = new ViewMetadata("dbo", "vw_Orders", Array.Empty<ColumnMetadata>())
        {
            Definition = "CREATE VIEW [dbo].[vw_Orders]\r\nAS\r\nSELECT OrderId FROM dbo.Orders"
        };

        SqlDefinitionFormatter.FormatViewDefinition(view)
            .Should()
            .Be("CREATE VIEW [dbo].[vw_Orders]\r\nAS\r\nSELECT OrderId FROM dbo.Orders");
    }

    [Fact]
    public void FormatProcedureDefinition_UsesSavedSqlDefinitionWhenAvailable()
    {
        var procedure = new ProcedureMetadata("dbo", "usp_LoadOrders")
        {
            Definition = "CREATE PROCEDURE [dbo].[usp_LoadOrders]\r\nAS\r\nBEGIN\r\n    SELECT 1;\r\nEND"
        };

        SqlDefinitionFormatter.FormatProcedureDefinition(procedure)
            .Should()
            .Contain("SELECT 1;");
    }

    [Fact]
    public void FormatFunctionDefinition_UsesSavedSqlDefinitionWhenAvailable()
    {
        var function = new FunctionMetadata("dbo", "fn_IsOpen")
        {
            Definition = "CREATE FUNCTION [dbo].[fn_IsOpen]()\r\nRETURNS bit\r\nAS\r\nBEGIN\r\n    RETURN 1;\r\nEND"
        };

        SqlDefinitionFormatter.FormatFunctionDefinition(function)
            .Should()
            .Contain("RETURNS bit");
    }
}
