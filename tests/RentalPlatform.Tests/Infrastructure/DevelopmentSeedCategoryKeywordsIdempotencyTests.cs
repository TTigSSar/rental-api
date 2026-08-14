using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Admin console Phase 6 ("Needs category fix"): the dev seed's CategoryKeyword rows
// (DevelopmentSeedRunner.SeedCategoryKeywordsAsync) must be idempotent — re-running the seed
// against a database that already has them must not duplicate any row. Same convention as
// DevelopmentSeedReportsIdempotencyTests: exercises the REAL DevelopmentSeedRunner.RunAsync end to
// end, with outbound image downloads intercepted by a fake handler that always 404s so this stays
// fast and network-free.
public sealed class DevelopmentSeedCategoryKeywordsIdempotencyTests
{
    private sealed class NeverSucceedsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static DevelopmentSeedRunner CreateRunner(AppDbContext context) => new(
        context,
        new BcryptPasswordHasher(),
        NullLogger<DevelopmentSeedRunner>.Instance,
        new FakeFileStorageService(),
        new HttpClient(new NeverSucceedsHandler()));

    [Fact]
    public async Task RunAsync_Seeds_CategoryKeywords_For_Every_Seeded_Category_Exactly_Once()
    {
        using var db = new SqliteTestDatabase();

        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var keywords = await verify.CategoryKeywords.ToListAsync();

        Assert.Equal(DevelopmentSeedData.CategoryKeywords.Length, keywords.Count);
        // No duplicate (CategoryId, Keyword) pairs.
        Assert.Equal(
            keywords.Count,
            keywords.Select(k => (k.CategoryId, Keyword: k.Keyword.ToLowerInvariant())).Distinct().Count());

        var categoryIdBySlug = await verify.Categories
            .ToDictionaryAsync(c => c.Slug, c => c.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var slug in DevelopmentSeedData.CategoryKeywords.Select(k => k.CategorySlug).Distinct())
        {
            var categoryId = categoryIdBySlug[slug];
            Assert.True(
                keywords.Any(k => k.CategoryId == categoryId),
                $"Expected at least one keyword seeded for category slug '{slug}'.");
        }
    }

    [Fact]
    public async Task RunAsync_Run_Twice_Does_Not_Duplicate_CategoryKeywords()
    {
        using var db = new SqliteTestDatabase();

        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        int firstRunCount;
        await using (var check = db.CreateContext())
        {
            firstRunCount = await check.CategoryKeywords.CountAsync();
        }

        // Re-run the ENTIRE seed pipeline against the same database — the real startup scenario
        // (app restarts against a DB that already has demo data).
        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var secondRunCount = await verify.CategoryKeywords.CountAsync();

        Assert.Equal(DevelopmentSeedData.CategoryKeywords.Length, firstRunCount);
        Assert.Equal(firstRunCount, secondRunCount);
    }
}
