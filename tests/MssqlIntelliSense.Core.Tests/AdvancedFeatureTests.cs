using FluentAssertions;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Completion.Snippets;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

public sealed class AdvancedFeatureTests
{
    // ─── ScriptDomContextAnalyzer: CTE context ────────────────────────────

    [Fact]
    public void ScriptDomAnalyzer_DetectsCteBodyContext()
    {
        var sql = "WITH cte AS (SELECT Id, Name FROM dbo.Users) SELECT * FROM cte";
        var caret = sql.IndexOf("FROM dbo.Users") + 5;

        ScriptDomContextAnalyzer.Analyze(
            sql, caret,
            out _, out _, out var isTable, out _, out _, out _,
            out _, out _, out _, out _, out _,
            out _, out _, out _);

        isTable.Should().BeTrue("caret is in CTE body's FROM clause");
    }

    [Fact]
    public void ScriptDomAnalyzer_DetectsCteSelectContext()
    {
        var sql = "WITH cte AS (SELECT Id, Name FROM dbo.Users) SELECT * FROM cte";
        var caret = sql.IndexOf("SELECT *") + 7;

        ScriptDomContextAnalyzer.Analyze(
            sql, caret,
            out var qualifiers, out _,
            out _, out _, out _, out _,
            out _, out _, out _, out _, out _,
            out _, out _, out _);

        qualifiers.Should().BeEmpty("caret is in main query, not CTE");
    }

    // ─── ScriptDomContextAnalyzer: Subquery nesting ───────────────────────

    [Fact]
    public void ScriptDomAnalyzer_DetectsSubqueryFromContext()
    {
        var sql = "SELECT * FROM (SELECT Id FROM dbo.Users) AS u";
        var caret = sql.IndexOf("FROM dbo.Users") + 5;

        ScriptDomContextAnalyzer.Analyze(
            sql, caret,
            out _, out _, out var isTable, out _, out _, out _,
            out _, out _, out _, out _, out _,
            out _, out _, out _);

        isTable.Should().BeTrue("caret is in subquery's FROM clause");
    }

    // ─── ScriptDomContextAnalyzer: Join context ──────────────────────────

    [Fact]
    public void ScriptDomAnalyzer_DetectsJoinContext()
    {
        var sql = "SELECT * FROM dbo.Users u INNER JOIN sales.Orders o ON u.Id = o.UserId";
        var caret = sql.IndexOf("sales.Orders") + 1;

        ScriptDomContextAnalyzer.Analyze(
            sql, caret,
            out _, out _, out var isTable, out _, out var isJoin, out _,
            out _, out _, out _, out _, out _,
            out _, out _, out _);

        isTable.Should().BeTrue();
        isJoin.Should().BeTrue("caret is after INNER JOIN keyword");
    }

    // ─── SnippetLoader: JSON parsing ──────────────────────────────────────

    [Fact]
    public void SnippetLoader_LoadsValidJson()
    {
        var json = """
            {
                "prefix": "cte",
                "description": "WITH cte fragment",
                "body": "WITH $name$ AS ($CURSOR$)",
                "placeholders": [{ "name": "name", "defaultValue": "cte_name" }]
            }
            """;

        var snippet = SnippetLoader.LoadFromJson(json);

        snippet.Should().NotBeNull();
        snippet!.Prefix.Should().Be("cte");
        snippet.Description.Should().Be("WITH cte fragment");
        snippet.Body.Should().Be("WITH $name$ AS ($CURSOR$)");
        snippet.Placeholders.Should().HaveCount(1);
        snippet.Placeholders[0].Name.Should().Be("name");
        snippet.Placeholders[0].DefaultValue.Should().Be("cte_name");
    }

    [Fact]
    public void SnippetLoader_ReturnsNullForInvalidJson()
    {
        var snippet = SnippetLoader.LoadFromJson("not json");
        snippet.Should().BeNull();
    }

    [Fact]
    public void SnippetLoader_ReturnsNullForEmptyPrefix()
    {
        var json = """{ "prefix": "" }""";
        var snippet = SnippetLoader.LoadFromJson(json);
        snippet.Should().BeNull();
    }

    // ─── SnippetLoader: LoadFromDirectory ────────────────────────────────

    [Fact]
    public void SnippetLoader_LoadsFromDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "snippet_test_" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "cte.json"), """
                { "prefix": "cte", "description": "CTE", "body": "$CURSOR$" }
                """);
            File.WriteAllText(Path.Combine(dir, "sf.json"), """
                { "prefix": "sf", "description": "SELECT FROM", "body": "SELECT $CURSOR$" }
                """);
            File.WriteAllText(Path.Combine(dir, "invalid.txt"), "not json");

            var snippets = SnippetLoader.LoadFromDirectory(dir).ToList();

            snippets.Should().HaveCount(2, "only .json files are loaded");
            snippets.Should().Contain(s => s.Prefix == "cte");
            snippets.Should().Contain(s => s.Prefix == "sf");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SnippetLoader_ReturnsEmptyForMissingDirectory()
    {
        var snippets = SnippetLoader.LoadFromDirectory("Z:\\nonexistent_path_xyz");
        snippets.Should().BeEmpty();
    }

    // ─── SnippetExpander: Placeholder replacement ─────────────────────────

    [Fact]
    public void SnippetExpander_ReplacesPlaceholders()
    {
        var snippet = new Snippet
        {
            Prefix = "test",
            Body = "SELECT $cols$ FROM $table$ WHERE $CURSOR$",
            Placeholders =
            [
                new SnippetPlaceholder("cols", "*"),
                new SnippetPlaceholder("table", "dbo.Users")
            ]
        };

        var result = SnippetExpander.Expand(snippet);

        result.Text.Should().Be("SELECT * FROM dbo.Users WHERE ");
        result.CursorOffset.Should().Be("SELECT * FROM dbo.Users WHERE ".Length);
    }

    [Fact]
    public void SnippetExpander_NoCursor()
    {
        var snippet = new Snippet
        {
            Prefix = "test",
            Body = "SELECT * FROM dbo.Users",
            Placeholders = []
        };

        var result = SnippetExpander.Expand(snippet);

        result.Text.Should().Be("SELECT * FROM dbo.Users");
        result.CursorOffset.Should().Be(-1);
    }

    [Fact]
    public void SnippetExpander_ReplacesBuiltinPlaceholders()
    {
        var snippet = new Snippet
        {
            Prefix = "test",
            Body = "$USER$ $MACHINE$ $GUID$",
            Placeholders = []
        };

        var result = SnippetExpander.Expand(snippet, new Dictionary<string, string>
        {
            ["USER"] = "testuser",
            ["MACHINE"] = "testpc",
            ["GUID"] = "ABCDEF",
        });

        result.Text.Should().Be("testuser testpc ABCDEF");
    }

    // ─── SnippetCompletionHelper: Integration ─────────────────────────────

    [Fact]
    public void SnippetCompletionHelper_AddsMatchingSnippets()
    {
        var suggestions = new List<SqlCompletionItem>();
        var snippets = new List<Snippet>
        {
            new() { Prefix = "cte", Description = "CTE", Body = "WITH $n$ AS ($CURSOR$)", Placeholders = [new SnippetPlaceholder("n", "x")] },
            new() { Prefix = "sf", Description = "SELECT FROM", Body = "SELECT $CURSOR$\nFROM ", Placeholders = [] },
        };

        SnippetCompletionHelper.AddSnippetCompletions(suggestions, "s", null, snippets);

        suggestions.Should().ContainSingle();
        suggestions[0].Label.Should().Be("sf");
        suggestions[0].Kind.Should().Be(SqlCompletionKind.Snippet);
        suggestions[0].Description.Should().Be("SELECT FROM");
    }

    [Fact]
    public void SnippetCompletionHelper_RespectsFilterPrefix()
    {
        var suggestions = new List<SqlCompletionItem>();
        var snippets = new List<Snippet>
        {
            new() { Prefix = "cte", Body = "$CURSOR$", Placeholders = [] },
            new() { Prefix = "sf", Body = "$CURSOR$", Placeholders = [] },
        };

        SnippetCompletionHelper.AddSnippetCompletions(suggestions, "x", null, snippets);

        suggestions.Should().BeEmpty();
    }

    // ─── CandidateUsageRecorder: MRU tracking ────────────────────────────

    [Fact]
    public void UsageRecorder_RecordsAndRetrievesUsage()
    {
        var recorder = new CandidateUsageRecorder();

        recorder.CandidateUsed(SqlCompletionKind.Table, "dbo.Users");

        recorder.TryGetLastUsedTime(SqlCompletionKind.Table, "dbo.Users", out var time)
            .Should().BeTrue();
        time.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void UsageRecorder_ReturnsFalseForUnknownItem()
    {
        var recorder = new CandidateUsageRecorder();

        recorder.TryGetLastUsedTime(SqlCompletionKind.Table, "nonexistent", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void UsageRecorder_OverwritesOnReuse()
    {
        var recorder = new CandidateUsageRecorder();

        recorder.CandidateUsed(SqlCompletionKind.Table, "dbo.Users");
        var firstTime = DateTime.UtcNow;
        Thread.Sleep(10);
        recorder.CandidateUsed(SqlCompletionKind.Table, "dbo.Users");

        recorder.TryGetLastUsedTime(SqlCompletionKind.Table, "dbo.Users", out var secondTime);
        secondTime.Should().BeAfter(firstTime);
    }

    // ─── SqlCompletionHelper.UsageRank ────────────────────────────────────

    [Fact]
    public void UsageRank_ReturnsZeroForNoRecorder()
    {
        var rank = SqlCompletionHelper.UsageRank(SqlCompletionKind.Table, "dbo.Users", null);
        rank.Should().Be(0);
    }

    [Fact]
    public void UsageRank_ReturnsZeroForUnusedItem()
    {
        var recorder = new CandidateUsageRecorder();
        var rank = SqlCompletionHelper.UsageRank(SqlCompletionKind.Table, "dbo.Users", recorder);
        rank.Should().Be(0);
    }

    [Fact]
    public void UsageRank_ReturnsNegativeForRecentlyUsed()
    {
        var recorder = new CandidateUsageRecorder();
        recorder.CandidateUsed(SqlCompletionKind.Table, "dbo.Users");

        var rank = SqlCompletionHelper.UsageRank(SqlCompletionKind.Table, "dbo.Users", recorder);
        rank.Should().Be(-99, "rank was just recorded (<1min ago)");
    }

    // ─── SqlCompletionProvider: RecordUsage ──────────────────────────────

    [Fact]
    public void Provider_RecordUsage_DelegatesToRecorder()
    {
        var recorder = new CandidateUsageRecorder();
        var provider = new SqlCompletionProvider { UsageRecorder = recorder };
        var item = new SqlCompletionItem("dbo.Users", "dbo.Users", SqlCompletionKind.Table, "table");

        provider.RecordUsage(item);

        recorder.TryGetLastUsedTime(SqlCompletionKind.Table, "dbo.Users", out _).Should().BeTrue();
    }

    // ─── Default snippets ────────────────────────────────────────────────

    [Fact]
    public void DefaultSnippets_ContainsExpectedEntries()
    {
        var defaults = SnippetDefaults.GetDefaultSnippets();

        defaults.Should().NotBeEmpty();
        defaults.Should().Contain(s => s.Prefix == "cte" && s.Description == "WITH cte fragment");
        defaults.Should().Contain(s => s.Prefix == "sf" && s.Description == "SELECT FROM");
        defaults.Should().Contain(s => s.Prefix == "s" && s.Description == "SELECT");
        defaults.Should().Contain(s => s.Prefix == "f" && s.Description == "FROM");
        defaults.Should().Contain(s => s.Prefix == "w" && s.Description == "WHERE");
        defaults.Should().Contain(s => s.Prefix == "i" && s.Description == "INSERT INTO");
        defaults.Should().Contain(s => s.Prefix == "u" && s.Description == "UPDATE");
        defaults.Should().Contain(s => s.Prefix == "d" && s.Description == "DELETE FROM");
        defaults.Should().Contain(s => s.Prefix == "try" && s.Description == "TRY CATCH");
    }

    // ─── SqlCompletionHelper.KindRank for Snippet ─────────────────────────

    [Fact]
    public void KindRank_Snippet_RanksJustAboveKeyword()
    {
        var snippetRank = SqlCompletionHelper.KindRank(SqlCompletionKind.Snippet);
        var keywordRank = SqlCompletionHelper.KindRank(SqlCompletionKind.Keyword);

        snippetRank.Should().BeLessThan(keywordRank,
            "snippet rank should be just above keyword (lower number = higher position)");
    }
}
