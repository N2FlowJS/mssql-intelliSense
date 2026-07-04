using FluentAssertions;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

/// <summary>
/// Unit tests for SelectStarExpander, TableQualifier, CrudGenerator, and MssqlIntelliSenseCacheReader.FilterByDatabase.
/// </summary>
public sealed class NewFeaturesTests
{
    // ─── Shared test metadata ──────────────────────────────────────────────────

    private static DatabaseMetadata CreateRichMetadata()
    {
        var tables = new[]
        {
            new TableMetadata("dbo", "Users",
                [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2), new("Email", "nvarchar", true, 3)],
                ["Id"]) { Database = "MyDb" },
            new TableMetadata("sales", "Orders",
                [new("Id", "int", false, 1), new("UserId", "int", false, 2), new("Total", "decimal", false, 3)],
                ["Id"]) { Database = "MyDb" },
        };

        var fks = new[]
        {
            new ForeignKeyMetadata("FK_Orders_Users", "sales", "Orders", "UserId", "dbo", "Users", "Id", 1) { Database = "MyDb" }
        };

        return new DatabaseMetadata(tables, fks, [], ["MyDb", "OtherDb"], [])
        {
            Views = [new ViewMetadata("dbo", "ActiveUsers", [new("Id", "int", false, 1)]) { Database = "MyDb" }]
        };
    }

    // ─── SelectStarExpander tests ──────────────────────────────────────────────

    [Fact]
    public void SelectStarExpander_ExpandsSingleTable()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT * FROM dbo.Users";
        var caret = sql.IndexOf('*') + 1; // caret after *

        var result = SelectStarExpander.TryExpand(sql, caret, metadata);

        result.Should().NotBeNull();
        result!.ExpandedText.Should().Contain("[Id]");
        result.ExpandedText.Should().Contain("[Name]");
        result.ExpandedText.Should().Contain("[Email]");
    }

    [Fact]
    public void SelectStarExpander_ExpandsInSql_ReplacesStarInPlace()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT * FROM dbo.Users";
        var caret = sql.IndexOf('*') + 1;

        var result = SelectStarExpander.ExpandInSql(sql, caret, metadata);

        result.Should().NotBeNull();
        result.Should().NotContain("SELECT *");
        result.Should().Contain("[Id]");
        result.Should().Contain("FROM dbo.Users");
    }

    [Fact]
    public void SelectStarExpander_ReturnsNull_WhenNoTable()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT *";
        var caret = sql.Length;

        var result = SelectStarExpander.TryExpand(sql, caret, metadata);

        // No table in FROM clause, sources empty → null
        result.Should().BeNull();
    }

    [Fact]
    public void SelectStarExpander_ReturnsNull_WhenNoStarNearCaret()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT Id FROM dbo.Users";
        var caret = sql.Length;

        var result = SelectStarExpander.TryExpand(sql, caret, metadata);

        result.Should().BeNull();
    }

    // ─── TableQualifier tests ──────────────────────────────────────────────────

    [Fact]
    public void TableQualifier_QualifiesUnqualifiedTableName()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT * FROM Users";
        var caret = sql.IndexOf("Users") + 3; // mid-word

        var result = TableQualifier.TryQualifyAt(sql, caret, metadata);

        result.Should().NotBeNull();
        result!.QualifiedText.Should().Be("[dbo].[Users]");
        result.Schema.Should().Be("dbo");
        result.Name.Should().Be("Users");
    }

    [Fact]
    public void TableQualifier_SkipsAlreadyQualifiedName()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT * FROM dbo.Users";
        var caret = sql.IndexOf("Users") + 2;

        var result = TableQualifier.TryQualifyAt(sql, caret, metadata);

        // Users already has dbo. prefix → return null
        result.Should().BeNull();
    }

    [Fact]
    public void TableQualifier_QualifyAll_AddsSchemaToAllUnqualifiedTables()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT * FROM Users JOIN Orders ON Orders.UserId = Users.Id";

        var result = TableQualifier.QualifyAll(sql, metadata);

        result.Should().Contain("[dbo].[Users]");
        result.Should().Contain("[sales].[Orders]");
    }

    [Fact]
    public void TableQualifier_QualifyAll_LeavesAlreadyQualifiedUnchanged()
    {
        var metadata = CreateRichMetadata();
        var sql = "SELECT * FROM dbo.Users";

        var result = TableQualifier.QualifyAll(sql, metadata);

        // Already qualified - should not double-qualify
        result.Should().NotContain("[dbo].[dbo]");
        result.Should().Contain("dbo.Users");
    }

    // ─── CrudGenerator tests ──────────────────────────────────────────────────

    [Fact]
    public void CrudGenerator_GeneratesGetAll()
    {
        var table = new TableMetadata("dbo", "Users",
            [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2)],
            ["Id"]);

        var script = CrudGenerator.Generate(table, CrudOperation.GetAll);

        script.Should().Contain("CREATE OR ALTER PROCEDURE");
        script.Should().Contain("usp_Users_GetAll");
        script.Should().Contain("SELECT");
        script.Should().Contain("[Id]");
        script.Should().Contain("[Name]");
        script.Should().Contain("FROM [dbo].[Users]");
    }

    [Fact]
    public void CrudGenerator_GeneratesGetById_WithPkParam()
    {
        var table = new TableMetadata("dbo", "Users",
            [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2)],
            ["Id"]);

        var script = CrudGenerator.Generate(table, CrudOperation.GetById);

        script.Should().Contain("@Id int");
        script.Should().Contain("WHERE [Id] = @Id");
    }

    [Fact]
    public void CrudGenerator_GeneratesInsert_WithoutPk()
    {
        var table = new TableMetadata("dbo", "Users",
            [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2), new("Email", "nvarchar", true, 3)],
            ["Id"]);

        var script = CrudGenerator.Generate(table, CrudOperation.Insert);

        // PK (Id) should be excluded from insert params (single PK → identity assumed)
        script.Should().Contain("@Name nvarchar");
        script.Should().Contain("@Email nvarchar = NULL");
        script.Should().NotContain("@Id int");
        script.Should().Contain("OUTPUT INSERTED.[Id]");
    }

    [Fact]
    public void CrudGenerator_GeneratesUpdate_WithSetAndWhere()
    {
        var table = new TableMetadata("dbo", "Users",
            [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2)],
            ["Id"]);

        var script = CrudGenerator.Generate(table, CrudOperation.Update);

        script.Should().Contain("SET");
        script.Should().Contain("[Name] = @Name");
        script.Should().Contain("WHERE [Id] = @Id");
    }

    [Fact]
    public void CrudGenerator_GeneratesDelete_WithWhere()
    {
        var table = new TableMetadata("dbo", "Users",
            [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2)],
            ["Id"]);

        var script = CrudGenerator.Generate(table, CrudOperation.Delete);

        script.Should().Contain("DELETE FROM [dbo].[Users]");
        script.Should().Contain("WHERE [Id] = @Id");
    }

    [Fact]
    public void CrudGenerator_GeneratesAll_ProducesAllFiveProcs()
    {
        var table = new TableMetadata("dbo", "Users",
            [new("Id", "int", false, 1), new("Name", "nvarchar", false, 2)],
            ["Id"]);

        var script = CrudGenerator.Generate(table, CrudOperation.All);

        script.Should().Contain("usp_Users_GetAll");
        script.Should().Contain("usp_Users_GetById");
        script.Should().Contain("usp_Users_Insert");
        script.Should().Contain("usp_Users_Update");
        script.Should().Contain("usp_Users_Delete");
    }

    // ─── MssqlIntelliSenseCacheReader.FilterByDatabase tests ───────────────────────────

    [Fact]
    public void FilterByDatabase_FiltersObjectsToActiveDatabase()
    {
        var metadata = CreateRichMetadata();

        var filtered = MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, "MyDb");

        filtered.Tables.Should().HaveCount(2); // both in MyDb
        filtered.Databases.Should().ContainSingle().Which.Should().Be("MyDb");
    }

    [Fact]
    public void FilterByDatabase_FallsBackToFull_WhenDatabaseNotFound()
    {
        var metadata = CreateRichMetadata();

        // "OtherDb" has no objects tagged → fallback to full
        var filtered = MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, "OtherDb");

        // Fallback: returns full metadata because OtherDb has no tables/views/procedures
        filtered.Tables.Count.Should().Be(metadata.Tables.Count);
    }

    [Fact]
    public void FilterByDatabase_FallsBackToFull_WhenNoDbTagPresent()
    {
        // Metadata without Database tags (legacy)
        var metadata = new DatabaseMetadata(
            [new TableMetadata("dbo", "Users", [new("Id", "int", false, 1)], ["Id"])],
            [], [], ["master"], []);

        var filtered = MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, "SomeDb");

        // No Database tags → hasDbData = false → return full
        filtered.Should().Be(metadata);
    }
}
