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
    public void GetCompletions_AfterCteAliasDotSuggestsExplicitColumns()
    {
        var sql = "WITH user_cte (Id, Name) AS (SELECT Id, Name FROM dbo.Users) SELECT c.Na FROM user_cte c";
        var caret = sql.IndexOf("c.Na", StringComparison.Ordinal) + 4;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[Name]" &&
            item.Description.Contains("cte.user_cte.Name"));
    }

    [Fact]
    public void GetCompletions_SelectListSuggestsExplicitCteColumns()
    {
        var sql = "WITH user_cte (Id, Name) AS (SELECT Id, Name FROM dbo.Users) SELECT Na FROM user_cte";
        var caret = sql.IndexOf("Na FROM", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "user_cte.Name" &&
            item.InsertText == "[user_cte].[Name]");
    }

    [Fact]
    public void GetCompletions_AfterCteAliasDotSuggestsInferredColumns()
    {
        var sql = "WITH user_cte AS (SELECT Id, Name FROM dbo.Users) SELECT c.Na FROM user_cte c";
        var caret = sql.IndexOf("c.Na", StringComparison.Ordinal) + 4;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[Name]" &&
            item.Description.Contains("cte.user_cte.Name"));
    }

    [Fact]
    public void GetCompletions_CteInferredColumnsUseSelectAliases()
    {
        var sql = "WITH user_cte AS (SELECT Name AS DisplayName FROM dbo.Users) SELECT c.Dis FROM user_cte c";
        var caret = sql.IndexOf("c.Dis", StringComparison.Ordinal) + 5;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[DisplayName]");
    }

    [Fact]
    public void GetCompletions_AfterDerivedTableAliasDotSuggestsInferredColumns()
    {
        var sql = "SELECT d.Na FROM (SELECT Id, Name FROM dbo.Users) d";
        var caret = sql.IndexOf("d.Na", StringComparison.Ordinal) + 4;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[Name]" &&
            item.Description.Contains("derived.d.Name"));
    }

    [Fact]
    public void GetCompletions_DerivedTableInferredColumnsUseSelectAliases()
    {
        var sql = "SELECT d.Dis FROM (SELECT Name AS DisplayName FROM dbo.Users) d";
        var caret = sql.IndexOf("d.Dis", StringComparison.Ordinal) + 5;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[DisplayName]");
    }

    [Fact]
    public void GetCompletions_SelectListSuggestsDerivedTableColumns()
    {
        var sql = "SELECT Na FROM (SELECT Id, Name FROM dbo.Users) d";
        var caret = sql.IndexOf("Na FROM", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().Contain(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "d.Name" &&
            item.InsertText == "[d].[Name]");
    }

    [Fact]
    public void GetCompletions_AfterDotWithEmptyPrefixSuggestsAllColumns()
    {
        var sql = "SELECT u. FROM dbo.Users AS u";
        var caret = sql.IndexOf("u.", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().HaveCount(3);
        items.Should().Contain(item => item.InsertText == "[Id]" && item.Kind == SqlCompletionKind.Column);
        items.Should().Contain(item => item.InsertText == "[Name]" && item.Kind == SqlCompletionKind.Column);
        items.Should().Contain(item => item.InsertText == "[Email]" && item.Kind == SqlCompletionKind.Column);
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
    public void GetCompletions_SelectAtSuggestsDeclaredLocalVariables()
    {
        var sql = "DECLARE @CustomerId int; SELECT @Cu";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Variable &&
            item.Label == "@CustomerId" &&
            item.InsertText == "@CustomerId");
    }

    [Fact]
    public void GetCompletions_ComparisonRhsSuggestsDeclaredLocalVariables()
    {
        var sql = "DECLARE @CustomerId int; SELECT * FROM dbo.Users u WHERE u.Id = @Cu";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Variable &&
            item.InsertText == "@CustomerId");
    }

    [Fact]
    public void GetCompletions_AfterComparisonOperatorSuggestsValueSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Id = ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "value").Which;

        item.InsertText.Should().Be("?");
        item.SelectionStart.Should().Be(0);
        item.SelectionEnd.Should().Be(1);
    }

    [Fact]
    public void GetCompletions_AfterStringColumnComparisonSuggestsStringValueSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Name = ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "string value").Which;

        item.InsertText.Should().Be("N'?'");
        item.SelectionStart.Should().Be("N'".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
        items.Should().Contain(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "value" &&
            item.InsertText == "?");
    }

    [Fact]
    public void GetCompletions_AfterDateColumnComparisonSuggestsDateValueSkeleton()
    {
        var metadata = new DatabaseMetadata(
            [
                new TableMetadata("dbo", "Users",
                    [
                        new("Id", "int", false, 1),
                        new("CreatedDate", "datetime2", false, 2)
                    ],
                    ["Id"])
            ],
            [], [], [], []);
        var sql = "SELECT * FROM dbo.Users u WHERE u.CreatedDate >= ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "date value").Which;

        item.InsertText.Should().Be("'?'");
        item.SelectionStart.Should().Be("'".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_AfterLikeSuggestsSearchPatternSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Name LIKE ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "LIKE pattern").Which;

        item.InsertText.Should().Be("N'%?%'");
        item.SelectionStart.Should().Be("N'%".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_AfterInSuggestsListSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Id IN ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "IN list").Which;

        item.InsertText.Should().Be("(?)");
        item.SelectionStart.Should().Be("(".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_AfterBetweenSuggestsRangeSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Id BETWEEN ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "BETWEEN range").Which;

        item.InsertText.Should().Be("? AND ?");
        item.SelectionStart.Should().Be(0);
        item.SelectionEnd.Should().Be(1);
    }

    [Fact]
    public void GetCompletions_AfterBetweenAndSuggestsEndValueSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Id BETWEEN 1 AND ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "BETWEEN end").Which;

        item.InsertText.Should().Be("?");
        item.SelectionStart.Should().Be(0);
        item.SelectionEnd.Should().Be(1);
        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "BETWEEN range");
    }

    [Fact]
    public void GetCompletions_AfterNotLikeSuggestsSearchPatternSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Name NOT LIKE ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "LIKE pattern" &&
            item.InsertText == "N'%?%'");
    }

    [Fact]
    public void GetCompletions_AfterNotInSuggestsListSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Id NOT IN ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "IN list" &&
            item.InsertText == "(?)");
    }

    [Fact]
    public void GetCompletions_AfterNotKeywordDoesNotEnterComparisonRhsContext()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE NOT I";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().Contain(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.InsertText == "IN");
        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "IN list");
    }

    [Fact]
    public void GetCompletions_DoesNotSuggestUndeclaredLocalVariablesFromPriorUsage()
    {
        var sql = "SELECT @MissingValue; SELECT @Mi";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Variable &&
            item.InsertText == "@MissingValue");
    }

    [Fact]
    public void GetCompletions_FromSuggestsDeclaredTableVariables()
    {
        var sql = "DECLARE @Ids TABLE (Id int); SELECT * FROM @I";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Variable &&
            item.Label == "@Ids" &&
            item.InsertText == "@Ids" &&
            item.Description == "Table variable");
    }

    [Fact]
    public void GetCompletions_JoinSuggestsDeclaredTableVariables()
    {
        var sql = "DECLARE @Ids TABLE (Id int); SELECT * FROM dbo.Users u JOIN @I";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Variable &&
            item.InsertText == "@Ids");
    }

    [Fact]
    public void GetCompletions_FromDoesNotSuggestScalarLocalVariables()
    {
        var sql = "DECLARE @CustomerId int; SELECT * FROM @Cu";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Variable &&
            item.InsertText == "@CustomerId");
    }

    [Fact]
    public void GetCompletions_AfterTableVariableAliasDotSuggestsDeclaredColumns()
    {
        var sql = "DECLARE @Ids TABLE (Id int, Name nvarchar(100)); SELECT i.Na FROM @Ids i";
        var caret = sql.IndexOf("i.Na", StringComparison.Ordinal) + 4;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[Name]" &&
            item.Description.Contains("@.@Ids.Name"));
    }

    [Fact]
    public void GetCompletions_SelectListSuggestsDeclaredTableVariableColumns()
    {
        var sql = "DECLARE @Ids TABLE (Id int, Name nvarchar(100)); SELECT Na FROM @Ids";
        var caret = sql.IndexOf("Na FROM", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[Name]");
    }

    [Fact]
    public void GetCompletions_FromSuggestsCreatedTemporaryTables()
    {
        var sql = "CREATE TABLE #Results (Id int); SELECT * FROM #R";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Table &&
            item.Label == "#Results" &&
            item.InsertText == "#Results" &&
            item.Description == "Temporary table");
    }

    [Fact]
    public void GetCompletions_JoinSuggestsSelectIntoTemporaryTables()
    {
        var sql = "SELECT Id INTO #UserIds FROM dbo.Users; SELECT * FROM dbo.Users u JOIN #U";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Table &&
            item.InsertText == "#UserIds");
    }

    [Fact]
    public void GetCompletions_DoesNotTreatInsertIntoTemporaryTableAsDeclaration()
    {
        var sql = "INSERT INTO #Existing SELECT 1; SELECT * FROM #E";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Table &&
            item.InsertText == "#Existing");
    }

    [Fact]
    public void GetCompletions_AfterTempTableAliasDotSuggestsCreatedColumns()
    {
        var sql = "CREATE TABLE #Results (Id int, Name nvarchar(100)); SELECT r.Na FROM #Results r";
        var caret = sql.IndexOf("r.Na", StringComparison.Ordinal) + 4;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[Name]" &&
            item.Description.Contains("#.#Results.Name"));
    }

    [Fact]
    public void GetCompletions_SelectListSuggestsCreatedTempTableColumns()
    {
        var sql = "CREATE TABLE #Results (Id int, Name nvarchar(100)); SELECT Na FROM #Results";
        var caret = sql.IndexOf("Na FROM", StringComparison.Ordinal) + 2;
        var items = _provider.GetCompletions(sql, caret, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.InsertText == "[Name]");
    }

    [Fact]
    public void GetCompletions_OrderBySuggestsDirectionKeywords()
    {
        var sql = "SELECT * FROM dbo.Users u ORDER BY u.Name DE";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "DESC" &&
            item.InsertText == "DESC");
    }

    [Fact]
    public void GetCompletions_OrderBySuggestsOffsetFetchSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u ORDER BY u.Name OF";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OFFSET FETCH").Which;

        item.InsertText.Should().Be("OFFSET ? ROWS FETCH NEXT ? ROWS ONLY");
        item.SelectionStart.Should().Be("OFFSET ".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_OrderBySuggestsFetchNextSkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u ORDER BY u.Name OFFSET 10 ROWS F";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "FETCH NEXT").Which;

        item.InsertText.Should().Be("FETCH NEXT ? ROWS ONLY");
        item.SelectionStart.Should().Be("FETCH NEXT ".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_GroupByDoesNotSuggestOrderByTailCompletions()
    {
        var sql = "SELECT u.Name, COUNT(*) FROM dbo.Users u GROUP BY u.Name DE";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "DESC" &&
            item.Description == "ORDER BY direction");
        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OFFSET FETCH");
    }

    [Fact]
    public void GetCompletions_OrderBySuggestsSelectAliases()
    {
        var sql = "SELECT u.Name AS DisplayName FROM dbo.Users u ORDER BY Dis";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "DisplayName" &&
            item.InsertText == "[DisplayName]" &&
            item.Description == "SELECT output alias");
    }

    [Fact]
    public void GetCompletions_GroupByDoesNotSuggestSelectAliases()
    {
        var sql = "SELECT u.Name AS DisplayName FROM dbo.Users u GROUP BY Dis";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "DisplayName" &&
            item.Description == "SELECT output alias");
    }

    [Fact]
    public void GetCompletions_GroupBySuggestsNonAggregateSelectColumnsSkeleton()
    {
        var sql = "SELECT u.Name AS DisplayName, u.CreatedDate, COUNT(*) FROM dbo.Users u GROUP BY ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "GROUP BY SELECT columns" &&
            item.InsertText == "u.Name, u.CreatedDate");
    }

    [Fact]
    public void GetCompletions_OrderByDoesNotSuggestGroupBySelectColumnsSkeleton()
    {
        var sql = "SELECT u.Name, COUNT(*) FROM dbo.Users u ORDER BY ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "GROUP BY SELECT columns");
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
    public void GetCompletions_AfterJoinOnSuggestsFkConditionBetweenVisibleAliases()
    {
        var sql = "SELECT * FROM dbo.Users u JOIN sales.Orders o ON ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column &&
                                       item.Label == "o.UserId = u.Id" &&
                                       item.InsertText == "[o].[UserId] = [u].[Id]");
    }

    [Fact]
    public void GetCompletions_AfterWhereSuggestsPredicateSkeletonsForVisibleColumns()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE N";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "u.Name = ?").Which;

        item.InsertText.Should().Be("[u].[Name] = ?");
        item.CaretOffset.Should().Be("[u].[Name] = ".Length);
        item.SelectionStart.Should().Be("[u].[Name] = ".Length);
        item.SelectionEnd.Should().Be("[u].[Name] = ?".Length);
    }

    [Fact]
    public void GetCompletions_AfterWhereSuggestsPredicateOperatorSkeletons()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE N";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var likeItem = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "u.Name LIKE ?").Which;
        likeItem.InsertText.Should().Be("[u].[Name] LIKE ?");
        likeItem.SelectionStart.Should().Be(likeItem.InsertText.IndexOf("?", StringComparison.Ordinal));
        likeItem.SelectionEnd.Should().Be(likeItem.SelectionStart + 1);

        var betweenItem = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "u.Name BETWEEN ? AND ?").Which;
        betweenItem.InsertText.Should().Be("[u].[Name] BETWEEN ? AND ?");
        betweenItem.SelectionStart.Should().Be(betweenItem.InsertText.IndexOf("?", StringComparison.Ordinal));
        betweenItem.SelectionEnd.Should().Be(betweenItem.SelectionStart + 1);

        var inItem = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "u.Name IN (?)").Which;
        inItem.InsertText.Should().Be("[u].[Name] IN (?)");
        inItem.SelectionStart.Should().Be(inItem.InsertText.IndexOf("?", StringComparison.Ordinal));
        inItem.SelectionEnd.Should().Be(inItem.SelectionStart + 1);

        var nullItem = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "u.Name IS NULL").Which;
        nullItem.InsertText.Should().Be("[u].[Name] IS NULL");
        nullItem.SelectionStart.Should().Be(-1);
        nullItem.SelectionEnd.Should().Be(-1);
    }

    [Fact]
    public void GetCompletions_AfterAndSuggestsPredicateSkeletonsForVisibleColumns()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE u.Id = 1 AND Na";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Column &&
                                       item.Label == "u.Name = ?" &&
                                       item.InsertText == "[u].[Name] = ?");
    }

    [Fact]
    public void GetCompletions_AfterHavingSuggestsPredicateSkeletonsForVisibleColumns()
    {
        var sql = "SELECT u.Name, COUNT(*) FROM dbo.Users u GROUP BY u.Name HAVING N";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "u.Name = ?").Which;

        item.InsertText.Should().Be("[u].[Name] = ?");
        item.SelectionStart.Should().Be("[u].[Name] = ".Length);
        item.SelectionEnd.Should().Be("[u].[Name] = ?".Length);
    }

    [Fact]
    public void GetCompletions_AfterHavingSuggestsAggregatePredicateSkeletons()
    {
        var sql = "SELECT u.Name, COUNT(*) FROM dbo.Users u GROUP BY u.Name HAVING CO";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "COUNT(*) > ?").Which;

        item.InsertText.Should().Be("COUNT(*) > ?");
        item.SelectionStart.Should().Be("COUNT(*) > ".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_AfterHavingSuggestsAggregateArgumentPredicateSkeletons()
    {
        var sql = "SELECT u.Name, COUNT(*) FROM dbo.Users u GROUP BY u.Name HAVING SU";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "SUM(?) > ?").Which;

        item.InsertText.Should().Be("SUM(?) > ?");
        item.SelectionStart.Should().Be("SUM(".Length);
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_AfterHavingAggregateComparisonSuggestsValueSkeleton()
    {
        var sql = "SELECT u.Name, COUNT(*) FROM dbo.Users u GROUP BY u.Name HAVING COUNT(*) > ";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "value").Which;

        item.InsertText.Should().Be("?");
        item.SelectionStart.Should().Be(0);
        item.SelectionEnd.Should().Be(1);
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
    public void GetCompletions_OnClauseSuggestsOnlyCurrentJoinForeignKeys()
    {
        var metadata = new DatabaseMetadata(
            [
                new TableMetadata("dbo", "Users", [new("Id", "int", false, 1)], ["Id"]),
                new TableMetadata("sales", "Orders", [new("Id", "int", false, 1), new("UserId", "int", false, 2)], ["Id"]),
                new TableMetadata("sales", "OrderDetails", [new("Id", "int", false, 1), new("OrderId", "int", false, 2)], ["Id"])
            ],
            [
                new ForeignKeyMetadata("FK_Orders_Users", "sales", "Orders", "UserId", "dbo", "Users", "Id", 1),
                new ForeignKeyMetadata("FK_OrderDetails_Orders", "sales", "OrderDetails", "OrderId", "sales", "Orders", "Id", 1)
            ],
            [], [], []);

        var sql = "SELECT * FROM dbo.Users u JOIN sales.Orders o ON o.UserId = u.Id JOIN sales.OrderDetails od ON ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().Contain(item => item.Label == "od.OrderId = o.Id");
        items.Should().NotContain(item => item.Label == "o.UserId = u.Id");
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

        items.Should().Contain(item => item.Kind == SqlCompletionKind.Keyword && item.InsertText == "COALESCE(?, ?)");

        // Non-function keywords (types) don't get parentheses
        var sql2 = "NVAR";
        var items2 = _provider.GetCompletions(sql2, sql2.Length);

        items2.Should().Contain(item => item.Kind == SqlCompletionKind.Keyword && item.InsertText == "NVARCHAR");
    }

    [Fact]
    public void GetCompletions_FunctionKeywordsGetArgumentSkeletons()
    {
        var sql = "COA";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "COALESCE").Which;

        item.InsertText.Should().Be("COALESCE(?, ?)");
        item.CaretOffset.Should().Be("COALESCE(".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_CastKeywordSelectsExpressionPlaceholder()
    {
        var sql = "CAS";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "CAST").Which;

        item.InsertText.Should().Be("CAST(? AS INT)");
        item.CaretOffset.Should().Be("CAST(".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_CaseKeywordGetsExpressionSkeleton()
    {
        var sql = "SELECT CA";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "CASE").Which;

        item.InsertText.Should().Be("CASE WHEN ? THEN ? ELSE ? END");
        item.CaretOffset.Should().Be("CASE WHEN ".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_ExistsKeywordGetsSubquerySkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE EX";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "EXISTS").Which;

        item.InsertText.Should().Be("EXISTS (SELECT 1 FROM ?)");
        item.CaretOffset.Should().Be("EXISTS (SELECT 1 FROM ".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_NotExistsKeywordGetsSubquerySkeleton()
    {
        var sql = "SELECT * FROM dbo.Users u WHERE NO";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "NOT EXISTS").Which;

        item.InsertText.Should().Be("NOT EXISTS (SELECT 1 FROM ?)");
        item.CaretOffset.Should().Be("NOT EXISTS (SELECT 1 FROM ".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_TopKeywordGetsCountSkeleton()
    {
        var sql = "SELECT TO";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "TOP").Which;

        item.InsertText.Should().Be("TOP (?)");
        item.CaretOffset.Should().Be("TOP (".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_AggregateKeywordsGetArgumentSkeletons()
    {
        var sql = "SU";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "SUM").Which;

        item.InsertText.Should().Be("SUM(?)");
        item.CaretOffset.Should().Be("SUM(".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_CountKeywordDefaultsToCountStar()
    {
        var sql = "COU";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "COUNT").Which;

        item.InsertText.Should().Be("COUNT(*)");
        item.CaretOffset.Should().Be(-1);
        item.SelectionStart.Should().Be(-1);
        item.SelectionEnd.Should().Be(-1);
    }

    [Fact]
    public void GetCompletions_DateAddKeywordSelectsNumberPlaceholder()
    {
        var sql = "DATEA";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "DATEADD").Which;

        item.InsertText.Should().Be("DATEADD(day, ?, ?)");
        item.CaretOffset.Should().Be("DATEADD(day, ".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_GetDateKeywordAddsParentheses()
    {
        var sql = "GETD";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "GETDATE").Which;

        item.InsertText.Should().Be("GETDATE()");
        item.CaretOffset.Should().Be(-1);
        item.SelectionStart.Should().Be(-1);
        item.SelectionEnd.Should().Be(-1);
    }

    [Fact]
    public void GetCompletions_StringKeywordSelectsExpressionPlaceholder()
    {
        var sql = "LOW";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "LOWER").Which;

        item.InsertText.Should().Be("LOWER(?)");
        item.CaretOffset.Should().Be("LOWER(".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_SubstringKeywordAddsArgumentSkeleton()
    {
        var sql = "SUBS";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "SUBSTRING").Which;

        item.InsertText.Should().Be("SUBSTRING(?, ?, ?)");
        item.CaretOffset.Should().Be("SUBSTRING(".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_NumericKeywordSelectsExpressionPlaceholder()
    {
        var sql = "ROU";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "ROUND").Which;

        item.InsertText.Should().Be("ROUND(?, ?)");
        item.CaretOffset.Should().Be("ROUND(".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_RandKeywordAddsParentheses()
    {
        var sql = "RAN";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "RAND").Which;

        item.InsertText.Should().Be("RAND()");
        item.CaretOffset.Should().Be(-1);
        item.SelectionStart.Should().Be(-1);
        item.SelectionEnd.Should().Be(-1);
    }

    [Fact]
    public void GetCompletions_RowNumberKeywordAddsOverOrderBySkeleton()
    {
        var sql = "ROW_N";
        var items = _provider.GetCompletions(sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "ROW_NUMBER").Which;

        item.InsertText.Should().Be("ROW_NUMBER() OVER (ORDER BY ?)");
        item.CaretOffset.Should().Be("ROW_NUMBER() OVER (ORDER BY ".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_ScalarFunctionWithParametersSelectsFirstParameter()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Functions =
            [
                new FunctionMetadata("dbo", "CalcScore")
                {
                    FunctionType = "FN",
                    ReturnType = "int",
                    Parameters =
                    [
                        new FunctionParameterMetadata("id", "int", false, 1),
                        new FunctionParameterMetadata("@weight", "decimal", false, 2)
                    ]
                }
            ]
        };

        var sql = "SELECT Cal";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Function &&
            item.Label == "dbo.CalcScore").Which;
        item.InsertText.Should().Be("[CalcScore](@id, @weight)");
        item.CaretOffset.Should().Be("[CalcScore]".Length + 1);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + "@id".Length);
    }

    [Fact]
    public void GetCandidateCompletions_ParameterizedProcedureSelectsFirstPlaceholder()
    {
        var sql = "EXEC Get";
        var items = _provider.GetCandidateCompletions(TestMetadata.Create(), sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Procedure &&
            item.Label == "dbo.GetUser").Which;
        item.InsertText.Should().Be("[GetUser](@UserId = ?, @IncludeInactive = ?)");
        item.CaretOffset.Should().Be("[GetUser](".Length);
        item.SelectionStart.Should().Be(item.InsertText.IndexOf('?', StringComparison.Ordinal));
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCandidateCompletions_ProcedureOutputParametersIncludeOutputKeyword()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Procedures =
            [
                new ProcedureMetadata("dbo", "TryFindUser")
                {
                    Parameters =
                    [
                        new FunctionParameterMetadata("@Name", "nvarchar", false, 1),
                        new FunctionParameterMetadata("@UserId", "int", true, 2),
                    ]
                }
            ]
        };
        var sql = "EXEC Try";
        var items = _provider.GetCandidateCompletions(metadata, sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Procedure &&
            item.Label == "dbo.TryFindUser").Which;

        item.InsertText.Should().Be("[TryFindUser](@Name = ?, @UserId = ? OUTPUT)");
        item.SelectionStart.Should().Be(item.InsertText.IndexOf('?', StringComparison.Ordinal));
        item.SelectionEnd.Should().Be(item.SelectionStart + 1);
    }

    [Fact]
    public void GetCandidateCompletions_ScalarFunctionSelectsFirstParameter()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Functions =
            [
                new FunctionMetadata("dbo", "CalcScore")
                {
                    FunctionType = "FN",
                    ReturnType = "int",
                    Parameters =
                    [
                        new FunctionParameterMetadata("id", "int", false, 1),
                        new FunctionParameterMetadata("@weight", "decimal", false, 2)
                    ]
                }
            ]
        };

        var sql = "SELECT Cal";
        var items = _provider.GetCandidateCompletions(metadata, sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Function &&
            item.Label == "dbo.CalcScore").Which;
        item.InsertText.Should().Be("[CalcScore](@id, @weight)");
        item.CaretOffset.Should().Be("[CalcScore](".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + "@id".Length);
    }

    [Fact]
    public void GetCandidateCompletions_InsertTableSelectsFirstValue()
    {
        var sql = "INSERT INTO Ord";
        var items = _provider.GetCandidateCompletions(TestMetadata.Create(), sql, sql.Length);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Table &&
            item.Label == "sales.Orders").Which;
        item.InsertText.Should().Be("[Orders]\n([Id], [UserId], [Total])\nVALUES (0, 0, 0)");
        item.SelectionStart.Should().Be(item.InsertText.IndexOf("0, 0, 0", StringComparison.Ordinal));
        item.SelectionEnd.Should().Be(item.SelectionStart + "0".Length);
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
    public void GetCompletions_FromSuggestsTableAliasSkeleton()
    {
        var sql = "SELECT * FROM Us";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "dbo.Users AS alias" &&
            item.InsertText == "[dbo].[Users] AS [u]");
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
        withParamsItem1.SelectionStart.Should().Be(withParamsItem1.CaretOffset);
        withParamsItem1.SelectionEnd.Should().Be(withParamsItem1.CaretOffset + "@id".Length);

        // 2. Qualified
        var sql2 = "SELECT * FROM dbo.";
        var items2 = _provider.GetCompletions(sql2, sql2.Length, metadata);
        
        var noParamsItem2 = items2.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Function && item.Label == "MyTvftNoParams").Which;
        noParamsItem2.InsertText.Should().Be("[MyTvftNoParams]()");
        noParamsItem2.CaretOffset.Should().Be("[MyTvftNoParams]".Length + 1);

        var withParamsItem2 = items2.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Function && item.Label == "MyTvftWithParams").Which;
        withParamsItem2.InsertText.Should().Be("[MyTvftWithParams](@id, @name)");
        withParamsItem2.CaretOffset.Should().Be("[MyTvftWithParams]".Length + 1);
        withParamsItem2.SelectionStart.Should().Be(withParamsItem2.CaretOffset);
        withParamsItem2.SelectionEnd.Should().Be(withParamsItem2.CaretOffset + "@id".Length);
    }

    [Fact]
    public void GetCompletions_TableFunctionSuggestsAliasSkeleton()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Functions =
            [
                new FunctionMetadata("dbo", "MyTableFunction")
                {
                    FunctionType = "TF",
                    Parameters = [new FunctionParameterMetadata("id", "int", false, 1)]
                }
            ]
        };

        var sql = "SELECT * FROM My";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "dbo.MyTableFunction AS alias").Which;

        item.InsertText.Should().Be("[dbo].[MyTableFunction](@id) AS [mtf]");
        item.CaretOffset.Should().Be("[dbo].[MyTableFunction](".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + "@id".Length);
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
    public void GetCompletions_InsertValuesSuggestsPlaceholdersForExplicitColumns()
    {
        var sql = "INSERT INTO dbo.Users ([Name], [CreatedDate]) VALUES (";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "VALUES placeholders").Which;

        item.InsertText.Should().Be("?, ?");
        item.SelectionStart.Should().Be(0);
        item.SelectionEnd.Should().Be(1);
    }

    [Fact]
    public void GetCompletions_InsertValuesSuggestsPlaceholdersForTargetTableColumns()
    {
        var sql = "INSERT INTO sales.Orders VALUES (";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "VALUES placeholders").Which;

        item.InsertText.Should().Be("?, ?, ?");
        item.SelectionStart.Should().Be(0);
        item.SelectionEnd.Should().Be(1);
    }

    [Fact]
    public void GetCompletions_UpdateSetSuggestsTargetTableColumnsOnly()
    {
        var metadata = TestMetadata.Create();

        // Target table is sales.Orders, columns are Id, UserId, Total
        var sql = "UPDATE sales.Orders SET T";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        // Should suggest 'Total' of Orders, but NOT 'Name' of Users
        items.Should().ContainSingle(item => item.Kind == SqlCompletionKind.Column && item.InsertText == "[Total] = ?");
        items.Should().NotContain(item => item.InsertText == "[Name]");
    }

    [Fact]
    public void GetCompletions_UpdateSetSuggestsAssignmentSkeletons()
    {
        var metadata = TestMetadata.Create();

        var sql = "UPDATE sales.Orders SET T";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Column &&
            item.Label == "Total = ?").Which;

        item.InsertText.Should().Be("[Total] = ?");
        item.CaretOffset.Should().Be("[Total] = ".Length);
        item.SelectionStart.Should().Be("[Total] = ".Length);
        item.SelectionEnd.Should().Be("[Total] = ?".Length);
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
    public void GetCompletions_MergeSuggestsWhenMatchedSkeleton()
    {
        var sql = "MERGE INTO dbo.Users AS t USING sales.Orders AS s ON t.Id = s.UserId WH";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "WHEN MATCHED").Which;

        item.InsertText.Should().Be("WHEN MATCHED THEN UPDATE SET ?");
        item.CaretOffset.Should().Be("WHEN MATCHED THEN UPDATE SET ".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
    }

    [Fact]
    public void GetCompletions_MergeSuggestsWhenNotMatchedSkeleton()
    {
        var sql = "MERGE INTO dbo.Users AS t USING sales.Orders AS s ON t.Id = s.UserId WH";
        var items = _provider.GetCompletions(sql, sql.Length, TestMetadata.Create());

        var item = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Keyword &&
            item.Label == "WHEN NOT MATCHED").Which;

        item.InsertText.Should().Be("WHEN NOT MATCHED THEN INSERT (?) VALUES (?)");
        item.CaretOffset.Should().Be("WHEN NOT MATCHED THEN INSERT (".Length);
        item.SelectionStart.Should().Be(item.CaretOffset);
        item.SelectionEnd.Should().Be(item.CaretOffset + 1);
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
    public void GetCompletions_InsertOutputSuggestsInsertedColumnListSkeleton()
    {
        var metadata = TestMetadata.Create();

        var sql = "INSERT INTO dbo.Users (Name) OUTPUT ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OUTPUT INSERTED columns" &&
            item.InsertText == "INSERTED.[Id], INSERTED.[Name], INSERTED.[Email]");
        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OUTPUT DELETED columns");
    }

    [Fact]
    public void GetCompletions_DeleteOutputSuggestsDeletedColumnListSkeleton()
    {
        var metadata = TestMetadata.Create();

        var sql = "DELETE FROM sales.Orders OUTPUT ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OUTPUT DELETED columns" &&
            item.InsertText == "DELETED.[Id], DELETED.[UserId], DELETED.[Total]");
        items.Should().NotContain(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OUTPUT INSERTED columns");
    }

    [Fact]
    public void GetCompletions_UpdateOutputSuggestsInsertedAndDeletedColumnListSkeletons()
    {
        var metadata = TestMetadata.Create();

        var sql = "UPDATE sales.Orders SET Total = 1 OUTPUT ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OUTPUT INSERTED columns" &&
            item.InsertText == "INSERTED.[Id], INSERTED.[UserId], INSERTED.[Total]");
        items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Snippet &&
            item.Label == "OUTPUT DELETED columns" &&
            item.InsertText == "DELETED.[Id], DELETED.[UserId], DELETED.[Total]");
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
        orderItem.SelectionStart.Should().Be(orderItem.InsertText.IndexOf("0, 0, 0", StringComparison.Ordinal));
        orderItem.SelectionEnd.Should().Be(orderItem.SelectionStart + "0".Length);
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
        procItem.SelectionStart.Should().Be(procItem.InsertText.IndexOf('?', StringComparison.Ordinal));
        procItem.SelectionEnd.Should().Be(procItem.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_ExecSuggestsProcedureOutputParameters()
    {
        var metadata = new DatabaseMetadata([], [], [], [], [])
        {
            Procedures =
            [
                new ProcedureMetadata("dbo", "TryFindUser")
                {
                    Parameters =
                    [
                        new FunctionParameterMetadata("@Name", "nvarchar", false, 1),
                        new FunctionParameterMetadata("@UserId", "int", true, 2),
                    ]
                }
            ]
        };

        var sql = "EXEC dbo.Try";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var procItem = items.Should().ContainSingle(item =>
            item.Kind == SqlCompletionKind.Procedure &&
            item.Label == "TryFindUser").Which;

        procItem.InsertText.Should().Be("[TryFindUser](@Name = ?, @UserId = ? OUTPUT)");
        procItem.SelectionStart.Should().Be(procItem.InsertText.IndexOf('?', StringComparison.Ordinal));
        procItem.SelectionEnd.Should().Be(procItem.SelectionStart + 1);
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
        procItem.SelectionStart.Should().Be(procItem.InsertText.IndexOf('?', StringComparison.Ordinal));
        procItem.SelectionEnd.Should().Be(procItem.SelectionStart + 1);
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
        procItem.SelectionStart.Should().Be(procItem.InsertText.IndexOf('?', StringComparison.Ordinal));
        procItem.SelectionEnd.Should().Be(procItem.SelectionStart + 1);
    }

    [Fact]
    public void GetCompletions_SuggestsBuiltInFunctionsWithFunctionKind()
    {
        var metadata = TestMetadata.Create();

        var sql = "SELECT Norm";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var fnItem = items.FirstOrDefault(item => item.Label.Contains("NormalizeEmail"));
        fnItem.Should().NotBeNull();
        fnItem!.Kind.Should().Be(SqlCompletionKind.Function);
        fnItem.InsertText.Should().Be("[NormalizeEmail]()");
    }

    [Fact]
    public void GetCompletions_FunctionDescriptionUsesSavedDefinition()
    {
        var metadata = TestMetadata.Create();

        var sql = "SELECT Norm";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var fnItem = items.First(item => item.Label.Contains("NormalizeEmail"));
        fnItem.Description.Should().Contain("CREATE FUNCTION [dbo].[NormalizeEmail]");
        fnItem.Description.Should().Contain("RETURN LOWER(@value);");
    }

    [Fact]
    public void GetCompletions_ViewDescriptionUsesSavedDefinition()
    {
        var metadata = TestMetadata.Create();

        var sql = "SELECT * FROM Active";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var viewItem = items.First(item => item.Kind == SqlCompletionKind.View && item.Label.Contains("ActiveUsers"));
        viewItem.Description.Should().Contain("CREATE VIEW [dbo].[ActiveUsers]");
        viewItem.Description.Should().Contain("WHERE IsActive = 1");
    }

    [Fact]
    public void GetCompletions_ProcedureDescriptionUsesSavedDefinition()
    {
        var metadata = TestMetadata.Create();

        var sql = "EXEC Get";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var procItem = items.First(item => item.Kind == SqlCompletionKind.Procedure && item.Label.Contains("GetUser"));
        procItem.Description.Should().Contain("CREATE PROCEDURE [dbo].[GetUser]");
        procItem.Description.Should().Contain("SELECT * FROM dbo.Users");
    }

    [Fact]
    public void GetCompletions_JoinOnSuggestsImplicitColumnMatchWhenNoFk()
    {
        var tables = new[]
        {
            new TableMetadata("dbo", "Employees", new[] { new ColumnMetadata("EmpId", "int", false, 1), new ColumnMetadata("Name", "nvarchar", true, 2) }, new[] { "EmpId" }),
            new TableMetadata("dbo", "Salaries", new[] { new ColumnMetadata("EmpId", "int", false, 1), new ColumnMetadata("Amount", "decimal", false, 2) }, new[] { "EmpId" })
        };

        var metadata = new DatabaseMetadata(tables, Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>(), new[] { "TestDb" }, Array.Empty<LinkedServerInfo>());

        var sql = "SELECT * FROM dbo.Employees e JOIN dbo.Salaries s ON ";
        var items = _provider.GetCompletions(sql, sql.Length, metadata);

        var joinItem = items.FirstOrDefault(item => item.Label.Contains(" = "));
        joinItem.Should().NotBeNull();
        joinItem!.InsertText.Should().Be("[e].[EmpId] = [s].[EmpId]");
    }
}
