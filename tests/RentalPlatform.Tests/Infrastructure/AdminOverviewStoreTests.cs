using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Admin console Phase 5: exercises AdminOverviewStore.GetStatsAsync directly (not through the
// service's own DateTime.UtcNow) so the trailing-7-day / trailing-30-day window edges can be
// asserted against an exact, caller-supplied "now" instead of racing the wall clock.
public sealed class AdminOverviewStoreTests
{
    private static readonly Guid OwnerId = new("e5000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("e5000000-0000-0000-0000-000000000002");

    private static Listing MakeListing(
        Guid ownerId, Guid categoryId, ListingStatus status, DateTime createdAt, DateTime? moderatedAt) =>
        new Listing
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            CategoryId = categoryId,
            Title = "Boundary listing",
            Description = "Boundary test listing.",
            PricePerDay = 10m,
            Currency = "AMD",
            Country = "Armenia",
            City = "Yerevan",
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ModeratedAt = moderatedAt
        };

    [Fact]
    public async Task GetStatsAsync_LiveListingsAddedThisWeek_Is_Inclusive_At_Exactly_Seven_Days()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
        var exactlySevenDaysAgo = now.AddDays(-7);
        var justOverSevenDaysAgo = now.AddDays(-7).AddSeconds(-1);

        await db.SeedAsync(
            MakeListing(OwnerId, CategoryId, ListingStatus.Approved, now.AddDays(-20), exactlySevenDaysAgo),
            MakeListing(OwnerId, CategoryId, ListingStatus.Approved, now.AddDays(-20), justOverSevenDaysAgo));

        await using var context = db.CreateContext();
        var stats = await new AdminOverviewStore(context).GetStatsAsync(now);

        // Exactly 7 days ago is still "within the trailing 7 days" (>=); one second further back
        // is not.
        Assert.Equal(2, stats.LiveListingCount);
        Assert.Equal(1, stats.LiveListingsAddedThisWeek);
    }

    [Fact]
    public async Task GetStatsAsync_AverageReviewTimeMinutes_Is_Inclusive_At_Exactly_Thirty_Days()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
        var exactlyThirtyDaysAgo = now.AddDays(-30);
        var justOverThirtyDaysAgo = now.AddDays(-30).AddSeconds(-1);

        await db.SeedAsync(
            // Moderated exactly 30 days ago, took 30 minutes -- in the window.
            MakeListing(OwnerId, CategoryId, ListingStatus.Approved, exactlyThirtyDaysAgo.AddMinutes(-30), exactlyThirtyDaysAgo),
            // Moderated one second further back, took 9999 minutes -- outside the window; if this
            // leaked in it would grossly skew the mean.
            MakeListing(OwnerId, CategoryId, ListingStatus.Approved, justOverThirtyDaysAgo.AddMinutes(-9999), justOverThirtyDaysAgo));

        await using var context = db.CreateContext();
        var stats = await new AdminOverviewStore(context).GetStatsAsync(now);

        Assert.NotNull(stats.AverageReviewTimeMinutes);
        Assert.Equal(30.0, stats.AverageReviewTimeMinutes!.Value, 2);
    }

    // ---------- SHOULD-FIX 4: resubmission skew ----------

    [Fact]
    public async Task GetStatsAsync_AverageReviewTimeMinutes_Uses_Last_Rejection_Not_Original_CreatedAt()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

        // Originally created 10 days ago; rejected, fixed by the owner, and resubmitted; the
        // second (real) review ran from 6 minutes ago to 1 minute ago, i.e. took 5 minutes — but
        // CreatedAt is 10 days back. Without the fix this listing alone would report ~14400
        // minutes of "review time" instead of 5.
        var originalCreatedAt = now.AddDays(-10);
        var lastRejectedAt = now.AddMinutes(-6);
        var approvedAt = now.AddMinutes(-1);
        var listing = MakeListing(OwnerId, CategoryId, ListingStatus.Approved, originalCreatedAt, approvedAt);

        await db.SeedAsync(listing);
        await db.SeedAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = OwnerId,
            Action = ModerationAction.ListingRejected,
            TargetType = ModerationTargetType.Listing,
            TargetId = listing.Id,
            TargetLabel = listing.Title,
            DetailJson = null,
            CreatedAt = lastRejectedAt
        });

        await using var context = db.CreateContext();
        var stats = await new AdminOverviewStore(context).GetStatsAsync(now);

        Assert.NotNull(stats.AverageReviewTimeMinutes);
        Assert.Equal(5.0, stats.AverageReviewTimeMinutes!.Value, 2);
    }

    [Fact]
    public async Task GetStatsAsync_AverageReviewTimeMinutes_Ignores_The_Rejection_That_Is_The_Current_Moderation_Event()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
        var createdAt = now.AddMinutes(-45);
        var rejectedAt = now.AddMinutes(-1);
        // Currently Rejected — its own ModeratedAt/log entry must not be treated as a "prior"
        // rejection cycle for itself; the review time must still be measured from CreatedAt.
        var listing = MakeListing(OwnerId, CategoryId, ListingStatus.Rejected, createdAt, rejectedAt);

        await db.SeedAsync(listing);
        await db.SeedAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = OwnerId,
            Action = ModerationAction.ListingRejected,
            TargetType = ModerationTargetType.Listing,
            TargetId = listing.Id,
            TargetLabel = listing.Title,
            DetailJson = null,
            CreatedAt = rejectedAt
        });

        await using var context = db.CreateContext();
        var stats = await new AdminOverviewStore(context).GetStatsAsync(now);

        Assert.NotNull(stats.AverageReviewTimeMinutes);
        Assert.Equal(44.0, stats.AverageReviewTimeMinutes!.Value, 2);
    }
}
