using FluentAssertions;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

public sealed class SqlCompletionProviderTests
{
    private readonly SqlCompletionProvider _provider = new();

    [Fact]
    public void GetCompletions_AfterFromSuggestsQualifiedTablesAndSchemas()
    {
        var items = _provider.GetCompletions("SELECT * FROM Us", "SELECT * FROM Us".Length, TestMetadata.Create());

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table &&
                                      item.InsertText == "[dbo].[Users]");
    }

    [Fact]
    public void GetCompletions_AfterSchemaDotSuggestsTablesInThatSchema()
    {
        var sql = "SELECT * FROM sales.Or";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Table)
            .Which.InsertText.Should().Be("[Orders]");
    }

    [Fact]
    public void GetCompletions_AfterQuotedSchemaDotSuggestsTablesInThatSchema()
    {
        var sql = "SELECT * FROM [sales].";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[Orders]");
    }

    [Fact]
    public void GetCompletions_AfterAliasDotSuggestsColumnsWithTypes()
    {
        var sql = "SELECT u.Na FROM dbo.Users AS u";
        var caret = sql.IndexOf("Na", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Column)
            .Which.Should().Match<SqlCompletionItem>(item =>
                item.InsertText == "[Name]" && item.Description.Contains("nvarchar"));
    }

    [Fact]
    public void GetCompletions_AfterDotWithEmptyPrefixSuggestsAllColumns()
    {
        var sql = "SELECT u. FROM dbo.Users AS u";
        var caret = sql.IndexOf("u.", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().HaveCount(2);
        items.Should().Contain(item => item.InsertText == "[Id]" && item.Kind == SqlCompletionKind.Column);
        items.Should().Contain(item => item.InsertText == "[Name]" && item.Kind == SqlCompletionKind.Column);
    }

    [Fact]
    public void GetCompletions_QualifiesAmbiguousVisibleColumns()
    {
        var sql = "SELECT I FROM dbo.Users u JOIN sales.Orders o ON o.UserId = u.Id";
        var caret = sql.IndexOf("I FROM", StringComparison.Ordinal) + 1;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Where(item => item.Kind == SqlCompletionKind.Column && item.Label.EndsWith(".Id"))
            .Select(item => item.InsertText)
            .Should().BeEquivalentTo("[u].[Id]", "[o].[Id]");
    }

    [Fact]
    public void GetCompletions_WithoutMetadataStillSuggestsKeywords()
    {
        var items = _provider.GetCompletions("SEL", 3);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Keyword && item.InsertText == "SELECT");
    }

    [Fact]
    public void GetCompletions_RejectsInvalidCaretPosition()
    {
        var action = () => _provider.GetCompletions("SELECT", 99);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetCompletions_CleansSqlCommentsAndStrings()
    {
        var sql = """
            -- this is a commented out FROM clause: FROM dbo.OldTable
            /* block comment FROM dbo.BlockedTable */
            SELECT u.Na FROM dbo.Users AS u
            """;
        var caret = sql.IndexOf("u.Na", StringComparison.Ordinal) + 4;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Column)
            .Which.InsertText.Should().Be("[Name]");
    }

    [Fact]
    public void GetCompletions_DottedDatabaseAndSchemaCompletions()
    {
        var metadata = new DatabaseMetadata(
            [
                new TableMetadata("dbo", "Users", [new("Id", "int", false, 1)], ["Id"])
            ],
            [], [], ["MyDb"], [new LinkedServerInfo("MyServer", "")]
        );

        // 1. After empty table context, suggest servers, databases, schemas, tables
        var sql1 = "SELECT * FROM ";
        var items1 = _provider.GetCompletions(sql1, sql1.Length, metadata);
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.LinkedServer && item.InsertText == "[MyServer].");
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.Database && item.InsertText == "[MyDb].");
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.Schema && item.InsertText == "[dbo].");

        // 2. Under server context, suggest databases
        var sql2 = "SELECT * FROM MyServer.";
        var items2 = _provider.GetCompletions(sql2, sql2.Length, metadata);
        items2.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Database && item.InsertText == "[MyDb].");

        // 3. Under database context, suggest schemas
        var sql3 = "SELECT * FROM MyDb.";
        var items3 = _provider.GetCompletions(sql3, sql3.Length, metadata);
        items3.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Schema && item.InsertText == "[dbo].");
    }

    [Fact]
    public void GetCompletions_JoinContextSuggestsFkBasedJoinClause()
    {
        var sql = "SELECT * FROM dbo.Users u JOIN ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table &&
                                       item.Label == "sales.Orders ON [o].[UserId] = [u].[Id]" &&
                                       item.InsertText == "[sales].[Orders] AS [o] ON [o].[UserId] = [u].[Id]");
    }

    [Fact]
    public void GetCompletions_JoinContextSuggestsTransitiveJoinClauses()
    {
        var metadata = new DatabaseMetadata(
            [
                new TableMetadata("dbo", "Users", [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2)], ["Id"]),
                new TableMetadata("sales", "Orders", [new("Id", "int", false, 1), new("UserId", "int", false, 2)], ["Id"]),
                new TableMetadata("sales", "OrderDetails", [new("Id", "int", false, 1), new("OrderId", "int", false, 2), new("ProductId", "int", false, 3)], ["Id"])
            ],
            [
                new ForeignKeyMetadata("FK_Orders_Users", "sales", "Orders", "UserId", "dbo", "Users", "Id", 1),
                new ForeignKeyMetadata("FK_OrderDetails_Orders", "sales", "OrderDetails", "OrderId", "sales", "Orders", "Id", 1)
            ],
            [], [], []
        );

        var sql = "SELECT * FROM dbo.Users u JOIN ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        // 1. Check depth-1 join suggestion (to Orders)
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table &&
                                       item.Label == "sales.Orders ON [o].[UserId] = [u].[Id]" &&
                                       item.InsertText == "[sales].[Orders] AS [o] ON [o].[UserId] = [u].[Id]");

        // 2. Check depth-2 join suggestion (to OrderDetails via Orders)
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table &&
                                       item.Label == "sales.OrderDetails (via Orders) ON [od].[OrderId] = [o].[Id]" &&
                                       item.InsertText == "[sales].[Orders] AS [o] ON [o].[UserId] = [u].[Id] JOIN [sales].[OrderDetails] AS [od] ON [od].[OrderId] = [o].[Id]");
    }

    [Fact]
    public void GetCompletions_ColumnCompletionsUnderSchemaQualifiedTables()
    {
        var sql = "SELECT dbo.Users. FROM dbo.Users";
        var caret = sql.IndexOf("Users.", StringComparison.Ordinal) + 6;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Name]");
    }

    [Fact]
    public void GetCompletions_ExpandedKeywords()
    {
        // Function keywords get auto-parentheses in expression context
        var sql = "COA";
        var items = _provider.GetCompletions(sql, sql.Length);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Keyword && item.InsertText == "COALESCE()");

        // Non-function keywords (types) don't get parentheses
        var sql2 = "NVAR";
        var items2 = _provider.GetCompletions(sql2, sql2.Length);

        items2.Should().Contain(item => item.Kind == SqlCompletionKind.Keyword && item.InsertText == "NVARCHAR");
    }

    [Fact]
    public void GetCompletions_FunctionKeywordsGetAutoParentheses()
    {
        var sql = "COA";
        var items = _provider.GetCompletions(sql, sql.Length);

        items.Should().Contain(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.InsertText == "COALESCE()" &&
            item.CaretOffset == 9);
    }

    [Fact]
    public void GetCompletions_TypeContextDoesNotAddFunctionParentheses()
    {
        // DECLARE context: types added without parentheses
        var sql = "DECLARE @x ";
        var items = _provider.GetCompletions(sql, sql.Length);

        // NVARCHAR appears as a BaseType (not Keyword), without parentheses
        items.Should().Contain(item =>
            item.Kind == SqlCompletionKind.BaseType &&
            item.InsertText == "NVARCHAR" &&
            item.CaretOffset == -1);
        // COALESCE should not appear here since it's not a type context
        items.Should().NotContain(item =>
            item.Label == "COALESCE");
    }

    [Fact]
    public void GetCompletions_AlterTableAndAlterViewSuggestsTablesAndViews()
    {
        var sql1 = "ALTER TABLE ";
        var items1 = _provider.GetCompletions(sql1, sql1.Length, TestMetadata.Create());
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[dbo].[Users]");

        var sql2 = "ALTER VIEW ";
        var items2 = _provider.GetCompletions(sql2, sql2.Length, TestMetadata.Create());
        items2.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[sales].[Orders]");
    }

    [Fact]
    public void GetCompletions_ExecAndAlterProcSuggestsProcedures()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Procedures = [new ProcedureMetadata("dbo", "GetUserData")]
        };

        var sql1 = "EXEC ";
        var items1 = _provider.GetCompletions(sql1, sql1.Length, metadata);
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.Procedure &&
                                       item.Label == "dbo.GetUserData" &&
                                       item.InsertText == "[dbo].[GetUserData]");

        var sql2 = "ALTER PROCEDURE dbo.";
        var items2 = _provider.GetCompletions(sql2, sql2.Length, metadata);
        items2.Should().Contain(item => item.Kind == SqlCompletionKind.Procedure &&
                                       item.Label == "GetUserData" &&
                                       item.InsertText == "[GetUserData]");
    }

    [Fact]
    public void GetCompletions_IncludesDynamicKeywords()
    {
        var items = _provider.GetCompletions("PIV", 3);
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Keyword && item.InsertText == "PIVOT");

        var items2 = _provider.GetCompletions("UNPIV", 5);
        items2.Should().Contain(item => item.Kind == SqlCompletionKind.Keyword && item.InsertText == "UNPIVOT");
    }

    [Fact]
    public void GetCompletions_TruncateTableSuggestsTables()
    {
        var sql = "TRUNCATE TABLE ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[dbo].[Users]");
    }

    [Fact]
    public void GetCompletions_InsertAndDeleteSuggestsTables()
    {
        var sql1 = "INSERT ";
        var items1 = _provider.GetCompletions(sql1, sql1.Length, TestMetadata.Create());
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[dbo].[Users]");

        var sql2 = "DELETE ";
        var items2 = _provider.GetCompletions(sql2, sql2.Length, TestMetadata.Create());
        items2.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[dbo].[Users]");
    }

    [Fact]
    public void GetCompletions_CreateIndexOnSuggestsTables()
    {
        var sql = "CREATE UNIQUE NONCLUSTERED INDEX idx_name ON ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[dbo].[Users]");
    }

    [Fact]
    public void GetCompletions_TokenStreamFallbackExtractorHandlesCommaSeparatedTables()
    {
        // Syntax-invalid SQL because of trailing SELECT, but lexer works.
        // It should extract both 'u' (Users) and 'o' (Orders) as sources from the token stream.
        var sqlWithDot = "SELECT u.Id, o.Id FROM dbo.Users u, sales.Orders o SELECT u.";
        var itemsWithDot = _provider.GetCompletions(sqlWithDot, sqlWithDot.Length, TestMetadata.Create());
        itemsWithDot.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Name]");
    }

    [Fact]
    public void GetCompletions_FromSuggestsViewsSynonymsAndFunctions()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Views = [new ViewMetadata("dbo", "MyView", [])],
            Synonyms = [new SynonymMetadata("dbo", "MySynonym", "dbo.TargetTable")],
            Functions = [new FunctionMetadata("dbo", "MyTableFunction") { FunctionType = "TF" }]
        };

        var sql = "SELECT * FROM ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.View && item.Label == "dbo.MyView");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Synonym && item.Label == "dbo.MySynonym");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Function && item.Label == "dbo.MyTableFunction");
    }

    [Fact]
    public void GetCompletions_TypeContextSuggestsBaseTypesAndUserTypes()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            UserTypes = [new UserTypeMetadata("dbo", "MyCustomType") { BaseType = "varchar" }]
        };

        // 1. After DECLARE @variable
        var sql1 = "DECLARE @myVar ";
        var items1 = _provider.GetCompletions(sql1, sql1.Length, metadata);
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.BaseType && item.InsertText == "VARCHAR");
        items1.Should().Contain(item => item.Kind == SqlCompletionKind.UserType && item.Label == "dbo.MyCustomType");

        // 2. After CAST(x AS
        var sql2 = "SELECT CAST(id AS ";
        var items2 = _provider.GetCompletions(sql2, sql2.Length, metadata);
        items2.Should().Contain(item => item.Kind == SqlCompletionKind.BaseType && item.InsertText == "INT");
        items2.Should().Contain(item => item.Kind == SqlCompletionKind.UserType && item.Label == "dbo.MyCustomType");
    }

    [Fact]
    public void GetCompletions_SchemaDotSuggestsViewsSynonymsAndFunctions()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Views = [new ViewMetadata("dbo", "MyView", [])],
            Synonyms = [new SynonymMetadata("dbo", "MySynonym", "dbo.TargetTable")],
            Functions = [new FunctionMetadata("dbo", "MyTableFunction") { FunctionType = "TF" }],
            Procedures = [new ProcedureMetadata("dbo", "MyProcedure")]
        };

        var sql = "SELECT * FROM dbo.";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.View && item.Label == "MyView");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Synonym && item.Label == "MySynonym");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Function && item.Label == "MyTableFunction");
    }

    [Fact]
    public void GetCompletions_TableFunctionSuggestsWithParameters()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Functions = [
                new FunctionMetadata("dbo", "MyTvftNoParams") { FunctionType = "TF" },
                new FunctionMetadata("dbo", "MyTvftWithParams")
                {
                    FunctionType = "TF",
                    Parameters = [
                        new FunctionParameterMetadata("id", "int", false, 1),
                        new FunctionParameterMetadata("name", "nvarchar", false, 2)
                    ]
                }
            ]
        };

        // 1. Unqualified
        var sql1 = "SELECT * FROM ";
        var items1 = _provider.GetCompletions(sql1, sql1.Length, metadata);
        
        var noParamsItem1 = items1.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Function && item.Label == "dbo.MyTvftNoParams").Which;
        noParamsItem1.InsertText.Should().Be("[dbo].[MyTvftNoParams]()");
        noParamsItem1.CaretOffset.Should().Be("[dbo].[MyTvftNoParams]".Length + 1);

        var withParamsItem1 = items1.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Function && item.Label == "dbo.MyTvftWithParams").Which;
        withParamsItem1.InsertText.Should().Be("[dbo].[MyTvftWithParams](@id, @name)");
        withParamsItem1.CaretOffset.Should().Be("[dbo].[MyTvftWithParams]".Length + 1);

        // 2. Qualified
        var sql2 = "SELECT * FROM dbo.";
        var items2 = _provider.GetCompletions(sql2, sql2.Length, metadata);
        
        var noParamsItem2 = items2.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Function && item.Label == "MyTvftNoParams").Which;
        noParamsItem2.InsertText.Should().Be("[MyTvftNoParams]()");
        noParamsItem2.CaretOffset.Should().Be("[MyTvftNoParams]".Length + 1);

        var withParamsItem2 = items2.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Function && item.Label == "MyTvftWithParams").Which;
        withParamsItem2.InsertText.Should().Be("[MyTvftWithParams](@id, @name)");
        withParamsItem2.CaretOffset.Should().Be("[MyTvftWithParams]".Length + 1);
    }

    [Fact]
    public void GetCompletions_InsertIntoSuggestsTargetTableColumnsOnly()
    {
        var metadata = TestMetadata.Create();

        // Target table is dbo.Users, columns are Id and Name
        var sql = "INSERT INTO dbo.Users (N";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        // Should suggest 'Name' of Users, but NOT 'UserId' or 'Total' of sales.Orders
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Name]");
        items.Should().NotContain(item => item.InsertText == "[UserId]" || item.InsertText == "[Total]");
    }

    [Fact]
    public void GetCompletions_UpdateSetSuggestsTargetTableColumnsOnly()
    {
        var metadata = TestMetadata.Create();

        // Target table is sales.Orders, columns are Id, UserId, Total
        var sql = "UPDATE sales.Orders SET T";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        // Should suggest 'Total' of Orders, but NOT 'Name' of Users
        items.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Total]");
        items.Should().NotContain(item => item.InsertText == "[Name]");
    }

    [Fact]
    public void GetCompletions_MergeContextSuggestsTables()
    {
        var metadata = TestMetadata.Create();

        var sql = "MERGE ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[dbo].[Users]");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[sales].[Orders]");
    }

    [Fact]
    public void GetCompletions_UsingContextSuggestsTables()
    {
        var metadata = TestMetadata.Create();

        var sql = "MERGE INTO dbo.Users AS t USING ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[dbo].[Users]");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.InsertText == "[sales].[Orders]");
    }

    [Fact]
    public void GetCompletions_OutputContextSuggestsInsertedAndDeleted()
    {
        var metadata = TestMetadata.Create();

        var sql = "INSERT INTO dbo.Users (Name) OUTPUT ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.InsertText == "INSERTED" && item.Kind == SqlCompletionKind.Keyword);
        items.Should().Contain(item => item.InsertText == "DELETED" && item.Kind == SqlCompletionKind.Keyword);
    }

    [Fact]
    public void GetCompletions_OutputInsertedDotSuggestsTargetColumns()
    {
        var metadata = TestMetadata.Create();

        // Users table has Id and Name
        var sql = "INSERT INTO dbo.Users (Name) OUTPUT INSERTED.";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Id]");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Name]");
        // Should NOT suggest Orders columns
        items.Should().NotContain(item => item.InsertText == "[UserId]");
        items.Should().NotContain(item => item.InsertText == "[Total]");
    }

    [Fact]
    public void GetCompletions_OutputDeletedDotSuggestsTargetColumns()
    {
        var metadata = TestMetadata.Create();

        // Orders table has Id, UserId, Total
        var sql = "DELETE FROM sales.Orders OUTPUT DELETED.";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Id]");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[UserId]");
        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Total]");
    }

    [Fact]
    public void GetCompletions_WordBoundaryMatchSuggestsMatchingNames()
    {
        var metadata = new DatabaseMetadata(
            [new TableMetadata("dbo", "FirstName", [new("Id", "int", false, 1)], ["Id"])],
            [], [], [], []);

        // "fn" should match "FirstName" via camelCase word-boundary
        var sql = "SELECT * FROM fn";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.Label == "dbo.FirstName");
    }

    [Fact]
    public void GetCompletions_SubstringMatchIncludesNonBoundary()
    {
        var metadata = new DatabaseMetadata(
            [new TableMetadata("dbo", "CategoryName", [new("Id", "int", false, 1)], ["Id"])],
            [], [], [], []);

        // "at" matches "CategoryName" via substring (not word-boundary)
        var sql = "SELECT * FROM at";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Table && item.Label == "dbo.CategoryName");
    }

    [Fact]
    public void GetCompletions_NonMatchingPrefixExcludesAll()
    {
        var metadata = new DatabaseMetadata(
            [new TableMetadata("dbo", "CategoryName", [new("Id", "int", false, 1)], ["Id"])],
            [], [], [], []);

        // "zz" matches nothing (not prefix, not word-boundary, not substring)
        var sql = "SELECT * FROM zz";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().NotContain(item => item.Kind == SqlCompletionKind.Table && item.Label == "dbo.CategoryName");
    }

    [Fact]
    public void GetCompletions_WordBoundaryMatchColumns()
    {
        var metadata = new DatabaseMetadata(
            [new TableMetadata("dbo", "Users",
                [new("Id", "int", false, 1), new("FullName", "nvarchar", false, 2), new("DateOfBirth", "date", false, 3)],
                ["Id"])],
            [], [], [], []);

        // "fn" should match "FullName" but not "DateOfBirth"
        var sql = "SELECT fn FROM dbo.Users";
        var caret = sql.IndexOf("fn", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, metadata);

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[FullName]");
        items.Should().NotContain(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[DateOfBirth]");
    }

    [Fact]
    public void GetCompletions_InsertIntoSuggestsTableWithColumnList()
    {
        var metadata = TestMetadata.Create();

        // After INSERT INTO, suggest tables. When selecting Orders, include column list for INSERT body.
        var sql = "INSERT INTO ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var orderItem = items.FirstOrDefault(item => item.Label == "sales.Orders");
        orderItem.Should().NotBeNull();
        orderItem!.InsertText.Should().Contain("([Id], [UserId], [Total])");
        orderItem.InsertText.Should().Contain("VALUES (0, 0, 0)");
    }

    [Fact]
    public void GetCompletions_InsertIntoSchemaQualifiedSuggestsTableWithColumnList()
    {
        var metadata = TestMetadata.Create();

        // After INSERT INTO dbo., suggest tables in dbo schema with column list
        var sql = "INSERT INTO dbo.";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var userItem = items.FirstOrDefault(item => item.Label == "Users");
        userItem.Should().NotBeNull();
        userItem!.InsertText.Should().Be("[Users]");
    }

    [Fact]
    public void GetCompletions_ExecSuggestsProcedureWithParameters()
    {
        var metadata = TestMetadata.Create();

        // When schema is typed (dbo. prefix already present), only proc name is inserted
        var sql = "EXEC dbo.Get";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var procItem = items.FirstOrDefault(item => item.Kind == SqlCompletionKind.Procedure);
        procItem.Should().NotBeNull();
        procItem!.InsertText.Should().Be("[GetUser](@UserId = ?, @IncludeInactive = ?)");
        procItem.CaretOffset.Should().Be("[GetUser](".Length);
    }

    [Fact]
    public void GetCompletions_ExecNoParamsProcedure()
    {
        var metadata = TestMetadata.Create();

        var sql = "EXEC dbo.No";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var procItem = items.FirstOrDefault(item => item.Kind == SqlCompletionKind.Procedure);
        procItem.Should().NotBeNull();
        procItem!.InsertText.Should().Be("[NoParamsProc]");
        procItem.CaretOffset.Should().Be(-1);
    }

    [Fact]
    public void GetCompletions_ExecUnqualifiedSuggestsProcedureWithParameters()
    {
        var metadata = TestMetadata.Create();

        var sql = "EXEC Get";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var procItem = items.FirstOrDefault(item => item.Kind == SqlCompletionKind.Procedure);
        procItem.Should().NotBeNull();
        procItem!.InsertText.Should().Be("[dbo].[GetUser](@UserId = ?, @IncludeInactive = ?)");
        procItem.CaretOffset.Should().Be("[dbo].[GetUser](".Length);
    }

    [Fact]
    public void GetCompletions_ExecSchemaQualifiedSuggestsProcedureWithParameters()
    {
        var metadata = TestMetadata.Create();

        var sql = "EXEC [dbo].";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var procItem = items.FirstOrDefault(item => item.Kind == SqlCompletionKind.Procedure);
        procItem.Should().NotBeNull();
        procItem!.InsertText.Should().Be("[GetUser](@UserId = ?, @IncludeInactive = ?)");
        procItem.CaretOffset.Should().Be("[GetUser](".Length);
    }
}

