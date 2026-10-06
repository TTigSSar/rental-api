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

    // ---- Home-point reconciliation ---------------------------------------------------------------
    //
    // The runner gained two passes for the home-point model. They exist because a listing's location
    // is a COPY of its owner's home point, and a copy drifts: an old build, a race between a create
    // and a move, or — the case that will actually happen — the AddUserHomePoint migration, which
    // deliberately writes only the exact half of the home point and leaves the derived half to this
    // runner (a geohash must never be computed in SQL).

    // Exactly the state the migration leaves behind: the user has an exact home point, no public
    // pair and no district. This pass is what makes that migration correct.
    [Fact]
    public async Task Fills_A_Users_Public_Home_Pair_And_District_From_Their_Exact_Home_Point()
    {
        using var db = new SqliteTestDatabase();
        var owner = TestData.User(
            OwnerId, "owner@test.local",
            homeLatitude: KentronLatitude,
            homeLongitude: KentronLongitude);
        await db.SeedAsync(owner, TestData.Category(CategoryId));

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Users.FindAsync(OwnerId);
        var expected = new GeohashSnapper().SnapToCellCenter(KentronLatitude, KentronLongitude);

        Assert.Equal(expected.Latitude, stored!.HomePublicLatitude);
        Assert.Equal(expected.Longitude, stored.HomePublicLongitude);
        Assert.NotNull(stored.HomeDistrictId);
    }

    [Fact]
    public async Task Leaves_A_Users_Home_District_Null_When_Their_Home_Point_Is_Outside_Yerevan()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local", homeLatitude: OutsideLatitude, homeLongitude: OutsideLongitude),
            TestData.Category(CategoryId));

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Users.FindAsync(OwnerId);
        Assert.NotNull(stored!.HomePublicLatitude);
        Assert.Null(stored.HomeDistrictId);
    }

    // M-012: derived state written by an event needs a self-heal path, because an event that fired
    // under a build without the writer never fires again. Here: a listing whose location disagrees
    // with its owner's home point is re-pointed at the owner's, whatever it currently says.
    [Fact]
    public async Task Resyncs_A_Listing_Whose_Location_Drifted_From_Its_Owners_Home_Point()
    {
        using var db = new SqliteTestDatabase();
        var expected = new GeohashSnapper().SnapToCellCenter(KentronLatitude, KentronLongitude);

        await db.SeedAsync(
            TestData.User(
                OwnerId, "owner@test.local",
                homeLatitude: KentronLatitude,
                homeLongitude: KentronLongitude,
                homePublicLatitude: expected.Latitude,
                homePublicLongitude: expected.Longitude,
                homeDistrictId: TestData.KentronDistrictId),
            TestData.Category(CategoryId));

        // A stale row: the location the owner used to have, districts and all.
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = 40.2207m;
        listing.Longitude = 44.5253m;
        listing.PublicLatitude = 40.22m;
        listing.PublicLongitude = 44.52m;
        listing.DistrictId = ArabkirDistrictId;
        await db.SeedAsync(listing);

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(KentronLatitude, stored!.Latitude);
        Assert.Equal(KentronLongitude, stored.Longitude);
        Assert.Equal(expected.Latitude, stored.PublicLatitude);
        Assert.Equal(TestData.KentronDistrictId, stored.DistrictId);
    }

    // Re-pointing a copy is reconciliation, not an owner edit — an Approved listing must not be
    // pushed back into the moderation queue by a startup task.
    [Fact]
    public async Task Resync_Does_Not_Change_A_Listings_Status_Or_UpdatedAt()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.OwnerWithHome(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        var originalUpdatedAt = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        listing.UpdatedAt = originalUpdatedAt;
        listing.Latitude = 40.9m;
        listing.Longitude = 44.9m;
        await db.SeedAsync(listing);

        await BuildRunner(db).RunAsync();

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(TestData.KentronPoint.Latitude, stored!.Latitude); // it did move
        Assert.Equal(ListingStatus.Approved, stored.Status);
        Assert.Equal(originalUpdatedAt, stored.UpdatedAt);
    }

    // M-038: the home point arrived after these rows did. An owner who never set one must not have
    // their listings wiped of the only location they have.
    [Fact]
    public async Task Leaves_A_Legacy_Listing_Alone_When_Its_Owner_Has_No_Home_Point()
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
        Assert.Equal(KentronLatitude, stored!.Latitude);
        // The legacy pass still fills what it can from the listing's own coordinates.
        Assert.NotNull(stored.PublicLatitude);
        Assert.NotNull(stored.DistrictId);
    }

    // The whole sequence a boot right after the migration performs — derive the users' half, then
    // re-sync the listings against it — has to land on a fixed point, or every restart would rewrite
    // rows and the log would never go quiet.
    [Fact]
    public async Task Home_Point_Reconciliation_Is_Stable_Across_Restarts()
    {
        using var db = new SqliteTestDatabase();

        // Post-migration shape: the user holds only the exact pair, the listing only the copy of it.
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local", homeLatitude: KentronLatitude, homeLongitude: KentronLongitude),
            TestData.Category(CategoryId));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.Latitude = KentronLatitude;
        listing.Longitude = KentronLongitude;
        await db.SeedAsync(listing);

        await BuildRunner(db).RunAsync();

        decimal? publicLatitude, publicLongitude;
        Guid? districtId, homeDistrictId;
        DateTime updatedAt;
        await using (var afterFirst = db.CreateContext())
        {
            var storedListing = await afterFirst.Listings.FindAsync(ListingId);
            publicLatitude = storedListing!.PublicLatitude;
            publicLongitude = storedListing.PublicLongitude;
            districtId = storedListing.DistrictId;
            updatedAt = storedListing.UpdatedAt;
            homeDistrictId = (await afterFirst.Users.FindAsync(OwnerId))!.HomeDistrictId;

            Assert.NotNull(publicLatitude);
            Assert.NotNull(districtId);
            Assert.NotNull(homeDistrictId);
        }

        await BuildRunner(db).RunAsync();

        await using var afterSecond = db.CreateContext();
        var second = await afterSecond.Listings.FindAsync(ListingId);
        Assert.Equal(publicLatitude, second!.PublicLatitude);
        Assert.Equal(publicLongitude, second.PublicLongitude);
        Assert.Equal(districtId, second.DistrictId);
        Assert.Equal(updatedAt, second.UpdatedAt);
        Assert.Equal(homeDistrictId, (await afterSecond.Users.FindAsync(OwnerId))!.HomeDistrictId);
    }
}
