using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// P1-3 (geohash snapping) + P1-4 (district assignment) exercised through the real
// ListingsOwnerService, over the real ListingsOwnerStore/GeohashSnapper/DistrictBoundaryProvider,
// against SQLite — the same style as ListingsOwnerServiceTests. The 12 Districts rows come from
// DistrictConfiguration.HasData (seeded automatically by EnsureCreated), so no extra seeding is
// needed to reference them by their fixed Guids.
public sealed class ListingLocationDerivationTests
{
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid ListingId = new("e0000000-0000-0000-0000-000000000003");

    // Fixed Guids from DistrictConfiguration.HasData.
    private static readonly Guid KentronDistrictId = new("d0000007-0000-4000-9000-000000000007");
    private static readonly Guid ArabkirDistrictId = new("d0000002-0000-4000-9000-000000000002");

    private const decimal KentronLatitude = 40.1776m; // Republic Square
    private const decimal KentronLongitude = 44.5126m;

    // Gyumri — genuinely outside every Yerevan district polygon.
    private const decimal OutsideLatitude = 40.7850m;
    private const decimal OutsideLongitude = 43.8453m;

    private static async Task SeedBaselineAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));
    }

    private static ListingsOwnerService CreateService(AppDbContext context) =>
        new(
            new FakeCurrentUserContext(OwnerId),
            new ListingsOwnerStore(context),
            new GeohashSnapper(),
            new DistrictBoundaryProvider());

    private static CreateListingRequest ValidCreate(
        decimal? latitude = null,
        decimal? longitude = null,
        Guid? districtId = null) => new()
    {
        CategoryId = CategoryId,
        Title = "Wooden Train Set",
        Description = "A long enough description to satisfy validation rules.",
        PricePerDay = 12m,
        Country = "Armenia",
        City = "Yerevan",
        Latitude = latitude,
        Longitude = longitude,
        DistrictId = districtId
    };

    [Fact]
    public async Task Create_Derives_District_From_Exact_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate(KentronLatitude, KentronLongitude));

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal(KentronDistrictId, stored!.DistrictId);
    }

    [Fact]
    public async Task Create_Leaves_District_Null_When_Point_Is_Outside_Every_District()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate(OutsideLatitude, OutsideLongitude));

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Null(stored!.DistrictId);
        // Public coordinates are still computed even though the district lookup misses — the two
        // are independent derivations from the same exact point.
        Assert.NotNull(stored.PublicLatitude);
        Assert.NotNull(stored.PublicLongitude);
    }

    [Fact]
    public async Task Create_Leaves_Everything_Null_When_No_Coordinates_Given()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate());

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Null(stored!.DistrictId);
        Assert.Null(stored.PublicLatitude);
        Assert.Null(stored.PublicLongitude);
    }

    [Fact]
    public async Task Create_Honors_Explicit_District_Override_Over_Derivation()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        // Exact point is inside Kentron, but the owner explicitly picks Arabkir — override wins.
        await using var context = db.CreateContext();
        var result = await CreateService(context)
            .CreateAsync(ValidCreate(KentronLatitude, KentronLongitude, districtId: ArabkirDistrictId));

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal(ArabkirDistrictId, stored!.DistrictId);
    }

    [Fact]
    public async Task Create_Fails_When_District_Does_Not_Exist()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context)
            .CreateAsync(ValidCreate(KentronLatitude, KentronLongitude, districtId: Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.district_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Computes_Public_Coordinates_As_The_Geohash_Cell_Centroid()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        var expected = new GeohashSnapper().SnapToCellCenter(KentronLatitude, KentronLongitude);

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate(KentronLatitude, KentronLongitude));

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal(expected.Latitude, stored!.PublicLatitude);
        Assert.Equal(expected.Longitude, stored.PublicLongitude);
        Assert.NotEqual(KentronLatitude, stored.PublicLatitude);
    }

    [Fact]
    public async Task Update_Applies_Explicit_District_Override_Without_Touching_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = KentronLatitude;
        listing.Longitude = KentronLongitude;
        listing.DistrictId = KentronDistrictId;
        listing.PublicLatitude = KentronLatitude;
        listing.PublicLongitude = KentronLongitude;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest
        {
            DistrictId = ArabkirDistrictId
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ArabkirDistrictId, stored!.DistrictId);
        // Update does not accept Latitude/Longitude, so the exact/public pair are untouched.
        Assert.Equal(KentronLatitude, stored.Latitude);
        Assert.Equal(KentronLatitude, stored.PublicLatitude);
    }

    [Fact]
    public async Task Update_Fails_When_District_Does_Not_Exist()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest
        {
            DistrictId = Guid.NewGuid()
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.district_not_found", result.Error!.Code);
    }
}
