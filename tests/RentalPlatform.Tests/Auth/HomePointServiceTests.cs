using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// HomePointService is the ONLY writer of User.Home* and of a listing's location, so everything the
// home-point model promises is promised by this class: move the point and every listing the owner
// has moves with it, in every status, without becoming an edit and without touching anybody else's
// listings. These tests run the real service over the real store against SQLite.
//
// The 12 Districts rows come from DistrictConfiguration.HasData (seeded by EnsureCreated), so
// district ids can be asserted directly.
public sealed class HomePointServiceTests
{
    private static readonly Guid OwnerId = new("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherOwnerId = new("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid RenterId = new("a1000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("a1000000-0000-0000-0000-000000000004");

    // Fixed Guids from DistrictConfiguration.HasData.
    private static readonly Guid KentronDistrictId = new("d0000007-0000-4000-9000-000000000007");

    private static HomePointService CreateService(AppDbContext context, FakeNotificationEmitter emitter) =>
        new(new HomePointStore(context), new GeohashSnapper(), new DistrictBoundaryProvider(), emitter);

    private static async Task SeedOwnerAsync(SqliteTestDatabase db, bool blocked = false)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local", isBlocked: blocked),
            TestData.Category(CategoryId));
    }

    // ---- The fan-out ------------------------------------------------------------------------

    // Every status, no exceptions: a Draft, a listing waiting for moderation, an Approved one, a
    // Rejected one and an Archived one all move. Leaving any of them behind would publish a stale
    // address the moment it changed status.
    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.PendingApproval)]
    [InlineData(ListingStatus.Approved)]
    [InlineData(ListingStatus.Rejected)]
    [InlineData(ListingStatus.Archived)]
    public async Task SetHomePoint_Moves_The_Owners_Listing_In_Every_Status(ListingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);

        var listingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(listingId, OwnerId, CategoryId, status));

        var (latitude, longitude) = TestData.KentronPoint;
        var expectedPublic = new GeohashSnapper().SnapToCellCenter(latitude, longitude);

        await using (var context = db.CreateContext())
        {
            var result = await CreateService(context, new FakeNotificationEmitter())
                .SetHomePointAsync(OwnerId, latitude, longitude);

            Assert.True(result.IsSuccess);
            Assert.True(result.Value);
        }

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.SingleAsync(listing => listing.Id == listingId);
        Assert.Equal(latitude, stored.Latitude);
        Assert.Equal(longitude, stored.Longitude);
        Assert.Equal(expectedPublic.Latitude, stored.PublicLatitude);
        Assert.Equal(expectedPublic.Longitude, stored.PublicLongitude);
        Assert.Equal(KentronDistrictId, stored.DistrictId);
        Assert.Equal(LocationKind.Home, stored.LocationKind);

        var owner = await verify.Users.SingleAsync(user => user.Id == OwnerId);
        Assert.Equal(latitude, owner.HomeLatitude);
        Assert.Equal(expectedPublic.Latitude, owner.HomePublicLatitude);
        Assert.Equal(KentronDistrictId, owner.HomeDistrictId);
        Assert.NotNull(owner.HomePointUpdatedAt);
    }

    // Moving house is not an edit. If it re-triggered moderation, every owner who moved would have
    // their whole catalogue taken off the site until a moderator got to it.
    [Fact]
    public async Task SetHomePoint_Leaves_Status_And_UpdatedAt_Untouched()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);

        var listingId = Guid.NewGuid();
        var listing = TestData.Listing(listingId, OwnerId, CategoryId, ListingStatus.Approved);
        var originalUpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        listing.UpdatedAt = originalUpdatedAt;
        await db.SeedAsync(listing);

        await using (var context = db.CreateContext())
        {
            await CreateService(context, new FakeNotificationEmitter())
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.SingleAsync(l => l.Id == listingId);
        Assert.Equal(ListingStatus.Approved, stored.Status);
        Assert.Equal(originalUpdatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task SetHomePoint_Never_Touches_Another_Owners_Listings()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);
        await db.SeedAsync(TestData.User(OtherOwnerId, "other@test.local"));

        var otherListingId = Guid.NewGuid();
        var otherListing = TestData.Listing(otherListingId, OtherOwnerId, CategoryId, ListingStatus.Approved);
        otherListing.Latitude = 40.9999m;
        otherListing.Longitude = 44.9999m;
        await db.SeedAsync(otherListing);

        await using (var context = db.CreateContext())
        {
            await CreateService(context, new FakeNotificationEmitter())
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.SingleAsync(l => l.Id == otherListingId);
        Assert.Equal(40.9999m, stored.Latitude);
        Assert.Equal(44.9999m, stored.Longitude);

        var otherOwner = await verify.Users.SingleAsync(u => u.Id == OtherOwnerId);
        Assert.Null(otherOwner.HomeLatitude);
    }

    // A save that changes nothing must not look like a move: it would re-stamp HomePointUpdatedAt
    // and notify every renter that a pickup area "changed" when it did not.
    [Fact]
    public async Task SetHomePoint_Is_A_NoOp_When_The_Point_Is_Unchanged()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);
        await db.SeedAsync(TestData.Listing(Guid.NewGuid(), OwnerId, CategoryId, ListingStatus.Approved));

        var (latitude, longitude) = TestData.KentronPoint;
        var emitter = new FakeNotificationEmitter();

        await using (var context = db.CreateContext())
        {
            await CreateService(context, emitter).SetHomePointAsync(OwnerId, latitude, longitude);
        }

        DateTime? stampAfterFirst;
        await using (var read = db.CreateContext())
        {
            stampAfterFirst = (await read.Users.SingleAsync(u => u.Id == OwnerId)).HomePointUpdatedAt;
        }

        await using (var context = db.CreateContext())
        {
            var second = await CreateService(context, emitter).SetHomePointAsync(OwnerId, latitude, longitude);

            Assert.True(second.IsSuccess);
            Assert.False(second.Value); // "nothing was written"
        }

        await using var verify = db.CreateContext();
        Assert.Equal(stampAfterFirst, (await verify.Users.SingleAsync(u => u.Id == OwnerId)).HomePointUpdatedAt);
    }

    [Fact]
    public async Task SetHomePoint_Fails_For_An_Unknown_User()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, new FakeNotificationEmitter())
            .SetHomePointAsync(Guid.NewGuid(), TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.unauthenticated", result.Error!.Code);
    }

    [Fact]
    public async Task SetHomePoint_Fails_For_A_Blocked_User()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db, blocked: true);

        await using var context = db.CreateContext();
        var result = await CreateService(context, new FakeNotificationEmitter())
            .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.user_blocked", result.Error!.Code);
    }

    // DoRent operates in Yerevan only, so a point outside every district is refused outright — for
    // everyone, renters included. "Inside a district" IS the validity rule; there is no separate
    // country check and no bounding box.
    [Theory]
    [InlineData(40.7850, 43.8453)]   // Gyumri — in Armenia, but not Yerevan
    [InlineData(51.5074, -0.1278)]   // London
    public async Task SetHomePoint_Refuses_A_Point_Outside_Yerevan(double latitude, double longitude)
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);
        var listingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(listingId, OwnerId, CategoryId, ListingStatus.Approved));

        await using (var context = db.CreateContext())
        {
            var result = await CreateService(context, new FakeNotificationEmitter())
                .SetHomePointAsync(OwnerId, (decimal)latitude, (decimal)longitude);

            Assert.False(result.IsSuccess);
            Assert.Equal("auth.home_point_outside_yerevan", result.Error!.Code);
        }

        // Nothing was written — not the user's point, not the listing's location.
        await using var verify = db.CreateContext();
        Assert.Null((await verify.Users.SingleAsync(u => u.Id == OwnerId)).HomeLatitude);
        Assert.Null((await verify.Listings.SingleAsync(l => l.Id == listingId)).Latitude);
    }

    // The refusal is the single writer's own rule, so it holds no matter who calls — including the
    // pure pre-check registration uses before it has a user row to write to.
    [Fact]
    public void ValidateForSave_Accepts_Yerevan_And_Refuses_Everything_Else()
    {
        using var db = new SqliteTestDatabase();
        using var context = db.CreateContext();
        var service = CreateService(context, new FakeNotificationEmitter());

        Assert.True(service.ValidateForSave(TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude).IsSuccess);

        var refused = service.ValidateForSave(TestData.OutsideYerevanPoint.Latitude, TestData.OutsideYerevanPoint.Longitude);
        Assert.False(refused.IsSuccess);
        Assert.Equal("auth.home_point_outside_yerevan", refused.Error!.Code);
    }

    // ---- City follows the pin -------------------------------------------------------------------

    // City is the one location field nothing can derive from a coordinate, so it is the one that can
    // silently end up contradicting the map. Resolving to a district IS the statement "this is
    // Yerevan", so the move rewrites it — on every listing, in every status.
    [Fact]
    public async Task SetHomePoint_Rewrites_City_And_Country_On_Every_Listing()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);

        var listingId = Guid.NewGuid();
        var listing = TestData.Listing(listingId, OwnerId, CategoryId, ListingStatus.Approved);
        // A stale city, the shape a legacy row migrated from outside Yerevan actually has.
        listing.City = "Gyumri";
        listing.Country = "Neverland";
        await db.SeedAsync(listing);

        await using (var context = db.CreateContext())
        {
            await CreateService(context, new FakeNotificationEmitter())
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.SingleAsync(l => l.Id == listingId);
        Assert.Equal("Yerevan", stored.City);
        Assert.Equal("Armenia", stored.Country);
    }

    // ---- Notification gating ------------------------------------------------------------------

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Approved)]
    [InlineData(BookingStatus.Active)]
    public async Task SetHomePoint_Notifies_The_Renter_Of_An_InFlight_Booking(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        var listingId = await SeedOwnerWithBookingAsync(db, status);

        var emitter = new FakeNotificationEmitter();
        await using (var context = db.CreateContext())
        {
            await CreateService(context, emitter)
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        var call = Assert.Single(emitter.PickupAreaChangedCalls);
        Assert.Equal(listingId, call.Booking.ListingId);
        Assert.Equal(OwnerId, call.Owner.Id);
        Assert.NotNull(call.NewDistrict);
        Assert.Equal(KentronDistrictId, call.NewDistrict!.Id);
    }

    // What the emitter is HANDED, not what it does with it. NotificationEmitter reads
    // booking.Listing.Images to fill the notification's ToyImageUrl thumbnail, lazy loading is off,
    // and an un-included collection is silently empty rather than an error — so a missing
    // .ThenInclude in the query behind this fan-out produced a notification with no toy image and
    // no failure anywhere. That is exactly what HomePointStore did: it included Renter and Listing
    // but not Listing.Images, making the pickup notification the only booking notification in the
    // product rendering without a thumbnail. Asserted here because this is the only test that runs
    // the real store, and it is the store that was wrong.
    [Fact]
    public async Task SetHomePoint_Hands_The_Emitter_A_Booking_Whose_Listing_Carries_Its_Images()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerWithBookingAsync(db, BookingStatus.Approved);

        var emitter = new FakeNotificationEmitter();
        await using (var context = db.CreateContext())
        {
            await CreateService(context, emitter)
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        var call = Assert.Single(emitter.PickupAreaChangedCalls);
        Assert.NotEmpty(call.Booking.Listing.Images);
        Assert.All(call.Booking.Listing.Images, image => Assert.False(string.IsNullOrWhiteSpace(image.Url)));
    }

    // One bad booking costs one notification, not everybody's.
    //
    // The fan-out is a loop, and it used to have no per-iteration guard — only one try/catch around
    // the whole thing, with a comment asserting the emitter logged its own failures. It did not: the
    // emitter resolved the language, rendered the copy and read booking.Listing.Title BEFORE
    // reaching its own try/catch, so a single booking whose Listing was unloaded or deleted threw
    // out of the loop and every renter further down the list silently got nothing — no notification
    // and no log line anywhere to say so. "Further down the list" is unbounded: a busy owner can
    // have dozens of bookings in flight.
    //
    // Parameterised over WHICH booking fails on purpose. With the bug, the run only survives when
    // the failing booking happens to be the last one the query returns, so a single-case test could
    // pass by luck on an ordering nobody pinned.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SetHomePoint_Still_Notifies_The_Other_Renters_When_One_Bookings_Notification_Throws(int failingIndex)
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);
        await db.SeedAsync(TestData.User(RenterId, "renter@test.local"));

        var bookingIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var listingId = Guid.NewGuid();
            await db.SeedAsync(TestData.Listing(listingId, OwnerId, CategoryId, ListingStatus.Approved));

            var bookingId = Guid.NewGuid();
            await db.SeedAsync(TestData.Booking(
                bookingId, listingId, RenterId,
                TestData.Today.AddDays(3), TestData.Today.AddDays(5),
                BookingStatus.Approved));

            bookingIds.Add(bookingId);
        }

        var emitter = new FakeNotificationEmitter
        {
            ThrowOnPickupAreaChangedForBookingId = bookingIds[failingIndex]
        };

        await using (var context = db.CreateContext())
        {
            var result = await CreateService(context, emitter)
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);

            Assert.True(result.IsSuccess);
        }

        // Every booking was reached, including the ones after the failure.
        Assert.Equal(
            bookingIds.OrderBy(id => id),
            emitter.PickupAreaChangedCalls.Select(call => call.Booking.Id).OrderBy(id => id));
    }

    // A finished or dead booking has no pickup left to change.
    [Theory]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public async Task SetHomePoint_Does_Not_Notify_For_A_Booking_That_Is_Not_In_Flight(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerWithBookingAsync(db, status);

        var emitter = new FakeNotificationEmitter();
        await using (var context = db.CreateContext())
        {
            await CreateService(context, emitter)
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        Assert.Empty(emitter.PickupAreaChangedCalls);
    }

    // Moving between districts is the notifiable case, and the renter is told the NEW district by
    // name — that is the whole content of the message.
    [Fact]
    public async Task SetHomePoint_Notifies_With_The_New_District_When_Moving_Across_Yerevan()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerWithBookingAsync(db, BookingStatus.Approved);

        var emitter = new FakeNotificationEmitter();
        await using (var context = db.CreateContext())
        {
            await CreateService(context, emitter)
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }
        emitter.PickupAreaChangedCalls.Clear();

        await using (var context = db.CreateContext())
        {
            await CreateService(context, emitter)
                .SetHomePointAsync(OwnerId, TestData.ArabkirPoint.Latitude, TestData.ArabkirPoint.Longitude);
        }

        var call = Assert.Single(emitter.PickupAreaChangedCalls);
        Assert.NotNull(call.NewDistrict);
        Assert.Equal("arabkir", call.NewDistrict!.Code);
    }

    // A nudge within the same geohash cell and the same district publishes an identical pickup area,
    // so there is nothing to tell the renter.
    [Fact]
    public async Task SetHomePoint_Does_Not_Notify_When_The_Published_Area_Did_Not_Change()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerWithBookingAsync(db, BookingStatus.Approved);

        var (latitude, longitude) = TestData.KentronPoint;
        var emitter = new FakeNotificationEmitter();

        await using (var context = db.CreateContext())
        {
            await CreateService(context, emitter).SetHomePointAsync(OwnerId, latitude, longitude);
        }

        Assert.Single(emitter.PickupAreaChangedCalls);
        emitter.PickupAreaChangedCalls.Clear();

        // A metre or so away: a different exact point (so not the unchanged-point no-op), but the
        // same geohash cell centroid and the same district.
        var snapper = new GeohashSnapper();
        var nudged = (Latitude: latitude + 0.000001m, Longitude: longitude + 0.000001m);
        Assert.Equal(snapper.SnapToCellCenter(latitude, longitude), snapper.SnapToCellCenter(nudged.Latitude, nudged.Longitude));

        await using (var context = db.CreateContext())
        {
            var result = await CreateService(context, emitter).SetHomePointAsync(OwnerId, nudged.Latitude, nudged.Longitude);
            Assert.True(result.Value); // the exact point DID change and was written
        }

        Assert.Empty(emitter.PickupAreaChangedCalls);
    }

    // The move is the operation; the notification is a courtesy. A failing emitter must not undo a
    // relocation that already committed — the owner would be told their move failed while their
    // listings had in fact moved.
    [Fact]
    public async Task SetHomePoint_Survives_A_Failing_Notification_Emitter()
    {
        using var db = new SqliteTestDatabase();
        var listingId = await SeedOwnerWithBookingAsync(db, BookingStatus.Approved);

        var emitter = new FakeNotificationEmitter { ThrowOnPickupAreaChanged = true };
        var (latitude, longitude) = TestData.KentronPoint;

        await using (var context = db.CreateContext())
        {
            var result = await CreateService(context, emitter).SetHomePointAsync(OwnerId, latitude, longitude);

            Assert.True(result.IsSuccess);
            Assert.True(result.Value);
        }

        await using var verify = db.CreateContext();
        Assert.Equal(latitude, (await verify.Users.SingleAsync(u => u.Id == OwnerId)).HomeLatitude);
        Assert.Equal(latitude, (await verify.Listings.SingleAsync(l => l.Id == listingId)).Latitude);
    }

    // ---- Clearing ------------------------------------------------------------------------------

    [Fact]
    public async Task ClearHomePoint_Blanks_All_Six_Fields_When_The_User_Owns_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);

        await using (var context = db.CreateContext())
        {
            await CreateService(context, new FakeNotificationEmitter())
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        await using (var context = db.CreateContext())
        {
            var result = await CreateService(context, new FakeNotificationEmitter()).ClearHomePointAsync(OwnerId);
            Assert.True(result.IsSuccess);
            Assert.True(result.Value);
        }

        await using var verify = db.CreateContext();
        var owner = await verify.Users.SingleAsync(u => u.Id == OwnerId);
        Assert.Null(owner.HomeLatitude);
        Assert.Null(owner.HomeLongitude);
        Assert.Null(owner.HomePublicLatitude);
        Assert.Null(owner.HomePublicLongitude);
        Assert.Null(owner.HomeDistrictId);
        Assert.Null(owner.HomePointUpdatedAt);
    }

    // Clearing while listings exist would leave them unplaceable on the map, so it is refused for
    // anyone who has ever published — in any status, archived included.
    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Approved)]
    [InlineData(ListingStatus.Archived)]
    public async Task ClearHomePoint_Is_Refused_While_The_User_Owns_A_Listing(ListingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedOwnerAsync(db);
        await db.SeedAsync(TestData.Listing(Guid.NewGuid(), OwnerId, CategoryId, status));

        await using (var context = db.CreateContext())
        {
            await CreateService(context, new FakeNotificationEmitter())
                .SetHomePointAsync(OwnerId, TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude);
        }

        await using (var context = db.CreateContext())
        {
            var result = await CreateService(context, new FakeNotificationEmitter()).ClearHomePointAsync(OwnerId);

            Assert.False(result.IsSuccess);
            Assert.Equal("auth.home_point_in_use", result.Error!.Code);
        }

        await using var verify = db.CreateContext();
        Assert.NotNull((await verify.Users.SingleAsync(u => u.Id == OwnerId)).HomeLatitude);
    }

    private static async Task<Guid> SeedOwnerWithBookingAsync(SqliteTestDatabase db, BookingStatus status)
    {
        await SeedOwnerAsync(db);
        await db.SeedAsync(TestData.User(RenterId, "renter@test.local"));

        var listingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(listingId, OwnerId, CategoryId, ListingStatus.Approved));
        // The toy has a photo, so a notification rendered from this booking has a thumbnail to
        // find. See SetHomePoint_Hands_The_Emitter_A_Booking_Whose_Listing_Carries_Its_Images.
        await db.SeedAsync(TestData.Image(Guid.NewGuid(), listingId, isPrimary: true, sortOrder: 0));

        await db.SeedAsync(TestData.Booking(
            Guid.NewGuid(), listingId, RenterId,
            TestData.Today.AddDays(3), TestData.Today.AddDays(5),
            status,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        return listingId;
    }
}
