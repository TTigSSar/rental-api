using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Owner-side listing lifecycle: create validation, edit re-moderation, archive/restore.
// Runs the real ListingsOwnerService over the real ListingsOwnerStore against SQLite.
public sealed class ListingsOwnerServiceTests
{
    private static readonly Guid OwnerId = new("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid ListingId = new("c0000000-0000-0000-0000-000000000003");

    private static async Task SeedBaselineAsync(SqliteTestDatabase db, bool ownerBlocked = false)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local", isBlocked: ownerBlocked),
            TestData.Category(CategoryId));
    }

    private static ListingsOwnerService CreateService(AppDbContext context, Guid currentUserId) =>
        new(new FakeCurrentUserContext(currentUserId), new ListingsOwnerStore(context));

    private static CreateListingRequest ValidCreate(
        Guid? categoryId = null,
        int? ageFromMonths = null,
        int? ageToMonths = null) => new()
    {
        CategoryId = categoryId ?? CategoryId,
        Title = "Wooden Train Set",
        Description = "A long enough description to satisfy validation rules.",
        PricePerDay = 12m,
        Country = "Armenia",
        City = "Yerevan",
        AgeFromMonths = ageFromMonths,
        AgeToMonths = ageToMonths
    };

    private static async Task SeedApprovedListingAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));
    }

    [Fact]
    public async Task Create_Submits_Listing_For_Review()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(ValidCreate());

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingStatus.PendingApproval, result.Value!.Status);
    }

    [Fact]
    public async Task Create_Fails_When_Category_Missing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        var request = ValidCreate(categoryId: Guid.NewGuid());

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.category_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Age_Range_Inverted()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        var request = ValidCreate(ageFromMonths: 24, ageToMonths: 12);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_age_range", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Owner_Blocked()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db, ownerBlocked: true);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(ValidCreate());

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.user_blocked", result.Error!.Code);
    }

    [Fact]
    public async Task Update_Resets_Approved_Listing_To_Pending_When_Content_Changes()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            Description = "An edited description that changes the public listing content."
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
    }

    [Fact]
    public async Task Update_Keeps_Approved_Listing_Approved_When_Only_Price_Changes()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            PricePerDay = 99m
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.Approved, stored!.Status);
        Assert.Equal(99m, stored.PricePerDay);
    }

    [Fact]
    public async Task Update_Resends_Rejected_Listing_For_Review_And_Clears_Reason()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Rejected);
        listing.RejectionReason = "Unsafe parts.";
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            Title = "Refreshed Title"
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
        Assert.Null(stored.RejectionReason);
    }

    [Fact]
    public async Task Update_Fails_On_Archived_Listing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Archived));

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            Title = "Cannot edit archived"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_status", result.Error!.Code);
    }

    [Fact]
    public async Task Archive_Then_Archive_Again_Fails()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using (var context = db.CreateContext())
        {
            var first = await CreateService(context, OwnerId).ArchiveAsync(ListingId);
            Assert.True(first.IsSuccess);
        }

        await using var second = db.CreateContext();
        var result = await CreateService(second, OwnerId).ArchiveAsync(ListingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_status", result.Error!.Code);
    }

    [Fact]
    public async Task Restore_Moves_Archived_Listing_Back_To_Pending()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Archived));

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).RestoreAsync(ListingId);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
    }
}
