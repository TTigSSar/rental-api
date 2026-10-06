using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Where a listing's location comes from, exercised through the real ListingsOwnerService over the
// real ListingsOwnerStore against SQLite. The 12 Districts rows come from
// DistrictConfiguration.HasData (seeded automatically by EnsureCreated), so they can be referenced
// by their fixed Guids without extra seeding.
//
// The rule these tests pin down (home-point model): a listing NEVER derives its own location. It
// copies the owner's home point verbatim at create time — exact pair, public pair, district, city
// and country — and only HomePointService ever changes it afterwards. The derivation itself
// (snapping + point-in-polygon) happens once, when the home point is written; see HomePointServiceTests.
public sealed class ListingLocationDerivationTests
{
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("e0000000-0000-0000-0000-000000000002");

    // Fixed Guids from DistrictConfiguration.HasData.
    private static readonly Guid KentronDistrictId = new("d0000007-0000-4000-9000-000000000007");

    private const decimal KentronLatitude = 40.1776m; // Republic Square
    private const decimal KentronLongitude = 44.5126m;

    private static ListingsOwnerService CreateService(AppDbContext context) =>
        new(new FakeCurrentUserContext(OwnerId), new ListingsOwnerStore(context));

    private static CreateListingRequest ValidCreate() => new()
    {
        CategoryId = CategoryId,
        Title = "Wooden Train Set",
        Description = "A long enough description to satisfy validation rules.",
        PricePerDay = 12m,
        CompensationAmount = 12000m
    };

    [Fact]
    public async Task Create_Copies_The_Owners_Home_Point_Onto_The_Listing()
    {
        using var db = new SqliteTestDatabase();
        var snapped = new GeohashSnapper().SnapToCellCenter(KentronLatitude, KentronLongitude);

        var owner = TestData.User(
            OwnerId,
            "owner@test.local",
            homeLatitude: KentronLatitude,
            homeLongitude: KentronLongitude,
            homePublicLatitude: snapped.Latitude,
            homePublicLongitude: snapped.Longitude,
            homeDistrictId: KentronDistrictId);
        await db.SeedAsync(owner, TestData.Category(CategoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate());

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal(KentronLatitude, stored!.Latitude);
        Assert.Equal(KentronLongitude, stored.Longitude);
        Assert.Equal(snapped.Latitude, stored.PublicLatitude);
        Assert.Equal(snapped.Longitude, stored.PublicLongitude);
        Assert.Equal(KentronDistrictId, stored.DistrictId);
        Assert.Equal(LocationKind.Home, stored.LocationKind);
    }

    // The exact point is copied, but the PUBLIC pair is what everyone else sees — and it must be the
    // geohash cell centroid, never the exact coordinate (ADR-008). Create copies whatever the home
    // point already derived, so this asserts the copy really is the snapped pair.
    [Fact]
    public async Task Create_Publishes_The_Snapped_Pair_Not_The_Exact_One()
    {
        using var db = new SqliteTestDatabase();
        var snapped = new GeohashSnapper().SnapToCellCenter(KentronLatitude, KentronLongitude);

        await db.SeedAsync(
            TestData.User(
                OwnerId,
                "owner@test.local",
                homeLatitude: KentronLatitude,
                homeLongitude: KentronLongitude,
                homePublicLatitude: snapped.Latitude,
                homePublicLongitude: snapped.Longitude,
                homeDistrictId: KentronDistrictId),
            TestData.Category(CategoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate());

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.NotEqual(KentronLatitude, stored!.PublicLatitude);
        Assert.Equal(snapped.Latitude, stored.PublicLatitude);
    }

    // The district is copied from the home point, null included. A home point outside every Yerevan
    // district (Gyumri) is legal, and its listings simply carry no district.
    [Fact]
    public async Task Create_Leaves_District_Null_When_The_Home_Point_Has_None()
    {
        using var db = new SqliteTestDatabase();
        var (latitude, longitude) = TestData.OutsideYerevanPoint;
        var snapped = new GeohashSnapper().SnapToCellCenter(latitude, longitude);

        await db.SeedAsync(
            TestData.User(
                OwnerId,
                "owner@test.local",
                homeLatitude: latitude,
                homeLongitude: longitude,
                homePublicLatitude: snapped.Latitude,
                homePublicLongitude: snapped.Longitude,
                homeDistrictId: null),
            TestData.Category(CategoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate());

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Null(stored!.DistrictId);
        // The public pair is still computed — the two derivations are independent.
        Assert.NotNull(stored.PublicLatitude);
        Assert.NotNull(stored.PublicLongitude);
    }

    // The gate. An owner with no home point cannot publish at all: the listing would have no
    // location, and there is no longer any field on the request that could supply one.
    [Fact]
    public async Task Create_Fails_When_The_Owner_Has_No_Home_Point()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate());

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.home_point_required", result.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.Empty(verify.Listings);
    }

    // City and country are derived too, so a listing's city can never contradict its pin.
    [Fact]
    public async Task Create_Sets_City_To_Yerevan_When_The_Home_Point_Resolved_A_District()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(
                OwnerId,
                "owner@test.local",
                homeLatitude: KentronLatitude,
                homeLongitude: KentronLongitude,
                homeDistrictId: KentronDistrictId),
            TestData.Category(CategoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate());

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal("Yerevan", stored!.City);
        Assert.Equal("Armenia", stored.Country);
    }

    // Outside Yerevan there is no district to name a city from and deliberately no geocoder, so the
    // city the owner's existing listings already carry is reused rather than guessed at.
    [Fact]
    public async Task Create_Reuses_The_Owners_Previous_City_When_The_Home_Point_Has_No_District()
    {
        using var db = new SqliteTestDatabase();
        var (latitude, longitude) = TestData.OutsideYerevanPoint;

        await db.SeedAsync(
            TestData.User(
                OwnerId,
                "owner@test.local",
                homeLatitude: latitude,
                homeLongitude: longitude,
                homeDistrictId: null),
            TestData.Category(CategoryId));

        var existing = TestData.Listing(
            new Guid("e0000000-0000-0000-0000-00000000000a"), OwnerId, CategoryId, ListingStatus.Approved);
        existing.City = "Gyumri";
        existing.CreatedAt = DateTime.UtcNow.AddDays(-1);
        await db.SeedAsync(existing);

        await using var context = db.CreateContext();
        var result = await CreateService(context).CreateAsync(ValidCreate());

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal("Gyumri", stored!.City);
    }

    // ---- Create racing a home-point move ---------------------------------------------------------

    // The interleaving itself, reproduced rather than reasoned about.
    //
    // CreateAsync reads the owner, then checks the category, then inserts — and a
    // PUT /api/auth/me/home-point that commits inside that gap relocates only the listings that
    // already exist. So the create used to commit the owner's PREVIOUS point and stay there: the
    // home-point writer had already finished, update never re-derives a location, and the one thing
    // that would repair it is ListingLocationBackfillRunner's second pass, which runs once per
    // process start — production stays up for weeks between deploys. The listing would advertise the
    // area the owner had left, in /listings, in the map pins and in the radius filter, for that
    // whole time.
    //
    // The hook is CategoryExistsAsync because that is exactly where the request path is at the
    // moment the window opens, so nothing about the service's own sequence has to be faked.
    [Fact]
    public async Task Create_Lands_On_The_Home_Point_A_Concurrent_Move_Committed_Not_The_One_It_First_Read()
    {
        using var db = new SqliteTestDatabase();
        var snapper = new GeohashSnapper();
        var firstRead = snapper.SnapToCellCenter(KentronLatitude, KentronLongitude);

        await db.SeedAsync(
            TestData.User(
                OwnerId,
                "owner@test.local",
                homeLatitude: KentronLatitude,
                homeLongitude: KentronLongitude,
                homePublicLatitude: firstRead.Latitude,
                homePublicLongitude: firstRead.Longitude,
                homeDistrictId: KentronDistrictId),
            TestData.Category(CategoryId));

        var (movedLatitude, movedLongitude) = TestData.ArabkirPoint;

        await using var context = db.CreateContext();
        var store = new InterleavingListingsOwnerStore(
            new ListingsOwnerStore(context),
            async () =>
            {
                // A real home-point move, through the real single writer, on its own context and
                // committed before control returns to the create.
                await using var moving = db.CreateContext();
                var move = await new HomePointService(
                        new HomePointStore(moving),
                        new GeohashSnapper(),
                        new DistrictBoundaryProvider(),
                        new FakeNotificationEmitter())
                    .SetHomePointAsync(OwnerId, movedLatitude, movedLongitude);

                Assert.True(move.IsSuccess);
                Assert.True(move.Value); // the point really changed
            });

        var result = await new ListingsOwnerService(new FakeCurrentUserContext(OwnerId), store).CreateAsync(ValidCreate());
        Assert.True(result.IsSuccess);

        // The race really was reproduced: the create's own context is STILL holding the pre-move
        // owner row, so every fresh value asserted below can only have come from the re-read inside
        // the insert's transaction. Without this, a test that silently stopped interleaving would
        // keep passing.
        var trackedOwner = Assert.Single(context.ChangeTracker.Entries<User>()).Entity;
        Assert.Equal(KentronLatitude, trackedOwner.HomeLatitude);

        await using var verify = db.CreateContext();
        var owner = await verify.Users.SingleAsync(user => user.Id == OwnerId);
        var stored = await verify.Listings.SingleAsync();

        // The invariant, stated the way the product states it: a listing IS its owner's home point.
        Assert.Equal(owner.HomeLatitude, stored.Latitude);
        Assert.Equal(owner.HomeLongitude, stored.Longitude);
        Assert.Equal(owner.HomePublicLatitude, stored.PublicLatitude);
        Assert.Equal(owner.HomePublicLongitude, stored.PublicLongitude);
        Assert.Equal(owner.HomeDistrictId, stored.DistrictId);

        // And spelled out in absolute values, so the assertions above cannot be satisfied by the
        // owner row having been dragged backwards instead.
        var movedPublic = snapper.SnapToCellCenter(movedLatitude, movedLongitude);
        Assert.Equal(movedLatitude, stored.Latitude);
        Assert.Equal(movedLongitude, stored.Longitude);
        Assert.Equal(movedPublic.Latitude, stored.PublicLatitude);
        Assert.Equal(movedPublic.Longitude, stored.PublicLongitude);
        Assert.NotEqual(KentronDistrictId, stored.DistrictId);
    }

    // The other side of the same window: the owner did not move their point, they DELETED it
    // (DELETE me/home-point) while the create was in flight. There is then nothing to publish at,
    // so the insert must not happen at all — a listing with a null location is invisible to the
    // radius filter and unplaceable on the map.
    [Fact]
    public async Task Create_Fails_When_The_Owner_Clears_Their_Home_Point_Mid_Request()
    {
        using var db = new SqliteTestDatabase();

        await db.SeedAsync(
            TestData.User(
                OwnerId,
                "owner@test.local",
                homeLatitude: KentronLatitude,
                homeLongitude: KentronLongitude,
                homeDistrictId: KentronDistrictId),
            TestData.Category(CategoryId));

        await using var context = db.CreateContext();
        var store = new InterleavingListingsOwnerStore(
            new ListingsOwnerStore(context),
            async () =>
            {
                await using var clearing = db.CreateContext();
                var cleared = await new HomePointService(
                        new HomePointStore(clearing),
                        new GeohashSnapper(),
                        new DistrictBoundaryProvider(),
                        new FakeNotificationEmitter())
                    .ClearHomePointAsync(OwnerId);

                Assert.True(cleared.IsSuccess);
            });

        var result = await new ListingsOwnerService(new FakeCurrentUserContext(OwnerId), store).CreateAsync(ValidCreate());

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.home_point_required", result.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Listings.ToListAsync());
    }

    /// <summary>
    /// Wraps the real store and runs one action in the middle of a create — after the owner has been
    /// read, before the listing is inserted — to reproduce a request that overlaps a home-point
    /// write. It delegates everything; the hook is <see cref="CategoryExistsAsync"/> only.
    /// </summary>
    private sealed class InterleavingListingsOwnerStore : IListingsOwnerStore
    {
        private readonly IListingsOwnerStore _inner;
        private readonly Func<Task> _interleave;
        private bool _fired;

        public InterleavingListingsOwnerStore(IListingsOwnerStore inner, Func<Task> interleave)
        {
            _inner = inner;
            _interleave = interleave;
        }

        public async Task<bool> CategoryExistsAsync(Guid categoryId, CancellationToken cancellationToken = default)
        {
            var exists = await _inner.CategoryExistsAsync(categoryId, cancellationToken);

            // Once only: the concurrent writer commits a single time, exactly as one HTTP request
            // would.
            if (!_fired)
            {
                _fired = true;
                await _interleave();
            }

            return exists;
        }

        public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            _inner.FindUserByIdAsync(userId, cancellationToken);

        public Task<bool> TryAddListingAtOwnerHomePointAsync(
            Listing listing,
            Func<User, bool> applyOwnerHomePoint,
            CancellationToken cancellationToken = default) =>
            _inner.TryAddListingAtOwnerHomePointAsync(listing, applyOwnerHomePoint, cancellationToken);

        public Task<IReadOnlyCollection<Listing>> GetListingsByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
            _inner.GetListingsByOwnerIdAsync(ownerId, cancellationToken);

        public Task<string?> FindMostRecentListingCityAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
            _inner.FindMostRecentListingCityAsync(ownerId, cancellationToken);

        public Task<Listing?> FindListingByIdWithImagesAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            _inner.FindListingByIdWithImagesAsync(listingId, cancellationToken);

        public Task<Listing?> FindListingByIdAndOwnerAsync(Guid listingId, Guid ownerId, CancellationToken cancellationToken = default) =>
            _inner.FindListingByIdAndOwnerAsync(listingId, ownerId, cancellationToken);

        public Task AddListingImagesAsync(IEnumerable<ListingImage> images, CancellationToken cancellationToken = default) =>
            _inner.AddListingImagesAsync(images, cancellationToken);

        public void RemoveListingImage(ListingImage image) => _inner.RemoveListingImage(image);

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
            _inner.SaveChangesAsync(cancellationToken);
    }
}
