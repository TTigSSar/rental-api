using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// AI search end to end through the real pipeline (ADR-054): auth, rate limit, binding, the service, the
// real catalogue query on SQLite, the log store. Only the LLM is replaced (FakeAiSearchModel), so each
// test scripts the model and reads its call counters.
//
// The host and its database are shared by the whole collection, so every test makes its own category
// (unique slug) and its own user, and narrows the scripted parse to that category.
[Collection("Integration")]
public sealed class AiSearchHttpTests
{
    private const string Url = "/api/listings/ai-search";
    private static readonly Guid ShengavitDistrictId = new("d000000c-0000-4000-9000-00000000000c");

    private readonly RentalPlatformWebAppFactory _factory;

    public AiSearchHttpTests(RentalPlatformWebAppFactory factory)
    {
        _factory = factory;
        _factory.AiModel.Reset();
    }

    private FakeAiSearchModel Model => _factory.AiModel;

    // ---- seeding helpers ---------------------------------------------------------------------------

    private sealed record Catalogue(Guid CategoryId, string Slug, Guid OwnerId, List<Listing> Listings);

    // Newest first: Listings[0] was created most recently.
    private async Task<Catalogue> SeedCatalogueAsync(
        int count,
        Guid? districtId = null,
        Action<Listing, int>? customise = null)
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var category = TestData.Category(categoryId, name: "AI Test Toys");
        var owner = TestData.OwnerWithHome(ownerId, $"{ownerId:N}@ai-search.local");

        var listings = new List<Listing>();
        for (var i = 0; i < count; i++)
        {
            var listing = TestData.Listing(Guid.NewGuid(), ownerId, categoryId);
            listing.Title = $"AI toy {i}";
            listing.CreatedAt = DateTime.UtcNow.AddMinutes(-i - 1);
            listing.DistrictId = districtId;
            customise?.Invoke(listing, i);
            listings.Add(listing);
        }

        await _factory.SeedAsync(new object[] { owner, category }.Concat(listings).ToArray());
        return new Catalogue(categoryId, category.Slug, ownerId, listings);
    }

    private async Task<(HttpClient Client, Guid UserId)> NewUserAsync(bool withHome = false)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@ai-search.local";
        var user = withHome ? TestData.OwnerWithHome(userId, email) : TestData.User(userId, email);
        await _factory.SeedAsync(user);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(userId, email));
        return (client, userId);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static List<Guid> Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static List<Guid> Guids(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetGuid()).ToList();

    // ---- the happy path ------------------------------------------------------------------------------

    [Fact]
    public async Task Page1_Parses_Applies_Filters_And_Returns_The_Models_Order()
    {
        var cat = await SeedCatalogueAsync(4, customise: (l, _) => { l.AgeFromMonths = 36; l.AgeToMonths = 72; });
        var (client, userId) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug, ageMonths: 40, maxPrice: 50, intent: "A sturdy toy for play");
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).Reverse().ToList();
        Model.ParseCostUsd = 0.002m;
        Model.RankCostUsd = 0.010m;

        var response = await client.PostAsJsonAsync(Url, new { description = "something for my 3 year old" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("ranked").GetBoolean());
        Assert.False(body.GetProperty("aiUnavailable").GetBoolean());
        Assert.Equal("A sturdy toy for play", body.GetProperty("intent").GetString());
        Assert.Equal(cat.Listings.Select(l => l.Id).Reverse(), Ids(body));

        var applied = body.GetProperty("appliedFilters");
        Assert.Equal(cat.CategoryId, applied.GetProperty("categoryId").GetGuid());
        Assert.Equal("36-72", applied.GetProperty("ageGroup").GetString());
        Assert.Equal(50m, applied.GetProperty("maxPrice").GetDecimal());

        Assert.Equal(1, Model.ParseCalls);
        Assert.Equal(1, Model.RankCalls);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await db.AiSearchLogs.AsNoTracking().SingleAsync(l => l.UserId == userId);
        Assert.Equal("ok", log.Outcome);
        Assert.Equal("fake-parse", log.ParseModel);
        Assert.Equal("fake-rank", log.RankModel);
        Assert.Equal(0.012m, log.CostUsd);
        Assert.Equal(300, log.InputTokens);
        Assert.Equal("something for my 3 year old", log.Description);
    }

    [Fact]
    public async Task The_Models_Ranking_Cannot_Add_Or_Drop_Listings()
    {
        var cat = await SeedCatalogueAsync(3);
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        // Invents an id, repeats one, and forgets the other two.
        Model.OnRank = (_, candidates) => new[] { Guid.NewGuid(), candidates[2].Id, candidates[2].Id }.ToList();

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "blocks" }));

        var expected = new[] { cat.Listings[2].Id, cat.Listings[0].Id, cat.Listings[1].Id };
        Assert.Equal(expected, Ids(body));
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Only_The_First_10_Of_A_20_Item_Page_Are_Ranked_The_Rest_Keep_Catalogue_Order()
    {
        var cat = await SeedCatalogueAsync(20);
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        IReadOnlyList<AiRankCandidate>? seen = null;
        Model.OnRank = (_, candidates) => { seen = candidates; return candidates.Select(c => c.Id).Reverse().ToList(); };

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "blocks" }));

        Assert.Equal(10, seen!.Count);
        var catalogue = cat.Listings.Select(l => l.Id).ToList();
        Assert.Equal(catalogue.Take(10).Reverse().Concat(catalogue.Skip(10)), Ids(body));
    }

    [Fact]
    public async Task Page2_With_ExcludeIds_Makes_No_Model_Call_And_Continues_In_CreatedAt_Order()
    {
        var cat = await SeedCatalogueAsync(25);
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).Reverse().ToList();

        var first = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "blocks" }));
        var firstIds = Ids(first);
        Assert.Equal(20, firstIds.Count);
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, Model.ParseCalls);
        Assert.Equal(1, Model.RankCalls);

        var applied = first.GetProperty("appliedFilters");
        var second = await ReadAsync(await client.PostAsJsonAsync(Url, new
        {
            description = "blocks",
            filters = new { categoryId = applied.GetProperty("categoryId").GetGuid() },
            page = 2,
            excludeIds = firstIds
        }));

        Assert.Equal(1, Model.ParseCalls);
        Assert.Equal(1, Model.RankCalls);

        var secondIds = Ids(second);
        Assert.Equal(5, secondIds.Count);
        Assert.Empty(secondIds.Intersect(firstIds));
        Assert.Equal(cat.Listings.Skip(20).Select(l => l.Id), secondIds);
        Assert.False(second.GetProperty("ranked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("intent").ValueKind);
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Equal(25, second.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Page2_Is_Not_Logged()
    {
        var cat = await SeedCatalogueAsync(3);
        var (client, userId) = await NewUserAsync();

        await client.PostAsJsonAsync(Url, new
        {
            description = "blocks",
            filters = new { categoryId = cat.CategoryId },
            page = 2,
            excludeIds = new[] { cat.Listings[0].Id }
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AiSearchLogs.AnyAsync(l => l.UserId == userId));
        Assert.Equal(0, Model.ParseCalls);
        Assert.Equal(0, Model.RankCalls);
    }

    [Fact]
    public async Task Rerank_With_Filters_Does_Not_Parse_And_Only_Ranks()
    {
        var cat = await SeedCatalogueAsync(3);
        var (client, _) = await NewUserAsync();
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).Reverse().ToList();

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new
        {
            description = "blocks",
            filters = new { categoryId = cat.CategoryId }
        }));

        Assert.Equal(0, Model.ParseCalls);
        Assert.Equal(1, Model.RankCalls);
        Assert.True(body.GetProperty("ranked").GetBoolean());
        Assert.Equal(cat.Listings.Select(l => l.Id).Reverse(), Ids(body));
        // The one-line intent comes from parsing; a re-rank has none.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("intent").ValueKind);
    }

    // ---- degradation -----------------------------------------------------------------------------------

    [Fact]
    public async Task Rank_Failure_Falls_Back_To_CreatedAt_Order_And_Is_Not_Ranked()
    {
        var cat = await SeedCatalogueAsync(3);
        var (client, userId) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        Model.OnRank = (_, _) => null;

        var response = await client.PostAsJsonAsync(Url, new { description = "blocks" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.False(body.GetProperty("ranked").GetBoolean());
        Assert.False(body.GetProperty("aiUnavailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("intent").ValueKind);
        Assert.Equal(cat.Listings.Select(l => l.Id), Ids(body));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("ranking_failed", (await db.AiSearchLogs.AsNoTracking().SingleAsync(l => l.UserId == userId)).Outcome);
    }

    [Fact]
    public async Task Parse_Failure_Is_A_200_AiUnavailable_Text_Search_On_The_Description()
    {
        var token = $"Zorblax{Guid.NewGuid():N}";
        var cat = await SeedCatalogueAsync(2, customise: (l, i) => { if (i == 1) l.Title = token; });
        var (client, userId) = await NewUserAsync();
        Model.OnParse = (_, _, _) => null;

        var response = await client.PostAsJsonAsync(Url, new { description = token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("aiUnavailable").GetBoolean());
        Assert.False(body.GetProperty("ranked").GetBoolean());
        Assert.Equal(new[] { cat.Listings[1].Id }, Ids(body));
        Assert.Equal(0, Model.RankCalls);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("ai_unavailable", (await db.AiSearchLogs.AsNoTracking().SingleAsync(l => l.UserId == userId)).Outcome);
    }

    [Fact]
    public async Task A_Spent_Budget_Turns_Status_Off_And_Posts_Never_Reach_The_Model()
    {
        var anonymous = _factory.CreateClient();
        var before = await ReadAsync(await anonymous.GetAsync(Url + "/status"));
        Assert.True(before.GetProperty("available").GetBoolean());

        var spent = new AiSearchLog
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            CostUsd = 5m,
            Outcome = "ok"
        };
        await _factory.SeedAsync(spent);

        try
        {
            var status = await anonymous.GetAsync(Url + "/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.False((await ReadAsync(status)).GetProperty("available").GetBoolean());

            var cat = await SeedCatalogueAsync(1);
            var (client, userId) = await NewUserAsync();
            Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);

            var response = await client.PostAsJsonAsync(Url, new { description = "blocks" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await ReadAsync(response)).GetProperty("aiUnavailable").GetBoolean());
            Assert.Equal(0, Model.ParseCalls);
            Assert.Equal(0, Model.RankCalls);

            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("budget_exhausted", (await db.AiSearchLogs.AsNoTracking().SingleAsync(l => l.UserId == userId)).Outcome);
        }
        finally
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The budget is process-wide in this shared host: leave it as we found it.
            await db.AiSearchLogs.Where(l => l.Id == spent.Id).ExecuteDeleteAsync();
        }
    }

    // ---- auth, validation, rate limit -------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_Post_Is_401_With_The_Error_Code()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(Url, new { description = "blocks" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("ai_search.unauthenticated", body.GetProperty("errorCode").GetString());
        Assert.Equal(0, Model.ParseCalls);
    }

    [Fact]
    public async Task Another_Endpoints_401_Keeps_Its_Empty_Body()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/listings", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_Blank_Description_Is_400_With_The_Error_Code(string description)
    {
        var (client, _) = await NewUserAsync();

        var response = await client.PostAsJsonAsync(Url, new { description });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ai_search.invalid_description", (await ReadAsync(response)).GetProperty("errorCode").GetString());
        Assert.Equal(0, Model.ParseCalls);
    }

    [Fact]
    public async Task A_501_Character_Description_Is_400_And_500_Is_Accepted()
    {
        var (client, _) = await NewUserAsync();

        var tooLong = await client.PostAsJsonAsync(Url, new { description = new string('a', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("ai_search.invalid_description", (await ReadAsync(tooLong)).GetProperty("errorCode").GetString());

        var ok = await client.PostAsJsonAsync(Url, new { description = new string('a', 500) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task The_Sixth_Search_Within_A_Minute_Is_429_With_The_Error_Code_And_Retry_After()
    {
        var (client, _) = await NewUserAsync();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, "198.51.100.77");

        for (var i = 0; i < 5; i++)
        {
            var allowed = await client.PostAsJsonAsync(Url, new { description = "blocks" });
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var rejected = await client.PostAsJsonAsync(Url, new { description = "blocks" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("ai_search.rate_limited", (await ReadAsync(rejected)).GetProperty("errorCode").GetString());
        Assert.True(rejected.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter!.Single(), CultureInfo.InvariantCulture) is >= 1 and <= 60);

        // The budget is per account: a different user from the same address is unaffected.
        var (other, _) = await NewUserAsync();
        other.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, "198.51.100.77");
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync(Url, new { description = "blocks" })).StatusCode);
    }

    // ---- per-user daily limit (ADR-054) -----------------------------------------------------------------

    private async Task SeedLogsAsync(Guid userId, int count, string outcome, DateTime createdAt)
    {
        var rows = Enumerable.Range(0, count)
            .Select(_ => new AiSearchLog { Id = Guid.NewGuid(), UserId = userId, CreatedAt = createdAt, Outcome = outcome })
            .Cast<object>()
            .ToArray();
        await _factory.SeedAsync(rows);
    }

    private static DateTime YerevanMidnightUtc() => YerevanCalendar.StartOfTodayUtc(DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_50th_Search_Of_The_Day_Is_Allowed_And_The_51st_Is_429_Daily_Limit_Without_Retry_After()
    {
        var cat = await SeedCatalogueAsync(1);
        var (client, userId) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        await SeedLogsAsync(userId, 49, "ok", DateTime.UtcNow);

        var fiftieth = await client.PostAsJsonAsync(Url, new { description = "blocks" });
        Assert.Equal(HttpStatusCode.OK, fiftieth.StatusCode);
        var parseCallsAfter50th = Model.ParseCalls;

        var rejected = await client.PostAsJsonAsync(Url, new { description = "blocks" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("ai_search.daily_limit", (await ReadAsync(rejected)).GetProperty("errorCode").GetString());
        Assert.False(rejected.Headers.Contains("Retry-After"));
        Assert.Equal(parseCallsAfter50th, Model.ParseCalls);
        Assert.Equal(1, parseCallsAfter50th);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(50, await db.AiSearchLogs.CountAsync(l => l.UserId == userId));
    }

    [Fact]
    public async Task Ranking_Failed_Counts_Toward_The_Daily_Limit()
    {
        var (client, userId) = await NewUserAsync();
        await SeedLogsAsync(userId, 25, "ok", DateTime.UtcNow);
        await SeedLogsAsync(userId, 25, "ranking_failed", DateTime.UtcNow);

        var response = await client.PostAsJsonAsync(Url, new { description = "blocks" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Fallback_Rows_Other_Users_And_Rows_Before_Yerevan_Midnight_Do_Not_Count()
    {
        var cat = await SeedCatalogueAsync(1);
        var (client, userId) = await NewUserAsync();
        var (_, otherId) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        await SeedLogsAsync(userId, 30, "ai_unavailable", DateTime.UtcNow);
        await SeedLogsAsync(userId, 30, "budget_exhausted", DateTime.UtcNow);
        await SeedLogsAsync(otherId, 60, "ok", DateTime.UtcNow);
        await SeedLogsAsync(userId, 60, "ok", YerevanMidnightUtc().AddMinutes(-1));

        var response = await client.PostAsJsonAsync(Url, new { description = "blocks" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadAsync(response)).GetProperty("aiUnavailable").GetBoolean());
    }

    [Fact]
    public async Task Page_2_Is_Never_Limited()
    {
        var cat = await SeedCatalogueAsync(3);
        var (client, userId) = await NewUserAsync();
        await SeedLogsAsync(userId, 50, "ok", DateTime.UtcNow);

        var response = await client.PostAsJsonAsync(Url, new
        {
            description = "blocks",
            filters = new { categoryId = cat.CategoryId },
            page = 2,
            excludeIds = new[] { cat.Listings[0].Id }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rerank_With_Filters_Is_Also_Limited_And_Makes_No_Model_Call()
    {
        var cat = await SeedCatalogueAsync(3);
        var (client, userId) = await NewUserAsync();
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).ToList();
        await SeedLogsAsync(userId, 50, "ok", DateTime.UtcNow);

        var response = await client.PostAsJsonAsync(Url, new
        {
            description = "blocks",
            filters = new { categoryId = cat.CategoryId }
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("ai_search.daily_limit", (await ReadAsync(response)).GetProperty("errorCode").GetString());
        Assert.Equal(0, Model.ParseCalls);
        Assert.Equal(0, Model.RankCalls);
    }

    [Fact]
    public async Task A_Failed_Parse_Does_Not_Count_Toward_The_Daily_Limit()
    {
        var cat = await SeedCatalogueAsync(1);
        var (client, userId) = await NewUserAsync();
        await SeedLogsAsync(userId, 49, "ok", DateTime.UtcNow);

        Model.OnParse = (_, _, _) => null;
        var failed = await client.PostAsJsonAsync(Url, new { description = "blocks" });
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.True((await ReadAsync(failed)).GetProperty("aiUnavailable").GetBoolean());

        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        var fiftieth = await client.PostAsJsonAsync(Url, new { description = "blocks" });
        Assert.Equal(HttpStatusCode.OK, fiftieth.StatusCode);
        Assert.False((await ReadAsync(fiftieth)).GetProperty("aiUnavailable").GetBoolean());

        var rejected = await client.PostAsJsonAsync(Url, new { description = "blocks" });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("ai_search.daily_limit", (await ReadAsync(rejected)).GetProperty("errorCode").GetString());
    }

    // ---- widening ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Widening_Adds_Neighbouring_Districts_But_Never_Touches_Age_Or_Price()
    {
        // Arabkir is named; nothing is there. Kentron borders it.
        var cat = await SeedCatalogueAsync(
            4,
            districtId: TestData.KentronDistrictId,
            customise: (l, i) =>
            {
                l.AgeFromMonths = 36; l.AgeToMonths = 72; l.PricePerDay = 30m;
                if (i == 1) { l.AgeFromMonths = 0; l.AgeToMonths = 12; }     // wrong age: must stay out
                if (i == 2) { l.PricePerDay = 100m; }                          // over budget: must stay out
            });
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(
            cat.Slug, ageMonths: 40, maxPrice: 50, districts: new[] { "arabkir" });
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).ToList();

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "blocks near arabkir" }));

        Assert.Equal(new[] { cat.Listings[0].Id, cat.Listings[3].Id }, Ids(body));
        var widened = Guids(body.GetProperty("widenedDistrictIds"));
        Assert.Contains(TestData.KentronDistrictId, widened);
        Assert.DoesNotContain(TestData.ArabkirDistrictId, widened);

        var applied = body.GetProperty("appliedFilters");
        Assert.Equal("36-72", applied.GetProperty("ageGroup").GetString());
        Assert.Equal(50m, applied.GetProperty("maxPrice").GetDecimal());
        var appliedDistricts = Guids(applied.GetProperty("districtIds"));
        Assert.Contains(TestData.ArabkirDistrictId, appliedDistricts);
        Assert.Contains(TestData.KentronDistrictId, appliedDistricts);
        Assert.False(body.GetProperty("purposeWidened").GetBoolean());
    }

    [Fact]
    public async Task A_Hard_District_Is_Never_Widened()
    {
        var cat = await SeedCatalogueAsync(2, districtId: TestData.KentronDistrictId);
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(
            cat.Slug, districts: new[] { "arabkir" }, districtIsHard: true);

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "only arabkir" }));

        Assert.Empty(Ids(body));
        Assert.Empty(Guids(body.GetProperty("widenedDistrictIds")));
        Assert.False(body.GetProperty("purposeWidened").GetBoolean());
        Assert.Equal(new[] { TestData.ArabkirDistrictId },
            Guids(body.GetProperty("appliedFilters").GetProperty("districtIds")));
    }

    [Fact]
    public async Task Step_Two_Drops_The_Category_For_A_Purpose_But_Keeps_Age_Price_And_Districts()
    {
        // Nothing in the named category; a good match sits in another category in the same district.
        var empty = await SeedCatalogueAsync(0);
        var other = await SeedCatalogueAsync(
            1, districtId: ShengavitDistrictId,
            customise: (l, _) => { l.AgeFromMonths = 36; l.AgeToMonths = 72; l.PricePerDay = 30m; });
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(
            empty.Slug, ageMonths: 40, maxPrice: 50, districts: new[] { "shengavit" }, districtIsHard: true, purpose: "outdoor play");
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).ToList();

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "outdoor toy in shengavit only" }));

        Assert.True(body.GetProperty("purposeWidened").GetBoolean());
        Assert.Contains(other.Listings[0].Id, Ids(body));
        var applied = body.GetProperty("appliedFilters");
        Assert.Equal(JsonValueKind.Null, applied.GetProperty("categoryId").ValueKind);
        Assert.Equal("36-72", applied.GetProperty("ageGroup").GetString());
        Assert.Equal(50m, applied.GetProperty("maxPrice").GetDecimal());
        Assert.Equal(new[] { ShengavitDistrictId }, Guids(applied.GetProperty("districtIds")));
        Assert.All(body.GetProperty("items").EnumerateArray(), item => Assert.True(item.GetProperty("pricePerDay").GetDecimal() <= 50m));
    }

    // ---- privacy -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Near_Me_Uses_The_Home_Point_On_The_Server_And_It_Never_Reaches_The_Model()
    {
        var (publicLat, publicLng) = new GeohashSnapper().SnapToCellCenter(TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        var cat = await SeedCatalogueAsync(
            2,
            districtId: TestData.KentronDistrictId,
            customise: (l, i) =>
            {
                l.PublicLatitude = publicLat;
                l.PublicLongitude = publicLng;
                // The second listing is far away, outside the 1 km radius.
                if (i == 1) { l.PublicLatitude = 40.12m; l.PublicLongitude = 44.50m; }
            });
        var (client, userId) = await NewUserAsync(withHome: true);
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug, nearMe: true);
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).ToList();

        var response = await client.PostAsJsonAsync(Url, new { description = "something near me" });
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var body = doc.RootElement;

        Assert.Equal(new[] { cat.Listings[0].Id }, Ids(body));
        var applied = body.GetProperty("appliedFilters");
        Assert.True(applied.GetProperty("nearHome").GetBoolean());
        Assert.Equal(1.0, applied.GetProperty("radiusKm").GetDouble());
        Assert.False(body.GetProperty("locationNotConsidered").GetBoolean());

        var lat = TestData.KentronPoint.Latitude.ToString(CultureInfo.InvariantCulture);
        var lng = TestData.KentronPoint.Longitude.ToString(CultureInfo.InvariantCulture);

        // Nothing identifying or exact was given to the model ...
        var sent = string.Join("\n", Model.Inputs);
        Assert.DoesNotContain(lat, sent);
        Assert.DoesNotContain(lng, sent);
        Assert.DoesNotContain(userId.ToString(), sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ai-search.local", sent);
        // ... and the home point is not echoed back to the client either.
        Assert.DoesNotContain(lat, raw);
        Assert.DoesNotContain(lng, raw);
    }

    [Fact]
    public async Task Near_Me_Without_A_Home_Point_Is_Flagged_And_Not_Applied()
    {
        var cat = await SeedCatalogueAsync(2);
        var (client, _) = await NewUserAsync(withHome: false);
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug, nearMe: true);
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).ToList();

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "something near me" }));

        Assert.True(body.GetProperty("locationNotConsidered").GetBoolean());
        Assert.False(body.GetProperty("appliedFilters").GetProperty("nearHome").GetBoolean());
        Assert.Equal(2, Ids(body).Count);
    }

    [Fact]
    public async Task Candidates_Sent_For_Ranking_Carry_Public_Fields_Trimmed_To_200_Characters()
    {
        var cat = await SeedCatalogueAsync(2, customise: (l, _) => l.Description = new string('x', 450));
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug);
        IReadOnlyList<AiRankCandidate>? seen = null;
        Model.OnRank = (_, candidates) => { seen = candidates; return candidates.Select(c => c.Id).ToList(); };

        await client.PostAsJsonAsync(Url, new { description = "blocks" });

        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Count);
        Assert.All(seen, c => Assert.Equal(200, c.Description.Length));
    }

    // ---- intent line -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("A toy for a 3 year old")]
    [InlineData("A toy near Kentron")]
    [InlineData("Игрушка в Кентрон Кентрон")]
    public async Task An_Intent_With_A_Digit_Or_A_District_Name_Is_Dropped(string intent)
    {
        var cat = await SeedCatalogueAsync(2);
        var (client, _) = await NewUserAsync();
        Model.OnParse = (_, _, _) => FakeAiSearchModel.Parsed(cat.Slug, intent: intent);
        Model.OnRank = (_, candidates) => candidates.Select(c => c.Id).ToList();

        var body = await ReadAsync(await client.PostAsJsonAsync(Url, new { description = "blocks" }));

        Assert.True(body.GetProperty("ranked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("intent").ValueKind);
    }

    [Fact]
    public async Task The_Intent_Language_Follows_Accept_Language()
    {
        var cat = await SeedCatalogueAsync(1);
        var (client, _) = await NewUserAsync();
        string? languageSeen = null;
        Model.OnParse = (_, language, _) => { languageSeen = language; return FakeAiSearchModel.Parsed(cat.Slug); };
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("hy-AM,hy;q=0.9,en;q=0.5");

        await client.PostAsJsonAsync(Url, new { description = "blocks" });
        Assert.Equal("hy", languageSeen);

        client.DefaultRequestHeaders.AcceptLanguage.Clear();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de");
        await client.PostAsJsonAsync(Url, new { description = "blocks" });
        Assert.Equal("en", languageSeen);
    }

    // ---- public catalogue stays public ------------------------------------------------------------------

    [Fact]
    public async Task The_Public_Listings_Endpoint_Does_Not_Bind_ExcludeIds_From_The_Query_String()
    {
        var cat = await SeedCatalogueAsync(2);

        var response = await _factory.CreateClient().GetAsync(
            $"/api/listings?categoryId={cat.CategoryId}&excludeIds={cat.Listings[0].Id}");

        var body = await ReadAsync(response);
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
    }

    // ---- retention ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_Nulls_Descriptions_Older_Than_30_Days_And_Keeps_The_Metadata_Row()
    {
        var old = new AiSearchLog
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddDays(-31), Description = "old text",
            CostUsd = 0.001m, Outcome = "ok"
        };
        var recent = new AiSearchLog
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddDays(-29), Description = "recent text",
            CostUsd = 0.001m, Outcome = "ok"
        };
        await _factory.SeedAsync(old, recent);

        var cleanup = new AiSearchLogCleanupService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AiSearchLogCleanupService>.Instance);

        var cleared = await cleanup.SweepAsync();

        Assert.True(cleared >= 1);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var oldRow = await db.AiSearchLogs.AsNoTracking().SingleAsync(l => l.Id == old.Id);
        Assert.Null(oldRow.Description);
        Assert.Equal(0.001m, oldRow.CostUsd);
        Assert.Equal("recent text", (await db.AiSearchLogs.AsNoTracking().SingleAsync(l => l.Id == recent.Id)).Description);
    }
}
