using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

internal static class TestMetadata
{
    public static DatabaseMetadata Create() => new(
        [
            new TableMetadata("dbo", "Users",
                [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2), new("Email", "nvarchar", true, 3)], ["Id"])
                { Database = "TestDb" },
            new TableMetadata("sales", "Orders",
                [new("Id", "int", false, 1), new("UserId", "int", false, 2), new("Total", "decimal", false, 3)], ["Id"])
                { Database = "TestDb" }
        ],
        [new("FK_Orders_Users", "sales", "Orders", "UserId", "dbo", "Users", "Id", 1)],
        [new("dbo", "Users", "IX_Users_Email", true, ["Email"]) { Database = "TestDb" }],
        ["TestDb"],
        [])
    {
        Views =
        [
            new ViewMetadata("dbo", "ActiveUsers", [new("Id", "int", false, 1), new("Email", "nvarchar", true, 2)])
            {
                Database = "TestDb",
                Definition = "CREATE VIEW [dbo].[ActiveUsers]\nAS\nSELECT Id, Email FROM dbo.Users WHERE IsActive = 1"
            }
        ],
        Procedures =
        [
            new ProcedureMetadata("dbo", "GetUser")
            {
                Database = "TestDb",
                ExtendedDescription = "Returns a user profile for application authentication.",
                Definition = "CREATE PROCEDURE [dbo].[GetUser]\n    @UserId int,\n    @IncludeInactive bit\nAS\nSELECT * FROM dbo.Users",
                Parameters =
                [
                    new FunctionParameterMetadata("@UserId", "int", false, 1),
                    new FunctionParameterMetadata("@IncludeInactive", "bit", false, 2),
                ]
            },
            new ProcedureMetadata("dbo", "NoParamsProc") { Database = "TestDb" },
        ],
        Functions =
        [
            new FunctionMetadata("dbo", "NormalizeEmail")
            {
                Database = "TestDb",
                ExtendedDescription = "Normalizes email addresses before identity matching.",
                ReturnType = "nvarchar",
                Definition = "CREATE FUNCTION [dbo].[NormalizeEmail](@value nvarchar(320))\nRETURNS nvarchar(320)\nAS\nBEGIN\n    RETURN LOWER(@value);\nEND"
            }
        ],
        Endpoints =
        [
            new EndpointInfo("TSQL Default TCP", "TSQL", "TCP", "STARTED", 1433)
        ]
    };
}
