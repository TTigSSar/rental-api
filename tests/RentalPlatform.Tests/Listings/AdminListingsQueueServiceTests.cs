using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Phase 1 admin console redesign: the status-filtered paged queue, the recategorise endpoint,
// the extended reject-reason catalog, and the moderation audit trail written by all three.
public sealed class AdminListingsQueueServiceTests
{
    private static readonly Guid AdminId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid OtherCategoryId = new("e0000000-0000-0000-0000-000000000004");

    private static AdminListingsService CreateService(AppDbContext context, Guid currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new AdminListingsStore(context),
            new ReviewsStore(context),
            new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance),
            new FakeEmailService(),
            new FakeNotificationEmitter());

    private static Listing MakeListing(
        Guid id, Guid ownerId, Guid categoryId, ListingStatus status, string title, DateTime createdAt, DateTime? moderatedAt = null)
    {
        var listing = TestData.Listing(id, ownerId, categoryId, status);
        listing.Title = title;
        listing.CreatedAt = createdAt;
        listing.UpdatedAt = createdAt;
        listing.ModeratedAt = moderatedAt;
        return listing;
    }

    // --- status filtering + counts ---

    [Fact]
    public async Task GetQueue_Filters_By_Status_And_Returns_Unfiltered_Counts()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Pending A", now),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Pending B", now),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved, "Approved A", now, now),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Rejected, "Rejected A", now, now));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminListingQueueFilter { Status = "Pending" });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Items.Count);
        Assert.All(result.Value.Items, item => Assert.Contains("Pending", item.Title));
        Assert.Equal(2, result.Value.Counts.Pending);
        Assert.Equal(1, result.Value.Counts.Approved);
        Assert.Equal(1, result.Value.Counts.Rejected);
    }

    [Fact]
    public async Task GetQueue_Defaults_To_Pending_When_Status_Omitted()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Pending A", now),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved, "Approved A", now, now));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminListingQueueFilter());

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);
        Assert.Equal("Pending A", result.Value.Items.Single().Title);
    }

    [Fact]
    public async Task GetQueue_Pending_Sorts_Oldest_First()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Newer", now),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Older", now.AddDays(-3)));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminListingQueueFilter { Status = "Pending" });

        Assert.True(result.IsSuccess);
        Assert.Equal(["Older", "Newer"], result.Value!.Items.Select(i => i.Title));
    }

    // --- search matching ---

    [Fact]
    public async Task GetQueue_Search_Matches_Listing_Title()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Wooden Train Set", now),
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "LEGO Duplo", now));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId)
            .GetQueueAsync(new AdminListingQueueFilter { Status = "Pending", Search = "train" });

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);
        Assert.Equal("Wooden Train Set", result.Value.Items.Single().Title);
        // Counts still reflect the search filter across all three statuses.
        Assert.Equal(1, result.Value.Counts.Pending);
    }

    [Fact]
    public async Task GetQueue_Search_Matches_Owner_Email()
    {
        var searchedOwnerId = Guid.NewGuid();
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(searchedOwnerId, "unique.searchtarget@test.local"),
            TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(
            MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Listing One", now),
            MakeListing(Guid.NewGuid(), searchedOwnerId, CategoryId, ListingStatus.PendingApproval, "Listing Two", now));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId)
            .GetQueueAsync(new AdminListingQueueFilter { Status = "Pending", Search = "searchtarget" });

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);
        Assert.Equal("Listing Two", result.Value.Items.Single().Title);
    }

    // --- pagination bounds ---

    [Fact]
    public async Task GetQueue_Page_Below_One_Clamps_To_First_Page()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Only", now));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId)
            .GetQueueAsync(new AdminListingQueueFilter { Status = "Pending", Page = 0 });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Page);
        Assert.Single(result.Value.Items);
    }

    [Fact]
    public async Task GetQueue_PageSize_Above_Max_Is_Clamped()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var now = DateTime.UtcNow;
        await db.SeedAsync(MakeListing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.PendingApproval, "Only", now));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId)
            .GetQueueAsync(new AdminListingQueueFilter { Status = "Pending", PageSize = 5000 });

        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value!.PageSize);
    }

    // --- recategorise ---

    [Fact]
    public async Task UpdateCategory_Happy_Path_Recategorises_And_Logs()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId),
            new Category { Id = OtherCategoryId, Name = "Outdoor Toys", Slug = "outdoor-toys" });

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.PendingApproval, "Recategorise Me", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateCategoryAsync(listingId, OtherCategoryId);

        Assert.True(result.IsSuccess);
        Assert.Equal(OtherCategoryId, result.Value!.CategoryId);
        Assert.Equal("Outdoor Toys", result.Value.CategoryName);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(listingId);
        Assert.Equal(OtherCategoryId, stored!.CategoryId);

        var logEntry = Assert.Single(verify.ModerationLogEntries);
        Assert.Equal(ModerationAction.ListingRecategorised, logEntry.Action);
        Assert.Equal(ModerationTargetType.Listing, logEntry.TargetType);
        Assert.Equal(listingId, logEntry.TargetId);
        Assert.Equal(AdminId, logEntry.ActorUserId);
        Assert.Contains("Outdoor Toys", logEntry.DetailJson);
    }

    [Fact]
    public async Task UpdateCategory_Allowed_When_Listing_Already_Approved()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId),
            new Category { Id = OtherCategoryId, Name = "Outdoor Toys", Slug = "outdoor-toys" });

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.Approved, "Live Listing", DateTime.UtcNow, DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateCategoryAsync(listingId, OtherCategoryId);

        Assert.True(result.IsSuccess);
        Assert.Equal(OtherCategoryId, result.Value!.CategoryId);
    }

    [Fact]
    public async Task UpdateCategory_Listing_Not_Found_Fails()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.Category(CategoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateCategoryAsync(Guid.NewGuid(), CategoryId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.listing_not_found", result.Error!.Code);
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Archived)]
    public async Task UpdateCategory_Invalid_Status_Fails(ListingStatus status)
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId),
            new Category { Id = OtherCategoryId, Name = "Outdoor Toys", Slug = "outdoor-toys" });

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, status, "Draft Listing", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateCategoryAsync(listingId, OtherCategoryId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.invalid_listing_status", result.Error!.Code);
    }

    [Fact]
    public async Task UpdateCategory_Unknown_Category_Fails()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.PendingApproval, "Listing", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateCategoryAsync(listingId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_reassign_target_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task UpdateCategory_NoOp_When_Category_Already_Target()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.PendingApproval, "Listing", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateCategoryAsync(listingId, CategoryId);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategoryId, result.Value!.CategoryId);

        await using var verify = db.CreateContext();
        Assert.Empty(verify.ModerationLogEntries);
    }

    // --- extended reject-reason catalog ---

    [Theory]
    [InlineData("hygiene", "No cleaning or hygiene details")]
    [InlineData("pricing", "Unrealistic pricing")]
    [InlineData("other", "Other")]
    public async Task Reject_Accepts_New_Reason_Codes(string code, string expectedLabel)
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.PendingApproval, "Listing", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).RejectAsync(listingId, code, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedLabel, result.Value!.RejectionReason);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(listingId);
        Assert.Equal(code, stored!.RejectionReasonCode);
    }

    [Fact]
    public async Task Reject_Unknown_Reason_Code_Fails()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.PendingApproval, "Listing", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).RejectAsync(listingId, "not-a-real-code", null);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.invalid_reject_reason", result.Error!.Code);
    }

    // --- moderation audit trail ---

    [Fact]
    public async Task Approve_Writes_Moderation_Log_Entry()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.PendingApproval, "Approve Me", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ApproveAsync(listingId);
        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var logEntry = Assert.Single(verify.ModerationLogEntries);
        Assert.Equal(ModerationAction.ListingApproved, logEntry.Action);
        Assert.Equal(ModerationTargetType.Listing, logEntry.TargetType);
        Assert.Equal(listingId, logEntry.TargetId);
        Assert.Equal(AdminId, logEntry.ActorUserId);
        Assert.Equal("Approve Me", logEntry.TargetLabel);
    }

    [Fact]
    public async Task Reject_Writes_Moderation_Log_Entry()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.PendingApproval, "Reject Me", DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).RejectAsync(listingId, "other", "needs work");
        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var logEntry = Assert.Single(verify.ModerationLogEntries);
        Assert.Equal(ModerationAction.ListingRejected, logEntry.Action);
        Assert.Equal(listingId, logEntry.TargetId);
        Assert.Equal(AdminId, logEntry.ActorUserId);
    }

    // --- inspect dossier ---

    [Fact]
    public async Task GetDetail_Returns_Full_Dossier_For_Any_Status()
    {
        using var db = new SqliteTestDatabase();
        var listingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(MakeListing(listingId, OwnerId, CategoryId, ListingStatus.Approved, "Dossier Listing", DateTime.UtcNow, DateTime.UtcNow));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingStatus.Approved, result.Value!.Status);
        Assert.Equal(0, result.Value.OwnerOpenReportCount);
    }

    [Fact]
    public async Task GetDetail_Not_Found_Fails()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(AdminId, "admin@test.local", role: UserRole.Admin));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.listing_not_found", result.Error!.Code);
    }
}
