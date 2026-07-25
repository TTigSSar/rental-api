using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.DependencyInjection.LocationBackfill;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// P1-4: the idempotent backfill for listings that predate the privacy-coordinate/district
// features — rows with an exact Latitude/Longitude but a null PublicLatitude/PublicLongitude
// and/or DistrictId. Runs the real ListingLocationBackfillRunner over the real
// GeohashSnapper/DistrictBoundaryProvider against SQLite, mirroring DemoContentBootstrapTests'
// style for the sibling bootstrap runner.
public sealed class ListingLocationBackfillRunnerTests
{
    private static readonly Guid OwnerId = new("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid ListingId = new("b0000000-0000-0000-0000-000000000003");

    private static readonly Guid ArabkirDistrictId = new("d0000002-0000-4000-9000-000000000002");

    private const decimal KentronLatitude = 40.1776m; // Republic Square
    private const decimal KentronLongitude = 44.5126m;

    // Gyumri — genuinely outside every Yerevan district polygon.
    private const decimal OutsideLatitude = 40.7850m;
    private const decimal OutsideLongitude = 43.8453m;

    private static ListingLocationBackfillRunner BuildRunner(SqliteTestDatabase db) =>
        new(
            db.CreateContext(),
            new GeohashSnapper(),
            new DistrictBoundaryProvider(),
            NullLogger<ListingLocationBackfillRunner>.Instance);

    [Fact]
    public async Task Fills_Public_Coordinates_And_District_For_A_Listing_With_Exact_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = KentronLatitude;
        listing.Longitude = KentronLongitude;
        await db.SeedAsync(listing);

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.NotNull(stored!.PublicLatitude);
        Assert.NotNull(stored.PublicLongitude);
        Assert.NotNull(stored.DistrictId);
    }

    [Fact]
    public async Task Fills_Public_Coordinates_But_Leaves_District_Null_When_Point_Is_Outside_Every_District()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = OutsideLatitude;
        listing.Longitude = OutsideLongitude;
        await db.SeedAsync(listing);

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.NotNull(stored!.PublicLatitude);
        Assert.NotNull(stored.PublicLongitude);
        Assert.Null(stored.DistrictId);
    }

    [Fact]
    public async Task Does_Not_Overwrite_An_Already_Set_District_Even_If_Derivation_Would_Differ()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        // Exact point is in Kentron, but DistrictId already holds an owner's Arabkir override —
        // the backfill must never touch a non-null DistrictId, whatever the derivation would say.
        listing.Latitude = KentronLatitude;
        listing.Longitude = KentronLongitude;
        listing.DistrictId = ArabkirDistrictId;
        await db.SeedAsync(listing);

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ArabkirDistrictId, stored!.DistrictId);
        // The public pair was still missing, so that half of the backfill still applies.
        Assert.NotNull(stored.PublicLatitude);
        Assert.NotNull(stored.PublicLongitude);
    }

    [Fact]
    public async Task Ignores_Listings_With_No_Exact_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Null(stored!.PublicLatitude);
        Assert.Null(stored.PublicLongitude);
        Assert.Null(stored.DistrictId);
    }

    // Verifies the mechanism the InvalidatePublicCoordinatesForGeohashPrecisionUpgrade migration
    // relies on: it does NOT reimplement geohash math in SQL, it just nulls PublicLatitude/
    // PublicLongitude for every listing with exact coordinates, and this unchanged runner recomputes
    // them at whatever precision GeohashSnapper.Precision currently is — the same self-heal path
    // that already handles rows whose derived value was never written (M-012 in
    // knowledge/mistakes.md). This test simulates exactly that: a row carrying a STALE public pair
    // (as if computed at the old geohash-6 precision) gets invalidated to null and re-filled.
    [Fact]
    public async Task Recomputes_Public_Coordinates_At_The_Current_Precision_When_Invalidated_To_Null()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = KentronLatitude;
        listing.Longitude = KentronLongitude;
        // A deliberately WRONG/stale public pair standing in for a value computed at the old
        // geohash-6 precision — the exact value doesn't matter, only that it's non-null, so the
        // runner's "fill only nulls" guard would otherwise skip this row.
        listing.PublicLatitude = 40.0m;
        listing.PublicLongitude = 44.0m;
        await db.SeedAsync(listing);

        // Simulate what the migration's raw SQL does: invalidate the stale derived value.
        await using (var invalidate = db.CreateContext())
        {
            var toInvalidate = await invalidate.Listings.FindAsync(ListingId);
            toInvalidate!.PublicLatitude = null;
            toInvalidate.PublicLongitude = null;
            await invalidate.SaveChangesAsync();
        }

        await BuildRunner(db).RunAsync();

        var expected = new GeohashSnapper().SnapToCellCenter(KentronLatitude, KentronLongitude);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(expected.Latitude, stored!.PublicLatitude);
        Assert.Equal(expected.Longitude, stored.PublicLongitude);
        // The stale value must actually be gone, not coincidentally equal to the new one.
        Assert.NotEqual(40.0m, stored.PublicLatitude);
        Assert.NotEqual(44.0m, stored.PublicLongitude);
    }

    [Fact]
    public async Task Running_Twice_Is_Idempotent_Second_Run_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"), TestData.Category(CategoryId));
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = KentronLatitude;
        listing.Longitude = KentronLongitude;
        await db.SeedAsync(listing);

        await BuildRunner(db).RunAsync();

        await using var afterFirstRun = db.CreateContext();
        var afterFirst = await afterFirstRun.Listings.FindAsync(ListingId);
        var firstPublicLatitude = afterFirst!.PublicLatitude;
        var firstPublicLongitude = afterFirst.PublicLongitude;
        var firstDistrictId = afterFirst.DistrictId;
        Assert.NotNull(firstPublicLatitude);
        Assert.NotNull(firstDistrictId);

        // Run again against the now-fully-populated row.
        await BuildRunner(db).RunAsync();

        await using var afterSecondRun = db.CreateContext();
        var afterSecond = await afterSecondRun.Listings.FindAsync(ListingId);
        Assert.Equal(firstPublicLatitude, afterSecond!.PublicLatitude);
        Assert.Equal(firstPublicLongitude, afterSecond.PublicLongitude);
        Assert.Equal(firstDistrictId, afterSecond.DistrictId);
    }
}
