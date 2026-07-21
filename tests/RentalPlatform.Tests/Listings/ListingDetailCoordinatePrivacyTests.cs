using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Hotfix H1: exact Latitude/Longitude must never reach a caller who isn't the listing's owner
// or an admin — leaking them lets anyone reverse-geocode a family's home address, defeating the
// AddressLine privacy gate entirely.
public sealed class ListingDetailCoordinatePrivacyTests
{
    private static readonly Guid OwnerId = new("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId = new("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminId = new("f0000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("f0000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId = new("f0000000-0000-0000-0000-000000000005");

    private const decimal ExactLatitude = 40.1872m;
    private const decimal ExactLongitude = 44.5152m;

    private static async Task SeedAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(OtherUserId, "other@test.local"),
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.Category(CategoryId));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = ExactLatitude;
        listing.Longitude = ExactLongitude;

        await db.SeedAsync(listing);
    }

    [Fact]
    public async Task Coordinates_Hidden_For_Anonymous_Caller()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        Assert.NotNull(result);
        Assert.Null(result!.Latitude);
        Assert.Null(result.Longitude);
    }

    [Fact]
    public async Task Coordinates_Hidden_For_NonOwner_Authenticated_Caller()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: OtherUserId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Null(result!.Latitude);
        Assert.Null(result.Longitude);
    }

    [Fact]
    public async Task Coordinates_Revealed_For_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: OwnerId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Equal(ExactLatitude, result!.Latitude);
        Assert.Equal(ExactLongitude, result.Longitude);
    }

    [Fact]
    public async Task Coordinates_Revealed_For_Admin()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: AdminId, isAdmin: true);

        Assert.NotNull(result);
        Assert.Equal(ExactLatitude, result!.Latitude);
        Assert.Equal(ExactLongitude, result.Longitude);
    }
}
