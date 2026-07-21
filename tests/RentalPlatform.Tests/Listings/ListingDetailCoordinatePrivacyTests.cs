using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Hotfix H1 + P1-3 (the public-coordinate rule): exact Latitude/Longitude must never reach a
// caller who isn't the listing's owner or an admin — leaking them lets anyone reverse-geocode a
// family's home address, defeating the AddressLine privacy gate entirely. Since P1-3, a non-
// owner/non-admin caller gets the PUBLIC pair (geohash-6 cell centroid) instead of null, so these
// tests also cover the "no trilateration" requirement: repeated/independent reads of the same
// listing, by any caller, for any exact point inside a given cell, all resolve to the identical
// published pair.
public sealed class ListingDetailCoordinatePrivacyTests
{
    private static readonly Guid OwnerId = new("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId = new("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid SecondOtherUserId = new("f0000000-0000-0000-0000-000000000006");
    private static readonly Guid AdminId = new("f0000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("f0000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId = new("f0000000-0000-0000-0000-000000000005");
    private static readonly Guid SecondListingId = new("f0000000-0000-0000-0000-000000000007");

    private const decimal ExactLatitude = 40.1872m;
    private const decimal ExactLongitude = 44.5152m;

    // A second exact point independently verified (via the geohash bit-bisection) to fall inside
    // the SAME geohash-6 cell as (ExactLatitude, ExactLongitude) above — i.e. a different exact
    // location that must still publish the identical pair.
    private const decimal SecondExactLatitude = 40.18715m;
    private const decimal SecondExactLongitude = 44.51525m;

    private static readonly GeohashSnapper Snapper = new();

    private static async Task SeedAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(OtherUserId, "other@test.local"),
            TestData.User(SecondOtherUserId, "other2@test.local"),
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.Category(CategoryId));

        var (publicLatitude, publicLongitude) = Snapper.SnapToCellCenter(ExactLatitude, ExactLongitude);

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = ExactLatitude;
        listing.Longitude = ExactLongitude;
        // Mirrors what ListingsOwnerService.CreateAsync computes at write time (P1-3) — seeded
        // directly here since this test seeds the entity straight into the DB, bypassing the service.
        listing.PublicLatitude = publicLatitude;
        listing.PublicLongitude = publicLongitude;

        await db.SeedAsync(listing);
    }

    [Fact]
    public async Task Anonymous_Caller_Sees_Public_Pair_Not_Exact()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var (expectedLatitude, expectedLongitude) = Snapper.SnapToCellCenter(ExactLatitude, ExactLongitude);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        Assert.NotNull(result);
        Assert.Equal(expectedLatitude, result!.Latitude);
        Assert.Equal(expectedLongitude, result.Longitude);
        Assert.NotEqual(ExactLatitude, result.Latitude);
        Assert.NotEqual(ExactLongitude, result.Longitude);
    }

    [Fact]
    public async Task NonOwner_Authenticated_Caller_Sees_Public_Pair_Not_Exact()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var (expectedLatitude, expectedLongitude) = Snapper.SnapToCellCenter(ExactLatitude, ExactLongitude);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: OtherUserId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Equal(expectedLatitude, result!.Latitude);
        Assert.Equal(expectedLongitude, result.Longitude);
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

    // Trilateration guard: repeated reads of the SAME listing by DIFFERENT non-owner callers must
    // return the byte-for-byte identical published pair — no caller-specific jitter that would let
    // several observers correlate their reads and average toward the exact point.
    [Fact]
    public async Task Repeated_Reads_By_Different_Callers_Return_Identical_Public_Pair()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context1 = db.CreateContext();
        var firstRead = await new ListingsQueryService(context1)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        await using var context2 = db.CreateContext();
        var secondRead = await new ListingsQueryService(context2)
            .GetApprovedListingByIdAsync(ListingId, callerId: OtherUserId, isAdmin: false);

        await using var context3 = db.CreateContext();
        var thirdRead = await new ListingsQueryService(context3)
            .GetApprovedListingByIdAsync(ListingId, callerId: SecondOtherUserId, isAdmin: false);

        Assert.NotNull(firstRead);
        Assert.NotNull(secondRead);
        Assert.NotNull(thirdRead);
        Assert.Equal(firstRead!.Latitude, secondRead!.Latitude);
        Assert.Equal(firstRead.Longitude, secondRead.Longitude);
        Assert.Equal(firstRead.Latitude, thirdRead!.Latitude);
        Assert.Equal(firstRead.Longitude, thirdRead.Longitude);
    }

    // Trilateration guard, the other half: two DIFFERENT exact points that share a geohash-6 cell
    // must publish the SAME pair — a caller who somehow knew (or guessed) both listings' cell
    // membership gains no information distinguishing the two exact locations from each other.
    [Fact]
    public async Task Two_Exact_Points_In_The_Same_Cell_Publish_The_Identical_Pair()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await db.SeedAsync(
            TestData.User(new Guid("f0000000-0000-0000-0000-000000000008"), "owner2@test.local"),
            TestData.Category(new Guid("f0000000-0000-0000-0000-000000000009")));

        var (publicLatitude, publicLongitude) = Snapper.SnapToCellCenter(SecondExactLatitude, SecondExactLongitude);
        var secondListing = TestData.Listing(
            SecondListingId,
            new Guid("f0000000-0000-0000-0000-000000000008"),
            new Guid("f0000000-0000-0000-0000-000000000009"),
            ListingStatus.Approved);
        secondListing.Latitude = SecondExactLatitude;
        secondListing.Longitude = SecondExactLongitude;
        secondListing.PublicLatitude = publicLatitude;
        secondListing.PublicLongitude = publicLongitude;
        await db.SeedAsync(secondListing);

        await using var context1 = db.CreateContext();
        var firstListingRead = await new ListingsQueryService(context1)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        await using var context2 = db.CreateContext();
        var secondListingRead = await new ListingsQueryService(context2)
            .GetApprovedListingByIdAsync(SecondListingId, callerId: null, isAdmin: false);

        Assert.NotNull(firstListingRead);
        Assert.NotNull(secondListingRead);
        // Sanity: the two exact points genuinely differ...
        Assert.NotEqual(ExactLatitude, SecondExactLatitude);
        // ...yet the published pair is identical, because both fall in the same geohash-6 cell.
        Assert.Equal(firstListingRead!.Latitude, secondListingRead!.Latitude);
        Assert.Equal(firstListingRead.Longitude, secondListingRead.Longitude);
    }
}
