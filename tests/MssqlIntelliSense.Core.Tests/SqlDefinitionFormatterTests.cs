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

    [Fact]
    public void FormatTableDefinition_IncludesForeignKeysAndIndexesWhenAvailable()
    {
        var table = new TableMetadata(
            "sales",
            "Orders",
            new[]
            {
                new ColumnMetadata("Id", "int", false, 1),
                new ColumnMetadata("UserId", "int", false, 2),
                new ColumnMetadata("OrderCode", "nvarchar", true, 3)
            },
            new[] { "Id" })
        {
            Database = "ShopDb"
        };

        var foreignKeys = new[]
        {
            new ForeignKeyMetadata("FK_Orders_Users", "sales", "Orders", "UserId", "dbo", "Users", "Id", 1)
            {
                Database = "ShopDb"
            }
        };
        var indexes = new[]
        {
            new IndexMetadata("sales", "Orders", "PK_Orders", true, new[] { "Id" }) { Database = "ShopDb" },
            new IndexMetadata("sales", "Orders", "IX_Orders_UserId", false, new[] { "UserId" }) { Database = "ShopDb" },
            new IndexMetadata("sales", "Orders", "UX_Orders_OrderCode", true, new[] { "OrderCode" }) { Database = "ShopDb" }
        };

        var definition = SqlDefinitionFormatter.FormatTableDefinition(table, foreignKeys, indexes);

        definition.Should().Contain("CONSTRAINT [FK_Orders_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([Id])");
        definition.Should().Contain("CREATE INDEX [IX_Orders_UserId] ON [sales].[Orders] ([UserId]);");
        definition.Should().Contain("CREATE UNIQUE INDEX [UX_Orders_OrderCode] ON [sales].[Orders] ([OrderCode]);");
        definition.Should().NotContain("CREATE UNIQUE INDEX [PK_Orders]");
    }
}
