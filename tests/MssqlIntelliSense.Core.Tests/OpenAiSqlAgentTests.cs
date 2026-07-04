using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MssqlIntelliSense.Core.Ai;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Tests;

public sealed class OpenAiSqlAgentTests
{
    [Fact]
    public async Task ImproveSqlAsync_SendsSchemaAndParsesStructuredResult()
    {
        string? requestJson = null;
        var handler = new StubHandler(async request =>
        {
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("test-key");
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, """
                {
                  "choices": [
                    {
                      "index": 0,
                      "message": {
                        "role": "assistant",
                        "content": "{\"status\":\"completed\",\"result\":{\"improvedSql\":\"SELECT [Id] FROM [dbo].[Users];\",\"explanation\":\"Qualified identifiers.\",\"warnings\":[],\"indexSuggestions\":[]}}" 
                      },
                      "finish_reason": "stop"
                    }
                  ]
                }
                """);
        });
        var assistant = CreateAssistant(handler);

        var result = await assistant.ImproveSqlAsync(
            "SELECT Id FROM dbo.Users", TestMetadata.Create(), "Improve readability", CancellationToken.None);

        result.ImprovedSql.Should().Contain("[dbo].[Users]");
        using var request = JsonDocument.Parse(requestJson!);
        request.RootElement.GetProperty("model").GetString().Should().Be("test-model");
        requestJson.Should().Contain("dbo.Users");
    }

    [Fact]
    public async Task ImproveSqlAsync_ExecutesToolCallsAndReturnsFinalResult()
    {
        int callCount = 0;
        var handler = new StubHandler(async request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "choices": [
                        {
                          "index": 0,
                          "message": {
                            "role": "assistant",
                            "content": "{\"status\":\"tool_call\",\"toolCall\":{\"name\":\"list_tables\",\"arguments\":{}}}"
                          },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """);
            }
            else
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "choices": [
                        {
                          "index": 0,
                          "message": {
                            "role": "assistant",
                            "content": "{\"status\":\"completed\",\"result\":{\"improvedSql\":\"SELECT [Id] FROM [dbo].[Users];\",\"explanation\":\"Qualified identifiers.\",\"warnings\":[],\"indexSuggestions\":[]}}"
                          },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """);
            }
        });

        var assistant = new TestableOpenAiSqlAgent(handler);

        var result = await assistant.ImproveSqlAsync(
            "SELECT Id FROM dbo.Users", DatabaseMetadata.Empty, "Improve readability", CancellationToken.None);

        result.ImprovedSql.Should().Contain("[dbo].[Users]");
        callCount.Should().Be(2);
        assistant.LastQuery.Should().Contain("tablesList");
    }

    [Fact]
    public async Task ImproveSqlAsync_ExecutesInMemoryToolCallsWhenMetadataProvided()
    {
        int callCount = 0;
        var handler = new StubHandler(async request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "choices": [
                        {
                          "index": 0,
                          "message": {
                            "role": "assistant",
                            "content": "{\"status\":\"tool_call\",\"toolCall\":{\"name\":\"list_tables\",\"arguments\":{}}}"
                          },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """);
            }
            else
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "choices": [
                        {
                          "index": 0,
                          "message": {
                            "role": "assistant",
                            "content": "{\"status\":\"completed\",\"result\":{\"improvedSql\":\"SELECT [Id] FROM [dbo].[Users];\",\"explanation\":\"Qualified identifiers.\",\"warnings\":[],\"indexSuggestions\":[]}}"
                          },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """);
            }
        });

        var assistant = new TestableOpenAiSqlAgent(handler);

        var result = await assistant.ImproveSqlAsync(
            "SELECT Id FROM dbo.Users", TestMetadata.Create(), "Improve readability", CancellationToken.None);

        result.ImprovedSql.Should().Contain("[dbo].[Users]");
        callCount.Should().Be(2);
        assistant.LastQuery.Should().BeNull();
    }

    [Fact]
    public async Task ImproveSqlAsync_DoesNotExecuteToolWhenUserRejectsApproval()
    {
        int callCount = 0;
        var handler = new StubHandler(_ =>
        {
            callCount++;
            return Task.FromResult(callCount == 1
                ? JsonResponse(HttpStatusCode.OK, """
                    {
                      "choices": [
                        {
                          "index": 0,
                          "message": {
                            "role": "assistant",
                            "content": "{\"status\":\"tool_call\",\"toolCall\":{\"name\":\"list_tables\",\"arguments\":{}}}"
                          },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """)
                : JsonResponse(HttpStatusCode.OK, """
                    {
                      "choices": [
                        {
                          "index": 0,
                          "message": {
                            "role": "assistant",
                            "content": "{\"status\":\"completed\",\"result\":{\"improvedSql\":\"SELECT 1;\",\"explanation\":\"User rejected tool.\",\"warnings\":[\"Tool rejected.\"],\"indexSuggestions\":[]}}"
                          },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """));
        });

        var assistant = new TestableOpenAiSqlAgent(handler, approveTools: false);

        var result = await assistant.ImproveSqlAsync(
            "SELECT 1", DatabaseMetadata.Empty, "Improve readability", CancellationToken.None);

        result.ImprovedSql.Should().Be("SELECT 1;");
        callCount.Should().Be(2);
        assistant.ApprovalCount.Should().Be(1);
        assistant.LastQuery.Should().BeNull();
    }


    [Fact]
    public async Task ImproveSqlAsync_ApiErrorExposesStatusWithoutLeakingKey()
    {
        var assistant = CreateAssistant(new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"Rate limit reached\"}}"))));

        var action = () => assistant.ImproveSqlAsync("SELECT 1", TestMetadata.Create(), "Improve", CancellationToken.None);

        var exception = await action.Should().ThrowAsync<OpenAiSqlAgentException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        exception.Which.Message.Should().Contain("Rate limit reached").And.NotContain("test-key");
    }

    [Fact]
    public async Task ImproveSqlAsync_UnexpectedResponseThrowsActionableError()
    {
        var assistant = CreateAssistant(new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"))));
        var action = () => assistant.ImproveSqlAsync("SELECT 1", TestMetadata.Create(), "Improve", CancellationToken.None);
        await action.Should().ThrowAsync<OpenAiSqlAgentException>();
    }

    private static OpenAiSqlAgent CreateAssistant(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new OpenAiSqlAgentOptions
        {
            ApiKey = "test-key",
            Model = "test-model",
            Endpoint = new Uri("https://api.openai.test/v1/responses")
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private class TestableOpenAiSqlAgent : OpenAiSqlAgent
    {
        private readonly ToolApprovalProbe _approvalProbe;
        public string? LastQuery { get; private set; }
        public int ApprovalCount => _approvalProbe.Count;

        public TestableOpenAiSqlAgent(HttpMessageHandler handler, bool approveTools = true)
            : this(handler, new ToolApprovalProbe(approveTools))
        {
        }

        private TestableOpenAiSqlAgent(HttpMessageHandler handler, ToolApprovalProbe approvalProbe) : base(
            new HttpClient(handler),
            new OpenAiSqlAgentOptions
            {
                ApiKey = "test-key",
                Model = "test-model",
                Endpoint = new Uri("https://api.openai.test/v1/responses"),
                ToolApprovalHandler = approvalProbe.HandleAsync
            })
        {
            _approvalProbe = approvalProbe;
        }

        protected override Task<string> CallGraphQLToolAsync(string query, object? variables = null)
        {
            LastQuery = query;
            return Task.FromResult("{\"data\":{\"tablesList\":[{\"schema\":\"dbo\",\"name\":\"Users\"}]}}");
        }

        private sealed class ToolApprovalProbe(bool approveTools)
        {
            public int Count { get; private set; }

            public Task<bool> HandleAsync(OpenAiSqlToolCall toolCall, CancellationToken cancellationToken)
            {
                Count++;
                return Task.FromResult(approveTools);
            }
        }
    }
}
