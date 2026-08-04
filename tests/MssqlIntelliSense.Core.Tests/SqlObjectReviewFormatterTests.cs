using FluentAssertions;
using MssqlIntelliSense.Core.Completion;

namespace MssqlIntelliSense.Core.Tests;

public sealed class SqlObjectReviewFormatterTests
{
    [Fact]
    public void Build_View_UsesSavedDefinitionAndInsertTextForMatching()
    {
        var item = new SqlCompletionItem(
            "ActiveUsers",
            "[dbo].[ActiveUsers]",
            SqlCompletionKind.View,
            "custom view description");

        var review = SqlObjectReviewFormatter.Build(item, TestMetadata.Create());

        review.Title.Should().Be("View [dbo].[ActiveUsers]");
        review.Subtitle.Should().Be("TestDb.dbo.ActiveUsers");
        review.Definition.Should().Contain("CREATE VIEW [dbo].[ActiveUsers]");
        review.Definition.Should().Contain("WHERE IsActive = 1");
        review.Details.Should().Contain("Columns:");
    }

    [Fact]
    public void Build_Procedure_UsesSavedDefinitionAndParameterDetails()
    {
        var item = new SqlCompletionItem(
            "dbo.GetUser",
            "[dbo].[GetUser]",
            SqlCompletionKind.Procedure,
            "custom procedure description");

        var review = SqlObjectReviewFormatter.Build(item, TestMetadata.Create());

        review.Title.Should().Be("Procedure [dbo].[GetUser]");
        review.Definition.Should().Contain("CREATE PROCEDURE [dbo].[GetUser]");
        review.Definition.Should().Contain("@IncludeInactive bit");
        review.Details.Should().Contain("- @UserId: int");
        review.Details.Should().Contain("Returns a user profile for application authentication.");
    }

    [Fact]
    public void Build_Function_UsesSavedDefinitionAndReturnTypeDetails()
    {
        var item = new SqlCompletionItem(
            "NormalizeEmail",
            "[dbo].[NormalizeEmail]",
            SqlCompletionKind.Function,
            "custom function description");

        var review = SqlObjectReviewFormatter.Build(item, TestMetadata.Create());

        review.Title.Should().Be("Function [dbo].[NormalizeEmail]");
        review.Definition.Should().Contain("CREATE FUNCTION [dbo].[NormalizeEmail]");
        review.Definition.Should().Contain("RETURN LOWER(@value);");
        review.Details.Should().Contain("Return type: nvarchar");
        review.Details.Should().Contain("Normalizes email addresses before identity matching.");
    }

    [Fact]
    public void Build_Table_DefaultCustomDescription_IncludesColumnDetails()
    {
        var item = new SqlCompletionItem(
            "Users",
            "[dbo].[Users]",
            SqlCompletionKind.Table,
            "custom table description");

        var review = SqlObjectReviewFormatter.Build(item, TestMetadata.Create());

        review.CustomDescription.Should().Contain("Columns:");
        review.CustomDescription.Should().Contain("Id");
        review.CustomDescription.Should().Contain("Email");
        review.CustomDescription.Should().Contain("Primary key: Id");
    }
}
