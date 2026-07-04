using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

internal static class TestMetadata
{
    public static DatabaseMetadata Create() => new(
        [
            new TableMetadata("dbo", "Users",
                [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2)], ["Id"]),
            new TableMetadata("sales", "Orders",
                [new("Id", "int", false, 1), new("UserId", "int", false, 2), new("Total", "decimal", false, 3)], ["Id"])
        ],
        [new("FK_Orders_Users", "sales", "Orders", "UserId", "dbo", "Users", "Id", 1)],
        [], [], [])
    {
        Procedures =
        [
            new ProcedureMetadata("dbo", "GetUser")
            {
                Parameters =
                [
                    new FunctionParameterMetadata("@UserId", "int", false, 1),
                    new FunctionParameterMetadata("@IncludeInactive", "bit", false, 2),
                ]
            },
            new ProcedureMetadata("dbo", "NoParamsProc"),
        ]
    };
}
