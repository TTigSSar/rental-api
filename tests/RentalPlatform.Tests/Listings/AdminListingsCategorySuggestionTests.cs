using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Admin console Phase 6: the "Needs category fix" suggestion rule (AdminListingsService's
// ComputeSuggestedCategory), exercised end to end through GetQueueAsync/GetDetailAsync against a
// real CategoryKeywords table — whole-word matching, the "never suggest the owner's own pick"
// rule, title-over-description weighting, deterministic tie-breaking, and the PendingApproval-only
// scope.
public sealed class AdminListingsCategorySuggestionTests
{
    private static readonly Guid AdminId = new("e6000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = new("e6000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryAId = new("e6000000-0000-0000-0000-0000000000a1"); // owner's pick
    private static readonly Guid CategoryBId = new("e6000000-0000-0000-0000-0000000000b1"); // candidate suggestion
    private static readonly Guid CategoryCId = new("e6000000-0000-0000-0000-0000000000c1"); // second candidate (tie-break)

    private static AdminListingsService CreateService(AppDbContext context, Guid currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new AdminListingsStore(context),
            new ReviewsStore(context),
            new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance),
            new FakeEmailService(),
            new FakeNotificationEmitter());

    private static Listing MakeListing(
        Guid id, Guid categoryId, string title, string description, ListingStatus status = ListingStatus.PendingApproval)
    {
        var listing = TestData.Listing(id, OwnerId, categoryId, status);
        listing.Title = title;
        listing.Description = description;
        return listing;
    }

    private static CategoryKeyword Keyword(Guid categoryId, string keyword) => new()
    {
        Id = Guid.NewGuid(),
        CategoryId = categoryId,
        Keyword = keyword
    };

    private static async Task SeedBaseAsync(SqliteTestDatabase db, int categoryBDisplayOrder = 1, int categoryCDisplayOrder = 2)
    {
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryAId, name: "Category A", slug: "category-a", displayOrder: 0),
            TestData.Category(CategoryBId, name: "Category B", slug: "category-b", displayOrder: categoryBDisplayOrder),
            TestData.Category(CategoryCId, name: "Category C", slug: "category-c", displayOrder: categoryCDisplayOrder));
    }

    // ---------- Whole-word matching ----------

    [Fact]
    public async Task Suggestion_Does_Not_Match_Keyword_As_Substring_Of_Another_Word()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(Keyword(CategoryBId, "car"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Kids Carpet Playmat", "A soft foam carpet for tummy time."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.SuggestedCategoryId);
        Assert.Null(result.Value.SuggestedCategoryName);
    }

    [Fact]
    public async Task Suggestion_Matches_Keyword_At_A_True_Word_Boundary()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(Keyword(CategoryBId, "car"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Toy Car Set", "A set of small toy cars."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategoryBId, result.Value!.SuggestedCategoryId);
        Assert.Equal("Category B", result.Value.SuggestedCategoryName);
    }

    [Fact]
    public async Task Suggestion_Matches_Multi_Word_Hyphenated_Keyword_At_String_Edges()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(Keyword(CategoryBId, "ride-on"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Classic ride-on", "Foot-to-floor propulsion, no pedals."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategoryBId, result.Value!.SuggestedCategoryId);
    }

    // ---------- Never suggest the owner's own pick ----------

    [Fact]
    public async Task Suggestion_Is_Null_When_The_Best_Match_Is_The_Owners_Own_Category()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        // The keyword belongs to Category A, which is also the owner's chosen category.
        await db.SeedAsync(Keyword(CategoryAId, "puzzle"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Wooden Puzzle Set", "A classic wooden jigsaw puzzle."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.SuggestedCategoryId);
        Assert.Null(result.Value.SuggestedCategoryName);
    }

    [Fact]
    public async Task Suggestion_Is_Null_When_No_Keyword_Matches_Anything()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(Keyword(CategoryBId, "scooter"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Board Game Night Bundle", "Three cooperative board games."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.SuggestedCategoryId);
    }

    // ---------- Title-over-description weighting ----------

    [Fact]
    public async Task Suggestion_Weighs_A_Title_Hit_Higher_Than_A_Description_Hit()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        // Category A (the owner's pick) only matches in the description; Category B only matches
        // in the title. A single title hit must outrank a single description hit.
        await db.SeedAsync(
            Keyword(CategoryAId, "sensory"),
            Keyword(CategoryBId, "scooter"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId,
            "Kids Scooter", // Category B keyword in the title
            "Great for sensory play at the park.")); // Category A keyword in the description

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategoryBId, result.Value!.SuggestedCategoryId);
    }

    // ---------- Deterministic tie-breaking ----------

    [Fact]
    public async Task Suggestion_Breaks_A_Tied_Score_By_The_Lower_DisplayOrder_Category()
    {
        using var db = new SqliteTestDatabase();
        // Category B has a lower DisplayOrder than Category C.
        await SeedBaseAsync(db, categoryBDisplayOrder: 5, categoryCDisplayOrder: 9);
        await db.SeedAsync(
            Keyword(CategoryBId, "scooter"),
            Keyword(CategoryCId, "trike"));

        var listingId = Guid.NewGuid();
        // Both keywords appear in the title with equal weight -- an exact score tie.
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Scooter and Trike Combo", "Two ride-on toys in one bundle."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategoryBId, result.Value!.SuggestedCategoryId);
    }

    [Fact]
    public async Task Suggestion_Tie_Break_Is_Stable_Regardless_Of_DisplayOrder_Direction()
    {
        using var db = new SqliteTestDatabase();
        // Swap which category has the lower DisplayOrder versus the previous test -- Category C
        // now wins, proving the tie-break follows DisplayOrder rather than any incidental
        // insertion/iteration order.
        await SeedBaseAsync(db, categoryBDisplayOrder: 9, categoryCDisplayOrder: 5);
        await db.SeedAsync(
            Keyword(CategoryBId, "scooter"),
            Keyword(CategoryCId, "trike"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Scooter and Trike Combo", "Two ride-on toys in one bundle."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategoryCId, result.Value!.SuggestedCategoryId);
    }

    // ---------- PendingApproval-only scope ----------

    [Theory]
    [InlineData(ListingStatus.Approved)]
    [InlineData(ListingStatus.Rejected)]
    [InlineData(ListingStatus.Draft)]
    public async Task Suggestion_Is_Null_For_Non_Pending_Listings_Even_When_A_Keyword_Would_Match(ListingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(Keyword(CategoryBId, "scooter"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(
            listingId, CategoryAId, "Kids Scooter", "A fun ride for the park.", status));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetDetailAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.SuggestedCategoryId);
        Assert.Null(result.Value.SuggestedCategoryName);
    }

    // ---------- Also surfaced on the queue (summary) shape, not just the detail dossier ----------

    [Fact]
    public async Task GetQueue_Also_Carries_The_Suggested_Category_On_Summary_Rows()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(Keyword(CategoryBId, "scooter"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(MakeListing(listingId, CategoryAId, "Kids Scooter", "A fun ride for the park."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminListingQueueFilter { Status = "Pending" });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(CategoryBId, item.SuggestedCategoryId);
        Assert.Equal("Category B", item.SuggestedCategoryName);
    }
}
