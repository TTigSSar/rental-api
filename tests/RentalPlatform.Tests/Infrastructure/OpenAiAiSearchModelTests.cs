using Xunit;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;

namespace RentalPlatform.Tests.Infrastructure;

// The adapter against a stub HttpMessageHandler: request shape, answers, and the never-throws contract.
public sealed class OpenAiAiSearchModelTests
{
    private const string Key = "sk-test-key";

    private static readonly AiSearchCatalogs Catalogs = new(
        new[] { new AiCatalogCategory("puzzles", "Puzzles"), new AiCatalogCategory("outdoor-toys", "Outdoor Toys") },
        new[] { new AiCatalogDistrict("kentron", "Kentron", "Կենտրոն", "Кентрон") });

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        public HttpRequestMessage? Request { get; private set; }
        public JsonObject? Body { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)) as JsonObject;
            return await Respond(request);
        }
    }

    private static HttpResponseMessage Completion(JsonNode content, int promptTokens = 1000, int completionTokens = 200) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content.ToJsonString() }
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens }
            }.ToJsonString())
        };

    private static (OpenAiAiSearchModel Model, StubHandler Handler) Create(
        AiSearchOptions? options = null, TimeSpan? timeout = null)
    {
        var handler = new StubHandler();
        var model = new OpenAiAiSearchModel(
            new HttpClient(handler),
            Options.Create(options ?? new AiSearchOptions { ApiKey = Key }),
            NullLogger<OpenAiAiSearchModel>.Instance,
            timeout);
        return (model, handler);
    }

    private static JsonObject ParseAnswer() => new()
    {
        ["ageMonths"] = 36,
        ["categorySlug"] = "puzzles",
        ["maxPricePerDay"] = null,
        ["priceIsHard"] = false,
        ["districtCodes"] = new JsonArray("kentron"),
        ["districtIsHard"] = false,
        ["nearMe"] = false,
        ["datesMentioned"] = true,
        ["itemType"] = null,
        ["purpose"] = "educational",
        ["intent"] = "A puzzle"
    };

    [Fact]
    public async Task Parse_SendsPlainOpenAiChatCompletionsRequest()
    {
        var (model, handler) = Create(new AiSearchOptions { ApiKey = Key, BaseUrl = "https://example.test/v1/" });
        handler.Respond = _ => Task.FromResult(Completion(ParseAnswer()));

        await model.ParseAsync("a puzzle for my 3 year old", "en", Catalogs);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://example.test/v1/chat/completions", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, handler.Request.Headers.Authorization.Parameter);

        var body = handler.Body!;
        Assert.Equal("gpt-6-luna", (string?)body["model"]);
        Assert.Equal("low", (string?)body["reasoning_effort"]);
        Assert.True((int)body["max_completion_tokens"]! >= 4096);
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["provider"]);
        Assert.Null(body["reasoning"]);

        var messages = body["messages"]!.AsArray();
        Assert.Equal("system", (string?)messages[0]!["role"]);
        Assert.Equal("user", (string?)messages[1]!["role"]);
        // The description is data in the user turn, never part of the system prompt.
        Assert.Equal("a puzzle for my 3 year old", (string?)messages[1]!["content"]);
        Assert.DoesNotContain("a puzzle for my 3 year old", (string?)messages[0]!["content"]);

        var format = body["response_format"]!;
        Assert.Equal("json_schema", (string?)format["type"]);
        Assert.True((bool)format["json_schema"]!["strict"]!);
        Assert.False(string.IsNullOrEmpty((string?)format["json_schema"]!["name"]));
        AssertStrict(format["json_schema"]!["schema"]!);
    }

    // Strict mode: every property is required and every object closes additionalProperties.
    private static void AssertStrict(JsonNode schema)
    {
        if (schema is JsonObject obj)
        {
            if ((string?)obj["type"] == "object")
            {
                Assert.False((bool)obj["additionalProperties"]!);
                var required = obj["required"]!.AsArray().Select(n => (string)n!).OrderBy(n => n).ToList();
                var properties = obj["properties"]!.AsObject().Select(p => p.Key).OrderBy(n => n).ToList();
                Assert.Equal(properties, required);
            }

            foreach (var child in obj)
            {
                if (child.Value is not null)
                {
                    AssertStrict(child.Value);
                }
            }
        }
        else if (schema is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    AssertStrict(item);
                }
            }
        }
    }

    [Fact]
    public async Task Parse_ReturnsParsedQueryWithModelAndCost()
    {
        var (model, handler) = Create(new AiSearchOptions
        {
            ApiKey = Key,
            ParseInputUsdPerMTok = 0.10m,
            ParseOutputUsdPerMTok = 0.50m
        });
        handler.Respond = _ => Task.FromResult(Completion(ParseAnswer(), promptTokens: 2_000_000, completionTokens: 1_000_000));

        var result = await model.ParseAsync("puzzle", "en", Catalogs);

        Assert.NotNull(result);
        Assert.Equal("gpt-6-luna", result!.Model);
        Assert.Equal(2_000_000, result.InputTokens);
        Assert.Equal(1_000_000, result.OutputTokens);
        // 2 M x 0.10 + 1 M x 0.50
        Assert.Equal(0.70m, result.CostUsd);
        Assert.Equal(36, result.Value.AgeMonths);
        Assert.Equal("puzzles", result.Value.CategorySlug);
        Assert.Null(result.Value.MaxPricePerDay);
        Assert.Equal(new[] { "kentron" }, result.Value.DistrictCodes);
        Assert.True(result.Value.DatesMentioned);
        Assert.Equal("A puzzle", result.Value.Intent);
    }

    [Fact]
    public async Task Rank_SendsListingsAndReturnsOrderedIds()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (model, handler) = Create(new AiSearchOptions
        {
            ApiKey = Key,
            RankModel = "rank-model",
            RankInputUsdPerMTok = 1m,
            RankOutputUsdPerMTok = 2m
        });
        handler.Respond = _ => Task.FromResult(Completion(
            new JsonObject { ["orderedIds"] = new JsonArray(2, 1) }, 500_000, 250_000));

        var result = await model.RankAsync(
            "something quiet",
            new[]
            {
                new AiRankCandidate(a, "Blocks", "building-blocks", 10m, 12, 36, "Wooden blocks"),
                new AiRankCandidate(b, "Puzzle", "puzzles", 8m, null, null, "Big puzzle")
            });

        Assert.NotNull(result);
        Assert.Equal(new[] { b, a }, result!.Value);
        Assert.Equal("rank-model", result.Model);
        Assert.Equal(1.0m, result.CostUsd); // 0.5 x 1 + 0.25 x 2

        Assert.Equal("rank-model", (string?)handler.Body!["model"]);
        var user = (string)handler.Body["messages"]![1]!["content"]!;
        Assert.Contains("something quiet", user);
        Assert.DoesNotContain(a.ToString(), user);
        Assert.DoesNotContain(b.ToString(), user);
        var lines = user.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith('{')).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(JsonValueKind.Number, lines[0]["id"]!.GetValueKind());
        Assert.Equal(1, (int)lines[0]["id"]!);
        Assert.Equal(2, (int)lines[1]["id"]!);

        var schema = handler.Body["response_format"]!["json_schema"]!["schema"]!;
        AssertStrict(schema);
        Assert.Equal("integer", (string?)schema["properties"]!["orderedIds"]!["items"]!["type"]);
    }

    [Fact]
    public async Task Rank_MapsNumbersToGuids_DroppingOutOfRangeAndDuplicates()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var (model, handler) = Create();
        handler.Respond = _ => Task.FromResult(Completion(
            new JsonObject { ["orderedIds"] = new JsonArray(3, 0, 4, -1, 3, 1, 1) }));

        var result = await model.RankAsync(
            "x",
            new[]
            {
                new AiRankCandidate(a, "A", "cat", 1m, null, null, "a"),
                new AiRankCandidate(b, "B", "cat", 1m, null, null, "b"),
                new AiRankCandidate(c, "C", "cat", 1m, null, null, "c")
            });

        Assert.Equal(new[] { c, a }, result!.Value);
    }

    [Fact]
    public async Task HttpError_ReturnsNull()
    {
        var (model, handler) = Create();
        handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        Assert.Null(await model.ParseAsync("puzzle", "en", Catalogs));
        Assert.Null(await model.RankAsync("puzzle", Array.Empty<AiRankCandidate>()));
    }

    [Fact]
    public async Task Timeout_IsChargedAPessimisticEstimate()
    {
        var slow = new SlowHandler();
        var model = new OpenAiAiSearchModel(
            new HttpClient(slow),
            Options.Create(new AiSearchOptions { ApiKey = Key, ParseInputUsdPerMTok = 1m, ParseOutputUsdPerMTok = 2m }),
            NullLogger<OpenAiAiSearchModel>.Instance, TimeSpan.FromMilliseconds(50));

        var result = await model.ParseAsync("puzzle", "en", Catalogs);

        Assert.Equal(1, slow.Calls);
        Assert.NotNull(result);
        Assert.True(result!.IsFailed);
        Assert.Equal(4096, result.OutputTokens); // the full max_completion_tokens allowance
        Assert.True(result.InputTokens > 0);
        Assert.Equal((result.InputTokens * 1m + 4096 * 2m) / 1_000_000m, result.CostUsd);
    }

    [Fact]
    public async Task CallerAbort_StopsTheProviderCall_AndIsChargedAPessimisticEstimate()
    {
        var slow = new SlowHandler();
        var model = new OpenAiAiSearchModel(
            new HttpClient(slow), Options.Create(new AiSearchOptions { ApiKey = Key }),
            NullLogger<OpenAiAiSearchModel>.Instance);
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await model.ParseAsync("puzzle", "en", Catalogs, abort.Token);

        Assert.NotNull(result);
        Assert.True(result!.IsFailed);
        Assert.True(result.CostUsd > 0m);
    }

    [Fact]
    public async Task UsageWithUnusableContent_KeepsTheBilledCost()
    {
        var (model, handler) = Create(new AiSearchOptions { ApiKey = Key, ParseInputUsdPerMTok = 1m, ParseOutputUsdPerMTok = 2m });
        // finish_reason=length: reasoning ate the budget, content is empty, but tokens were billed.
        handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["finish_reason"] = "length",
                    ["message"] = new JsonObject { ["content"] = "" }
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1_000_000, ["completion_tokens"] = 500_000 }
            }.ToJsonString())
        });

        var result = await model.ParseAsync("puzzle", "en", Catalogs);

        Assert.NotNull(result);
        Assert.True(result!.IsFailed);
        Assert.Equal(2.0m, result.CostUsd); // 1 M x 1 + 0.5 M x 2
    }

    private sealed class CapturingLogger : ILogger<OpenAiAiSearchModel>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task FinishReasonLength_WithCutOffContent_IsABilledFailureLoggedApart()
    {
        var logger = new CapturingLogger();
        var handler = new StubHandler();
        var model = new OpenAiAiSearchModel(
            new HttpClient(handler),
            Options.Create(new AiSearchOptions { ApiKey = Key, ParseInputUsdPerMTok = 1m, ParseOutputUsdPerMTok = 2m }),
            logger);
        // Truncated mid-string: not valid JSON, and the secret-looking text must never reach the log.
        handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["finish_reason"] = "length",
                    ["message"] = new JsonObject { ["content"] = "{\"ageMonths\":36,\"intent\":\"SECRET-DESCRIPTION" }
                }),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 1_000_000,
                    ["completion_tokens"] = 4096,
                    ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 4000 }
                }
            }.ToJsonString())
        });

        var result = await model.ParseAsync("puzzle", "en", Catalogs);

        Assert.NotNull(result);
        Assert.True(result!.IsFailed);
        Assert.Equal(4096, result.OutputTokens);
        Assert.Equal((1_000_000m * 1m + 4096 * 2m) / 1_000_000m, result.CostUsd);
        var line = Assert.Single(logger.Messages, m => m.Contains("max_completion_tokens"));
        Assert.Contains("4000 reasoning", line);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SECRET") || m.Contains(Key));
    }

    [Fact]
    public async Task Parse_BoundsDistrictCodesToTheCatalogue()
    {
        var (model, handler) = Create();
        handler.Respond = _ => Task.FromResult(Completion(ParseAnswer()));

        await model.ParseAsync("puzzle", "en", Catalogs);

        var districts = handler.Body!["response_format"]!["json_schema"]!["schema"]!["properties"]!["districtCodes"]!;
        Assert.Equal(Catalogs.Districts.Count, (int)districts["maxItems"]!);
    }

    [Fact]
    public async Task UsageWithUnreadableShape_KeepsTheBilledCost()
    {
        var (model, handler) = Create(new AiSearchOptions { ApiKey = Key, RankInputUsdPerMTok = 1m, RankOutputUsdPerMTok = 1m });
        handler.Respond = _ => Task.FromResult(Completion(new JsonObject { ["nope"] = 1 }, 1_000_000, 1_000_000));

        var result = await model.RankAsync("x", new[] { new AiRankCandidate(Guid.NewGuid(), "A", "c", 1m, null, null, "a") });

        Assert.NotNull(result);
        Assert.True(result!.IsFailed);
        Assert.Equal(2.0m, result.CostUsd);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task UnusableContent_ReturnsNull(string content)
    {
        var (model, handler) = Create();
        handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } })
            }.ToJsonString())
        });

        Assert.Null(await model.ParseAsync("puzzle", "en", Catalogs));
    }

    [Fact]
    public async Task Refusal_ReturnsNull()
    {
        var (model, handler) = Create();
        handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = null, ["refusal"] = "no" }
                })
            }.ToJsonString())
        });

        Assert.Null(await model.ParseAsync("puzzle", "en", Catalogs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingOrEmptyKey_ReturnsNullWithoutCalling(string? key)
    {
        var (model, handler) = Create(new AiSearchOptions { ApiKey = key });

        Assert.Null(await model.ParseAsync("puzzle", "en", Catalogs));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Defaults_PointAtOpenAiDirect()
    {
        var options = new AiSearchOptions();

        Assert.Equal("https://api.openai.com/v1", options.BaseUrl);
        Assert.Equal("OPENAI_API_KEY", options.ApiKeyEnvironmentVariable);
        Assert.Equal("gpt-6-luna", options.ParseModel);
        Assert.Equal("gpt-6-luna", options.RankModel);
        Assert.Equal(0.10m, options.RankInputUsdPerMTok);
        Assert.Equal(0.50m, options.RankOutputUsdPerMTok);
        Assert.True(AiSearchOptions.IsValid(options));
    }
}
