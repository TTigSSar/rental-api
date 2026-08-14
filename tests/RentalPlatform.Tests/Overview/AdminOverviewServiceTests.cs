using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Overview;

// Admin console Phase 5: the Overview screen backend (GET /api/admin/overview and
// /api/admin/overview/activity). Covers every stat against seeded data, the empty-database case
// (nulls/zeros, not exceptions), admin-role enforcement, and the activity feed's ordering, take
// clamping, and deleted-actor handling.
public sealed class AdminOverviewServiceTests
{
    private static readonly Guid AdminId = new("e4000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = new("e4000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e4000000-0000-0000-0000-000000000003");

    private static AdminOverviewService CreateService(AppDbContext context, Guid? currentUserId) =>
        new(new FakeCurrentUserContext(currentUserId), new AdminOverviewStore(context));

    private static Listing MakeListing(
        Guid id, Guid ownerId, Guid categoryId, ListingStatus status, DateTime createdAt, DateTime? moderatedAt = null)
    {
        var listing = TestData.Listing(id, ownerId, categoryId, status);
        listing.CreatedAt = createdAt;
        listing.UpdatedAt = createdAt;
        listing.ModeratedAt = moderatedAt;
        return listing;
    }

    private static async Task SeedAdminAsync(SqliteTestDatabase db) =>
        await db.SeedAsync(TestData.User(AdminId, "admin@test.local", role: UserRole.Admin, isIdConfirmed: true));

    // ---------- Empty database ----------

    [Fact]
    public async Task GetOverview_Empty_Database_Returns_Zeros_And_Nulls_Not_Exceptions()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetOverviewAsync();

        Assert.True(result.IsSuccess);
        var overview = result.Value!;
        Assert.Equal(0, overview.AwaitingReviewCount);
        Assert.Null(overview.OldestAwaitingCreatedAt);
        Assert.Equal(0, overview.LiveListingCount);
        Assert.Equal(0, overview.LiveListingsAddedThisWeek);
        Assert.Equal(0, overview.CategoryCount);
        Assert.Equal(0, overview.VisibleCategoryCount);
        Assert.Equal(0, overview.OpenReportCount);
        Assert.Null(overview.AverageReviewTimeMinutes);
        Assert.Equal(6, overview.ReviewTimeTargetHours);
    }

    // ---------- AwaitingReviewCount / OldestAwaitingCreatedAt ----------

    [Fact]
    public async Task GetOverview_Computes_AwaitingReviewCount_And_Oldest_Pending_CreatedAt()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        var oldest = now.AddDays(-5);
        await db.SeedAsync(
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, now.AddDays(-1)),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, oldest),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, now.AddDays(-2)),
            // Approved/Rejected must not count toward the pending queue.
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved, now, now));

        await using var context = db.CreateContext();
        var overview = (await CreateService(context, AdminId).GetOverviewAsync()).Value!;

        Assert.Equal(3, overview.AwaitingReviewCount);
        Assert.NotNull(overview.OldestAwaitingCreatedAt);
        Assert.Equal(oldest, overview.OldestAwaitingCreatedAt!.Value, TimeSpan.FromSeconds(1));
    }

    // ---------- LiveListingCount / LiveListingsAddedThisWeek ----------

    [Fact]
    public async Task GetOverview_Computes_LiveListingCount_And_AddedThisWeek_By_ModeratedAt()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            // Moderated (approved) 2 days ago -- inside the trailing 7 days.
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved, now.AddDays(-20), now.AddDays(-2)),
            // Moderated 3 days ago -- also inside.
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved, now.AddDays(-30), now.AddDays(-3)),
            // Submitted (CreatedAt) yesterday but moderated 10 days ago -- outside the window;
            // proves the stat uses ModeratedAt, not CreatedAt.
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved, now.AddDays(-1), now.AddDays(-10)),
            // Rejected listings never count as "live".
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Rejected, now.AddDays(-2), now.AddDays(-1)));

        await using var context = db.CreateContext();
        var overview = (await CreateService(context, AdminId).GetOverviewAsync()).Value!;

        Assert.Equal(3, overview.LiveListingCount);
        Assert.Equal(2, overview.LiveListingsAddedThisWeek);
    }

    // ---------- CategoryCount / VisibleCategoryCount ----------

    [Fact]
    public async Task GetOverview_Computes_CategoryCount_And_VisibleCategoryCount()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(
            TestData.Category(Guid.NewGuid(), name: "Visible A", slug: "visible-a", isVisible: true),
            TestData.Category(Guid.NewGuid(), name: "Visible B", slug: "visible-b", isVisible: true),
            TestData.Category(Guid.NewGuid(), name: "Hidden A", slug: "hidden-a", isVisible: false));

        await using var context = db.CreateContext();
        var overview = (await CreateService(context, AdminId).GetOverviewAsync()).Value!;

        Assert.Equal(3, overview.CategoryCount);
        Assert.Equal(2, overview.VisibleCategoryCount);
    }

    // ---------- OpenReportCount ----------

    [Fact]
    public async Task GetOverview_Computes_OpenReportCount_Only()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));
        var listingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(listingId, OwnerId, CategoryId));
        await db.SeedAsync(
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, listingId, OwnerId, status: ReportStatus.Open),
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, listingId, OwnerId, status: ReportStatus.Open),
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, listingId, OwnerId, status: ReportStatus.Resolved),
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, listingId, OwnerId, status: ReportStatus.Dismissed));

        await using var context = db.CreateContext();
        var overview = (await CreateService(context, AdminId).GetOverviewAsync()).Value!;

        Assert.Equal(2, overview.OpenReportCount);
    }

    // ---------- AverageReviewTimeMinutes ----------

    [Fact]
    public async Task GetOverview_AverageReviewTimeMinutes_Averages_Moderated_Listings_Within_30Days()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            // Moderated 10 days ago, took 60 minutes -- inside the window.
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved,
                now.AddDays(-10).AddMinutes(-60), now.AddDays(-10)),
            // Moderated 5 days ago, took 120 minutes -- inside the window (rejected also counts).
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Rejected,
                now.AddDays(-5).AddMinutes(-120), now.AddDays(-5)),
            // Moderated 40 days ago -- clearly outside the trailing 30-day window, must not skew the mean.
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved,
                now.AddDays(-40).AddMinutes(-999), now.AddDays(-40)),
            // Still pending -- ModeratedAt is null, excluded entirely.
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, now));

        await using var context = db.CreateContext();
        var overview = (await CreateService(context, AdminId).GetOverviewAsync()).Value!;

        Assert.NotNull(overview.AverageReviewTimeMinutes);
        Assert.Equal(90.0, overview.AverageReviewTimeMinutes!.Value, 1);
    }

    [Fact]
    public async Task GetOverview_AverageReviewTimeMinutes_Null_When_Nothing_Moderated_In_Window()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved,
                now.AddDays(-90), now.AddDays(-60)));

        await using var context = db.CreateContext();
        var overview = (await CreateService(context, AdminId).GetOverviewAsync()).Value!;

        Assert.Null(overview.AverageReviewTimeMinutes);
    }

    // ---------- Admin-role enforcement ----------

    [Fact]
    public async Task GetOverview_Unauthenticated_Returns_Unauthenticated()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();

        var result = await CreateService(context, null).GetOverviewAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.unauthenticated", result.Error!.Code);
    }

    [Fact]
    public async Task GetOverview_NonAdmin_Returns_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        var userId = Guid.NewGuid();
        await db.SeedAsync(TestData.User(userId, "user@test.local", role: UserRole.User));

        await using var context = db.CreateContext();
        var result = await CreateService(context, userId).GetOverviewAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.forbidden", result.Error!.Code);
    }

    // ---------- Activity feed ----------

    [Fact]
    public async Task GetActivityFeed_Orders_Newest_First()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        var now = DateTime.UtcNow;
        var oldest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var newest = Guid.NewGuid();
        await db.SeedAsync(
            LogEntry(oldest, AdminId, ModerationAction.ListingApproved, now.AddHours(-3)),
            LogEntry(newest, AdminId, ModerationAction.ListingRejected, now.AddHours(-1)),
            LogEntry(middle, AdminId, ModerationAction.ListingRecategorised, now.AddHours(-2)));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetActivityFeedAsync(take: 20);

        Assert.True(result.IsSuccess);
        Assert.Equal([newest, middle, oldest], result.Value!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetActivityFeed_Take_Is_Clamped_To_Max_50()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        var now = DateTime.UtcNow;
        var entries = Enumerable.Range(0, 60)
            .Select(i => LogEntry(Guid.NewGuid(), AdminId, ModerationAction.ListingApproved, now.AddMinutes(-i)))
            .ToArray();
        await db.SeedAsync(entries);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetActivityFeedAsync(take: 500);

        Assert.True(result.IsSuccess);
        Assert.Equal(50, result.Value!.Items.Count);
    }

    [Fact]
    public async Task GetActivityFeed_Nonpositive_Take_Defaults_To_20()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        var now = DateTime.UtcNow;
        var entries = Enumerable.Range(0, 30)
            .Select(i => LogEntry(Guid.NewGuid(), AdminId, ModerationAction.ListingApproved, now.AddMinutes(-i)))
            .ToArray();
        await db.SeedAsync(entries);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetActivityFeedAsync(take: 0);

        Assert.True(result.IsSuccess);
        Assert.Equal(20, result.Value!.Items.Count);
    }

    [Fact]
    public async Task GetActivityFeed_Deleted_Actor_Returns_Null_Names_Not_Dropped()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        var ghostActorId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        // The actor row never existed (simulating "actor account later deleted"). The test SQLite
        // connection enforces FK constraints (unlike the FK Restrict semantics only manifesting as
        // a real DDL constraint in some engines), so a raw INSERT still needs the pragma dropped
        // for this one statement — this is purely a test-harness workaround for a row shape
        // production's own Restrict FK would never let arise mid-flight, not a change to any real
        // constraint.
        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await context.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ModerationLogEntries (Id, ActorUserId, Action, TargetType, TargetId, TargetLabel, DetailJson, CreatedAt)
                VALUES ({entryId}, {ghostActorId}, 0, 0, {Guid.NewGuid()}, {"Ghost target"}, {(string?)null}, {DateTime.UtcNow})");
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }

        await using var context2 = db.CreateContext();
        var result = await CreateService(context2, AdminId).GetActivityFeedAsync(take: 20);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(entryId, item.Id);
        Assert.Equal(ghostActorId, item.ActorId);
        Assert.Null(item.ActorFirstName);
        Assert.Null(item.ActorLastName);
        Assert.Null(item.ActorAvatarUrl);
    }

    private static ModerationLogEntry LogEntry(Guid id, Guid actorId, ModerationAction action, DateTime createdAt) => new()
    {
        Id = id,
        ActorUserId = actorId,
        Action = action,
        TargetType = ModerationTargetType.Listing,
        TargetId = Guid.NewGuid(),
        TargetLabel = "Test target",
        DetailJson = null,
        CreatedAt = createdAt
    };
}
